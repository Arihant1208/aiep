using Aiep.Migrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Configuration: ConnectionStrings:AiepMigrator (must be the aiep_migrator role) and optionally
// Migrator:MigrationsPath. Environment variables and command-line arguments work as usual for .NET hosts.
// Content root = the binary's folder, so appsettings*.json and migrations/ resolve the same from any working directory.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Aiep.Migrator");

try
{
    var connectionString = builder.Configuration.GetConnectionString("AiepMigrator")
        ?? throw new MigrationException("Connection string 'ConnectionStrings:AiepMigrator' is not configured.");
    var path = builder.Configuration["Migrator:MigrationsPath"] ?? Path.Combine(AppContext.BaseDirectory, "migrations");

    var scripts = MigrationDiscovery.Load(path);
    var runner = new MigrationRunner(connectionString, host.Services.GetRequiredService<ILogger<MigrationRunner>>());
    await runner.RunAsync(scripts);
    return 0;
}
catch (MigrationException ex)
{
    logger.LogError("{Message}", ex.Message);
    return 1;
}
