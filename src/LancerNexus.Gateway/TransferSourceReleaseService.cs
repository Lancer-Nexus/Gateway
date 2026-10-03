using System.Net;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public enum TransferSourceReleaseFailure
{
    None,
    Rejected,
    CoordinatorUnavailable,
    CoordinatorInvalidResponse,
    SnapshotStoreUnavailable,
    PersistenceUnavailable,
    PersistenceInvalidResponse
}

public sealed record TransferSourceReleaseResult(
    Guid TransferId,
    TransferSourceReleaseFailure Failure,
    string ReasonCode)
{
    public bool Accepted => TransferId != Guid.Empty && Failure == TransferSourceReleaseFailure.None;
}

public sealed class TransferSourceReleaseService(
    ICoordinatorTransferClient coordinator,
    ITransferSnapshotStore snapshots,
    TransferStatusService status)
{
    public async Task<TransferSourceReleaseResult> ReleaseAsync(
        TransferSourceReleaseRequest request,
        string authenticatedSourceInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (request.TransferId == Guid.Empty || string.IsNullOrWhiteSpace(authenticatedSourceInstanceId))
            return Reject(request.TransferId, "transfer_release_request_invalid");

        var lookup = await status.GetAsync(request.TransferId, authenticatedSourceInstanceId, cancellationToken);
        if (!lookup.Accepted)
            return Fail(request.TransferId, lookup.Failure, lookup.ReasonCode);
        var transfer = lookup.Status!;
        if (!string.Equals(transfer.SourceInstanceId, authenticatedSourceInstanceId, StringComparison.Ordinal))
            return Reject(request.TransferId, "transfer_source_instance_mismatch");
        if (transfer.State == TransferState.SourceReleased)
            return await DeleteSnapshotAsync(request.TransferId, duplicate: true, cancellationToken);
        if (transfer.State != TransferState.Committed || transfer.LeaseVersion <= 0)
            return Reject(request.TransferId, "transfer_not_committed");

        // Finish a Coordinator acknowledgement lost after the durable SQL commit.
        // This remains idempotent when the Coordinator was already committed.
        var commit = await coordinator.CommitAsync(request.TransferId, transfer.LeaseVersion, cancellationToken);
        if (!commit.IsAvailable)
            return Fail(request.TransferId, TransferSourceReleaseFailure.CoordinatorUnavailable,
                commit.Error ?? "coordinator_unavailable");
        if (commit.Outcome is null || !commit.Outcome.Accepted ||
            commit.Outcome.State != TransferState.Committed)
            return Fail(request.TransferId, TransferSourceReleaseFailure.CoordinatorInvalidResponse,
                "coordinator_commit_rejected");

        var result = await coordinator.MarkSourceReleasedAsync(request.TransferId, cancellationToken);
        if (!result.IsAvailable)
            return Fail(request.TransferId, TransferSourceReleaseFailure.CoordinatorUnavailable,
                result.Error ?? "coordinator_unavailable");
        if (result.Outcome is null || !result.Outcome.Accepted ||
            result.Outcome.State != TransferState.SourceReleased)
            return Fail(request.TransferId, TransferSourceReleaseFailure.CoordinatorInvalidResponse,
                "coordinator_source_release_rejected");
        return await DeleteSnapshotAsync(request.TransferId, result.Outcome.Duplicate, cancellationToken);
    }

    private async Task<TransferSourceReleaseResult> DeleteSnapshotAsync(Guid transferId, bool duplicate,
        CancellationToken cancellationToken)
    {
        if (!await snapshots.DeleteAsync(transferId, cancellationToken))
            return Fail(transferId, TransferSourceReleaseFailure.SnapshotStoreUnavailable,
                "transfer_snapshot_cleanup_unavailable");
        return new TransferSourceReleaseResult(transferId, TransferSourceReleaseFailure.None,
            duplicate ? "duplicate" : "released");
    }

    private static TransferSourceReleaseResult Reject(Guid id, string reason) =>
        Fail(id, TransferSourceReleaseFailure.Rejected, reason);

    private static TransferSourceReleaseResult Fail(Guid id, TransferSourceReleaseFailure failure, string reason) =>
        new(id, failure, reason);
}
