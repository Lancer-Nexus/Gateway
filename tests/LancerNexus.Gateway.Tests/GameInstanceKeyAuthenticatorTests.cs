using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class GameInstanceKeyAuthenticatorTests
{
    [Fact]
    public void PrivateJsonFileLoadsHyphenatedBaselineIdsAndRetainsExistingKeys()
    {
        var file = Path.Combine(Path.GetTempPath(), $"nexus-keys-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["br-01"] = new string('b', 40), ["mixed-01"] = new string('m', 40)
            }));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:GameInstanceKeysFile"] = file,
                ["Gateway:GameInstanceKeys:legacy"] = new string('l', 40)
            }).Build();
            var authenticator = new GameInstanceKeyAuthenticator(configuration);
            foreach (var (id, key) in new[] { ("br-01", 'b'), ("mixed-01", 'm'), ("legacy", 'l') })
            {
                var context = new DefaultHttpContext();
                context.Request.Headers.Authorization = "Bearer " + new string(key, 40);
                Assert.True(authenticator.TryAuthenticate(context.Request, out var instance));
                Assert.Equal(id, instance);
            }
        }
        finally { File.Delete(file); }
    }
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
