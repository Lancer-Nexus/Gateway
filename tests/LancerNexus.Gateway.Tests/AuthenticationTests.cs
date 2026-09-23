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

    private sealed class FakeAccountRepository(AccountRecord? account) : IAccountRepository
    {
        public (Guid SessionId, Guid AccountId)? CreatedSession { get; private set; }

        public Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult(account);

        public Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash,
            DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
        {
            CreatedSession = (sessionId, accountId);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTime value) : TimeProvider
    {
        private readonly DateTimeOffset current = new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => current;
    }
}
