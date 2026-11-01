using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Aiep.Infrastructure.Persistence;

internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // Do not include connection details in the description; it may be exposed by the endpoint.
            return new HealthCheckResult(context.Registration.FailureStatus, "PostgreSQL is unreachable.", ex);
        }
    }
}
