using System.Net;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class TransferInitiationServiceTests
{
    [Fact]
    public async Task Start_UsesDatabaseSourceLeaseAndReturnsBoundTicket()
    {
        var now = DateTime.UtcNow;
        var request = Request(now);
        var session = Session(request.SessionId);
        var lease = new CharacterLeaseRecord("li01-instance", 14, now.AddMinutes(4));
        var accounts = new StubAccountRepository(lease);
        var coordinator = new StubCoordinatorTransferClient(accepted: true);
        var options = TicketOptions();
        var service = CreateService(accounts, coordinator, options, now);

        var result = await service.StartAsync(request, session);

        Assert.True(result.Succeeded);
        Assert.Equal(lease.InstanceId, coordinator.LastPrepare!.SourceInstanceId);
        Assert.Equal(request.TargetInstanceId, coordinator.LastPrepare.TargetInstanceId);
        Assert.Equal(lease.LeaseVersion, result.Response!.LeaseVersion);
        var ticket = new TransferTicketCodec(options).Validate(result.Response.Prepared.TransferTicket!, now.AddSeconds(1));
        Assert.True(ticket.Accepted);
        Assert.Equal(request.TransferId, ticket.Claims!.TransferId);
        Assert.Equal(session.AccountId, ticket.Claims.AccountId);
        Assert.Equal(lease.InstanceId, ticket.Claims.SourceInstanceId);
        Assert.Equal(request.TargetInstanceId, ticket.Claims.TargetInstanceId);
        Assert.Equal(lease.LeaseVersion, ticket.Claims.LeaseVersion);
    }

    [Fact]
    public async Task Start_RejectsMissingSourceLeaseWithoutCallingCoordinator()
    {
        var now = DateTime.UtcNow;
        var request = Request(now);
        var coordinator = new StubCoordinatorTransferClient(accepted: true);
        var service = CreateService(new StubAccountRepository((CharacterLeaseRecord?)null), coordinator, TicketOptions(), now);

        var result = await service.StartAsync(request, Session(request.SessionId));

        Assert.False(result.Succeeded);
        Assert.Equal(TransferInitiationFailure.ActiveLeaseUnavailable, result.Failure);
        Assert.Null(coordinator.LastPrepare);
    }

    [Fact]
    public async Task Start_RejectsSessionMismatchBeforeReadingLease()
    {
        var now = DateTime.UtcNow;
        var request = Request(now);
        var accounts = new StubAccountRepository(new CharacterLeaseRecord("li01-instance", 14, now.AddMinutes(4)));
        var coordinator = new StubCoordinatorTransferClient(accepted: true);
        var service = CreateService(accounts, coordinator, TicketOptions(), now);

        var result = await service.StartAsync(request, Session(Guid.NewGuid()));

        Assert.Equal(TransferInitiationFailure.SessionMismatch, result.Failure);
        Assert.False(accounts.LeaseRead);
        Assert.Null(coordinator.LastPrepare);
    }

    [Fact]
    public async Task Start_RejectsCoordinatorDenialWithoutIssuingTicket()
    {
        var now = DateTime.UtcNow;
        var request = Request(now);
        var coordinator = new StubCoordinatorTransferClient(accepted: false);
        var service = CreateService(
            new StubAccountRepository(new CharacterLeaseRecord("li01-instance", 14, now.AddMinutes(4))),
            coordinator, TicketOptions(), now);

        var result = await service.StartAsync(request, Session(request.SessionId));

        Assert.Equal(TransferInitiationFailure.TargetRejected, result.Failure);
        Assert.Equal("target_instance_unavailable", result.ReasonCode);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task Start_AbortsReservationIfSourceLeaseChangesDuringPrepare()
    {
        var now = DateTime.UtcNow;
        var request = Request(now);
        var accounts = new StubAccountRepository(
            new CharacterLeaseRecord("li01-instance", 14, now.AddMinutes(4)),
            new CharacterLeaseRecord("li01-instance", 15, now.AddMinutes(4)));
        var coordinator = new StubCoordinatorTransferClient(accepted: true);
        var service = CreateService(accounts, coordinator, TicketOptions(), now);

        var result = await service.StartAsync(request, Session(request.SessionId));

        Assert.Equal(TransferInitiationFailure.ActiveLeaseUnavailable, result.Failure);
        Assert.Equal("source_lease_changed", result.ReasonCode);
        Assert.Equal(2, accounts.LeaseReadCount);
        Assert.Equal("source_lease_changed", coordinator.LastAbort!.ReasonCode);
        Assert.Null(result.Response);
    }

    private static TransferInitiationService CreateService(
        IAccountRepository accounts, ICoordinatorTransferClient coordinator, TransferTicketOptions ticketOptions,
        DateTime now) => new(accounts, coordinator, new TransferTicketCodec(ticketOptions), ticketOptions,
        new FixedTimeProvider(now));

    private static TransferTicketOptions TicketOptions() => new(
        new string('t', 32), "transfer-key", "game-server-transfer");

    private static TransferStartRequest Request(DateTime now) => new()
    {
        TransferId = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        CharacterId = 73,
        TargetInstanceId = "li02-instance",
        TargetSystemId = "li02",
        ExpiresUtc = now.AddMinutes(1),
        IdempotencyKey = "transfer-73-li02"
    };

    private static SessionTokenClaims Session(Guid sessionId) => new()
    {
        SessionId = sessionId,
        AccountId = Guid.NewGuid(),
        Audience = "game-server",
        IssuedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
        Nonce = "session-nonce",
        KeyId = "session-key"
    };

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(now, DateTimeKind.Utc));
    }

    private sealed class StubAccountRepository(params CharacterLeaseRecord?[] leases) : IAccountRepository
    {
        private int leaseReadCount;
        public bool LeaseRead { get; private set; }
        public int LeaseReadCount => leaseReadCount;

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
        public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId,
            long characterId, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            LeaseRead = true;
            var index = Math.Min(leaseReadCount++, leases.Length - 1);
            return Task.FromResult(leases[index]);
        }
        public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
            long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
            byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubCoordinatorTransferClient(bool accepted) : ICoordinatorTransferClient
    {
        public TransferPrepareRequest? LastPrepare { get; private set; }
        public TransferAbort? LastAbort { get; private set; }

        public Task<CoordinatorTransferLookupResult> GetTransferAsync(Guid transferId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CoordinatorTransferCallResult> PrepareAsync(TransferPrepareRequest request,
            CancellationToken cancellationToken = default)
        {
            LastPrepare = request;
            return Task.FromResult(new CoordinatorTransferCallResult(HttpStatusCode.OK,
                new CoordinatorTransferEnvelope(new TransferPrepared
                {
                    TransferId = request.TransferId,
                    Accepted = accepted,
                    ExpiresUtc = request.ExpiresUtc,
                    ReasonCode = accepted ? "prepared" : "target_instance_unavailable"
                }, TransferState.Prepared, accepted ? "10.0.0.2:2300" : null, false), null));
        }

        public Task<CoordinatorTransferStateResult> MarkSourceFrozenAsync(Guid transferId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkTargetAcceptedAsync(Guid transferId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> CommitAsync(Guid transferId, long leaseVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkSourceReleasedAsync(Guid transferId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> AbortAsync(TransferAbort request,
            CancellationToken cancellationToken = default)
        {
            LastAbort = request;
            return Task.FromResult(new CoordinatorTransferStateResult(HttpStatusCode.OK,
                new CoordinatorTransferOutcome(true, "accepted", TransferState.Aborted, false), null));
        }
    }
}
