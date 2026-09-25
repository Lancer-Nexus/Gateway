using System.Security.Cryptography;
using MySqlConnector;

namespace LancerNexus.Gateway;

public sealed record TransferSnapshotMetadata(
    Guid TransferId,
    string SourceInstanceId,
    string TargetInstanceId,
    long CharacterId,
    long LeaseVersion,
    DateTime CreatedAtUtc);

public sealed record TransferSnapshotRecord(TransferSnapshotMetadata Metadata, byte[] Snapshot);

public enum TransferSnapshotStoreStatus
{
    Stored,
    Duplicate,
    Conflict,
    NotFound,
    Unavailable
}

public sealed record TransferSnapshotStoreResult(TransferSnapshotStoreStatus Status, TransferSnapshotRecord? Record = null);

public interface ITransferSnapshotStore
{
    Task<TransferSnapshotStoreResult> StoreAsync(TransferSnapshotMetadata metadata, ReadOnlyMemory<byte> snapshot,
        CancellationToken cancellationToken = default);
    Task<TransferSnapshotStoreResult> ReadAsync(Guid transferId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid transferId, CancellationToken cancellationToken = default) => Task.FromResult(true);
}

public sealed class TransferSnapshotStoreNotConfigured : ITransferSnapshotStore
{
    public Task<TransferSnapshotStoreResult> StoreAsync(TransferSnapshotMetadata metadata,
        ReadOnlyMemory<byte> snapshot, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TransferSnapshotStoreResult(TransferSnapshotStoreStatus.Unavailable));

    public Task<TransferSnapshotStoreResult> ReadAsync(Guid transferId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TransferSnapshotStoreResult(TransferSnapshotStoreStatus.Unavailable));

    public Task<bool> DeleteAsync(Guid transferId, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

public sealed class MySqlTransferSnapshotStore(
    string connectionString,
    TransferSnapshotProtection protection,
    TimeProvider timeProvider) : ITransferSnapshotStore
{
    public async Task<TransferSnapshotStoreResult> StoreAsync(TransferSnapshotMetadata metadata,
        ReadOnlyMemory<byte> snapshot, CancellationToken cancellationToken = default)
    {
        if (metadata.TransferId == Guid.Empty || string.IsNullOrWhiteSpace(metadata.SourceInstanceId) ||
            string.IsNullOrWhiteSpace(metadata.TargetInstanceId) || metadata.CharacterId <= 0 ||
            metadata.LeaseVersion < 0 || snapshot.Length is 0 or > TransferSnapshotLimits.MaxBytes)
            return new(TransferSnapshotStoreStatus.Conflict);
        if (!protection.TryProtect(metadata.TransferId, snapshot.Span, out var keyId, out var encrypted))
            return new(TransferSnapshotStoreStatus.Unavailable);

        var snapshotHash = SHA256.HashData(snapshot.Span);
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT IGNORE INTO character_transfer_snapshots
                        (transfer_id, source_instance_id, target_instance_id, character_id, lease_version,
                         key_id, snapshot_hash, protected_snapshot, created_at_utc)
                    VALUES (@transfer_id, @source_instance_id, @target_instance_id, @character_id, @lease_version,
                            @key_id, @snapshot_hash, @protected_snapshot, @created_at_utc);
                    """;
                AddMetadata(insert, metadata);
                insert.Parameters.AddWithValue("@key_id", keyId);
                insert.Parameters.AddWithValue("@snapshot_hash", snapshotHash);
                insert.Parameters.AddWithValue("@protected_snapshot", encrypted);
                insert.Parameters.AddWithValue("@created_at_utc", timeProvider.GetUtcNow().UtcDateTime);
                var inserted = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;

                await using var select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT source_instance_id, target_instance_id, character_id, lease_version, snapshot_hash, created_at_utc
                    FROM character_transfer_snapshots WHERE transfer_id = @transfer_id;
                    """;
                select.Parameters.AddWithValue("@transfer_id", metadata.TransferId.ToString());
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await reader.DisposeAsync();
                    await transaction.RollbackAsync(cancellationToken);
                    return new(TransferSnapshotStoreStatus.Unavailable);
                }

                var matches = reader.GetString(0) == metadata.SourceInstanceId &&
                              reader.GetString(1) == metadata.TargetInstanceId &&
                              reader.GetInt64(2) == metadata.CharacterId &&
                              reader.GetInt64(3) == metadata.LeaseVersion &&
                              CryptographicOperations.FixedTimeEquals((byte[]) reader.GetValue(4), snapshotHash);
                var createdAt = DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc);
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                if (!matches)
                    return new(TransferSnapshotStoreStatus.Conflict);
                var status = inserted ? TransferSnapshotStoreStatus.Stored : TransferSnapshotStoreStatus.Duplicate;
                return new(status, new TransferSnapshotRecord(metadata with { CreatedAtUtc = createdAt }, snapshot.ToArray()));
            }
        }
        catch (MySqlException)
        {
            return new(TransferSnapshotStoreStatus.Unavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(snapshotHash);
        }
    }

    public async Task<TransferSnapshotStoreResult> ReadAsync(Guid transferId,
        CancellationToken cancellationToken = default)
    {
        if (transferId == Guid.Empty)
            return new(TransferSnapshotStoreStatus.NotFound);
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT source_instance_id, target_instance_id, character_id, lease_version,
                       snapshot_hash, key_id, protected_snapshot, created_at_utc
                FROM character_transfer_snapshots WHERE transfer_id = @transfer_id;
                """;
            command.Parameters.AddWithValue("@transfer_id", transferId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new(TransferSnapshotStoreStatus.NotFound);
            var source = reader.GetString(0);
            var target = reader.GetString(1);
            var characterId = reader.GetInt64(2);
            var leaseVersion = reader.GetInt64(3);
            var hash = (byte[]) reader.GetValue(4);
            var keyId = reader.GetString(5);
            var encrypted = (byte[]) reader.GetValue(6);
            var createdAt = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc);
            await reader.DisposeAsync();

            if (!protection.TryUnprotect(transferId, keyId, encrypted, out var snapshot))
                return new(TransferSnapshotStoreStatus.Unavailable);
            if (snapshot.Length > TransferSnapshotLimits.MaxBytes ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(snapshot), hash))
            {
                CryptographicOperations.ZeroMemory(snapshot);
                return new(TransferSnapshotStoreStatus.Unavailable);
            }
            return new(TransferSnapshotStoreStatus.Stored, new TransferSnapshotRecord(
                new TransferSnapshotMetadata(transferId, source, target, characterId, leaseVersion, createdAt), snapshot));
        }
        catch (MySqlException)
        {
            return new(TransferSnapshotStoreStatus.Unavailable);
        }
    }

    public async Task<bool> DeleteAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        if (transferId == Guid.Empty)
            return true;
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM character_transfer_snapshots WHERE transfer_id = @transfer_id;";
            command.Parameters.AddWithValue("@transfer_id", transferId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (MySqlException)
        {
            return false;
        }
    }

    private static void AddMetadata(MySqlCommand command, TransferSnapshotMetadata metadata)
    {
        command.Parameters.AddWithValue("@transfer_id", metadata.TransferId.ToString());
        command.Parameters.AddWithValue("@source_instance_id", metadata.SourceInstanceId);
        command.Parameters.AddWithValue("@target_instance_id", metadata.TargetInstanceId);
        command.Parameters.AddWithValue("@character_id", metadata.CharacterId);
        command.Parameters.AddWithValue("@lease_version", metadata.LeaseVersion);
    }
}
