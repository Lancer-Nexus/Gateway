using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;

namespace LancerNexus.Gateway;

public sealed class GatewayReadinessHealthCheck(
    CoordinatorGatewayOptions coordinatorOptions,
    SessionTokenOptions tokenOptions,
    IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!tokenOptions.IsConfigured)
            return HealthCheckResult.Unhealthy("token_signing_not_configured");
        if (!coordinatorOptions.IsConfigured)
            return HealthCheckResult.Unhealthy("coordinator_not_configured");

        var connectionString = configuration.GetConnectionString("Gateway");
        if (string.IsNullOrWhiteSpace(connectionString))
            return HealthCheckResult.Unhealthy("gateway_database_not_configured");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            _ = await command.ExecuteScalarAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is MySqlException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("gateway_database_unavailable");
        }
    }
}
