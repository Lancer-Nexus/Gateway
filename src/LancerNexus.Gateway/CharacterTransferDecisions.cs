using MySqlConnector;

namespace LancerNexus.Gateway;

public enum CharacterTransferDecisionKind : byte { Committed = 6, Aborted = 22 }
public sealed record CharacterTransferBinding(Guid TransferId, Guid SessionId, long CharacterId,
    string SourceInstanceId, string TargetInstanceId, long ExpectedLeaseVersion);
public sealed record CharacterTransferDecision(CharacterTransferBinding Binding, CharacterTransferDecisionKind Kind);
public sealed record CharacterTransferAbortResult(bool Accepted, string ReasonCode, long? CommittedLeaseVersion = null);

public sealed partial class MySqlAccountRepository
{
    public async Task<CharacterTransferDecision?> FindCharacterTransferDecisionAsync(Guid transferId,
        CancellationToken cancellationToken = default)
    {
        if (transferId == Guid.Empty)
            return null;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadDecisionAsync(connection, null, transferId, cancellationToken);
    }

    public async Task<CharacterTransferAbortResult> AbortCharacterLeaseTransferAsync(CharacterTransferBinding binding,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (binding.TransferId == Guid.Empty || binding.SessionId == Guid.Empty || binding.CharacterId <= 0 ||
            binding.ExpectedLeaseVersion < 0 || binding.ExpectedLeaseVersion == long.MaxValue ||
            string.IsNullOrWhiteSpace(binding.SourceInstanceId) || string.IsNullOrWhiteSpace(binding.TargetInstanceId) ||
            binding.SourceInstanceId.Length > 96 || binding.TargetInstanceId.Length > 96 ||
            binding.SourceInstanceId == binding.TargetInstanceId)
            return new(false, "invalid_transfer");

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Same lock order as lease commit. Only one decision can win for this character.
        await using (var characterLock = connection.CreateCommand())
        {
            characterLock.Transaction = transaction;
            characterLock.CommandText = "SELECT character_id FROM characters WHERE character_id=@character FOR UPDATE";
            characterLock.Parameters.AddWithValue("@character", binding.CharacterId);
            if (await characterLock.ExecuteScalarAsync(cancellationToken) is null)
                return new(false, "character_not_found");
        }
        var previous = await ReadDecisionAsync(connection, transaction, binding.TransferId, cancellationToken);
        if (previous is not null)
        {
            if (previous.Binding != binding)
                return new(false, "transfer_id_conflict");
            if (previous.Kind == CharacterTransferDecisionKind.Committed)
                return new(false, "committed_transfer_cannot_abort", binding.ExpectedLeaseVersion + 1);
            await transaction.CommitAsync(cancellationToken);
            return new(true, "duplicate_abort");
        }

        // Expiry alone is not rollback authority. Bind the durable veto to the
        // unchanged source lease; no active session/lease expiry requirement is
        // needed to prevent a future commit of this exact transfer.
        await using (var lease = connection.CreateCommand())
        {
            lease.Transaction = transaction;
            lease.CommandText = """
                SELECT 1 FROM character_leases WHERE character_id=@character AND session_id=@session
                    AND instance_id=@source AND lease_version=@version FOR UPDATE
                """;
            lease.Parameters.AddWithValue("@character", binding.CharacterId);
            lease.Parameters.AddWithValue("@session", binding.SessionId.ToString("D"));
            lease.Parameters.AddWithValue("@source", binding.SourceInstanceId);
            lease.Parameters.AddWithValue("@version", binding.ExpectedLeaseVersion);
            if (await lease.ExecuteScalarAsync(cancellationToken) is null)
                return new(false, "lease_fence_rejected");
        }
        await InsertDecisionAsync(connection, transaction, binding, CharacterTransferDecisionKind.Aborted,
            nowUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, "aborted");
    }

    private static async Task<CharacterTransferDecision?> ReadDecisionAsync(MySqlConnection connection,
        MySqlTransaction? transaction, Guid transferId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session_id, character_id, source_instance_id, target_instance_id, expected_lease_version, decision
            FROM character_transfer_decisions WHERE transfer_id=@transfer
            """ + (transaction is null ? "" : " FOR UPDATE");
        command.Parameters.AddWithValue("@transfer", transferId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var kind = (CharacterTransferDecisionKind)reader.GetByte(5);
        if (kind is not (CharacterTransferDecisionKind.Committed or CharacterTransferDecisionKind.Aborted))
            throw new InvalidOperationException("Invalid persisted character transfer decision.");
        return new(new(transferId, reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4)), kind);
    }

    private static async Task InsertDecisionAsync(MySqlConnection connection, MySqlTransaction transaction,
        CharacterTransferBinding binding, CharacterTransferDecisionKind kind, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO character_transfer_decisions
                (transfer_id, session_id, character_id, source_instance_id, target_instance_id,
                 expected_lease_version, decision, decided_at_utc)
            VALUES (@transfer, @session, @character, @source, @target, @version, @decision, @now)
            """;
        command.Parameters.AddWithValue("@transfer", binding.TransferId.ToString("D"));
        command.Parameters.AddWithValue("@session", binding.SessionId.ToString("D"));
        command.Parameters.AddWithValue("@character", binding.CharacterId);
        command.Parameters.AddWithValue("@source", binding.SourceInstanceId);
        command.Parameters.AddWithValue("@target", binding.TargetInstanceId);
        command.Parameters.AddWithValue("@version", binding.ExpectedLeaseVersion);
        command.Parameters.AddWithValue("@decision", (byte)kind);
        command.Parameters.AddWithValue("@now", nowUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
