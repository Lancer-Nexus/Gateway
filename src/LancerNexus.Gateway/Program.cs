using LancerNexus.Gateway;
using LancerNexus.Protocol;
using MySqlConnector;
using System.Net.Sockets;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var loginRateLimit = ReadPositiveLimit(builder.Configuration, "Gateway:LoginRateLimitPerMinute", 10);
var placementRateLimit = ReadPositiveLimit(builder.Configuration, "Gateway:PlacementRateLimitPerMinute", 30);
var transferRateLimit = ReadPositiveLimit(builder.Configuration, "Gateway:TransferRateLimitPerMinute", 10);
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
    options.AddPolicy("transfer", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = transferRateLimit,
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
builder.Services.AddSingleton(ClientVersionPolicy.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<ClientVersionHandshake>();
var joinTicketOptions = JoinTicketOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(joinTicketOptions);
builder.Services.AddSingleton<JoinTicketCodec>();
builder.Services.AddSingleton<JoinTicketReplayStore>();
builder.Services.AddSingleton<ITransferTicketReplayStore>(services => services.GetRequiredService<JoinTicketReplayStore>());
var transferTicketOptions = TransferTicketOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(transferTicketOptions);
builder.Services.AddSingleton<TransferTicketCodec>();
builder.Services.AddTransient<TransferInitiationService>();
builder.Services.AddTransient<TransferTicketAdmissionService>();
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
builder.Services.AddHttpClient<ICoordinatorTransferClient, CoordinatorTransferClient>();

var app = builder.Build();
await LogStartupDiagnosticsAsync(app, gatewayConnectionString, coordinatorOptions, sessionTokenOptions);
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
    capabilities = new[] { "health_v1", "protocol_v1", "auth_session_v1", "coordinator_placement_v1", "join_ticket_v1", "transfer_start_v1", "transfer_ticket_v1", "client_version_hello_v1" }
}));

