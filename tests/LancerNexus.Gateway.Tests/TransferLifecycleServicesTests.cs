using System.Net;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class TransferLifecycleServicesTests
{
    [Fact]
    public async Task SourceCanPollTransferStatusButOtherInstancesCannot()
    {
        var coordinator = new FakeCoordinator(TransferState.Committed, leaseVersion: 15);
        var status = new TransferStatusService(coordinator);

        var source = await status.GetAsync(coordinator.TransferId, "li01-instance");
        var target = await status.GetAsync(coordinator.TransferId, "li02-instance");
        var other = await status.GetAsync(coordinator.TransferId, "li03-instance");

        Assert.True(source.Accepted);
        Assert.Equal(TransferState.Committed, source.Status!.State);
        Assert.Equal(15, source.Status.LeaseVersion);
        Assert.True(target.Accepted);
        Assert.False(other.Accepted);
        Assert.Equal("transfer_instance_mismatch", other.ReasonCode);
    }

    [Fact]
    public async Task SourceReleaseRequiresCommittedLeaseAndIsIdempotent()
    {
        var coordinator = new FakeCoordinator(TransferState.TargetAccepted, leaseVersion: 0);
        var release = new TransferSourceReleaseService(coordinator);

        var early = await release.ReleaseAsync(new TransferSourceReleaseRequest
        { TransferId = coordinator.TransferId }, "li01-instance");
        Assert.False(early.Accepted);
        Assert.Equal("transfer_not_committed", early.ReasonCode);
        Assert.Equal(0, coordinator.SourceReleaseCalls);

        coordinator.SetState(TransferState.Committed, leaseVersion: 15);
        var completed = await release.ReleaseAsync(new TransferSourceReleaseRequest
        { TransferId = coordinator.TransferId }, "li01-instance");
        var duplicate = await release.ReleaseAsync(new TransferSourceReleaseRequest
        { TransferId = coordinator.TransferId }, "li01-instance");
        var wrongSource = await release.ReleaseAsync(new TransferSourceReleaseRequest
        { TransferId = coordinator.TransferId }, "li03-instance");

        Assert.True(completed.Accepted);
        Assert.True(duplicate.Accepted);
        Assert.Equal("duplicate", duplicate.ReasonCode);
        Assert.False(wrongSource.Accepted);
        Assert.Equal(1, coordinator.SourceReleaseCalls);
    }

    private sealed class FakeCoordinator(TransferState state, long leaseVersion) : ICoordinatorTransferClient
    {
        private TransferState state = state;
        private long leaseVersion = leaseVersion;
        public Guid TransferId { get; } = Guid.NewGuid();
        public int SourceReleaseCalls { get; private set; }

        private CoordinatorTransferSnapshot Snapshot() => new(TransferId, new TransferPrepareRequest
        {
            TransferId = TransferId,
            SessionId = Guid.NewGuid(),
            CharacterId = 73,
            SourceInstanceId = "li01-instance",
            TargetInstanceId = "li02-instance",
            TargetSystemId = "li02",
            ExpiresUtc = DateTime.UtcNow.AddMinutes(1),
            IdempotencyKey = "lifecycle-test"
        }, state, DateTime.UtcNow.AddMinutes(1), leaseVersion);

        public void SetState(TransferState nextState, long leaseVersion)
        {
            state = nextState;
            this.leaseVersion = leaseVersion;
        }

        public Task<CoordinatorTransferLookupResult> GetTransferAsync(Guid transferId,
            CancellationToken cancellationToken = default) => Task.FromResult(new CoordinatorTransferLookupResult(
            HttpStatusCode.OK, transferId == TransferId ? Snapshot() : null, null));

        public Task<CoordinatorTransferStateResult> MarkSourceReleasedAsync(Guid transferId,
            CancellationToken cancellationToken = default)
        {
            SourceReleaseCalls++;
            state = TransferState.SourceReleased;
            return Task.FromResult(new CoordinatorTransferStateResult(HttpStatusCode.OK,
                new CoordinatorTransferOutcome(true, "accepted", state, false), null));
        }

        public Task<CoordinatorTransferCallResult> PrepareAsync(TransferPrepareRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkSourceFrozenAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkTargetAcceptedAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> CommitAsync(Guid transferId, long leaseVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> AbortAsync(TransferAbort request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
