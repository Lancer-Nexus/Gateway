using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record TransferTicketOptions(string? SigningKey, string KeyId, string Audience)
{
    public static TransferTicketOptions FromConfiguration(IConfiguration configuration) => new(
        configuration["Gateway:TransferTicketSigningKey"],
        configuration["Gateway:TransferTicketKeyId"] ?? "gateway-transfer-01",
        configuration["Gateway:TransferTicketAudience"] ?? "game-server-transfer");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SigningKey) &&
                                Encoding.UTF8.GetByteCount(SigningKey) >= 32 &&
                                !string.IsNullOrWhiteSpace(KeyId) &&
                                !string.IsNullOrWhiteSpace(Audience);
}

public sealed record TransferTicketValidationResult(
    bool Accepted,
    TransferTicketClaims? Claims,
    string ReasonCode);

public sealed class TransferTicketCodec(TransferTicketOptions options)
{
    private const string Prefix = "lt1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(5);

    public string Issue(TransferTicketClaims claims)
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("Gateway transfer-ticket signing is not configured.");
        ValidateClaims(claims, claims.IssuedAtUtc, TimeSpan.Zero);
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims, JsonOptions));
        var unsigned = $"{Prefix}.{payload}";
        return $"{unsigned}.{Sign(unsigned)}";
    }

    public TransferTicketValidationResult Validate(string ticket, DateTime nowUtc)
    {
        if (!options.IsConfigured)
            return new TransferTicketValidationResult(false, null, "transfer_ticket_signing_not_configured");
        var parts = ticket.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != Prefix)
            return new TransferTicketValidationResult(false, null, "transfer_ticket_format_invalid");
        var supplied = Decode(parts[2]);
        var expected = Decode(Sign($"{parts[0]}.{parts[1]}"));
        if (supplied is null || !CryptographicOperations.FixedTimeEquals(supplied, expected))
            return new TransferTicketValidationResult(false, null, "transfer_ticket_signature_invalid");

        try
        {
            var claims = JsonSerializer.Deserialize<TransferTicketClaims>(Base64UrlDecode(parts[1]), JsonOptions);
            if (claims is null || !string.Equals(claims.KeyId, options.KeyId, StringComparison.Ordinal))
                return new TransferTicketValidationResult(false, null, "transfer_ticket_claims_invalid");
            ValidateClaims(claims, nowUtc, ClockSkew);
            return new TransferTicketValidationResult(true, claims, "accepted");
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return new TransferTicketValidationResult(false, null, "transfer_ticket_claims_invalid");
        }
    }

    private void ValidateClaims(TransferTicketClaims claims, DateTime nowUtc, TimeSpan allowedClockSkew)
    {
        if (claims.TransferId == Guid.Empty || claims.SessionId == Guid.Empty || claims.AccountId == Guid.Empty ||
            claims.CharacterId <= 0 || claims.LeaseVersion < 0 ||
            string.IsNullOrWhiteSpace(claims.SourceInstanceId) ||
            string.IsNullOrWhiteSpace(claims.TargetInstanceId) ||
            string.Equals(claims.SourceInstanceId, claims.TargetInstanceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(claims.TargetSystemId) || string.IsNullOrWhiteSpace(claims.Nonce) ||
            !string.Equals(claims.Audience, options.Audience, StringComparison.Ordinal) ||
            !string.Equals(claims.KeyId, options.KeyId, StringComparison.Ordinal) ||
            claims.ExpiresAtUtc <= claims.IssuedAtUtc ||
            claims.ExpiresAtUtc - claims.IssuedAtUtc > MaximumLifetime ||
            claims.IssuedAtUtc > nowUtc + allowedClockSkew ||
            claims.ExpiresAtUtc < nowUtc - allowedClockSkew)
            throw new ArgumentException("Transfer ticket claims are invalid.", nameof(claims));
    }

    private string Sign(string value) => Base64UrlEncode(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(options.SigningKey!), Encoding.UTF8.GetBytes(value)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') +
                                 new string('=', (4 - value.Length % 4) % 4));

    private static byte[]? Decode(string value)
    {
        try { return Base64UrlDecode(value); }
        catch (FormatException) { return null; }
    }
}
