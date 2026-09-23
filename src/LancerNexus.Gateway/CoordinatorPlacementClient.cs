using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record CoordinatorGatewayOptions(Uri? BaseAddress, string? InternalApiKey, TimeSpan Timeout)
{
    public static CoordinatorGatewayOptions FromConfiguration(IConfiguration configuration)
    {
        var rawAddress = configuration["Gateway:CoordinatorBaseUrl"];
        Uri? address = Uri.TryCreate(rawAddress, UriKind.Absolute, out var parsed) &&
                       string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? parsed
            : null;
        var timeoutSeconds = configuration.GetValue<int?>("Gateway:RequestTimeoutSeconds") ?? 5;
        if (timeoutSeconds <= 0)
            throw new InvalidOperationException("Gateway:RequestTimeoutSeconds must be positive.");
        return new CoordinatorGatewayOptions(address, configuration["Gateway:CoordinatorApiKey"],
            TimeSpan.FromSeconds(timeoutSeconds));
    }

    public bool IsConfigured => BaseAddress is not null &&
                                 !string.IsNullOrWhiteSpace(InternalApiKey) &&
                                 System.Text.Encoding.UTF8.GetByteCount(InternalApiKey) >= 32;
}

public sealed record CoordinatorPlacementResult(
    HttpStatusCode? StatusCode,
    CoordinatorPlacementEnvelope? Envelope,
    string? Error)
{
    public bool IsAvailable => StatusCode is not null;
}

public sealed record CoordinatorPlacementEnvelope(PlacementDecision Decision, bool Duplicate);

public sealed class CoordinatorPlacementClient(HttpClient httpClient, CoordinatorGatewayOptions options)
{
    public async Task<CoordinatorPlacementResult> PlaceAsync(
        PlacementRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return new CoordinatorPlacementResult(null, null, "coordinator_not_configured");

        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(options.BaseAddress!, "api/v1/placement"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.InternalApiKey);
        message.Content = JsonContent.Create(request);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var response = await httpClient.SendAsync(message, timeout.Token);
            var envelope = await response.Content.ReadFromJsonAsync<CoordinatorPlacementEnvelope>(timeout.Token);
            if (envelope is null)
                return new CoordinatorPlacementResult(response.StatusCode, null, "coordinator_invalid_response");
            return new CoordinatorPlacementResult(response.StatusCode, envelope, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CoordinatorPlacementResult(null, null, "coordinator_timeout");
        }
        catch (HttpRequestException)
        {
            return new CoordinatorPlacementResult(null, null, "coordinator_unavailable");
        }
    }
}
