using System.Buffers.Binary;
using System.Security.Cryptography;
using LancerNexus.Protocol;
using MessagePack;
using MySqlConnector;

namespace LancerNexus.Gateway;

public enum MarketTradeCommitStoreStatus
{
    Prepared,
    Reserved,
    Committed,
    Duplicate,
    Aborted,
    Conflict,
    LeaseRejected,
    SnapshotConflict,
    NotFound,
    Unavailable
}

public sealed record MarketTradeCommitStoreResult(MarketTradeCommitStoreStatus Status, long? SnapshotRevision = null);

public interface IMarketTradeCommitStore
{
    Task<MarketTradeCommitStoreResult> PrepareAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
        MarketTradeCommitRequestV1 request, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<MarketTradeCommitStoreResult> MarkReservedAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
        MarketTradeCommitRequestV1 request, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<MarketTradeCommitStoreResult> CommitAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
        MarketTradeCommitRequestV1 request, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<MarketTradeCommitStoreResult> MarkAbortedAsync(Guid tradeId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingMarketTradeIntent>> ReadPendingIntentsAsync(int maxCount,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PendingMarketTradeIntent>>([]);
    Task<bool> HasActiveIntentLeaseAsync(Guid tradeId, DateTime nowUtc,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
    Task<MarketTradeCommitStoreResult> MarkRecoveredReservedAsync(Guid tradeId, DateTime nowUtc,
        CancellationToken cancellationToken = default) => Task.FromResult(new MarketTradeCommitStoreResult(MarketTradeCommitStoreStatus.Unavailable));
    Task<MarketTradeCommitStoreResult> CommitRecoveredAsync(Guid tradeId, DateTime nowUtc,
        CancellationToken cancellationToken = default) => Task.FromResult(new MarketTradeCommitStoreResult(MarketTradeCommitStoreStatus.Unavailable));
    Task ScheduleIntentRetryAsync(Guid tradeId, DateTime nowUtc,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<IReadOnlyList<MarketTradeOutboxItem>> ReadPendingOutboxAsync(int maxCount,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MarketTradeOutboxItem>>([]);
    Task MarkOutboxDeliveredAsync(Guid tradeId, DateTime nowUtc,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task ScheduleOutboxRetryAsync(Guid tradeId, DateTime nowUtc,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed record MarketTradeOutboxItem(Guid TradeId, Guid CorrelationId, int AttemptCount);
public sealed record PendingMarketTradeIntent(Guid TradeId, string InstanceId, long LeaseVersion,
    byte State, MarketTradeQuoteReservationRequestV1 Reservation, int AttemptCount);

public sealed class MarketTradeCommitStoreNotConfigured : IMarketTradeCommitStore
{
    private static Task<MarketTradeCommitStoreResult> Unavailable() =>
        Task.FromResult(new MarketTradeCommitStoreResult(MarketTradeCommitStoreStatus.Unavailable));

    public Task<MarketTradeCommitStoreResult> PrepareAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
        MarketTradeCommitRequestV1 request, DateTime nowUtc, CancellationToken cancellationToken = default) => Unavailable();
    public Task<MarketTradeCommitStoreResult> MarkReservedAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
        MarketTradeCommitRequestV1 request, DateTime nowUtc, CancellationToken cancellationToken = default) => Unavailable();
    public Task<MarketTradeCommitStoreResult> CommitAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
        MarketTradeCommitRequestV1 request, DateTime nowUtc, CancellationToken cancellationToken = default) => Unavailable();
    public Task<MarketTradeCommitStoreResult> MarkAbortedAsync(Guid tradeId, CancellationToken cancellationToken = default) => Unavailable();
}

/// <summary>Persists a market intent before Coordinator reservation and atomically commits its character snapshot, ledger and outbox.</summary>
public sealed class MySqlMarketTradeCommitStore(string connectionString, TransferSnapshotProtection protection)
    : IMarketTradeCommitStore
{
    public async Task<MarketTradeCommitStoreResult> PrepareAsync(string instanceId,
        ReadOnlyMemory<byte> leaseTokenHash, MarketTradeCommitRequestV1 request, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (!ValidCaller(instanceId, leaseTokenHash, request))
            return new(MarketTradeCommitStoreStatus.Conflict);
        var requestDigest = Digest(request);
        var payloadHash = SHA256.HashData(request.Snapshot.Payload);
        var associatedData = CharacterSaveSnapshotProtectionData(request.Snapshot.CharacterId,
            request.Snapshot.Revision);
        if (!protection.TryProtect(associatedData, request.Snapshot.Payload, out var keyId,
                out var protectedSnapshot))
        {
            Zero(requestDigest, payloadHash, associatedData);
            return new(MarketTradeCommitStoreStatus.Unavailable);
        }
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (!await MySqlCharacterSaveSnapshotStore.LockCharacterAsync(connection, transaction,
                    request.Snapshot.CharacterId, cancellationToken))
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.NotFound, cancellationToken);

            var stored = await ReadIntentAsync(connection, transaction, request.TradeId, cancellationToken);
            if (stored is not null)
            {
                var same = CryptographicOperations.FixedTimeEquals(stored.RequestDigest, requestDigest) &&
                           string.Equals(stored.InstanceId, instanceId, StringComparison.Ordinal) &&
                           stored.LeaseVersion == request.Snapshot.LeaseVersion &&
                           CryptographicOperations.FixedTimeEquals(stored.LeaseTokenHash, leaseTokenHash.Span);
                var state = stored.State;
                Zero(stored.RequestDigest, stored.LeaseTokenHash);
                if (!same || state == 4)
                    return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Conflict, cancellationToken);
                if (state == 3)
                    return await CommitTransactionAsync(transaction,
                        new(MarketTradeCommitStoreStatus.Duplicate, request.Snapshot.Revision), cancellationToken);
                if (!await MySqlCharacterSaveSnapshotStore.HasCurrentLeaseAsync(connection, transaction,
                        instanceId, leaseTokenHash, request.Snapshot.CharacterId,
                        request.Snapshot.LeaseVersion, nowUtc, cancellationToken))
                    return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.LeaseRejected, cancellationToken);
                return await CommitTransactionAsync(transaction,
                    new(state == 2 ? MarketTradeCommitStoreStatus.Reserved : MarketTradeCommitStoreStatus.Prepared),
                    cancellationToken);
            }

            if (!await MySqlCharacterSaveSnapshotStore.HasCurrentLeaseAsync(connection, transaction,
                    instanceId, leaseTokenHash, request.Snapshot.CharacterId,
                    request.Snapshot.LeaseVersion, nowUtc, cancellationToken))
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.LeaseRejected, cancellationToken);

            var snapshot = await MySqlCharacterSaveSnapshotStore.ReadRevisionForUpdateAsync(connection, transaction,
                request.Snapshot.CharacterId, cancellationToken);
            var expectedRevision = snapshot?.Revision + 1 ?? 1;
            if (request.Snapshot.Revision != expectedRevision)
            {
                if (snapshot is { } existing) CryptographicOperations.ZeroMemory(existing.Hash);
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.SnapshotConflict, cancellationToken);
            }
            if (snapshot is { } current) CryptographicOperations.ZeroMemory(current.Hash);

            await InsertIntentAsync(connection, transaction, instanceId, leaseTokenHash, request, requestDigest,
                payloadHash, keyId, protectedSnapshot, nowUtc, cancellationToken);
            return await CommitTransactionAsync(transaction,
                new(MarketTradeCommitStoreStatus.Prepared), cancellationToken);
        }
        catch (MySqlException)
        {
            return new(MarketTradeCommitStoreStatus.Unavailable);
        }
        finally
        {
            Zero(requestDigest, payloadHash, associatedData, protectedSnapshot);
        }
    }

    public async Task<MarketTradeCommitStoreResult> MarkReservedAsync(string instanceId,
        ReadOnlyMemory<byte> leaseTokenHash, MarketTradeCommitRequestV1 request, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (!ValidCaller(instanceId, leaseTokenHash, request))
            return new(MarketTradeCommitStoreStatus.Conflict);
        var digest = Digest(request);
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (!await MySqlCharacterSaveSnapshotStore.LockCharacterAsync(connection, transaction,
                    request.Snapshot.CharacterId, cancellationToken))
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.NotFound, cancellationToken);
            var stored = await ReadIntentAsync(connection, transaction, request.TradeId, cancellationToken);
            if (stored is null)
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.NotFound, cancellationToken);
            var matches = Matches(stored, instanceId, leaseTokenHash.Span, request, digest);
            var state = stored.State;
            Zero(stored.RequestDigest, stored.LeaseTokenHash);
            if (!matches || state == 4)
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Conflict, cancellationToken);
            if (state == 3)
                return await CommitTransactionAsync(transaction,
                    new(MarketTradeCommitStoreStatus.Duplicate, request.Snapshot.Revision), cancellationToken);
            if (!await MySqlCharacterSaveSnapshotStore.HasCurrentLeaseAsync(connection, transaction,
                    instanceId, leaseTokenHash, request.Snapshot.CharacterId,
                    request.Snapshot.LeaseVersion, nowUtc, cancellationToken))
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.LeaseRejected, cancellationToken);
            if (state == 1)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE gateway_market_trade_intents SET state = 2, updated_at_utc = @now WHERE trade_id = @trade AND state = 1";
                command.Parameters.AddWithValue("@now", nowUtc);
                command.Parameters.AddWithValue("@trade", request.TradeId.ToByteArray());
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Conflict, cancellationToken);
            }
            return await CommitTransactionAsync(transaction, new(MarketTradeCommitStoreStatus.Reserved), cancellationToken);
        }
        catch (MySqlException)
        {
            return new(MarketTradeCommitStoreStatus.Unavailable);
        }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    public async Task<MarketTradeCommitStoreResult> CommitAsync(string instanceId,
        ReadOnlyMemory<byte> leaseTokenHash, MarketTradeCommitRequestV1 request, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (!ValidCaller(instanceId, leaseTokenHash, request))
            return new(MarketTradeCommitStoreStatus.Conflict);
        var digest = Digest(request);
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (!await MySqlCharacterSaveSnapshotStore.LockCharacterAsync(connection, transaction,
                    request.Snapshot.CharacterId, cancellationToken))
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.NotFound, cancellationToken);
            var stored = await ReadIntentAsync(connection, transaction, request.TradeId, cancellationToken);
            if (stored is null)
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.NotFound, cancellationToken);
            var matches = Matches(stored, instanceId, leaseTokenHash.Span, request, digest);
            var state = stored.State;
            Zero(stored.RequestDigest, stored.LeaseTokenHash);
            if (!matches || state == 4)
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Conflict, cancellationToken);
            if (state == 3)
                return await CommitTransactionAsync(transaction,
                    new(MarketTradeCommitStoreStatus.Duplicate, request.Snapshot.Revision), cancellationToken);
            if (state != 2)
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Conflict, cancellationToken);
            if (!await MySqlCharacterSaveSnapshotStore.HasCurrentLeaseAsync(connection, transaction,
                    instanceId, leaseTokenHash, request.Snapshot.CharacterId,
                    request.Snapshot.LeaseVersion, nowUtc, cancellationToken))
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.LeaseRejected, cancellationToken);

            var current = await MySqlCharacterSaveSnapshotStore.ReadRevisionForUpdateAsync(connection, transaction,
                request.Snapshot.CharacterId, cancellationToken);
            if ((current?.Revision ?? 0) + 1 != request.Snapshot.Revision)
            {
                if (current is { } old) CryptographicOperations.ZeroMemory(old.Hash);
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.SnapshotConflict, cancellationToken);
            }
            if (current is { } existing) CryptographicOperations.ZeroMemory(existing.Hash);

            var intent = await ReadProtectedPayloadAsync(connection, transaction, request.TradeId,
                cancellationToken);
            if (intent is null)
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Unavailable, cancellationToken);
            var associatedData = CharacterSaveSnapshotProtectionData(request.Snapshot.CharacterId,
                request.Snapshot.Revision);
            if (!protection.TryUnprotect(associatedData, intent.KeyId, intent.ProtectedSnapshot,
                    out var payload))
            {
                Zero(associatedData, intent.Hash, intent.ProtectedSnapshot);
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Unavailable, cancellationToken);
            }
            var actualHash = SHA256.HashData(payload);
            var payloadMatches = CryptographicOperations.FixedTimeEquals(actualHash, intent.Hash) &&
                                 CryptographicOperations.FixedTimeEquals(payload, request.Snapshot.Payload);
            CryptographicOperations.ZeroMemory(actualHash);
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(associatedData);
            if (!payloadMatches)
            {
                Zero(intent.Hash, intent.ProtectedSnapshot);
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Conflict, cancellationToken);
            }

            await UpdateSnapshotFromIntentAsync(connection, transaction, request, intent.KeyId,
                intent.Hash, intent.ProtectedSnapshot, nowUtc, cancellationToken);
            Zero(intent.Hash, intent.ProtectedSnapshot);
            await InsertLedgerAndOutboxAsync(connection, transaction, request, digest, nowUtc, cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE gateway_market_trade_intents SET state = 3, updated_at_utc = @now WHERE trade_id = @trade AND state = 2";
                command.Parameters.AddWithValue("@now", nowUtc);
                command.Parameters.AddWithValue("@trade", request.TradeId.ToByteArray());
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Conflict, cancellationToken);
            }
            return await CommitTransactionAsync(transaction,
                new(MarketTradeCommitStoreStatus.Committed, request.Snapshot.Revision), cancellationToken);
        }
        catch (MySqlException)
        {
            return new(MarketTradeCommitStoreStatus.Unavailable);
        }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    public async Task<MarketTradeCommitStoreResult> MarkAbortedAsync(Guid tradeId,
        CancellationToken cancellationToken = default)
    {
        if (tradeId == Guid.Empty) return new(MarketTradeCommitStoreStatus.Conflict);
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT state FROM gateway_market_trade_intents WHERE trade_id = @trade FOR UPDATE";
                read.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
                var value = await read.ExecuteScalarAsync(cancellationToken);
                if (value is null or DBNull)
                    return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.NotFound, cancellationToken);
                var state = Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture);
                if (state == 3)
                    return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Committed, cancellationToken);
                if (state == 4)
                    return await CommitTransactionAsync(transaction, new(MarketTradeCommitStoreStatus.Aborted), cancellationToken);
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE gateway_market_trade_intents SET state = 4, updated_at_utc = UTC_TIMESTAMP(6) WHERE trade_id = @trade AND state IN (1, 2)";
            command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
            var changed = await command.ExecuteNonQueryAsync(cancellationToken);
            return await CommitTransactionAsync(transaction, new(changed == 1
                ? MarketTradeCommitStoreStatus.Aborted : MarketTradeCommitStoreStatus.Conflict), cancellationToken);
        }
        catch (MySqlException) { return new(MarketTradeCommitStoreStatus.Unavailable); }
    }

    public async Task<IReadOnlyList<MarketTradeOutboxItem>> ReadPendingOutboxAsync(int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (maxCount is < 1 or > 100) return [];
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT trade_id, correlation_id, attempt_count FROM gateway_market_trade_outbox
                WHERE delivered_at_utc IS NULL AND next_attempt_at_utc <= UTC_TIMESTAMP(6)
                ORDER BY next_attempt_at_utc, created_at_utc LIMIT @limit
                """;
            command.Parameters.AddWithValue("@limit", maxCount);
            var items = new List<MarketTradeOutboxItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new(new Guid((byte[])reader[0]), new Guid((byte[])reader[1]), reader.GetInt32(2)));
            return items;
        }
        catch (MySqlException) { return []; }
    }

    public async Task MarkOutboxDeliveredAsync(Guid tradeId, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (tradeId == Guid.Empty) return;
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE gateway_market_trade_outbox SET delivered_at_utc = @now WHERE trade_id = @trade AND delivered_at_utc IS NULL";
            command.Parameters.AddWithValue("@now", nowUtc);
            command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException) { }
    }

    public async Task ScheduleOutboxRetryAsync(Guid tradeId, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (tradeId == Guid.Empty) return;
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE gateway_market_trade_outbox
                SET attempt_count = attempt_count + 1,
                    next_attempt_at_utc = DATE_ADD(@now, INTERVAL LEAST(300, POW(2, LEAST(attempt_count, 8))) SECOND)
                WHERE trade_id = @trade AND delivered_at_utc IS NULL
                """;
            command.Parameters.AddWithValue("@now", nowUtc);
            command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException) { }
    }

    public async Task<IReadOnlyList<PendingMarketTradeIntent>> ReadPendingIntentsAsync(int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (maxCount is < 1 or > 100) return [];
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT trade_id, instance_id, lease_version, state, market_id, system_id, good_id,
                       trade_side, quantity, expected_market_revision, expected_reputation_revision,
                       reputation_price_factor_parts_per_million, quote_unit_price, total_price,
                       character_id, market_faction_id, attempt_count
                FROM gateway_market_trade_intents
                WHERE state IN (1, 2) AND next_attempt_at_utc <= UTC_TIMESTAMP(6)
                ORDER BY next_attempt_at_utc, created_at_utc LIMIT @limit
                """;
            command.Parameters.AddWithValue("@limit", maxCount);
            var intents = new List<PendingMarketTradeIntent>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var tradeId = new Guid((byte[])reader[0]);
                var reservation = new MarketTradeQuoteReservationRequestV1
                {
                    CorrelationId = tradeId,
                    TradeId = tradeId,
                    MarketId = reader.GetString(4),
                    SystemId = reader.GetString(5),
                    GoodId = reader.GetString(6),
                    Side = (MarketTradeSideV1)reader.GetByte(7),
                    Quantity = reader.GetInt64(8),
                    ExpectedMarketRevision = reader.GetInt64(9),
                    ExpectedReputationRevision = reader.GetInt64(10),
                    ReputationPriceFactorPartsPerMillion = reader.GetInt64(11),
                    ExpectedQuoteUnitPrice = reader.GetInt64(12),
                    ExpectedTotalPrice = reader.GetInt64(13),
                    CharacterId = reader.GetInt64(14),
                    MarketFactionId = reader.GetString(15)
                };
                intents.Add(new(tradeId, reader.GetString(1), reader.GetInt64(2), reader.GetByte(3),
                    reservation, reader.GetInt32(16)));
            }
            return intents;
        }
        catch (MySqlException) { return []; }
    }

    public async Task<bool> HasActiveIntentLeaseAsync(Guid tradeId, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (tradeId == Guid.Empty) return false;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                SELECT i.trade_id
                FROM gateway_market_trade_intents i
                INNER JOIN character_leases l ON l.character_id = i.character_id
                    AND l.instance_id = i.instance_id AND l.lease_version = i.lease_version
                    AND l.lease_token_hash = i.lease_token_hash AND l.valid_until_utc > @now
                INNER JOIN gateway_sessions s ON s.session_id = l.session_id
                INNER JOIN accounts a ON a.account_id = s.account_id
                INNER JOIN characters c ON c.character_id = i.character_id AND c.deleted_at_utc IS NULL
                WHERE i.trade_id = @trade AND i.state IN (1, 2)
                  AND s.revoked_at_utc IS NULL AND s.expires_at_utc > @now AND a.status = 'active'
                LIMIT 1
            """;
        command.Parameters.AddWithValue("@now", nowUtc);
        command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<MarketTradeCommitStoreResult> MarkRecoveredReservedAsync(Guid tradeId,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (tradeId == Guid.Empty) return new(MarketTradeCommitStoreStatus.Conflict);
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var intent = await ReadRecoveryLeaseAsync(connection, transaction, tradeId, cancellationToken);
            if (intent is null)
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.NotFound, cancellationToken);
            if (intent.State == 3)
                return await CommitTransactionAsync(transaction,
                    new(MarketTradeCommitStoreStatus.Duplicate, intent.SnapshotRevision), cancellationToken);
            if (intent.State == 4)
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Conflict, cancellationToken);
            if (!await MySqlCharacterSaveSnapshotStore.LockCharacterAsync(connection, transaction,
                    intent.CharacterId, cancellationToken) ||
                !await MySqlCharacterSaveSnapshotStore.HasCurrentLeaseAsync(connection, transaction,
                    intent.InstanceId, intent.LeaseTokenHash, intent.CharacterId, intent.LeaseVersion,
                    nowUtc, cancellationToken))
                return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.LeaseRejected, cancellationToken);
            if (intent.State == 1)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE gateway_market_trade_intents SET state = 2, updated_at_utc = @now, next_attempt_at_utc = @now WHERE trade_id = @trade AND state = 1";
                command.Parameters.AddWithValue("@now", nowUtc);
                command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    return await RollbackAsync(transaction, MarketTradeCommitStoreStatus.Conflict, cancellationToken);
            }
            return await CommitTransactionAsync(transaction, new(MarketTradeCommitStoreStatus.Reserved), cancellationToken);
        }
        catch (MySqlException) { return new(MarketTradeCommitStoreStatus.Unavailable); }
    }

    public async Task<MarketTradeCommitStoreResult> CommitRecoveredAsync(Guid tradeId, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var recovery = await ReadRecoveryIntentAsync(tradeId, cancellationToken);
        if (recovery is null) return new(MarketTradeCommitStoreStatus.NotFound);
        if (recovery.State == 3)
            return new(MarketTradeCommitStoreStatus.Duplicate, recovery.SnapshotRevision);
        if (recovery.State != 2)
            return new(recovery.State == 4 ? MarketTradeCommitStoreStatus.Conflict : MarketTradeCommitStoreStatus.Unavailable);
        var associatedData = CharacterSaveSnapshotProtectionData(recovery.CharacterId, recovery.SnapshotRevision);
        if (!protection.TryUnprotect(associatedData, recovery.KeyId, recovery.ProtectedSnapshot, out var payload))
        {
            Zero(associatedData, recovery.Hash, recovery.ProtectedSnapshot, recovery.LeaseTokenHash,
                recovery.RequestDigest);
            return new(MarketTradeCommitStoreStatus.Unavailable);
        }
        try
        {
            var actualHash = SHA256.HashData(payload);
            var validPayload = CryptographicOperations.FixedTimeEquals(actualHash, recovery.Hash);
            CryptographicOperations.ZeroMemory(actualHash);
            if (!validPayload) return new(MarketTradeCommitStoreStatus.Unavailable);
            var snapshot = new CharacterSaveSnapshotWriteRequestV1
            {
                CorrelationId = tradeId,
                CharacterId = recovery.CharacterId,
                LeaseToken = new string('A', 43),
                LeaseVersion = recovery.LeaseVersion,
                Revision = recovery.SnapshotRevision,
                SchemaVersion = recovery.SnapshotSchemaVersion,
                Payload = payload
            };
            var reservation = recovery.Reservation with { CorrelationId = tradeId };
            var request = new MarketTradeCommitRequestV1
            {
                CorrelationId = recovery.CorrelationId,
                TradeId = tradeId,
                Reservation = reservation,
                Snapshot = snapshot
            };
            return await CommitAsync(recovery.InstanceId, recovery.LeaseTokenHash, request, nowUtc,
                cancellationToken);
        }
        finally
        {
            Zero(associatedData, payload, recovery.Hash, recovery.ProtectedSnapshot,
                recovery.LeaseTokenHash, recovery.RequestDigest);
        }
    }

    public async Task ScheduleIntentRetryAsync(Guid tradeId, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (tradeId == Guid.Empty) return;
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE gateway_market_trade_intents
                SET attempt_count = attempt_count + 1,
                    next_attempt_at_utc = DATE_ADD(@now, INTERVAL LEAST(300, POW(2, LEAST(attempt_count, 8))) SECOND)
                WHERE trade_id = @trade AND state IN (1, 2)
                """;
            command.Parameters.AddWithValue("@now", nowUtc);
            command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException) { }
    }

    private static async Task InsertIntentAsync(MySqlConnection connection, MySqlTransaction transaction,
        string instanceId, ReadOnlyMemory<byte> leaseTokenHash, MarketTradeCommitRequestV1 request,
        byte[] digest, byte[] payloadHash, string keyId, byte[] protectedSnapshot, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO gateway_market_trade_intents
                (trade_id, correlation_id, request_sha256, instance_id, lease_token_hash, character_id, lease_version,
                 snapshot_revision, snapshot_schema_version, snapshot_key_id, snapshot_hash, protected_snapshot,
                 market_id, system_id, good_id, trade_side, quantity, expected_market_revision,
                 expected_reputation_revision, reputation_price_factor_parts_per_million, quote_unit_price,
                 total_price, market_faction_id, state, attempt_count, next_attempt_at_utc,
                 created_at_utc, updated_at_utc)
            VALUES (@trade, @correlation, @digest, @instance, @lease_hash, @character, @lease_version,
                    @snapshot_revision, @snapshot_schema, @key_id, @snapshot_hash, @snapshot,
                    @market, @system, @good, @side, @quantity, @market_revision,
                    @reputation_revision, @factor, @unit_price, @total_price, @faction, 1, 0, @now, @now, @now)
            """;
        var quote = request.Reservation;
        command.Parameters.AddWithValue("@trade", request.TradeId.ToByteArray());
        command.Parameters.AddWithValue("@correlation", request.CorrelationId.ToByteArray());
        command.Parameters.AddWithValue("@digest", digest);
        command.Parameters.AddWithValue("@instance", instanceId);
        command.Parameters.AddWithValue("@lease_hash", leaseTokenHash.ToArray());
        command.Parameters.AddWithValue("@character", quote.CharacterId);
        command.Parameters.AddWithValue("@lease_version", request.Snapshot.LeaseVersion);
        command.Parameters.AddWithValue("@snapshot_revision", request.Snapshot.Revision);
        command.Parameters.AddWithValue("@snapshot_schema", request.Snapshot.SchemaVersion);
        command.Parameters.AddWithValue("@key_id", keyId);
        command.Parameters.AddWithValue("@snapshot_hash", payloadHash);
        command.Parameters.AddWithValue("@snapshot", protectedSnapshot);
        command.Parameters.AddWithValue("@market", quote.MarketId);
        command.Parameters.AddWithValue("@system", quote.SystemId);
        command.Parameters.AddWithValue("@good", quote.GoodId);
        command.Parameters.AddWithValue("@side", (byte)quote.Side);
        command.Parameters.AddWithValue("@quantity", quote.Quantity);
        command.Parameters.AddWithValue("@market_revision", quote.ExpectedMarketRevision);
        command.Parameters.AddWithValue("@reputation_revision", quote.ExpectedReputationRevision);
        command.Parameters.AddWithValue("@factor", quote.ReputationPriceFactorPartsPerMillion);
        command.Parameters.AddWithValue("@unit_price", quote.ExpectedQuoteUnitPrice);
        command.Parameters.AddWithValue("@total_price", quote.ExpectedTotalPrice);
        command.Parameters.AddWithValue("@faction", quote.MarketFactionId);
        command.Parameters.AddWithValue("@now", nowUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLedgerAndOutboxAsync(MySqlConnection connection,
        MySqlTransaction transaction, MarketTradeCommitRequestV1 request, byte[] digest, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var quote = request.Reservation;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO gateway_market_trade_ledger
                    (trade_id, request_sha256, character_id, snapshot_revision, market_id, system_id,
                     good_id, trade_side, quantity, total_price, committed_at_utc)
                VALUES (@trade, @digest, @character, @snapshot_revision, @market, @system,
                        @good, @side, @quantity, @total_price, @now)
                """;
            command.Parameters.AddWithValue("@trade", request.TradeId.ToByteArray());
            command.Parameters.AddWithValue("@digest", digest);
            command.Parameters.AddWithValue("@character", quote.CharacterId);
            command.Parameters.AddWithValue("@snapshot_revision", request.Snapshot.Revision);
            command.Parameters.AddWithValue("@market", quote.MarketId);
            command.Parameters.AddWithValue("@system", quote.SystemId);
            command.Parameters.AddWithValue("@good", quote.GoodId);
            command.Parameters.AddWithValue("@side", (byte)quote.Side);
            command.Parameters.AddWithValue("@quantity", quote.Quantity);
            command.Parameters.AddWithValue("@total_price", quote.ExpectedTotalPrice);
            command.Parameters.AddWithValue("@now", nowUtc);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO gateway_market_trade_outbox (trade_id, correlation_id, next_attempt_at_utc, created_at_utc) VALUES (@trade, @correlation, @now, @now)";
            command.Parameters.AddWithValue("@trade", request.TradeId.ToByteArray());
            command.Parameters.AddWithValue("@correlation", request.CorrelationId.ToByteArray());
            command.Parameters.AddWithValue("@now", nowUtc);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpdateSnapshotFromIntentAsync(MySqlConnection connection,
        MySqlTransaction transaction, MarketTradeCommitRequestV1 request, string keyId,
        byte[] payloadHash, byte[] protectedSnapshot, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var snapshot = request.Snapshot;
        if (snapshot.Revision == 1)
        {
            await MySqlCharacterSaveSnapshotStore.InsertAsync(connection, transaction, snapshot, keyId,
                payloadHash, protectedSnapshot, nowUtc, cancellationToken);
        }
        else
        {
            await MySqlCharacterSaveSnapshotStore.UpdateAsync(connection, transaction, snapshot, keyId,
                payloadHash, protectedSnapshot, nowUtc, cancellationToken);
        }
    }

    private static async Task<StoredIntent?> ReadIntentAsync(MySqlConnection connection,
        MySqlTransaction transaction, Guid tradeId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_sha256, instance_id, lease_token_hash, lease_version, state FROM gateway_market_trade_intents WHERE trade_id = @trade FOR UPDATE";
        command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new((byte[])reader[0], reader.GetString(1), (byte[])reader[2], reader.GetInt64(3), reader.GetByte(4))
            : null;
    }

    private static async Task<ProtectedIntent?> ReadProtectedPayloadAsync(MySqlConnection connection,
        MySqlTransaction transaction, Guid tradeId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT snapshot_key_id, snapshot_hash, protected_snapshot FROM gateway_market_trade_intents WHERE trade_id = @trade FOR UPDATE";
        command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), (byte[])reader[1], (byte[])reader[2]) : null;
    }

    private static async Task<RecoveryLease?> ReadRecoveryLeaseAsync(MySqlConnection connection,
        MySqlTransaction transaction, Guid tradeId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT instance_id, lease_token_hash, character_id, lease_version, snapshot_revision, state FROM gateway_market_trade_intents WHERE trade_id = @trade";
        command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), (byte[])reader[1], reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetByte(5))
            : null;
    }

    private async Task<RecoveryIntent?> ReadRecoveryIntentAsync(Guid tradeId,
        CancellationToken cancellationToken)
    {
        if (tradeId == Guid.Empty) return null;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                SELECT request_sha256, correlation_id, instance_id, lease_token_hash, character_id, lease_version,
                       snapshot_revision, snapshot_schema_version, snapshot_key_id, snapshot_hash,
                       protected_snapshot, market_id, system_id, good_id, trade_side, quantity,
                       expected_market_revision, expected_reputation_revision,
                       reputation_price_factor_parts_per_million, quote_unit_price, total_price,
                       market_faction_id, state
                FROM gateway_market_trade_intents WHERE trade_id = @trade
            """;
        command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var reservation = new MarketTradeQuoteReservationRequestV1
        {
            CorrelationId = tradeId,
            TradeId = tradeId,
            MarketId = reader.GetString(11),
            SystemId = reader.GetString(12),
            GoodId = reader.GetString(13),
            Side = (MarketTradeSideV1)reader.GetByte(14),
            Quantity = reader.GetInt64(15),
            ExpectedMarketRevision = reader.GetInt64(16),
            ExpectedReputationRevision = reader.GetInt64(17),
            ReputationPriceFactorPartsPerMillion = reader.GetInt64(18),
            ExpectedQuoteUnitPrice = reader.GetInt64(19),
            ExpectedTotalPrice = reader.GetInt64(20),
            CharacterId = reader.GetInt64(4),
            MarketFactionId = reader.GetString(21)
        };
        return new((byte[])reader[0], new Guid((byte[])reader[1]), reader.GetString(2),
            (byte[])reader[3], reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
            reader.GetInt32(7), reader.GetString(8), (byte[])reader[9], (byte[])reader[10],
            reservation, reader.GetByte(22));
    }

    private static bool Matches(StoredIntent stored, string instanceId, ReadOnlySpan<byte> leaseTokenHash,
        MarketTradeCommitRequestV1 request, byte[] digest) =>
        CryptographicOperations.FixedTimeEquals(stored.RequestDigest, digest) &&
        string.Equals(stored.InstanceId, instanceId, StringComparison.Ordinal) &&
        stored.LeaseVersion == request.Snapshot.LeaseVersion &&
        CryptographicOperations.FixedTimeEquals(stored.LeaseTokenHash, leaseTokenHash);

    private static bool ValidCaller(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
        MarketTradeCommitRequestV1 request) => AdminQuery.ValidIdentifier(instanceId) &&
        leaseTokenHash.Length == 32 && request is not null && request.IsValid();

    private static byte[] Digest(MarketTradeCommitRequestV1 request)
    {
        var canonical = request with
        {
            CorrelationId = Guid.Empty,
            Reservation = request.Reservation with { CorrelationId = Guid.Empty },
            Snapshot = request.Snapshot with { CorrelationId = Guid.Empty, LeaseToken = string.Empty }
        };
        var encoded = MessagePackSerializer.Serialize(canonical);
        try { return SHA256.HashData(encoded); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    private static byte[] CharacterSaveSnapshotProtectionData(long characterId, long revision)
    {
        var data = new byte[sizeof(long) * 2];
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0, sizeof(long)), characterId);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(sizeof(long)), revision);
        return data;
    }

    private static async Task<MarketTradeCommitStoreResult> RollbackAsync(MySqlTransaction transaction,
        MarketTradeCommitStoreStatus status, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return new(status);
    }

    private static async Task<MarketTradeCommitStoreResult> CommitTransactionAsync(MySqlTransaction transaction,
        MarketTradeCommitStoreResult result, CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static void Zero(params byte[][] values)
    {
        foreach (var value in values) CryptographicOperations.ZeroMemory(value);
    }

    private sealed record StoredIntent(byte[] RequestDigest, string InstanceId, byte[] LeaseTokenHash,
        long LeaseVersion, byte State);
    private sealed record ProtectedIntent(string KeyId, byte[] Hash, byte[] ProtectedSnapshot);
    private sealed record RecoveryLease(string InstanceId, byte[] LeaseTokenHash, long CharacterId,
        long LeaseVersion, long SnapshotRevision, byte State);
    private sealed record RecoveryIntent(byte[] RequestDigest, Guid CorrelationId, string InstanceId, byte[] LeaseTokenHash,
        long CharacterId, long LeaseVersion, long SnapshotRevision, int SnapshotSchemaVersion, string KeyId,
        byte[] Hash, byte[] ProtectedSnapshot, MarketTradeQuoteReservationRequestV1 Reservation, byte State);
}
