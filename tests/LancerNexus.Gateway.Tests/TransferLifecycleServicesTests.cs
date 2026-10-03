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
        var accounts = new FakeAccounts { Commit = coordinator.CommitRecord() };
        var status = new TransferStatusService(coordinator, accounts);

        var source = await status.GetAsync(coordinator.TransferId, "li01-instance");
        var target = await status.GetAsync(coordinator.TransferId, "li02-instance");
        var other = await status.GetAsync(coordinator.TransferId, "li03-instance");

        Assert.True(source.Accepted);
        Assert.Equal(TransferState.Committed, source.Status!.State);
        Assert.Equal(15, source.Status.LeaseVersion);
        Assert.True(target.Accepted);
        Assert.False(other.Accepted);
        Assert.Equal("transfer_instance_mismatch", other.ReasonCode);
        Assert.Equal(2, accounts.ReadCalls);
    }

    [Theory]
    [InlineData(TransferState.SourceFrozen)]
    [InlineData(TransferState.TargetAccepted)]
    public async Task SqlCommitIsReportedEvenWhenCoordinatorAcknowledgementWasLost(TransferState state)
    {
        var coordinator = new FakeCoordinator(state, leaseVersion: 0);
        var accounts = new FakeAccounts { Commit = coordinator.CommitRecord() };
        var status = new TransferStatusService(coordinator, accounts);
        var result = await status.GetAsync(coordinator.TransferId, "li02-instance");
        Assert.True(result.Accepted);
        Assert.Equal(TransferState.Committed, result.Status!.State);
        Assert.Equal(15, result.Status.LeaseVersion);
    }

    [Theory]
    [InlineData(TransferState.Committed)]
    [InlineData(TransferState.SourceReleased)]
    public async Task CoordinatorAloneCannotProveCharacterOwnershipCommit(TransferState state)
    {
        var coordinator = new FakeCoordinator(state, leaseVersion: 15);
        var result = await new TransferStatusService(coordinator, new FakeAccounts())
            .GetAsync(coordinator.TransferId, "li02-instance");
        Assert.False(result.Accepted);
        Assert.Null(result.Status);
        Assert.Equal("character_transfer_commit_missing", result.ReasonCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task ConflictingCommitBindingFailsClosed(int field)
    {
        var coordinator = new FakeCoordinator(TransferState.Committed, leaseVersion: 15);
        var commit = coordinator.CommitRecord();
        commit = field switch
        {
            0 => commit with { TransferId = Guid.NewGuid() },
            1 => commit with { SessionId = Guid.NewGuid() },
            2 => commit with { CharacterId = 74 },
            3 => commit with { SourceInstanceId = "other-source" },
            4 => commit with { TargetInstanceId = "other-target" },
            5 => commit with { ExpectedLeaseVersion = -1 },
            6 => commit with { ExpectedLeaseVersion = long.MaxValue },
            _ => commit with { CommittedLeaseVersion = 16 }
        };
        var result = await new TransferStatusService(coordinator, new FakeAccounts { Commit = commit })
            .GetAsync(coordinator.TransferId, "li02-instance");
        Assert.False(result.Accepted);
        Assert.Equal("character_transfer_commit_mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task SqlCommitAndCoordinatorAbortCannotAuthorizeSourceRollback()
    {
        var coordinator = new FakeCoordinator(TransferState.Aborted, leaseVersion: 0);
        var result = await new TransferStatusService(coordinator,
                new FakeAccounts { Commit = coordinator.CommitRecord() })
            .GetAsync(coordinator.TransferId, "li01-instance");
        Assert.False(result.Accepted);
        Assert.Null(result.Status);
        Assert.Equal("character_transfer_commit_mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task UnavailableSqlCannotAuthorizeCommitOrRollback()
    {
        var coordinator = new FakeCoordinator(TransferState.TargetAccepted, leaseVersion: 0);
        var result = await new TransferStatusService(coordinator, new AccountRepositoryNotConfigured())
            .GetAsync(coordinator.TransferId, "li01-instance");
        Assert.False(result.Accepted);
        Assert.Null(result.Status);
        Assert.Equal(TransferSourceReleaseFailure.PersistenceUnavailable, result.Failure);
    }

    [Fact]
    public async Task PendingTransferWithoutSqlCommitRetainsCoordinatorState()
    {
        var coordinator = new FakeCoordinator(TransferState.TargetAccepted, leaseVersion: 0);
        var result = await new TransferStatusService(coordinator, new FakeAccounts())
            .GetAsync(coordinator.TransferId, "li02-instance");
        Assert.True(result.Accepted);
        Assert.Equal(TransferState.TargetAccepted, result.Status!.State);
        Assert.Equal(0, result.Status.LeaseVersion);
    }

    [Fact]
    public async Task MissionAuthorityConfirmsSqlCommitInCoordinatorAcknowledgementWindow()
    {
        var coordinator = new FakeCoordinator(TransferState.TargetAccepted, 0);
        var committed = coordinator.CommitRecord();
        var accounts = new FakeAccounts
        {
            Commit = committed,
            Decision = new(new(committed.TransferId, committed.SessionId, 73, "li01-instance", "li02-instance", 14),
                CharacterTransferDecisionKind.Committed)
        };
        var authority = new NpcMissionAuthorityService(coordinator, accounts, new FakeSnapshotStore(), TimeProvider.System);
        var request = AuthorityRequest(coordinator, NpcTransferState.Committed);
        Assert.True((await authority.DecideAsync(request)).Authorizes(request));
        Assert.False((await authority.DecideAsync(request with { Decision = NpcTransferState.Aborted })).Accepted);
        Assert.Equal(0, accounts.AbortCalls);
    }

    [Fact]
    public async Task MissionAuthorityAbortIsPermanentAndReplayDoesNotNeedSnapshot()
    {
        var coordinator = new FakeCoordinator(TransferState.TargetAccepted, 0);
        var accounts = new FakeAccounts();
        var snapshots = new FakeSnapshotStore
        {
            ReadResult = new(TransferSnapshotStoreStatus.Stored, new(new(coordinator.TransferId,
                "li01-instance", "li02-instance", 73, 14, DateTime.UtcNow), [1]))
        };
        var authority = new NpcMissionAuthorityService(coordinator, accounts, snapshots, TimeProvider.System);
        var request = AuthorityRequest(coordinator, NpcTransferState.Aborted);
        Assert.True((await authority.DecideAsync(request)).Authorizes(request));
        Assert.Equal(14, accounts.Decision!.Binding.ExpectedLeaseVersion);
        snapshots.ReadResult = new(TransferSnapshotStoreStatus.Unavailable);
        authority = new(coordinator, accounts, snapshots, TimeProvider.System);
        Assert.True((await authority.DecideAsync(request)).Authorizes(request));
        Assert.Equal(1, accounts.AbortCalls);
        Assert.False((await authority.DecideAsync(request with { Decision = NpcTransferState.Committed })).Accepted);
    }

    [Fact]
    public async Task MissionAuthorityRejectsWrongBindingAndUncommittedOwnership()
    {
        var coordinator = new FakeCoordinator(TransferState.TargetAccepted, 0);
        var accounts = new FakeAccounts();
        var authority = new NpcMissionAuthorityService(coordinator, accounts, new FakeSnapshotStore(), TimeProvider.System);
        var request = AuthorityRequest(coordinator, NpcTransferState.Committed);
        Assert.False((await authority.DecideAsync(request)).Accepted);
        Assert.False((await authority.DecideAsync(request with { TargetSystemId = "li03" })).Accepted);
        Assert.False((await authority.DecideAsync(request with { SourceInstanceId = "wrong-source" })).Accepted);
        Assert.False((await authority.DecideAsync(request with { TargetInstanceId = "wrong-target" })).Accepted);
        Assert.Equal(0, accounts.AbortCalls);
    }

    [Fact]
    public async Task FrozenMissionWithoutCharacterSnapshotCannotAuthorizeRollback()
    {
        var coordinator = new FakeCoordinator(TransferState.SourceFrozen, 0);
        var accounts = new FakeAccounts();
        var authority = new NpcMissionAuthorityService(coordinator, accounts, new FakeSnapshotStore(), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authority.DecideAsync(AuthorityRequest(coordinator, NpcTransferState.Aborted)));
        Assert.Equal(0, accounts.AbortCalls);
    }

    [Fact]
    public async Task DurableAbortRemainsDiscoverableAfterLostCoordinatorAcknowledgement()
    {
        var coordinator = new FakeCoordinator(TransferState.SourceFrozen, 0);
        var committed = coordinator.CommitRecord();
        var accounts = new FakeAccounts
        {
            Decision = new(new(committed.TransferId, committed.SessionId, 73, "li01-instance", "li02-instance", 14),
                CharacterTransferDecisionKind.Aborted)
        };
        var result = await new TransferStatusService(coordinator, accounts)
            .GetAsync(coordinator.TransferId, "li01-instance");
        Assert.True(result.Accepted);
        Assert.Equal(TransferState.Aborted, result.Status!.State);
        var authority = new NpcMissionAuthorityService(coordinator, accounts, new FakeSnapshotStore(), TimeProvider.System);
        var request = AuthorityRequest(coordinator, NpcTransferState.Aborted);
        Assert.True((await authority.DecideAsync(request)).Authorizes(request));
        Assert.Equal(0, accounts.AbortCalls);
    }

    private static NpcMissionAuthorityRequestV1 AuthorityRequest(FakeCoordinator coordinator, NpcTransferState state) => new()
    {
        TransferId = coordinator.TransferId, SourceInstanceId = "li01-instance", TargetInstanceId = "li02-instance",
        TargetSystemId = "li02", Decision = state
    };

    [Fact]
    public async Task SourceReleaseRequiresCommittedLeaseAndIsIdempotent()
    {
        var coordinator = new FakeCoordinator(TransferState.TargetAccepted, leaseVersion: 0);
        var snapshots = new FakeSnapshotStore();
        var accounts = new FakeAccounts();
        var release = new TransferSourceReleaseService(coordinator, snapshots,
            new TransferStatusService(coordinator, accounts));

        var early = await release.ReleaseAsync(new TransferSourceReleaseRequest
        { TransferId = coordinator.TransferId }, "li01-instance");
        Assert.False(early.Accepted);
        Assert.Equal("transfer_not_committed", early.ReasonCode);
        Assert.Equal(0, coordinator.SourceReleaseCalls);

        coordinator.SetState(TransferState.Committed, leaseVersion: 15);
        accounts.Commit = coordinator.CommitRecord();
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
        Assert.Equal(2, snapshots.DeleteCalls);
    }

    [Fact]
    public async Task SourceReleaseRepairsCoordinatorAfterSqlCommitAndLostAcknowledgement()
    {
        var coordinator = new FakeCoordinator(TransferState.TargetAccepted, leaseVersion: 0);
        var snapshots = new FakeSnapshotStore();
        var release = new TransferSourceReleaseService(coordinator, snapshots,
            new TransferStatusService(coordinator, new FakeAccounts { Commit = coordinator.CommitRecord() }));
        var result = await release.ReleaseAsync(new TransferSourceReleaseRequest
            { TransferId = coordinator.TransferId }, "li01-instance");
        Assert.True(result.Accepted);
        Assert.Equal(1, coordinator.CommitCalls);
        Assert.Equal(1, coordinator.SourceReleaseCalls);
        Assert.Equal(1, snapshots.DeleteCalls);
    }

    [Fact]
    public async Task SourceReleaseCannotDeleteSnapshotWithoutSqlCommitProof()
    {
        var coordinator = new FakeCoordinator(TransferState.Committed, leaseVersion: 15);
        var snapshots = new FakeSnapshotStore();
        var release = new TransferSourceReleaseService(coordinator, snapshots,
            new TransferStatusService(coordinator, new FakeAccounts()));
        var result = await release.ReleaseAsync(new TransferSourceReleaseRequest
            { TransferId = coordinator.TransferId }, "li01-instance");
        Assert.False(result.Accepted);
        Assert.Equal(0, coordinator.CommitCalls);
        Assert.Equal(0, coordinator.SourceReleaseCalls);
        Assert.Equal(0, snapshots.DeleteCalls);
    }

    private sealed class FakeSnapshotStore : ITransferSnapshotStore
    {
        public int DeleteCalls { get; private set; }
        public TransferSnapshotStoreResult ReadResult { get; set; } = new(TransferSnapshotStoreStatus.NotFound);

        public Task<TransferSnapshotStoreResult> StoreAsync(TransferSnapshotMetadata metadata,
            ReadOnlyMemory<byte> snapshot, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TransferSnapshotStoreResult> ReadAsync(Guid transferId,
            CancellationToken cancellationToken = default) => Task.FromResult(ReadResult);

        public Task<bool> DeleteAsync(Guid transferId, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeCoordinator(TransferState state, long leaseVersion) : ICoordinatorTransferClient
    {
        private TransferState state = state;
        private long leaseVersion = leaseVersion;
        public Guid TransferId { get; } = Guid.NewGuid();
        private readonly Guid sessionId = Guid.NewGuid();
        public int SourceReleaseCalls { get; private set; }
        public int CommitCalls { get; private set; }

        public CharacterLeaseTransferCommitRecord CommitRecord() =>
            new(TransferId, sessionId, 73, "li01-instance", "li02-instance", 14, 15);

        private CoordinatorTransferSnapshot Snapshot() => new(TransferId, new TransferPrepareRequest
        {
            TransferId = TransferId,
            SessionId = sessionId,
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
        public Task<CoordinatorTransferStateResult> CommitAsync(Guid transferId, long leaseVersion, CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            Assert.Equal(TransferId, transferId);
            Assert.Equal(15, leaseVersion);
            state = TransferState.Committed;
            this.leaseVersion = leaseVersion;
            return Task.FromResult(new CoordinatorTransferStateResult(HttpStatusCode.OK,
                new CoordinatorTransferOutcome(true, "accepted", state, false), null));
        }
        public Task<CoordinatorTransferStateResult> AbortAsync(TransferAbort request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(TransferId, request.TransferId);
            state = TransferState.Aborted;
            return Task.FromResult(new CoordinatorTransferStateResult(HttpStatusCode.OK,
                new CoordinatorTransferOutcome(true, "aborted", state, false), null));
        }
    }

    private sealed class FakeAccounts : IAccountRepository
    {
        public CharacterLeaseTransferCommitRecord? Commit { get; set; }
        public CharacterTransferDecision? Decision { get; set; }
        public int AbortCalls { get; private set; }
        public Task<CharacterTransferDecision?> FindCharacterTransferDecisionAsync(Guid transferId,
            CancellationToken cancellationToken = default) => Task.FromResult(Decision);
        public Task<CharacterTransferAbortResult> AbortCharacterLeaseTransferAsync(CharacterTransferBinding binding,
            DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            AbortCalls++;
            Decision = new(binding, CharacterTransferDecisionKind.Aborted);
            return Task.FromResult(new CharacterTransferAbortResult(true, "aborted"));
        }
        public int ReadCalls { get; private set; }
        public Task<CharacterLeaseTransferCommitRecord?> FindCharacterLeaseTransferCommitAsync(Guid transferId,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult(Commit);
        }
        public Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash,
            DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
            byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId, long characterId,
            DateTime nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
            long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
            byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
