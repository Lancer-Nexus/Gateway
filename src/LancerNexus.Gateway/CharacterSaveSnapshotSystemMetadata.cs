using System.Text;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public enum CharacterSaveSystemMetadataParseStatus
{
    Found,
    Unknown,
    Invalid
}

public readonly record struct CharacterSaveSystemMetadataParseResult(
    CharacterSaveSystemMetadataParseStatus Status, string? SystemId = null);

/// <summary>Reads only the legacy Freelancer [Player] system field used for placement recovery.</summary>
public static class CharacterSaveSnapshotSystemMetadata
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CharacterSaveSystemMetadataParseResult Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > CharacterSaveSnapshotWriteRequestV1.MaxPayloadBytes)
            return new(CharacterSaveSystemMetadataParseStatus.Invalid);

        string text;
        try
        {
            text = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return new(CharacterSaveSystemMetadataParseStatus.Invalid);
        }

        var inPlayer = false;
        var playerSections = 0;
        var systemEntries = 0;
        string? systemId = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            if (line[0] == '[')
            {
                if (line[^1] != ']')
                    return new(CharacterSaveSystemMetadataParseStatus.Invalid);
                inPlayer = string.Equals(line[1..^1].Trim(), "player", StringComparison.OrdinalIgnoreCase);
                if (inPlayer && ++playerSections > 1)
                    return new(CharacterSaveSystemMetadataParseStatus.Invalid);
                continue;
            }

            if (!inPlayer)
                continue;
            var separator = line.IndexOf('=');
            if (separator < 0)
                continue;
            if (!string.Equals(line[..separator].Trim(), "system", StringComparison.OrdinalIgnoreCase))
                continue;

            if (++systemEntries > 1)
                return new(CharacterSaveSystemMetadataParseStatus.Invalid);
            var value = line[(separator + 1)..].Trim();
            if (!AdminQuery.ValidIdentifier(value))
                return new(CharacterSaveSystemMetadataParseStatus.Unknown);
            systemId = value.ToLowerInvariant();
        }

        if (playerSections != 1 || systemEntries != 1 || systemId is null)
            return new(CharacterSaveSystemMetadataParseStatus.Unknown);
        return new(CharacterSaveSystemMetadataParseStatus.Found, systemId);
    }
}
