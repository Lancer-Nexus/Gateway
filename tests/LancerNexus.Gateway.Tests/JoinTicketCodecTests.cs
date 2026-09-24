using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class JoinTicketCodecTests
{
    [Fact]
    public void IssueAndValidate_RoundTripsBoundTarget()
    {
        var now = DateTime.UtcNow;
        var codec = new JoinTicketCodec(new JoinTicketOptions(new string('j', 32), "join-01", "game-server", TimeSpan.FromMinutes(2)));
        var claims = Claims(now);

        var result = codec.Validate(codec.Issue(claims), now.AddSeconds(1));

        Assert.True(result.Accepted);
        Assert.Equal(claims.SessionId, result.Claims!.SessionId);
        Assert.Equal(claims.CharacterId, result.Claims.CharacterId);
        Assert.Equal("liberty-01", result.Claims.InstanceId);
    }

    [Fact]
    public void Validate_RejectsExpiredTicket()
    {
        var now = DateTime.UtcNow;
        var codec = new JoinTicketCodec(new JoinTicketOptions(new string('j', 32), "join-01", "game-server", TimeSpan.FromMinutes(2)));
        var ticket = codec.Issue(Claims(now));

        var result = codec.Validate(ticket, now.AddMinutes(3));

        Assert.False(result.Accepted);
        Assert.Equal("join_ticket_claims_invalid", result.ReasonCode);
    }

    private static JoinTicketClaims Claims(DateTime now) => new()
    {
        SessionId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        CharacterId = 7,
        InstanceId = "liberty-01",
        SystemId = "li01",
        Endpoint = "10.20.0.31:2300",
        IssuedAtUtc = now,
        ExpiresAtUtc = now.AddMinutes(2),
        Nonce = "nonce-01",
        Audience = "game-server",
        KeyId = "join-01"
    };
}
