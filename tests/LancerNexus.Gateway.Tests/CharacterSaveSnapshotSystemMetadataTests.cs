using System.Text;
using LancerNexus.Gateway;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class CharacterSaveSnapshotSystemMetadataTests
{
    [Theory]
    [InlineData("[Player]\nsystem = Li03\n", "li03")]
    [InlineData("\uFEFF[Player]\nSystem=BR01\n[Ship]\nsystem = ignored\n", "br01")]
    public void ParseReadsPlayerSystemCaseInsensitivelyAndNormalizes(string save, string expected)
    {
        var parsed = CharacterSaveSnapshotSystemMetadata.Parse(Encoding.UTF8.GetBytes(save));

        Assert.Equal(CharacterSaveSystemMetadataParseStatus.Found, parsed.Status);
        Assert.Equal(expected, parsed.SystemId);
    }

    [Theory]
    [InlineData("[Player]\n", CharacterSaveSystemMetadataParseStatus.Unknown)]
    [InlineData("[Ship]\nsystem=li03\n", CharacterSaveSystemMetadataParseStatus.Unknown)]
    [InlineData("[Player]\nsystem=bad system\n", CharacterSaveSystemMetadataParseStatus.Unknown)]
    [InlineData("[Player]\nsystem=li03\n[Player]\nsystem=li03\n", CharacterSaveSystemMetadataParseStatus.Invalid)]
    [InlineData("[Player]\nsystem=li03\nsystem=li04\n", CharacterSaveSystemMetadataParseStatus.Invalid)]
    [InlineData("[Player\nsystem=li03\n", CharacterSaveSystemMetadataParseStatus.Invalid)]
    public void ParseFailsClosedWhenPlayerSystemIsMissingAmbiguousOrMalformed(string save,
        CharacterSaveSystemMetadataParseStatus expected)
    {
        var parsed = CharacterSaveSnapshotSystemMetadata.Parse(Encoding.UTF8.GetBytes(save));

        Assert.Equal(expected, parsed.Status);
        Assert.Null(parsed.SystemId);
    }

    [Fact]
    public void ParseRejectsInvalidUtf8AndEmptyPayload()
    {
        Assert.Equal(CharacterSaveSystemMetadataParseStatus.Invalid,
            CharacterSaveSnapshotSystemMetadata.Parse([0xFF]).Status);
        Assert.Equal(CharacterSaveSystemMetadataParseStatus.Invalid,
            CharacterSaveSnapshotSystemMetadata.Parse([]).Status);
    }
}
