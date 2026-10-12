using System.Security.Cryptography;
using System.Text;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class MySqlCharacterSaveSnapshotTests
{
    private const long LegacyCharacterId = 900_000_000_001;

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
            foreach (var migrationName in new[] { "001_identity_and_leases.sql", "006_character_save_snapshots.sql", "008_character_tombstones.sql", "010_character_snapshot_system_metadata.sql" })
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
            await using (var legacyCharacter = connection.CreateCommand())
            {
                legacyCharacter.CommandText = """
                    INSERT INTO characters (character_id, account_id, display_name, created_at_utc, updated_at_utc)
                    VALUES (@legacy_character, @account, 'LegacyPilot', @now, @now);
                    """;
                legacyCharacter.Parameters.AddWithValue("@legacy_character", LegacyCharacterId);
                legacyCharacter.Parameters.AddWithValue("@account", account.ToString("D"));
                legacyCharacter.Parameters.AddWithValue("@now", now);
                await legacyCharacter.ExecuteNonQueryAsync();
            }
            Assert.True((await leases.AcquireCharacterLeaseAsync(account, session, 73, "li-01",
                SHA256.HashData(token1), now.AddMinutes(15), now)).Accepted);
            Assert.True((await leases.AcquireCharacterLeaseAsync(account, session, LegacyCharacterId, "li-01",
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
            var hash1 = SHA256.HashData(token1);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:TransferSnapshotCurrentKeyId"] = "test-key",
                ["Gateway:TransferSnapshotKeys:test-key"] = Convert.ToBase64String(key)
            }).Build();
            var protection = new TransferSnapshotProtection(configuration);
            var store = new MySqlCharacterSaveSnapshotStore(databaseConfig.ConnectionString, protection);
            var legacyPayload = Encoding.UTF8.GetBytes("[Player]\nsystem = Li03\n");
            var legacySnapshot = await store.WriteAsync("li-01", hash1,
                WriteRequest(token1, 1, 1, legacyPayload, characterId: LegacyCharacterId), now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored, legacySnapshot.Status);
            var recoveryRequest = new CharacterSystemMetadataRecoveryRequestV1
            {
                CorrelationId = Guid.NewGuid(), AccountId = account, SessionId = session,
                CharacterId = LegacyCharacterId
            };
            var recovered = await store.RecoverSystemMetadataAsync(recoveryRequest, now);
            Assert.Equal(CharacterSystemMetadataRecoveryStatus.Recovered, recovered.Status);
            Assert.Equal("li03", recovered.SystemId);
            var repeatedRecovery = await store.RecoverSystemMetadataAsync(recoveryRequest with
            {
                CorrelationId = Guid.NewGuid()
            }, now);
            Assert.Equal(CharacterSystemMetadataRecoveryStatus.AlreadyKnown, repeatedRecovery.Status);
            Assert.Equal("li03", repeatedRecovery.SystemId);
            var rosterAfterRecovery = await leases.ListActiveCharactersForSessionAsync(account, session, now);
            Assert.Equal("li03", Assert.Single(rosterAfterRecovery.Characters,
                character => character.CharacterId == LegacyCharacterId).SystemId);
            var write = WriteRequest(token1, 1, 1, Encoding.Latin1.GetBytes("""
                [Player]
                house = 0.625, li_n_grp
                house = -0.25, fc_rh_grp
                """), "li01");
            var stored = await store.WriteAsync("li-01", hash1, write, now);
            var duplicate = await store.WriteAsync("li-01", hash1, write, now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored, stored.Status);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Duplicate, duplicate.Status);
            var conflictingLocationRetry = await store.WriteAsync("li-01", hash1,
                write with { SystemId = "li02" }, now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Conflict, conflictingLocationRetry.Status);

            var read = await store.ReadAsync("li-01", hash1, new CharacterSaveSnapshotReadRequestV1
            {
                CorrelationId = Guid.NewGuid(),
                CharacterId = 73,
                LeaseToken = Encode(token1),
                LeaseVersion = 1
            }, now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored, read.Status);
            Assert.Equal(write.Payload, read.Snapshot!.Payload);
            Assert.Equal("li01", read.Snapshot.SystemId);

            var reputation = await store.ReadFactionReputationAsync(account, session, 73, now);
            Assert.Equal(CharacterFactionReputationReadStatus.Found, reputation.Status);
            Assert.Equal(1, reputation.ReputationRevision);
            Assert.Collection(reputation.Factions!,
                item =>
                {
                    Assert.Equal("fc_rh_grp", item.FactionId);
                    Assert.Equal(-0.25f, item.Value);
                },
                item =>
                {
                    Assert.Equal("li_n_grp", item.FactionId);
                    Assert.Equal(0.625f, item.Value);
                });
            var leaseReputation = await store.ReadFactionReputationForLeaseAsync(
                "li-01", hash1, 73, 1, now);
            Assert.Equal(CharacterFactionReputationReadStatus.Found, leaseReputation.Status);
            Assert.Equal(reputation.ReputationRevision, leaseReputation.ReputationRevision);
            Assert.Equal(reputation.Factions, leaseReputation.Factions);
            var wrongInstanceReputation = await store.ReadFactionReputationForLeaseAsync(
                "li-02", hash1, 73, 1, now);
            Assert.Equal(CharacterFactionReputationReadStatus.LeaseRejected, wrongInstanceReputation.Status);
            var otherAccount = await store.ReadFactionReputationAsync(Guid.NewGuid(), session, 73, now);
            Assert.Equal(CharacterFactionReputationReadStatus.NotFound, otherAccount.Status);
            var serviceRequest = new MarketFactionReputationReadRequestV1
            {
                CorrelationId = Guid.NewGuid(),
                CharacterId = 73,
                ExpectedReputationRevision = 1
            };
            var serviceRead = await store.ReadFactionReputationForServiceAsync(serviceRequest);
            Assert.Equal(CharacterFactionReputationReadStatus.Found, serviceRead.Status);
            Assert.Equal(1, serviceRead.ReputationRevision);
            var staleServiceRead = await store.ReadFactionReputationForServiceAsync(serviceRequest with
            {
                ExpectedReputationRevision = 2
            });
            Assert.Equal(CharacterFactionReputationReadStatus.RevisionMismatch, staleServiceRead.Status);
            Assert.Equal(1, staleServiceRead.ReputationRevision);
            Assert.Null(staleServiceRead.Factions);

            await using (var takeover = connection.CreateCommand())
            {
                takeover.CommandText = "UPDATE character_leases SET lease_token_hash = @hash, lease_version = 2, instance_id = 'li-02' WHERE character_id = 73";
                takeover.Parameters.AddWithValue("@hash", SHA256.HashData(token2));
                await takeover.ExecuteNonQueryAsync();
            }

            var stale = await store.WriteAsync("li-01", hash1, WriteRequest(token1, 1, 2, [40]), now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.LeaseRejected, stale.Status);
            var staleLeaseReputation = await store.ReadFactionReputationForLeaseAsync(
                "li-01", hash1, 73, 1, now);
            Assert.Equal(CharacterFactionReputationReadStatus.LeaseRejected, staleLeaseReputation.Status);
            var current = await store.WriteAsync("li-02", SHA256.HashData(token2),
                WriteRequest(token2, 2, 2, write.Payload, "li03"), now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored, current.Status);
            var currentSnapshot = await store.ReadAsync("li-02", SHA256.HashData(token2),
                new CharacterSaveSnapshotReadRequestV1
                {
                    CorrelationId = Guid.NewGuid(), CharacterId = 73,
                    LeaseToken = Encode(token2), LeaseVersion = 2
                }, now);
            Assert.Equal("li03", currentSnapshot.Snapshot!.SystemId);
            var currentLeaseReputation = await store.ReadFactionReputationForLeaseAsync(
                "li-02", SHA256.HashData(token2), 73, 2, now);
            Assert.Equal(CharacterFactionReputationReadStatus.Found, currentLeaseReputation.Status);
            Assert.Equal(2, currentLeaseReputation.ReputationRevision);

            var legacyWrite = await store.WriteAsync("li-02", SHA256.HashData(token2),
                WriteRequest(token2, 2, 3, [41]), now);
            Assert.Equal(CharacterSaveSnapshotStoreStatus.Stored, legacyWrite.Status);
            var afterLegacyWrite = await store.ReadAsync("li-02", SHA256.HashData(token2),
                new CharacterSaveSnapshotReadRequestV1
                {
                    CorrelationId = Guid.NewGuid(), CharacterId = 73,
                    LeaseToken = Encode(token2), LeaseVersion = 2
                }, now);
            Assert.Equal("li03", afterLegacyWrite.Snapshot!.SystemId);

            await using (var revoke = connection.CreateCommand())
            {
                revoke.CommandText = "UPDATE gateway_sessions SET revoked_at_utc = @now WHERE session_id = @session";
                revoke.Parameters.AddWithValue("@now", now);
                revoke.Parameters.AddWithValue("@session", session.ToString("D"));
                await revoke.ExecuteNonQueryAsync();
            }
            var revokedSession = await store.ReadFactionReputationAsync(account, session, 73, now);
            Assert.Equal(CharacterFactionReputationReadStatus.SessionRejected, revokedSession.Status);
            var revokedLease = await store.ReadFactionReputationForLeaseAsync(
                "li-02", SHA256.HashData(token2), 73, 2, now);
            Assert.Equal(CharacterFactionReputationReadStatus.LeaseRejected, revokedLease.Status);
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            adminCommand.CommandText = $"DROP DATABASE IF EXISTS `{database}`";
            await adminCommand.ExecuteNonQueryAsync();
        }
    }

    private static CharacterSaveSnapshotWriteRequestV1 WriteRequest(byte[] token, long fence, long revision,
        byte[] payload, string? systemId = null, long characterId = 73) => new()
    {
        CorrelationId = Guid.NewGuid(),
        CharacterId = characterId,
        LeaseToken = Encode(token),
        LeaseVersion = fence,
        Revision = revision,
        Payload = payload,
        SystemId = systemId
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
