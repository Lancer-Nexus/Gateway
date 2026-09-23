using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class SessionTokenCodecTests
{
    [Fact]
    public void IssueAndValidate_RoundTripClaims()
    {
        var now = DateTime.UtcNow;
        var codec = Codec();
        var claims = Claims(now);

        var token = codec.Issue(claims);
        var result = codec.Validate(token, now.AddSeconds(1));

        Assert.True(result.Accepted);
        Assert.Equal("accepted", result.ReasonCode);
        Assert.Equal(claims.SessionId, result.Claims!.SessionId);
        Assert.Equal(claims.AccountId, result.Claims.AccountId);
        Assert.Equal("liberty-01", result.Claims.InstanceId);
    }

    [Fact]
    public void Validate_RejectsTamperedPayloadAndExpiredClaims()
    {
        var now = DateTime.UtcNow;
        var codec = Codec();
        var token = codec.Issue(Claims(now));
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}x.{parts[2]}";

        var signatureResult = codec.Validate(tampered, now);
        var expiredResult = codec.Validate(token, now.AddMinutes(11));

        Assert.False(signatureResult.Accepted);
        Assert.Equal("token_signature_invalid", signatureResult.ReasonCode);
        Assert.False(expiredResult.Accepted);
        Assert.Equal("token_claims_invalid", expiredResult.ReasonCode);
    }

    [Fact]
    public void Validate_FailsClosedWhenSigningIsNotConfigured()
    {
        var codec = new SessionTokenCodec(new SessionTokenOptions(
            null,
            "gateway-key-01",
            "game-server",
            TimeSpan.FromMinutes(10)));

        var result = codec.Validate("ln1.invalid.invalid", DateTime.UtcNow);

        Assert.False(result.Accepted);
        Assert.Equal("token_signing_not_configured", result.ReasonCode);
    }

    private static SessionTokenCodec Codec() => new(new SessionTokenOptions(
        new string('s', 32),
        "gateway-key-01",
        "game-server",
        TimeSpan.FromMinutes(10)));

    private static SessionTokenClaims Claims(DateTime now) => new()
    {
        SessionId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        Audience = "game-server",
        IssuedAtUtc = now,
        ExpiresAtUtc = now.AddMinutes(10),
        Nonce = "nonce-01",
        InstanceId = "liberty-01",
        KeyId = "gateway-key-01"
    };
}
