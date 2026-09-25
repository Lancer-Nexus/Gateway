using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace LancerNexus.Gateway;

public sealed class GameInstanceKeyAuthenticator(IConfiguration configuration)
{
    public bool TryAuthenticate(HttpRequest request, out string instanceId)
    {
        instanceId = string.Empty;
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;
        var supplied = authorization[7..].Trim();
        if (supplied.Length < 32)
            return false;

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        string? match = null;
        foreach (var entry in configuration.GetSection("Gateway:GameInstanceKeys").GetChildren())
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value) ||
                Encoding.UTF8.GetByteCount(entry.Value) < 32)
                continue;
            var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(entry.Value));
            if (!CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash))
                continue;
            if (match is not null)
                return false;
            match = entry.Key;
        }

        if (match is null)
            return false;
        instanceId = match;
        return true;
    }
}
