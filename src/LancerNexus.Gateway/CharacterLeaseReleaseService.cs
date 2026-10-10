using System.Security.Cryptography;
using LancerNexus.Protocol;
using MySqlConnector;

namespace LancerNexus.Gateway;

public sealed class CharacterLeaseReleaseService(
    ICharacterLeaseAcquisitionStore leases,
    TimeProvider timeProvider)
{
    public async Task<CharacterLeaseReleaseResultV1> ReleaseAsync(CharacterLeaseReleaseRequestV1 request,
        string authenticatedInstanceId, CancellationToken cancellationToken = default)
    {
        if (!request.IsValid() || !AdminQuery.ValidIdentifier(authenticatedInstanceId))
            return Reject(request.CorrelationId, "lease_release_request_invalid");

        byte[] token;
        try
        {
            token = Convert.FromBase64String(request.LeaseToken.Replace('-', '+').Replace('_', '/') + "=");
        }
        catch (FormatException)
        {
            return Reject(request.CorrelationId, "lease_token_invalid");
        }

        try
        {
            var result = await leases.ReleaseCharacterLeaseAsync(request.CharacterId, request.SessionId,
                authenticatedInstanceId, SHA256.HashData(token), request.LeaseVersion,
                timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            return new CharacterLeaseReleaseResultV1
            {
                CorrelationId = request.CorrelationId,
                Released = result.Released,
                ReasonCode = result.ReasonCode,
                LeaseVersion = result.LeaseVersion
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or MySqlException)
        {
            return Reject(request.CorrelationId, "lease_persistence_unavailable");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    private static CharacterLeaseReleaseResultV1 Reject(Guid correlationId, string reasonCode) => new()
    {
        CorrelationId = correlationId,
        Released = false,
        ReasonCode = reasonCode
    };
}
