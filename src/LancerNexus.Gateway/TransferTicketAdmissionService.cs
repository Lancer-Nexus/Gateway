using System.Net;
using System.Net.Sockets;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public enum TransferTicketAdmissionFailure
{
    None,
    Rejected,
    CoordinatorUnavailable,
    CoordinatorInvalidResponse,
    ReplayStoreUnavailable
}

public sealed record TransferTicketAdmissionResult(
    TransferTicketClaims? Claims,
    TransferTicketAdmissionFailure Failure,
    string ReasonCode)
{
    public bool Accepted => Claims is not null && Failure == TransferTicketAdmissionFailure.None;
}

public sealed class TransferTicketAdmissionService(
    TransferTicketCodec tickets,
    ICoordinatorTransferClient coordinator,
    ITransferTicketReplayStore replayStore,
    TimeProvider timeProvider)
{
    public async Task<TransferTicketAdmissionResult> VerifyAsync(
        TransferTicketVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Ticket) || string.IsNullOrWhiteSpace(request.TargetInstanceId))
            return Reject("transfer_ticket_request_invalid");

        var validation = tickets.ValidateForTransferRecovery(request.Ticket, timeProvider.GetUtcNow().UtcDateTime);
        if (!validation.Accepted)
            return Reject(validation.ReasonCode);
        var claims = validation.Claims!;
        if (!string.Equals(claims.TargetInstanceId, request.TargetInstanceId, StringComparison.Ordinal))
            return Reject("transfer_ticket_target_mismatch");

        var lookup = await coordinator.GetTransferAsync(claims.TransferId, cancellationToken);
        if (!lookup.IsAvailable)
            return new(null, TransferTicketAdmissionFailure.CoordinatorUnavailable,
                lookup.Error ?? "coordinator_unavailable");
        var transfer = lookup.Transfer;
        if (transfer is null)
            return Reject("transfer_not_found");
        if (transfer.TransferId != claims.TransferId || transfer.Request.TransferId != claims.TransferId)
            return Invalid("coordinator_transfer_id_mismatch");
        var detailsMatch = transfer.Request.SessionId == claims.SessionId &&
                           transfer.Request.CharacterId == claims.CharacterId &&
                           string.Equals(transfer.Request.SourceInstanceId, claims.SourceInstanceId, StringComparison.Ordinal) &&
                           string.Equals(transfer.Request.TargetInstanceId, claims.TargetInstanceId, StringComparison.Ordinal) &&
                           string.Equals(transfer.Request.TargetSystemId, claims.TargetSystemId, StringComparison.Ordinal) &&
                           transfer.ExpiresUtc == claims.ExpiresAtUtc && transfer.Request.ExpiresUtc == claims.ExpiresAtUtc;
        if (!detailsMatch)
            return Reject("transfer_ticket_claims_mismatch");
        if (transfer.State != TransferState.SourceFrozen)
            return Reject("transfer_not_ready");

        try
        {
            return await replayStore.TryConsumeTransferAsync(claims.Nonce, claims.ExpiresAtUtc, cancellationToken)
                ? new(claims, TransferTicketAdmissionFailure.None, "accepted")
                : Reject("transfer_ticket_replayed");
        }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException or OperationCanceledException)
        {
            return new(null, TransferTicketAdmissionFailure.ReplayStoreUnavailable, "transfer_replay_store_unavailable");
        }
    }

    private static TransferTicketAdmissionResult Reject(string reason) =>
        new(null, TransferTicketAdmissionFailure.Rejected, reason);

    private static TransferTicketAdmissionResult Invalid(string reason) =>
        new(null, TransferTicketAdmissionFailure.CoordinatorInvalidResponse, reason);
}
