using System.Buffers.Binary;
using System.Security.Cryptography;
using LancerNexus.Protocol;
using MySqlConnector;

namespace LancerNexus.Gateway;

public enum CharacterSaveSnapshotStoreStatus
{
    Stored,
    Duplicate,
    Conflict,
    LeaseRejected,
    SessionRejected,
    NotFound,
    Unavailable
}

public sealed record CharacterSaveSnapshotStoreResult(
    CharacterSaveSnapshotStoreStatus Status,
    long? Revision = null,
    CharacterSaveSnapshotV1? Snapshot = null);

public interface ICharacterSaveSnapshotStore
{
    Task<CharacterSaveSnapshotStoreResult> WriteAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
        CharacterSaveSnapshotWriteRequestV1 request, DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<CharacterSaveSnapshotStoreResult> ReadAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
        CharacterSaveSnapshotReadRequestV1 request, DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<CharacterFactionReputationReadResult> ReadFactionReputationAsync(Guid accountId, Guid sessionId,
        long characterId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CharacterFactionReputationReadResult(CharacterFactionReputationReadStatus.Unavailable));

    Task<CharacterSaveSnapshotStoreResult> BootstrapAsync(CharacterBootstrapRequestV1 request, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CharacterSaveSnapshotStoreResult(CharacterSaveSnapshotStoreStatus.Unavailable));

    Task<CharacterSaveSnapshotStoreResult> ReadPreviewAsync(CharacterSnapshotPreviewRequestV1 request,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CharacterSaveSnapshotStoreResult(CharacterSaveSnapshotStoreStatus.Unavailable));
}

public sealed class CharacterSaveSnapshotStoreNotConfigured : ICharacterSaveSnapshotStore
{
    public Task<CharacterSaveSnapshotStoreResult> WriteAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
        CharacterSaveSnapshotWriteRequestV1 request, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CharacterSaveSnapshotStoreResult(CharacterSaveSnapshotStoreStatus.Unavailable));

    public Task<CharacterSaveSnapshotStoreResult> ReadAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
        CharacterSaveSnapshotReadRequestV1 request, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CharacterSaveSnapshotStoreResult(CharacterSaveSnapshotStoreStatus.Unavailable));

    public Task<CharacterFactionReputationReadResult> ReadFactionReputationAsync(Guid accountId, Guid sessionId,
        long characterId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CharacterFactionReputationReadResult(CharacterFactionReputationReadStatus.Unavailable));

    public Task<CharacterSaveSnapshotStoreResult> BootstrapAsync(CharacterBootstrapRequestV1 request,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CharacterSaveSnapshotStoreResult(CharacterSaveSnapshotStoreStatus.Unavailable));

    public Task<CharacterSaveSnapshotStoreResult> ReadPreviewAsync(CharacterSnapshotPreviewRequestV1 request,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CharacterSaveSnapshotStoreResult(CharacterSaveSnapshotStoreStatus.Unavailable));
}

