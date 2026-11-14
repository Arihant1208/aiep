using Aiep.Application.Identity;
using Aiep.Application.Tenancy;
using Aiep.Infrastructure.Persistence;
using Npgsql;

namespace Aiep.Infrastructure.Identity;

/// <summary>
/// API key persistence for authentication. No positive cache yet: every request reads the key, so revocation and
/// membership removal take effect on the next request. ADR-0008 allows a ≤ 30 s cache if this becomes a hot spot.
/// </summary>
internal sealed class ApiKeyStore(TenantDatabase database) : IApiKeyStore
{
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromSeconds(60);

    public Task<StoredApiKey?> FindByKeyIdAsync(string keyId, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.None, async (session, token) =>
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT id, organization_id, workspace_id, owner_subject_id, secret_hash, environment, permissions,
                       expires_at, revoked_at
                FROM aiep.find_api_key($1)
                """,
                session.Connection, session.Transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = keyId });
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return null;
            }

            return new StoredApiKey(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetFieldValue<byte[]>(4),
                reader.GetString(5),
                reader.GetFieldValue<string[]>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
        }, ct);

    public Task<bool> ConfirmOwnerAndRecordUseAsync(StoredApiKey key, DateTimeOffset now, CancellationToken ct) =>
        database.ExecuteAsync(TenantContext.ForOrganization(key.OrganizationId, key.OwnerSubjectId), async (session, token) =>
        {
            await using var command = new NpgsqlCommand(
                """
                WITH owner AS (
                    SELECT EXISTS (
                        SELECT 1 FROM aiep.memberships
                        WHERE subject_id = $2 AND status = 'active') AS active
                ), touched AS (
                    UPDATE aiep.api_keys SET last_used_at = $3
                    WHERE id = $1 AND (SELECT active FROM owner)
                      AND (last_used_at IS NULL OR last_used_at < $3 - $4::interval)
                )
                SELECT active FROM owner
                """,
                session.Connection, session.Transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = key.Id });
            command.Parameters.Add(new NpgsqlParameter { Value = key.OwnerSubjectId });
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            command.Parameters.Add(new NpgsqlParameter { Value = LastUsedResolution });
            return (bool)(await command.ExecuteScalarAsync(token))!;
        }, ct);
}
