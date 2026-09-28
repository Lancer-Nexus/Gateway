using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace LancerNexus.Gateway;

// JSON wire shapes deliberately mirror Administration's closed PermissionMutation contract.
public enum GatewayPermissionMutationKind { UpsertGroup, DeleteGroup, AssignGroup, UnassignGroup, SetUserNode, RemoveUserNode, PromoteGroup, DemoteGroup }
public enum GatewayPermissionPatternKind { Exact, Wildcard, Regex }
public sealed record GatewayPermissionNode(string Pattern, bool Allowed, GatewayPermissionPatternKind Kind = GatewayPermissionPatternKind.Wildcard,
    string? InstanceId = null, string? SystemId = null, DateTimeOffset? ExpiresUtc = null);
public sealed record GatewayPermissionMutation(GatewayPermissionMutationKind Kind, string? GroupName = null,
    string? ParentGroup = null, string[]? Parents = null, int Rank = 0, string? Ladder = null,
    string? Prefix = null, string? Suffix = null, Guid? AccountId = null, int Position = 0,
    string? Pattern = null, GatewayPermissionPatternKind PatternKind = GatewayPermissionPatternKind.Wildcard,
    bool Allowed = true, string? SystemId = null, string? InstanceId = null,
    DateTimeOffset? ExpiresUtc = null, GatewayPermissionNode[]? Nodes = null);
public sealed record GatewayPermissionMutationRequest(Guid CorrelationId, string IdempotencyKey, GatewayPermissionMutation Mutation);
public sealed record GamePermissionMutationRequest(Guid AccountId, Guid SessionId, long CharacterId, Guid TransferId,
    GatewayPermissionMutationRequest Request);
public sealed record GamePermissionCheckRequest(Guid AccountId, Guid SessionId, long CharacterId, Guid TransferId,
    string Permission, string? SystemId = null);
public sealed record AdminPermissionMutationRequest(Guid ActorAccountId, string Source, Guid CorrelationId,
    string IdempotencyKey, GatewayPermissionMutation Mutation);
public sealed record GatewayPermissionMutationResult(string Status, long? Revision = null);

