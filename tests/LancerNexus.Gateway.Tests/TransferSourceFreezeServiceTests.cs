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
        var snapshots = new FakeSnapshotStore();
        var service = new TransferSourceFreezeService(coordinator, new FakeAccounts(), snapshots, TimeProvider.System);

        var wrongSource = await service.MarkFrozenAsync(coordinator.TransferId, "li03-instance", 4, new byte[] { 1, 2, 3 });
        Assert.False(wrongSource.Accepted);
        Assert.Equal("transfer_source_instance_mismatch", wrongSource.ReasonCode);
        Assert.Equal(0, coordinator.MarkSourceFrozenCalls);

        var frozen = await service.MarkFrozenAsync(coordinator.TransferId, "li01-instance", 4, new byte[] { 1, 2, 3 });
        var retry = await service.MarkFrozenAsync(coordinator.TransferId, "li01-instance", 4, new byte[] { 1, 2, 3 });

        Assert.True(frozen.Accepted);
        Assert.Equal("source_frozen", frozen.ReasonCode);
        Assert.True(retry.Accepted);
        Assert.Equal("duplicate", retry.ReasonCode);
        Assert.Equal(1, coordinator.MarkSourceFrozenCalls);
        Assert.Equal(1, snapshots.StoredCount);
    }

    [Fact]
    public async Task TransferMustBePreparedAndUnexpiredBeforeFreezeTransition()
    {
        var coordinator = new FakeCoordinator(TransferState.TargetAccepted);
        var snapshots = new FakeSnapshotStore();
        var service = new TransferSourceFreezeService(coordinator, new FakeAccounts(), snapshots, TimeProvider.System);

        var result = await service.MarkFrozenAsync(coordinator.TransferId, "li01-instance", 4, new byte[] { 1, 2, 3 });

        Assert.False(result.Accepted);
        Assert.Equal("transfer_not_prepared", result.ReasonCode);
        Assert.Equal(0, coordinator.MarkSourceFrozenCalls);
        Assert.Equal(0, snapshots.StoredCount);
    }

    [Fact]
    public async Task StaleLeaseVersionCannotStageOrFreezeSnapshot()
    {
        var coordinator = new FakeCoordinator(TransferState.Prepared);
        var snapshots = new FakeSnapshotStore();
        var service = new TransferSourceFreezeService(coordinator, new FakeAccounts(), snapshots, TimeProvider.System);

        var result = await service.MarkFrozenAsync(coordinator.TransferId, "li01-instance", 3,
            new byte[] { 1, 2, 3 });

        Assert.False(result.Accepted);
        Assert.Equal("source_lease_changed", result.ReasonCode);
        Assert.Equal(0, snapshots.StoredCount);
        Assert.Equal(0, coordinator.MarkSourceFrozenCalls);
    }

    [Fact]
    public async Task OnlyBoundTargetCanRetrieveSnapshotAfterSourceFreeze()
    {
        var coordinator = new FakeCoordinator(TransferState.SourceFrozen);
        var snapshots = new FakeSnapshotStore();
        await snapshots.StoreAsync(new TransferSnapshotMetadata(coordinator.TransferId, "li01-instance",
            "li02-instance", 73, 0, DateTime.UtcNow), new byte[] { 4, 5, 6 });
        var service = new TransferSnapshotReadService(coordinator, snapshots);

        var wrongTarget = await service.ReadForTargetAsync(coordinator.TransferId, "li03-instance");
        var target = await service.ReadForTargetAsync(coordinator.TransferId, "li02-instance");

        Assert.Equal(TransferSnapshotStoreStatus.NotFound, wrongTarget.Status);
        Assert.Equal(TransferSnapshotStoreStatus.Stored, target.Status);
        Assert.Equal(new byte[] { 4, 5, 6 }, target.Record!.Snapshot);
    }

    private sealed class FakeSnapshotStore : ITransferSnapshotStore
    {
        private TransferSnapshotRecord? record;
        public int StoredCount { get; private set; }

        public Task<TransferSnapshotStoreResult> StoreAsync(TransferSnapshotMetadata metadata,
            ReadOnlyMemory<byte> snapshot, CancellationToken cancellationToken = default)
        {
            if (record is not null)
            {
                var metadataMatches = record.Metadata.TransferId == metadata.TransferId &&
                                      record.Metadata.SourceInstanceId == metadata.SourceInstanceId &&
                                      record.Metadata.TargetInstanceId == metadata.TargetInstanceId &&
                                      record.Metadata.CharacterId == metadata.CharacterId &&
                                      record.Metadata.LeaseVersion == metadata.LeaseVersion;
                return Task.FromResult(new TransferSnapshotStoreResult(
                    metadataMatches && record.Snapshot.SequenceEqual(snapshot.ToArray())
                        ? TransferSnapshotStoreStatus.Duplicate
                        : TransferSnapshotStoreStatus.Conflict, record));
            }
            record = new TransferSnapshotRecord(metadata, snapshot.ToArray());
            StoredCount++;
            return Task.FromResult(new TransferSnapshotStoreResult(TransferSnapshotStoreStatus.Stored, record));
        }

        public Task<TransferSnapshotStoreResult> ReadAsync(Guid transferId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(record?.Metadata.TransferId == transferId
                ? new TransferSnapshotStoreResult(TransferSnapshotStoreStatus.Stored, record)
                : new TransferSnapshotStoreResult(TransferSnapshotStoreStatus.NotFound));
    }

    private sealed class FakeAccounts : IAccountRepository
    {
        public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseForTransferAsync(Guid sessionId, long characterId,
            DateTime nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterLeaseRecord?>(new CharacterLeaseRecord(
                "li01-instance", 4, nowUtc.AddMinutes(1)));

        public Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash,
            DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
            byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId, long characterId,
            DateTime nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
            long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
            byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
