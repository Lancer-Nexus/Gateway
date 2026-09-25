using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record TransferStatusResult(
    TransferStatusResponse? Status,
    TransferSourceReleaseFailure Failure,
    string ReasonCode)
{
    public bool Accepted => Status is not null && Failure == TransferSourceReleaseFailure.None;
}

public sealed class TransferStatusService(ICoordinatorTransferClient coordinator)
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
        return new(new TransferStatusResponse
        {
            TransferId = transfer.TransferId,
            SourceInstanceId = transfer.Request.SourceInstanceId,
            TargetInstanceId = transfer.Request.TargetInstanceId,
            TargetSystemId = transfer.Request.TargetSystemId,
            State = transfer.State,
            ExpiresUtc = transfer.ExpiresUtc,
            LeaseVersion = transfer.LeaseVersion
        }, TransferSourceReleaseFailure.None, "accepted");
    }

    private static TransferStatusResult Failed(string reasonCode) =>
        new(null, TransferSourceReleaseFailure.Rejected, reasonCode);
}