app.MapPost("/api/v1/client/version", (ClientVersionHello hello, ClientVersionHandshake handshake) =>
{
    try { return Results.Ok(handshake.Evaluate(hello)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_client_version_hello" }); }
    catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
}).RequireRateLimiting("login");

app.MapPost("/api/v1/auth/login", async (
    LoginRequest request,
    ClientVersionHandshake handshake,
    GatewayAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    if (!handshake.ValidateProof(request.HandshakeToken))
        return Results.Json(new { error = "client_version_handshake_required" },
            statusCode: StatusCodes.Status428PreconditionRequired);
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
    {
        app.Logger.LogWarning("Game join ticket rejected: {ReasonCode}.", result.ReasonCode);
        return Results.Unauthorized();
    }
    var claims = result.Claims!;
    try
    {
        if (!await replayStore.TryConsumeAsync(claims.Nonce, claims.ExpiresAtUtc, cancellationToken))
        {
            app.Logger.LogWarning("Game join ticket replay rejected.");
            return Results.Unauthorized();
        }
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

app.MapPost("/api/v1/game/verify-transfer-ticket", async (
    TransferTicketVerificationRequest request,
    TransferTicketAdmissionService admission,
    CancellationToken cancellationToken) =>
{
    var result = await admission.VerifyAsync(request, cancellationToken);
    if (result.Accepted)
    {
        var claims = result.Claims!;
        return Results.Ok(new
        {
            guid = claims.AccountId,
            sessionId = claims.SessionId,
            transferId = claims.TransferId,
            characterId = claims.CharacterId,
            sourceInstanceId = claims.SourceInstanceId,
            instanceId = claims.TargetInstanceId,
            systemId = claims.TargetSystemId,
            leaseVersion = claims.LeaseVersion
        });
    }
    app.Logger.LogWarning("Game transfer ticket rejected: {ReasonCode}.", result.ReasonCode);
    return result.Failure switch
    {
        TransferTicketAdmissionFailure.CoordinatorUnavailable or TransferTicketAdmissionFailure.ReplayStoreUnavailable =>
            Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        TransferTicketAdmissionFailure.CoordinatorInvalidResponse =>
            Results.StatusCode(StatusCodes.Status502BadGateway),
        _ => Results.Unauthorized()
    };
}).RequireRateLimiting("transfer");

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

app.MapPost("/api/v1/transfers/start", async (
    TransferStartRequest request,
    HttpContext context,
    SessionTokenCodec sessionTokens,
    TransferInitiationService transfers,
    CancellationToken cancellationToken) =>
{
    var authorization = SessionAuthorization.AuthorizeToken(
        context.Request.Headers.Authorization, sessionTokens, DateTime.UtcNow);
    if (authorization.ConfigurationError)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!authorization.Accepted)
        return Results.Unauthorized();

    try
    {
        var result = await transfers.StartAsync(request, authorization.Claims!, cancellationToken);
        return result.Failure switch
        {
            TransferInitiationFailure.None => Results.Ok(result.Response),
            TransferInitiationFailure.InvalidRequest => Results.BadRequest(new { error = result.ReasonCode }),
            TransferInitiationFailure.SessionMismatch => Results.Unauthorized(),
            TransferInitiationFailure.ActiveLeaseUnavailable => Results.Conflict(new { error = result.ReasonCode }),
            TransferInitiationFailure.TicketSigningUnavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            TransferInitiationFailure.CoordinatorUnavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            TransferInitiationFailure.CoordinatorInvalidResponse => Results.StatusCode(StatusCodes.Status502BadGateway),
            TransferInitiationFailure.TargetRejected => Results.Conflict(new { error = result.ReasonCode }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
    catch (Exception exception) when (exception is MySqlException or SocketException or IOException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("transfer");

app.Run();

static int ReadPositiveLimit(IConfiguration configuration, string key, int defaultValue)
{
    var value = configuration.GetValue<int?>(key) ?? defaultValue;
    if (value <= 0)
        throw new InvalidOperationException($"Configuration value '{key}' must be positive.");
    return value;
}

static async Task LogStartupDiagnosticsAsync(
    WebApplication app,
    string? connectionString,
    CoordinatorGatewayOptions coordinatorOptions,
    SessionTokenOptions tokenOptions)
{
    var logger = app.Logger;
    var redisConfigured = !string.IsNullOrWhiteSpace(app.Configuration["Gateway:RedisEndpoint"]);
    logger.LogInformation(
        "Gateway configuration: MySQL {MySqlConfiguration}, Redis {RedisConfiguration}, Coordinator API {CoordinatorConfiguration}, token signing {TokenSigningConfiguration}",
        string.IsNullOrWhiteSpace(connectionString) ? "not configured" : "configured",
        redisConfigured ? "configured" : "not configured",
        coordinatorOptions.IsConfigured ? "configured" : "not configured",
        tokenOptions.IsConfigured ? "configured" : "not configured");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        logger.LogWarning("Gateway MySQL probe skipped: ConnectionStrings:Gateway is missing; readiness remains unhealthy.");
    }
    else
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            _ = await command.ExecuteScalarAsync(timeout.Token);
            logger.LogInformation("Gateway MySQL probe succeeded: connection opened and SELECT 1 returned.");
        }
        catch (Exception exception) when (exception is MySqlException or SocketException or IOException or OperationCanceledException)
        {
            logger.LogWarning(
                "Gateway MySQL probe failed ({FailureType}, MySQL error {MySqlErrorNumber}); readiness remains unhealthy.",
                exception.GetType().Name,
                exception is MySqlException mysqlException ? mysqlException.Number : 0);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Gateway MySQL probe failed with {FailureType}; connection details are omitted.",
                exception.GetType().Name);
        }
    }

    if (!coordinatorOptions.IsConfigured)
    {
        logger.LogWarning("Gateway Coordinator probe skipped: HTTPS endpoint or internal API key is not configured.");
        return;
    }

    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var response = await client.GetAsync(new Uri(coordinatorOptions.BaseAddress!, "health/ready"));
        if (response.IsSuccessStatusCode)
            logger.LogInformation("Gateway Coordinator probe succeeded: readiness endpoint returned HTTP {StatusCode}.", (int)response.StatusCode);
        else
            logger.LogWarning("Gateway Coordinator probe returned HTTP {StatusCode}; placement may be unavailable.", (int)response.StatusCode);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        logger.LogWarning("Gateway Coordinator probe failed ({FailureType}); placement may be unavailable.", exception.GetType().Name);
    }
}

public partial class Program;
public sealed record JoinTicketVerificationRequest(string Ticket);
