using System.Security.Cryptography;
using System.Text;
using Aiep.Application.Execution;
using Aiep.Application.Tenancy;
using Aiep.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace Aiep.Infrastructure.Execution;

/// <summary>
/// Idempotency-key persistence over the tenant unit of work (ADR-0015). Keys and payloads are stored only as SHA-256
/// hashes; the scope is (organization via RLS, workspace, caller). Recording uses <c>ON CONFLICT DO NOTHING</c> so two
/// concurrent submissions of the same key race safely -- the loser re-reads the winner's run.
/// </summary>
internal sealed class IdempotencyStore(TenantDatabase database, TimeProvider time) : IIdempotencyStore
{
    public Task<IdempotencyReservation> LookupAsync(
        TenantContext context,
        Guid workspaceId,
        Guid callerId,
        string idempotencyKey,
        string payload,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(context, async (session, token) =>
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT run_id, request_hash FROM aiep.idempotency_keys
                WHERE workspace_id = $1 AND caller_id = $2 AND key_hash = $3
                """, session.Connection, session.Transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = workspaceId });
            command.Parameters.Add(new NpgsqlParameter { Value = callerId });
            command.Parameters.Add(new NpgsqlParameter { Value = Hash(idempotencyKey), NpgsqlDbType = NpgsqlDbType.Bytea });

            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return IdempotencyReservation.Fresh;
            }

            var runId = reader.GetGuid(0);
            var storedPayloadHash = reader.GetFieldValue<byte[]>(1);
            var matches = CryptographicOperations.FixedTimeEquals(storedPayloadHash, Hash(payload));
            return new IdempotencyReservation(runId, matches);
        }, cancellationToken);

    public Task<bool> TryRecordAsync(
        TenantContext context,
        Guid workspaceId,
        Guid callerId,
        string idempotencyKey,
        string payload,
        Guid runId,
        TimeSpan retention,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(context, async (session, token) =>
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO aiep.idempotency_keys
                    (id, organization_id, workspace_id, caller_id, key_hash, request_hash, run_id, expires_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                ON CONFLICT (organization_id, workspace_id, caller_id, key_hash) DO NOTHING
                """, session.Connection, session.Transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            command.Parameters.Add(new NpgsqlParameter { Value = context.OrganizationId!.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = workspaceId });
            command.Parameters.Add(new NpgsqlParameter { Value = callerId });
            command.Parameters.Add(new NpgsqlParameter { Value = Hash(idempotencyKey), NpgsqlDbType = NpgsqlDbType.Bytea });
            command.Parameters.Add(new NpgsqlParameter { Value = Hash(payload), NpgsqlDbType = NpgsqlDbType.Bytea });
            command.Parameters.Add(new NpgsqlParameter { Value = runId });
            command.Parameters.Add(new NpgsqlParameter { Value = time.GetUtcNow() + retention });

            var inserted = await command.ExecuteNonQueryAsync(token);
            return inserted == 1;
        }, cancellationToken);

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
