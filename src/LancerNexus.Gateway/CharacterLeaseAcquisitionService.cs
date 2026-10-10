using System.Security.Cryptography;
using LancerNexus.Protocol;
using MySqlConnector;

namespace LancerNexus.Gateway;

public sealed class CharacterLeaseAcquisitionService(
    JoinTicketCodec joinTickets,
    ICharacterLeaseAcquisitionStore leases,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    public async Task<CharacterLeaseAcquireResultV1> AcquireAsync(CharacterLeaseAcquireRequestV1 request,
        string authenticatedInstanceId, CancellationToken cancellationToken = default)
    {
        if (!request.IsValid() || !AdminQuery.ValidIdentifier(authenticatedInstanceId))
            return Reject(request.CorrelationId, "lease_request_invalid");

        var ticket = joinTickets.Validate(request.JoinTicket, timeProvider.GetUtcNow().UtcDateTime);
        if (!ticket.Accepted || ticket.Claims is null)
            return Reject(request.CorrelationId, "join_ticket_invalid");
        var claims = ticket.Claims;
        if (!string.Equals(claims.InstanceId, authenticatedInstanceId, StringComparison.Ordinal))
            return Reject(request.CorrelationId, "join_ticket_instance_mismatch");
        if (claims.CharacterId is { } ticketCharacterId && ticketCharacterId != request.CharacterId)
            return Reject(request.CorrelationId, "join_ticket_character_mismatch");

        var leaseLifetimeSeconds = configuration.GetValue("Gateway:CharacterLeaseLifetimeSeconds", 900);
        if (leaseLifetimeSeconds is < 60 or > 86400)
            return Reject(request.CorrelationId, "lease_lifetime_configuration_invalid");

        byte[] token;
        try
        {
            token = Convert.FromBase64String(request.LeaseToken.Replace('-', '+').Replace('_', '/') + "=");
        }
        catch (FormatException)
        {
            return Reject(request.CorrelationId, "lease_token_invalid");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        CharacterLeaseAcquireResult acquired;
        try
        {
            acquired = await leases.AcquireCharacterLeaseAsync(claims.AccountId, claims.SessionId, request.CharacterId,
                authenticatedInstanceId, SHA256.HashData(token), now.AddSeconds(leaseLifetimeSeconds), now,
                cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or MySqlException)
        {
            return Reject(request.CorrelationId, "character_lease_persistence_unavailable");
        }

        return new CharacterLeaseAcquireResultV1
        {
            CorrelationId = request.CorrelationId,
            Accepted = acquired.Accepted,
            ReasonCode = acquired.ReasonCode,
            LeaseVersion = acquired.LeaseVersion,
            ValidUntilUtc = acquired.ValidUntilUtc is { } expiry
                ? new DateTimeOffset(DateTime.SpecifyKind(expiry, DateTimeKind.Utc))
                : null
        };
    }

    private static CharacterLeaseAcquireResultV1 Reject(Guid correlationId, string reasonCode) => new()
    {
        CorrelationId = correlationId,
        Accepted = false,
        ReasonCode = reasonCode
    };
}
