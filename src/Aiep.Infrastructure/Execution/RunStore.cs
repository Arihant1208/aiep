using System.Security.Cryptography;
using System.Text;
using Aiep.Application.ControlPlane;
using Aiep.Application.Execution;
using Aiep.Application.Tenancy;
using Aiep.Domain.Runs;
using Aiep.Infrastructure.Persistence;
using Aiep.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Aiep.Infrastructure.Execution;

/// <summary>
/// Run submission and reads over <see cref="TenantDatabase"/>. Submission inserts the run and its idempotency mapping in
/// one transaction and issues the enqueue NOTIFY, so the same (workspace, caller, key) maps to exactly one run even under
/// concurrent submits (a loser's transaction rolls back and re-reads the winner). ADR-0005, ADR-0015.
/// </summary>
internal sealed class RunStore(TenantDatabase database, TimeProvider time) : IRunStore
{
    private const string EnqueueChannel = "aiep_runs";

    public async Task<SubmitResult> SubmitAsync(
        TenantContext context, SubmitRunRequest request, TimeSpan idempotencyRetention, CancellationToken cancellationToken = default)
    {
        try
        {
            return await database.ExecuteAsync(context,
                (session, token) => SubmitCoreAsync(session, context, request, idempotencyRetention, token), cancellationToken);
        }
        catch (IdempotencyRaceException)
        {
            var winner = await database.ExecuteAsync(context, async (session, token) =>
            {
                var existing = await FindByKeyAsync(session, request.WorkspaceId, request.CallerId, Hash(request.IdempotencyKey!), token);
                return existing is { } row ? await LoadAsync(session, row.RunId, token) : null;
            }, cancellationToken);
            return new SubmitResult(SubmitStatus.Existing, winner);
        }
    }

