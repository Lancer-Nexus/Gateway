using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed class MarketTradeOutboxWorker(
    IMarketTradeCommitStore store,
    CoordinatorMarketClient coordinator,
    TimeProvider timeProvider,
    ILogger<MarketTradeOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var intents = await store.ReadPendingIntentsAsync(50, stoppingToken);
            foreach (var intent in intents)
            {
                try
                {
                    var now = timeProvider.GetUtcNow().UtcDateTime;
                    if (!await store.HasActiveIntentLeaseAsync(intent.TradeId, now, stoppingToken))
                    {
                        await AbortIntentAsync(intent.TradeId, intent.AttemptCount, stoppingToken);
                        continue;
                    }

                    if (intent.State == 1)
                    {
                        var reservation = await coordinator.ReserveQuoteAsync(intent.Reservation, stoppingToken);
                        if (reservation.Reservation?.Status == MarketTradeReservationStatusV1.Aborted)
                        {
                            await store.MarkAbortedAsync(intent.TradeId, stoppingToken);
                            continue;
                        }
                        if (reservation.Reservation?.Status is MarketTradeReservationStatusV1.Rejected or
                            MarketTradeReservationStatusV1.NotFound or MarketTradeReservationStatusV1.MarketRevisionMismatch or
                            MarketTradeReservationStatusV1.ReputationRevisionMismatch)
                        {
                            await store.MarkAbortedAsync(intent.TradeId, stoppingToken);
                            continue;
                        }
                        if (reservation.Reservation?.Status != MarketTradeReservationStatusV1.Reserved)
                        {
                            await RetryIntentAsync(intent.TradeId, intent.AttemptCount,
                                reservation.Error ?? reservation.StatusCode?.ToString() ?? "unavailable", stoppingToken);
                            continue;
                        }
                        var marked = await store.MarkRecoveredReservedAsync(intent.TradeId, now, stoppingToken);
                        if (marked.Status == MarketTradeCommitStoreStatus.LeaseRejected)
                        {
                            await AbortIntentAsync(intent.TradeId, intent.AttemptCount, stoppingToken);
                            continue;
                        }
                        if (marked.Status is not (MarketTradeCommitStoreStatus.Reserved or
                            MarketTradeCommitStoreStatus.Duplicate or MarketTradeCommitStoreStatus.Committed))
                        {
                            await RetryIntentAsync(intent.TradeId, intent.AttemptCount, marked.Status.ToString(), stoppingToken);
                            continue;
                        }
                    }

                    var committed = await store.CommitRecoveredAsync(intent.TradeId,
                        timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
                    if (committed.Status is MarketTradeCommitStoreStatus.Committed or
                        MarketTradeCommitStoreStatus.Duplicate)
                        continue;
                    if (committed.Status is MarketTradeCommitStoreStatus.LeaseRejected or
                        MarketTradeCommitStoreStatus.SnapshotConflict or MarketTradeCommitStoreStatus.NotFound)
                    {
                        await AbortIntentAsync(intent.TradeId, intent.AttemptCount, stoppingToken);
                        continue;
                    }
                    await RetryIntentAsync(intent.TradeId, intent.AttemptCount, committed.Status.ToString(), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error) when (error is MySqlConnector.MySqlException or HttpRequestException or InvalidOperationException)
                {
                    await RetryIntentAsync(intent.TradeId, intent.AttemptCount, error.GetType().Name, stoppingToken);
                }
            }

            var pending = await store.ReadPendingOutboxAsync(50, stoppingToken);
            foreach (var item in pending)
            {
                try
                {
                    var request = new MarketTradeResolutionRequestV1
                    {
                        CorrelationId = item.CorrelationId,
                        TradeId = item.TradeId,
                        Resolution = MarketTradeResolutionV1.Commit
                    };
                    var result = await coordinator.ResolveTradeAsync(request, stoppingToken);
                    if (result.Resolution?.Status == MarketTradeReservationStatusV1.Committed)
                    {
                        await store.MarkOutboxDeliveredAsync(item.TradeId,
                            timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
                        continue;
                    }
                    await store.ScheduleOutboxRetryAsync(item.TradeId,
                        timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
                    logger.LogWarning("Coordinator market outbox attempt {Attempt} was not acknowledged ({Error}).",
                        item.AttemptCount + 1, result.Error ?? result.StatusCode?.ToString() ?? "unavailable");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error) when (error is MySqlConnector.MySqlException or HttpRequestException or InvalidOperationException)
                {
                    await store.ScheduleOutboxRetryAsync(item.TradeId,
                        timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
                    logger.LogWarning("Coordinator market outbox attempt {Attempt} will retry after {ErrorType}.",
                        item.AttemptCount + 1, error.GetType().Name);
                }
            }
        }
    }

    private async Task AbortIntentAsync(Guid tradeId, int attempt, CancellationToken cancellationToken)
    {
        var request = new MarketTradeResolutionRequestV1
        {
            CorrelationId = tradeId,
            TradeId = tradeId,
            Resolution = MarketTradeResolutionV1.Abort
        };
        var result = await coordinator.ResolveTradeAsync(request, cancellationToken);
        if (result.Resolution?.Status is MarketTradeReservationStatusV1.Aborted or
            MarketTradeReservationStatusV1.Rejected)
        {
            var marked = await store.MarkAbortedAsync(tradeId, cancellationToken);
            if (marked.Status is MarketTradeCommitStoreStatus.Aborted or MarketTradeCommitStoreStatus.NotFound)
                return;
        }
        await RetryIntentAsync(tradeId, attempt,
            result.Error ?? result.Resolution?.Status.ToString() ?? result.StatusCode?.ToString() ?? "unavailable",
            cancellationToken);
    }

    private async Task RetryIntentAsync(Guid tradeId, int attempt, string reason,
        CancellationToken cancellationToken)
    {
        await store.ScheduleIntentRetryAsync(tradeId, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        logger.LogWarning("Market trade intent recovery attempt {Attempt} will retry ({Reason}).",
            attempt + 1, reason);
    }
}
