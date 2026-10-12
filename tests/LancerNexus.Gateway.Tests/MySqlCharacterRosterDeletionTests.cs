using System.Security.Cryptography;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class MySqlCharacterRosterDeletionTests
{
    [MySqlFact]
    public async Task RosterHidesTombstonesAndDeletionPreservesHistoryAndRejectsLiveOwnership()
    {
        var configured = Environment.GetEnvironmentVariable("LANCER_NEXUS_GATEWAY_TEST_MYSQL")!;
        var database = $"gateway_roster_test_{Guid.NewGuid():N}";
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
            foreach (var migrationName in new[]
                     {
                         "001_identity_and_leases.sql", "003_character_lease_transfers.sql",
                         "004_character_transfer_snapshots.sql", "005_character_transfer_decisions.sql",
                         "006_character_save_snapshots.sql", "007_character_identity_allocation.sql",
                         "008_character_tombstones.sql"
                     })
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
            var transfer = Guid.NewGuid();
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO accounts
                        (account_id, email, email_normalized, password_hash, status, created_at_utc, updated_at_utc)
                    VALUES (@account, 'roster@example.invalid', 'ROSTER@EXAMPLE.INVALID', 'test-hash', 'active', @now, @now);
                    INSERT INTO gateway_sessions
                        (session_id, account_id, token_nonce_hash, created_at_utc, last_seen_at_utc, expires_at_utc, revoked_at_utc)
                    VALUES (@session, @account, @hash, @now, @now, @expiry, NULL);
                    INSERT INTO characters (character_id, account_id, display_name, created_at_utc, updated_at_utc)
                    VALUES (73, @account, 'InUsePilot', @now, @now),
                           (74, @account, 'TransferringPilot', @now, @now),
                           (75, @account, 'DeletedPilot', @now, @now),
                           (76, @account, 'BootstrapPilot', @now, @now);
                    INSERT INTO character_leases
                        (character_id, session_id, instance_id, lease_token_hash, lease_version, valid_until_utc)
                    VALUES (73, @session, 'source-01', @hash, 1, @expiry);
                    INSERT INTO character_transfer_snapshots
                        (transfer_id, source_instance_id, target_instance_id, character_id, lease_version,
                         key_id, snapshot_hash, protected_snapshot, created_at_utc)
                    VALUES (@transfer, 'source-01', 'target-01', 74, 1, 'test-key', @snapshot_hash, @payload, @now);
                    INSERT INTO character_save_snapshots
                        (character_id, snapshot_revision, lease_version, schema_version, key_id,
                         snapshot_hash, protected_snapshot, updated_at_utc)
                    VALUES (75, 1, 1, 1, 'test-key', @snapshot_hash, @payload, @now);
                    """;
                seed.Parameters.AddWithValue("@account", account.ToString("D"));
                seed.Parameters.AddWithValue("@session", session.ToString("D"));
                seed.Parameters.AddWithValue("@transfer", transfer.ToString("D"));
                seed.Parameters.AddWithValue("@hash", new byte[32]);
                seed.Parameters.AddWithValue("@snapshot_hash", new byte[32]);
                seed.Parameters.AddWithValue("@payload", new byte[] { 1, 2, 3 });
                seed.Parameters.AddWithValue("@now", now);
                seed.Parameters.AddWithValue("@expiry", now.AddMinutes(5));
                await seed.ExecuteNonQueryAsync();
            }

            var repository = new MySqlAccountRepository(databaseConfig.ConnectionString);
            Assert.Equal("character_in_use", (await repository.TombstoneCharacterAsync(Delete(account, session, 73), now)).ReasonCode);
            Assert.Equal("transfer_in_progress", (await repository.TombstoneCharacterAsync(Delete(account, session, 74), now)).ReasonCode);
            Assert.Equal("session_rejected", (await repository.TombstoneCharacterAsync(
                Delete(account, Guid.NewGuid(), 75), now)).ReasonCode);

            Assert.Equal("deleted", (await repository.TombstoneCharacterAsync(Delete(account, session, 75), now)).ReasonCode);
            Assert.Equal("duplicate", (await repository.TombstoneCharacterAsync(Delete(account, session, 75), now)).ReasonCode);
            Assert.Null(await repository.FindCharacterAsync(account, 75));
            Assert.Equal("character_name_conflict",
                (await repository.ResolveCharacterIdentityAsync(account, session, "DeletedPilot", now)).ReasonCode);
            var roster = await repository.ListActiveCharactersForSessionAsync(account, session, now);
            Assert.True(roster.SessionAccepted);
            Assert.Equal(new long[] { 73, 74, 76 }, roster.Characters.Select(character => character.CharacterId));
            Assert.False((await repository.ListActiveCharactersForSessionAsync(account, Guid.NewGuid(), now)).SessionAccepted);

            var reacquire = await repository.AcquireCharacterLeaseAsync(account, session, 75, "target-01",
                new byte[32], now.AddMinutes(5), now);
            Assert.False(reacquire.Accepted);
            Assert.Equal("character_not_found", reacquire.ReasonCode);

            var encryptionKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            var protection = new TransferSnapshotProtection(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Gateway:TransferSnapshotCurrentKeyId"] = "roster-test-key",
                    ["Gateway:TransferSnapshotKeys:roster-test-key"] = Convert.ToBase64String(encryptionKey)
                }).Build());
            var snapshots = new MySqlCharacterSaveSnapshotStore(databaseConfig.ConnectionString, protection);
            var bootstrapPayload = "Pilot SaveGame"u8.ToArray();
            var bootstrap = new CharacterBootstrapRequestV1
            {
                CorrelationId = Guid.NewGuid(),
                AccountId = account,
                SessionId = session,
                CharacterId = 76,
                Payload = bootstrapPayload
            };
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored,
                (await snapshots.BootstrapAsync(bootstrap, now)).Status);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Duplicate,
                (await snapshots.BootstrapAsync(bootstrap with { Payload = [9, 9] }, now)).Status);
            var previewRequest = new CharacterSnapshotPreviewRequestV1
            {
                CorrelationId = Guid.NewGuid(),
                AccountId = account,
                SessionId = session,
                CharacterId = 76
            };
            var preview = await snapshots.ReadPreviewAsync(previewRequest, now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored, preview.Status);
            Assert.Equal(bootstrapPayload, preview.Snapshot!.Payload);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.SessionRejected,
                (await snapshots.ReadPreviewAsync(previewRequest with { SessionId = Guid.NewGuid() }, now)).Status);

            var leaseToken = Enumerable.Repeat((byte)3, 32).ToArray();
            Assert.True((await repository.AcquireCharacterLeaseAsync(account, session, 76, "target-01",
                SHA256.HashData(leaseToken), now.AddMinutes(5), now)).Accepted);
            var bootstrappedRead = await snapshots.ReadAsync("target-01", SHA256.HashData(leaseToken),
                new CharacterSaveSnapshotReadRequestV1
                {
                    CorrelationId = Guid.NewGuid(),
                    CharacterId = 76,
                    LeaseToken = Encode(leaseToken),
                    LeaseVersion = 1
                }, now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored, bootstrappedRead.Status);
            Assert.Equal(bootstrapPayload, bootstrappedRead.Snapshot!.Payload);

            await using var history = connection.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM character_save_snapshots WHERE character_id = 75;";
            Assert.Equal(1L, Convert.ToInt64(await history.ExecuteScalarAsync()));
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            adminCommand.CommandText = $"DROP DATABASE IF EXISTS `{database}`";
            await adminCommand.ExecuteNonQueryAsync();
        }
    }

    private static CharacterDeletionRequestV1 Delete(Guid account, Guid session, long characterId) => new()
    {
        CorrelationId = Guid.NewGuid(),
        AccountId = account,
        SessionId = session,
        CharacterId = characterId
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
