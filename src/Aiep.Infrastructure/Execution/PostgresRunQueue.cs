using Aiep.Application.Execution;
using Aiep.Application.Tenancy;
using Aiep.Domain.Runs;
using Aiep.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace Aiep.Infrastructure.Execution;

/// <summary>
/// The PostgreSQL-backed work queue (ADR-0005). Enqueue, heartbeat and settle run under the tenant unit of work with
/// the run's organization context; claim and reconcile call the <c>SECURITY DEFINER</c> functions in V0003 because the
/// worker has no tenant context when it looks across tenants for work. All state changes are conditional updates, so a
/// late or duplicate attempt that lost its lease cannot overwrite the result.
/// </summary>
internal sealed class PostgresRunQueue(TenantDatabase database, AiepDataSource dataSource)
    : IRunQueue
{
    private const string EnqueueChannel = "aiep_runs";

    public async Task<Guid> EnqueueAsync(
        TenantContext context, EnqueueRunRequest request, CancellationToken cancellationToken = default)
    {
        var runId = Guid.CreateVersion7();

        await database.ExecuteAsync(context, async (session, token) =>
        {
            // organization_id comes from the tenant context (authentication/resource), never the request (CLAUDE.md).
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO aiep.task_runs
                    (id, organization_id, workspace_id, task_definition_version_id, status, attempt_count, max_attempts,
                     input, next_attempt_at, traceparent, replayed_from_run_id, created_by)
                VALUES ($1, $2, $3, $4, 'Queued', 0, $5, $6, now(), $7, $8, $9)
                """, session.Connection, session.Transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = runId });
            insert.Parameters.Add(new NpgsqlParameter { Value = context.OrganizationId!.Value });
            insert.Parameters.Add(new NpgsqlParameter { Value = request.WorkspaceId });
            insert.Parameters.Add(new NpgsqlParameter { Value = request.TaskDefinitionVersionId });
            insert.Parameters.Add(new NpgsqlParameter { Value = request.MaxAttempts });
            insert.Parameters.Add(new NpgsqlParameter { Value = request.InputJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
            insert.Parameters.Add(new NpgsqlParameter { Value = (object?)request.Traceparent ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter { Value = (object?)request.ReplayedFromRunId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter { Value = (object?)request.CreatedBy ?? DBNull.Value });
            await insert.ExecuteNonQueryAsync(token);

            // Wake any idle worker (ADR-0005). No payload: the signal carries nothing tenant-specific.
            await using var notify = new NpgsqlCommand(
                $"SELECT pg_notify('{EnqueueChannel}', '')", session.Connection, session.Transaction);
            await notify.ExecuteScalarAsync(token);
        }, cancellationToken);

        return runId;
    }

    public Task<ClaimedRun?> ClaimAsync(string workerId, CancellationToken cancellationToken = default)
    {
        var attemptId = Guid.CreateVersion7();

        return database.ExecuteAsync(TenantContext.None, async (session, token) =>
        {
            await using var command = new NpgsqlCommand(
                "SELECT * FROM aiep.claim_next_run($1, $2)", session.Connection, session.Transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = workerId });
            command.Parameters.Add(new NpgsqlParameter { Value = attemptId });

            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return null;
            }

            string? Nullable(string column) =>
                reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetFieldValue<string>(reader.GetOrdinal(column));

            T Get<T>(string column) => reader.GetFieldValue<T>(reader.GetOrdinal(column));

            return new ClaimedRun(
                RunId: Get<Guid>("run_id"),
                OrganizationId: Get<Guid>("organization_id"),
                WorkspaceId: Get<Guid>("workspace_id"),
                AttemptId: Get<Guid>("attempt_id"),
                AttemptNumber: Get<int>("attempt_number"),
                MaxAttempts: Get<int>("max_attempts"),
                WorkerId: workerId,
                InputJson: Get<string>("input"),
                HandlerType: Get<string>("handler_type"),
                HandlerVersion: Get<string>("handler_version"),
                ModelProvider: Get<string>("model_provider"),
                ModelName: Get<string>("model_name"),
                ModelConfig: Get<string>("model_config"),
                PromptTemplate: Nullable("prompt_template"),
                OutputSchema: Get<string>("output_schema"),
                TimeoutSeconds: Get<int>("timeout_seconds"),
                LeaseSeconds: Get<int>("lease_seconds"),
                LeaseExpiresAt: Get<DateTimeOffset>("lease_expires_at"),
                CancelRequested: Get<bool>("cancel_requested"));
        }, cancellationToken);
    }

    public Task<HeartbeatResult> HeartbeatAsync(ClaimedRun run, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(run.OrganizationId, null), async (session, token) =>
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE aiep.task_runs
                SET lease_expires_at = now() + make_interval(secs => $3), updated_at = now()
                WHERE id = $1 AND status = 'Running' AND lease_owner = $2
                RETURNING (cancel_requested_at IS NOT NULL)
                """, session.Connection, session.Transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = run.RunId });
            command.Parameters.Add(new NpgsqlParameter { Value = run.WorkerId });
            command.Parameters.Add(new NpgsqlParameter { Value = run.LeaseSeconds });

            await using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token)
                ? new HeartbeatResult(true, reader.GetBoolean(0))
                : new HeartbeatResult(false, false);
        }, cancellationToken);

    public Task SettleAsync(RunSettlement settlement, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(settlement.OrganizationId, null), async (session, token) =>
        {
            var terminal = TaskRunTransitions.IsTerminal(settlement.TargetStatus);

            await using var runUpdate = new NpgsqlCommand(
                """
                UPDATE aiep.task_runs
                SET status = $2,
                    output = COALESCE($3, output),
                    error_category = $4,
                    error_message = $5,
                    next_attempt_at = $6,
                    lease_owner = NULL,
                    lease_expires_at = NULL,
                    provider_call_may_have_completed = $7,
                    completed_at = CASE WHEN $8 THEN now() ELSE completed_at END,
                    updated_at = now()
                WHERE id = $1 AND status = 'Running' AND lease_owner = $9
                """, session.Connection, session.Transaction);
            runUpdate.Parameters.Add(new NpgsqlParameter { Value = settlement.RunId });
            runUpdate.Parameters.Add(new NpgsqlParameter { Value = settlement.TargetStatus.ToString() });
            runUpdate.Parameters.Add(new NpgsqlParameter
            {
                Value = (object?)settlement.OutputJson ?? DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Jsonb,
            });
            runUpdate.Parameters.Add(new NpgsqlParameter { Value = (object?)settlement.ErrorCategory?.ToString() ?? DBNull.Value });
            runUpdate.Parameters.Add(new NpgsqlParameter { Value = (object?)settlement.ErrorMessage ?? DBNull.Value });
            runUpdate.Parameters.Add(new NpgsqlParameter { Value = (object?)settlement.NextAttemptAt ?? DBNull.Value });
            runUpdate.Parameters.Add(new NpgsqlParameter { Value = settlement.ProviderCallMayHaveCompleted });
            runUpdate.Parameters.Add(new NpgsqlParameter { Value = terminal });
            runUpdate.Parameters.Add(new NpgsqlParameter { Value = settlement.WorkerId });

            var affected = await runUpdate.ExecuteNonQueryAsync(token);
            if (affected == 0)
            {
                // Lost the lease or already settled: this attempt lost the race, so discard it.
                return;
            }

            await using var attemptUpdate = new NpgsqlCommand(
                """
                UPDATE aiep.task_run_attempts
                SET status = $2, ended_at = now(), error_category = $3, error_message = $4,
                    provider_latency_ms = $5, input_tokens = $6, output_tokens = $7
                WHERE id = $1 AND status = 'running'
                """, session.Connection, session.Transaction);
            attemptUpdate.Parameters.Add(new NpgsqlParameter { Value = settlement.AttemptId });
            attemptUpdate.Parameters.Add(new NpgsqlParameter { Value = settlement.AttemptStatus });
            attemptUpdate.Parameters.Add(new NpgsqlParameter { Value = (object?)settlement.ErrorCategory?.ToString() ?? DBNull.Value });
            attemptUpdate.Parameters.Add(new NpgsqlParameter { Value = (object?)settlement.ErrorMessage ?? DBNull.Value });
            attemptUpdate.Parameters.Add(new NpgsqlParameter
            {
                Value = settlement.Usage is { } latency
                    ? (int)latency.ProviderLatency.TotalMilliseconds
                    : (object)DBNull.Value,
            });
            attemptUpdate.Parameters.Add(new NpgsqlParameter { Value = (object?)settlement.Usage?.InputTokens ?? DBNull.Value });
            attemptUpdate.Parameters.Add(new NpgsqlParameter { Value = (object?)settlement.Usage?.OutputTokens ?? DBNull.Value });
            await attemptUpdate.ExecuteNonQueryAsync(token);
        }, cancellationToken);

    public Task<int> ReconcileExpiredLeasesAsync(int maxRuns = 100, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.None, async (session, token) =>
        {
            await using var command = new NpgsqlCommand(
                "SELECT aiep.reconcile_expired_leases($1)", session.Connection, session.Transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = maxRuns });
            return (int)(await command.ExecuteScalarAsync(token))!;
        }, cancellationToken);

    public async Task<bool> WaitForEnqueueSignalAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var listen = new NpgsqlCommand($"LISTEN {EnqueueChannel}", connection))
        {
            await listen.ExecuteNonQueryAsync(cancellationToken);
        }

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timed.CancelAfter(timeout);
        try
        {
            await connection.WaitAsync(timed.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
