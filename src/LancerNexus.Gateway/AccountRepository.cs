using MySqlConnector;

namespace LancerNexus.Gateway;

public sealed record AccountRecord(
    Guid AccountId,
    string Email,
    string PasswordHash,
    string Status);

public interface IAccountRepository
{
    Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash,
        DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default);
}

public sealed class AccountRepositoryNotConfigured : IAccountRepository
{
    public Task<AccountRecord?> FindByEmailAsync(
        string email,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash,
        DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default) =>
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

    public async Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash,
        DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gateway_sessions
                (session_id, account_id, token_nonce_hash, created_at_utc,
                 last_seen_at_utc, expires_at_utc)
            VALUES
                (@session_id, @account_id, @token_nonce_hash, @created_at_utc,
                 @last_seen_at_utc, @expires_at_utc);
            """;
        command.Parameters.AddWithValue("@session_id", sessionId.ToString());
        command.Parameters.AddWithValue("@account_id", accountId.ToString());
        command.Parameters.AddWithValue("@token_nonce_hash", nonceHash);
        command.Parameters.AddWithValue("@created_at_utc", createdAtUtc);
        command.Parameters.AddWithValue("@last_seen_at_utc", createdAtUtc);
        command.Parameters.AddWithValue("@expires_at_utc", expiresAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
