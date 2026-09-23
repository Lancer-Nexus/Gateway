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
}

public sealed class AccountRepositoryNotConfigured : IAccountRepository
{
    public Task<AccountRecord?> FindByEmailAsync(
        string email,
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
        return new AccountRecord(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
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
