using System.Net;
using System.Net.Http.Json;
using LancerNexus.Protocol;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class AdminQueryRelayTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("ku-01")]
    public async Task MissingOrForeignLeaseCannotReachAdministration(string? instance)
    {
        var handler = new Handler();
        var accounts = new Accounts(instance);
        var relay = Relay(handler, accounts);
        var response = await relay.FromGameAsync(Request(), "li-01", default);
        Assert.Equal("denied", response.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GatewayAttestsAccountAndInstanceFromCheckedLease()
    {
        var handler = new Handler();
        var request = Request();
        var accounts = new Accounts("li-01");
        var result = await Relay(handler, accounts).FromGameAsync(request, "li-01", default);
        Assert.Equal("ok", result.Status);
        Assert.Equal(request.AccountId, handler.Body!.AccountId);
        Assert.Equal("ingame:li-01", handler.Body.Source);
        Assert.Equal(request.SessionId, accounts.Session);
    }

    [Fact]
    public async Task MismatchedResponseCorrelationIsUnavailable()
    {
        var handler = new Handler { WrongCorrelation = true };
        var result = await Relay(handler, new Accounts("li-01")).FromGameAsync(Request(), "li-01", default);
        Assert.Equal("unavailable", result.Status);
    }

    [Theory]
    [InlineData(TransferState.SourceFrozen, "denied")]
    [InlineData(TransferState.SourceReleased, "ok")]
    public async Task TransferredLoginMustResolveCommittedSessionAndStillCheckLease(TransferState state, string expected)
    {
        var original = Request();
        var transferId = Guid.NewGuid();
        var session = Guid.NewGuid();
        var snapshot = new CoordinatorTransferSnapshot(transferId, new TransferPrepareRequest {
            TransferId = transferId, SessionId = session, CharacterId = original.CharacterId,
            TargetInstanceId = "li-01", SourceInstanceId = "ku-01" }, state, DateTime.UtcNow, 2);
        var coordinator = new CoordinatorTransferClient(new HttpClient(new TransferHandler(snapshot)),
            new CoordinatorGatewayOptions(new Uri("https://coordinator.example"), new string('k', 32), TimeSpan.FromSeconds(1)));
        var accounts = new Accounts("li-01");
        var result = await Relay(new Handler(), accounts, coordinator).FromGameAsync(new GameAdminQueryRequest {
            AccountId = original.AccountId, CharacterId = original.CharacterId, TransferId = transferId, Query = original.Query },
            "li-01", default);
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == "ok" ? session : Guid.Empty, accounts.Session);
    }

    private static GameAdminQueryRequest Request() => new() { AccountId = Guid.NewGuid(), SessionId = Guid.NewGuid(),
        CharacterId = 1, Query = new AdminQuery { CorrelationId = Guid.NewGuid(), IdempotencyKey = "test", Kind = AdminQueryKind.Status } };
    private static AdminQueryRelay Relay(Handler handler, Accounts accounts, ICoordinatorTransferClient? coordinator = null) => new(new HttpClient(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Gateway:AdministrationBaseUrl"] = "https://localhost:9999",
            ["Gateway:AdministrationApiKey"] = new string('x', 32) }).Build(), accounts,
        coordinator ?? new CoordinatorTransferClient(new HttpClient(), new CoordinatorGatewayOptions(null, null, TimeSpan.FromSeconds(1))), TimeProvider.System);
    private sealed class TransferHandler(CoordinatorTransferSnapshot snapshot) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) });
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public bool WrongCorrelation;
        public AuthorizedAdminQueryRequest? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Body = await request.Content!.ReadFromJsonAsync<AuthorizedAdminQueryRequest>(ct);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AdminQueryResponse {
                CorrelationId = WrongCorrelation ? Guid.NewGuid() : Body!.Query.CorrelationId, Status = "ok", Lines = ["Cluster"] }) };
        }
    }
    private sealed class Accounts(string? instance) : IAccountRepository
    {
        public Guid Session;
        public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId, long characterId,
            DateTime nowUtc, CancellationToken cancellationToken = default)
        { Session = sessionId; return Task.FromResult(instance is null ? null : new CharacterLeaseRecord(instance, 1, nowUtc.AddMinutes(1))); }
        public Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash, DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash, byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
            long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
            byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
