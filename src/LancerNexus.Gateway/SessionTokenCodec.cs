using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record SessionTokenOptions(
    string? SigningKey,
    string KeyId,
    string Audience,
    TimeSpan Lifetime)
{
    public static SessionTokenOptions FromConfiguration(IConfiguration configuration)
    {
        var lifetimeMinutes = configuration.GetValue<int?>("Gateway:AccessTokenLifetimeMinutes") ?? 10;
        if (lifetimeMinutes <= 0 || lifetimeMinutes > 60)
            throw new InvalidOperationException("Gateway:AccessTokenLifetimeMinutes must be between 1 and 60.");
        return new SessionTokenOptions(
            configuration["Gateway:AccessTokenSigningKey"],
            configuration["Gateway:AccessTokenKeyId"] ?? "gateway-key-01",
            configuration["Gateway:AccessTokenAudience"] ?? "game-server",
            TimeSpan.FromMinutes(lifetimeMinutes));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SigningKey) &&
                                Encoding.UTF8.GetByteCount(SigningKey) >= 32 &&
                                !string.IsNullOrWhiteSpace(KeyId) &&
                                !string.IsNullOrWhiteSpace(Audience);
}

public sealed record SessionTokenValidationResult(
    bool Accepted,
    SessionTokenClaims? Claims,
    string ReasonCode);

public sealed class SessionTokenCodec(SessionTokenOptions options)
{
    private const string Prefix = "ln1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Issue(SessionTokenClaims claims)
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("Gateway access-token signing is not configured.");
        if (!string.Equals(claims.KeyId, options.KeyId, StringComparison.Ordinal))
            throw new ArgumentException("Token claims use an unknown key identifier.", nameof(claims));
        SessionTokenClaimsValidator.Validate(
            claims,
            claims.IssuedAtUtc,
            options.Audience,
            TimeSpan.Zero);

        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims, JsonOptions));
        var unsigned = $"{Prefix}.{payload}";
        var signature = Sign(unsigned);
        return $"{unsigned}.{signature}";
    }

    public SessionTokenValidationResult Validate(string token, DateTime nowUtc)
    {
        if (!options.IsConfigured)
            return new SessionTokenValidationResult(false, null, "token_signing_not_configured");
        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return new SessionTokenValidationResult(false, null, "token_format_invalid");

        var unsigned = $"{parts[0]}.{parts[1]}";
        var expectedSignature = Sign(unsigned);
        var suppliedSignature = DecodeSignature(parts[2]);
        var expectedBytes = Base64UrlDecode(expectedSignature);
        if (suppliedSignature is null ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedSignature))
            return new SessionTokenValidationResult(false, null, "token_signature_invalid");

        try
        {
            var claims = JsonSerializer.Deserialize<SessionTokenClaims>(
                Base64UrlDecode(parts[1]), JsonOptions);
            if (claims is null)
                return new SessionTokenValidationResult(false, null, "token_payload_invalid");
            if (!string.Equals(claims.KeyId, options.KeyId, StringComparison.Ordinal))
                return new SessionTokenValidationResult(false, null, "token_key_id_invalid");
            SessionTokenClaimsValidator.Validate(claims, nowUtc, options.Audience, TimeSpan.FromSeconds(5));
            return new SessionTokenValidationResult(true, claims, "accepted");
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ProtocolViolationException)
        {
            return new SessionTokenValidationResult(false, null, "token_claims_invalid");
        }
    }

    private string Sign(string value)
    {
        var key = Encoding.UTF8.GetBytes(options.SigningKey!);
        return Base64UrlEncode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') +
                                 new string('=', (4 - value.Length % 4) % 4));

    private static byte[]? DecodeSignature(string value)
    {
        try { return Base64UrlDecode(value); }
        catch (FormatException) { return null; }
    }
}
