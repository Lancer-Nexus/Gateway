using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record CoordinatorTransferEnvelope(
    TransferPrepared Decision,
    TransferState State,
    string? TargetEndpoint,
    bool Duplicate);

public sealed record CoordinatorTransferCallResult(
    HttpStatusCode? StatusCode,
    CoordinatorTransferEnvelope? Envelope,
    string? Error)
{
    public bool IsAvailable => StatusCode is not null;
}

public sealed record CoordinatorTransferStateResult(
    HttpStatusCode? StatusCode,
    CoordinatorTransferOutcome? Outcome,
    string? Error)
{
    public bool IsAvailable => StatusCode is not null;
}

public sealed record CoordinatorTransferOutcome(bool Accepted, string ReasonCode, TransferState State, bool Duplicate);

public sealed class CoordinatorTransferClient(HttpClient httpClient, CoordinatorGatewayOptions options)
{
    public Task<CoordinatorTransferCallResult> PrepareAsync(
        TransferPrepareRequest request,
        CancellationToken cancellationToken = default) =>
        SendTransferAsync(HttpMethod.Post, "internal/v1/transfers/prepare", request, request.TransferId, cancellationToken);

    public Task<CoordinatorTransferStateResult> MarkSourceFrozenAsync(
        Guid transferId, CancellationToken cancellationToken = default) =>
        SendStateAsync($"internal/v1/transfers/{transferId:D}/source-frozen", transferId, cancellationToken);

    public Task<CoordinatorTransferStateResult> MarkTargetAcceptedAsync(
        Guid transferId, CancellationToken cancellationToken = default) =>
        SendStateAsync($"internal/v1/transfers/{transferId:D}/target-accepted", transferId, cancellationToken);

    public Task<CoordinatorTransferStateResult> CommitAsync(
        Guid transferId, long leaseVersion, CancellationToken cancellationToken = default) =>
        SendStateAsync($"internal/v1/transfers/{transferId:D}/commit/{leaseVersion}", transferId, cancellationToken);

    public Task<CoordinatorTransferStateResult> MarkSourceReleasedAsync(
        Guid transferId, CancellationToken cancellationToken = default) =>
        SendStateAsync($"internal/v1/transfers/{transferId:D}/source-released", transferId, cancellationToken);

    public Task<CoordinatorTransferStateResult> AbortAsync(
        TransferAbort request, CancellationToken cancellationToken = default) =>
        SendStateAsync("internal/v1/transfers/abort", request.TransferId, cancellationToken, request);

    private async Task<CoordinatorTransferCallResult> SendTransferAsync(
        HttpMethod method, string path, TransferPrepareRequest request, Guid transferId,
        CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
            return new CoordinatorTransferCallResult(null, null, "coordinator_not_configured");
        using var message = CreateRequest(method, path);
        message.Content = JsonContent.Create(request);
        var send = await SendAsync(message, cancellationToken);
        using var response = send.Response;
        if (response is null)
            return new CoordinatorTransferCallResult(null, null, send.Error);
        var envelope = await response.Content.ReadFromJsonAsync<CoordinatorTransferEnvelope>(cancellationToken);
        if (envelope is null)
            return new CoordinatorTransferCallResult(response.StatusCode, null, "coordinator_invalid_response");
        if (envelope.Decision.TransferId != transferId)
            return new CoordinatorTransferCallResult(response.StatusCode, null, "coordinator_transfer_id_mismatch");
        return new CoordinatorTransferCallResult(response.StatusCode, envelope, null);
    }

    private async Task<CoordinatorTransferStateResult> SendStateAsync(
        string path, Guid transferId, CancellationToken cancellationToken, object? body = null)
    {
        if (transferId == Guid.Empty)
            return new CoordinatorTransferStateResult(null, null, "invalid_transfer_id");
        if (!options.IsConfigured)
            return new CoordinatorTransferStateResult(null, null, "coordinator_not_configured");
        using var message = CreateRequest(HttpMethod.Post, path);
        if (body is not null)
            message.Content = JsonContent.Create(body);
        var send = await SendAsync(message, cancellationToken);
        using var response = send.Response;
        if (response is null)
            return new CoordinatorTransferStateResult(null, null, send.Error);
        var outcome = await response.Content.ReadFromJsonAsync<CoordinatorTransferOutcome>(cancellationToken);
        return outcome is null
            ? new CoordinatorTransferStateResult(response.StatusCode, null, "coordinator_invalid_response")
            : new CoordinatorTransferStateResult(response.StatusCode, outcome, null);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(options.BaseAddress!, path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.InternalApiKey);
        return message;
    }

    private async Task<(HttpResponseMessage? Response, string? Error)> SendAsync(
        HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            return (await httpClient.SendAsync(message, timeout.Token), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "coordinator_timeout");
        }
        catch (HttpRequestException)
        {
            return (null, "coordinator_unavailable");
        }
    }
}
