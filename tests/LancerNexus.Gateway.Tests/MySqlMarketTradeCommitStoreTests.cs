using System.Security.Cryptography;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class MySqlMarketTradeCommitStoreTests
{
    [MySqlFact]
    public async Task MarketTradeRecoveryAtomicallyCommitsSnapshotLedgerOutboxAndExactRetry()
    {
        var configured = Environment.GetEnvironmentVariable("LANCER_NEXUS_GATEWAY_TEST_MYSQL")!;
        var databaseName = $"gateway_market_commit_{Guid.NewGuid():N}";
        var admin = new MySqlConnectionStringBuilder(configured) { Database = "information_schema" };
        await using var adminConnection = new MySqlConnection(admin.ConnectionString);
        await adminConnection.OpenAsync();
        await using var create = adminConnection.CreateCommand();
        create.CommandText = $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_bin";
        await create.ExecuteNonQueryAsync();
        var isolated = new MySqlConnectionStringBuilder(configured) { Database = databaseName };
        try
        {
            await using var connection = new MySqlConnection(isolated.ConnectionString);
            await connection.OpenAsync();
            foreach (var name in new[]
                     {
                         "001_identity_and_leases.sql", "006_character_save_snapshots.sql",
                         "008_character_tombstones.sql", "009_market_trade_intents.sql"
                     })
            {
                var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                    "db", "migrations", name));
                foreach (var statement in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync();
                }
            }

            var now = DateTime.UtcNow;
            var accountId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var token = Enumerable.Repeat((byte)7, 32).ToArray();
            var tokenHash = SHA256.HashData(token);
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO accounts
                        (account_id, email, email_normalized, password_hash, status, created_at_utc, updated_at_utc)
                    VALUES (@account, 'market@example.invalid', 'MARKET@EXAMPLE.INVALID', 'test-hash', 'active', @now, @now);
                    INSERT INTO gateway_sessions
                        (session_id, account_id, token_nonce_hash, created_at_utc, last_seen_at_utc, expires_at_utc, revoked_at_utc)
                    VALUES (@session, @account, @hash, @now, @now, @expiry, NULL);
                    INSERT INTO characters (character_id, account_id, display_name, created_at_utc, updated_at_utc)
                    VALUES (73, @account, 'MarketPilot', @now, @now);
                    INSERT INTO character_leases
                        (character_id, session_id, instance_id, lease_token_hash, lease_version, valid_until_utc)
                    VALUES (73, @session, 'li-01', @hash, 4, @expiry);
                    """;
                seed.Parameters.AddWithValue("@account", accountId.ToString("D"));
                seed.Parameters.AddWithValue("@session", sessionId.ToString("D"));
                seed.Parameters.AddWithValue("@hash", tokenHash);
                seed.Parameters.AddWithValue("@now", now);
                seed.Parameters.AddWithValue("@expiry", now.AddHours(1));
                await seed.ExecuteNonQueryAsync();
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:TransferSnapshotCurrentKeyId"] = "market-test",
                ["Gateway:TransferSnapshotKeys:market-test"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray())
            }).Build();
            var protection = new TransferSnapshotProtection(configuration);
            var snapshots = new MySqlCharacterSaveSnapshotStore(isolated.ConnectionString, protection);
            var store = new MySqlMarketTradeCommitStore(isolated.ConnectionString, protection);
            var leaseToken = Encode(token);
            var initial = Write(leaseToken, 1, [1, 2]);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored,
                (await snapshots.WriteAsync("li-01", tokenHash, initial, now)).Status);

            var request = Request(leaseToken, 2, [3, 4, 5]);
            var prepared = await store.PrepareAsync("li-01", tokenHash, request, now);
            Assert.Equal(MarketTradeCommitStoreStatus.Prepared, prepared.Status);
            var pending = Assert.Single(await store.ReadPendingIntentsAsync(10));
            Assert.Equal(request.TradeId, pending.TradeId);
            Assert.Equal((byte)1, pending.State);
            Assert.Equal(request.Reservation.TradeId, pending.Reservation.TradeId);
            Assert.True(await store.HasActiveIntentLeaseAsync(request.TradeId, now));

            // Simulate Gateway restarting after Coordinator reserved the trade but
            // before Gateway persisted the reservation acknowledgement.
            Assert.Equal(MarketTradeCommitStoreStatus.Reserved,
                (await store.MarkRecoveredReservedAsync(request.TradeId, now)).Status);
            var reserved = Assert.Single(await store.ReadPendingIntentsAsync(10));
            Assert.Equal((byte)2, reserved.State);

            // Recovery reconstructs the commit request from the encrypted intent.
            var committed = await store.CommitRecoveredAsync(request.TradeId, now);
            Assert.Equal(MarketTradeCommitStoreStatus.Committed, committed.Status);
            Assert.Equal(2, committed.SnapshotRevision);
            Assert.Equal(MarketTradeCommitStoreStatus.Duplicate,
                (await store.CommitAsync("li-01", tokenHash, request, now)).Status);
            Assert.Equal(MarketTradeCommitStoreStatus.Duplicate,
                (await store.PrepareAsync("li-01", tokenHash, request, now)).Status);

            var read = await snapshots.ReadAsync("li-01", tokenHash, new CharacterSaveSnapshotReadRequestV1
            {
                CorrelationId = Guid.NewGuid(),
                CharacterId = 73,
                LeaseToken = leaseToken,
                LeaseVersion = 4
            }, now);
            Assert.Equal(new byte[] { 3, 4, 5 }, read.Snapshot!.Payload);
            var outbox = await store.ReadPendingOutboxAsync(10);
            var message = Assert.Single(outbox);
            Assert.Equal(request.TradeId, message.TradeId);
            Assert.Equal(request.CorrelationId, message.CorrelationId);

            await using (var verify = connection.CreateCommand())
            {
                verify.CommandText = """
                    SELECT (SELECT COUNT(*) FROM gateway_market_trade_ledger),
                           (SELECT COUNT(*) FROM gateway_market_trade_outbox),
                           (SELECT state FROM gateway_market_trade_intents WHERE trade_id = @trade)
                    """;
                verify.Parameters.AddWithValue("@trade", request.TradeId.ToByteArray());
                await using var reader = await verify.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1, reader.GetInt32(0));
                Assert.Equal(1, reader.GetInt32(1));
                Assert.Equal((byte)3, reader.GetByte(2));
            }

            var mismatch = await store.PrepareAsync("li-01", tokenHash,
                request with { Snapshot = request.Snapshot with { Payload = [8, 9] } }, now);
            Assert.Equal(MarketTradeCommitStoreStatus.Conflict, mismatch.Status);
            await store.MarkOutboxDeliveredAsync(request.TradeId, now);
            Assert.Empty(await store.ReadPendingOutboxAsync(10));
            CryptographicOperations.ZeroMemory(tokenHash);
            CryptographicOperations.ZeroMemory(token);
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            create.CommandText = $"DROP DATABASE IF EXISTS `{databaseName}`";
            await create.ExecuteNonQueryAsync();
        }
    }

    private static MarketTradeCommitRequestV1 Request(string leaseToken, long revision, byte[] payload)
    {
        var tradeId = Guid.NewGuid();
        var reservation = new MarketTradeQuoteReservationRequestV1
        {
            CorrelationId = Guid.NewGuid(),
            TradeId = tradeId,
            MarketId = "li01_base",
            SystemId = "li-01",
            GoodId = "commodity_food",
            Side = MarketTradeSideV1.Buy,
            Quantity = 2,
            ExpectedMarketRevision = 5,
            ExpectedReputationRevision = 1,
            ReputationPriceFactorPartsPerMillion = 1_000_000,
            ExpectedQuoteUnitPrice = 100,
            ExpectedTotalPrice = 201,
            CharacterId = 73,
            MarketFactionId = "li_grp"
        };
        return new MarketTradeCommitRequestV1
        {
            CorrelationId = Guid.NewGuid(),
            TradeId = tradeId,
            Reservation = reservation,
            Snapshot = Write(leaseToken, revision, payload)
        };
    }

    private static CharacterSaveSnapshotWriteRequestV1 Write(string leaseToken, long revision, byte[] payload) => new()
    {
        CorrelationId = Guid.NewGuid(),
        CharacterId = 73,
        LeaseToken = leaseToken,
        LeaseVersion = 4,
        Revision = revision,
        Payload = payload
    };

    private static string Encode(byte[] token) => Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class MySqlFactAttribute : FactAttribute
    {
        public MySqlFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LANCER_NEXUS_GATEWAY_TEST_MYSQL")))
                Skip = "Set LANCER_NEXUS_GATEWAY_TEST_MYSQL to an isolated MySQL test server.";
        }
    }
}
