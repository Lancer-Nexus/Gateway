using LancerNexus.Gateway;
using System.Net.Sockets;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var loginRateLimit = ReadPositiveLimit(builder.Configuration, "Gateway:LoginRateLimitPerMinute", 10);
var placementRateLimit = ReadPositiveLimit(builder.Configuration, "Gateway:PlacementRateLimitPerMinute", 30);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = loginRateLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("placement", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = placementRateLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
var coordinatorOptions = CoordinatorGatewayOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(coordinatorOptions);
var sessionTokenOptions = SessionTokenOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(sessionTokenOptions);
builder.Services.AddSingleton<SessionTokenCodec>();
var joinTicketOptions = JoinTicketOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(joinTicketOptions);
builder.Services.AddSingleton<JoinTicketCodec>();
builder.Services.AddSingleton<JoinTicketReplayStore>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<GatewayReadinessHealthCheck>();
builder.Services.AddHealthChecks()
    .AddCheck<GatewayReadinessHealthCheck>("gateway_dependencies");
builder.Services.AddSingleton<IPasswordVerifier, BcryptPasswordVerifier>();
var gatewayConnectionString = builder.Configuration.GetConnectionString("Gateway");
builder.Services.AddSingleton<IAccountRepository>(_ =>
    string.IsNullOrWhiteSpace(gatewayConnectionString)
        ? new AccountRepositoryNotConfigured()
        : new MySqlAccountRepository(gatewayConnectionString));
builder.Services.AddSingleton<GatewayAuthenticationService>();
builder.Services.AddHttpClient<CoordinatorPlacementClient>();

var app = builder.Build();
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});

app.MapHealthChecks("/health/ready");

app.MapGet("/api/v1/capabilities", () => Results.Ok(new
{
    service = "gateway",
    protocolVersion = LancerNexus.Protocol.ProtocolConstants.ProtocolVersion,
    capabilities = new[] { "health_v1", "protocol_v1", "auth_session_v1", "coordinator_placement_v1", "join_ticket_v1" }
}));

app.MapPost("/api/v1/auth/login", async (
    LoginRequest request,
    GatewayAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    var result = await authentication.LoginAsync(request, cancellationToken);
    return result.Failure switch
    {
        LoginFailure.None => Results.Ok(result.Response),
        LoginFailure.InvalidCredentials => Results.Unauthorized(),
        _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
    };
}).RequireRateLimiting("login");

app.MapPost("/api/v1/auth/refresh", async (
    RefreshRequest request,
    GatewayAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    var result = await authentication.RefreshAsync(request, cancellationToken);
    return result.Failure switch
    {
        LoginFailure.None => Results.Ok(result.Response),
        LoginFailure.InvalidRefreshToken => Results.Unauthorized(),
        _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
    };
});

app.MapGet("/api/v1/me", (HttpContext context, SessionTokenCodec sessionTokens) =>
{
    var authorization = SessionAuthorization.AuthorizeToken(
        context.Request.Headers.Authorization,
        sessionTokens,
        DateTime.UtcNow);
    if (authorization.ConfigurationError)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!authorization.Accepted)
        return Results.Unauthorized();
    var claims = authorization.Claims!;
    return Results.Ok(new
    {
        accountId = claims.AccountId,
        sessionId = claims.SessionId,
        audience = claims.Audience,
        instanceId = claims.InstanceId,
        issuedAtUtc = claims.IssuedAtUtc,
        expiresAtUtc = claims.ExpiresAtUtc
    });
});

app.MapGet("/api/v1/characters", async (
    HttpContext context,
    SessionTokenCodec sessionTokens,
    IAccountRepository accounts,
    CancellationToken cancellationToken) =>
{
    var authorization = SessionAuthorization.AuthorizeToken(
        context.Request.Headers.Authorization,
        sessionTokens,
        DateTime.UtcNow);
    if (authorization.ConfigurationError)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!authorization.Accepted)
        return Results.Unauthorized();
    try
    {
        var characters = await accounts.ListCharactersAsync(authorization.Claims!.AccountId, cancellationToken);
        return Results.Ok(characters);
    }
    catch (InvalidOperationException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/v1/game/verify-ticket", async (
    JoinTicketVerificationRequest request,
    JoinTicketCodec joinTickets,
    JoinTicketReplayStore replayStore,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Ticket))
        return Results.Unauthorized();
    var result = joinTickets.Validate(request.Ticket, DateTime.UtcNow);
    if (!result.Accepted)
        return Results.Unauthorized();
    var claims = result.Claims!;
    try
    {
        if (!await replayStore.TryConsumeAsync(claims.Nonce, claims.ExpiresAtUtc, cancellationToken))
            return Results.Unauthorized();
    }
    catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Ok(new
    {
        guid = claims.AccountId,
        sessionId = claims.SessionId,
        characterId = claims.CharacterId,
        instanceId = claims.InstanceId,
        systemId = claims.SystemId
    });
});

