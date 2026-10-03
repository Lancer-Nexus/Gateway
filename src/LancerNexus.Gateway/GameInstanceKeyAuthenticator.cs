using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace LancerNexus.Gateway;

public sealed class GameInstanceKeyAuthenticator(IConfiguration configuration)
{
    private readonly IReadOnlyDictionary<string, string?> keys = ReadKeys(configuration);

    private static IReadOnlyDictionary<string, string?> ReadKeys(IConfiguration configuration)
    {
        var result = configuration.GetSection("Gateway:GameInstanceKeys").GetChildren()
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var file = configuration["Gateway:GameInstanceKeysFile"];
        if (!string.IsNullOrWhiteSpace(file))
        {
            var additional = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))
                ?? throw new InvalidOperationException("Instance key file must contain a JSON object.");
            foreach (var entry in additional)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 96 ||
                    string.IsNullOrWhiteSpace(entry.Value) || Encoding.UTF8.GetByteCount(entry.Value) < 32)
                    throw new InvalidOperationException("Instance key file contains an invalid entry.");
                result[entry.Key] = entry.Value;
            }
        }
        return result;
    }

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
        foreach (var entry in keys)
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

    // Unlike identity resolution, this also recognizes credentials accidentally
    // shared by multiple instances. Service authority must reject all of them.
    public bool IsKnownCredential(string token)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return keys.Values.Any(value => !string.IsNullOrWhiteSpace(value) &&
            CryptographicOperations.FixedTimeEquals(suppliedHash, SHA256.HashData(Encoding.UTF8.GetBytes(value))));
    }
}
