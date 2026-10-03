using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed class NpcMissionAuthorityService(ICoordinatorTransferClient coordinator, IAccountRepository accounts,
    ITransferSnapshotStore snapshots, TimeProvider timeProvider)
{
    public async Task<NpcMissionAuthorityResultV1> DecideAsync(NpcMissionAuthorityRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsValid())
            return Reply(request, false, "invalid_mission_authority_request");
        var lookup = await coordinator.GetTransferAsync(request.TransferId, cancellationToken);
        if (!lookup.IsAvailable)
            throw new InvalidOperationException("Character transfer coordinator unavailable.");
        var transfer = lookup.Transfer;
        if (transfer is null || transfer.TransferId != request.TransferId ||
            transfer.Request.TransferId != request.TransferId || transfer.Request.SessionId == Guid.Empty ||
            transfer.Request.CharacterId <= 0 || transfer.Request.SourceInstanceId != request.SourceInstanceId ||
            transfer.Request.TargetInstanceId != request.TargetInstanceId ||
            !string.Equals(transfer.Request.TargetSystemId, request.TargetSystemId, StringComparison.OrdinalIgnoreCase))
            return Reply(request, false, "character_transfer_binding_mismatch");

        var previous = await accounts.FindCharacterTransferDecisionAsync(request.TransferId, cancellationToken);
        if (previous is not null)
        {
            if (!Matches(previous.Binding, transfer.Request))
                return Reply(request, false, "character_transfer_decision_mismatch");
            if (previous.Kind == CharacterTransferDecisionKind.Aborted)
                return request.Decision == NpcTransferState.Aborted
                    ? await FinishAbortAsync(request, cancellationToken)
                    : Reply(request, false, "character_transfer_aborted");
            if (request.Decision == NpcTransferState.Aborted)
                return Reply(request, false, "committed_transfer_cannot_abort");
            var committed = await accounts.FindCharacterLeaseTransferCommitAsync(request.TransferId, cancellationToken);
            if (committed is null || committed.TransferId != request.TransferId ||
                committed.SessionId != previous.Binding.SessionId || committed.CharacterId != previous.Binding.CharacterId ||
                committed.SourceInstanceId != request.SourceInstanceId || committed.TargetInstanceId != request.TargetInstanceId ||
                committed.ExpectedLeaseVersion != previous.Binding.ExpectedLeaseVersion ||
                committed.ExpectedLeaseVersion < 0 || committed.ExpectedLeaseVersion == long.MaxValue ||
                committed.CommittedLeaseVersion != committed.ExpectedLeaseVersion + 1 ||
                transfer.State is not (TransferState.SourceFrozen or TransferState.TargetAccepted or
                    TransferState.Committed or TransferState.SourceReleased))
                return Reply(request, false, "character_transfer_commit_mismatch");
            return Reply(request, true, "character_transfer_committed", committed.CommittedLeaseVersion);
        }
        if (request.Decision == NpcTransferState.Committed)
            return Reply(request, false, "character_transfer_not_committed");

        var stored = await snapshots.ReadAsync(request.TransferId, cancellationToken);
        long expectedVersion;
        if (stored.Status is TransferSnapshotStoreStatus.Stored or TransferSnapshotStoreStatus.Duplicate &&
            stored.Record is not null)
        {
            var metadata = stored.Record.Metadata;
            if (metadata.TransferId != request.TransferId || metadata.SourceInstanceId != request.SourceInstanceId ||
                metadata.TargetInstanceId != request.TargetInstanceId || metadata.CharacterId != transfer.Request.CharacterId)
                return Reply(request, false, "character_snapshot_binding_mismatch");
            expectedVersion = metadata.LeaseVersion;
        }
        else if (stored.Status == TransferSnapshotStoreStatus.NotFound &&
            transfer.State is TransferState.Requested or TransferState.Reserved or TransferState.Prepared)
        {
            var lease = await accounts.FindActiveCharacterLeaseForTransferAsync(transfer.Request.SessionId,
                transfer.Request.CharacterId, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            if (lease is null || lease.InstanceId != request.SourceInstanceId)
                return Reply(request, false, "character_source_lease_unavailable");
            expectedVersion = lease.LeaseVersion;
        }
        else
            throw new InvalidOperationException("Character snapshot required for a durable rollback decision.");

        var aborted = await accounts.AbortCharacterLeaseTransferAsync(new(request.TransferId, transfer.Request.SessionId,
            transfer.Request.CharacterId, request.SourceInstanceId, request.TargetInstanceId, expectedVersion),
            timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        return aborted.Accepted ? await FinishAbortAsync(request, cancellationToken)
            : Reply(request, false, aborted.ReasonCode);
    }

    private async Task<NpcMissionAuthorityResultV1> FinishAbortAsync(NpcMissionAuthorityRequestV1 request,
        CancellationToken cancellationToken)
    {
        var aborted = await coordinator.AbortAsync(new TransferAbort
            { TransferId = request.TransferId, ReasonCode = "durable_character_abort" }, cancellationToken);
        if (!aborted.IsAvailable || aborted.Outcome is null || !aborted.Outcome.Accepted ||
            aborted.Outcome.State != TransferState.Aborted)
            throw new InvalidOperationException("Durable character abort awaits Coordinator acknowledgement.");
        return Reply(request, true, "character_transfer_aborted");
    }

    private static bool Matches(CharacterTransferBinding binding, TransferPrepareRequest request) =>
        binding.TransferId == request.TransferId && binding.SessionId == request.SessionId &&
        binding.CharacterId == request.CharacterId && binding.SourceInstanceId == request.SourceInstanceId &&
        binding.TargetInstanceId == request.TargetInstanceId;

    private static NpcMissionAuthorityResultV1 Reply(NpcMissionAuthorityRequestV1 request, bool accepted,
        string reason, long? version = null) => new()
    {
        TransferId = request.TransferId, SourceInstanceId = request.SourceInstanceId,
        TargetInstanceId = request.TargetInstanceId, TargetSystemId = request.TargetSystemId,
        Decision = request.Decision, Accepted = accepted, ReasonCode = reason, CommittedLeaseVersion = version
    };
}
