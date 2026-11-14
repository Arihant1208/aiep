using Aiep.Application.Identity;
using Aiep.Application.Tenancy;
using Aiep.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Aiep.Infrastructure.Identity;

/// <summary>
/// Resolves <c>(iss, sub)</c> to a subject through <c>aiep.resolve_subject</c> (ADR-0017), with a short cache so a
/// signed-in user doesn't cause a database write path on every request. The cache key includes display data, so a
/// changed name or email misses the cache and is written through.
/// </summary>
internal sealed class SubjectDirectory(TenantDatabase database) : ISubjectDirectory, IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 10_000 });

    public async Task<Guid> ResolveAsync(ExternalIdentity identity, CancellationToken ct)
    {
        if (_cache.TryGetValue(identity, out Guid cached))
        {
            return cached;
        }

        var id = await database.ExecuteAsync(TenantContext.None, async (session, token) =>
        {
            await using var command = new NpgsqlCommand(
                "SELECT aiep.resolve_subject($1, $2, $3, $4, $5)", session.Connection, session.Transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            command.Parameters.Add(new NpgsqlParameter { Value = identity.Issuer });
            command.Parameters.Add(new NpgsqlParameter { Value = identity.Subject });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)identity.DisplayName ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)identity.Email ?? DBNull.Value });
            return (Guid)(await command.ExecuteScalarAsync(token))!;
        }, ct);

        _cache.Set(identity, id, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = 1 });
        return id;
    }

    public void Dispose() => _cache.Dispose();
}
