using System.Net;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class TransferTicketAdmissionServiceTests
{
    [Fact]
    public async Task Verify_RejectsTicketForDifferentActualInstance()
    {
        var now = DateTime.UtcNow;
        var f = new Fixture(now, TransferState.SourceFrozen);
        var result = await f.Service.VerifyAsync(new TransferTicketVerificationRequest
        {
            Ticket = f.Ticket,
            TargetInstanceId = "li03-instance"
        });

        Assert.False(result.Accepted);
        Assert.Equal("transfer_ticket_target_mismatch", result.ReasonCode);
        Assert.Equal(0, f.Replay.ConsumeCalls);
    }

    [Fact]
    public async Task Verify_RequiresSourceFrozenBeforeConsumingNonce()
    {
        var f = new Fixture(DateTime.UtcNow, TransferState.Prepared);
        var result = await f.VerifyAsync();

        Assert.False(result.Accepted);
        Assert.Equal("transfer_not_ready", result.ReasonCode);
        Assert.Equal(0, f.Replay.ConsumeCalls);
    }

    [Fact]
    public async Task Verify_AcceptsFrozenTransferOnce()
    {
        var f = new Fixture(DateTime.UtcNow, TransferState.SourceFrozen);

        var accepted = await f.VerifyAsync();
        var replayed = await f.VerifyAsync();

        Assert.True(accepted.Accepted);
        Assert.Equal("li02-instance", accepted.Claims!.TargetInstanceId);
        Assert.False(replayed.Accepted);
        Assert.Equal("transfer_ticket_replayed", replayed.ReasonCode);
        Assert.Equal(2, f.Replay.ConsumeCalls);
    }

    [Fact]
    public async Task Verify_RecoversAnExpiredTicketOnlyForAnAlreadyFrozenTransfer()
    {
        var f = new Fixture(DateTime.UtcNow, TransferState.SourceFrozen, expiredTicket: true);

        var accepted = await f.VerifyAsync();
        var replayed = await f.VerifyAsync();

        Assert.True(accepted.Accepted);
        Assert.False(replayed.Accepted);
        Assert.Equal("transfer_ticket_replayed", replayed.ReasonCode);
    }

    private sealed class Fixture
    {
        private readonly TransferTicketClaims claims;
        private readonly TransferTicketCodec codec = new(new TransferTicketOptions(
            new string('t', 32), "gateway-transfer-01", "game-server-transfer"));

        public Fixture(DateTime now, TransferState state, bool expiredTicket = false)
        {
            claims = new TransferTicketClaims
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
                Nonce = "nonce-01",
                Audience = "game-server-transfer",
                KeyId = "gateway-transfer-01"
            };
            Ticket = codec.Issue(claims);
            var request = new TransferPrepareRequest
            {
                TransferId = claims.TransferId,
                SessionId = claims.SessionId,
                CharacterId = claims.CharacterId,
                SourceInstanceId = claims.SourceInstanceId,
                TargetInstanceId = claims.TargetInstanceId,
                TargetSystemId = claims.TargetSystemId,
                ExpiresUtc = claims.ExpiresAtUtc,
                IdempotencyKey = "transfer-test"
            };
            Coordinator = new StubCoordinator(new CoordinatorTransferSnapshot(
                claims.TransferId, request, state, claims.ExpiresAtUtc, claims.LeaseVersion));
            Replay = new StubReplayStore();
            Service = new TransferTicketAdmissionService(codec, Coordinator, Replay,
                new FixedTimeProvider(now.AddSeconds(expiredTicket ? 180 : 1)));
        }

        public string Ticket { get; }
        public StubCoordinator Coordinator { get; }
        public StubReplayStore Replay { get; }
        public TransferTicketAdmissionService Service { get; }
        public Task<TransferTicketAdmissionResult> VerifyAsync() => Service.VerifyAsync(
            new TransferTicketVerificationRequest { Ticket = Ticket, TargetInstanceId = claims.TargetInstanceId });
    }

    private sealed class StubCoordinator(CoordinatorTransferSnapshot snapshot) : ICoordinatorTransferClient
    {
        public Task<CoordinatorTransferLookupResult> GetTransferAsync(Guid transferId,
            CancellationToken cancellationToken = default) => Task.FromResult(new CoordinatorTransferLookupResult(
                HttpStatusCode.OK, transferId == snapshot.TransferId ? snapshot : null, null));
        public Task<CoordinatorTransferCallResult> PrepareAsync(TransferPrepareRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkSourceFrozenAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkTargetAcceptedAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> CommitAsync(Guid transferId, long leaseVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkSourceReleasedAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> AbortAsync(TransferAbort request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubReplayStore : ITransferTicketReplayStore
    {
        private bool consumed;
        public int ConsumeCalls { get; private set; }
        public Task<bool> TryConsumeTransferAsync(string nonce, DateTime expiresUtc, CancellationToken cancellationToken)
        {
            ConsumeCalls++;
            var available = !consumed;
            consumed = true;
            return Task.FromResult(available);
        }
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(now, DateTimeKind.Utc));
    }
}
