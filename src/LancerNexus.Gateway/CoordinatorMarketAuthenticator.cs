using System.Security.Cryptography;
using System.Text;

namespace LancerNexus.Gateway;

public sealed class CoordinatorMarketAuthenticator(
    IConfiguration configuration,
    GameInstanceKeyAuthenticator gameInstances)
{
    private readonly string? key = configuration["Gateway:CoordinatorMarketApiKey"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(key) && Encoding.UTF8.GetByteCount(key) >= 32;

    public bool TryAuthenticate(HttpRequest request)
    {
        if (!IsConfigured || !request.IsHttps)
            return false;
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;
        var supplied = header[7..].Trim();
        if (supplied.Length < 32 || gameInstances.IsKnownCredential(supplied))
            return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key!)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }
}
