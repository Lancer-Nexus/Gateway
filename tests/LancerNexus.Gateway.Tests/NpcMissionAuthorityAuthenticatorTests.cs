using LancerNexus.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class NpcMissionAuthorityAuthenticatorTests
{
    [Fact]
    public void AuthorityRequiresConfiguredServiceKeyAndHttps()
    {
        var key = new string('x', 32);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Gateway:NpcMissionAuthorityApiKey"] = key }).Build();
        var authentication = new NpcMissionAuthorityAuthenticator(config, new GameInstanceKeyAuthenticator(config));
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + key;
        context.Request.Scheme = "http";
        Assert.False(authentication.TryAuthenticate(context.Request));
        context.Request.Scheme = "https";
        Assert.True(authentication.TryAuthenticate(context.Request));
        context.Request.Headers.Authorization = "Bearer " + new string('y', 32);
        Assert.False(authentication.TryAuthenticate(context.Request));
        var emptyConfig = new ConfigurationBuilder().Build();
        var missing = new NpcMissionAuthorityAuthenticator(emptyConfig, new GameInstanceKeyAuthenticator(emptyConfig));
        Assert.False(missing.IsConfigured);
        Assert.False(missing.TryAuthenticate(context.Request));
        var shared = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:NpcMissionAuthorityApiKey"] = key, ["Gateway:GameInstanceKeys:source"] = key,
            ["Gateway:GameInstanceKeys:target"] = key
        }).Build();
        context.Request.Headers.Authorization = "Bearer " + key;
        Assert.False(new NpcMissionAuthorityAuthenticator(shared, new GameInstanceKeyAuthenticator(shared))
            .TryAuthenticate(context.Request));
    }
}
