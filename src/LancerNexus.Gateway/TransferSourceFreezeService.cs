using LancerNexus.Protocol;
using MySqlConnector;

namespace LancerNexus.Gateway;

public enum TransferSourceFreezeFailure
{
    None,
    Rejected,
    CoordinatorUnavailable,
    CoordinatorInvalidResponse,
    SnapshotStoreUnavailable,
    PersistenceUnavailable
}

public sealed record TransferSourceFreezeResult(
    Guid TransferId,
    TransferSourceFreezeFailure Failure,
    string ReasonCode,
    TransferSnapshotRecord? Snapshot = null)
{
    public bool Accepted => TransferId != Guid.Empty && Failure == TransferSourceFreezeFailure.None;
}

public sealed class TransferSourceFreezeService(
    ICoordinatorTransferClient coordinator,
    IAccountRepository accounts,
    ITransferSnapshotStore snapshots,
    TimeProvider timeProvider)
{
    public async Task<TransferSourceFreezeResult> MarkFrozenAsync(
        Guid transferId,
        string authenticatedSourceInstanceId,
        long expectedLeaseVersion,
        ReadOnlyMemory<byte> snapshotBytes,
        CancellationToken cancellationToken = default)
    {
        if (transferId == Guid.Empty || string.IsNullOrWhiteSpace(authenticatedSourceInstanceId))
            return Reject(transferId, "transfer_source_freeze_request_invalid");
        if (expectedLeaseVersion < 0 || snapshotBytes.Length is 0 or > TransferSnapshotLimits.MaxBytes)
            return Reject(transferId, "transfer_snapshot_size_invalid");

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
        if (transfer.State is not (TransferState.Prepared or TransferState.SourceFrozen))
            return Reject(transferId, "transfer_not_prepared");
        if (transfer.State == TransferState.Prepared &&
            (transfer.ExpiresUtc.Kind != DateTimeKind.Utc || transfer.ExpiresUtc <= timeProvider.GetUtcNow().UtcDateTime))
            return Reject(transferId, "transfer_expired");

        CharacterLeaseRecord? lease;
        try
        {
            lease = await accounts.FindActiveCharacterLeaseForTransferAsync(
                transfer.Request.SessionId, transfer.Request.CharacterId,
                timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception exception) when (exception is MySqlException or InvalidOperationException)
        {
            return Fail(transferId, TransferSourceFreezeFailure.PersistenceUnavailable,
                "character_lease_persistence_unavailable");
        }
        if (lease is null || lease.ValidUntilUtc <= timeProvider.GetUtcNow().UtcDateTime ||
            lease.LeaseVersion != expectedLeaseVersion ||
            !string.Equals(lease.InstanceId, authenticatedSourceInstanceId, StringComparison.Ordinal))
            return Reject(transferId, "source_lease_changed");

        var stored = await snapshots.StoreAsync(new TransferSnapshotMetadata(
            transferId,
            transfer.Request.SourceInstanceId,
            transfer.Request.TargetInstanceId,
            transfer.Request.CharacterId,
            lease.LeaseVersion,
            timeProvider.GetUtcNow().UtcDateTime), snapshotBytes, cancellationToken);
        if (stored.Status is not (TransferSnapshotStoreStatus.Stored or TransferSnapshotStoreStatus.Duplicate) ||
            stored.Record is null)
            return stored.Status switch
            {
                TransferSnapshotStoreStatus.Conflict => Reject(transferId, "transfer_snapshot_conflict"),
                TransferSnapshotStoreStatus.Unavailable => Fail(transferId,
                    TransferSourceFreezeFailure.SnapshotStoreUnavailable, "transfer_snapshot_store_unavailable"),
                _ => Fail(transferId, TransferSourceFreezeFailure.CoordinatorInvalidResponse,
                    "transfer_snapshot_store_invalid")
            };

        if (transfer.State == TransferState.SourceFrozen)
            return new TransferSourceFreezeResult(transferId, TransferSourceFreezeFailure.None, "duplicate", stored.Record);

        var result = await coordinator.MarkSourceFrozenAsync(transferId, cancellationToken);
        if (!result.IsAvailable)
            return Fail(transferId, TransferSourceFreezeFailure.CoordinatorUnavailable,
                result.Error ?? "coordinator_unavailable");
        if (result.Outcome is null || !result.Outcome.Accepted || result.Outcome.State != TransferState.SourceFrozen)
            return Fail(transferId, TransferSourceFreezeFailure.CoordinatorInvalidResponse,
                "coordinator_source_freeze_rejected");
        return new TransferSourceFreezeResult(transferId, TransferSourceFreezeFailure.None,
            result.Outcome.Duplicate ? "duplicate" : "source_frozen", stored.Record);
    }

    private static TransferSourceFreezeResult Reject(Guid id, string reason) =>
        Fail(id, TransferSourceFreezeFailure.Rejected, reason);

    private static TransferSourceFreezeResult Fail(Guid id, TransferSourceFreezeFailure failure, string reason) =>
        new(id, failure, reason);
}

public sealed class TransferSnapshotReadService(
    ICoordinatorTransferClient coordinator,
    ITransferSnapshotStore snapshots)
{
    public async Task<TransferSnapshotStoreResult> ReadForTargetAsync(Guid transferId,
        string authenticatedTargetInstanceId, CancellationToken cancellationToken = default)
    {
        if (transferId == Guid.Empty || string.IsNullOrWhiteSpace(authenticatedTargetInstanceId))
            return new(TransferSnapshotStoreStatus.NotFound);
        var lookup = await coordinator.GetTransferAsync(transferId, cancellationToken);
        if (!lookup.IsAvailable)
            return new(TransferSnapshotStoreStatus.Unavailable);
        var transfer = lookup.Transfer;
        if (transfer is null || transfer.TransferId != transferId || transfer.Request.TransferId != transferId ||
            !string.Equals(transfer.Request.TargetInstanceId, authenticatedTargetInstanceId, StringComparison.Ordinal) ||
            transfer.State is not (TransferState.SourceFrozen or TransferState.TargetAccepted or
                TransferState.Committed or TransferState.SourceReleased))
            return new(TransferSnapshotStoreStatus.NotFound);

        var stored = await snapshots.ReadAsync(transferId, cancellationToken);
        if (stored.Status != TransferSnapshotStoreStatus.Stored || stored.Record is null)
            return stored;
        var metadata = stored.Record.Metadata;
        if (!string.Equals(metadata.SourceInstanceId, transfer.Request.SourceInstanceId, StringComparison.Ordinal) ||
            !string.Equals(metadata.TargetInstanceId, transfer.Request.TargetInstanceId, StringComparison.Ordinal) ||
            metadata.CharacterId != transfer.Request.CharacterId)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(stored.Record.Snapshot);
            return new(TransferSnapshotStoreStatus.Unavailable);
        }
        return stored;
    }
}
