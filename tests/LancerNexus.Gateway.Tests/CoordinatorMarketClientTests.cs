using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class CoordinatorMarketClientTests
{
    [Fact]
    public async Task ReadSystem_UsesPrivateCoordinatorCredentialAndValidatesPinnedPage()
    {
        using var handler = new MarketHandler();
        using var httpClient = new HttpClient(handler);
        var apiKey = new string('k', 32);
        var client = new CoordinatorMarketClient(httpClient,
            new CoordinatorGatewayOptions(new Uri("https://coordinator.example/"), apiKey,
                TimeSpan.FromSeconds(5)));
        var request = new MarketSnapshotReadRequestV1
        {
            CorrelationId = Guid.NewGuid(),
            SystemId = "li01",
            PageSize = 20
        };

        var result = await client.ReadSystemAsync(request);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Snapshot);
        Assert.True(result.Snapshot!.IsValidFor(request));
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal(apiKey, handler.Authorization?.Parameter);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://coordinator.example/internal/v1/market/snapshots/read",
            handler.RequestUri?.ToString());
        Assert.Equal(request.CorrelationId, handler.ForwardedRequest?.CorrelationId);
    }

    [Fact]
    public async Task ReadSystem_FailsClosedForInvalidCoordinatorSnapshot()
    {
        using var handler = new MarketHandler(mismatchCorrelationId: true);
        using var httpClient = new HttpClient(handler);
        var client = new CoordinatorMarketClient(httpClient,
            new CoordinatorGatewayOptions(new Uri("https://coordinator.example/"), new string('k', 32),
                TimeSpan.FromSeconds(5)));

        var result = await client.ReadSystemAsync(new MarketSnapshotReadRequestV1
        {
            CorrelationId = Guid.NewGuid(),
            SystemId = "li01"
        });

        Assert.Null(result.Snapshot);
        Assert.Equal("coordinator_invalid_market_response", result.Error);
    }

    private sealed class MarketHandler(bool mismatchCorrelationId = false) : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public MarketSnapshotReadRequestV1? ForwardedRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            Method = request.Method;
            RequestUri = request.RequestUri;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            ForwardedRequest = JsonSerializer.Deserialize<MarketSnapshotReadRequestV1>(body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var response = new MarketSnapshotReadResponseV1
            {
                CorrelationId = mismatchCorrelationId ? Guid.NewGuid() : ForwardedRequest!.CorrelationId,
                SystemId = "li01",
                Status = MarketSnapshotReadStatusV1.Ok,
                SystemRevision = 7,
                Goods =
                [
                    new MarketGoodSnapshotV1
                    {
                        MarketId = "li01_01_base",
                        SystemId = "li01",
                        GoodId = "commodity_food",
                        BaseUnitPrice = 100,
                        CurrentUnitPrice = 120,
                        AvailableStock = 25,
                        TargetStock = 20,
                        BuyEnabled = true,
                        SellEnabled = true,
                        MarketRevision = 3
                    }
                ]
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
}
