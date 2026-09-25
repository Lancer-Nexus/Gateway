using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class GameInstanceKeyAuthenticatorTests
{
    [Fact]
    public void TryAuthenticate_MapsBearerKeyToItsConfiguredInstance()
    {
        const string secret = "instance-secret-with-at-least-32-characters";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:GameInstanceKeys:li02-instance"] = secret
        }).Build();
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {secret}";

        var authenticated = new GameInstanceKeyAuthenticator(configuration)
            .TryAuthenticate(context.Request, out var instanceId);

        Assert.True(authenticated);
        Assert.Equal("li02-instance", instanceId);
    }

    [Fact]
    public void TryAuthenticate_RejectsUnmappedOrShortBearerKeys()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:GameInstanceKeys:li02-instance"] = "instance-secret-with-at-least-32-characters"
        }).Build();
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer short";

        var authenticated = new GameInstanceKeyAuthenticator(configuration)
            .TryAuthenticate(context.Request, out var instanceId);

        Assert.False(authenticated);
        Assert.Equal(string.Empty, instanceId);
    }
}
