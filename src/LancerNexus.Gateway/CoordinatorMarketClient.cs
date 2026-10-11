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

public sealed record CoordinatorMarketSystemsReadResult(
    HttpStatusCode? StatusCode,
    MarketSystemsReadResponseV1? Systems,
    string? Error);

public sealed record CoordinatorMarketGoodReadResult(
    HttpStatusCode? StatusCode,
    MarketGoodReadResponseV1? Good,
    string? Error);

public sealed record CoordinatorMarketQuoteReadResult(
    HttpStatusCode? StatusCode,
    MarketQuoteResponseV1? Quote,
    string? Error);

public sealed record CoordinatorMarketQuoteReservationResult(
    HttpStatusCode? StatusCode,
    MarketTradeQuoteReservationResponseV1? Reservation,
    string? Error);

public sealed record CoordinatorMarketTradeResolutionResult(
    HttpStatusCode? StatusCode,
    MarketTradeReservationResponseV1? Resolution,
    string? Error);

public sealed class CoordinatorMarketClient(HttpClient httpClient, CoordinatorGatewayOptions options)
{
    public async Task<CoordinatorMarketTradeResolutionResult> ResolveTradeAsync(
        MarketTradeResolutionRequestV1 request, CancellationToken cancellationToken = default)
    {
        if (!request.IsValid())
            return new(null, null, "invalid_market_trade_resolution");
        if (!options.IsMarketTradeConfigured)
            return new(null, null, "coordinator_market_trade_not_configured");
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(options.BaseAddress!, "internal/v1/market/trades/resolve"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.MarketGatewayApiKey);
        message.Content = JsonContent.Create(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var response = await httpClient.SendAsync(message, timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict or
                HttpStatusCode.BadRequest or HttpStatusCode.ServiceUnavailable))
                return new(response.StatusCode, null, "coordinator_market_resolution_rejected");
            var resolution = await response.Content.ReadFromJsonAsync<MarketTradeReservationResponseV1>(timeout.Token);
            if (resolution is null || !resolution.Matches(request))
                return new(response.StatusCode, null, "coordinator_invalid_market_resolution_response");
            return new(response.StatusCode, resolution, null);
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
            return new(null, null, "coordinator_invalid_market_resolution_response");
        }
    }

    public async Task<CoordinatorMarketQuoteReservationResult> ReserveQuoteAsync(
        MarketTradeQuoteReservationRequestV1 request, CancellationToken cancellationToken = default)
    {
        if (!request.IsValid())
            return new(null, null, "invalid_market_trade_quote_reservation");
        if (!options.IsMarketTradeConfigured)
            return new(null, null, "coordinator_market_trade_not_configured");
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(options.BaseAddress!, "internal/v1/market/trades/reserve-quote"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.MarketGatewayApiKey);
        message.Content = JsonContent.Create(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var response = await httpClient.SendAsync(message, timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict or
                HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.ServiceUnavailable))
                return new(response.StatusCode, null, "coordinator_market_reservation_rejected");
            var reservation = await response.Content
                .ReadFromJsonAsync<MarketTradeQuoteReservationResponseV1>(timeout.Token);
            if (reservation is null || !reservation.Matches(request))
                return new(response.StatusCode, null, "coordinator_invalid_market_reservation_response");
            return new(response.StatusCode, reservation, null);
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
            return new(null, null, "coordinator_invalid_market_reservation_response");
        }
    }

    public async Task<CoordinatorMarketSystemsReadResult> ReadSystemsAsync(
        MarketSystemsReadRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsValid())
            return new(null, null, "invalid_market_systems_request");
        if (!options.IsConfigured)
            return new(null, null, "coordinator_not_configured");

        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(options.BaseAddress!, "internal/v1/market/systems/read"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.InternalApiKey);
        message.Content = JsonContent.Create(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var response = await httpClient.SendAsync(message, timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
                return new(response.StatusCode, null, "coordinator_market_systems_rejected");
            var systems = await response.Content.ReadFromJsonAsync<MarketSystemsReadResponseV1>(timeout.Token);
            if (systems is null || !systems.IsValidFor(request))
                return new(response.StatusCode, null, "coordinator_invalid_market_systems_response");
            return new(response.StatusCode, systems, null);
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
            return new(null, null, "coordinator_invalid_market_systems_response");
        }
    }

    public async Task<CoordinatorMarketGoodReadResult> ReadGoodAsync(
        MarketGoodReadRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsValid())
            return new(null, null, "invalid_market_good_request");
        if (!options.IsConfigured)
            return new(null, null, "coordinator_not_configured");

        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(options.BaseAddress!, "internal/v1/market/goods/read"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.InternalApiKey);
        message.Content = JsonContent.Create(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var response = await httpClient.SendAsync(message, timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NotFound or HttpStatusCode.Conflict))
                return new(response.StatusCode, null, "coordinator_market_good_rejected");
            var result = await response.Content.ReadFromJsonAsync<MarketGoodReadResponseV1>(timeout.Token);
            if (result is null || !result.IsValidFor(request))
                return new(response.StatusCode, null, "coordinator_invalid_market_good_response");
            return new(response.StatusCode, result, null);
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
            return new(null, null, "coordinator_invalid_market_good_response");
        }
    }

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
