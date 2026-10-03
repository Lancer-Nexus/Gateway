using LancerNexus.Protocol;
using MySqlConnector;

namespace LancerNexus.Gateway;

public sealed record TransferStatusResult(
    TransferStatusResponse? Status,
    TransferSourceReleaseFailure Failure,
    string ReasonCode)
{
    public bool Accepted => Status is not null && Failure == TransferSourceReleaseFailure.None;
}

public sealed class TransferStatusService(ICoordinatorTransferClient coordinator, IAccountRepository accounts)
{
    public async Task<TransferStatusResult> GetAsync(
        Guid transferId,
        string authenticatedInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (transferId == Guid.Empty || string.IsNullOrWhiteSpace(authenticatedInstanceId))
            return Failed("transfer_status_request_invalid");
        var lookup = await coordinator.GetTransferAsync(transferId, cancellationToken);
        if (!lookup.IsAvailable)
            return new(null, TransferSourceReleaseFailure.CoordinatorUnavailable,
                lookup.Error ?? "coordinator_unavailable");
        var transfer = lookup.Transfer;
        if (transfer is null)
            return Failed("transfer_not_found");
        if (transfer.TransferId != transferId || transfer.Request.TransferId != transferId)
            return new(null, TransferSourceReleaseFailure.CoordinatorInvalidResponse,
                "coordinator_transfer_id_mismatch");
        if (!string.Equals(transfer.Request.SourceInstanceId, authenticatedInstanceId, StringComparison.Ordinal) &&
            !string.Equals(transfer.Request.TargetInstanceId, authenticatedInstanceId, StringComparison.Ordinal))
            return Failed("transfer_instance_mismatch");

        CharacterLeaseTransferCommitRecord? committed;
        CharacterTransferDecision? decision;
        try
        {
            committed = await accounts.FindCharacterLeaseTransferCommitAsync(transferId, cancellationToken);
            decision = await accounts.FindCharacterTransferDecisionAsync(transferId, cancellationToken);
        }
        catch (Exception exception) when (exception is MySqlException or InvalidOperationException)
        {
            return new(null, TransferSourceReleaseFailure.PersistenceUnavailable,
                "character_transfer_persistence_unavailable");
        }
        var state = transfer.State;
        var version = transfer.LeaseVersion;
        if (decision?.Kind == CharacterTransferDecisionKind.Aborted)
        {
            var binding = decision.Binding;
            if (committed is not null || binding.TransferId != transferId ||
                binding.SessionId != transfer.Request.SessionId || binding.CharacterId != transfer.Request.CharacterId ||
                binding.SourceInstanceId != transfer.Request.SourceInstanceId ||
                binding.TargetInstanceId != transfer.Request.TargetInstanceId ||
                binding.ExpectedLeaseVersion < 0 || binding.ExpectedLeaseVersion == long.MaxValue ||
                state is TransferState.Committed or TransferState.SourceReleased)
                return new(null, TransferSourceReleaseFailure.PersistenceInvalidResponse,
                    "character_transfer_abort_mismatch");
            // Durable veto is rollback proof even if its Coordinator ACK was lost.
            state = TransferState.Aborted;
            version = binding.ExpectedLeaseVersion;
        }
        if (committed is not null)
        {
            if (committed.TransferId != transferId || committed.SessionId != transfer.Request.SessionId ||
                committed.CharacterId != transfer.Request.CharacterId ||
                !string.Equals(committed.SourceInstanceId, transfer.Request.SourceInstanceId, StringComparison.Ordinal) ||
                !string.Equals(committed.TargetInstanceId, transfer.Request.TargetInstanceId, StringComparison.Ordinal) ||
                committed.ExpectedLeaseVersion < 0 || committed.ExpectedLeaseVersion == long.MaxValue ||
                committed.CommittedLeaseVersion != committed.ExpectedLeaseVersion + 1 ||
                state is not (TransferState.SourceFrozen or TransferState.TargetAccepted or
                    TransferState.Committed or TransferState.SourceReleased) ||
                (state is TransferState.Committed or TransferState.SourceReleased && version != committed.CommittedLeaseVersion))
                return new(null, TransferSourceReleaseFailure.PersistenceInvalidResponse,
                    "character_transfer_commit_mismatch");
            // SQL is authoritative in the crash window after lease commit but
            // before the Coordinator acknowledgement. Never advertise rollback.
            state = state == TransferState.SourceReleased ? state : TransferState.Committed;
            version = committed.CommittedLeaseVersion;
        }
        else if (state is TransferState.Committed or TransferState.SourceReleased)
            return new(null, TransferSourceReleaseFailure.PersistenceInvalidResponse,
                "character_transfer_commit_missing");
        return new(new TransferStatusResponse
        {
            TransferId = transfer.TransferId,
            SourceInstanceId = transfer.Request.SourceInstanceId,
            TargetInstanceId = transfer.Request.TargetInstanceId,
            TargetSystemId = transfer.Request.TargetSystemId,
            State = state,
            ExpiresUtc = transfer.ExpiresUtc,
            LeaseVersion = version
        }, TransferSourceReleaseFailure.None, "accepted");
    }

    private static TransferStatusResult Failed(string reasonCode) =>
        new(null, TransferSourceReleaseFailure.Rejected, reasonCode);
}
