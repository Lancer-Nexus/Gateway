using System.Security.Cryptography;
using LancerNexus.Protocol;
using MySqlConnector;

namespace LancerNexus.Gateway;

public sealed record AccountRecord(
    Guid AccountId,
    string Email,
    string PasswordHash,
    string Status);

public sealed record SessionRecord(Guid AccountId, DateTime ExpiresAtUtc);
public sealed record CharacterRecord(long CharacterId, string DisplayName, DateTime CreatedAtUtc,
    string? SystemId = null);
public sealed record CharacterRosterStoreResult(bool SessionAccepted, IReadOnlyList<CharacterRecord> Characters);
public sealed record CharacterDeletionStoreResult(string ReasonCode);
public sealed record CharacterIdentityResolveStoreResult(bool Accepted, string ReasonCode, long? CharacterId);
public sealed record CharacterLeaseRecord(string InstanceId, long LeaseVersion, DateTime ValidUntilUtc);
public sealed record CharacterLeaseAcquireResult(bool Accepted, string ReasonCode, long? LeaseVersion,
    DateTime? ValidUntilUtc);
public sealed record CharacterLeaseReleaseResult(bool Released, string ReasonCode, long? LeaseVersion);
public sealed record CharacterLeaseTransferResult(bool Accepted, string ReasonCode, long? LeaseVersion);
public sealed record CharacterLeaseTransferCommitRecord(Guid TransferId, Guid SessionId, long CharacterId,
    string SourceInstanceId, string TargetInstanceId, long ExpectedLeaseVersion, long CommittedLeaseVersion);

