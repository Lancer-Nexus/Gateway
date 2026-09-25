using System.Security.Cryptography;

namespace LancerNexus.Gateway;

public sealed class TransferSnapshotProtection(IConfiguration configuration)
{
    public bool TryProtect(Guid transferId, ReadOnlySpan<byte> snapshot, out string keyId, out byte[] protectedSnapshot)
    {
        keyId = configuration["Gateway:TransferSnapshotCurrentKeyId"] ?? string.Empty;
        protectedSnapshot = [];
        if (transferId == Guid.Empty || keyId.Length is 0 or > 64 ||
            snapshot.Length is 0 or > TransferSnapshotLimits.MaxBytes || !TryGetKey(keyId, out var key))
            return false;

        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[snapshot.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, snapshot, ciphertext, tag, transferId.ToByteArray());
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
    {
        snapshot = [];
        const int overhead = 12 + 16;
        if (transferId == Guid.Empty || protectedSnapshot.Length <= overhead ||
            protectedSnapshot.Length > TransferSnapshotLimits.MaxBytes + overhead || !TryGetKey(keyId, out var key))
            return false;

        var plaintext = new byte[protectedSnapshot.Length - overhead];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(protectedSnapshot[..12], protectedSnapshot[overhead..],
                protectedSnapshot.Slice(12, 16), plaintext, transferId.ToByteArray());
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
