using System.Net;
using System.Net.Http.Json;
using LancerNexus.Protocol;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class AdminActionRelayTests
{
    [Fact]
    public async Task RestRelayAttestsActorAndSessionAndNeverAcceptsClientStepUpOrTargetContext()
    {
        var handler = new Handler();
        var relay = Relay(handler, new Accounts(null));
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();
        var action = Action() with { ActorAccountId = Guid.NewGuid(), Source = "automation" };

        var result = await relay.FromRestAsync(actor, session, action, default);

        Assert.Equal(AdminActionStatus.Pending, result.Status);
        Assert.NotNull(handler.Body);
        Assert.Equal(actor, handler.Body.Action.ActorAccountId);
        Assert.Equal("rest", handler.Body.Action.Source);
        Assert.Equal(session, handler.Body.GatewaySessionId);
        Assert.Null(handler.Body.TargetContext);
        Assert.False(handler.Body.StepUpVerified);
        Assert.EndsWith("/api/v1/admin/actions", handler.Path);
    }

    [Fact]
    public async Task GameRelayRequiresCurrentCharacterLeaseBeforeForwarding()
    {
        var handler = new Handler();
        var relay = Relay(handler, new Accounts("ku-01"));
        var request = new GameAdminActionRequest(Guid.NewGuid(), Guid.NewGuid(), 41, Guid.Empty, Action());

        var result = await relay.FromGameAsync(request, "li-01", default);

        Assert.Equal(AdminActionStatus.Denied, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GameRelayAttestsActorAndSourceFromGameLease()
    {
        var handler = new Handler();
        var relay = Relay(handler, new Accounts("li-01"));
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();
        var request = new GameAdminActionRequest(actor, session, 41, Guid.Empty,
            Action() with { ActorAccountId = Guid.NewGuid(), Source = "rest" });

        var result = await relay.FromGameAsync(request, "li-01", default);

        Assert.Equal(AdminActionStatus.Pending, result.Status);
        Assert.Equal(actor, handler.Body!.Action.ActorAccountId);
        Assert.Equal("ingame:li-01", handler.Body.Action.Source);
        Assert.Equal(session, handler.Body.GatewaySessionId);
    }

    [Fact]
    public async Task MismatchedHttpStatusAndTypedResultFailClosed()
    {
        var handler = new Handler { HttpStatus = HttpStatusCode.OK };
        var result = await Relay(handler, new Accounts(null)).FromRestAsync(Guid.NewGuid(), Guid.NewGuid(), Action(), default);

        Assert.Equal(AdminActionStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task PlayerActionUsesActiveLeaseAndCoordinatorContext()
    {
        var handler = new Handler();
        var accounts = new Accounts(null, new CharacterLeaseRecord("ku-01", 3, DateTime.UtcNow.AddMinutes(2)));
        var coordinator = new RegistryCoordinator(new CoordinatorRegistryInstance("ku-01", "ku01", true,
            false, true, true));

        var result = await Relay(handler, accounts, coordinator).FromRestAsync(Guid.NewGuid(), Guid.NewGuid(),
            PlayerAction(), default);

        Assert.Equal(AdminActionStatus.Pending, result.Status);
        Assert.Equal("411", handler.Body!.TargetContext!.TargetId);
        Assert.Equal("ku-01", handler.Body.TargetContext.CurrentInstanceId);
        Assert.Equal("ku01", handler.Body.TargetContext.CurrentSystemId);
        Assert.False(handler.Body.StepUpVerified);
    }

    [Fact]
    public async Task PlayerActionFailsClosedWhenLeaseOrCoordinatorInstanceIsMissing()
    {
        var handler = new Handler();
        var coordinator = new RegistryCoordinator();
        var result = await Relay(handler, new Accounts(null, new CharacterLeaseRecord("ku-01", 3,
            DateTime.UtcNow.AddMinutes(2))), coordinator).FromRestAsync(Guid.NewGuid(), Guid.NewGuid(),
            PlayerAction(), default);

        Assert.Equal(AdminActionStatus.Unavailable, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MoveActionRequiresReadyNonDrainingDestination()
    {
        var handler = new Handler();
        var coordinator = new RegistryCoordinator(
            new CoordinatorRegistryInstance("ku-01", "ku01", true, false, true, true),
            new CoordinatorRegistryInstance("li-01", "li01", true, true, true, true));
        var action = PlayerAction() with { Kind = AdminActionKind.Move, TargetInstanceId = "li-01" };

        var result = await Relay(handler, new Accounts(null, new CharacterLeaseRecord("ku-01", 3,
            DateTime.UtcNow.AddMinutes(2))), coordinator).FromRestAsync(Guid.NewGuid(), Guid.NewGuid(), action, default);

        Assert.Equal(AdminActionStatus.Unavailable, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    private static AdminActionRequestV1 Action() => new()
    {
        CommandId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        ActorAccountId = Guid.NewGuid(),
        Source = "rest",
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        Kind = AdminActionKind.Message,
        TargetKind = AdminActionTargetKind.All,
        Message = "Service notice"
    };

    private static AdminActionRequestV1 PlayerAction() => Action() with
    {
        Kind = AdminActionKind.Kick,
        TargetKind = AdminActionTargetKind.Player,
        TargetId = "411",
        Message = null
    };

    private static AdminActionRelay Relay(Handler handler, IAccountRepository accounts,
        ICoordinatorTransferClient? coordinator = null) => new(
        new HttpClient(handler), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:AdministrationBaseUrl"] = "https://localhost:9999",
            ["Gateway:AdministrationApiKey"] = new string('x', 32)
        }).Build(), accounts,
        coordinator ?? new CoordinatorTransferClient(new HttpClient(), new CoordinatorGatewayOptions(null, null, TimeSpan.FromSeconds(1))),
        TimeProvider.System);

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode HttpStatus = HttpStatusCode.Accepted;
        public GatewayAuthorizedAdminActionSubmission? Body;
        public string? Path;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadFromJsonAsync<GatewayAuthorizedAdminActionSubmission>(ct);
            return new HttpResponseMessage(HttpStatus)
            {
                Content = JsonContent.Create(new AdminActionResultV1
                {
                    CommandId = Body!.Action.CommandId,
                    CorrelationId = Body.Action.CorrelationId,
                    Status = AdminActionStatus.Pending,
                    OperationId = Guid.NewGuid(),
                    Sequence = 5
                })
            };
        }
    }

    private sealed class Accounts(string? activeInstance, CharacterLeaseRecord? targetLease = null) : IAccountRepository
    {
        public Task<CharacterLeaseRecord?> FindActiveCharacterTargetLeaseAsync(long characterId, DateTime nowUtc,
            CancellationToken cancellationToken = default) => Task.FromResult(targetLease);
        public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId, long characterId,
            DateTime nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(activeInstance is null ? null : new CharacterLeaseRecord(activeInstance, 1, nowUtc.AddMinutes(1)));
        public Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash,
            DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash, byte[] newRefreshTokenHash,
            DateTime nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
            long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
            byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RegistryCoordinator(params CoordinatorRegistryInstance[] instances) : ICoordinatorTransferClient
    {
        public Task<CoordinatorRegistryLookupResult> GetRegistryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CoordinatorRegistryLookupResult(HttpStatusCode.OK, instances, null));
        public Task<CoordinatorTransferLookupResult> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferCallResult> PrepareAsync(TransferPrepareRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkSourceFrozenAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkTargetAcceptedAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> CommitAsync(Guid transferId, long leaseVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> MarkSourceReleasedAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoordinatorTransferStateResult> AbortAsync(TransferAbort request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
