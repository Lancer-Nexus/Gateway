using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public enum TransferSourceFreezeFailure
{
    None,
    Rejected,
    CoordinatorUnavailable,
    CoordinatorInvalidResponse
}

public sealed record TransferSourceFreezeResult(
    Guid TransferId,
    TransferSourceFreezeFailure Failure,
    string ReasonCode)
{
    public bool Accepted => TransferId != Guid.Empty && Failure == TransferSourceFreezeFailure.None;
}

public sealed class TransferSourceFreezeService(
    ICoordinatorTransferClient coordinator,
    TimeProvider timeProvider)
{
    public async Task<TransferSourceFreezeResult> MarkFrozenAsync(
        Guid transferId,
        string authenticatedSourceInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (transferId == Guid.Empty || string.IsNullOrWhiteSpace(authenticatedSourceInstanceId))
            return Reject(transferId, "transfer_source_freeze_request_invalid");

        var lookup = await coordinator.GetTransferAsync(transferId, cancellationToken);
        if (!lookup.IsAvailable)
            return Fail(transferId, TransferSourceFreezeFailure.CoordinatorUnavailable,
                lookup.Error ?? "coordinator_unavailable");
        var transfer = lookup.Transfer;
        if (transfer is null)
            return Reject(transferId, "transfer_not_found");
        if (transfer.TransferId != transferId || transfer.Request.TransferId != transferId)
            return Fail(transferId, TransferSourceFreezeFailure.CoordinatorInvalidResponse,
                "coordinator_transfer_id_mismatch");
        if (transfer.Request.ExpiresUtc != transfer.ExpiresUtc || transfer.Request.ExpiresUtc.Kind != DateTimeKind.Utc)
            return Fail(transferId, TransferSourceFreezeFailure.CoordinatorInvalidResponse,
                "coordinator_transfer_expiry_mismatch");
        if (!string.Equals(transfer.Request.SourceInstanceId, authenticatedSourceInstanceId, StringComparison.Ordinal))
            return Reject(transferId, "transfer_source_instance_mismatch");
        if (transfer.State == TransferState.SourceFrozen)
            return new TransferSourceFreezeResult(transferId, TransferSourceFreezeFailure.None, "duplicate");
        if (transfer.State != TransferState.Prepared)
            return Reject(transferId, "transfer_not_prepared");
        if (transfer.ExpiresUtc.Kind != DateTimeKind.Utc || transfer.ExpiresUtc <= timeProvider.GetUtcNow().UtcDateTime)
            return Reject(transferId, "transfer_expired");

        var result = await coordinator.MarkSourceFrozenAsync(transferId, cancellationToken);
        if (!result.IsAvailable)
            return Fail(transferId, TransferSourceFreezeFailure.CoordinatorUnavailable,
                result.Error ?? "coordinator_unavailable");
        if (result.Outcome is null || !result.Outcome.Accepted || result.Outcome.State != TransferState.SourceFrozen)
            return Fail(transferId, TransferSourceFreezeFailure.CoordinatorInvalidResponse,
                "coordinator_source_freeze_rejected");
        return new TransferSourceFreezeResult(transferId, TransferSourceFreezeFailure.None,
            result.Outcome.Duplicate ? "duplicate" : "source_frozen");
    }

    private static TransferSourceFreezeResult Reject(Guid id, string reason) =>
        Fail(id, TransferSourceFreezeFailure.Rejected, reason);

    private static TransferSourceFreezeResult Fail(Guid id, TransferSourceFreezeFailure failure, string reason) =>
        new(id, failure, reason);
}
