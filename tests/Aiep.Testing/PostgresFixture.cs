using Aiep.Migrator;
using DotNet.Testcontainers.Configurations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Aiep.Testing;

/// <summary>
/// One PostgreSQL 17 container per test run, initialised with the real <c>db/init/01-roles.sh</c> and migrated with
/// the real <c>db/migrations</c> by <see cref="MigrationRunner"/>, exactly as Compose does.
/// Before migrating, the freshly initialised database is copied to a template so tests can create empty
/// databases that have the same roles, schema and default privileges.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ApiPassword = "api_test";
    public const string WorkerPassword = "worker_test";
    public const string MigratorPassword = "migrator_test";
    private const string TemplateDatabase = "aiep_template";

    private readonly PostgreSqlContainer _container;
    private int _databaseCounter;

    public PostgresFixture()
    {
        var initScript = File.ReadAllBytes(Path.Combine(RepositoryRoot, "db", "init", "01-roles.sh"));
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("aiep")
            .WithUsername("postgres")
            .WithPassword("postgres_test")
            .WithEnvironment("AIEP_MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("AIEP_API_PASSWORD", ApiPassword)
            .WithEnvironment("AIEP_WORKER_PASSWORD", WorkerPassword)
            .WithResourceMapping(
                initScript,
                "/docker-entrypoint-initdb.d/01-roles.sh",
                fileMode: UnixFileModes.UserRead | UnixFileModes.UserExecute | UnixFileModes.GroupRead
                | UnixFileModes.GroupExecute | UnixFileModes.OtherRead | UnixFileModes.OtherExecute)
            .Build();
    }

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string MigrationsDirectory => Path.Combine(RepositoryRoot, "db", "migrations");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ExecuteAsSuperuserAsync($"CREATE DATABASE {TemplateDatabase} TEMPLATE aiep");
        var runner = new MigrationRunner(ConnectionString("aiep_migrator"), NullLogger<MigrationRunner>.Instance);
        await runner.RunAsync(MigrationDiscovery.Load(MigrationsDirectory));
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Connection string for one of the init-script roles (or the superuser) on a database.</summary>
    public string ConnectionString(string role, string database = "aiep", bool pooling = false)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Username = role,
            Password = role switch
            {
                "aiep_api" => ApiPassword,
                "aiep_worker" => WorkerPassword,
                "aiep_migrator" => MigratorPassword,
                "postgres" => "postgres_test",
                _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
            },
            Pooling = pooling,
        };
        return builder.ConnectionString;
    }

    /// <summary>A new, unmigrated database with the init script's roles, schema and default privileges.</summary>
    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = $"scratch_{Interlocked.Increment(ref _databaseCounter)}";
        await ExecuteAsSuperuserAsync($"CREATE DATABASE {name} TEMPLATE {TemplateDatabase}");
        return name;
    }

    /// <summary>
    /// A new, fully migrated database, isolated from the shared <c>aiep</c> one. Used by cross-tenant tests (such as the
    /// work queue) that would otherwise see every other test's rows.
    /// </summary>
    public async Task<string> CreateMigratedDatabaseAsync()
    {
        var name = await CreateEmptyDatabaseAsync();
        var runner = new MigrationRunner(ConnectionString("aiep_migrator", name), NullLogger<MigrationRunner>.Instance);
        await runner.RunAsync(MigrationDiscovery.Load(MigrationsDirectory));
        return name;
    }

    private async Task ExecuteAsSuperuserAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString("postgres", "postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aiep.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (Aiep.slnx).");
    }
}
