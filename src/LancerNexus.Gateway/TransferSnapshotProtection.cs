using System.Security.Cryptography;

namespace LancerNexus.Gateway;

public sealed class TransferSnapshotProtection(IConfiguration configuration)
{
    public bool TryProtect(Guid transferId, ReadOnlySpan<byte> snapshot, out string keyId, out byte[] protectedSnapshot)
        => transferId != Guid.Empty
            ? TryProtect(transferId.ToByteArray(), snapshot, out keyId, out protectedSnapshot)
            : Fail(out keyId, out protectedSnapshot);

    /// <summary>Protects a snapshot using caller-supplied, stable authenticated metadata.</summary>
    public bool TryProtect(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> snapshot,
        out string keyId, out byte[] protectedSnapshot)
    {
        keyId = configuration["Gateway:TransferSnapshotCurrentKeyId"] ?? string.Empty;
        protectedSnapshot = [];
        if (associatedData.Length is 0 or > 128 || keyId.Length is 0 or > 64 ||
            snapshot.Length is 0 or > TransferSnapshotLimits.MaxBytes || !TryGetKey(keyId, out var key))
            return false;

        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[snapshot.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, snapshot, ciphertext, tag, associatedData);
            protectedSnapshot = new byte[nonce.Length + tag.Length + ciphertext.Length];
            nonce.CopyTo(protectedSnapshot, 0);
            tag.CopyTo(protectedSnapshot, nonce.Length);
            ciphertext.CopyTo(protectedSnapshot, nonce.Length + tag.Length);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    public bool TryUnprotect(Guid transferId, string keyId, ReadOnlySpan<byte> protectedSnapshot,
        out byte[] snapshot)
        => transferId != Guid.Empty
            ? TryUnprotect(transferId.ToByteArray(), keyId, protectedSnapshot, out snapshot)
            : Fail(out snapshot);

    /// <summary>Unprotects a snapshot only when its authenticated metadata matches.</summary>
    public bool TryUnprotect(ReadOnlySpan<byte> associatedData, string keyId,
        ReadOnlySpan<byte> protectedSnapshot, out byte[] snapshot)
    {
        snapshot = [];
        const int overhead = 12 + 16;
        if (associatedData.Length is 0 or > 128 || protectedSnapshot.Length <= overhead ||
            protectedSnapshot.Length > TransferSnapshotLimits.MaxBytes + overhead || !TryGetKey(keyId, out var key))
            return false;

        var plaintext = new byte[protectedSnapshot.Length - overhead];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(protectedSnapshot[..12], protectedSnapshot[overhead..],
                protectedSnapshot.Slice(12, 16), plaintext, associatedData);
            snapshot = plaintext;
            return true;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool Fail(out string keyId, out byte[] protectedSnapshot)
    {
        keyId = string.Empty;
        protectedSnapshot = [];
        return false;
    }

    private static bool Fail(out byte[] snapshot)
    {
        snapshot = [];
        return false;
    }

    private bool TryGetKey(string keyId, out byte[] key)
    {
        key = [];
        var encoded = configuration[$"Gateway:TransferSnapshotKeys:{keyId}"];
        if (string.IsNullOrWhiteSpace(encoded))
            return false;
        try
        {
            var decoded = Convert.FromBase64String(encoded);
            if (decoded.Length != 32)
            {
                CryptographicOperations.ZeroMemory(decoded);
                return false;
            }
            key = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class TransferSnapshotLimits
{
    public const int MaxBytes = 16 * 1024 * 1024;
}
