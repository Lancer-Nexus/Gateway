using Microsoft.Extensions.Hosting;

namespace LancerNexus.Gateway;

public sealed class ExpiredSessionCleanupService(
    IAccountRepository accounts,
    TimeProvider timeProvider,
    ILogger<ExpiredSessionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var revoked = await accounts.RevokeExpiredSessionsAsync(
                    timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
                if (revoked > 0)
                    logger.LogInformation("Revoked {SessionCount} expired idle sessions.", revoked);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Expired session cleanup failed: {ExceptionType}.", exception.GetType().Name);
            }

            await Task.Delay(CleanupInterval, stoppingToken);
        }
    }
}
