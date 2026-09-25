using LancerNexus.Gateway;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task Login_VerifiesBcryptAndPersistsSessionBeforeReturningToken()
    {
        var now = DateTime.UtcNow;
        var account = new AccountRecord(
            Guid.NewGuid(),
            "pilot@example.net",
            BCrypt.Net.BCrypt.HashPassword("secret"),
            "active");
        var repository = new FakeAccountRepository(account);
        var options = new SessionTokenOptions(new string('s', 32), "gateway-key-01", "game-server", TimeSpan.FromMinutes(10));
        var service = new GatewayAuthenticationService(
            repository,
            new BcryptPasswordVerifier(),
            new SessionTokenCodec(options),
            options,
            new FixedTimeProvider(now));

        var result = await service.LoginAsync(new LoginRequest(" Pilot@Example.net ", "secret"));

        Assert.True(result.Succeeded);
        Assert.NotNull(repository.CreatedSession);
        Assert.Equal(result.Response!.SessionId, repository.CreatedSession!.Value.SessionId);
        Assert.True(new SessionTokenCodec(options).Validate(result.Response.AccessToken, now).Accepted);
    }

    [Fact]
    public async Task Login_RejectsWrongPasswordWithoutCreatingSession()
    {
        var account = new AccountRecord(Guid.NewGuid(), "pilot@example.net",
            BCrypt.Net.BCrypt.HashPassword("secret"), "active");
        var repository = new FakeAccountRepository(account);
        var options = new SessionTokenOptions(new string('s', 32), "gateway-key-01", "game-server", TimeSpan.FromMinutes(10));
        var service = new GatewayAuthenticationService(
            repository,
            new BcryptPasswordVerifier(),
            new SessionTokenCodec(options),
            options,
            new FixedTimeProvider(DateTime.UtcNow));

        var result = await service.LoginAsync(new LoginRequest("pilot@example.net", "wrong"));

        Assert.False(result.Succeeded);
        Assert.Equal(LoginFailure.InvalidCredentials, result.Failure);
        Assert.Null(repository.CreatedSession);
    }

    [Fact]
    public async Task Refresh_RotatesRefreshTokenAndIssuesNewAccessToken()
    {
        var now = DateTime.UtcNow;
        var account = new AccountRecord(Guid.NewGuid(), "pilot@example.net",
            BCrypt.Net.BCrypt.HashPassword("secret"), "active");
        var repository = new FakeAccountRepository(account);
        var options = new SessionTokenOptions(new string('s', 32), "gateway-key-01", "game-server", TimeSpan.FromMinutes(10));
        var service = new GatewayAuthenticationService(
            repository,
            new BcryptPasswordVerifier(),
            new SessionTokenCodec(options),
            options,
            new FixedTimeProvider(now));

        var login = await service.LoginAsync(new LoginRequest("pilot@example.net", "secret"));
        var refresh = await service.RefreshAsync(new RefreshRequest(
            login.Response!.SessionId,
            login.Response.RefreshToken));

        Assert.True(refresh.Succeeded);
        Assert.NotEqual(login.Response.RefreshToken, refresh.Response!.RefreshToken);
        Assert.True(new SessionTokenCodec(options).Validate(refresh.Response.AccessToken, now).Accepted);
        Assert.Equal(refresh.Response.SessionId, login.Response.SessionId);
    }

    private sealed class FakeAccountRepository(AccountRecord? account) : IAccountRepository
    {
        public (Guid SessionId, Guid AccountId)? CreatedSession { get; private set; }

        public Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult(account);

        public Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash,
            DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
        {
            CreatedSession = (sessionId, accountId);
            return Task.CompletedTask;
        }

        public Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
            byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionRecord?>(new(account!.AccountId, nowUtc.AddMinutes(10)));

        public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CharacterRecord>>([]);

        public Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterRecord?>(null);

        public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId,
            long characterId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterLeaseRecord?>(null);

        public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
            long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
            byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTime value) : TimeProvider
    {
        private readonly DateTimeOffset current = new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => current;
    }
}
