using System.Net;
using System.Net.Http.Json;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class GatewayEventRegistrationRelayTests
{
    [Fact]
    public async Task RelayUsesAuthenticatedIdentityAndCurrentLeaseFence()
    {
        var handler = new CaptureHandler();
        var accounts = new Accounts(new CharacterLeaseRecord("li-01", 7, DateTime.UtcNow.AddMinutes(2)));
        var relay = CreateRelay(handler, accounts);
        var accountId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var submission = Submission() with { CharacterId = 48 };

        var decision = await relay.FromRestAsync(accountId, sessionId, "li-event", submission, default);

        Assert.Equal(EventRegistrationStatus.Accepted, decision.Status);
        Assert.Equal(accountId, accounts.AccountId);
        Assert.Equal(sessionId, accounts.SessionId);
        Assert.Equal(submission.CharacterId, accounts.CharacterId);
        Assert.NotNull(handler.Request);
        Assert.Equal(accountId, handler.Request.AccountId);
        Assert.Equal(sessionId, handler.Request.SessionId);
        Assert.Equal(7, handler.Request.LeaseVersion);
        Assert.Equal("li-event", handler.Request.EventId);
        Assert.Equal(submission.RequestId, handler.Request.RequestId);
        Assert.Equal("https://events.example/internal/v1/events/li-event/registrations", handler.Path);
        Assert.Equal("Bearer events-service-test-key-0123456789abcdef", handler.Authorization);
    }

    [Fact]
    public async Task MissingOrExpiredLeaseDeniesRegistrationWithoutForwarding()
    {
        var handler = new CaptureHandler();
        var relay = CreateRelay(handler, new Accounts(null));

        var decision = await relay.FromRestAsync(Guid.NewGuid(), Guid.NewGuid(), "li-event", Submission(), default);

        Assert.Equal(EventRegistrationStatus.Denied, decision.Status);
        Assert.Equal("session_lease_invalid", decision.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingOrNonHttpsEventsConfigurationFailsClosed()
    {
        var handler = new CaptureHandler();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:EventsBaseUrl"] = "http://events.example",
            ["Gateway:EventsApiKey"] = new string('k', 40)
        }).Build();
        var relay = new GatewayEventRegistrationRelay(new HttpClient(handler), configuration,
            new Accounts(new CharacterLeaseRecord("li-01", 2, DateTime.UtcNow.AddMinutes(2))),
            TimeProvider.System);

        var decision = await relay.FromRestAsync(Guid.NewGuid(), Guid.NewGuid(), "li-event", Submission(), default);

        Assert.Equal(EventRegistrationStatus.Unavailable, decision.Status);
        Assert.Equal("events_unavailable", decision.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task InvalidDownstreamResponseFailsClosed()
    {
        var handler = new CaptureHandler { ResponseStatus = HttpStatusCode.Conflict };
        var relay = CreateRelay(handler, new Accounts(new CharacterLeaseRecord("li-01", 2,
            DateTime.UtcNow.AddMinutes(2))));

        var decision = await relay.FromRestAsync(Guid.NewGuid(), Guid.NewGuid(), "li-event", Submission(), default);

        Assert.Equal(EventRegistrationStatus.Unavailable, decision.Status);
        Assert.Equal("events_invalid_response", decision.ErrorCode);
    }

    private static GatewayEventRegistrationRelay CreateRelay(CaptureHandler handler, IAccountRepository accounts)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:EventsBaseUrl"] = "https://events.example/",
            ["Gateway:EventsApiKey"] = "events-service-test-key-0123456789abcdef"
        }).Build();
        return new GatewayEventRegistrationRelay(new HttpClient(handler), configuration, accounts,
            TimeProvider.System);
    }

    private static GatewayEventRegistrationSubmission Submission() => new(
        Guid.NewGuid(), Guid.NewGuid(), 41, "registration-idempotency");

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public EventRegistrationRequestV1? Request { get; private set; }
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }
        public int Calls { get; private set; }
        public HttpStatusCode ResponseStatus { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Request = await request.Content!.ReadFromJsonAsync<EventRegistrationRequestV1>(cancellationToken);
            var decision = new EventRegistrationDecisionV1
            {
                RequestId = Request!.RequestId,
                CorrelationId = Request.CorrelationId,
                RegistrationId = Guid.NewGuid(),
                EventRevision = 3,
                Status = EventRegistrationStatus.Accepted
            };
            if (ResponseStatus == HttpStatusCode.Conflict)
                decision = decision with { RequestId = Guid.NewGuid() };
            return new HttpResponseMessage(ResponseStatus) { Content = JsonContent.Create(decision) };
        }
    }

    private sealed class Accounts(CharacterLeaseRecord? lease) : IAccountRepository
    {
        public Guid AccountId { get; private set; }
        public Guid SessionId { get; private set; }
        public long CharacterId { get; private set; }

        public Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountRecord?>(null);
        public Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash,
            DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
            byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionRecord?>(null);
        public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CharacterRecord>>([]);
        public Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId,
            CancellationToken cancellationToken = default) => Task.FromResult<CharacterRecord?>(null);
        public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId,
            long characterId, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            AccountId = accountId;
            SessionId = sessionId;
            CharacterId = characterId;
            return Task.FromResult(lease);
        }
        public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId,
            Guid sessionId, long characterId, string sourceInstanceId, string targetInstanceId,
            long expectedLeaseVersion, byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
