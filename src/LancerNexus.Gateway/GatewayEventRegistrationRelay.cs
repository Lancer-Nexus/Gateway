using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using LancerNexus.Protocol;
using MySqlConnector;

namespace LancerNexus.Gateway;

public sealed record GatewayEventRegistrationSubmission(Guid RequestId, Guid CorrelationId,
    long CharacterId, string IdempotencyKey);

public sealed class GatewayEventRegistrationRelay(HttpClient http, IConfiguration configuration,
    IAccountRepository accounts, TimeProvider clock)
{
    public async Task<EventRegistrationDecisionV1> FromRestAsync(Guid accountId, Guid sessionId,
        string eventId, GatewayEventRegistrationSubmission submission, CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty || sessionId == Guid.Empty || submission is null ||
            submission.RequestId == Guid.Empty || submission.CorrelationId == Guid.Empty ||
            submission.CharacterId <= 0 || !AdminQuery.ValidIdentifier(eventId))
            return Reject(submission, EventRegistrationStatus.Invalid, "invalid_registration_request");

        var now = clock.GetUtcNow().UtcDateTime;
        CharacterLeaseRecord? lease;
        try
        {
            lease = await accounts.FindActiveCharacterLeaseAsync(accountId, sessionId,
                submission.CharacterId, now, cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or MySqlException or
                                      OperationCanceledException)
        {
            return Reject(submission, EventRegistrationStatus.Unavailable, "storage_unavailable");
        }

        if (lease is null || lease.LeaseVersion <= 0 || lease.ValidUntilUtc <= now ||
            !AdminQuery.ValidIdentifier(lease.InstanceId))
            return Reject(submission, EventRegistrationStatus.Denied, "session_lease_invalid");

        var request = new EventRegistrationRequestV1
        {
            RequestId = submission.RequestId,
            CorrelationId = submission.CorrelationId,
            AccountId = accountId,
            SessionId = sessionId,
            CharacterId = submission.CharacterId,
            LeaseVersion = lease.LeaseVersion,
            EventId = eventId,
            IdempotencyKey = submission.IdempotencyKey
        };
        if (!request.IsValid())
            return Reject(submission, EventRegistrationStatus.Invalid, "invalid_registration_request");

        if (!Uri.TryCreate(configuration["Gateway:EventsBaseUrl"], UriKind.Absolute, out var baseAddress) ||
            baseAddress.Scheme != Uri.UriSchemeHttps ||
            configuration["Gateway:EventsApiKey"] is not { Length: > 0 } apiKey ||
            Encoding.UTF8.GetByteCount(apiKey) < 32)
            return Reject(submission, EventRegistrationStatus.Unavailable, "events_unavailable");

        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(baseAddress, $"/internal/v1/events/{Uri.EscapeDataString(eventId)}/registrations"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(request);
        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            var decision = await response.Content.ReadFromJsonAsync<EventRegistrationDecisionV1>(cancellationToken);
            if (decision is null || decision.RequestId != request.RequestId ||
                decision.CorrelationId != request.CorrelationId || !decision.IsValid() ||
                response.StatusCode != ExpectedStatusCode(decision.Status))
                return Reject(submission, EventRegistrationStatus.Unavailable, "events_invalid_response");
            return decision;
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or
                                      OperationCanceledException)
        {
            return Reject(submission, EventRegistrationStatus.Unavailable, "events_unavailable");
        }
    }

    public static HttpStatusCode ExpectedStatusCode(EventRegistrationStatus status) => status switch
    {
        EventRegistrationStatus.Accepted or EventRegistrationStatus.AlreadyRegistered => HttpStatusCode.OK,
        EventRegistrationStatus.Denied => HttpStatusCode.Forbidden,
        EventRegistrationStatus.EventNotFound => HttpStatusCode.NotFound,
        EventRegistrationStatus.RegistrationClosed or EventRegistrationStatus.CapacityFull or
            EventRegistrationStatus.Conflict => HttpStatusCode.Conflict,
        EventRegistrationStatus.Invalid => HttpStatusCode.BadRequest,
        EventRegistrationStatus.Unavailable => HttpStatusCode.ServiceUnavailable,
        _ => 0
    };

    private static EventRegistrationDecisionV1 Reject(GatewayEventRegistrationSubmission? request,
        EventRegistrationStatus status, string errorCode) => new()
    {
        RequestId = request?.RequestId ?? Guid.Empty,
        CorrelationId = request?.CorrelationId ?? Guid.Empty,
        Status = status,
        ErrorCode = errorCode
    };
}
