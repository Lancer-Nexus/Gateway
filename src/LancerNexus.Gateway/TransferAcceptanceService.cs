using System.Security.Cryptography;
using LancerNexus.Protocol;
using MySqlConnector;

namespace LancerNexus.Gateway;

public enum TransferAcceptanceFailure
{
    None,
    Rejected,
    CoordinatorUnavailable,
    CoordinatorInvalidResponse,
    PersistenceUnavailable,
    PersistenceRejected
}

public sealed record TransferAcceptanceResult(
    Guid TransferId,
    long? LeaseVersion,
    TransferAcceptanceFailure Failure,
    string ReasonCode)
{
    public bool Accepted => Failure == TransferAcceptanceFailure.None && LeaseVersion.HasValue;
}

public sealed class TransferAcceptanceService(
    TransferTicketCodec tickets,
    ICoordinatorTransferClient coordinator,
    IAccountRepository accounts,
    TimeProvider timeProvider,
    IConfiguration configuration)
{
    public async Task<TransferAcceptanceResult> AcceptAsync(
        TransferTargetAcceptanceRequest request,
        string authenticatedTargetInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Ticket) || string.IsNullOrWhiteSpace(authenticatedTargetInstanceId))
            return Reject(Guid.Empty, "transfer_acceptance_request_invalid");
        if (!TryDecodeLeaseToken(request.TargetLeaseToken, out var leaseToken))
            return Reject(Guid.Empty, "target_lease_token_invalid");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var validation = tickets.ValidateForTransferRecovery(request.Ticket, now);
        if (!validation.Accepted)
            return Reject(Guid.Empty, validation.ReasonCode);
        var claims = validation.Claims!;
        if (!string.Equals(claims.TargetInstanceId, authenticatedTargetInstanceId, StringComparison.Ordinal))
            return Reject(claims.TransferId, "transfer_ticket_target_mismatch");

        var lookup = await coordinator.GetTransferAsync(claims.TransferId, cancellationToken);
        if (!lookup.IsAvailable)
            return Fail(claims.TransferId, TransferAcceptanceFailure.CoordinatorUnavailable,
                lookup.Error ?? "coordinator_unavailable");
        var transfer = lookup.Transfer;
        if (transfer is null || !Matches(transfer, claims))
            return transfer is null
                ? Reject(claims.TransferId, "transfer_not_found")
                : Fail(claims.TransferId, TransferAcceptanceFailure.CoordinatorInvalidResponse,
                    "coordinator_transfer_mismatch");
        if (transfer.State is not (TransferState.SourceFrozen or TransferState.TargetAccepted or TransferState.Committed))
            return Reject(claims.TransferId, "transfer_not_ready");
        if (transfer.State == TransferState.Committed && transfer.LeaseVersion != claims.LeaseVersion + 1)
            return Reject(claims.TransferId, "transfer_lease_version_mismatch");

        if (transfer.State == TransferState.SourceFrozen)
        {
            var accepted = await coordinator.MarkTargetAcceptedAsync(claims.TransferId, cancellationToken);
            if (!accepted.IsAvailable)
                return Fail(claims.TransferId, TransferAcceptanceFailure.CoordinatorUnavailable,
                    accepted.Error ?? "coordinator_unavailable");
            if (accepted.Outcome is null || !accepted.Outcome.Accepted ||
                accepted.Outcome.State != TransferState.TargetAccepted)
                return Fail(claims.TransferId, TransferAcceptanceFailure.CoordinatorInvalidResponse,
                    "coordinator_target_acceptance_rejected");
        }

        var leaseLifetimeSeconds = configuration.GetValue("Gateway:CharacterLeaseLifetimeSeconds", 900);
        if (leaseLifetimeSeconds is < 60 or > 86400)
            return Fail(claims.TransferId, TransferAcceptanceFailure.PersistenceUnavailable,
                "lease_lifetime_configuration_invalid");
        CharacterLeaseTransferResult lease;
        try
        {
            lease = await accounts.CommitCharacterLeaseTransferAsync(
                claims.TransferId, claims.SessionId, claims.CharacterId, claims.SourceInstanceId,
                claims.TargetInstanceId, claims.LeaseVersion, SHA256.HashData(leaseToken),
                now.AddSeconds(leaseLifetimeSeconds), now, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or MySqlException)
        {
            return Fail(claims.TransferId, TransferAcceptanceFailure.PersistenceUnavailable,
                "character_lease_persistence_unavailable");
        }

        if (!lease.Accepted || lease.LeaseVersion is null)
            return Fail(claims.TransferId, TransferAcceptanceFailure.PersistenceRejected, lease.ReasonCode);
        var committedVersion = lease.LeaseVersion.Value;
        if (committedVersion != claims.LeaseVersion + 1)
            return Fail(claims.TransferId, TransferAcceptanceFailure.PersistenceRejected,
                "committed_lease_version_mismatch");

        var committed = await coordinator.CommitAsync(claims.TransferId, committedVersion, cancellationToken);
        if (!committed.IsAvailable)
            return Fail(claims.TransferId, TransferAcceptanceFailure.CoordinatorUnavailable,
                committed.Error ?? "coordinator_unavailable");
        if (committed.Outcome is null || !committed.Outcome.Accepted ||
            committed.Outcome.State != TransferState.Committed)
            return Fail(claims.TransferId, TransferAcceptanceFailure.CoordinatorInvalidResponse,
                "coordinator_commit_rejected");

        return new TransferAcceptanceResult(claims.TransferId, committedVersion,
            TransferAcceptanceFailure.None, lease.ReasonCode);
    }

    private static bool Matches(CoordinatorTransferSnapshot transfer, TransferTicketClaims claims) =>
        transfer.TransferId == claims.TransferId && transfer.Request.TransferId == claims.TransferId &&
        transfer.Request.SessionId == claims.SessionId && transfer.Request.CharacterId == claims.CharacterId &&
        transfer.Request.ExpiresUtc == claims.ExpiresAtUtc && transfer.ExpiresUtc == claims.ExpiresAtUtc &&
        string.Equals(transfer.Request.SourceInstanceId, claims.SourceInstanceId, StringComparison.Ordinal) &&
        string.Equals(transfer.Request.TargetInstanceId, claims.TargetInstanceId, StringComparison.Ordinal) &&
        string.Equals(transfer.Request.TargetSystemId, claims.TargetSystemId, StringComparison.Ordinal);

    private static bool TryDecodeLeaseToken(string token, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64)
            return false;
        try
        {
            var decoded = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') +
                new string('=', (4 - token.Length % 4) % 4));
            if (decoded.Length != 32 || !string.Equals(EncodeBase64Url(decoded), token, StringComparison.Ordinal))
                return false;
            bytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static TransferAcceptanceResult Reject(Guid id, string reason) =>
        Fail(id, TransferAcceptanceFailure.Rejected, reason);

    private static TransferAcceptanceResult Fail(Guid id, TransferAcceptanceFailure failure, string reason) =>
        new(id, null, failure, reason);
}
