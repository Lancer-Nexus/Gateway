using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class TransferTicketCodecTests
{
    [Fact]
    public void IssueAndValidate_BindTicketToSourceTargetAndLeaseVersion()
    {
        var now = DateTime.UtcNow;
        var codec = CreateCodec();
        var claims = Claims(now);

        var ticket = codec.Issue(claims);
        var result = codec.Validate(ticket, now.AddSeconds(1));

        Assert.True(result.Accepted);
        Assert.Equal(claims.TransferId, result.Claims!.TransferId);
        Assert.Equal(claims.SourceInstanceId, result.Claims.SourceInstanceId);
        Assert.Equal(claims.TargetInstanceId, result.Claims.TargetInstanceId);
        Assert.Equal(claims.LeaseVersion, result.Claims.LeaseVersion);
    }

    [Fact]
    public void Validate_RejectsTamperedPayloadAndExpiredTicket()
    {
        var now = DateTime.UtcNow;
        var codec = CreateCodec();
        var ticket = codec.Issue(Claims(now));
        var parts = ticket.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{Convert.ToBase64String(new byte[32])}";

        Assert.Equal("transfer_ticket_signature_invalid", codec.Validate(tampered, now).ReasonCode);
        Assert.Equal("transfer_ticket_claims_invalid", codec.Validate(ticket, now.AddMinutes(3)).ReasonCode);
    }

    [Fact]
    public void ValidateForTransferRecovery_AcceptsExpiredSignatureForLifecycleRecheck()
    {
        var issued = DateTime.UtcNow;
        var codec = CreateCodec();
        var ticket = codec.Issue(Claims(issued));

        Assert.False(codec.Validate(ticket, issued.AddMinutes(3)).Accepted);
        var recovery = codec.ValidateForTransferRecovery(ticket, issued.AddMinutes(3));

        Assert.True(recovery.Accepted);
        Assert.Equal("accepted", recovery.ReasonCode);
    }

    [Fact]
    public void Issue_RejectsTicketLifetimeOverTwoMinutes()
    {
        var now = DateTime.UtcNow;
        var claims = Claims(now, TimeSpan.FromMinutes(3));

        var error = Assert.Throws<ArgumentException>(() => CreateCodec().Issue(claims));

        Assert.Contains("claims are invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TransferTicketCodec CreateCodec() => new(new TransferTicketOptions(
        new string('t', 32), "gateway-transfer-01", "game-server-transfer"));

    private static TransferTicketClaims Claims(DateTime now, TimeSpan? lifetime = null) => new()
    {
        TransferId = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        CharacterId = 73,
        SourceInstanceId = "li01-instance",
        TargetInstanceId = "li02-instance",
        TargetSystemId = "li02",
        LeaseVersion = 14,
        IssuedAtUtc = now,
        ExpiresAtUtc = now.Add(lifetime ?? TimeSpan.FromMinutes(1)),
        Nonce = "nonce-01",
        Audience = "game-server-transfer",
        KeyId = "gateway-transfer-01"
    };
}
