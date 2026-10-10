using System.Security.Cryptography;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class MySqlCharacterSaveSnapshotTests
{
    [MySqlFact]
    public async Task SnapshotIsEncryptedRevisionedIdempotentAndLeaseFenced()
    {
        var configured = Environment.GetEnvironmentVariable("LANCER_NEXUS_GATEWAY_TEST_MYSQL")!;
        var database = $"gateway_snapshot_test_{Guid.NewGuid():N}";
        var adminConfig = new MySqlConnectionStringBuilder(configured) { Database = "information_schema" };
        await using var admin = new MySqlConnection(adminConfig.ConnectionString);
        await admin.OpenAsync();
        await using var adminCommand = admin.CreateCommand();
        adminCommand.CommandText = $"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_bin";
        await adminCommand.ExecuteNonQueryAsync();
        var databaseConfig = new MySqlConnectionStringBuilder(configured) { Database = database };
        try
        {
            await using var connection = new MySqlConnection(databaseConfig.ConnectionString);
            await connection.OpenAsync();
            foreach (var migrationName in new[] { "001_identity_and_leases.sql", "006_character_save_snapshots.sql" })
            {
                var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                    "db", "migrations", migrationName));
                foreach (var statement in migration.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    await using var schema = connection.CreateCommand();
                    schema.CommandText = statement;
                    await schema.ExecuteNonQueryAsync();
                }
            }

            var now = DateTime.UtcNow;
            var account = Guid.NewGuid();
            var session = Guid.NewGuid();
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO accounts
                        (account_id, email, email_normalized, password_hash, status, created_at_utc, updated_at_utc)
                    VALUES (@account, 'snapshot@example.invalid', 'SNAPSHOT@EXAMPLE.INVALID', 'test-hash', 'active', @now, @now);
                    INSERT INTO gateway_sessions
                        (session_id, account_id, token_nonce_hash, created_at_utc, last_seen_at_utc, expires_at_utc, revoked_at_utc)
                    VALUES (@session, @account, @hash, @now, @now, @expiry, NULL);
                    INSERT INTO characters (character_id, account_id, display_name, created_at_utc, updated_at_utc)
                    VALUES (73, @account, 'SnapshotTest', @now, @now);
                    """;
                seed.Parameters.AddWithValue("@account", account.ToString("D"));
                seed.Parameters.AddWithValue("@session", session.ToString("D"));
                seed.Parameters.AddWithValue("@hash", new byte[32]);
                seed.Parameters.AddWithValue("@now", now);
                seed.Parameters.AddWithValue("@expiry", now.AddHours(1));
                await seed.ExecuteNonQueryAsync();
            }

            var allocationMigration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                "db", "migrations", "007_character_identity_allocation.sql"));
            foreach (var statement in allocationMigration.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await using var schema = connection.CreateCommand();
                schema.CommandText = statement;
                await schema.ExecuteNonQueryAsync();
            }

            var token1 = Enumerable.Repeat((byte)1, 32).ToArray();
            var token2 = Enumerable.Repeat((byte)2, 32).ToArray();
            var leases = new MySqlAccountRepository(databaseConfig.ConnectionString);
            var identity = await leases.ResolveCharacterIdentityAsync(account, session, "ResolvedPilot", now);
            var identityRetry = await leases.ResolveCharacterIdentityAsync(account, session, "ResolvedPilot", now);
            Assert.True(identity.Accepted, identity.ReasonCode);
            Assert.Equal("resolved", identity.ReasonCode);
            Assert.True(identity.CharacterId > 73);
            Assert.Equal(identity.CharacterId, identityRetry.CharacterId);
            Assert.True((await leases.AcquireCharacterLeaseAsync(account, session, 73, "li-01",
                SHA256.HashData(token1), now.AddMinutes(15), now)).Accepted);
            var renewedLease = await leases.RenewCharacterLeaseAsync(73, "li-01", SHA256.HashData(token1),
                1, now.AddMinutes(30), now.AddMinutes(1));
            Assert.True(renewedLease.Accepted);
            Assert.Equal("renewed", renewedLease.ReasonCode);
            Assert.Equal(1, renewedLease.LeaseVersion);
            var staleRenewal = await leases.RenewCharacterLeaseAsync(73, "li-01", SHA256.HashData(token2),
                1, now.AddMinutes(30), now.AddMinutes(1));
            Assert.False(staleRenewal.Accepted);
            Assert.Equal("character_lease_rejected", staleRenewal.ReasonCode);
            var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:TransferSnapshotCurrentKeyId"] = "test-key",
                ["Gateway:TransferSnapshotKeys:test-key"] = Convert.ToBase64String(key)
            }).Build();
            var protection = new TransferSnapshotProtection(configuration);
            var store = new MySqlCharacterSaveSnapshotStore(databaseConfig.ConnectionString, protection);
            var write = WriteRequest(token1, 1, 1, [10, 20, 30]);
            var hash1 = SHA256.HashData(token1);

            var stored = await store.WriteAsync("li-01", hash1, write, now);
            var duplicate = await store.WriteAsync("li-01", hash1, write, now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored, stored.Status);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Duplicate, duplicate.Status);

            var read = await store.ReadAsync("li-01", hash1, new CharacterSaveSnapshotReadRequestV1
            {
                CorrelationId = Guid.NewGuid(), CharacterId = 73, LeaseToken = Encode(token1), LeaseVersion = 1
            }, now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored, read.Status);
            Assert.Equal(write.Payload, read.Snapshot!.Payload);

            await using (var takeover = connection.CreateCommand())
            {
                takeover.CommandText = "UPDATE character_leases SET lease_token_hash = @hash, lease_version = 2, instance_id = 'li-02' WHERE character_id = 73";
                takeover.Parameters.AddWithValue("@hash", SHA256.HashData(token2));
                await takeover.ExecuteNonQueryAsync();
            }

            var stale = await store.WriteAsync("li-01", hash1, WriteRequest(token1, 1, 2, [40]), now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.LeaseRejected, stale.Status);
            var current = await store.WriteAsync("li-02", SHA256.HashData(token2), WriteRequest(token2, 2, 2, [40]), now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored, current.Status);
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            adminCommand.CommandText = $"DROP DATABASE IF EXISTS `{database}`";
            await adminCommand.ExecuteNonQueryAsync();
        }
    }

    private static CharacterSaveSnapshotWriteRequestV1 WriteRequest(byte[] token, long fence, long revision, byte[] payload) => new()
    {
        CorrelationId = Guid.NewGuid(), CharacterId = 73, LeaseToken = Encode(token),
        LeaseVersion = fence, Revision = revision, Payload = payload
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
