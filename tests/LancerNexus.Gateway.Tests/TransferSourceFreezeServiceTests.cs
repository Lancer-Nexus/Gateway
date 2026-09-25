using System.Net;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class TransferSourceFreezeServiceTests
{
    [Fact]
    public async Task OnlyAuthenticatedSourceCanAdvancePreparedTransferAndRetryIsIdempotent()
    {
        var coordinator = new FakeCoordinator(TransferState.Prepared);
        var service = new TransferSourceFreezeService(coordinator, TimeProvider.System);

        var wrongSource = await service.MarkFrozenAsync(coordinator.TransferId, "li03-instance");
        Assert.False(wrongSource.Accepted);
        Assert.Equal("transfer_source_instance_mismatch", wrongSource.ReasonCode);
        Assert.Equal(0, coordinator.MarkSourceFrozenCalls);

        var frozen = await service.MarkFrozenAsync(coordinator.TransferId, "li01-instance");
        var retry = await service.MarkFrozenAsync(coordinator.TransferId, "li01-instance");

        Assert.True(frozen.Accepted);
        Assert.Equal("source_frozen", frozen.ReasonCode);
        Assert.True(retry.Accepted);
        Assert.Equal("duplicate", retry.ReasonCode);
        Assert.Equal(1, coordinator.MarkSourceFrozenCalls);
    }

    [Fact]
    public async Task TransferMustBePreparedAndUnexpiredBeforeFreezeTransition()
    {
        var coordinator = new FakeCoordinator(TransferState.TargetAccepted);
        var service = new TransferSourceFreezeService(coordinator, TimeProvider.System);

        var result = await service.MarkFrozenAsync(coordinator.TransferId, "li01-instance");

        Assert.False(result.Accepted);
        Assert.Equal("transfer_not_prepared", result.ReasonCode);
        Assert.Equal(0, coordinator.MarkSourceFrozenCalls);
    }

    private sealed class FakeCoordinator(TransferState state) : ICoordinatorTransferClient
    {
        private TransferState currentState = state;
        public Guid TransferId { get; } = Guid.NewGuid();
        public int MarkSourceFrozenCalls { get; private set; }

        public Task<CoordinatorTransferLookupResult> GetTransferAsync(Guid transferId,
            CancellationToken cancellationToken = default)
        {
            var expiresUtc = DateTime.UtcNow.AddMinutes(1);
            CoordinatorTransferSnapshot? snapshot = transferId == TransferId
                ? new CoordinatorTransferSnapshot(TransferId, new TransferPrepareRequest
                {
                    TransferId = TransferId,
                    SessionId = Guid.NewGuid(),
                    CharacterId = 73,
                    SourceInstanceId = "li01-instance",
                    TargetInstanceId = "li02-instance",
                    TargetSystemId = "li02",
                    ExpiresUtc = expiresUtc,
                    IdempotencyKey = "source-freeze-test"
                }, currentState, expiresUtc, 4)
                : null;
            return Task.FromResult(new CoordinatorTransferLookupResult(HttpStatusCode.OK, snapshot, null));
        }

        public Task<CoordinatorTransferStateResult> MarkSourceFrozenAsync(Guid transferId,
            CancellationToken cancellationToken = default)
        {
            MarkSourceFrozenCalls++;
            currentState = TransferState.SourceFrozen;
            return Task.FromResult(new CoordinatorTransferStateResult(HttpStatusCode.OK,
                new CoordinatorTransferOutcome(true, "accepted", currentState, false), null));
        }

        public Task<CoordinatorTransferCallResult> PrepareAsync(TransferPrepareRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkTargetAcceptedAsync(Guid transferId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> CommitAsync(Guid transferId, long leaseVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkSourceReleasedAsync(Guid transferId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> AbortAsync(TransferAbort request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
