using LancerNexus.Gateway;
using MySqlConnector;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class MySqlCharacterTransferCommitTests
{
    [MySqlFact]
    public async Task CommitProofSurvivesRestartExpiryAndSubsequentOwnershipChange()
    {
        var configured = Environment.GetEnvironmentVariable("LANCER_NEXUS_GATEWAY_TEST_MYSQL")
            ?? throw new InvalidOperationException("MySQL test server must be configured.");
        var name = $"gateway_transfer_test_{Guid.NewGuid():N}";
        var adminConfig = new MySqlConnectionStringBuilder(configured) { Database = "information_schema" };
        await using var admin = new MySqlConnection(adminConfig.ConnectionString);
        await admin.OpenAsync();
        await using var adminCommand = admin.CreateCommand();
        adminCommand.CommandText = $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_bin";
        await adminCommand.ExecuteNonQueryAsync();
        var databaseConfig = new MySqlConnectionStringBuilder(configured) { Database = name };
        try
        {
            await using var connection = new MySqlConnection(databaseConfig.ConnectionString);
            await connection.OpenAsync();
            foreach (var migration in new[] { "001_identity_and_leases.sql", "003_character_lease_transfers.sql", "005_character_transfer_decisions.sql" })
            {
                var path = Path.Combine(AppContext.BaseDirectory, "db", "migrations", migration);
                foreach (var statement in (await File.ReadAllTextAsync(path))
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
                    INSERT INTO accounts VALUES (@account, 'test@example.invalid', 'TEST@EXAMPLE.INVALID',
                        'unused-test-hash', 'active', @now, @now);
                    INSERT INTO gateway_sessions VALUES (@session, @account, @hash, @now, @now, @expiry, NULL);
                    INSERT INTO characters VALUES (73, @account, 'TransferTest', @now, @now);
                    INSERT INTO character_leases VALUES (73, @session, 'source-01', @hash, 14, @expiry);
                    """;
                seed.Parameters.AddWithValue("@account", account.ToString("D"));
                seed.Parameters.AddWithValue("@session", session.ToString("D"));
                seed.Parameters.AddWithValue("@hash", new byte[32]);
                seed.Parameters.AddWithValue("@now", now);
                seed.Parameters.AddWithValue("@expiry", now.AddMinutes(5));
                await seed.ExecuteNonQueryAsync();
            }

            var repository = new MySqlAccountRepository(databaseConfig.ConnectionString);
            Assert.Null(await repository.FindCharacterLeaseTransferCommitAsync(transfer));
            Assert.Null(await repository.FindCharacterLeaseTransferCommitAsync(Guid.Empty));
            var committed = await repository.CommitCharacterLeaseTransferAsync(transfer, session, 73,
                "source-01", "target-01", 14, new byte[32], now.AddMinutes(5), now);
            Assert.True(committed.Accepted);
            Assert.Equal(15, committed.LeaseVersion);
            var expected = new CharacterLeaseTransferCommitRecord(transfer, session, 73,
                "source-01", "target-01", 14, 15);
            repository = new MySqlAccountRepository(databaseConfig.ConnectionString);
            Assert.Equal(expected, await repository.FindCharacterLeaseTransferCommitAsync(transfer));

            var replay = await repository.CommitCharacterLeaseTransferAsync(transfer, session, 73,
                "source-01", "target-01", 14, new byte[32], now.AddMinutes(5), now);
            Assert.True(replay.Accepted);
            Assert.Equal("duplicate", replay.ReasonCode);
            Assert.Equal(15, replay.LeaseVersion);

            // Recreate the new table against an already committed legacy journal.
            // Migration runs with writers stopped, as required for deployment.
            await using (var legacy = connection.CreateCommand())
            {
                legacy.CommandText = "DROP TABLE character_transfer_decisions";
                await legacy.ExecuteNonQueryAsync();
                var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                    "db", "migrations", "005_character_transfer_decisions.sql"));
                foreach (var statement in migration.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    legacy.CommandText = statement;
                    await legacy.ExecuteNonQueryAsync();
                }
            }
            Assert.Equal(new CharacterTransferDecision(new(transfer, session, 73,
                    "source-01", "target-01", 14), CharacterTransferDecisionKind.Committed),
                await repository.FindCharacterTransferDecisionAsync(transfer));

            // A later handoff must not erase the historical commit decision.
            var next = await repository.CommitCharacterLeaseTransferAsync(Guid.NewGuid(), session, 73,
                "target-01", "source-01", 15, new byte[32], now.AddMinutes(5), now);
            Assert.True(next.Accepted);
            Assert.Equal(16, next.LeaseVersion);

            var abortBinding = new CharacterTransferBinding(Guid.NewGuid(), session, 73, "source-01", "target-01", 16);
            Assert.True((await repository.AbortCharacterLeaseTransferAsync(abortBinding, now)).Accepted);
            repository = new MySqlAccountRepository(databaseConfig.ConnectionString);
            Assert.Equal(new CharacterTransferDecision(abortBinding, CharacterTransferDecisionKind.Aborted),
                await repository.FindCharacterTransferDecisionAsync(abortBinding.TransferId));
            var abortReplay = await repository.AbortCharacterLeaseTransferAsync(abortBinding, now);
            Assert.True(abortReplay.Accepted);
            Assert.Equal("duplicate_abort", abortReplay.ReasonCode);
            var vetoed = await repository.CommitCharacterLeaseTransferAsync(abortBinding.TransferId, session, 73,
                "source-01", "target-01", 16, new byte[32], now.AddMinutes(5), now);
            Assert.False(vetoed.Accepted);
            Assert.Equal("character_transfer_aborted", vetoed.ReasonCode);
            Assert.Null(await repository.FindCharacterLeaseTransferCommitAsync(abortBinding.TransferId));
            var conflict = await repository.AbortCharacterLeaseTransferAsync(abortBinding with { TargetInstanceId = "other-01" }, now);
            Assert.False(conflict.Accepted);
            Assert.Equal("transfer_id_conflict", conflict.ReasonCode);
            var committedAbort = await repository.AbortCharacterLeaseTransferAsync(new(transfer, session, 73,
                "source-01", "target-01", 14), now);
            Assert.False(committedAbort.Accepted);
            Assert.Equal("committed_transfer_cannot_abort", committedAbort.ReasonCode);
            Assert.Equal(15, committedAbort.CommittedLeaseVersion);

            // Run both operations against separate SQL connections. The character
            // lock and shared decision primary key must admit exactly one winner.
            var currentOwner = "source-01";
            long currentVersion = 16;
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var target = currentOwner == "source-01" ? "target-01" : "source-01";
                var binding = new CharacterTransferBinding(Guid.NewGuid(), session, 73, currentOwner, target, currentVersion);
                var commitTask = repository.CommitCharacterLeaseTransferAsync(binding.TransferId, session, 73,
                    currentOwner, target, currentVersion, new byte[32], now.AddMinutes(5), now);
                var abortTask = new MySqlAccountRepository(databaseConfig.ConnectionString)
                    .AbortCharacterLeaseTransferAsync(binding, now);
                await Task.WhenAll(commitTask, abortTask);
                Assert.NotEqual(commitTask.Result.Accepted, abortTask.Result.Accepted);
                var decision = await new MySqlAccountRepository(databaseConfig.ConnectionString)
                    .FindCharacterTransferDecisionAsync(binding.TransferId);
                Assert.Equal(binding, decision!.Binding);
                if (commitTask.Result.Accepted)
                {
                    Assert.Equal(CharacterTransferDecisionKind.Committed, decision.Kind);
                    Assert.Equal("committed_transfer_cannot_abort", abortTask.Result.ReasonCode);
                    currentOwner = target;
                    currentVersion++;
                }
                else
                {
                    Assert.Equal(CharacterTransferDecisionKind.Aborted, decision.Kind);
                    Assert.Equal("character_transfer_aborted", commitTask.Result.ReasonCode);
                }
                var lease = await repository.FindActiveCharacterLeaseForTransferAsync(session, 73, now);
                Assert.Equal(currentOwner, lease!.InstanceId);
                Assert.Equal(currentVersion, lease.LeaseVersion);
            }

            var staleBinding = new CharacterTransferBinding(Guid.NewGuid(), session, 73, currentOwner,
                currentOwner == "source-01" ? "target-01" : "source-01", currentVersion - 1);
            var stale = await repository.AbortCharacterLeaseTransferAsync(staleBinding, now);
            Assert.False(stale.Accepted);
            Assert.Equal("lease_fence_rejected", stale.ReasonCode);
            Assert.Null(await repository.FindCharacterTransferDecisionAsync(staleBinding.TransferId));
            await using (var expire = connection.CreateCommand())
            {
                expire.CommandText = """
                    UPDATE gateway_sessions SET revoked_at_utc=@past, expires_at_utc=@past;
                    UPDATE character_leases SET valid_until_utc=@past;
                    """;
                expire.Parameters.AddWithValue("@past", now.AddMinutes(-1));
                await expire.ExecuteNonQueryAsync();
            }
            repository = new MySqlAccountRepository(databaseConfig.ConnectionString);
            Assert.Equal(expected, await repository.FindCharacterLeaseTransferCommitAsync(transfer));
            Assert.Null(await repository.FindActiveCharacterLeaseForTransferAsync(session, 73, now));
            var expiredBinding = new CharacterTransferBinding(Guid.NewGuid(), session, 73, currentOwner,
                currentOwner == "source-01" ? "target-01" : "source-01", currentVersion);
            Assert.True((await repository.AbortCharacterLeaseTransferAsync(expiredBinding, now)).Accepted);
            Assert.Equal(CharacterTransferDecisionKind.Aborted,
                (await repository.FindCharacterTransferDecisionAsync(expiredBinding.TransferId))!.Kind);
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            adminCommand.CommandText = $"DROP DATABASE IF EXISTS `{name}`";
            await adminCommand.ExecuteNonQueryAsync();
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
