using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record JoinTicketOptions(string? SigningKey, string KeyId, string Audience, TimeSpan Lifetime)
{
    public static JoinTicketOptions FromConfiguration(IConfiguration configuration)
    {
        var lifetimeMinutes = configuration.GetValue<int?>("Gateway:JoinTicketLifetimeMinutes") ?? 2;
        if (lifetimeMinutes is < 1 or > 10)
            throw new InvalidOperationException("Gateway:JoinTicketLifetimeMinutes must be between 1 and 10.");
        return new JoinTicketOptions(
            configuration["Gateway:JoinTicketSigningKey"],
            configuration["Gateway:JoinTicketKeyId"] ?? "gateway-join-01",
            configuration["Gateway:JoinTicketAudience"] ?? "game-server",
            TimeSpan.FromMinutes(lifetimeMinutes));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SigningKey) &&
                                Encoding.UTF8.GetByteCount(SigningKey) >= 32 &&
                                !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(Audience);
}

public sealed class JoinTicketCodec(JoinTicketOptions options)
{
    private const string Prefix = "lj1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Issue(JoinTicketClaims claims)
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("Gateway join-ticket signing is not configured.");
        JoinTicketClaimsValidator.Validate(claims, claims.IssuedAtUtc, options.Audience, TimeSpan.Zero);
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims, JsonOptions));
        var unsigned = $"{Prefix}.{payload}";
        return $"{unsigned}.{Sign(unsigned)}";
    }

    public JoinTicketValidationResult Validate(string ticket, DateTime nowUtc)
    {
        if (!options.IsConfigured)
            return new JoinTicketValidationResult(false, null, "join_ticket_signing_not_configured");
        var parts = ticket.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != Prefix)
            return new JoinTicketValidationResult(false, null, "join_ticket_format_invalid");
        var supplied = Decode(parts[2]);
        var expected = Decode(Sign($"{parts[0]}.{parts[1]}"));
        if (supplied is null || !CryptographicOperations.FixedTimeEquals(supplied, expected))
            return new JoinTicketValidationResult(false, null, "join_ticket_signature_invalid");
        try
        {
            var claims = JsonSerializer.Deserialize<JoinTicketClaims>(Base64UrlDecode(parts[1]), JsonOptions);
            if (claims is null || claims.KeyId != options.KeyId)
                return new JoinTicketValidationResult(false, null, "join_ticket_claims_invalid");
            JoinTicketClaimsValidator.Validate(claims, nowUtc, options.Audience, TimeSpan.FromSeconds(5));
            return new JoinTicketValidationResult(true, claims, "accepted");
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ProtocolViolationException)
        {
            return new JoinTicketValidationResult(false, null, "join_ticket_claims_invalid");
        }
    }

    private string Sign(string value) => Base64UrlEncode(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(options.SigningKey!), Encoding.UTF8.GetBytes(value)));
    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    private static byte[]? Decode(string value) { try { return Base64UrlDecode(value); } catch (FormatException) { return null; } }
}

public sealed record JoinTicketValidationResult(bool Accepted, JoinTicketClaims? Claims, string ReasonCode);
