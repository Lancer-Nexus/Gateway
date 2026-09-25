using System.Net;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public enum TransferSourceReleaseFailure
{
    None,
    Rejected,
    CoordinatorUnavailable,
    CoordinatorInvalidResponse,
    SnapshotStoreUnavailable
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
    ITransferSnapshotStore snapshots)
{
    public async Task<TransferSourceReleaseResult> ReleaseAsync(
        TransferSourceReleaseRequest request,
        string authenticatedSourceInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (request.TransferId == Guid.Empty || string.IsNullOrWhiteSpace(authenticatedSourceInstanceId))
            return Reject(request.TransferId, "transfer_release_request_invalid");

        var lookup = await coordinator.GetTransferAsync(request.TransferId, cancellationToken);
        if (!lookup.IsAvailable)
            return Fail(request.TransferId, TransferSourceReleaseFailure.CoordinatorUnavailable,
                lookup.Error ?? "coordinator_unavailable");
        var transfer = lookup.Transfer;
        if (transfer is null)
            return Reject(request.TransferId, "transfer_not_found");
        if (transfer.TransferId != request.TransferId || transfer.Request.TransferId != request.TransferId)
            return Fail(request.TransferId, TransferSourceReleaseFailure.CoordinatorInvalidResponse,
                "coordinator_transfer_id_mismatch");
        if (!string.Equals(transfer.Request.SourceInstanceId, authenticatedSourceInstanceId, StringComparison.Ordinal))
            return Reject(request.TransferId, "transfer_source_instance_mismatch");
        if (transfer.State == TransferState.SourceReleased)
            return await DeleteSnapshotAsync(request.TransferId, duplicate: true, cancellationToken);
        if (transfer.State != TransferState.Committed || transfer.LeaseVersion <= 0)
            return Reject(request.TransferId, "transfer_not_committed");

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
