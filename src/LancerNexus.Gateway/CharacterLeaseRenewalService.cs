using System.Security.Cryptography;
using LancerNexus.Protocol;
using MySqlConnector;

namespace LancerNexus.Gateway;

public sealed class CharacterLeaseRenewalService(
    IAccountRepository accounts,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    public async Task<CharacterLeaseRenewResultV1> RenewAsync(CharacterLeaseRenewRequestV1 request,
        string instanceId, CancellationToken cancellationToken = default)
    {
        if (!request.IsValid() || !AdminQuery.ValidIdentifier(instanceId))
            return Reject(request.CorrelationId, "lease_request_invalid");
        var lifetimeSeconds = configuration.GetValue("Gateway:CharacterLeaseLifetimeSeconds", 900);
        if (lifetimeSeconds is < 60 or > 86400)
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

        var tokenHash = SHA256.HashData(token);
        CryptographicOperations.ZeroMemory(token);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expires = now.AddSeconds(lifetimeSeconds);
        try
        {
            var renewal = await accounts.RenewCharacterLeaseAsync(request.CharacterId, instanceId, tokenHash,
                request.LeaseVersion, expires, now, cancellationToken);
            return new CharacterLeaseRenewResultV1
            {
                CorrelationId = request.CorrelationId,
                Accepted = renewal.Accepted,
                ReasonCode = renewal.ReasonCode,
                LeaseVersion = renewal.LeaseVersion,
                ValidUntilUtc = renewal.ValidUntilUtc is { } validUntil
                    ? new DateTimeOffset(DateTime.SpecifyKind(validUntil, DateTimeKind.Utc))
                    : null
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or MySqlException)
        {
            return Reject(request.CorrelationId, "character_lease_persistence_unavailable");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenHash);
        }
    }

    private static CharacterLeaseRenewResultV1 Reject(Guid correlationId, string reasonCode) => new()
    {
        CorrelationId = correlationId,
        Accepted = false,
        ReasonCode = reasonCode
    };
}
