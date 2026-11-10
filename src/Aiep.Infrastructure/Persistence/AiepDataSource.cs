using Npgsql;

namespace Aiep.Infrastructure.Persistence;

/// <summary>
/// The service's connection pool. Internal on purpose: hosts can't resolve it, so the only way to reach tenant
/// data is <see cref="TenantDatabase"/>. Connections from here carry no tenant context until a unit of work sets it.
/// </summary>
internal sealed class AiepDataSource(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct) => dataSource.OpenConnectionAsync(ct);

    public NpgsqlCommand CreateCommand(string sql) => dataSource.CreateCommand(sql);

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}
