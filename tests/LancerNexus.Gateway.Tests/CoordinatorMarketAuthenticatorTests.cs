using LancerNexus.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class CoordinatorMarketAuthenticatorTests
{
    [Fact]
    public void AcceptsOnlyDedicatedHttpsCoordinatorCredential()
    {
        const string serviceKey = "s3cr3t-service-key-with-more-than-32-bytes";
        const string instanceKey = "instance-key-with-more-than-32-bytes-01";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:CoordinatorMarketApiKey"] = serviceKey,
            ["Gateway:GameInstanceKeys:li-01"] = instanceKey
        }).Build();
        var authentication = new CoordinatorMarketAuthenticator(configuration,
            new GameInstanceKeyAuthenticator(configuration));

        Assert.True(authentication.TryAuthenticate(Request("https", serviceKey)));
        Assert.False(authentication.TryAuthenticate(Request("http", serviceKey)));
        Assert.False(authentication.TryAuthenticate(Request("https", instanceKey)));
        Assert.False(authentication.TryAuthenticate(Request("https", new string('x', 40))));
    }

    private static HttpRequest Request(string scheme, string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Headers.Authorization = $"Bearer {token}";
        return context.Request;
    }
}
