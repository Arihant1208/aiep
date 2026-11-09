using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aiep.Migrator;

public sealed record MigrationResult(IReadOnlyList<MigrationScript> Applied, int AlreadyApplied);

/// <summary>
/// Applies versioned SQL migrations (ADR-0013). Guarantees:
/// <list type="bullet">
/// <item>A session advisory lock serializes concurrent runners; the second one waits, then finds nothing to do.</item>
/// <item>Each script and its <c>schema_migrations</c> row commit in one transaction, or neither does.</item>
/// <item>An applied script that changed (checksum or name), went missing, or a new script older than the newest
/// applied one, stops the run before anything is applied.</item>
/// </list>
/// </summary>
public sealed partial class MigrationRunner(string connectionString, ILogger<MigrationRunner> logger)
{
    // "AIEPMIGR" in ASCII. Any constant works as long as nothing else in the database uses it.
    internal const long AdvisoryLockKey = 0x414945504D494752L;

    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public async Task<MigrationResult> RunAsync(IReadOnlyList<MigrationScript> scripts, CancellationToken ct = default)
    {
        // No pooling: closing the connection must end the session, which also releases the advisory lock if the
        // explicit unlock below never runs.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);

        LogConnected(builder.Host, builder.Database, builder.Username);

        await AcquireLockAsync(connection, ct);
        try
        {
            await EnsureHistoryTableAsync(connection, ct);
            var applied = await ReadAppliedAsync(connection, ct);
            var pending = Plan(scripts, applied);

            foreach (var script in pending)
            {
                await ApplyAsync(connection, script, ct);
            }

            LogDone(pending.Count, applied.Count);
            return new MigrationResult(pending, applied.Count);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            unlock.Parameters.AddWithValue("key", AdvisoryLockKey);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>Validates history against the scripts on disk and returns the scripts still to apply.</summary>
    internal static List<MigrationScript> Plan(
        IReadOnlyList<MigrationScript> scripts, IReadOnlyDictionary<int, AppliedMigration> applied)
    {
        var byVersion = scripts.ToDictionary(s => s.Version);
        foreach (var (version, row) in applied.OrderBy(a => a.Key))
        {
            if (!byVersion.TryGetValue(version, out var script))
            {
                throw new MigrationException(
                    $"Applied migration V{version:D4}__{row.Name} is missing from the migrations directory.");
            }

            if (script.Name != row.Name || script.ChecksumSha256 != row.ChecksumSha256)
            {
                throw new MigrationException(
                    $"Applied migration {script.DisplayName} has changed since it was applied " +
                    $"(recorded {row.Name} / {row.ChecksumSha256}, found {script.Name} / {script.ChecksumSha256}). " +
                    "Applied migrations are immutable: revert the edit and add a new migration instead.");
            }
        }

        var newest = applied.Count == 0 ? 0 : applied.Keys.Max();
        var pending = scripts.Where(s => !applied.ContainsKey(s.Version)).ToList();
        var outOfOrder = pending.Where(s => s.Version < newest).ToList();
        if (outOfOrder.Count > 0)
        {
            throw new MigrationException(
                $"Migrations {string.Join(", ", outOfOrder.Select(s => s.DisplayName))} are older than the newest " +
                $"applied version V{newest:D4}. Renumber them after V{newest:D4}.");
        }

        return pending;
    }

    private async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection)
        {
            CommandTimeout = (int)LockTimeout.TotalSeconds,
        };
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        var stopwatch = Stopwatch.StartNew();
        await command.ExecuteNonQueryAsync(ct);
        if (stopwatch.Elapsed > TimeSpan.FromSeconds(1))
        {
            LogWaitedForLock(stopwatch.ElapsedMilliseconds);
        }
    }

    private static async Task EnsureHistoryTableAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        // Created by the runner (not by V0001) because it must exist before any migration runs.
        // The schema's default privileges grant DML on new tables to the service roles, so strip every grant:
        // only the owner (aiep_migrator) may read or change migration history.
        const string sql = """
            CREATE TABLE IF NOT EXISTS aiep.schema_migrations (
                version          integer     PRIMARY KEY CHECK (version > 0),
                name             text        NOT NULL,
                checksum_sha256  text        NOT NULL CHECK (checksum_sha256 ~ '^[0-9a-f]{64}$'),
                applied_at       timestamptz NOT NULL DEFAULT now()
            );
            DO $$
            DECLARE r record;
            BEGIN
                FOR r IN
                    SELECT DISTINCT grantee FROM information_schema.role_table_grants
                    WHERE table_schema = 'aiep' AND table_name = 'schema_migrations' AND grantee <> current_user
                LOOP
                    EXECUTE format('REVOKE ALL ON aiep.schema_migrations FROM %I', r.grantee);
                END LOOP;
            END
            $$;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Dictionary<int, AppliedMigration>> ReadAppliedAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT version, name, checksum_sha256 FROM aiep.schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var applied = new Dictionary<int, AppliedMigration>();
        while (await reader.ReadAsync(ct))
        {
            applied[reader.GetInt32(0)] = new AppliedMigration(reader.GetString(1), reader.GetString(2));
        }

        return applied;
    }

    private async Task ApplyAsync(NpgsqlConnection connection, MigrationScript script, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var command = new NpgsqlCommand(script.Sql, connection, transaction) { CommandTimeout = 0 })
            {
                await command.ExecuteNonQueryAsync(ct);
            }

            await using (var record = new NpgsqlCommand(
                "INSERT INTO aiep.schema_migrations (version, name, checksum_sha256) VALUES (@v, @n, @c)",
                connection, transaction))
            {
                record.Parameters.AddWithValue("v", script.Version);
                record.Parameters.AddWithValue("n", script.Name);
                record.Parameters.AddWithValue("c", script.ChecksumSha256);
                await record.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MigrationException($"Migration {script.DisplayName} failed and was rolled back: {ex.Message}", ex);
        }

        LogApplied(script.DisplayName, stopwatch.ElapsedMilliseconds);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {Host}/{Database} as {User}")]
    private partial void LogConnected(string? host, string? database, string? user);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waited {ElapsedMs} ms for the migration lock")]
    private partial void LogWaitedForLock(long elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {Migration} in {ElapsedMs} ms")]
    private partial void LogApplied(string migration, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Migrations complete: {AppliedCount} applied, {AlreadyApplied} already applied")]
    private partial void LogDone(int appliedCount, int alreadyApplied);
}

internal sealed record AppliedMigration(string Name, string ChecksumSha256);
