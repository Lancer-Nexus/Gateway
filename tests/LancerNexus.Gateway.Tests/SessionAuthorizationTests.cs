using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class SessionAuthorizationTests
{
    [Fact]
    public void Authorize_AcceptsMatchingSession()
    {
        var now = DateTime.UtcNow;
        var request = Request();
        var codec = Codec();
        var claims = Claims(now, request.SessionId);

        var result = SessionAuthorization.Authorize(
            $"Bearer {codec.Issue(claims)}", request, codec, now.AddSeconds(1));

        Assert.True(result.Accepted);
        Assert.Equal(claims.AccountId, result.Claims!.AccountId);
    }

    [Fact]
    public void Authorize_RejectsMissingOrMismatchedSession()
    {
        var now = DateTime.UtcNow;
        var request = Request();
        var codec = Codec();
        var token = codec.Issue(Claims(now, Guid.NewGuid()));

        var missing = SessionAuthorization.Authorize(null, request, codec, now);
        var mismatch = SessionAuthorization.Authorize($"Bearer {token}", request, codec, now);

        Assert.Equal("authorization_required", missing.ReasonCode);
        Assert.Equal("session_id_mismatch", mismatch.ReasonCode);
        Assert.False(missing.Accepted);
        Assert.False(mismatch.Accepted);
    }

    [Fact]
    public void Authorize_ReportsMissingSigningConfiguration()
    {
        var result = SessionAuthorization.Authorize(
            "Bearer anything",
            Request(),
            new SessionTokenCodec(new SessionTokenOptions(null, "key", "game-server", TimeSpan.FromMinutes(10))),
            DateTime.UtcNow);

        Assert.True(result.ConfigurationError);
        Assert.Equal("token_signing_not_configured", result.ReasonCode);
    }

    [Fact]
    public void AuthorizeToken_ReturnsOnlyValidatedClaims()
    {
        var now = DateTime.UtcNow;
        var codec = Codec();
        var claims = Claims(now, Guid.NewGuid());

        var result = SessionAuthorization.AuthorizeToken(
            $"Bearer {codec.Issue(claims)}", codec, now.AddSeconds(1));

        Assert.True(result.Accepted);
        Assert.Equal(claims.AccountId, result.Claims!.AccountId);
        Assert.Equal("game-server", result.Claims.Audience);
    }

    private static PlacementRequest Request() => new()
    {
        RequestId = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        TargetSystem = "li01",
        IdempotencyKey = "placement-1"
    };

    private static SessionTokenCodec Codec() => new(new SessionTokenOptions(
        new string('s', 32), "gateway-key-01", "game-server", TimeSpan.FromMinutes(10)));

    private static SessionTokenClaims Claims(DateTime now, Guid sessionId) => new()
    {
        SessionId = sessionId,
        AccountId = Guid.NewGuid(),
        Audience = "game-server",
        IssuedAtUtc = now,
        ExpiresAtUtc = now.AddMinutes(10),
        Nonce = "nonce-01",
        KeyId = "gateway-key-01"
    };
}
