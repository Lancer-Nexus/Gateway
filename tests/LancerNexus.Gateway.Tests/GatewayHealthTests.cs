using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class GatewayHealthTests
{
    [Fact]
    public void Gateway_UsesCurrentProtocolVersion()
    {
        Assert.Equal(1, ProtocolConstants.ProtocolVersion);
    }
}
