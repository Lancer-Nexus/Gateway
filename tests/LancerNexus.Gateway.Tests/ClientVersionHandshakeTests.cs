using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class ClientVersionHandshakeTests
{
    private static readonly ClientVersionPolicy Policy = new(
        new Version(1, 0, 1), new Version(1, 0, 2), 1, "data-2026-09-22", "stable");
    private static readonly SessionTokenOptions TokenOptions =
        new(new string('s', 32), "gateway-key-01", "game-server", TimeSpan.FromMinutes(10));

    [Fact]
    public void HandshakeRejectsMalformedVersionMetadata()
    {
        var handshake = new ClientVersionHandshake(Policy, TokenOptions, TimeProvider.System);
        Assert.Throws<ArgumentException>(() => handshake.Evaluate(Hello("")));
        Assert.Throws<ArgumentException>(() => handshake.Evaluate(new ClientVersionHello
        {
            ClientVersion = "1.0.2",
            BuildId = "20260923.1",
            ProtocolVersion = 1,
            Platform = "linux-x64",
            Channel = "stable",
            DataManifestId = "",
            Capabilities = ["transfer-v1"]
        }));
    }

    [Fact]
    public void Handshake_AllowsCurrentClientAndIssuesShortLivedProof()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var handshake = new ClientVersionHandshake(Policy, TokenOptions, clock);
        var decision = handshake.Evaluate(Hello("1.0.2"));

        Assert.Equal(ClientVersionStatus.Supported, decision.Status);
        Assert.True(decision.SessionAllowed);
        Assert.True(handshake.ValidateProof(decision.HandshakeToken));
        var raisedMinimum = new ClientVersionHandshake(
            Policy with { MinimumVersion = new Version(1, 0, 3), LatestVersion = new Version(1, 0, 3) },
            TokenOptions, clock);
        Assert.False(raisedMinimum.ValidateProof(decision.HandshakeToken));
        clock.Now = clock.Now.AddMinutes(6);
        Assert.False(handshake.ValidateProof(decision.HandshakeToken));
    }

    [Fact]
    public void Handshake_RejectsOldProtocolDataAndVersionWithoutProof()
    {
        var handshake = new ClientVersionHandshake(Policy, TokenOptions, TimeProvider.System);
        var protocol = handshake.Evaluate(Hello("1.0.2", protocolVersion: 2));
        var data = handshake.Evaluate(Hello("1.0.2", dataManifestId: "old-data"));
        var version = handshake.Evaluate(Hello("1.0.0"));

        Assert.Equal(ClientVersionStatus.ProtocolUnsupported, protocol.Status);
        Assert.Equal(ClientVersionStatus.UpdateRequired, data.Status);
        Assert.Equal(ClientVersionStatus.UpdateRequired, version.Status);
        Assert.All(new[] { protocol, data, version }, result =>
        {
            Assert.False(result.SessionAllowed);
            Assert.Null(result.HandshakeToken);
        });
        Assert.False(handshake.ValidateProof("forged"));
    }

    [Fact]
    public void Handshake_RecommendsOptionalUpdate()
    {
        var handshake = new ClientVersionHandshake(Policy, TokenOptions, TimeProvider.System);
        var decision = handshake.Evaluate(Hello("1.0.1"));
        Assert.Equal(ClientVersionStatus.UpdateRecommended, decision.Status);
        Assert.True(decision.SessionAllowed);
        Assert.True(handshake.ValidateProof(decision.HandshakeToken));
    }

    private static ClientVersionHello Hello(string version, int protocolVersion = 1,
        string dataManifestId = "data-2026-09-22") => new()
        {
            ClientVersion = version,
            BuildId = "20260923.1",
            ProtocolVersion = protocolVersion,
            DataManifestId = dataManifestId,
            Platform = "linux-x64",
            Channel = "stable",
            Capabilities = ["transfer-v1"]
        };

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
