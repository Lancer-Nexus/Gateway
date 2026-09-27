using System.Security.Cryptography;
using System.Text;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record LoginRequest(string Email, string Password, string? HandshakeToken = null);

public sealed record LoginResponse(string AccessToken, string RefreshToken, Guid AccountId, Guid SessionId, DateTime ExpiresAtUtc);
public sealed record RefreshRequest(Guid SessionId, string RefreshToken);

public sealed record AuthenticationSessionOptions(TimeSpan Lifetime, TimeSpan? IdleTimeout = null)
{
    public TimeSpan EffectiveIdleTimeout => IdleTimeout ?? TimeSpan.FromMinutes(5);

    public static AuthenticationSessionOptions FromConfiguration(IConfiguration configuration)
    {
        var lifetimeHours = configuration.GetValue<int?>("Gateway:SessionLifetimeHours") ?? 24;
        if (lifetimeHours is < 1 or > 720)
            throw new InvalidOperationException("Gateway:SessionLifetimeHours must be between 1 and 720.");
        var idleMinutes = configuration.GetValue<int?>("Gateway:SessionIdleTimeoutMinutes") ?? 5;
        if (idleMinutes is < 1 or > 1440)
            throw new InvalidOperationException("Gateway:SessionIdleTimeoutMinutes must be between 1 and 1440.");
        return new AuthenticationSessionOptions(TimeSpan.FromHours(lifetimeHours), TimeSpan.FromMinutes(idleMinutes));
    }
}

public enum LoginFailure
{
    None,
    InvalidCredentials,
    InvalidRefreshToken,
    PersistenceUnavailable,
    TokenSigningUnavailable
}

public sealed record LoginResult(LoginResponse? Response, LoginFailure Failure)
{
    public bool Succeeded => Response is not null && Failure == LoginFailure.None;
}

public interface IPasswordVerifier
{
    bool Verify(string password, string passwordHash);
}

public sealed class BcryptPasswordVerifier : IPasswordVerifier
{
    public bool Verify(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(passwordHash))
            return false;
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public sealed class GatewayAuthenticationService(
    IAccountRepository accounts,
    IPasswordVerifier passwordVerifier,
    SessionTokenCodec tokenCodec,
    SessionTokenOptions tokenOptions,
    TimeProvider timeProvider,
    AuthenticationSessionOptions? sessionOptions = null)
{
    private TimeSpan SessionLifetime => sessionOptions?.Lifetime ?? TimeSpan.FromHours(24);

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            return new LoginResult(null, LoginFailure.InvalidCredentials);

        AccountRecord? account;
        try
        {
            account = await accounts.FindByEmailAsync(request.Email, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return new LoginResult(null, LoginFailure.PersistenceUnavailable);
        }

        if (account is null || !string.Equals(account.Status, "active", StringComparison.Ordinal) ||
            !passwordVerifier.Verify(request.Password, account.PasswordHash))
            return new LoginResult(null, LoginFailure.InvalidCredentials);
        if (!tokenOptions.IsConfigured)
            return new LoginResult(null, LoginFailure.TokenSigningUnavailable);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sessionId = Guid.NewGuid();
        var nonce = CreateNonce();
        var sessionExpires = now.Add(Min(SessionLifetime, sessionOptions?.EffectiveIdleTimeout ?? TimeSpan.FromMinutes(5)));
        var accessTokenExpires = Min(sessionExpires, now.Add(tokenOptions.Lifetime));
        try
        {
            var refreshToken = CreateNonce();
            await accounts.CreateSessionAsync(
                sessionId,
                account.AccountId,
                SHA256.HashData(Encoding.UTF8.GetBytes(nonce)),
                SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)),
                now,
                sessionExpires,
                cancellationToken);
            var token = tokenCodec.Issue(new SessionTokenClaims
            {
                SessionId = sessionId,
                AccountId = account.AccountId,
                Audience = tokenOptions.Audience,
                IssuedAtUtc = now,
                ExpiresAtUtc = accessTokenExpires,
                Nonce = nonce,
                KeyId = tokenOptions.KeyId
            });
            return new LoginResult(new LoginResponse(token, refreshToken, account.AccountId, sessionId,
                accessTokenExpires), LoginFailure.None);
        }
        catch (InvalidOperationException)
        {
            return new LoginResult(null, LoginFailure.PersistenceUnavailable);
        }
    }

    public async Task<LoginResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.RefreshToken))
            return new LoginResult(null, LoginFailure.InvalidRefreshToken);
        if (!tokenOptions.IsConfigured)
            return new LoginResult(null, LoginFailure.TokenSigningUnavailable);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var newRefreshToken = CreateNonce();
        SessionRecord? session;
        try
        {
            session = await accounts.RotateRefreshTokenAsync(
                request.SessionId,
                SHA256.HashData(Encoding.UTF8.GetBytes(request.RefreshToken)),
                SHA256.HashData(Encoding.UTF8.GetBytes(newRefreshToken)),
                SessionLifetime,
                sessionOptions?.EffectiveIdleTimeout ?? TimeSpan.FromMinutes(5),
                now,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return new LoginResult(null, LoginFailure.PersistenceUnavailable);
        }
        if (session is null)
            return new LoginResult(null, LoginFailure.InvalidRefreshToken);

        var accessNonce = CreateNonce();
        // Database DATETIME values do not carry a timezone. The session store contract
        // records UTC, so restore that kind before serializing the API response.
        var sessionExpiresAtUtc = DateTime.SpecifyKind(session.ExpiresAtUtc, DateTimeKind.Utc);
        var expires = sessionExpiresAtUtc < now.Add(tokenOptions.Lifetime)
            ? sessionExpiresAtUtc
            : now.Add(tokenOptions.Lifetime);
        var token = tokenCodec.Issue(new SessionTokenClaims
        {
            SessionId = request.SessionId,
            AccountId = session.AccountId,
            Audience = tokenOptions.Audience,
            IssuedAtUtc = now,
            ExpiresAtUtc = expires,
            Nonce = accessNonce,
            KeyId = tokenOptions.KeyId
        });
        return new LoginResult(new LoginResponse(token, newRefreshToken, session.AccountId, request.SessionId, expires), LoginFailure.None);
    }

    private static string CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
    private static DateTime Min(DateTime left, DateTime right) => left <= right ? left : right;
}
