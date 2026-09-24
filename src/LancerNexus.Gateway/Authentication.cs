using System.Security.Cryptography;
using System.Text;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record LoginRequest(string Email, string Password, string? HandshakeToken = null);

public sealed record LoginResponse(string AccessToken, string RefreshToken, Guid AccountId, Guid SessionId, DateTime ExpiresAtUtc);
public sealed record RefreshRequest(Guid SessionId, string RefreshToken);

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
    TimeProvider timeProvider)
{
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
        var expires = now.Add(tokenOptions.Lifetime);
        try
        {
            var refreshToken = CreateNonce();
            await accounts.CreateSessionAsync(
                sessionId,
                account.AccountId,
                SHA256.HashData(Encoding.UTF8.GetBytes(nonce)),
                SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)),
                now,
                expires,
                cancellationToken);
            var token = tokenCodec.Issue(new SessionTokenClaims
            {
                SessionId = sessionId,
                AccountId = account.AccountId,
                Audience = tokenOptions.Audience,
                IssuedAtUtc = now,
                ExpiresAtUtc = expires,
                Nonce = nonce,
                KeyId = tokenOptions.KeyId
            });
            return new LoginResult(new LoginResponse(token, refreshToken, account.AccountId, sessionId, expires), LoginFailure.None);
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
        var expires = session.ExpiresAtUtc < now.Add(tokenOptions.Lifetime)
            ? session.ExpiresAtUtc
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
}
