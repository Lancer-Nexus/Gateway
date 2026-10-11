using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record CoordinatorMarketReadResult(
    HttpStatusCode? StatusCode,
    MarketSnapshotReadResponseV1? Snapshot,
    string? Error)
{
    public bool IsAvailable => StatusCode is not null;
}

public sealed record CoordinatorMarketCatalogReadResult(
    HttpStatusCode? StatusCode,
    MarketCatalogReadResponseV1? Catalog,
    string? Error);

public sealed record CoordinatorMarketQuoteReadResult(
    HttpStatusCode? StatusCode,
    MarketQuoteResponseV1? Quote,
    string? Error);

public sealed class CoordinatorMarketClient(HttpClient httpClient, CoordinatorGatewayOptions options)
{
    public async Task<CoordinatorMarketQuoteReadResult> ReadQuoteAsync(
        MarketQuoteRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsValid())
            return new(null, null, "invalid_market_quote_request");
        if (!options.IsConfigured)
            return new(null, null, "coordinator_not_configured");
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(options.BaseAddress!, "internal/v1/market/quotes/read"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.InternalApiKey);
        message.Content = JsonContent.Create(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var response = await httpClient.SendAsync(message, timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict or
                HttpStatusCode.NotFound or HttpStatusCode.BadRequest))
                return new(response.StatusCode, null, "coordinator_market_quote_rejected");
            var quote = await response.Content.ReadFromJsonAsync<MarketQuoteResponseV1>(timeout.Token);
            if (quote is null || !quote.IsValidFor(request))
                return new(response.StatusCode, null, "coordinator_invalid_market_quote_response");
            return new(response.StatusCode, quote, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, null, "coordinator_timeout");
        }
        catch (HttpRequestException)
        {
            return new(null, null, "coordinator_unavailable");
        }
        catch (JsonException)
        {
            return new(null, null, "coordinator_invalid_market_quote_response");
        }
    }

    public async Task<CoordinatorMarketCatalogReadResult> ReadCatalogAsync(
        MarketCatalogReadRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsValid())
            return new(null, null, "invalid_market_catalog_request");
        if (!options.IsConfigured)
            return new(null, null, "coordinator_not_configured");

        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(options.BaseAddress!, "internal/v1/market/catalog/read"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.InternalApiKey);
        message.Content = JsonContent.Create(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var response = await httpClient.SendAsync(message, timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
                return new(response.StatusCode, null, "coordinator_market_catalog_rejected");
            var catalog = await response.Content.ReadFromJsonAsync<MarketCatalogReadResponseV1>(timeout.Token);
            if (catalog is null || !catalog.IsValidFor(request))
                return new(response.StatusCode, null, "coordinator_invalid_market_catalog_response");
            return new(response.StatusCode, catalog, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, null, "coordinator_timeout");
        }
        catch (HttpRequestException)
        {
            return new(null, null, "coordinator_unavailable");
        }
        catch (JsonException)
        {
            return new(null, null, "coordinator_invalid_market_catalog_response");
        }
    }

    public async Task<CoordinatorMarketReadResult> ReadSystemAsync(
        MarketSnapshotReadRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsValid())
            return new(null, null, "invalid_market_snapshot_request");
        if (!options.IsConfigured)
            return new(null, null, "coordinator_not_configured");

        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(options.BaseAddress!, "internal/v1/market/snapshots/read"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.InternalApiKey);
        message.Content = JsonContent.Create(request);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var response = await httpClient.SendAsync(message, timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
                return new(response.StatusCode, null, "coordinator_market_read_rejected");

            var snapshot = await response.Content.ReadFromJsonAsync<MarketSnapshotReadResponseV1>(timeout.Token);
            if (snapshot is null || !snapshot.IsValidFor(request))
                return new(response.StatusCode, null, "coordinator_invalid_market_response");
            return new(response.StatusCode, snapshot, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, null, "coordinator_timeout");
        }
        catch (HttpRequestException)
        {
            return new(null, null, "coordinator_unavailable");
        }
        catch (JsonException)
        {
            return new(null, null, "coordinator_invalid_market_response");
        }
    }
}
