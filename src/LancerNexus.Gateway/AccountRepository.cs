using MySqlConnector;

namespace LancerNexus.Gateway;

public sealed record AccountRecord(
    Guid AccountId,
    string Email,
    string PasswordHash,
    string Status);

public sealed record SessionRecord(Guid AccountId, DateTime ExpiresAtUtc);
public sealed record CharacterRecord(long CharacterId, string DisplayName, DateTime CreatedAtUtc);

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
}

public sealed class MySqlAccountRepository(string connectionString) : IAccountRepository
{
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
