using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using MySqlConnector;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record GameAdminActionRequest(Guid AccountId, Guid SessionId, long CharacterId,
    Guid TransferId, AdminActionRequestV1 Action);

// This wire shape mirrors Administration's internal request. Gateway owns the actor and
// session fields; clients cannot set target context or claim step-up through this API.
public sealed record GatewayAuthorizedAdminActionSubmission(Guid GatewaySessionId,
    AdminActionRequestV1 Action, GatewayAdminActionTargetContext? TargetContext, bool StepUpVerified);

public sealed record GatewayAdminActionTargetContext(string TargetId, string? CurrentInstanceId,
    string? CurrentSystemId, string? DestinationInstanceId = null, string? DestinationSystemId = null);

public sealed class AdminActionRelay(HttpClient http, IConfiguration configuration,
    IAccountRepository accounts, ICoordinatorTransferClient coordinator, TimeProvider clock)
{
    public async Task<AdminActionResultV1> FromGameAsync(GameAdminActionRequest request,
        string instanceId, CancellationToken ct)
    {
        if (request is null || request.Action is null || request.AccountId == Guid.Empty ||
            request.CharacterId <= 0 || !AdminQuery.ValidIdentifier(instanceId))
            return Reject(request?.Action, AdminActionStatus.Invalid, "invalid_action");
        var action = request.Action with { ActorAccountId = request.AccountId, Source = "ingame:" + instanceId };
        if (!action.IsValid()) return Reject(action, AdminActionStatus.Invalid, "invalid_action");

        var sessionId = request.SessionId;
        if (sessionId == Guid.Empty && request.TransferId != Guid.Empty)
        {
            var lookup = await coordinator.GetTransferAsync(request.TransferId, ct);
            var transfer = lookup.Transfer;
            if (!lookup.IsAvailable)
                return Reject(action, AdminActionStatus.Unavailable, "coordinator_unavailable");
            if (transfer is null || transfer.Request.CharacterId != request.CharacterId ||
                transfer.Request.TargetInstanceId != instanceId ||
                transfer.State is not (TransferState.Committed or TransferState.SourceReleased))
                return Reject(action, AdminActionStatus.Denied, "session_lease_invalid");
            sessionId = transfer.Request.SessionId;
        }

        var lease = await accounts.FindActiveCharacterLeaseAsync(request.AccountId, sessionId,
            request.CharacterId, clock.GetUtcNow().UtcDateTime, ct);
        if (lease is null || !string.Equals(lease.InstanceId, instanceId, StringComparison.Ordinal))
            return Reject(action, AdminActionStatus.Denied, "session_lease_invalid");

        return await SendAsync(action, sessionId, ct);
    }

    public Task<AdminActionResultV1> FromRestAsync(Guid actorAccountId, Guid sessionId,
        AdminActionRequestV1 request, CancellationToken ct)
    {
        if (actorAccountId == Guid.Empty || sessionId == Guid.Empty || request is null)
            return Task.FromResult(Reject(request, AdminActionStatus.Invalid, "invalid_action"));
        var action = request with { ActorAccountId = actorAccountId, Source = "rest" };
        return !action.IsValid()
            ? Task.FromResult(Reject(action, AdminActionStatus.Invalid, "invalid_action"))
            : SendAsync(action, sessionId, ct);
    }

    private async Task<AdminActionResultV1> SendAsync(AdminActionRequestV1 action, Guid sessionId,
        CancellationToken ct)
    {
        GatewayAdminActionTargetContext? targetContext = null;
        if (action.TargetKind == AdminActionTargetKind.Player && action.TargetId is not null)
        {
            targetContext = await ResolveTargetContextAsync(action, ct);
            if (targetContext is null)
                return Reject(action, AdminActionStatus.Unavailable, "target_context_unavailable");
        }
        if (!Uri.TryCreate(configuration["Gateway:AdministrationBaseUrl"], UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || configuration["Gateway:AdministrationApiKey"] is not { Length: >= 32 } key)
            return Reject(action, AdminActionStatus.Unavailable, "administration_unavailable");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "/api/v1/admin/actions"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = JsonContent.Create(new GatewayAuthorizedAdminActionSubmission(sessionId,
            action, targetContext, false));
        try
        {
            using var response = await http.SendAsync(message, ct);
            var result = await response.Content.ReadFromJsonAsync<AdminActionResultV1>(ct);
            if (result is null || result.CommandId != action.CommandId ||
                result.CorrelationId != action.CorrelationId || !result.IsValid() ||
                response.StatusCode != ExpectedStatusCode(result.Status))
                return Reject(action, AdminActionStatus.Unavailable, "administration_unavailable");
            return result;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        { return Reject(action, AdminActionStatus.Unavailable, "administration_unavailable"); }
    }

    private async Task<GatewayAdminActionTargetContext?> ResolveTargetContextAsync(
        AdminActionRequestV1 action, CancellationToken ct)
    {
        if (!long.TryParse(action.TargetId, NumberStyles.None, CultureInfo.InvariantCulture, out var characterId) ||
            characterId <= 0)
            return null;
        CharacterLeaseRecord? lease;
        CoordinatorRegistryLookupResult registry;
        try
        {
            lease = await accounts.FindActiveCharacterTargetLeaseAsync(characterId,
                clock.GetUtcNow().UtcDateTime, ct);
            if (lease is null || !AdminQuery.ValidIdentifier(lease.InstanceId)) return null;
            registry = await coordinator.GetRegistryAsync(ct);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or MySqlException or
                                  System.Text.Json.JsonException or OperationCanceledException)
        {
            return null;
        }
        if (!registry.IsAvailable || registry.Instances is null) return null;
        var current = registry.Instances.SingleOrDefault(instance =>
            string.Equals(instance.InstanceId, lease.InstanceId, StringComparison.Ordinal));
        if (current is null || !current.IsAlive || !current.AgentIsAlive) return null;

        string? destinationInstance = null;
        string? destinationSystem = null;
        if (action.Kind is AdminActionKind.Move or AdminActionKind.Transfer)
        {
            var target = registry.Instances.SingleOrDefault(instance =>
                string.Equals(instance.InstanceId, action.TargetInstanceId, StringComparison.Ordinal) &&
                instance.IsAlive && instance.AgentIsAlive && instance.IsReady && !instance.IsDraining);
            if (target is null) return null;
            destinationInstance = target.InstanceId;
            destinationSystem = target.SystemId;
        }
        else if (action.Kind == AdminActionKind.Teleport)
        {
            var target = registry.Instances.FirstOrDefault(instance =>
                instance.IsAlive && instance.AgentIsAlive && instance.IsReady && !instance.IsDraining &&
                (instance.SystemIds is { Length: > 0 } systems
                    ? systems.Contains(action.TargetSystemId!, StringComparer.OrdinalIgnoreCase)
                    : string.Equals(instance.SystemId, action.TargetSystemId, StringComparison.OrdinalIgnoreCase)));
            if (target is null) return null;
            destinationInstance = target.InstanceId;
            destinationSystem = action.TargetSystemId;
        }
        return new GatewayAdminActionTargetContext(action.TargetId!, current.InstanceId,
            current.SystemId, destinationInstance, destinationSystem);
    }

    private static HttpStatusCode ExpectedStatusCode(AdminActionStatus status) => status switch
    {
        AdminActionStatus.Pending => HttpStatusCode.Accepted,
        AdminActionStatus.Completed => HttpStatusCode.OK,
        AdminActionStatus.Denied => HttpStatusCode.Forbidden,
        AdminActionStatus.Invalid => HttpStatusCode.BadRequest,
        AdminActionStatus.NotFound => HttpStatusCode.NotFound,
        AdminActionStatus.Conflict => HttpStatusCode.Conflict,
        AdminActionStatus.Unavailable => HttpStatusCode.ServiceUnavailable,
        _ => 0
    };

    private static AdminActionResultV1 Reject(AdminActionRequestV1? action,
        AdminActionStatus status, string errorCode) => new()
        {
            CommandId = action?.CommandId ?? Guid.NewGuid(),
            CorrelationId = action?.CorrelationId ?? Guid.NewGuid(),
            Status = status,
            ErrorCode = errorCode
        };
}