public sealed class AdminPermissionRelay(HttpClient http, IConfiguration configuration,
    IAccountRepository accounts, ICoordinatorTransferClient coordinator, TimeProvider clock)
{
    public async Task<bool?> CheckFromGameAsync(GamePermissionCheckRequest request, string instanceId,
        AdminPermissionSnapshotRelay permissions, CancellationToken ct)
    {
        if (request.AccountId == Guid.Empty || request.CharacterId <= 0 ||
            request.Permission is not { Length: > 0 and <= 256 } ||
            (request.SessionId == Guid.Empty && request.TransferId == Guid.Empty)) return false;
        var sessionId = request.SessionId;
        if (sessionId == Guid.Empty)
        {
            var lookup = await coordinator.GetTransferAsync(request.TransferId, ct);
            var transfer = lookup.Transfer;
            if (!lookup.IsAvailable || transfer is null || transfer.Request.CharacterId != request.CharacterId ||
                transfer.Request.TargetInstanceId != instanceId ||
                transfer.State is not (LancerNexus.Protocol.TransferState.Committed or LancerNexus.Protocol.TransferState.SourceReleased))
                return false;
            sessionId = transfer.Request.SessionId;
        }
        var lease = await accounts.FindActiveCharacterLeaseAsync(request.AccountId, sessionId,
            request.CharacterId, clock.GetUtcNow().UtcDateTime, ct);
        if (lease is null || !string.Equals(lease.InstanceId, instanceId, StringComparison.Ordinal)) return false;
        return await permissions.HasPermissionAsync(request.AccountId, request.Permission, instanceId, request.SystemId, ct);
    }

    public async Task<GatewayPermissionMutationResult> FromGameAsync(GamePermissionMutationRequest request,
        string instanceId, CancellationToken ct)
    {
        if (!Valid(request.Request) || request.AccountId == Guid.Empty || request.CharacterId <= 0) return new("invalid");
        var sessionId = request.SessionId;
        if (sessionId == Guid.Empty && request.TransferId != Guid.Empty)
        {
            var lookup = await coordinator.GetTransferAsync(request.TransferId, ct);
            var transfer = lookup.Transfer;
            if (!lookup.IsAvailable || transfer is null || transfer.Request.CharacterId != request.CharacterId ||
                transfer.Request.TargetInstanceId != instanceId ||
                transfer.State is not (LancerNexus.Protocol.TransferState.Committed or LancerNexus.Protocol.TransferState.SourceReleased))
                return new("denied");
            sessionId = transfer.Request.SessionId;
        }
        var lease = await accounts.FindActiveCharacterLeaseAsync(request.AccountId, sessionId,
            request.CharacterId, clock.GetUtcNow().UtcDateTime, ct);
        if (lease is null || !string.Equals(lease.InstanceId, instanceId, StringComparison.Ordinal)) return new("denied");
        return await SendAsync(request.AccountId, "ingame:" + instanceId, request.Request, ct);
    }

    public Task<GatewayPermissionMutationResult> FromRestAsync(Guid actor, GatewayPermissionMutationRequest request,
        CancellationToken ct) => SendAsync(actor, "rest", request, ct);

    private async Task<GatewayPermissionMutationResult> SendAsync(Guid actor, string source,
        GatewayPermissionMutationRequest request, CancellationToken ct)
    {
        if (!Valid(request)) return new("invalid");
        if (!Uri.TryCreate(configuration["Gateway:AdministrationBaseUrl"], UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            configuration["Gateway:AdministrationApiKey"] is not { Length: >= 32 } key) return new("unavailable");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "/api/v1/admin/permissions/mutations"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = JsonContent.Create(new AdminPermissionMutationRequest(actor, source,
            request.CorrelationId, request.IdempotencyKey, request.Mutation));
        try
        {
            using var response = await http.SendAsync(message, ct);
            var result = await response.Content.ReadFromJsonAsync<GatewayPermissionMutationResult>(ct);
            return result is { Status: "sync_pending" or "ok" or "invalid" or "conflict" or "denied" or "unavailable" }
                ? result : new("unavailable");
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        { return new("unavailable"); }
    }

    private static bool Valid(GatewayPermissionMutationRequest request) => request.CorrelationId != Guid.Empty &&
        request.IdempotencyKey is { Length: > 0 and <= 96 } && request.Mutation is not null &&
        Enum.IsDefined(request.Mutation.Kind);
}

public sealed class AdminPermissionSnapshotRelay(HttpClient http, IConfiguration configuration)
{
    public async Task<bool?> HasPermissionAsync(Guid actor, string permission, string? instanceId,
        string? systemId, CancellationToken ct)
    {
        if (!Uri.TryCreate(configuration["Gateway:AdministrationBaseUrl"], UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            configuration["Gateway:AdministrationApiKey"] is not { Length: >= 32 } key) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "/api/v1/admin/permissions/check"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new { AccountId = actor, Permission = permission, InstanceId = instanceId, SystemId = systemId });
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<PermissionCheckResult>(ct);
            return result?.Allowed;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException) { return null; }
    }

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(configuration["Gateway:AdministrationBaseUrl"], UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            configuration["Gateway:AdministrationApiKey"] is not { Length: >= 32 } key) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/api/v1/admin/permissions/snapshot"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct);
            return json.Length <= 8 * 1024 * 1024 ? json : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { return null; }
    }

    public async Task<bool> AcknowledgeAsync(LancerNexus.Protocol.PermissionRevisionAcknowledged ack, CancellationToken ct)
    {
        if (!Uri.TryCreate(configuration["Gateway:AdministrationBaseUrl"], UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            configuration["Gateway:AdministrationApiKey"] is not { Length: >= 32 } key) return false;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "/api/v1/admin/permissions/ack"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(ack);
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { return false; }
    }

    private sealed record PermissionCheckResult(bool Allowed);
}
