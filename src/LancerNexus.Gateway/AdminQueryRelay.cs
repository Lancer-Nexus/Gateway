using System.Net.Http.Headers;
using System.Net.Http.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed class AdminQueryRelay(HttpClient http, IConfiguration configuration,
    IAccountRepository accounts, ICoordinatorTransferClient coordinator, TimeProvider clock)
{
    public async Task<AdminQueryResponse> FromGameAsync(GameAdminQueryRequest request, string instanceId, CancellationToken ct)
    {
        if (request.Query is null || !request.Query.IsValid() || request.AccountId == Guid.Empty || request.CharacterId <= 0)
            return Reject(request.Query?.CorrelationId ?? Guid.Empty, "invalid");
        var sessionId = request.SessionId;
        if (sessionId == Guid.Empty && request.TransferId != Guid.Empty)
        {
            var lookup = await coordinator.GetTransferAsync(request.TransferId, ct);
            if (!lookup.IsAvailable) return Reject(request.Query.CorrelationId, "unavailable");
            var transfer = lookup.Transfer;
            if (transfer is null ||
                transfer.Request.CharacterId != request.CharacterId || transfer.Request.TargetInstanceId != instanceId ||
                transfer.State is not (TransferState.Committed or TransferState.SourceReleased))
                return Reject(request.Query.CorrelationId, "denied");
            sessionId = transfer.Request.SessionId;
        }
        var lease = await accounts.FindActiveCharacterLeaseAsync(request.AccountId, sessionId,
            request.CharacterId, clock.GetUtcNow().UtcDateTime, ct);
        if (lease is null || !string.Equals(lease.InstanceId, instanceId, StringComparison.Ordinal))
            return Reject(request.Query.CorrelationId, "denied");
        return await SendAsync(request.AccountId, "ingame:" + instanceId, request.Query, ct);
    }

    public async Task<AdminQueryResponse> SendAsync(Guid actor, string source, AdminQuery query, CancellationToken ct)
    {
        if (!Uri.TryCreate(configuration["Gateway:AdministrationBaseUrl"], UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || configuration["Gateway:AdministrationApiKey"] is not { Length: >= 32 } key)
            return Reject(query.CorrelationId, "unavailable");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "/api/v1/admin/commands"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = JsonContent.Create(new AuthorizedAdminQueryRequest { AccountId = actor, Source = source, Query = query });
        try
        {
            using var response = await http.SendAsync(message, ct);
            var result = await response.Content.ReadFromJsonAsync<AdminQueryResponse>(ct);
            if (result is null || result.CorrelationId != query.CorrelationId || result.Lines is null ||
                result.Lines.Length > 65 || result.Lines.Any(line => line is null || line.Length > 8192))
                return Reject(query.CorrelationId, "unavailable");
            return result;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        { return Reject(query.CorrelationId, "unavailable"); }
    }

    private static AdminQueryResponse Reject(Guid id, string status) => new()
    { CorrelationId = id, Status = status, Lines = [status == "denied" ?
        "Keine gültige Sitzung/Charakterzuordnung für diesen Administrationsbefehl." : "Administration derzeit nicht verfügbar oder Anfrage ungültig."] };
}
