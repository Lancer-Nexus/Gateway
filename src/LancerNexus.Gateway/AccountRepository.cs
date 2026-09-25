using MySqlConnector;

namespace LancerNexus.Gateway;

public sealed record AccountRecord(
    Guid AccountId,
    string Email,
    string PasswordHash,
    string Status);

public sealed record SessionRecord(Guid AccountId, DateTime ExpiresAtUtc);
public sealed record CharacterRecord(long CharacterId, string DisplayName, DateTime CreatedAtUtc);
public sealed record CharacterLeaseRecord(string InstanceId, long LeaseVersion, DateTime ValidUntilUtc);
public sealed record CharacterLeaseTransferResult(bool Accepted, string ReasonCode, long? LeaseVersion);

public interface IAccountRepository
{
    Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash,
        DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default);
    Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
        byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId,
        CancellationToken cancellationToken = default);
    Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId,
        CancellationToken cancellationToken = default);
    Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId, long characterId,
        DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<CharacterLeaseRecord?> FindActiveCharacterLeaseForTransferAsync(Guid sessionId, long characterId,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult<CharacterLeaseRecord?>(null);
    Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
        long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
        byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed class AccountRepositoryNotConfigured : IAccountRepository
{
    public Task<AccountRecord?> FindByEmailAsync(
        string email,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash,
        DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
        byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId,
        long characterId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseForTransferAsync(Guid sessionId, long characterId,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
        long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
        byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");
}

public sealed class MySqlAccountRepository(string connectionString) : IAccountRepository
{
    public async Task<CharacterLeaseRecord?> FindActiveCharacterLeaseForTransferAsync(Guid sessionId, long characterId,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty || characterId <= 0)
            return null;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.instance_id, l.lease_version, l.valid_until_utc
            FROM character_leases l
            INNER JOIN gateway_sessions s ON s.session_id = l.session_id
            INNER JOIN accounts a ON a.account_id = s.account_id
            INNER JOIN characters c ON c.character_id = l.character_id AND c.account_id = a.account_id
            WHERE l.session_id = @session_id AND l.character_id = @character_id
              AND s.revoked_at_utc IS NULL AND s.expires_at_utc > @now_utc
              AND a.status = 'active' AND l.valid_until_utc > @now_utc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@session_id", sessionId.ToString());
        command.Parameters.AddWithValue("@character_id", characterId);
        command.Parameters.AddWithValue("@now_utc", nowUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new CharacterLeaseRecord(reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2));
    }

    public async Task<AccountRecord?> FindByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = AccountEmail.Normalize(email);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_id, email, password_hash, status
            FROM accounts
            WHERE email_normalized = @email_normalized
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@email_normalized", normalizedEmail);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var accountId = reader.GetValue(0) switch
        {
            Guid value => value,
            string value => Guid.Parse(value),
            _ => throw new InvalidDataException("Gateway account_id has an unsupported database type.")
        };
        return new AccountRecord(
            accountId,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    public async Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash,
        DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gateway_sessions
                (session_id, account_id, token_nonce_hash, refresh_token_hash, created_at_utc,
                 last_seen_at_utc, expires_at_utc)
            VALUES
                (@session_id, @account_id, @token_nonce_hash, @refresh_token_hash, @created_at_utc,
                 @last_seen_at_utc, @expires_at_utc);
            """;
        command.Parameters.AddWithValue("@session_id", sessionId.ToString());
        command.Parameters.AddWithValue("@account_id", accountId.ToString());
        command.Parameters.AddWithValue("@token_nonce_hash", nonceHash);
        command.Parameters.AddWithValue("@refresh_token_hash", refreshTokenHash);
        command.Parameters.AddWithValue("@created_at_utc", createdAtUtc);
        command.Parameters.AddWithValue("@last_seen_at_utc", createdAtUtc);
        command.Parameters.AddWithValue("@expires_at_utc", expiresAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
        byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE gateway_sessions
            SET refresh_token_hash = @new_refresh_token_hash,
                last_seen_at_utc = @now_utc
            WHERE session_id = @session_id
              AND refresh_token_hash = @old_refresh_token_hash
              AND revoked_at_utc IS NULL
              AND expires_at_utc > @now_utc;
            """;
        update.Parameters.AddWithValue("@new_refresh_token_hash", newRefreshTokenHash);
        update.Parameters.AddWithValue("@session_id", sessionId.ToString());
        update.Parameters.AddWithValue("@old_refresh_token_hash", oldRefreshTokenHash);
        update.Parameters.AddWithValue("@now_utc", nowUtc);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT s.account_id, s.expires_at_utc
            FROM gateway_sessions s
            INNER JOIN accounts a ON a.account_id = s.account_id
            WHERE s.session_id = @session_id AND a.status = 'active';
            """;
        select.Parameters.AddWithValue("@session_id", sessionId.ToString());
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        var accountId = reader.GetValue(0) switch
        {
            Guid value => value,
            string value => Guid.Parse(value),
            _ => throw new InvalidDataException("Gateway account_id has an unsupported database type.")
        };
        var session = new SessionRecord(accountId, reader.GetDateTime(1));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT character_id, display_name, created_at_utc
            FROM characters
            WHERE account_id = @account_id
            ORDER BY character_id;
            """;
        command.Parameters.AddWithValue("@account_id", accountId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var characters = new List<CharacterRecord>();
        while (await reader.ReadAsync(cancellationToken))
            characters.Add(new CharacterRecord(reader.GetInt64(0), reader.GetString(1), reader.GetDateTime(2)));
        return characters;
    }

    public async Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT character_id, display_name, created_at_utc
            FROM characters
            WHERE account_id = @account_id AND character_id = @character_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@account_id", accountId.ToString());
        command.Parameters.AddWithValue("@character_id", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new CharacterRecord(reader.GetInt64(0), reader.GetString(1), reader.GetDateTime(2));
    }

    public async Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(
        Guid accountId, Guid sessionId, long characterId, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty || sessionId == Guid.Empty || characterId <= 0)
            return null;

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.instance_id, l.lease_version, l.valid_until_utc
            FROM character_leases l
            INNER JOIN characters c ON c.character_id = l.character_id
            INNER JOIN gateway_sessions s ON s.session_id = l.session_id
            INNER JOIN accounts a ON a.account_id = s.account_id
            WHERE c.character_id = @character_id AND c.account_id = @account_id
              AND s.session_id = @session_id AND s.account_id = @account_id
              AND s.revoked_at_utc IS NULL AND s.expires_at_utc > @now_utc
              AND a.status = 'active' AND l.valid_until_utc > @now_utc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@character_id", characterId);
        command.Parameters.AddWithValue("@account_id", accountId.ToString());
        command.Parameters.AddWithValue("@session_id", sessionId.ToString());
        command.Parameters.AddWithValue("@now_utc", nowUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new CharacterLeaseRecord(reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2));
    }

    public async Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(
        Guid transferId, Guid sessionId, long characterId, string sourceInstanceId, string targetInstanceId,
        long expectedLeaseVersion, byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (transferId == Guid.Empty || sessionId == Guid.Empty || characterId <= 0 ||
            expectedLeaseVersion < 0 || expectedLeaseVersion == long.MaxValue ||
            string.IsNullOrWhiteSpace(sourceInstanceId) ||
            string.IsNullOrWhiteSpace(targetInstanceId) ||
            string.Equals(sourceInstanceId, targetInstanceId, StringComparison.Ordinal) ||
            targetLeaseTokenHash.Length != 32 || validUntilUtc <= nowUtc)
            return new CharacterLeaseTransferResult(false, "invalid_transfer", null);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Serialize all transfers of this character, including duplicate retries.
        await using (var characterLock = connection.CreateCommand())
        {
            characterLock.Transaction = transaction;
            characterLock.CommandText = "SELECT character_id FROM characters WHERE character_id = @character_id FOR UPDATE;";
            characterLock.Parameters.AddWithValue("@character_id", characterId);
            if (await characterLock.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CharacterLeaseTransferResult(false, "character_not_found", null);
            }
        }

        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                SELECT session_id, character_id, source_instance_id, target_instance_id,
                       expected_lease_version, committed_lease_version, target_lease_token_hash
                FROM character_lease_transfers WHERE transfer_id = @transfer_id;
                """;
            duplicate.Parameters.AddWithValue("@transfer_id", transferId.ToString());
            await using var reader = await duplicate.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var matches = reader.GetString(0) == sessionId.ToString() && reader.GetInt64(1) == characterId &&
                    reader.GetString(2) == sourceInstanceId && reader.GetString(3) == targetInstanceId &&
                    reader.GetInt64(4) == expectedLeaseVersion &&
                    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        (byte[])reader.GetValue(6), targetLeaseTokenHash);
                var committedVersion = reader.GetInt64(5);
                await reader.DisposeAsync();
                await transaction.RollbackAsync(cancellationToken);
                return matches
                    ? new CharacterLeaseTransferResult(true, "duplicate", committedVersion)
                    : new CharacterLeaseTransferResult(false, "transfer_id_conflict", null);
            }
        }

        await using (var sessionCheck = connection.CreateCommand())
        {
            sessionCheck.Transaction = transaction;
            sessionCheck.CommandText = """
                SELECT 1 FROM gateway_sessions s
                INNER JOIN accounts a ON a.account_id = s.account_id
                INNER JOIN characters c ON c.account_id = a.account_id
                WHERE s.session_id = @session_id AND c.character_id = @character_id
                  AND s.revoked_at_utc IS NULL AND s.expires_at_utc > @now_utc AND a.status = 'active';
                """;
            sessionCheck.Parameters.AddWithValue("@session_id", sessionId.ToString());
            sessionCheck.Parameters.AddWithValue("@character_id", characterId);
            sessionCheck.Parameters.AddWithValue("@now_utc", nowUtc);
            if (await sessionCheck.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CharacterLeaseTransferResult(false, "session_or_character_invalid", null);
            }
        }

        var nextVersion = checked(expectedLeaseVersion + 1);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE character_leases
                SET session_id = @session_id, instance_id = @target_instance_id,
                    lease_token_hash = @lease_token_hash, lease_version = @next_version,
                    valid_until_utc = @valid_until_utc
                WHERE character_id = @character_id AND session_id = @session_id
                  AND instance_id = @source_instance_id AND lease_version = @expected_version
                  AND valid_until_utc > @now_utc;
                """;
            update.Parameters.AddWithValue("@session_id", sessionId.ToString());
            update.Parameters.AddWithValue("@target_instance_id", targetInstanceId);
            update.Parameters.AddWithValue("@lease_token_hash", targetLeaseTokenHash);
            update.Parameters.AddWithValue("@next_version", nextVersion);
            update.Parameters.AddWithValue("@valid_until_utc", validUntilUtc);
            update.Parameters.AddWithValue("@character_id", characterId);
            update.Parameters.AddWithValue("@source_instance_id", sourceInstanceId);
            update.Parameters.AddWithValue("@expected_version", expectedLeaseVersion);
            update.Parameters.AddWithValue("@now_utc", nowUtc);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CharacterLeaseTransferResult(false, "lease_fence_rejected", null);
            }
        }

        await using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO character_lease_transfers
                    (transfer_id, session_id, character_id, source_instance_id, target_instance_id,
                     expected_lease_version, committed_lease_version, target_lease_token_hash, committed_at_utc)
                VALUES (@transfer_id, @session_id, @character_id, @source_instance_id, @target_instance_id,
                        @expected_version, @next_version, @lease_token_hash, @now_utc);
                """;
            record.Parameters.AddWithValue("@transfer_id", transferId.ToString());
            record.Parameters.AddWithValue("@session_id", sessionId.ToString());
            record.Parameters.AddWithValue("@character_id", characterId);
            record.Parameters.AddWithValue("@source_instance_id", sourceInstanceId);
            record.Parameters.AddWithValue("@target_instance_id", targetInstanceId);
            record.Parameters.AddWithValue("@expected_version", expectedLeaseVersion);
            record.Parameters.AddWithValue("@next_version", nextVersion);
            record.Parameters.AddWithValue("@lease_token_hash", targetLeaseTokenHash);
            record.Parameters.AddWithValue("@now_utc", nowUtc);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CharacterLeaseTransferResult(true, "committed", nextVersion);
    }
}

public static class AccountEmail
{
    public static string Normalize(string email)
    {
        var normalized = email.Trim().ToUpperInvariant();
        if (normalized.Length is 0 or > 320 || !normalized.Contains('@', StringComparison.Ordinal))
            throw new ArgumentException("A valid account email is required.", nameof(email));
        return normalized;
    }
}
