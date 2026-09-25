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
public sealed record CoordinatorTransferSnapshot(Guid TransferId, TransferPrepareRequest Request,
    TransferState State, DateTime ExpiresUtc, long LeaseVersion);
public sealed record CoordinatorTransferLookupResult(HttpStatusCode? StatusCode,
    CoordinatorTransferSnapshot? Transfer, string? Error)
{
    public bool IsAvailable => StatusCode is not null;
}

public interface ICoordinatorTransferClient
{
    Task<CoordinatorTransferLookupResult> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    Task<CoordinatorTransferCallResult> PrepareAsync(TransferPrepareRequest request,
        CancellationToken cancellationToken = default);
    Task<CoordinatorTransferStateResult> MarkSourceFrozenAsync(Guid transferId,
        CancellationToken cancellationToken = default);
    Task<CoordinatorTransferStateResult> MarkTargetAcceptedAsync(Guid transferId,
        CancellationToken cancellationToken = default);
    Task<CoordinatorTransferStateResult> CommitAsync(Guid transferId, long leaseVersion,
        CancellationToken cancellationToken = default);
    Task<CoordinatorTransferStateResult> MarkSourceReleasedAsync(Guid transferId,
        CancellationToken cancellationToken = default);
    Task<CoordinatorTransferStateResult> AbortAsync(TransferAbort request,
        CancellationToken cancellationToken = default);
}

public sealed class CoordinatorTransferClient(HttpClient httpClient, CoordinatorGatewayOptions options)
    : ICoordinatorTransferClient
{
    public async Task<CoordinatorTransferLookupResult> GetTransferAsync(Guid transferId,
        CancellationToken cancellationToken = default)
    {
        if (transferId == Guid.Empty)
            return new(null, null, "invalid_transfer_id");
        if (!options.IsConfigured)
            return new(null, null, "coordinator_not_configured");
        using var message = CreateRequest(HttpMethod.Get, $"internal/v1/transfers/{transferId:D}");
        var send = await SendAsync(message, cancellationToken);
        using var response = send.Response;
        if (response is null)
            return new(null, null, send.Error);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(response.StatusCode, null, null);
        if (!response.IsSuccessStatusCode)
            return new(response.StatusCode, null, "coordinator_lookup_rejected");
        var transfer = await response.Content.ReadFromJsonAsync<CoordinatorTransferSnapshot>(cancellationToken);
        if (transfer is null || transfer.TransferId != transferId || transfer.Request is null ||
            transfer.Request.TransferId != transferId)
            return new(response.StatusCode, null, "coordinator_invalid_response");
        return new(response.StatusCode, transfer, null);
    }

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
