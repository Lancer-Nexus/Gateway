using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record ClientVersionPolicy(
    Version MinimumVersion, Version LatestVersion, int ProtocolVersion,
    string RequiredDataManifestId, string Channel)
{
    public static ClientVersionPolicy FromConfiguration(IConfiguration configuration)
    {
        var minimum = ParseVersion(configuration["Gateway:MinimumClientVersion"] ?? "1.0.0");
        var latest = ParseVersion(configuration["Gateway:LatestClientVersion"] ?? "1.0.0");
        var protocol = configuration.GetValue<int?>("Gateway:ClientProtocolVersion") ?? 1;
        var data = configuration["Gateway:RequiredDataManifestId"] ?? "data-2026-09-22";
        var channel = configuration["Gateway:ClientChannel"] ?? "stable";
        if (minimum > latest || protocol < 1 || string.IsNullOrWhiteSpace(data) ||
            string.IsNullOrWhiteSpace(channel))
            throw new InvalidOperationException("Gateway client-version policy is invalid.");
        return new ClientVersionPolicy(minimum, latest, protocol, data, channel);
    }

    public static Version ParseVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32)
            throw new FormatException("Client version must be major.minor.patch.");
        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)) ||
            !Version.TryParse(value, out var version))
            throw new FormatException("Client version must be major.minor.patch.");
        return version;
    }
}

public sealed class ClientVersionHandshake(ClientVersionPolicy policy, SessionTokenOptions tokenOptions, TimeProvider clock)
{
    private static readonly HashSet<string> SupportedPlatforms =
        ["linux-x64", "linux-arm64", "win-x64", "win-arm64"];
    private sealed record Proof(
        long ExpiresUnix, string Nonce, string ClientVersion, int ProtocolVersion,
        string DataManifestId, string Platform, string Channel);

    public ClientVersionDecision Evaluate(ClientVersionHello hello)
    {
        if (hello is null || string.IsNullOrWhiteSpace(hello.BuildId) ||
            string.IsNullOrWhiteSpace(hello.Platform) || string.IsNullOrWhiteSpace(hello.Channel) ||
            string.IsNullOrWhiteSpace(hello.DataManifestId) ||
            hello.Capabilities is null || hello.Capabilities.Length > 64 ||
            hello.Capabilities.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 96) ||
            hello.BuildId.Length > 128 || hello.Platform.Length > 64 ||
            hello.Channel.Length > 32 || hello.DataManifestId.Length > 128)
            throw new ArgumentException("Invalid client version hello.", nameof(hello));
        Version clientVersion;
        try { clientVersion = ClientVersionPolicy.ParseVersion(hello.ClientVersion); }
        catch (FormatException) { throw new ArgumentException("Invalid client version.", nameof(hello)); }

        if (hello.ProtocolVersion != policy.ProtocolVersion)
            return Denied(ClientVersionStatus.ProtocolUnsupported, "protocol_unsupported");
        if (hello.Channel != policy.Channel || !SupportedPlatforms.Contains(hello.Platform) ||
            hello.DataManifestId != policy.RequiredDataManifestId ||
            clientVersion < policy.MinimumVersion)
            return Denied(ClientVersionStatus.UpdateRequired,
                hello.DataManifestId != policy.RequiredDataManifestId ? "data_manifest_not_supported" :
                !SupportedPlatforms.Contains(hello.Platform) ? "platform_not_supported" :
                hello.Channel != policy.Channel ? "channel_not_supported" : "client_version_not_supported");
        var recommended = clientVersion < policy.LatestVersion;
        return new ClientVersionDecision
        {
            Status = recommended ? ClientVersionStatus.UpdateRecommended : ClientVersionStatus.Supported,
            SessionAllowed = true,
            ServerProtocolVersion = policy.ProtocolVersion,
            LatestClientVersion = policy.LatestVersion.ToString(3),
            MessageKey = recommended ? "client_update_available" : "client_version_supported",
            HandshakeToken = IssueProof(hello)
        };
    }

    public bool ValidateProof(string? token)
    {
        if (!tokenOptions.IsConfigured || string.IsNullOrWhiteSpace(token))
            return false;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != "lnvh1" || parts[1].Length > 512)
            return false;
        try
        {
            var supplied = Decode(parts[2]);
            var expected = Sign($"{parts[0]}.{parts[1]}");
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
                return false;
            var proof = JsonSerializer.Deserialize<Proof>(Decode(parts[1]));
            if (proof is not { Nonce.Length: 32 } ||
                proof.ExpiresUnix <= clock.GetUtcNow().ToUnixTimeSeconds() ||
                string.IsNullOrWhiteSpace(proof.ClientVersion) ||
                proof.ProtocolVersion != policy.ProtocolVersion ||
                proof.DataManifestId != policy.RequiredDataManifestId ||
                proof.Channel != policy.Channel || !SupportedPlatforms.Contains(proof.Platform))
                return false;
            return ClientVersionPolicy.ParseVersion(proof.ClientVersion) >= policy.MinimumVersion;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return false;
        }
    }

    private ClientVersionDecision Denied(ClientVersionStatus status, string reason) => new()
    {
        Status = status,
        SessionAllowed = false,
        ServerProtocolVersion = policy.ProtocolVersion,
        LatestClientVersion = policy.LatestVersion.ToString(3),
        MinimumClientVersion = policy.MinimumVersion.ToString(3),
        RequiredDataManifestId = policy.RequiredDataManifestId,
        UpdateChannel = policy.Channel,
        UpdateReason = reason,
        MessageKey = status == ClientVersionStatus.ProtocolUnsupported
            ? "client_protocol_unsupported" : "client_update_required"
    };

    private string IssueProof(ClientVersionHello hello)
    {
        if (!tokenOptions.IsConfigured)
            throw new InvalidOperationException("Gateway signing key is not configured.");
        var proof = new Proof(clock.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            hello.ClientVersion, hello.ProtocolVersion, hello.DataManifestId,
            hello.Platform, hello.Channel);
        var unsigned = $"lnvh1.{Encode(JsonSerializer.SerializeToUtf8Bytes(proof))}";
        return $"{unsigned}.{Encode(Sign(unsigned))}";
    }

    private byte[] Sign(string value) => HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(tokenOptions.SigningKey!),
        Encoding.UTF8.GetBytes($"client-version-handshake:v1:{value}"));

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') +
                                 new string('=', (4 - value.Length % 4) % 4));
}
