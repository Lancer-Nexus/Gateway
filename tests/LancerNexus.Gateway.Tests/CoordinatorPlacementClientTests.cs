using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class CoordinatorPlacementClientTests
{
    [Fact]
    public async Task Place_ForwardsBearerAndIdempotentRequest()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new CoordinatorPlacementClient(
            httpClient,
            new CoordinatorGatewayOptions(
                new Uri("https://coordinator.example/"),
                new string('k', 32),
                TimeSpan.FromSeconds(5)));
        var request = Request();

        var result = await client.PlaceAsync(request);

        Assert.True(result.IsAvailable);
        Assert.True(result.Envelope!.Decision.Accepted);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal(new string('k', 32), handler.Authorization?.Parameter);
        Assert.Contains("assignment-1", handler.Body, StringComparison.Ordinal);
        Assert.Equal("https://coordinator.example/api/v1/placement", handler.RequestUri!.ToString());
    }

    [Fact]
    public async Task Place_FailsClosedWhenCoordinatorIsNotConfigured()
    {
        using var httpClient = new HttpClient(new CaptureHandler());
        var client = new CoordinatorPlacementClient(
            httpClient,
            new CoordinatorGatewayOptions(null, null, TimeSpan.FromSeconds(5)));

        var result = await client.PlaceAsync(Request());

        Assert.False(result.IsAvailable);
        Assert.Equal("coordinator_not_configured", result.Error);
    }

    private static PlacementRequest Request() => new()
    {
        RequestId = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        TargetSystem = "li01",
        ClientBuild = "test",
        Region = "eu",
        IdempotencyKey = "assignment-1"
    };

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Body { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CoordinatorPlacementEnvelope(
                    new PlacementDecision
                    {
                        RequestId = Guid.NewGuid(),
                        Accepted = true,
                        InstanceId = "liberty-01",
                        SystemId = "li01",
                        Endpoint = "10.20.0.31:2300",
                        ReasonCode = "assigned",
                        ExpiresUtc = DateTime.UtcNow.AddSeconds(15)
                    },
                    false))
            };
        }
    }
}
