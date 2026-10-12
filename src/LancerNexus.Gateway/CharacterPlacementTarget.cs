using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public static class CharacterPlacementTarget
{
    public static bool TryResolve(PlacementRequest request, CharacterRecord? character,
        out PlacementRequest resolved, out string reasonCode)
    {
        resolved = request;
        reasonCode = "accepted";
        if (request.CharacterId is not { } characterId)
            return true;
        if (character is null || character.CharacterId != characterId)
        {
            reasonCode = "character_not_found";
            return false;
        }
        var systemId = character.SystemId;
        if (!AdminQuery.ValidIdentifier(systemId))
        {
            reasonCode = "character_system_unknown";
            return false;
        }

        resolved = new PlacementRequest
        {
            RequestId = request.RequestId,
            SessionId = request.SessionId,
            CharacterId = request.CharacterId,
            TargetSystem = systemId!.ToLowerInvariant(),
            GroupId = request.GroupId,
            EventId = request.EventId,
            ClientBuild = request.ClientBuild,
            Region = request.Region,
            IdempotencyKey = request.IdempotencyKey
        };
        return true;
    }
}
