using System.Net;
using System.Net.Http.Json;
using LancerNexus.Protocol;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class AdminCommandPermissionTests
{
    [Fact]
    public async Task PermissionCheckRequiresCurrentCharacterLeaseAndForwardsServerContext()
    {
        var handler = new PermissionHandler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:AdministrationBaseUrl"] = "https://localhost:9999",
            ["Gateway:AdministrationApiKey"] = new string('s', 32)
        }).Build();
        var accounts = new Accounts("li-01");
        var coordinator = new CoordinatorTransferClient(new HttpClient(),
            new CoordinatorGatewayOptions(null, null, TimeSpan.FromSeconds(1)));
        var gateway = new AdminPermissionRelay(new HttpClient(), config, accounts, coordinator, TimeProvider.System);
        var permissionStore = new AdminPermissionSnapshotRelay(new HttpClient(handler), config);
        var accountId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var allowed = await gateway.CheckFromGameAsync(new(accountId, sessionId, 42, Guid.Empty,
            "command.godmode", "li03"), "li-01", permissionStore, default);

        Assert.True(allowed);
        Assert.Equal(sessionId, accounts.Session);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(accountId, handler.Check!.AccountId);
        Assert.Equal("command.godmode", handler.Check.Permission);
        Assert.Equal("li-01", handler.Check.InstanceId);
        Assert.Equal("li03", handler.Check.SystemId);
    }

    [Fact]
    public async Task ForeignLeaseCannotAuthorizeCommand()
    {
        var handler = new PermissionHandler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:AdministrationBaseUrl"] = "https://localhost:9999",
            ["Gateway:AdministrationApiKey"] = new string('s', 32)
        }).Build();
        var accounts = new Accounts("bw-01");
        var coordinator = new CoordinatorTransferClient(new HttpClient(),
            new CoordinatorGatewayOptions(null, null, TimeSpan.FromSeconds(1)));
        var gateway = new AdminPermissionRelay(new HttpClient(), config, accounts, coordinator, TimeProvider.System);
        var permissionStore = new AdminPermissionSnapshotRelay(new HttpClient(handler), config);

        var allowed = await gateway.CheckFromGameAsync(new(Guid.NewGuid(), Guid.NewGuid(), 42, Guid.Empty,
            "command.godmode", "li03"), "li-01", permissionStore, default);

        Assert.False(allowed);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class PermissionHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public PermissionCheckRequest? Check { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Check = await request.Content!.ReadFromJsonAsync<PermissionCheckRequest>(ct);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed = true }) };
        }
    }

    private sealed record PermissionCheckRequest(Guid AccountId, string Permission, string? InstanceId, string? SystemId);

    private sealed class Accounts(string? instance) : IAccountRepository
    {
        public Guid Session { get; private set; }
        public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId, long characterId,
            DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            Session = sessionId;
            return Task.FromResult(instance is null ? null : new CharacterLeaseRecord(instance, 1, nowUtc.AddMinutes(1)));
        }
        public Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash,
            DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
            byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
            long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
            byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
