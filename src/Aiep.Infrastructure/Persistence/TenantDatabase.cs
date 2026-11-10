using Aiep.Application.Tenancy;
using Npgsql;

namespace Aiep.Infrastructure.Persistence;

/// <summary>
/// The tenant-context unit of work: the only way services access tenant data (ADR-0006, ADR-0013).
/// <para>
/// Each call takes a pooled connection, begins a transaction, sets <c>app.organization_id</c> and
/// <c>app.subject_id</c> with <c>set_config(..., true)</c> (transaction-local, so nothing survives on the pooled
/// connection), runs <paramref name="work"/>, saves EF changes and commits. Any exception rolls everything back.
/// </para>
/// </summary>
public sealed class TenantDatabase
{
    private readonly AiepDataSource _dataSource;

    internal TenantDatabase(AiepDataSource dataSource) => _dataSource = dataSource;

    public async Task<T> ExecuteAsync<T>(
        TenantContext context, Func<TenantSession, CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(connection, transaction, context, ct);

        await using var db = AiepDbContext.Create(connection, transaction);
        var result = await work(new TenantSession(context, db, connection, transaction), ct);

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return result;
    }

    public Task ExecuteAsync(
        TenantContext context, Func<TenantSession, CancellationToken, Task> work, CancellationToken ct = default) =>
        ExecuteAsync(context, async (session, token) =>
        {
            await work(session, token);
            return true;
        }, ct);

    internal static async Task SetContextAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, TenantContext context, CancellationToken ct)
    {
        // Empty string, not NULL, for "no value": policies use nullif(..., '') so both match nothing.
        await using var command = new NpgsqlCommand(
            "SELECT set_config('app.organization_id', $1, true), set_config('app.subject_id', $2, true)",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = context.OrganizationId?.ToString() ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter { Value = context.SubjectId?.ToString() ?? string.Empty });
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>One tenant-scoped transaction. Valid only inside the <see cref="TenantDatabase"/> callback.</summary>
public sealed class TenantSession
{
    internal TenantSession(
        TenantContext context, AiepDbContext db, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Context = context;
        Db = db;
        Connection = connection;
        Transaction = transaction;
    }

    public TenantContext Context { get; }

    public AiepDbContext Db { get; }

    /// <summary>For raw Npgsql paths inside Infrastructure (hot paths, SECURITY DEFINER functions).</summary>
    internal NpgsqlConnection Connection { get; }

    internal NpgsqlTransaction Transaction { get; }
}
