using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
                         "008_character_tombstones.sql", "009_market_trade_intents.sql",
                         "010_character_snapshot_system_metadata.sql"
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
            // A fresh store instance models the process losing all in-memory state
            // after the Gateway commit and resuming from its durable outbox.
            var restartedStore = new MySqlMarketTradeCommitStore(isolated.ConnectionString, protection);
            var outbox = await restartedStore.ReadPendingOutboxAsync(10);
            var message = Assert.Single(outbox);
            Assert.Equal(request.TradeId, message.TradeId);
            Assert.Equal(request.CorrelationId, message.CorrelationId);

            // Simulate the Coordinator committing, then losing its HTTP response.
            // The first Gateway worker must leave the outbox pending; a replacement
            // worker must replay the same identifiers and accept the idempotent ACK.
            var coordinator = new LostFirstResponseCoordinator();
            using var firstHttp = new HttpClient(coordinator, disposeHandler: false);
            using var secondHttp = new HttpClient(coordinator, disposeHandler: false);
            var options = new CoordinatorGatewayOptions(new Uri("https://coordinator.test/"), null,
                TimeSpan.FromSeconds(3), new string('k', 32));
            var firstWorker = new MarketTradeOutboxWorker(restartedStore,
                new CoordinatorMarketClient(firstHttp, options), TimeProvider.System,
                NullLogger<MarketTradeOutboxWorker>.Instance);
            await firstWorker.StartAsync(CancellationToken.None);
            await WaitForOutboxAttemptAsync(connection, request.TradeId, expectedAttemptCount: 1);
            await firstWorker.StopAsync(CancellationToken.None);

            await using (var makeRetryDue = connection.CreateCommand())
            {
                makeRetryDue.CommandText = "UPDATE gateway_market_trade_outbox SET next_attempt_at_utc = UTC_TIMESTAMP(6) WHERE trade_id = @trade";
                makeRetryDue.Parameters.AddWithValue("@trade", request.TradeId.ToByteArray());
                Assert.Equal(1, await makeRetryDue.ExecuteNonQueryAsync());
            }

            var restartedWorker = new MarketTradeOutboxWorker(restartedStore,
                new CoordinatorMarketClient(secondHttp, options), TimeProvider.System,
                NullLogger<MarketTradeOutboxWorker>.Instance);
            await restartedWorker.StartAsync(CancellationToken.None);
            try
            {
                await WaitForOutboxDeliveryAsync(connection, request.TradeId);
            }
            finally
            {
                await restartedWorker.StopAsync(CancellationToken.None);
            }
            Assert.Equal(2, coordinator.Requests.Count);
            Assert.All(coordinator.Requests, sent =>
            {
                Assert.Equal(request.TradeId, sent.TradeId);
                Assert.Equal(message.CorrelationId, sent.CorrelationId);
            });

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

    private static async Task WaitForOutboxAttemptAsync(MySqlConnection connection, Guid tradeId,
        int expectedAttemptCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT attempt_count FROM gateway_market_trade_outbox WHERE trade_id = @trade";
            command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
            var value = await command.ExecuteScalarAsync(timeout.Token);
            if (Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) >= expectedAttemptCount)
                return;
            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task WaitForOutboxDeliveryAsync(MySqlConnection connection, Guid tradeId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT delivered_at_utc IS NOT NULL FROM gateway_market_trade_outbox WHERE trade_id = @trade";
            command.Parameters.AddWithValue("@trade", tradeId.ToByteArray());
            if (Convert.ToBoolean(await command.ExecuteScalarAsync(timeout.Token),
                    System.Globalization.CultureInfo.InvariantCulture))
                return;
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class LostFirstResponseCoordinator : HttpMessageHandler
    {
        private readonly HashSet<Guid> committedTrades = [];
        public List<MarketTradeResolutionRequestV1> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var resolution = await request.Content!.ReadFromJsonAsync<MarketTradeResolutionRequestV1>(cancellationToken);
            Assert.NotNull(resolution);
            Requests.Add(resolution!);
            if (committedTrades.Add(resolution!.TradeId))
                throw new HttpRequestException("Simulated lost response after Coordinator commit.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new MarketTradeReservationResponseV1
                {
                    CorrelationId = resolution.CorrelationId,
                    TradeId = resolution.TradeId,
                    Status = MarketTradeReservationStatusV1.Committed,
                    QuoteUnitPrice = 100,
                    TotalPrice = 201,
                    MarketRevision = 3,
                    SystemRevision = 3
                })
            };
        }
    }

    private sealed class MySqlFactAttribute : FactAttribute
    {
        public MySqlFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LANCER_NEXUS_GATEWAY_TEST_MYSQL")))
                Skip = "Set LANCER_NEXUS_GATEWAY_TEST_MYSQL to an isolated MySQL test server.";
        }
    }
}
