using System.Net;
using System.Security.Cryptography;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class TransferAcceptanceServiceTests
{
    [Fact]
    public async Task Accept_FencesMysqlLeaseThenCommitsCoordinatorTransfer()
    {
        var fixture = new Fixture();

        var result = await fixture.AcceptAsync();

        Assert.True(result.Accepted);
        Assert.Equal(15, result.LeaseVersion);
        Assert.Equal(TransferState.Committed, fixture.Coordinator.State);
        Assert.Equal(1, fixture.Accounts.CommitCalls);
        Assert.Equal(14, fixture.Accounts.ExpectedLeaseVersion);
        Assert.Equal("li02-instance", fixture.Accounts.TargetInstanceId);
    }

    [Fact]
    public async Task Accept_RejectsDifferentAuthenticatedInstanceBeforeChangingLease()
    {
        var fixture = new Fixture();

        var result = await fixture.AcceptAsync("li03-instance");

        Assert.False(result.Accepted);
        Assert.Equal("transfer_ticket_target_mismatch", result.ReasonCode);
        Assert.Equal(0, fixture.Accounts.CommitCalls);
        Assert.Equal(TransferState.SourceFrozen, fixture.Coordinator.State);
    }

    [Fact]
    public async Task Accept_RetryCompletesCoordinatorAfterMysqlCommittedButCoordinatorWasUnavailable()
    {
        var fixture = new Fixture { CoordinatorCommitUnavailableOnce = true };

        var first = await fixture.AcceptAsync();
        var retry = await fixture.AcceptAsync();

        Assert.False(first.Accepted);
        Assert.Equal(TransferAcceptanceFailure.CoordinatorUnavailable, first.Failure);
        Assert.True(retry.Accepted);
        Assert.Equal(2, fixture.Accounts.CommitCalls);
        Assert.Equal(TransferState.Committed, fixture.Coordinator.State);
    }

    [Fact]
    public async Task Accept_AllowsAuthenticatedRecoveryAfterTicketExpiryOnceTransferWasFrozen()
    {
        var fixture = new Fixture(expiredTicket: true, initialState: TransferState.TargetAccepted);

        var result = await fixture.AcceptAsync();

        Assert.True(result.Accepted);
        Assert.Equal(15, result.LeaseVersion);
        Assert.Equal(TransferState.Committed, fixture.Coordinator.State);
    }

    private sealed class Fixture
    {
        private readonly DateTime now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        private readonly TransferTicketCodec tickets = new(new TransferTicketOptions(
            new string('t', 32), "gateway-transfer-01", "game-server-transfer"));
        private readonly string ticket;
        private readonly TransferTargetAcceptanceRequest request;
        private readonly CoordinatorTransferSnapshot snapshot;

        public Fixture(bool expiredTicket = false, TransferState initialState = TransferState.SourceFrozen)
        {
            var claims = new TransferTicketClaims
            {
                TransferId = Guid.NewGuid(),
                SessionId = Guid.NewGuid(),
                AccountId = Guid.NewGuid(),
                CharacterId = 73,
                SourceInstanceId = "li01-instance",
                TargetInstanceId = "li02-instance",
                TargetSystemId = "li02",
                LeaseVersion = 14,
                IssuedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(1),
                Nonce = "nonce-accept-01",
                Audience = "game-server-transfer",
                KeyId = "gateway-transfer-01"
            };
            ticket = tickets.Issue(claims);
            request = new TransferTargetAcceptanceRequest
            {
                Ticket = ticket,
                TargetLeaseToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_')
            };
            var transferRequest = new TransferPrepareRequest
            {
                TransferId = claims.TransferId,
                SessionId = claims.SessionId,
                CharacterId = claims.CharacterId,
                SourceInstanceId = claims.SourceInstanceId,
                TargetInstanceId = claims.TargetInstanceId,
                TargetSystemId = claims.TargetSystemId,
                ExpiresUtc = claims.ExpiresAtUtc,
                IdempotencyKey = "acceptance-test"
            };
            snapshot = new CoordinatorTransferSnapshot(claims.TransferId, transferRequest,
                initialState, claims.ExpiresAtUtc, 0);
            Coordinator = new FakeCoordinator(snapshot);
            Accounts = new FakeAccounts();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Gateway:CharacterLeaseLifetimeSeconds"] = "900" }).Build();
            Service = new TransferAcceptanceService(tickets, Coordinator, Accounts,
                new FixedTimeProvider(now.AddSeconds(expiredTicket ? 180 : 1)), configuration);
        }

        public FakeCoordinator Coordinator { get; }
        public FakeAccounts Accounts { get; }
        public TransferAcceptanceService Service { get; }
        public bool CoordinatorCommitUnavailableOnce { set => Coordinator.CommitUnavailableOnce = value; }

        public Task<TransferAcceptanceResult> AcceptAsync(string target = "li02-instance") =>
            Service.AcceptAsync(request, target);
    }

    private sealed class FakeCoordinator(CoordinatorTransferSnapshot snapshot) : ICoordinatorTransferClient
    {
        public TransferState State { get; private set; } = snapshot.State;
        public bool CommitUnavailableOnce { private get; set; }

        public Task<CoordinatorTransferLookupResult> GetTransferAsync(Guid transferId,
            CancellationToken cancellationToken = default) => Task.FromResult(new CoordinatorTransferLookupResult(
            HttpStatusCode.OK, transferId == snapshot.TransferId ? snapshot with
            {
                State = State,
                LeaseVersion = State == TransferState.Committed ? 15 : 0
            } : null, null));

        public Task<CoordinatorTransferStateResult> MarkTargetAcceptedAsync(Guid transferId,
            CancellationToken cancellationToken = default)
        {
            State = TransferState.TargetAccepted;
            return Task.FromResult(new CoordinatorTransferStateResult(HttpStatusCode.OK,
                new CoordinatorTransferOutcome(true, "accepted", State, false), null));
        }

        public Task<CoordinatorTransferStateResult> CommitAsync(Guid transferId, long leaseVersion,
            CancellationToken cancellationToken = default)
        {
            if (CommitUnavailableOnce)
            {
                CommitUnavailableOnce = false;
                return Task.FromResult(new CoordinatorTransferStateResult(null, null, "coordinator_unavailable"));
            }
            State = TransferState.Committed;
            return Task.FromResult(new CoordinatorTransferStateResult(HttpStatusCode.OK,
                new CoordinatorTransferOutcome(true, "accepted", State, false), null));
        }

        public Task<CoordinatorTransferCallResult> PrepareAsync(TransferPrepareRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkSourceFrozenAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkSourceReleasedAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> AbortAsync(TransferAbort request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeAccounts : IAccountRepository
    {
        private byte[]? committedTokenHash;
        public int CommitCalls { get; private set; }
        public long ExpectedLeaseVersion { get; private set; }
        public string? TargetInstanceId { get; private set; }

        public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
            long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
            byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
            CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            ExpectedLeaseVersion = expectedLeaseVersion;
            TargetInstanceId = targetInstanceId;
            if (committedTokenHash is not null && !CryptographicOperations.FixedTimeEquals(committedTokenHash, targetLeaseTokenHash))
                return Task.FromResult(new CharacterLeaseTransferResult(false, "transfer_id_conflict", null));
            committedTokenHash ??= targetLeaseTokenHash;
            return Task.FromResult(new CharacterLeaseTransferResult(true,
                CommitCalls == 1 ? "committed" : "duplicate", 15));
        }

        public Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash, DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash, byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId, long characterId, DateTime nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
