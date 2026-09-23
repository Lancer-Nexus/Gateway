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

    [Fact]
    public async Task Place_PreservesCoordinatorRejectionEnvelope()
    {
        using var handler = new CaptureHandler(HttpStatusCode.Conflict, accepted: false);
        using var httpClient = new HttpClient(handler);
        var client = new CoordinatorPlacementClient(
            httpClient,
            new CoordinatorGatewayOptions(
                new Uri("https://coordinator.example/"),
                new string('k', 32),
                TimeSpan.FromSeconds(5)));

        var result = await client.PlaceAsync(Request());

        Assert.True(result.IsAvailable);
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.False(result.Envelope!.Decision.Accepted);
        Assert.Equal("no_capacity", result.Envelope.Decision.ReasonCode);
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
        private readonly HttpStatusCode statusCode;
        private readonly bool accepted;

        public CaptureHandler(HttpStatusCode statusCode = HttpStatusCode.OK, bool accepted = true)
        {
            this.statusCode = statusCode;
            this.accepted = accepted;
        }

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
            return new HttpResponseMessage(statusCode)
            {
                Content = JsonContent.Create(new CoordinatorPlacementEnvelope(
                    new PlacementDecision
                    {
                        RequestId = Guid.NewGuid(),
                        Accepted = accepted,
                        InstanceId = accepted ? "liberty-01" : null,
                        SystemId = accepted ? "li01" : null,
                        Endpoint = accepted ? "10.20.0.31:2300" : null,
                        ReasonCode = accepted ? "assigned" : "no_capacity",
                        ExpiresUtc = DateTime.UtcNow.AddSeconds(15)
                    },
                    false))
            };
        }
    }
}