/// <summary>Stores opaque SaveGame payloads only while the caller holds the active, fenced character lease.</summary>
public sealed class MySqlCharacterSaveSnapshotStore(
    string connectionString,
    TransferSnapshotProtection protection) : ICharacterSaveSnapshotStore
{
    public async Task<CharacterFactionReputationReadResult> ReadFactionReputationAsync(Guid accountId,
        Guid sessionId, long characterId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty || sessionId == Guid.Empty || characterId <= 0)
            return new(CharacterFactionReputationReadStatus.InvalidRequest);
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (!await LockCharacterAsync(connection, transaction, accountId, characterId, cancellationToken))
                return await RollbackReputationAsync(transaction, CharacterFactionReputationReadStatus.NotFound,
                    cancellationToken);
            if (!await HasActiveSessionAsync(connection, transaction, accountId, sessionId, nowUtc,
                    cancellationToken))
                return await RollbackReputationAsync(transaction, CharacterFactionReputationReadStatus.SessionRejected,
                    cancellationToken);

            var stored = await ReadSnapshotAsync(connection, transaction, characterId, cancellationToken);
            if (stored is null)
                return await RollbackReputationAsync(transaction, CharacterFactionReputationReadStatus.NotFound,
                    cancellationToken);

            var associatedData = AssociatedData(characterId, stored.Value.Revision);
            try
            {
                if (!protection.TryUnprotect(associatedData, stored.Value.KeyId, stored.Value.ProtectedSnapshot,
                        out var payload))
                    return await RollbackReputationAsync(transaction,
                        CharacterFactionReputationReadStatus.Unavailable, cancellationToken);
                try
                {
                    var hash = SHA256.HashData(payload);
                    var hashMatches = CryptographicOperations.FixedTimeEquals(hash, stored.Value.Hash);
                    CryptographicOperations.ZeroMemory(hash);
                    if (!hashMatches || !SaveGameFactionReputationParser.TryParse(payload, out var reputations))
                        return await RollbackReputationAsync(transaction,
                            CharacterFactionReputationReadStatus.Unavailable, cancellationToken);

                    await transaction.CommitAsync(cancellationToken);
                    return new(CharacterFactionReputationReadStatus.Found, stored.Value.Revision, reputations);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(associatedData);
                CryptographicOperations.ZeroMemory(stored.Value.Hash);
                CryptographicOperations.ZeroMemory(stored.Value.ProtectedSnapshot);
            }
        }
        catch (MySqlException)
        {
            return new(CharacterFactionReputationReadStatus.Unavailable);
        }
    }

    public async Task<CharacterSaveSnapshotStoreResult> BootstrapAsync(CharacterBootstrapRequestV1 request,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (!request.IsValid()) return new(CharacterSaveSnapshotStoreStatus.Conflict);
        var hash = SHA256.HashData(request.Payload);
        var associatedData = AssociatedData(request.CharacterId, 1);
        if (!protection.TryProtect(associatedData, request.Payload, out var keyId, out var protectedSnapshot))
        {
            CryptographicOperations.ZeroMemory(hash);
            CryptographicOperations.ZeroMemory(associatedData);
            return new(CharacterSaveSnapshotStoreStatus.Unavailable);
        }
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (!await LockCharacterAsync(connection, transaction, request.AccountId, request.CharacterId,
                    cancellationToken))
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.NotFound, cancellationToken);
            if (!await HasActiveSessionAsync(connection, transaction, request.AccountId, request.SessionId,
                    nowUtc, cancellationToken))
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.SessionRejected, cancellationToken);
            var existing = await ReadRevisionForUpdateAsync(connection, transaction, request.CharacterId,
                cancellationToken);
            if (existing is not null)
            {
                CryptographicOperations.ZeroMemory(existing.Value.Hash);
                await transaction.RollbackAsync(cancellationToken);
                return new(CharacterSaveSnapshotStoreStatus.Duplicate, 1);
            }
            if (await HasAnyLeaseAsync(connection, transaction, request.CharacterId, cancellationToken))
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.LeaseRejected, cancellationToken);

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO character_save_snapshots
                        (character_id, snapshot_revision, lease_version, schema_version, key_id,
                         snapshot_hash, protected_snapshot, updated_at_utc)
                    VALUES (@character, 1, 1, 1, @key_id, @hash, @snapshot, @updated_at);
                    """;
                insert.Parameters.AddWithValue("@character", request.CharacterId);
                insert.Parameters.AddWithValue("@key_id", keyId);
                insert.Parameters.AddWithValue("@hash", hash);
                insert.Parameters.AddWithValue("@snapshot", protectedSnapshot);
                insert.Parameters.AddWithValue("@updated_at", nowUtc);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new(CharacterSaveSnapshotStoreStatus.Stored, 1);
        }
        catch (MySqlException)
        {
            return new(CharacterSaveSnapshotStoreStatus.Unavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(protectedSnapshot);
        }
    }

    public async Task<CharacterSaveSnapshotStoreResult> ReadPreviewAsync(CharacterSnapshotPreviewRequestV1 request,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (!request.IsValid()) return new(CharacterSaveSnapshotStoreStatus.Conflict);
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (!await LockCharacterAsync(connection, transaction, request.AccountId, request.CharacterId,
                    cancellationToken))
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.NotFound, cancellationToken);
            if (!await HasActiveSessionAsync(connection, transaction, request.AccountId, request.SessionId,
                    nowUtc, cancellationToken))
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.SessionRejected, cancellationToken);
            var stored = await ReadSnapshotAsync(connection, transaction, request.CharacterId, cancellationToken);
            if (stored is null)
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.NotFound, cancellationToken);
            var associatedData = AssociatedData(request.CharacterId, stored.Value.Revision);
            if (!protection.TryUnprotect(associatedData, stored.Value.KeyId, stored.Value.ProtectedSnapshot,
                    out var payload))
            {
                CryptographicOperations.ZeroMemory(associatedData);
                CryptographicOperations.ZeroMemory(stored.Value.Hash);
                CryptographicOperations.ZeroMemory(stored.Value.ProtectedSnapshot);
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.Unavailable, cancellationToken);
            }
            var hash = SHA256.HashData(payload);
            var valid = payload.Length <= CharacterSaveSnapshotWriteRequestV1.MaxPayloadBytes &&
                        CryptographicOperations.FixedTimeEquals(hash, stored.Value.Hash);
            CryptographicOperations.ZeroMemory(hash);
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(stored.Value.Hash);
            CryptographicOperations.ZeroMemory(stored.Value.ProtectedSnapshot);
            if (!valid)
            {
                CryptographicOperations.ZeroMemory(payload);
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.Unavailable, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new(CharacterSaveSnapshotStoreStatus.Stored, stored.Value.Revision,
                new CharacterSaveSnapshotV1
                {
                    CorrelationId = request.CorrelationId,
                    CharacterId = request.CharacterId,
                    LeaseVersion = stored.Value.LeaseVersion,
                    Revision = stored.Value.Revision,
                    Payload = payload
                });
        }
        catch (MySqlException)
        {
            return new(CharacterSaveSnapshotStoreStatus.Unavailable);
        }
    }

    public async Task<CharacterSaveSnapshotStoreResult> WriteAsync(string instanceId,
        ReadOnlyMemory<byte> leaseTokenHash, CharacterSaveSnapshotWriteRequestV1 request,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (!AdminQuery.ValidIdentifier(instanceId) || leaseTokenHash.Length != 32 || !request.IsValid())
            return new(CharacterSaveSnapshotStoreStatus.Conflict);

        var payloadHash = SHA256.HashData(request.Payload);
        var associatedData = AssociatedData(request.CharacterId, request.Revision);
        if (!protection.TryProtect(associatedData, request.Payload, out var keyId, out var protectedSnapshot))
        {
            CryptographicOperations.ZeroMemory(payloadHash);
            CryptographicOperations.ZeroMemory(associatedData);
            return new(CharacterSaveSnapshotStoreStatus.Unavailable);
        }

        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var characterExists = await LockCharacterAsync(connection, transaction, request.CharacterId,
                cancellationToken);
            if (!characterExists)
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.NotFound, cancellationToken);

            if (!await HasCurrentLeaseAsync(connection, transaction, instanceId, leaseTokenHash,
                    request.CharacterId, request.LeaseVersion, nowUtc, cancellationToken))
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.LeaseRejected, cancellationToken);

            var existing = await ReadRevisionForUpdateAsync(connection, transaction, request.CharacterId,
                cancellationToken);
            if (existing is null)
            {
                if (request.Revision != 1)
                    return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.Conflict, cancellationToken);
                await InsertAsync(connection, transaction, request, keyId, payloadHash, protectedSnapshot,
                    nowUtc, cancellationToken);
            }
            else if (request.Revision == existing.Value.Revision)
            {
                var duplicate = existing.Value.LeaseVersion == request.LeaseVersion &&
                                existing.Value.SchemaVersion == request.SchemaVersion &&
                                CryptographicOperations.FixedTimeEquals(existing.Value.Hash, payloadHash);
                CryptographicOperations.ZeroMemory(existing.Value.Hash);
                await transaction.RollbackAsync(cancellationToken);
                return new(duplicate ? CharacterSaveSnapshotStoreStatus.Duplicate : CharacterSaveSnapshotStoreStatus.Conflict,
                    duplicate ? request.Revision : null);
            }
            else if (request.Revision != existing.Value.Revision + 1)
            {
                CryptographicOperations.ZeroMemory(existing.Value.Hash);
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.Conflict, cancellationToken);
            }
            else
            {
                CryptographicOperations.ZeroMemory(existing.Value.Hash);
                await UpdateAsync(connection, transaction, request, keyId, payloadHash, protectedSnapshot,
                    nowUtc, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new(CharacterSaveSnapshotStoreStatus.Stored, request.Revision);
        }
        catch (MySqlException)
        {
            return new(CharacterSaveSnapshotStoreStatus.Unavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadHash);
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(protectedSnapshot);
        }
    }

    public async Task<CharacterSaveSnapshotStoreResult> ReadAsync(string instanceId,
        ReadOnlyMemory<byte> leaseTokenHash, CharacterSaveSnapshotReadRequestV1 request,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (!AdminQuery.ValidIdentifier(instanceId) || leaseTokenHash.Length != 32 || !request.IsValid())
            return new(CharacterSaveSnapshotStoreStatus.Conflict);

        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var characterExists = await LockCharacterAsync(connection, transaction, request.CharacterId,
                cancellationToken);
            if (!characterExists)
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.NotFound, cancellationToken);

            if (!await HasCurrentLeaseAsync(connection, transaction, instanceId, leaseTokenHash,
                    request.CharacterId, request.LeaseVersion, nowUtc, cancellationToken))
                return await RollbackAsync(transaction, CharacterSaveSnapshotStoreStatus.LeaseRejected, cancellationToken);

            var stored = await ReadSnapshotAsync(connection, transaction, request.CharacterId, cancellationToken);
            if (stored is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(CharacterSaveSnapshotStoreStatus.NotFound);
            }

            var associatedData = AssociatedData(request.CharacterId, stored.Value.Revision);
            if (!protection.TryUnprotect(associatedData, stored.Value.KeyId, stored.Value.ProtectedSnapshot,
                    out var payload))
            {
                CryptographicOperations.ZeroMemory(associatedData);
                CryptographicOperations.ZeroMemory(stored.Value.Hash);
                CryptographicOperations.ZeroMemory(stored.Value.ProtectedSnapshot);
                await transaction.RollbackAsync(cancellationToken);
                return new(CharacterSaveSnapshotStoreStatus.Unavailable);
            }

            var actualHash = SHA256.HashData(payload);
            var hashMatches = payload.Length <= CharacterSaveSnapshotWriteRequestV1.MaxPayloadBytes &&
                              CryptographicOperations.FixedTimeEquals(actualHash, stored.Value.Hash);
            CryptographicOperations.ZeroMemory(actualHash);
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(stored.Value.Hash);
            CryptographicOperations.ZeroMemory(stored.Value.ProtectedSnapshot);
            if (!hashMatches)
            {
                CryptographicOperations.ZeroMemory(payload);
                await transaction.RollbackAsync(cancellationToken);
                return new(CharacterSaveSnapshotStoreStatus.Unavailable);
            }

            await transaction.CommitAsync(cancellationToken);
            return new(CharacterSaveSnapshotStoreStatus.Stored, stored.Value.Revision,
                new CharacterSaveSnapshotV1
                {
                    CorrelationId = request.CorrelationId,
                    CharacterId = request.CharacterId,
                    LeaseVersion = request.LeaseVersion,
                    Revision = stored.Value.Revision,
                    Payload = payload
                });
        }
        catch (MySqlException)
        {
            return new(CharacterSaveSnapshotStoreStatus.Unavailable);
        }
    }

    private static async Task<bool> LockCharacterAsync(MySqlConnection connection, MySqlTransaction transaction,
        long characterId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT character_id FROM characters WHERE character_id = @character AND deleted_at_utc IS NULL FOR UPDATE;";
        command.Parameters.AddWithValue("@character", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> LockCharacterAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid accountId, long characterId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT character_id FROM characters WHERE account_id = @account AND character_id = @character AND deleted_at_utc IS NULL FOR UPDATE;";
        command.Parameters.AddWithValue("@account", accountId.ToString("D"));
        command.Parameters.AddWithValue("@character", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasActiveSessionAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid accountId, Guid sessionId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.session_id FROM gateway_sessions s
            INNER JOIN accounts a ON a.account_id = s.account_id
            WHERE s.session_id = @session AND s.account_id = @account
              AND s.revoked_at_utc IS NULL AND s.expires_at_utc > @now AND a.status = 'active'
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("@session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("@account", accountId.ToString("D"));
        command.Parameters.AddWithValue("@now", nowUtc);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasAnyLeaseAsync(MySqlConnection connection, MySqlTransaction transaction,
        long characterId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT character_id FROM character_leases WHERE character_id = @character FOR UPDATE;";
        command.Parameters.AddWithValue("@character", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasCurrentLeaseAsync(MySqlConnection connection, MySqlTransaction transaction,
        string instanceId, ReadOnlyMemory<byte> leaseTokenHash, long characterId, long leaseVersion,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT l.character_id
            FROM character_leases l
            INNER JOIN gateway_sessions s ON s.session_id = l.session_id
            INNER JOIN accounts a ON a.account_id = s.account_id
            WHERE l.character_id = @character AND l.instance_id = @instance
              AND l.lease_version = @lease_version AND l.lease_token_hash = @token_hash
              AND l.valid_until_utc > @now_utc AND s.revoked_at_utc IS NULL
              AND s.expires_at_utc > @now_utc AND a.status = 'active'
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("@character", characterId);
        command.Parameters.AddWithValue("@instance", instanceId);
        command.Parameters.AddWithValue("@lease_version", leaseVersion);
        var tokenHash = leaseTokenHash.ToArray();
        command.Parameters.AddWithValue("@token_hash", tokenHash);
        command.Parameters.AddWithValue("@now_utc", nowUtc);
        try { return await command.ExecuteScalarAsync(cancellationToken) is not null; }
        finally { CryptographicOperations.ZeroMemory(tokenHash); }
    }

    private static async Task<(long Revision, long LeaseVersion, int SchemaVersion, byte[] Hash)?>
        ReadRevisionForUpdateAsync(MySqlConnection connection, MySqlTransaction transaction, long characterId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot_revision, lease_version, schema_version, snapshot_hash
            FROM character_save_snapshots WHERE character_id = @character FOR UPDATE;
            """;
        command.Parameters.AddWithValue("@character", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), (byte[])reader.GetValue(3));
    }

    private static async Task InsertAsync(MySqlConnection connection, MySqlTransaction transaction,
        CharacterSaveSnapshotWriteRequestV1 request, string keyId, byte[] hash, byte[] protectedSnapshot,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO character_save_snapshots
                (character_id, snapshot_revision, lease_version, schema_version, key_id,
                 snapshot_hash, protected_snapshot, updated_at_utc)
            VALUES (@character, @revision, @lease_version, @schema_version, @key_id,
                    @hash, @snapshot, @updated_at);
            """;
        AddSnapshotParameters(command, request, keyId, hash, protectedSnapshot, nowUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAsync(MySqlConnection connection, MySqlTransaction transaction,
        CharacterSaveSnapshotWriteRequestV1 request, string keyId, byte[] hash, byte[] protectedSnapshot,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE character_save_snapshots
            SET snapshot_revision = @revision, lease_version = @lease_version,
                schema_version = @schema_version, key_id = @key_id, snapshot_hash = @hash,
                protected_snapshot = @snapshot, updated_at_utc = @updated_at
            WHERE character_id = @character;
            """;
        AddSnapshotParameters(command, request, keyId, hash, protectedSnapshot, nowUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddSnapshotParameters(MySqlCommand command, CharacterSaveSnapshotWriteRequestV1 request,
        string keyId, byte[] hash, byte[] protectedSnapshot, DateTime nowUtc)
    {
        command.Parameters.AddWithValue("@character", request.CharacterId);
        command.Parameters.AddWithValue("@revision", request.Revision);
        command.Parameters.AddWithValue("@lease_version", request.LeaseVersion);
        command.Parameters.AddWithValue("@schema_version", request.SchemaVersion);
        command.Parameters.AddWithValue("@key_id", keyId);
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@snapshot", protectedSnapshot);
        command.Parameters.AddWithValue("@updated_at", nowUtc);
    }

    private static async Task<(long Revision, long LeaseVersion, int SchemaVersion, string KeyId,
        byte[] Hash, byte[] ProtectedSnapshot)?> ReadSnapshotAsync(MySqlConnection connection,
        MySqlTransaction transaction, long characterId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot_revision, lease_version, schema_version, key_id, snapshot_hash, protected_snapshot
            FROM character_save_snapshots WHERE character_id = @character FOR UPDATE;
            """;
        command.Parameters.AddWithValue("@character", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3),
            (byte[])reader.GetValue(4), (byte[])reader.GetValue(5));
    }

    private static byte[] AssociatedData(long characterId, long revision)
    {
        var value = new byte[sizeof(long) * 2];
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0, sizeof(long)), characterId);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(sizeof(long)), revision);
        return value;
    }

    private static async Task<CharacterSaveSnapshotStoreResult> RollbackAsync(MySqlTransaction transaction,
        CharacterSaveSnapshotStoreStatus status, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return new CharacterSaveSnapshotStoreResult(status);
    }

    private static async Task<CharacterFactionReputationReadResult> RollbackReputationAsync(
        MySqlTransaction transaction, CharacterFactionReputationReadStatus status,
        CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return new(status);
    }
}

public enum CharacterSaveSnapshotReadStatus
{
    Found,
    LeaseRejected,
    NotFound,
    Unavailable
}

public sealed record CharacterSaveSnapshotReadResult(
    CharacterSaveSnapshotReadStatus Status,
    CharacterSaveSnapshotV1? Snapshot = null);

public sealed class CharacterSaveSnapshotService(
    ICharacterSaveSnapshotStore store,
    TimeProvider timeProvider)
{
    public async Task<CharacterSaveSnapshotWriteResultV1> WriteAsync(
        CharacterSaveSnapshotWriteRequestV1 request, string authenticatedInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsValid() || !AdminQuery.ValidIdentifier(authenticatedInstanceId))
            return Reject(request.CorrelationId, "snapshot_request_invalid");
        if (!TryHashLeaseToken(request.LeaseToken, out var tokenHash))
            return Reject(request.CorrelationId, "lease_token_invalid");

        try
        {
            var stored = await store.WriteAsync(authenticatedInstanceId, tokenHash, request,
                timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            return stored.Status switch
            {
                CharacterSaveSnapshotStoreStatus.Stored => Accept(request.CorrelationId, "stored", stored.Revision),
                CharacterSaveSnapshotStoreStatus.Duplicate => Accept(request.CorrelationId, "duplicate", stored.Revision),
                CharacterSaveSnapshotStoreStatus.LeaseRejected => Reject(request.CorrelationId, "character_lease_rejected"),
                CharacterSaveSnapshotStoreStatus.NotFound => Reject(request.CorrelationId, "character_not_found"),
                CharacterSaveSnapshotStoreStatus.Unavailable => Reject(request.CorrelationId, "snapshot_persistence_unavailable"),
                _ => Reject(request.CorrelationId, "snapshot_revision_conflict")
            };
        }
        finally { CryptographicOperations.ZeroMemory(tokenHash); }
    }

    public async Task<CharacterSaveSnapshotReadResult> ReadAsync(
        CharacterSaveSnapshotReadRequestV1 request, string authenticatedInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsValid() || !AdminQuery.ValidIdentifier(authenticatedInstanceId))
            return new(CharacterSaveSnapshotReadStatus.LeaseRejected);
        if (!TryHashLeaseToken(request.LeaseToken, out var tokenHash))
            return new(CharacterSaveSnapshotReadStatus.LeaseRejected);

        try
        {
            var stored = await store.ReadAsync(authenticatedInstanceId, tokenHash, request,
                timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            return stored.Status switch
            {
                CharacterSaveSnapshotStoreStatus.Stored when stored.Snapshot is not null =>
                    new(CharacterSaveSnapshotReadStatus.Found, stored.Snapshot),
                CharacterSaveSnapshotStoreStatus.LeaseRejected => new(CharacterSaveSnapshotReadStatus.LeaseRejected),
                CharacterSaveSnapshotStoreStatus.NotFound => new(CharacterSaveSnapshotReadStatus.NotFound),
                _ => new(CharacterSaveSnapshotReadStatus.Unavailable)
            };
        }
        finally { CryptographicOperations.ZeroMemory(tokenHash); }
    }

    private static bool TryHashLeaseToken(string value, out byte[] tokenHash)
    {
        tokenHash = [];
        try
        {
            var token = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
            if (token.Length != 32)
            {
                CryptographicOperations.ZeroMemory(token);
                return false;
            }
            tokenHash = SHA256.HashData(token);
            CryptographicOperations.ZeroMemory(token);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static CharacterSaveSnapshotWriteResultV1 Accept(Guid correlationId, string reasonCode, long? revision) => new()
    {
        CorrelationId = correlationId,
        Accepted = revision is > 0,
        ReasonCode = revision is > 0 ? reasonCode : "snapshot_store_invalid_response",
        Revision = revision is > 0 ? revision : null
    };

    private static CharacterSaveSnapshotWriteResultV1 Reject(Guid correlationId, string reasonCode) => new()
    {
        CorrelationId = correlationId,
        Accepted = false,
        ReasonCode = reasonCode
    };
}