    private async Task<SubmitResult> SubmitCoreAsync(
        TenantSession session, TenantContext context, SubmitRunRequest request, TimeSpan retention, CancellationToken token)
    {
        if (request.IdempotencyKey is { } key)
        {
            var existing = await FindByKeyAsync(session, request.WorkspaceId, request.CallerId, Hash(key), token);
            if (existing is { } row)
            {
                return CryptographicOperations.FixedTimeEquals(row.RequestHash, Hash(request.InputJson))
                    ? new SubmitResult(SubmitStatus.Existing, await LoadAsync(session, row.RunId, token))
                    : new SubmitResult(SubmitStatus.Conflict, null);
            }
        }

        var runId = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO aiep.task_runs
                (id, organization_id, workspace_id, task_definition_version_id, status, attempt_count, max_attempts,
                 input, next_attempt_at, traceparent, created_by)
            VALUES ($1, $2, $3, $4, 'Queued', 0, $5, $6, now(), $7, $8)
            """, session.Connection, session.Transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = runId });
            insert.Parameters.Add(new NpgsqlParameter { Value = context.OrganizationId!.Value });
            insert.Parameters.Add(new NpgsqlParameter { Value = request.WorkspaceId });
            insert.Parameters.Add(new NpgsqlParameter { Value = request.TaskDefinitionVersionId });
            insert.Parameters.Add(new NpgsqlParameter { Value = request.MaxAttempts });
            insert.Parameters.Add(new NpgsqlParameter { Value = request.InputJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
            insert.Parameters.Add(new NpgsqlParameter { Value = (object?)request.Traceparent ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter { Value = (object?)request.CreatedBy ?? DBNull.Value });
            await insert.ExecuteNonQueryAsync(token);
        }

        if (request.IdempotencyKey is { } idemKey)
        {
            await using var record = new NpgsqlCommand(
                """
                INSERT INTO aiep.idempotency_keys
                    (id, organization_id, workspace_id, caller_id, key_hash, request_hash, run_id, expires_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                ON CONFLICT (organization_id, workspace_id, caller_id, key_hash) DO NOTHING
                """, session.Connection, session.Transaction);
            record.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            record.Parameters.Add(new NpgsqlParameter { Value = context.OrganizationId!.Value });
            record.Parameters.Add(new NpgsqlParameter { Value = request.WorkspaceId });
            record.Parameters.Add(new NpgsqlParameter { Value = request.CallerId });
            record.Parameters.Add(new NpgsqlParameter { Value = Hash(idemKey), NpgsqlDbType = NpgsqlDbType.Bytea });
            record.Parameters.Add(new NpgsqlParameter { Value = Hash(request.InputJson), NpgsqlDbType = NpgsqlDbType.Bytea });
            record.Parameters.Add(new NpgsqlParameter { Value = runId });
            record.Parameters.Add(new NpgsqlParameter { Value = time.GetUtcNow() + retention });

            if (await record.ExecuteNonQueryAsync(token) == 0)
            {
                // A concurrent submit with the same key already recorded: roll this one back and return the winner.
                throw new IdempotencyRaceException();
            }
        }

        await using (var notify = new NpgsqlCommand(
            $"SELECT pg_notify('{EnqueueChannel}', '')", session.Connection, session.Transaction))
        {
            await notify.ExecuteScalarAsync(token);
        }

        return new SubmitResult(SubmitStatus.Created, await LoadAsync(session, runId, token));
    }

    public Task<RunView?> GetRunAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, Guid runId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var run = await session.Db.TaskRuns.AsNoTracking()
                .SingleOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Id == runId, token);
            return run is null ? null : ToView(run);
        }, cancellationToken);

    public Task<IReadOnlyList<RunView>> ListRunsAsync(
        Guid organizationId, Guid subjectId, Guid workspaceId, int limit, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, subjectId), async (session, token) =>
        {
            var runs = await session.Db.TaskRuns.AsNoTracking()
                .Where(r => r.WorkspaceId == workspaceId)
                .OrderByDescending(r => r.Id)
                .Take(limit)
                .ToListAsync(token);
            return (IReadOnlyList<RunView>)[.. runs.Select(ToView)];
        }, cancellationToken);

    public Task<RunView?> CancelRunAsync(
        ControlPlaneActor actor, Guid organizationId, Guid workspaceId, Guid runId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(TenantContext.ForOrganization(organizationId, actor.SubjectId), async (session, token) =>
        {
            var run = await session.Db.TaskRuns.SingleOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Id == runId, token);
            if (run is null)
            {
                return (RunView?)null;
            }

            var status = Enum.Parse<TaskRunStatus>(run.Status);
            if (!TaskRunTransitions.IsTerminal(status) && run.CancelRequestedAt is null)
            {
                var now = time.GetUtcNow();
                run.CancelRequestedAt = now;
                run.UpdatedAt = now;

                // Pending runs can be cancelled now; a Running run is cancelled by the worker at its next heartbeat.
                if (status is TaskRunStatus.Queued or TaskRunStatus.RetryWait or TaskRunStatus.Accepted)
                {
                    run.Status = TaskRunStatus.Cancelled.ToString();
                    run.CompletedAt = now;
                    run.LeaseOwner = null;
                    run.LeaseExpiresAt = null;
                    run.NextAttemptAt = null;
                }

                session.Db.AuditEvents.Add(Audit(actor, organizationId, workspaceId, "run.cancel_requested", run.Id));
                await session.Db.SaveChangesAsync(token);
            }

            return ToView(run);
        }, cancellationToken);

    private static async Task<(Guid RunId, byte[] RequestHash)?> FindByKeyAsync(
        TenantSession session, Guid workspaceId, Guid callerId, byte[] keyHash, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            "SELECT run_id, request_hash FROM aiep.idempotency_keys WHERE workspace_id = $1 AND caller_id = $2 AND key_hash = $3",
            session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = workspaceId });
        command.Parameters.Add(new NpgsqlParameter { Value = callerId });
        command.Parameters.Add(new NpgsqlParameter { Value = keyHash, NpgsqlDbType = NpgsqlDbType.Bytea });

        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? (reader.GetGuid(0), reader.GetFieldValue<byte[]>(1))
            : null;
    }

    private static async Task<RunView> LoadAsync(TenantSession session, Guid runId, CancellationToken token) =>
        ToView(await session.Db.TaskRuns.AsNoTracking().SingleAsync(r => r.Id == runId, token));

    private static RunView ToView(TaskRun r) => new(
        r.Id, r.WorkspaceId, r.TaskDefinitionVersionId, r.Status, r.AttemptCount, r.MaxAttempts, r.Output,
        r.ErrorCategory, r.ErrorMessage, r.ProviderCallMayHaveCompleted, r.CancelRequestedAt, r.StartedAt,
        r.CompletedAt, r.CreatedAt);

    private static AuditEvent Audit(ControlPlaneActor actor, Guid organizationId, Guid workspaceId, string action, Guid targetId) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        WorkspaceId = workspaceId,
        ActorType = actor.ActorType,
        ActorSubjectId = actor.SubjectId,
        ActorApiKeyId = actor.ApiKeyId,
        Action = action,
        TargetType = "task_run",
        TargetId = targetId,
        Outcome = "success",
        CorrelationId = actor.CorrelationId,
    };

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private sealed class IdempotencyRaceException : Exception;
}