public interface ICharacterLeaseAcquisitionStore
{
    Task<CharacterLeaseAcquireResult> AcquireCharacterLeaseAsync(Guid accountId, Guid sessionId, long characterId,
        string instanceId, byte[] leaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default);
    Task<CharacterLeaseReleaseResult> ReleaseCharacterLeaseAsync(long characterId, Guid sessionId,
        string instanceId, byte[] leaseTokenHash, long leaseVersion, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Character lease release is not configured.");
}

public interface IAccountRepository
{
    Task<CharacterTransferDecision?> FindCharacterTransferDecisionAsync(Guid transferId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Character transfer decisions are not configured.");
    Task<CharacterTransferAbortResult> AbortCharacterLeaseTransferAsync(CharacterTransferBinding binding,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Character transfer decisions are not configured.");
    Task<CharacterLeaseTransferCommitRecord?> FindCharacterLeaseTransferCommitAsync(Guid transferId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Authoritative character transfer reads are not configured.");
    Task<SessionRecord?> FindActiveSessionAsync(Guid sessionId, Guid accountId, DateTime nowUtc,
        CancellationToken cancellationToken = default) => Task.FromResult<SessionRecord?>(null);
    Task<AccountRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task CreateSessionAsync(Guid sessionId, Guid accountId, byte[] nonceHash, byte[] refreshTokenHash,
        DateTime createdAtUtc, DateTime expiresAtUtc, CancellationToken cancellationToken = default);
    Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
        byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
        byte[] newRefreshTokenHash, TimeSpan maximumLifetime, TimeSpan idleTimeout, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        RotateRefreshTokenAsync(sessionId, oldRefreshTokenHash, newRefreshTokenHash, nowUtc, cancellationToken);
    Task<int> RevokeExpiredSessionsAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
    Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId,
        CancellationToken cancellationToken = default);
    Task<CharacterRosterStoreResult> ListActiveCharactersForSessionAsync(Guid accountId, Guid sessionId,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Character roster persistence is not configured.");
    Task<CharacterDeletionStoreResult> TombstoneCharacterAsync(CharacterDeletionRequestV1 request,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Character deletion persistence is not configured.");
    Task<CharacterIdentityResolveStoreResult> ResolveCharacterIdentityAsync(Guid accountId, Guid sessionId,
        string displayName, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Character identity resolution is not configured.");
    Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId,
        CancellationToken cancellationToken = default);
    Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId, long characterId,
        DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<CharacterLeaseRecord?> FindActiveCharacterLeaseForTransferAsync(Guid sessionId, long characterId,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult<CharacterLeaseRecord?>(null);
    Task<CharacterLeaseRecord?> FindActiveCharacterTargetLeaseAsync(long characterId, DateTime nowUtc,
        CancellationToken cancellationToken = default) => Task.FromResult<CharacterLeaseRecord?>(null);
    Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
        long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
        byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default);
    Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
        long characterId, string sourceInstanceId, string targetInstanceId, string targetSystemId,
        long expectedLeaseVersion, byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        CommitCharacterLeaseTransferAsync(transferId, sessionId, characterId, sourceInstanceId,
            targetInstanceId, expectedLeaseVersion, targetLeaseTokenHash, validUntilUtc, nowUtc,
            cancellationToken);
    Task<CharacterLeaseAcquireResult> RenewCharacterLeaseAsync(long characterId, string instanceId,
        byte[] leaseTokenHash, long leaseVersion, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Character lease renewal is not configured.");
}

public sealed class AccountRepositoryNotConfigured : IAccountRepository, ICharacterLeaseAcquisitionStore
{
    public Task<CharacterLeaseRecord?> FindActiveCharacterTargetLeaseAsync(long characterId, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");
    public Task<CharacterLeaseTransferCommitRecord?> FindCharacterLeaseTransferCommitAsync(Guid transferId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");
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

    public Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
        byte[] newRefreshTokenHash, TimeSpan maximumLifetime, TimeSpan idleTimeout, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<int> RevokeExpiredSessionsAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterRosterStoreResult> ListActiveCharactersForSessionAsync(Guid accountId, Guid sessionId,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterDeletionStoreResult> TombstoneCharacterAsync(CharacterDeletionRequestV1 request,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterIdentityResolveStoreResult> ResolveCharacterIdentityAsync(Guid accountId, Guid sessionId,
        string displayName, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseAsync(Guid accountId, Guid sessionId,
        long characterId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterLeaseAcquireResult> AcquireCharacterLeaseAsync(Guid accountId, Guid sessionId,
        long characterId, string instanceId, byte[] leaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterLeaseReleaseResult> ReleaseCharacterLeaseAsync(long characterId, Guid sessionId,
        string instanceId, byte[] leaseTokenHash, long leaseVersion, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterLeaseRecord?> FindActiveCharacterLeaseForTransferAsync(Guid sessionId, long characterId,
        DateTime nowUtc, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(Guid transferId, Guid sessionId,
        long characterId, string sourceInstanceId, string targetInstanceId, long expectedLeaseVersion,
        byte[] targetLeaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");

    public Task<CharacterLeaseAcquireResult> RenewCharacterLeaseAsync(long characterId, string instanceId,
        byte[] leaseTokenHash, long leaseVersion, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Gateway account persistence is not configured.");
}

public sealed partial class MySqlAccountRepository(string connectionString) : IAccountRepository, ICharacterLeaseAcquisitionStore
{
    public async Task<CharacterLeaseAcquireResult> AcquireCharacterLeaseAsync(Guid accountId, Guid sessionId,
        long characterId, string instanceId, byte[] leaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty || sessionId == Guid.Empty || characterId <= 0 ||
            !AdminQuery.ValidIdentifier(instanceId) || leaseTokenHash.Length != 32 || validUntilUtc <= nowUtc)
            return new(false, "lease_request_invalid", null, null);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The character row serializes first acquisition, expired-lease takeover, and transfers.
        await using (var characterLock = connection.CreateCommand())
        {
            characterLock.Transaction = transaction;
            characterLock.CommandText = "SELECT account_id FROM characters WHERE character_id = @character_id AND deleted_at_utc IS NULL FOR UPDATE;";
            characterLock.Parameters.AddWithValue("@character_id", characterId);
            var ownerValue = await characterLock.ExecuteScalarAsync(cancellationToken);
            var ownerId = ownerValue switch
            {
                Guid value => value,
                string value when Guid.TryParse(value, out var parsed) => parsed,
                _ => Guid.Empty
            };
            if (ownerId != accountId)
                return await RejectAcquireAsync(transaction, "character_not_found", cancellationToken);
        }

        await using (var sessionCheck = connection.CreateCommand())
        {
            sessionCheck.Transaction = transaction;
            sessionCheck.CommandText = """
                SELECT s.session_id FROM gateway_sessions s
                INNER JOIN accounts a ON a.account_id = s.account_id
                WHERE s.session_id = @session_id AND s.account_id = @account_id
                  AND s.revoked_at_utc IS NULL AND s.expires_at_utc > @now_utc AND a.status = 'active'
                FOR UPDATE;
                """;
            sessionCheck.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));
            sessionCheck.Parameters.AddWithValue("@account_id", accountId.ToString("D"));
            sessionCheck.Parameters.AddWithValue("@now_utc", nowUtc);
            if (await sessionCheck.ExecuteScalarAsync(cancellationToken) is null)
                return await RejectAcquireAsync(transaction, "session_or_account_invalid", cancellationToken);
        }

        long? currentVersion = null;
        Guid currentSession = Guid.Empty;
        string? currentInstance = null;
        byte[]? currentTokenHash = null;
        DateTime? currentExpiry = null;
        await using (var leaseRead = connection.CreateCommand())
        {
            leaseRead.Transaction = transaction;
            leaseRead.CommandText = """
                SELECT session_id, instance_id, lease_token_hash, lease_version, valid_until_utc
                FROM character_leases WHERE character_id = @character_id FOR UPDATE;
                """;
            leaseRead.Parameters.AddWithValue("@character_id", characterId);
            await using var reader = await leaseRead.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                currentSession = reader.GetGuid(0);
                currentInstance = reader.GetString(1);
                currentTokenHash = (byte[])reader.GetValue(2);
                currentVersion = reader.GetInt64(3);
                currentExpiry = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);
            }
        }

        if (currentVersion is { } version)
        {
            var currentSessionActive = false;
            await using (var currentSessionCheck = connection.CreateCommand())
            {
                currentSessionCheck.Transaction = transaction;
                currentSessionCheck.CommandText = """
                    SELECT 1 FROM gateway_sessions s
                    INNER JOIN accounts a ON a.account_id = s.account_id
                    WHERE s.session_id = @session_id AND s.revoked_at_utc IS NULL
                      AND s.expires_at_utc > @now_utc AND a.status = 'active'
                    FOR UPDATE;
                    """;
                currentSessionCheck.Parameters.AddWithValue("@session_id", currentSession.ToString("D"));
                currentSessionCheck.Parameters.AddWithValue("@now_utc", nowUtc);
                currentSessionActive = await currentSessionCheck.ExecuteScalarAsync(cancellationToken) is not null;
            }

            if (currentExpiry > nowUtc && currentSessionActive)
            {
                var sameOwner = currentSession == sessionId && currentInstance == instanceId;
                var sameToken = currentTokenHash is { Length: 32 } storedHash &&
                    CryptographicOperations.FixedTimeEquals(storedHash, leaseTokenHash);
                if (sameOwner && sameToken)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new(true, "duplicate", version, currentExpiry);
                }
                return await RejectAcquireAsync(transaction, "lease_owned", cancellationToken);
            }

            if (version == long.MaxValue)
                return await RejectAcquireAsync(transaction, "lease_version_exhausted", cancellationToken);
            var nextVersion = version + 1;
            await using var takeover = connection.CreateCommand();
            takeover.Transaction = transaction;
            takeover.CommandText = """
                UPDATE character_leases
                SET session_id = @session_id, instance_id = @instance_id, lease_token_hash = @token_hash,
                    lease_version = @next_version, valid_until_utc = @valid_until_utc
                WHERE character_id = @character_id AND session_id = @old_session_id
                  AND lease_version = @old_version
                  AND (valid_until_utc <= @now_utc OR @reclaim_invalid_session = 1);
                """;
            takeover.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));
            takeover.Parameters.AddWithValue("@instance_id", instanceId);
            takeover.Parameters.AddWithValue("@token_hash", leaseTokenHash);
            takeover.Parameters.AddWithValue("@next_version", nextVersion);
            takeover.Parameters.AddWithValue("@valid_until_utc", validUntilUtc);
            takeover.Parameters.AddWithValue("@character_id", characterId);
            takeover.Parameters.AddWithValue("@old_session_id", currentSession.ToString("D"));
            takeover.Parameters.AddWithValue("@old_version", version);
            takeover.Parameters.AddWithValue("@now_utc", nowUtc);
            takeover.Parameters.AddWithValue("@reclaim_invalid_session", currentSessionActive ? 0 : 1);
            if (await takeover.ExecuteNonQueryAsync(cancellationToken) != 1)
                return await RejectAcquireAsync(transaction, "lease_fence_rejected", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(true, "reacquired", nextVersion, validUntilUtc);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO character_leases
                    (character_id, session_id, instance_id, lease_token_hash, lease_version, valid_until_utc)
                VALUES (@character_id, @session_id, @instance_id, @token_hash, 1, @valid_until_utc);
                """;
            insert.Parameters.AddWithValue("@character_id", characterId);
            insert.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));
            insert.Parameters.AddWithValue("@instance_id", instanceId);
            insert.Parameters.AddWithValue("@token_hash", leaseTokenHash);
            insert.Parameters.AddWithValue("@valid_until_utc", validUntilUtc);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(true, "acquired", 1, validUntilUtc);
    }

    public async Task<CharacterLeaseReleaseResult> ReleaseCharacterLeaseAsync(long characterId, Guid sessionId,
        string instanceId, byte[] leaseTokenHash, long leaseVersion, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || sessionId == Guid.Empty || !AdminQuery.ValidIdentifier(instanceId) ||
            leaseTokenHash.Length != 32 || leaseVersion <= 0)
            return new(false, "lease_request_invalid", null);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var characterLock = connection.CreateCommand())
        {
            characterLock.Transaction = transaction;
            characterLock.CommandText = "SELECT character_id FROM characters WHERE character_id = @character_id FOR UPDATE;";
            characterLock.Parameters.AddWithValue("@character_id", characterId);
            if (await characterLock.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "character_not_found", null);
            }
        }

        Guid currentSession;
        string currentInstance;
        byte[] currentTokenHash;
        long currentVersion;
        DateTime currentExpiry;
        await using (var leaseRead = connection.CreateCommand())
        {
            leaseRead.Transaction = transaction;
            leaseRead.CommandText = """
                SELECT session_id, instance_id, lease_token_hash, lease_version, valid_until_utc
                FROM character_leases WHERE character_id = @character_id FOR UPDATE;
                """;
            leaseRead.Parameters.AddWithValue("@character_id", characterId);
            await using var reader = await leaseRead.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.DisposeAsync();
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "lease_not_found", null);
            }
            currentSession = reader.GetGuid(0);
            currentInstance = reader.GetString(1);
            currentTokenHash = (byte[])reader.GetValue(2);
            currentVersion = reader.GetInt64(3);
            currentExpiry = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);
        }

        if (currentSession != sessionId || currentInstance != instanceId || currentVersion != leaseVersion ||
            currentTokenHash.Length != 32 || !CryptographicOperations.FixedTimeEquals(currentTokenHash, leaseTokenHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, "lease_mismatch", null);
        }

        if (currentExpiry <= nowUtc)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(true, "duplicate", leaseVersion);
        }

        await using (var release = connection.CreateCommand())
        {
            release.Transaction = transaction;
            release.CommandText = """
                UPDATE character_leases SET valid_until_utc = @now_utc
                WHERE character_id = @character_id AND session_id = @session_id AND instance_id = @instance_id
                  AND lease_token_hash = @token_hash AND lease_version = @lease_version
                  AND valid_until_utc > @now_utc;
                """;
            release.Parameters.AddWithValue("@now_utc", nowUtc);
            release.Parameters.AddWithValue("@character_id", characterId);
            release.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));
            release.Parameters.AddWithValue("@instance_id", instanceId);
            release.Parameters.AddWithValue("@token_hash", leaseTokenHash);
            release.Parameters.AddWithValue("@lease_version", leaseVersion);
            if (await release.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "lease_mismatch", null);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new(true, "released", leaseVersion);
    }

    public async Task<CharacterLeaseAcquireResult> RenewCharacterLeaseAsync(long characterId, string instanceId,
        byte[] leaseTokenHash, long leaseVersion, DateTime validUntilUtc, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0 || !AdminQuery.ValidIdentifier(instanceId) || leaseTokenHash.Length != 32 ||
            leaseVersion <= 0 || validUntilUtc <= nowUtc)
            return new(false, "lease_request_invalid", null, null);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE character_leases l
            INNER JOIN gateway_sessions s ON s.session_id = l.session_id
            INNER JOIN accounts a ON a.account_id = s.account_id
            INNER JOIN characters c ON c.character_id = l.character_id AND c.deleted_at_utc IS NULL
            SET l.valid_until_utc = @valid_until_utc
            WHERE l.character_id = @character_id AND l.instance_id = @instance_id
              AND l.lease_version = @lease_version AND l.lease_token_hash = @lease_token_hash
              AND l.valid_until_utc > @now_utc AND s.revoked_at_utc IS NULL
              AND s.expires_at_utc > @now_utc AND a.status = 'active';
            """;
        command.Parameters.AddWithValue("@valid_until_utc", validUntilUtc);
        command.Parameters.AddWithValue("@character_id", characterId);
        command.Parameters.AddWithValue("@instance_id", instanceId);
        command.Parameters.AddWithValue("@lease_version", leaseVersion);
        command.Parameters.AddWithValue("@lease_token_hash", leaseTokenHash);
        command.Parameters.AddWithValue("@now_utc", nowUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            return new(false, "character_lease_rejected", null, null);
        return new(true, "renewed", leaseVersion, validUntilUtc);
    }

    private static async Task<CharacterLeaseAcquireResult> RejectAcquireAsync(MySqlTransaction transaction,
        string reasonCode, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return new(false, reasonCode, null, null);
    }

    public async Task<CharacterLeaseRecord?> FindActiveCharacterTargetLeaseAsync(long characterId, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) return null;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.instance_id, l.lease_version, l.valid_until_utc
            FROM character_leases l
            INNER JOIN characters c ON c.character_id = l.character_id AND c.deleted_at_utc IS NULL
            INNER JOIN gateway_sessions s ON s.session_id = l.session_id
            INNER JOIN accounts a ON a.account_id = s.account_id AND a.account_id = c.account_id
            WHERE c.character_id = @character_id AND s.revoked_at_utc IS NULL
              AND s.expires_at_utc > @now_utc AND a.status = 'active' AND l.valid_until_utc > @now_utc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@character_id", characterId);
        command.Parameters.AddWithValue("@now_utc", nowUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CharacterLeaseRecord(reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2))
            : null;
    }

    public async Task<CharacterLeaseTransferCommitRecord?> FindCharacterLeaseTransferCommitAsync(Guid transferId,
        CancellationToken cancellationToken = default)
    {
        if (transferId == Guid.Empty)
            return null;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // A historical commit is durable even after session expiry or a later transfer.
        // Do not treat absence of a current active lease as evidence of rollback.
        command.CommandText = """
            SELECT session_id, character_id, source_instance_id, target_instance_id,
                   expected_lease_version, committed_lease_version
            FROM character_lease_transfers WHERE transfer_id = @transfer_id;
            """;
        command.Parameters.AddWithValue("@transfer_id", transferId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(transferId, reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5))
            : null;
    }

    public async Task<SessionRecord?> FindActiveSessionAsync(Guid sessionId, Guid accountId, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.expires_at_utc FROM gateway_sessions s JOIN accounts a ON a.account_id = s.account_id
            WHERE s.session_id = @session AND s.account_id = @actor AND a.status = 'active'
              AND s.revoked_at_utc IS NULL AND s.expires_at_utc > @now LIMIT 1
            """;
        command.Parameters.AddWithValue("@session", sessionId.ToString());
        command.Parameters.AddWithValue("@actor", accountId.ToString());
        command.Parameters.AddWithValue("@now", nowUtc);
        var expiry = await command.ExecuteScalarAsync(cancellationToken);
        return expiry is DateTime value ? new SessionRecord(accountId, DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;
    }
    public Task<SessionRecord?> RotateRefreshTokenAsync(Guid sessionId, byte[] oldRefreshTokenHash,
        byte[] newRefreshTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        RotateRefreshTokenAsync(sessionId, oldRefreshTokenHash, newRefreshTokenHash, TimeSpan.FromHours(24),
            TimeSpan.FromMinutes(5), nowUtc, cancellationToken);

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
            INNER JOIN characters c ON c.character_id = l.character_id AND c.account_id = a.account_id AND c.deleted_at_utc IS NULL
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
        byte[] newRefreshTokenHash, TimeSpan maximumLifetime, TimeSpan idleTimeout, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE gateway_sessions
            SET refresh_token_hash = @new_refresh_token_hash,
                last_seen_at_utc = @now_utc,
                expires_at_utc = LEAST(
                    DATE_ADD(created_at_utc, INTERVAL @maximum_lifetime_seconds SECOND),
                    DATE_ADD(@now_utc, INTERVAL @idle_timeout_seconds SECOND))
            WHERE session_id = @session_id
              AND refresh_token_hash = @old_refresh_token_hash
              AND revoked_at_utc IS NULL
              AND expires_at_utc > @now_utc;
            """;
        update.Parameters.AddWithValue("@new_refresh_token_hash", newRefreshTokenHash);
        update.Parameters.AddWithValue("@session_id", sessionId.ToString());
        update.Parameters.AddWithValue("@old_refresh_token_hash", oldRefreshTokenHash);
        update.Parameters.AddWithValue("@now_utc", nowUtc);
        update.Parameters.AddWithValue("@maximum_lifetime_seconds", checked((long)maximumLifetime.TotalSeconds));
        update.Parameters.AddWithValue("@idle_timeout_seconds", checked((long)idleTimeout.TotalSeconds));
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

    public async Task<int> RevokeExpiredSessionsAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE gateway_sessions
            SET revoked_at_utc = @now_utc, refresh_token_hash = NULL
            WHERE revoked_at_utc IS NULL AND expires_at_utc <= @now_utc;
            """;
        command.Parameters.AddWithValue("@now_utc", nowUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CharacterRecord>> ListCharactersAsync(Guid accountId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.character_id, c.display_name, c.created_at_utc, s.system_id
            FROM characters c
            LEFT JOIN character_save_snapshots s ON s.character_id = c.character_id
            WHERE c.account_id = @account_id
              AND c.deleted_at_utc IS NULL
            ORDER BY c.character_id;
            """;
        command.Parameters.AddWithValue("@account_id", accountId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var characters = new List<CharacterRecord>();
        while (await reader.ReadAsync(cancellationToken))
            characters.Add(new CharacterRecord(reader.GetInt64(0), reader.GetString(1), reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return characters;
    }

    public async Task<CharacterRosterStoreResult> ListActiveCharactersForSessionAsync(Guid accountId, Guid sessionId,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty || sessionId == Guid.Empty)
            return new(false, []);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = """
                SELECT s.session_id FROM gateway_sessions s
                INNER JOIN accounts a ON a.account_id = s.account_id
                WHERE s.session_id = @session_id AND s.account_id = @account_id
                  AND s.revoked_at_utc IS NULL AND s.expires_at_utc > @now_utc AND a.status = 'active';
                """;
            session.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));
            session.Parameters.AddWithValue("@account_id", accountId.ToString("D"));
            session.Parameters.AddWithValue("@now_utc", nowUtc);
            if (await session.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, []);
            }
        }
        var characters = new List<CharacterRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT c.character_id, c.display_name, c.created_at_utc, s.system_id
                FROM characters c
                LEFT JOIN character_save_snapshots s ON s.character_id = c.character_id
                WHERE c.account_id = @account_id AND c.deleted_at_utc IS NULL ORDER BY c.character_id;
                """;
            command.Parameters.AddWithValue("@account_id", accountId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                characters.Add(new CharacterRecord(reader.GetInt64(0), reader.GetString(1), reader.GetDateTime(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        await transaction.CommitAsync(cancellationToken);
        return new(true, characters);
    }

    public async Task<CharacterDeletionStoreResult> TombstoneCharacterAsync(CharacterDeletionRequestV1 request,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (!request.IsValid())
            return new("character_not_found");
        if (await FindActiveSessionAsync(request.SessionId, request.AccountId, nowUtc, cancellationToken) is null)
            return new("session_rejected");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        bool found;
        bool alreadyDeleted;
        await using (var character = connection.CreateCommand())
        {
            character.Transaction = transaction;
            character.CommandText = """
                SELECT character_id, deleted_at_utc FROM characters
                WHERE character_id = @character_id AND account_id = @account_id FOR UPDATE;
                """;
            character.Parameters.AddWithValue("@character_id", request.CharacterId);
            character.Parameters.AddWithValue("@account_id", request.AccountId.ToString("D"));
            await using var reader = await character.ExecuteReaderAsync(cancellationToken);
            found = await reader.ReadAsync(cancellationToken);
            alreadyDeleted = found && !reader.IsDBNull(1);
        }
        if (!found)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new("character_not_found");
        }

        await using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = """
                SELECT s.session_id FROM gateway_sessions s
                INNER JOIN accounts a ON a.account_id = s.account_id
                WHERE s.session_id = @session_id AND s.account_id = @account_id
                  AND s.revoked_at_utc IS NULL AND s.expires_at_utc > @now_utc AND a.status = 'active'
                FOR UPDATE;
                """;
            session.Parameters.AddWithValue("@session_id", request.SessionId.ToString("D"));
            session.Parameters.AddWithValue("@account_id", request.AccountId.ToString("D"));
            session.Parameters.AddWithValue("@now_utc", nowUtc);
            if (await session.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new("session_rejected");
            }
        }
        if (alreadyDeleted)
        {
            await transaction.CommitAsync(cancellationToken);
            return new("duplicate");
        }

        await using (var lease = connection.CreateCommand())
        {
            lease.Transaction = transaction;
            lease.CommandText = "SELECT valid_until_utc FROM character_leases WHERE character_id = @character_id FOR UPDATE;";
            lease.Parameters.AddWithValue("@character_id", request.CharacterId);
            var value = await lease.ExecuteScalarAsync(cancellationToken);
            if (value is DateTime expiry && expiry > nowUtc)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new("character_in_use");
            }
        }

        await using (var transfer = connection.CreateCommand())
        {
            transfer.Transaction = transaction;
            transfer.CommandText = "SELECT transfer_id FROM character_transfer_snapshots WHERE character_id = @character_id LIMIT 1 FOR UPDATE;";
            transfer.Parameters.AddWithValue("@character_id", request.CharacterId);
            if (await transfer.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new("transfer_in_progress");
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE characters SET deleted_at_utc = @now_utc, updated_at_utc = @now_utc WHERE character_id = @character_id AND deleted_at_utc IS NULL;";
            update.Parameters.AddWithValue("@now_utc", nowUtc);
            update.Parameters.AddWithValue("@character_id", request.CharacterId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new("character_not_found");
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new("deleted");
    }

    public async Task<CharacterIdentityResolveStoreResult> ResolveCharacterIdentityAsync(Guid accountId,
        Guid sessionId, string displayName, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty || sessionId == Guid.Empty || string.IsNullOrWhiteSpace(displayName) ||
            displayName.Length > 96 || displayName != displayName.Trim() || displayName.Any(char.IsControl))
            return new(false, "character_identity_invalid", null);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = """
                SELECT s.session_id
                FROM gateway_sessions s
                INNER JOIN accounts a ON a.account_id = s.account_id
                WHERE s.session_id = @session_id AND s.account_id = @account_id
                  AND s.revoked_at_utc IS NULL AND s.expires_at_utc > @now_utc
                  AND a.status = 'active'
                FOR UPDATE;
                """;
            session.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));
            session.Parameters.AddWithValue("@account_id", accountId.ToString("D"));
            session.Parameters.AddWithValue("@now_utc", nowUtc);
            if (await session.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "session_rejected", null);
            }
        }

        long characterId;
        await using (var allocate = connection.CreateCommand())
        {
            allocate.Transaction = transaction;
            allocate.CommandText = "INSERT INTO gateway_character_id_allocator () VALUES ();";
            await allocate.ExecuteNonQueryAsync(cancellationToken);
            characterId = allocate.LastInsertedId;
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO characters (character_id, account_id, display_name, created_at_utc, updated_at_utc)
                VALUES (@character_id, @account_id, @display_name, @now_utc, @now_utc)
                ON DUPLICATE KEY UPDATE character_id = LAST_INSERT_ID(character_id);
                """;
            insert.Parameters.AddWithValue("@character_id", characterId);
            insert.Parameters.AddWithValue("@account_id", accountId.ToString("D"));
            insert.Parameters.AddWithValue("@display_name", displayName);
            insert.Parameters.AddWithValue("@now_utc", nowUtc);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            if (insert.LastInsertedId > 0)
                characterId = insert.LastInsertedId;
        }

        var belongsToAccount = false;
        await using (var owner = connection.CreateCommand())
        {
            owner.Transaction = transaction;
            owner.CommandText = "SELECT account_id FROM characters WHERE character_id = @character_id AND deleted_at_utc IS NULL FOR UPDATE;";
            owner.Parameters.AddWithValue("@character_id", characterId);
            var ownerId = await owner.ExecuteScalarAsync(cancellationToken);
            belongsToAccount = ownerId switch
            {
                Guid value => value == accountId,
                string value => string.Equals(value, accountId.ToString("D"), StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }
        await transaction.CommitAsync(cancellationToken);
        return belongsToAccount
            ? new(true, "resolved", characterId)
            : new(false, "character_name_conflict", null);
    }

    public async Task<CharacterRecord?> FindCharacterAsync(Guid accountId, long characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.character_id, c.display_name, c.created_at_utc, s.system_id
            FROM characters c
            LEFT JOIN character_save_snapshots s ON s.character_id = c.character_id
            WHERE c.account_id = @account_id AND c.character_id = @character_id AND c.deleted_at_utc IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@account_id", accountId.ToString());
        command.Parameters.AddWithValue("@character_id", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new CharacterRecord(reader.GetInt64(0), reader.GetString(1), reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
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
            INNER JOIN characters c ON c.character_id = l.character_id AND c.deleted_at_utc IS NULL
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
        => await CommitCharacterLeaseTransferCoreAsync(transferId, sessionId, characterId, sourceInstanceId,
            targetInstanceId, null, expectedLeaseVersion, targetLeaseTokenHash, validUntilUtc, nowUtc,
            cancellationToken);

    public Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferAsync(
        Guid transferId, Guid sessionId, long characterId, string sourceInstanceId, string targetInstanceId,
        string targetSystemId, long expectedLeaseVersion, byte[] targetLeaseTokenHash,
        DateTime validUntilUtc, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        CommitCharacterLeaseTransferCoreAsync(transferId, sessionId, characterId, sourceInstanceId,
            targetInstanceId, targetSystemId, expectedLeaseVersion, targetLeaseTokenHash, validUntilUtc,
            nowUtc, cancellationToken);

    private async Task<CharacterLeaseTransferResult> CommitCharacterLeaseTransferCoreAsync(
        Guid transferId, Guid sessionId, long characterId, string sourceInstanceId, string targetInstanceId,
        string? targetSystemId, long expectedLeaseVersion, byte[] targetLeaseTokenHash,
        DateTime validUntilUtc, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (transferId == Guid.Empty || sessionId == Guid.Empty || characterId <= 0 ||
            expectedLeaseVersion <= 0 || expectedLeaseVersion == long.MaxValue ||
            string.IsNullOrWhiteSpace(sourceInstanceId) ||
            string.IsNullOrWhiteSpace(targetInstanceId) ||
            sourceInstanceId.Length > 96 || targetInstanceId.Length > 96 ||
            string.Equals(sourceInstanceId, targetInstanceId, StringComparison.Ordinal) ||
            (targetSystemId is not null && !AdminQuery.ValidIdentifier(targetSystemId)) ||
            targetLeaseTokenHash.Length != 32 || validUntilUtc <= nowUtc)
            return new CharacterLeaseTransferResult(false, "invalid_transfer", null);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Serialize all transfers of this character, including duplicate retries.
        await using (var characterLock = connection.CreateCommand())
        {
            characterLock.Transaction = transaction;
            characterLock.CommandText = "SELECT character_id FROM characters WHERE character_id = @character_id AND deleted_at_utc IS NULL FOR UPDATE;";
            characterLock.Parameters.AddWithValue("@character_id", characterId);
            if (await characterLock.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CharacterLeaseTransferResult(false, "character_not_found", null);
            }
        }

        var binding = new CharacterTransferBinding(transferId, sessionId, characterId, sourceInstanceId,
            targetInstanceId, expectedLeaseVersion);
        var decision = await ReadDecisionAsync(connection, transaction, transferId, cancellationToken);
        if (decision is not null && decision.Binding != binding)
            return new(false, "transfer_id_conflict", null);
        if (decision?.Kind == CharacterTransferDecisionKind.Aborted)
            return new(false, "character_transfer_aborted", null);

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
                var matches = reader.GetGuid(0) == sessionId && reader.GetInt64(1) == characterId &&
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

        if (decision?.Kind == CharacterTransferDecisionKind.Committed)
            return new(false, "character_transfer_commit_missing", null);

        await using (var sessionCheck = connection.CreateCommand())
        {
            sessionCheck.Transaction = transaction;
            sessionCheck.CommandText = """
                SELECT 1 FROM gateway_sessions s
                INNER JOIN accounts a ON a.account_id = s.account_id
                INNER JOIN characters c ON c.account_id = a.account_id
                WHERE s.session_id = @session_id AND c.character_id = @character_id AND c.deleted_at_utc IS NULL
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

        if (targetSystemId is not null)
        {
            await using var location = connection.CreateCommand();
            location.Transaction = transaction;
            location.CommandText = "UPDATE character_save_snapshots SET system_id = @system_id WHERE character_id = @character_id;";
            location.Parameters.AddWithValue("@system_id", targetSystemId.ToLowerInvariant());
            location.Parameters.AddWithValue("@character_id", characterId);
            await location.ExecuteNonQueryAsync(cancellationToken);
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

        if (decision is null)
            await InsertDecisionAsync(connection, transaction, binding, CharacterTransferDecisionKind.Committed,
                nowUtc, cancellationToken);
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
