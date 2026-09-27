using System.Net;
using System.Threading.RateLimiting;
using LancerNexus.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public class TransferRateLimitPartitionTests
{
    [Fact]
    public async Task HandoffStepsHaveIndependentBudgetsButRepeatedRequestsRemainLimited()
    {
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(TransferRateLimitPartition.GetKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 2, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                    AutoReplenishment = false
                }));
        var routes = new[] { "start-transfer", "freeze-transfer/{transferId:guid}",
            "verify-transfer-ticket", "transfers/{transferId:guid}/snapshot", "accept-transfer", "release-transfer" };
        for (var jump = 0; jump < 2; jump++)
            foreach (var route in routes)
            {
                using var lease = await limiter.AcquireAsync(Context(route, Guid.NewGuid()));
                Assert.True(lease.IsAcquired);
            }
        foreach (var route in routes)
        {
            using var lease = await limiter.AcquireAsync(Context(route, Guid.NewGuid()));
            Assert.False(lease.IsAcquired);
        }
        using var otherHost = await limiter.AcquireAsync(Context(routes[0], Guid.NewGuid(), "127.0.0.2"));
        Assert.True(otherHost.IsAcquired);
    }

    private static HttpContext Context(string route, Guid id, string ip = "127.0.0.1")
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.Method = route.EndsWith("/snapshot") ? "GET" : "POST";
        context.Request.Path = "/api/v1/game/" + route.Replace("{transferId:guid}", id.ToString());
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/v1/game/" + route), 0, EndpointMetadataCollection.Empty, route));
        return context;
    }
}
