using Aiep.Application.Identity;
using Aiep.Infrastructure.Identity;
using Aiep.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aiep.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string ConnectionStringName = "Aiep";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // Resolved lazily so hosts can start (and report liveness) before configuration is validated.
        // NpgsqlDataSource and AiepDbContext are deliberately not registered: TenantDatabase is the only way in.
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured.");
            return new AiepDataSource(NpgsqlDataSource.Create(connectionString));
        });
        services.AddSingleton(sp => new TenantDatabase(sp.GetRequiredService<AiepDataSource>()));
        services.AddSingleton<ISubjectDirectory>(sp => new SubjectDirectory(sp.GetRequiredService<TenantDatabase>()));
        services.AddSingleton<IApiKeyStore>(sp => new ApiKeyStore(sp.GetRequiredService<TenantDatabase>()));

        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

        return services;
    }
}
