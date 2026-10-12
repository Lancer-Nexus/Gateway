using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class CharacterPlacementTargetTests
{
    [Fact]
    public void CharacterPlacementUsesStoredSystemInsteadOfClientTarget()
    {
        var request = Request(characterId: 42, targetSystem: "li01");
        var character = new CharacterRecord(42, "Pilot", DateTime.UnixEpoch, "Li03");

        var accepted = CharacterPlacementTarget.TryResolve(request, character, out var resolved, out var reason);

        Assert.True(accepted);
        Assert.Equal("accepted", reason);
        Assert.Equal("li03", resolved.TargetSystem);
        Assert.Equal(request.RequestId, resolved.RequestId);
        Assert.Equal(request.SessionId, resolved.SessionId);
        Assert.Equal(request.CharacterId, resolved.CharacterId);
        Assert.Equal(request.IdempotencyKey, resolved.IdempotencyKey);
    }

    [Fact]
    public void CharacterPlacementFailsClosedWhenSystemMetadataIsMissingOrInvalid()
    {
        foreach (var systemId in new string?[] { null, "", "not a system" })
        {
            var accepted = CharacterPlacementTarget.TryResolve(Request(42, "li01"),
                new CharacterRecord(42, "Pilot", DateTime.UnixEpoch, systemId), out _, out var reason);

            Assert.False(accepted);
            Assert.Equal("character_system_unknown", reason);
        }
    }

    [Fact]
    public void CharacterPlacementRejectsMissingOrMismatchedOwnedCharacter()
    {
        Assert.False(CharacterPlacementTarget.TryResolve(Request(42, "li01"), null, out _, out var missing));
        Assert.Equal("character_not_found", missing);
        Assert.False(CharacterPlacementTarget.TryResolve(Request(42, "li01"),
            new CharacterRecord(43, "Other", DateTime.UnixEpoch, "li03"), out _, out var mismatched));
        Assert.Equal("character_not_found", mismatched);
    }

    [Fact]
    public void PlacementWithoutCharacterKeepsRequestedSystem()
    {
        var request = Request(null, "li01");

        var accepted = CharacterPlacementTarget.TryResolve(request, null, out var resolved, out var reason);

        Assert.True(accepted);
        Assert.Equal("accepted", reason);
        Assert.Same(request, resolved);
        Assert.Equal("li01", resolved.TargetSystem);
    }

    private static PlacementRequest Request(long? characterId, string targetSystem) => new()
    {
        RequestId = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        CharacterId = characterId,
        TargetSystem = targetSystem,
        ClientBuild = "tests",
        Region = "eu",
        IdempotencyKey = Guid.NewGuid().ToString("N")
    };
}
