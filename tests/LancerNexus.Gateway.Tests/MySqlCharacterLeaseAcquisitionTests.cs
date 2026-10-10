using LancerNexus.Gateway;
using MySqlConnector;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class MySqlCharacterLeaseAcquisitionTests
{
    [MySqlFact]
    public async Task InitialLeaseIsIdempotentAndExpiredTakeoverIncrementsFence()
    {
        var configured = Environment.GetEnvironmentVariable("LANCER_NEXUS_GATEWAY_TEST_MYSQL")
            ?? throw new InvalidOperationException("MySQL test server must be configured.");
        var database = $"gateway_lease_test_{Guid.NewGuid():N}";
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
            var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                "db", "migrations", "001_identity_and_leases.sql"));
            foreach (var statement in migration.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await using var schema = connection.CreateCommand();
                schema.CommandText = statement;
                await schema.ExecuteNonQueryAsync();
            }

            var now = DateTime.UtcNow;
            var account = Guid.NewGuid();
            var firstSession = Guid.NewGuid();
            var secondSession = Guid.NewGuid();
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO accounts
                        (account_id, email, email_normalized, password_hash, status, created_at_utc, updated_at_utc)
                    VALUES (@account, 'lease@example.invalid', 'LEASE@EXAMPLE.INVALID', 'test-hash', 'active', @now, @now);
                    INSERT INTO gateway_sessions
                        (session_id, account_id, token_nonce_hash, created_at_utc, last_seen_at_utc, expires_at_utc, revoked_at_utc)
                    VALUES (@first_session, @account, @hash, @now, @now, @session_expiry, NULL),
                           (@second_session, @account, @hash, @now, @now, @session_expiry, NULL);
                    INSERT INTO characters (character_id, account_id, display_name, created_at_utc, updated_at_utc)
                    VALUES (73, @account, 'LeaseTest', @now, @now);
                    """;
                seed.Parameters.AddWithValue("@account", account.ToString("D"));
                seed.Parameters.AddWithValue("@first_session", firstSession.ToString("D"));
                seed.Parameters.AddWithValue("@second_session", secondSession.ToString("D"));
                seed.Parameters.AddWithValue("@hash", new byte[32]);
                seed.Parameters.AddWithValue("@now", now);
                seed.Parameters.AddWithValue("@session_expiry", now.AddHours(1));
                await seed.ExecuteNonQueryAsync();
            }

            var repository = new MySqlAccountRepository(databaseConfig.ConnectionString);
            var firstHash = Enumerable.Repeat((byte)1, 32).ToArray();
            var acquired = await repository.AcquireCharacterLeaseAsync(account, firstSession, 73,
                "liberty-01", firstHash, now.AddMinutes(15), now);
            Assert.True(acquired.Accepted);
            Assert.Equal("acquired", acquired.ReasonCode);
            Assert.Equal(1, acquired.LeaseVersion);

            var duplicate = await repository.AcquireCharacterLeaseAsync(account, firstSession, 73,
                "liberty-01", firstHash, now.AddMinutes(15), now);
            Assert.True(duplicate.Accepted);
            Assert.Equal("duplicate", duplicate.ReasonCode);
            Assert.Equal(1, duplicate.LeaseVersion);

            var competing = await repository.AcquireCharacterLeaseAsync(account, secondSession, 73,
                "liberty-02", Enumerable.Repeat((byte)2, 32).ToArray(), now.AddMinutes(15), now);
            Assert.False(competing.Accepted);
            Assert.Equal("lease_owned", competing.ReasonCode);

            await using (var expire = connection.CreateCommand())
            {
                expire.CommandText = "UPDATE character_leases SET valid_until_utc = @expired WHERE character_id = 73";
                expire.Parameters.AddWithValue("@expired", now.AddSeconds(-1));
                await expire.ExecuteNonQueryAsync();
            }

            var takeover = await repository.AcquireCharacterLeaseAsync(account, secondSession, 73,
                "liberty-02", Enumerable.Repeat((byte)2, 32).ToArray(), now.AddMinutes(15), now);
            Assert.True(takeover.Accepted);
            Assert.Equal("reacquired", takeover.ReasonCode);
            Assert.Equal(2, takeover.LeaseVersion);

            var staleOwner = await repository.AcquireCharacterLeaseAsync(account, firstSession, 73,
                "liberty-01", firstHash, now.AddMinutes(15), now);
            Assert.False(staleOwner.Accepted);
            Assert.Equal("lease_owned", staleOwner.ReasonCode);
            var current = await repository.FindActiveCharacterTargetLeaseAsync(73, now);
            Assert.Equal("liberty-02", current?.InstanceId);
            Assert.Equal(2, current?.LeaseVersion);
        }
        finally
        {
            MySqlConnection.ClearAllPools();
            adminCommand.CommandText = $"DROP DATABASE IF EXISTS `{database}`";
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
