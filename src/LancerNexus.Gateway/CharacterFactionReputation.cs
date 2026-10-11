using System.Globalization;
using System.Text;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record CharacterFactionReputationV1(string FactionId, float Value);

public enum CharacterFactionReputationReadStatus
{
    Found,
    RevisionMismatch,
    InvalidRequest,
    SessionRejected,
    NotFound,
    Unavailable
}

public sealed record CharacterFactionReputationReadResult(
    CharacterFactionReputationReadStatus Status,
    long? ReputationRevision = null,
    IReadOnlyList<CharacterFactionReputationV1>? Factions = null);

/// <summary>Reads Freelancer [Player] house entries from a Gateway-owned SaveGame snapshot.</summary>
public static class SaveGameFactionReputationParser
{
    private const int MaximumFactionCount = 256;

    public static bool TryParse(ReadOnlySpan<byte> payload, out CharacterFactionReputationV1[] reputations)
    {
        reputations = [];
        if (payload.IsEmpty || payload.Length > CharacterSaveSnapshotWriteRequestV1.MaxPayloadBytes)
            return false;

        var text = Encoding.Latin1.GetString(payload);
        var inPlayerSection = false;
        var foundPlayerSection = false;
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
            {
                inPlayerSection = string.Equals(line[1..^1], "Player", StringComparison.OrdinalIgnoreCase);
                foundPlayerSection |= inPlayerSection;
                continue;
            }

            if (!inPlayerSection)
                continue;
            var equals = line.IndexOf('=');
            if (equals < 0 || !string.Equals(line[..equals].Trim(), "house", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line[(equals + 1)..].Trim();
            var separator = value.LastIndexOf(',');
            if (separator <= 0 || separator == value.Length - 1)
                return false;
            var rawReputation = value[..separator].Trim();
            var factionId = value[(separator + 1)..].Trim().ToLowerInvariant();
            if (!MarketContractValidation.ValidId(factionId) || factionId.Length > 64 ||
                !float.TryParse(rawReputation, NumberStyles.Float, CultureInfo.InvariantCulture, out var reputation) ||
                !float.IsFinite(reputation) || reputation is < -1f or > 1f ||
                !values.TryAdd(factionId, reputation) || values.Count > MaximumFactionCount)
                return false;
        }

        if (!foundPlayerSection)
            return false;
        reputations = values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new CharacterFactionReputationV1(pair.Key, pair.Value)).ToArray();
        return true;
    }
}