async Task<IResult> HandlePlacement(
    LancerNexus.Protocol.PlacementRequest request,
    HttpContext context,
    LancerNexus.Gateway.CoordinatorPlacementClient coordinator,
    LancerNexus.Gateway.SessionTokenCodec sessionTokens,
    LancerNexus.Gateway.JoinTicketCodec joinTickets,
    LancerNexus.Gateway.IAccountRepository accounts,
    CancellationToken cancellationToken)
{
    var authorization = SessionAuthorization.Authorize(
        context.Request.Headers.Authorization,
        request,
        sessionTokens,
        DateTime.UtcNow);
    if (authorization.ConfigurationError)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!authorization.Accepted)
        return Results.Unauthorized();
    if (request.RequestId == Guid.Empty || request.SessionId == Guid.Empty ||
        string.IsNullOrWhiteSpace(request.TargetSystem) ||
        string.IsNullOrWhiteSpace(request.IdempotencyKey))
        return Results.BadRequest(new { error = "invalid_placement_request" });
    if (request.CharacterId is { } characterId)
    {
        try
        {
            if (await accounts.FindCharacterAsync(authorization.Claims!.AccountId, characterId, cancellationToken) is null)
                return Results.NotFound(new { error = "character_not_found" });
        }
        catch (InvalidOperationException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    var result = await coordinator.PlaceAsync(request, cancellationToken);
    if (!result.IsAvailable)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (result.Envelope is null)
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    if (!result.Envelope.Decision.Accepted)
        return Results.Conflict(result.Envelope);
    if (string.IsNullOrWhiteSpace(result.Envelope.Decision.InstanceId) ||
        string.IsNullOrWhiteSpace(result.Envelope.Decision.SystemId) ||
        string.IsNullOrWhiteSpace(result.Envelope.Decision.Endpoint))
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    try
    {
        var now = DateTime.UtcNow;
        var expires = result.Envelope.Decision.ExpiresUtc;
        var ticket = joinTickets.Issue(new LancerNexus.Protocol.JoinTicketClaims
        {
            SessionId = request.SessionId,
            AccountId = authorization.Claims!.AccountId,
            CharacterId = request.CharacterId,
            InstanceId = result.Envelope.Decision.InstanceId,
            SystemId = result.Envelope.Decision.SystemId,
            Endpoint = result.Envelope.Decision.Endpoint,
            IssuedAtUtc = now,
            ExpiresAtUtc = expires,
            Nonce = Guid.NewGuid().ToString("N"),
            Audience = joinTicketOptions.Audience,
            KeyId = joinTicketOptions.KeyId
        });
        var decision = result.Envelope.Decision;
        var response = new CoordinatorPlacementEnvelope(new LancerNexus.Protocol.PlacementDecision
        {
            RequestId = decision.RequestId,
            Accepted = decision.Accepted,
            InstanceId = decision.InstanceId,
            SystemId = decision.SystemId,
            Endpoint = decision.Endpoint,
            ReasonCode = decision.ReasonCode,
            ExpiresUtc = decision.ExpiresUtc,
            JoinTicket = ticket
        }, result.Envelope.Duplicate);
        return Results.Ok(response);
    }
    catch (InvalidOperationException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}

app.MapPost("/api/v1/placement/request", HandlePlacement).RequireRateLimiting("placement");
app.MapPost("/api/v1/placement", HandlePlacement).RequireRateLimiting("placement");

app.Run();

static int ReadPositiveLimit(IConfiguration configuration, string key, int defaultValue)
{
    var value = configuration.GetValue<int?>(key) ?? defaultValue;
    if (value <= 0)
        throw new InvalidOperationException($"Configuration value '{key}' must be positive.");
    return value;
}

public partial class Program;

public sealed record JoinTicketVerificationRequest(string Ticket);
