using Aiep.Infrastructure.Persistence;
using Npgsql;

namespace Aiep.Infrastructure.Execution;

/// <summary>
/// The reserve-then-settle quota ledger (threat T12), operating inside an existing tenant transaction so a reservation is
/// atomic with the run it belongs to. A run reserves its estimated cost at submission; the worker settles the actual cost
/// when it finishes. Committed spend over the budget window is <c>sum(coalesce(actual, estimated))</c>.
/// </summary>
internal static class QuotaLedger
{
    /// <summary>Reserves the estimate against the organization-wide budget, inserting a reservation. Returns false (and
    /// inserts nothing) if it would exceed the budget. No budget row means no limit.</summary>
    public static async Task<bool> TryReserveAsync(
        TenantSession session, Guid organizationId, Guid workspaceId, Guid runId, decimal estimatedUsd, CancellationToken ct)
    {
        int windowSeconds;
        decimal limitUsd;
        await using (var budget = new NpgsqlCommand(
            "SELECT window_seconds, limit_usd FROM aiep.quota_budgets WHERE organization_id = $1 AND workspace_id IS NULL",
            session.Connection, session.Transaction))
        {
            budget.Parameters.Add(new NpgsqlParameter { Value = organizationId });
            await using var reader = await budget.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                // No budget configured: unlimited, but still record the reservation for accounting.
                await reader.DisposeAsync();
                await InsertReservationAsync(session, organizationId, workspaceId, runId, estimatedUsd, ct);
                return true;
            }

            windowSeconds = reader.GetInt32(0);
            limitUsd = reader.GetDecimal(1);
        }

        decimal committed;
        await using (var sum = new NpgsqlCommand(
            """
            SELECT coalesce(sum(coalesce(actual_usd, estimated_usd)), 0)
            FROM aiep.quota_reservations
            WHERE organization_id = $1 AND created_at >= now() - make_interval(secs => $2)
            """, session.Connection, session.Transaction))
        {
            sum.Parameters.Add(new NpgsqlParameter { Value = organizationId });
            sum.Parameters.Add(new NpgsqlParameter { Value = windowSeconds });
            committed = (decimal)(await sum.ExecuteScalarAsync(ct))!;
        }

        if (committed + estimatedUsd > limitUsd)
        {
            return false;
        }

        await InsertReservationAsync(session, organizationId, workspaceId, runId, estimatedUsd, ct);
        return true;
    }

    /// <summary>Settles the actual cost for a run's reservation (no-op if there is none, e.g. a pre-quota run).</summary>
    public static async Task SettleAsync(
        TenantSession session, Guid organizationId, Guid runId, decimal actualUsd, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE aiep.quota_reservations
            SET actual_usd = $3, status = 'settled', settled_at = now()
            WHERE organization_id = $1 AND run_id = $2 AND status = 'reserved'
            """, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = organizationId });
        command.Parameters.Add(new NpgsqlParameter { Value = runId });
        command.Parameters.Add(new NpgsqlParameter { Value = actualUsd });
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertReservationAsync(
        TenantSession session, Guid organizationId, Guid workspaceId, Guid runId, decimal estimatedUsd, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO aiep.quota_reservations (id, organization_id, workspace_id, run_id, estimated_usd, status)
            VALUES ($1, $2, $3, $4, $5, 'reserved')
            """, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        command.Parameters.Add(new NpgsqlParameter { Value = organizationId });
        command.Parameters.Add(new NpgsqlParameter { Value = workspaceId });
        command.Parameters.Add(new NpgsqlParameter { Value = runId });
        command.Parameters.Add(new NpgsqlParameter { Value = estimatedUsd });
        await command.ExecuteNonQueryAsync(ct);
    }
}
