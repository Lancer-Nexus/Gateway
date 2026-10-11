using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed class MarketTradeCommitService(
    IMarketTradeCommitStore store,
    CoordinatorMarketClient coordinator,
    TimeProvider timeProvider)
{
    public async Task<MarketTradeCommitResponseV1> CommitAsync(MarketTradeCommitRequestV1 request,
        string authenticatedInstanceId, CancellationToken cancellationToken = default)
    {
        if (request is null || !request.IsValid() || !AdminQuery.ValidIdentifier(authenticatedInstanceId) ||
            !CharacterSaveSnapshotService.TryHashLeaseToken(request.Snapshot.LeaseToken, out var leaseTokenHash))
            return Response(request, MarketTradeCommitStatusV1.Conflict);

        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var prepared = await store.PrepareAsync(authenticatedInstanceId, leaseTokenHash, request, now,
                cancellationToken);
            if (prepared.Status is MarketTradeCommitStoreStatus.LeaseRejected or
                MarketTradeCommitStoreStatus.SnapshotConflict or MarketTradeCommitStoreStatus.NotFound)
                return await AbortUncommittedIntentAsync(request, prepared.Status, cancellationToken);
            var early = MapStoreFailure(request, prepared);
            if (early is not null) return early;
            if (prepared.Status is MarketTradeCommitStoreStatus.Committed or MarketTradeCommitStoreStatus.Duplicate)
                return Response(request, MarketTradeCommitStatusV1.Duplicate, request.Snapshot.Revision);

            var reserved = await coordinator.ReserveQuoteAsync(request.Reservation, cancellationToken);
            if (reserved.Reservation is null)
                return Response(request, MarketTradeCommitStatusV1.Pending);
            if (reserved.Reservation.Status == MarketTradeReservationStatusV1.Unavailable)
                return Response(request, MarketTradeCommitStatusV1.Pending);
            if (reserved.Reservation.Status != MarketTradeReservationStatusV1.Reserved)
            {
                if (reserved.Reservation.Status == MarketTradeReservationStatusV1.Aborted)
                {
                    var aborted = await store.MarkAbortedAsync(request.TradeId, cancellationToken);
                    if (aborted.Status == MarketTradeCommitStoreStatus.Aborted)
                        return Response(request, MarketTradeCommitStatusV1.ReservationRejected);
                }
                if (reserved.Reservation.Status == MarketTradeReservationStatusV1.Committed)
                    return Response(request, MarketTradeCommitStatusV1.Pending);
                var rejected = await store.MarkAbortedAsync(request.TradeId, cancellationToken);
                if (rejected.Status == MarketTradeCommitStoreStatus.Aborted)
                    return Response(request, MarketTradeCommitStatusV1.ReservationRejected);
                return Response(request, MarketTradeCommitStatusV1.Pending);
            }

            var marked = await store.MarkReservedAsync(authenticatedInstanceId, leaseTokenHash, request,
                timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            if (marked.Status is not (MarketTradeCommitStoreStatus.Reserved or
                MarketTradeCommitStoreStatus.Duplicate or MarketTradeCommitStoreStatus.Committed))
            {
                if (marked.Status is MarketTradeCommitStoreStatus.LeaseRejected or
                    MarketTradeCommitStoreStatus.SnapshotConflict or MarketTradeCommitStoreStatus.NotFound)
                    return await AbortReservationAsync(request, marked.Status, cancellationToken);
                return Response(request, MarketTradeCommitStatusV1.Pending);
            }
            if (marked.Status is MarketTradeCommitStoreStatus.Duplicate or MarketTradeCommitStoreStatus.Committed)
                return Response(request, MarketTradeCommitStatusV1.Duplicate, request.Snapshot.Revision);

            var committed = await store.CommitAsync(authenticatedInstanceId, leaseTokenHash, request,
                timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            if (committed.Status is MarketTradeCommitStoreStatus.Committed or MarketTradeCommitStoreStatus.Duplicate)
                return Response(request, committed.Status == MarketTradeCommitStoreStatus.Committed
                    ? MarketTradeCommitStatusV1.Committed : MarketTradeCommitStatusV1.Duplicate,
                    committed.SnapshotRevision);
            if (committed.Status is MarketTradeCommitStoreStatus.LeaseRejected or
                MarketTradeCommitStoreStatus.SnapshotConflict or MarketTradeCommitStoreStatus.NotFound)
                return await AbortReservationAsync(request, committed.Status, cancellationToken);
            return Response(request, MarketTradeCommitStatusV1.Pending);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(leaseTokenHash);
        }
    }

    private async Task<MarketTradeCommitResponseV1> AbortReservationAsync(MarketTradeCommitRequestV1 request,
        MarketTradeCommitStoreStatus reason, CancellationToken cancellationToken)
        => await AbortUncommittedIntentAsync(request, reason, cancellationToken);

    private async Task<MarketTradeCommitResponseV1> AbortUncommittedIntentAsync(
        MarketTradeCommitRequestV1 request, MarketTradeCommitStoreStatus reason,
        CancellationToken cancellationToken)
    {
        var resolutionRequest = new MarketTradeResolutionRequestV1
        {
            CorrelationId = request.CorrelationId,
            TradeId = request.TradeId,
            Resolution = MarketTradeResolutionV1.Abort
        };
        var resolution = await coordinator.ResolveTradeAsync(resolutionRequest, cancellationToken);
        if (resolution.Resolution?.Status is not (MarketTradeReservationStatusV1.Aborted or
            MarketTradeReservationStatusV1.Rejected))
            return Response(request, MarketTradeCommitStatusV1.Pending);
        var marked = await store.MarkAbortedAsync(request.TradeId, cancellationToken);
        if (marked.Status == MarketTradeCommitStoreStatus.Committed)
            return Response(request, MarketTradeCommitStatusV1.Pending);
        if (marked.Status is not (MarketTradeCommitStoreStatus.Aborted or MarketTradeCommitStoreStatus.NotFound))
            return Response(request, MarketTradeCommitStatusV1.Pending);
        return Response(request, reason switch
        {
            MarketTradeCommitStoreStatus.LeaseRejected => MarketTradeCommitStatusV1.LeaseRejected,
            MarketTradeCommitStoreStatus.NotFound => MarketTradeCommitStatusV1.NotFound,
            _ => MarketTradeCommitStatusV1.SnapshotConflict
        });
    }

    private static MarketTradeCommitResponseV1? MapStoreFailure(MarketTradeCommitRequestV1 request,
        MarketTradeCommitStoreResult result) => result.Status switch
        {
            MarketTradeCommitStoreStatus.Committed or MarketTradeCommitStoreStatus.Duplicate =>
                Response(request, MarketTradeCommitStatusV1.Duplicate, result.SnapshotRevision),
            MarketTradeCommitStoreStatus.LeaseRejected => Response(request, MarketTradeCommitStatusV1.LeaseRejected),
            MarketTradeCommitStoreStatus.SnapshotConflict => Response(request, MarketTradeCommitStatusV1.SnapshotConflict),
            MarketTradeCommitStoreStatus.NotFound => Response(request, MarketTradeCommitStatusV1.NotFound),
            MarketTradeCommitStoreStatus.Conflict => Response(request, MarketTradeCommitStatusV1.Conflict),
            MarketTradeCommitStoreStatus.Unavailable => Response(request, MarketTradeCommitStatusV1.Unavailable),
            _ => null
        };

    private static MarketTradeCommitResponseV1 Response(MarketTradeCommitRequestV1? request,
        MarketTradeCommitStatusV1 status, long? revision = null) => new()
        {
            CorrelationId = request?.CorrelationId ?? Guid.NewGuid(),
            TradeId = request?.TradeId ?? Guid.Empty,
            Status = status,
            SnapshotRevision = revision
        };
}
