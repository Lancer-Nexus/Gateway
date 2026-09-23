using System.Net.Http.Headers;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public sealed record SessionAuthorizationResult(
    bool Accepted,
    bool ConfigurationError,
    SessionTokenClaims? Claims,
    string ReasonCode);

public static class SessionAuthorization
{
    public static SessionAuthorizationResult AuthorizeToken(
        string? authorizationHeader,
        SessionTokenCodec codec,
        DateTime nowUtc)
    {
        if (!AuthenticationHeaderValue.TryParse(authorizationHeader, out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            return new SessionAuthorizationResult(false, false, null, "authorization_required");

        var validation = codec.Validate(authorization.Parameter, nowUtc);
        if (validation.ReasonCode == "token_signing_not_configured")
            return new SessionAuthorizationResult(false, true, null, validation.ReasonCode);
        return validation.Accepted
            ? new SessionAuthorizationResult(true, false, validation.Claims, "accepted")
            : new SessionAuthorizationResult(false, false, null, validation.ReasonCode);
    }

    public static SessionAuthorizationResult Authorize(
        string? authorizationHeader,
        PlacementRequest request,
        SessionTokenCodec codec,
        DateTime nowUtc)
    {
        var authorization = AuthorizeToken(authorizationHeader, codec, nowUtc);
        if (!authorization.Accepted)
            return authorization;
        if (authorization.Claims!.SessionId != request.SessionId)
            return new SessionAuthorizationResult(false, false, null, "session_id_mismatch");

        return authorization;
    }
}
