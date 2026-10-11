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
    public async Task ResolveTrade_UsesDedicatedCredentialAndValidatesTradeBinding()
    {
        using var handler = new TradeResolutionHandler();
        using var httpClient = new HttpClient(handler);
        var tradeKey = new string('t', 32);
        var client = new CoordinatorMarketClient(httpClient,
            new CoordinatorGatewayOptions(new Uri("https://coordinator.example/"), new string('r', 32),
                TimeSpan.FromSeconds(5), tradeKey));
        var request = new MarketTradeResolutionRequestV1
        {
            CorrelationId = Guid.NewGuid(), TradeId = Guid.NewGuid(),
            Resolution = MarketTradeResolutionV1.Commit
        };

        var result = await client.ResolveTradeAsync(request);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Resolution);
        Assert.True(result.Resolution!.Matches(request));
        Assert.Equal(request, handler.ForwardedRequest);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal(tradeKey, handler.Authorization?.Parameter);
        Assert.Equal("https://coordinator.example/internal/v1/market/trades/resolve",
            handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task ReserveQuote_UsesDedicatedMarketTradeCredentialAndValidatesBinding()
    {
        using var handler = new ReservationHandler();
        using var httpClient = new HttpClient(handler);
        var tradeKey = new string('t', 32);
        var client = new CoordinatorMarketClient(httpClient,
            new CoordinatorGatewayOptions(new Uri("https://coordinator.example/"), new string('r', 32),
                TimeSpan.FromSeconds(5), tradeKey));
        var request = new MarketTradeQuoteReservationRequestV1
        {
            CorrelationId = Guid.NewGuid(), TradeId = Guid.NewGuid(), MarketId = "li01_base",
            SystemId = "li-01", GoodId = "commodity_food", Side = MarketTradeSideV1.Buy,
            Quantity = 2, ExpectedMarketRevision = 7, ExpectedReputationRevision = 9,
            ReputationPriceFactorPartsPerMillion = 950_000, ExpectedQuoteUnitPrice = 95,
            ExpectedTotalPrice = 190, CharacterId = 42, MarketFactionId = "li_grp"
        };

        var result = await client.ReserveQuoteAsync(request);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Reservation);
        Assert.True(result.Reservation!.Matches(request));
        Assert.Equal(request, handler.ForwardedRequest);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal(tradeKey, handler.Authorization?.Parameter);
        Assert.Equal("https://coordinator.example/internal/v1/market/trades/reserve-quote",
            handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task ReadQuote_ForwardsCharacterAndRevisionBindingOverPrivateCoordinatorCall()
    {
        using var handler = new QuoteHandler();
        using var httpClient = new HttpClient(handler);
        var apiKey = new string('k', 32);
        var client = new CoordinatorMarketClient(httpClient,
            new CoordinatorGatewayOptions(new Uri("https://coordinator.example/"), apiKey,
                TimeSpan.FromSeconds(5)));
        var request = new MarketQuoteRequestV1
        {
            CorrelationId = Guid.NewGuid(), CharacterId = 42, SystemId = "li-01",
            MarketId = "li01_base", GoodId = "commodity_food", Side = MarketTradeSideV1.Buy,
            Quantity = 2, ExpectedMarketRevision = 7, ExpectedReputationRevision = 9
        };

        var result = await client.ReadQuoteAsync(request);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Quote);
        Assert.True(result.Quote!.IsValidFor(request));
        Assert.Equal(request, handler.ForwardedRequest);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal(apiKey, handler.Authorization?.Parameter);
        Assert.Equal("https://coordinator.example/internal/v1/market/quotes/read",
            handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task ReadSystems_UsesPrivateCredentialAndReturnsZeroOfferSystems()
    {
        using var handler = new SystemsHandler();
        using var httpClient = new HttpClient(handler);
        var apiKey = new string('k', 32);
        var client = new CoordinatorMarketClient(httpClient,
            new CoordinatorGatewayOptions(new Uri("https://coordinator.example/"), apiKey,
                TimeSpan.FromSeconds(5)));
        var request = new MarketSystemsReadRequestV1 { CorrelationId = Guid.NewGuid(), PageSize = 20 };

        var result = await client.ReadSystemsAsync(request);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Systems);
        Assert.True(result.Systems!.IsValidFor(request));
        Assert.Equal("bw-01", Assert.Single(result.Systems.Systems).SystemId);
        Assert.Equal(0, result.Systems.Systems[0].MarketCount);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal(apiKey, handler.Authorization?.Parameter);
        Assert.Equal("https://coordinator.example/internal/v1/market/systems/read",
            handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task ReadGood_ForwardsExactMarketIdentityAndRevisionPins()
    {
        using var handler = new GoodHandler();
        using var httpClient = new HttpClient(handler);
        var apiKey = new string('k', 32);
        var client = new CoordinatorMarketClient(httpClient,
            new CoordinatorGatewayOptions(new Uri("https://coordinator.example/"), apiKey,
                TimeSpan.FromSeconds(5)));
        var request = new MarketGoodReadRequestV1
        {
            CorrelationId = Guid.NewGuid(), SystemId = "li-01", MarketId = "li01_base",
            GoodId = "commodity_food", ExpectedSystemRevision = 7, ExpectedMarketRevision = 3
        };

        var result = await client.ReadGoodAsync(request);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Good);
        Assert.True(result.Good!.IsValidFor(request));
        Assert.Equal(request, handler.ForwardedRequest);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal(apiKey, handler.Authorization?.Parameter);
        Assert.Equal("https://coordinator.example/internal/v1/market/goods/read", handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task ReadCatalog_UsesPrivateCoordinatorCredentialAndValidatesRevisionPinnedResponse()
    {
        using var handler = new CatalogHandler();
        using var httpClient = new HttpClient(handler);
        var apiKey = new string('k', 32);
        var client = new CoordinatorMarketClient(httpClient,
            new CoordinatorGatewayOptions(new Uri("https://coordinator.example/"), apiKey,
                TimeSpan.FromSeconds(5)));
        var request = new MarketCatalogReadRequestV1 { CorrelationId = Guid.NewGuid(), PageSize = 2 };

        var result = await client.ReadCatalogAsync(request);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Catalog);
        Assert.True(result.Catalog!.IsValidFor(request));
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal(apiKey, handler.Authorization?.Parameter);
        Assert.Equal("https://coordinator.example/internal/v1/market/catalog/read", handler.RequestUri?.ToString());
        Assert.Equal(request.CorrelationId, handler.ForwardedRequest?.CorrelationId);
    }

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

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public Uri? RequestUri { get; private set; }
        public MarketCatalogReadRequestV1? ForwardedRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            RequestUri = request.RequestUri;
            ForwardedRequest = JsonSerializer.Deserialize<MarketCatalogReadRequestV1>(
                await request.Content!.ReadAsStringAsync(cancellationToken),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var response = new MarketCatalogReadResponseV1
            {
                CorrelationId = ForwardedRequest!.CorrelationId,
                Status = MarketCatalogReadStatusV1.Ok,
                CatalogRevision = new string('a', 64),
                Goods = [new MarketCatalogGoodV1 { GoodId = "commodity_food", Category = "commodity" }]
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }

    private sealed class SystemsHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            RequestUri = request.RequestUri;
            var forwarded = await request.Content!.ReadFromJsonAsync<MarketSystemsReadRequestV1>(cancellationToken);
            var response = new MarketSystemsReadResponseV1
            {
                CorrelationId = forwarded!.CorrelationId,
                Status = MarketCatalogReadStatusV1.Ok,
                CatalogRevision = new string('a', 64),
                Systems = [new MarketSystemSummaryV1 { SystemId = "bw-01" }]
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }

    private sealed class QuoteHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public Uri? RequestUri { get; private set; }
        public MarketQuoteRequestV1? ForwardedRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            RequestUri = request.RequestUri;
            ForwardedRequest = await request.Content!.ReadFromJsonAsync<MarketQuoteRequestV1>(cancellationToken);
            var quote = new MarketQuoteResponseV1
            {
                CorrelationId = ForwardedRequest!.CorrelationId,
                CharacterId = ForwardedRequest.CharacterId,
                SystemId = ForwardedRequest.SystemId,
                MarketId = ForwardedRequest.MarketId,
                GoodId = ForwardedRequest.GoodId,
                Side = ForwardedRequest.Side,
                Quantity = ForwardedRequest.Quantity,
                Status = MarketQuoteStatusV1.Quoted,
                MarketRevision = ForwardedRequest.ExpectedMarketRevision ?? 7,
                ReputationRevision = ForwardedRequest.ExpectedReputationRevision,
                MarketFactionId = "li_grp",
                AppliedReputation = 0.5f,
                ReputationFactorPartsPerMillion = 950_000,
                QuoteUnitPrice = 95,
                TotalPrice = 190,
                ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(15),
                PricingPolicyVersion = 1
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(quote) };
        }
    }

    private sealed class ReservationHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public Uri? RequestUri { get; private set; }
        public MarketTradeQuoteReservationRequestV1? ForwardedRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            RequestUri = request.RequestUri;
            ForwardedRequest = await request.Content!.ReadFromJsonAsync<MarketTradeQuoteReservationRequestV1>(cancellationToken);
            var response = new MarketTradeQuoteReservationResponseV1
            {
                CorrelationId = ForwardedRequest!.CorrelationId,
                TradeId = ForwardedRequest.TradeId,
                Status = MarketTradeReservationStatusV1.Reserved,
                QuoteUnitPrice = ForwardedRequest.ExpectedQuoteUnitPrice,
                TotalPrice = ForwardedRequest.ExpectedTotalPrice,
                MarketRevision = 8,
                SystemRevision = 22,
                ReputationRevision = ForwardedRequest.ExpectedReputationRevision,
                ReputationPriceFactorPartsPerMillion = ForwardedRequest.ReputationPriceFactorPartsPerMillion,
                MarketFactionId = ForwardedRequest.MarketFactionId
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }

    private sealed class TradeResolutionHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public Uri? RequestUri { get; private set; }
        public MarketTradeResolutionRequestV1? ForwardedRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            RequestUri = request.RequestUri;
            ForwardedRequest = await request.Content!.ReadFromJsonAsync<MarketTradeResolutionRequestV1>(cancellationToken);
            var result = new MarketTradeReservationResponseV1
            {
                CorrelationId = ForwardedRequest!.CorrelationId,
                TradeId = ForwardedRequest.TradeId,
                Status = MarketTradeReservationStatusV1.Committed,
                QuoteUnitPrice = 100,
                TotalPrice = 200,
                MarketRevision = 8,
                SystemRevision = 9
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }

    private sealed class GoodHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public Uri? RequestUri { get; private set; }
        public MarketGoodReadRequestV1? ForwardedRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            RequestUri = request.RequestUri;
            ForwardedRequest = await request.Content!.ReadFromJsonAsync<MarketGoodReadRequestV1>(cancellationToken);
            var good = new MarketGoodSnapshotV1
            {
                MarketId = ForwardedRequest!.MarketId, SystemId = ForwardedRequest.SystemId,
                GoodId = ForwardedRequest.GoodId, BaseUnitPrice = 100, CurrentUnitPrice = 110,
                AvailableStock = 6, TargetStock = 10, BuyEnabled = true, SellEnabled = false,
                MarketRevision = ForwardedRequest.ExpectedMarketRevision ?? 3
            };
            var response = new MarketGoodReadResponseV1
            {
                CorrelationId = ForwardedRequest.CorrelationId, SystemId = ForwardedRequest.SystemId,
                MarketId = ForwardedRequest.MarketId, GoodId = ForwardedRequest.GoodId,
                Status = MarketGoodReadStatusV1.Found,
                SystemRevision = ForwardedRequest.ExpectedSystemRevision ?? 7,
                MarketRevision = good.MarketRevision, Good = good
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
}
