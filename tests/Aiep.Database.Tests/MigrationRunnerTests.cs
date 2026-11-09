using Aiep.Migrator;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aiep.Database.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MigrationRunnerTests(PostgresFixture db)
{
    private static MigrationScript Script(int version, string sql, string name = "step") =>
        MigrationScript.FromText(version, name, sql);

    private async Task<(string Database, MigrationRunner Runner)> NewDatabaseAsync()
    {
        var database = await db.CreateEmptyDatabaseAsync();
        return (database, NewRunner(database));
    }

    private MigrationRunner NewRunner(string database) =>
        new(db.ConnectionString("aiep_migrator", database), NullLogger<MigrationRunner>.Instance);

    private async Task<List<(int Version, string Name)>> HistoryAsync(string database)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString("aiep_migrator", database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT version, name FROM aiep.schema_migrations ORDER BY version", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(int, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        return rows;
    }

    private async Task<bool> TableExistsAsync(string database, string table)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString("aiep_migrator", database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", connection);
        command.Parameters.AddWithValue("t", $"aiep.{table}");
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Applies_pending_scripts_in_order_and_a_rerun_is_a_no_op()
    {
        var (database, runner) = await NewDatabaseAsync();
        MigrationScript[] scripts =
        [
            Script(1, "CREATE TABLE aiep.t1 (id int);"),
            Script(2, "ALTER TABLE aiep.t1 ADD COLUMN name text;"),
        ];

        var first = await runner.RunAsync(scripts);
        var second = await runner.RunAsync(scripts);

        Assert.Equal([1, 2], first.Applied.Select(s => s.Version));
        Assert.Empty(second.Applied);
        Assert.Equal(2, second.AlreadyApplied);
        Assert.Equal([(1, "step"), (2, "step")], await HistoryAsync(database));
    }

    [Fact]
    public async Task An_edited_applied_script_stops_the_run_before_anything_else_is_applied()
    {
        var (database, runner) = await NewDatabaseAsync();
        await runner.RunAsync([Script(1, "CREATE TABLE aiep.t1 (id int);")]);

        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.RunAsync(
        [
            Script(1, "CREATE TABLE aiep.t1 (id bigint);"),
            Script(2, "CREATE TABLE aiep.t2 (id int);"),
        ]));

        Assert.Contains("has changed since it was applied", ex.Message, StringComparison.Ordinal);
        Assert.False(await TableExistsAsync(database, "t2"));
        Assert.Single(await HistoryAsync(database));
    }

    [Fact]
    public async Task A_renamed_applied_script_is_treated_as_changed()
    {
        var (_, runner) = await NewDatabaseAsync();
        await runner.RunAsync([Script(1, "SELECT 1;", name: "original")]);

        await Assert.ThrowsAsync<MigrationException>(() => runner.RunAsync([Script(1, "SELECT 1;", name: "renamed")]));
    }

    [Fact]
    public async Task A_missing_applied_script_stops_the_run()
    {
        var (_, runner) = await NewDatabaseAsync();
        await runner.RunAsync([Script(1, "SELECT 1;"), Script(2, "SELECT 2;")]);

        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.RunAsync([Script(1, "SELECT 1;")]));
        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_script_older_than_the_newest_applied_one_stops_the_run()
    {
        var (database, runner) = await NewDatabaseAsync();
        await runner.RunAsync([Script(1, "SELECT 1;"), Script(3, "SELECT 3;")]);

        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.RunAsync(
            [Script(1, "SELECT 1;"), Script(2, "CREATE TABLE aiep.t2 (id int);"), Script(3, "SELECT 3;")]));

        Assert.Contains("older than the newest applied version V0003", ex.Message, StringComparison.Ordinal);
        Assert.False(await TableExistsAsync(database, "t2"));
    }

    [Fact]
    public async Task A_failing_script_is_rolled_back_and_not_recorded_and_earlier_ones_stay_applied()
    {
        var (database, runner) = await NewDatabaseAsync();

        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.RunAsync(
        [
            Script(1, "CREATE TABLE aiep.t1 (id int);"),
            Script(2, "CREATE TABLE aiep.t2 (id int); SELECT 1 / 0;"),
        ]));

        Assert.Contains("V0002__step failed and was rolled back", ex.Message, StringComparison.Ordinal);
        Assert.True(await TableExistsAsync(database, "t1"));
        Assert.False(await TableExistsAsync(database, "t2"));
        Assert.Equal([(1, "step")], await HistoryAsync(database));

        // After fixing the script, a rerun applies it.
        var fixedRun = await runner.RunAsync(
            [Script(1, "CREATE TABLE aiep.t1 (id int);"), Script(2, "CREATE TABLE aiep.t2 (id int);")]);
        Assert.Equal([2], fixedRun.Applied.Select(s => s.Version));
    }

    [Fact]
    public async Task Concurrent_runners_apply_each_script_exactly_once()
    {
        var database = await db.CreateEmptyDatabaseAsync();
        // Not idempotent on purpose: a second application would fail with "relation already exists".
        MigrationScript[] scripts =
        [
            Script(1, "CREATE TABLE aiep.t1 (id int); SELECT pg_sleep(0.5);"),
            Script(2, "CREATE TABLE aiep.t2 (id int);"),
        ];

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => NewRunner(database).RunAsync(scripts)));

        Assert.Equal(2, results.Sum(r => r.Applied.Count));
        Assert.Equal([(1, "step"), (2, "step")], await HistoryAsync(database));
    }

    [Theory]
    [InlineData("aiep_api")]
    [InlineData("aiep_worker")]
    public async Task Service_roles_cannot_read_or_write_migration_history(string role)
    {
        var (database, runner) = await NewDatabaseAsync();
        await runner.RunAsync([Script(1, "SELECT 1;")]);

        await using var connection = new NpgsqlConnection(db.ConnectionString(role, database));
        await connection.OpenAsync();
        foreach (var sql in new[]
                 {
                     "SELECT count(*) FROM aiep.schema_migrations",
                     "DELETE FROM aiep.schema_migrations",
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }
}
