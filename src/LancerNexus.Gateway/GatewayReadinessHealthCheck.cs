using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;
using System.Net.Sockets;
using System.Text;

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

        var redisEndpoint = configuration["Gateway:RedisEndpoint"];
        if (!TryParseEndpoint(redisEndpoint, out var redisHost, out var redisPort))
            return HealthCheckResult.Unhealthy("gateway_redis_not_configured");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            _ = await command.ExecuteScalarAsync(timeout.Token);

            using var redis = new TcpClient();
            await redis.ConnectAsync(redisHost, redisPort, timeout.Token);
            await using var stream = redis.GetStream();
            var ping = Encoding.ASCII.GetBytes("*1\r\n$4\r\nPING\r\n");
            await stream.WriteAsync(ping, timeout.Token);
            var response = new byte[7];
            var read = 0;
            while (read < response.Length)
            {
                var received = await stream.ReadAsync(response.AsMemory(read, response.Length - read), timeout.Token);
                if (received == 0)
                    return HealthCheckResult.Unhealthy("gateway_redis_unavailable");
                read += received;
            }

            if (!Encoding.ASCII.GetString(response).Equals("+PONG\r\n", StringComparison.Ordinal))
                return HealthCheckResult.Unhealthy("gateway_redis_unavailable");

            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is MySqlException or SocketException or IOException or OperationCanceledException)
        {
            return exception is MySqlException
                ? HealthCheckResult.Unhealthy("gateway_database_unavailable")
                : HealthCheckResult.Unhealthy("gateway_redis_unavailable");
        }
    }

    private static bool TryParseEndpoint(string? value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 || !int.TryParse(value[(separator + 1)..], out port))
            return false;

        host = value[..separator].Trim();
        return host.Length > 0 && port is > 0 and <= 65535;
    }
}
