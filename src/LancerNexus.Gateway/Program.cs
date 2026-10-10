using LancerNexus.Gateway;
using LancerNexus.Protocol;
using MySqlConnector;
using System.Globalization;
using System.Net.Sockets;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var loginRateLimit = ReadPositiveLimit(builder.Configuration, "Gateway:LoginRateLimitPerMinute", 10);
var placementRateLimit = ReadPositiveLimit(builder.Configuration, "Gateway:PlacementRateLimitPerMinute", 30);
var eventRegistrationRateLimit = ReadPositiveLimit(builder.Configuration,
    "Gateway:EventRegistrationRateLimitPerMinute", 10);
var transferRateLimit = ReadPositiveLimit(builder.Configuration, "Gateway:TransferRateLimitPerMinute", 10);
var transferStatusRateLimit = ReadPositiveLimit(builder.Configuration, "Gateway:TransferStatusRateLimitPerMinute", 120);
var characterSnapshotRateLimit = ReadPositiveLimit(builder.Configuration, "Gateway:CharacterSnapshotRateLimitPerMinute", 600);
var sessionRefreshRateLimit = ReadPositiveLimit(builder.Configuration, "Gateway:SessionRefreshRateLimitPerMinute", 120);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("admin", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = loginRateLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("session-refresh", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = sessionRefreshRateLimit,
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
    options.AddPolicy("event-registration", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = eventRegistrationRateLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("transfer", context => RateLimitPartition.GetFixedWindowLimiter(
        TransferRateLimitPartition.GetKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = transferRateLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("transfer-status", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = transferStatusRateLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("character-snapshot", context => RateLimitPartition.GetFixedWindowLimiter(
        TransferRateLimitPartition.GetKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = characterSnapshotRateLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
var coordinatorOptions = CoordinatorGatewayOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(coordinatorOptions);
var sessionTokenOptions = SessionTokenOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(sessionTokenOptions);
builder.Services.AddSingleton(AuthenticationSessionOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<SessionTokenCodec>();
builder.Services.AddSingleton(ClientVersionPolicy.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<ClientVersionHandshake>();
var joinTicketOptions = JoinTicketOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(joinTicketOptions);
builder.Services.AddSingleton<JoinTicketCodec>();
builder.Services.AddSingleton<JoinTicketReplayStore>();
builder.Services.AddTransient<CharacterLeaseAcquisitionService>();
builder.Services.AddTransient<CharacterLeaseReleaseService>();
builder.Services.AddTransient<CharacterLeaseRenewalService>();
builder.Services.AddSingleton<ITransferTicketReplayStore>(services => services.GetRequiredService<JoinTicketReplayStore>());
var transferTicketOptions = TransferTicketOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(transferTicketOptions);
builder.Services.AddSingleton<TransferTicketCodec>();
builder.Services.AddTransient<TransferInitiationService>();
builder.Services.AddTransient<TransferTicketAdmissionService>();
builder.Services.AddSingleton<GameInstanceKeyAuthenticator>();
builder.Services.AddHttpClient<AdminQueryRelay>(http => http.Timeout = TimeSpan.FromSeconds(8))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<AdminActionRelay>(http => http.Timeout = TimeSpan.FromSeconds(8))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<AdminPermissionRelay>(http => http.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<AdminPermissionSnapshotRelay>(http => http.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<GatewayEventRegistrationRelay>(http => http.Timeout = TimeSpan.FromSeconds(8))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddTransient<TransferAcceptanceService>();
builder.Services.AddTransient<TransferSourceFreezeService>();
builder.Services.AddTransient<TransferSnapshotReadService>();
builder.Services.AddTransient<TransferSourceReleaseService>();
builder.Services.AddTransient<TransferStatusService>();
builder.Services.AddTransient<NpcMissionAuthorityService>();
builder.Services.AddSingleton<NpcMissionAuthorityAuthenticator>();
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
builder.Services.AddSingleton<ICharacterLeaseAcquisitionStore>(services =>
    (ICharacterLeaseAcquisitionStore)services.GetRequiredService<IAccountRepository>());
builder.Services.AddSingleton<ICharacterSaveSnapshotStore>(services =>
    string.IsNullOrWhiteSpace(gatewayConnectionString)
        ? new CharacterSaveSnapshotStoreNotConfigured()
        : new MySqlCharacterSaveSnapshotStore(gatewayConnectionString,
            services.GetRequiredService<TransferSnapshotProtection>()));
builder.Services.AddTransient<CharacterSaveSnapshotService>();
if (!string.IsNullOrWhiteSpace(gatewayConnectionString))
    builder.Services.AddHostedService<ExpiredSessionCleanupService>();
builder.Services.AddSingleton<TransferSnapshotProtection>();
builder.Services.AddSingleton<ITransferSnapshotStore>(services =>
    string.IsNullOrWhiteSpace(gatewayConnectionString)
        ? new TransferSnapshotStoreNotConfigured()
        : new MySqlTransferSnapshotStore(gatewayConnectionString,
            services.GetRequiredService<TransferSnapshotProtection>(),
            services.GetRequiredService<TimeProvider>()));
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
    capabilities = new[] { "health_v1", "protocol_v1", "auth_session_v1", "coordinator_placement_v1", "join_ticket_v1", "character_identity_resolve_v1", "character_lease_acquire_v1", "character_lease_renew_v1", "character_save_snapshot_v1", "transfer_start_v1", "transfer_ticket_v1", "transfer_source_freeze_v1", "transfer_snapshot_v1", "transfer_accept_v1", "transfer_release_v1", "event_registration_v1", "client_version_hello_v1" }
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
}).RequireRateLimiting("session-refresh");

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

app.MapPost("/api/v1/events/{eventId}/registrations", async (string eventId,
    GatewayEventRegistrationSubmission request, SessionTokenCodec sessionTokens,
    IAccountRepository accounts, GatewayEventRegistrationRelay relay, HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var authorization = SessionAuthorization.AuthorizeToken(context.Request.Headers.Authorization,
        sessionTokens, DateTime.UtcNow);
    if (authorization.ConfigurationError)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!authorization.Accepted)
        return Results.Unauthorized();
    if (!AdminQuery.ValidIdentifier(eventId))
        return Results.BadRequest(new { error = "invalid_event_id" });

    var claims = authorization.Claims!;
    try
    {
        if (await accounts.FindActiveSessionAsync(claims.SessionId, claims.AccountId,
                DateTime.UtcNow, cancellationToken) is null)
            return Results.Unauthorized();
        var decision = await relay.FromRestAsync(claims.AccountId, claims.SessionId,
            eventId, request, cancellationToken);
        return Results.Json(decision,
            statusCode: (int)GatewayEventRegistrationRelay.ExpectedStatusCode(decision.Status));
    }
    catch (Exception error) when (error is MySqlException or InvalidOperationException or
                                  OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("event-registration");

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

app.MapPost("/api/v1/game/acquire-character-lease", async (
    CharacterLeaseAcquireRequestV1 request,
    GameInstanceKeyAuthenticator instances,
    CharacterLeaseAcquisitionService acquisition,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!request.IsValid())
        return Results.BadRequest(new { error = "invalid_character_lease_request" });
    if (!instances.TryAuthenticate(context.Request, out var instanceId))
        return Results.Unauthorized();

    var result = await acquisition.AcquireAsync(request, instanceId, cancellationToken);
    if (!result.IsValid())
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    if (result.Accepted)
        return Results.Ok(result);
    return result.ReasonCode switch
    {
        "join_ticket_invalid" or "join_ticket_instance_mismatch" => Results.Unauthorized(),
        "lease_owned" => Results.Conflict(result),
        "character_lease_persistence_unavailable" or "lease_lifetime_configuration_invalid" =>
            Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        _ => Results.BadRequest(result)
    };
});

app.MapPost("/api/v1/game/release-character-lease", async (
    CharacterLeaseReleaseRequestV1 request,
    GameInstanceKeyAuthenticator instances,
    CharacterLeaseReleaseService release,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!request.IsValid()) return Results.BadRequest(new { error = "invalid_character_lease_release_request" });
    if (!instances.TryAuthenticate(context.Request, out var instanceId)) return Results.Unauthorized();

    var result = await release.ReleaseAsync(request, instanceId, cancellationToken);
    if (!result.IsValid()) return Results.StatusCode(StatusCodes.Status502BadGateway);
    if (result.Released) return Results.Ok(result);
    return result.ReasonCode switch
    {
        "lease_persistence_unavailable" => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        "lease_mismatch" or "lease_not_found" => Results.Conflict(result),
        _ => Results.BadRequest(result)
    };
});

app.MapPost("/api/v1/game/renew-character-lease", async (
    CharacterLeaseRenewRequestV1 request,
    GameInstanceKeyAuthenticator instances,
    CharacterLeaseRenewalService renewal,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!request.IsValid()) return Results.BadRequest(new { error = "invalid_character_lease_renewal" });
    if (!instances.TryAuthenticate(context.Request, out var instanceId)) return Results.Unauthorized();
    var result = await renewal.RenewAsync(request, instanceId, cancellationToken);
    if (!result.IsValid()) return Results.StatusCode(StatusCodes.Status502BadGateway);
    if (result.Accepted) return Results.Ok(result);
    return result.ReasonCode == "character_lease_persistence_unavailable"
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Conflict(result);
}).RequireRateLimiting("character-snapshot");

app.MapPost("/api/v1/game/characters/list", async (
    CharacterRosterRequestV1 request,
    GameInstanceKeyAuthenticator instances,
    IAccountRepository accounts,
    HttpContext context,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!request.IsValid()) return Results.BadRequest(new { error = "invalid_character_roster_request" });
    if (!instances.TryAuthenticate(context.Request, out _)) return Results.Unauthorized();
    try
    {
        var result = await accounts.ListActiveCharactersForSessionAsync(request.AccountId, request.SessionId,
            timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        if (!result.SessionAccepted) return Results.Unauthorized();
        var response = new CharacterRosterResultV1
        {
            CorrelationId = request.CorrelationId,
            Accepted = true,
            ReasonCode = "listed",
            Characters = result.Characters.Select(character => new CharacterRosterEntryV1
            {
                CharacterId = character.CharacterId,
                DisplayName = character.DisplayName
            }).ToArray()
        };
        return response.IsValid() ? Results.Ok(response) : Results.StatusCode(StatusCodes.Status502BadGateway);
    }
    catch (Exception error) when (error is MySqlException or InvalidOperationException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("character-snapshot");

app.MapPost("/api/v1/game/characters/delete", async (
    CharacterDeletionRequestV1 request,
    GameInstanceKeyAuthenticator instances,
    IAccountRepository accounts,
    HttpContext context,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!request.IsValid()) return Results.BadRequest(new { error = "invalid_character_deletion_request" });
    if (!instances.TryAuthenticate(context.Request, out _)) return Results.Unauthorized();
    try
    {
        var result = await accounts.TombstoneCharacterAsync(request, timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        var response = new CharacterDeletionResultV1
        {
            CorrelationId = request.CorrelationId,
            Accepted = result.ReasonCode is "deleted" or "duplicate",
            ReasonCode = result.ReasonCode
        };
        if (!response.IsValid()) return Results.StatusCode(StatusCodes.Status502BadGateway);
        return result.ReasonCode switch
        {
            "deleted" or "duplicate" => Results.Ok(response),
            "session_rejected" => Results.Unauthorized(),
            "character_not_found" => Results.NotFound(response),
            "character_in_use" or "transfer_in_progress" => Results.Conflict(response),
            _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        };
    }
    catch (Exception error) when (error is MySqlException or InvalidOperationException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("character-snapshot");

app.MapPost("/api/v1/game/characters/bootstrap", async (
    CharacterBootstrapRequestV1 request,
    GameInstanceKeyAuthenticator instances,
    ICharacterSaveSnapshotStore snapshots,
    HttpContext context,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!request.IsValid()) return Results.BadRequest(new { error = "invalid_character_bootstrap_request" });
    if (!instances.TryAuthenticate(context.Request, out _)) return Results.Unauthorized();
    var stored = await snapshots.BootstrapAsync(request, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
    var response = new CharacterBootstrapResultV1
    {
        CorrelationId = request.CorrelationId,
        Accepted = stored.Status is CharacterSaveSnapshotStoreStatus.Stored or CharacterSaveSnapshotStoreStatus.Duplicate,
        ReasonCode = stored.Status switch
        {
            CharacterSaveSnapshotStoreStatus.Stored => "stored",
            CharacterSaveSnapshotStoreStatus.Duplicate => "duplicate",
            CharacterSaveSnapshotStoreStatus.SessionRejected => "session_rejected",
            CharacterSaveSnapshotStoreStatus.NotFound => "character_not_found",
            CharacterSaveSnapshotStoreStatus.LeaseRejected => "character_in_use",
            CharacterSaveSnapshotStoreStatus.Conflict => "snapshot_conflict",
            _ => "unavailable"
        }
    };
    if (!response.IsValid()) return Results.StatusCode(StatusCodes.Status502BadGateway);
    return stored.Status switch
    {
        CharacterSaveSnapshotStoreStatus.Stored or CharacterSaveSnapshotStoreStatus.Duplicate => Results.Ok(response),
        CharacterSaveSnapshotStoreStatus.SessionRejected => Results.Unauthorized(),
        CharacterSaveSnapshotStoreStatus.NotFound => Results.NotFound(response),
        CharacterSaveSnapshotStoreStatus.LeaseRejected or CharacterSaveSnapshotStoreStatus.Conflict => Results.Conflict(response),
        _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
    };
}).RequireRateLimiting("character-snapshot");

app.MapPost("/api/v1/game/characters/snapshot/preview", async (
    CharacterSnapshotPreviewRequestV1 request,
    GameInstanceKeyAuthenticator instances,
    ICharacterSaveSnapshotStore snapshots,
    HttpContext context,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!request.IsValid()) return Results.BadRequest(new { error = "invalid_character_snapshot_preview_request" });
    if (!instances.TryAuthenticate(context.Request, out _)) return Results.Unauthorized();
    var result = await snapshots.ReadPreviewAsync(request, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
    if (result.Status == CharacterSaveSnapshotStoreStatus.Stored &&
        result.Snapshot is { } snapshot && snapshot.IsValid() && snapshot.CorrelationId == request.CorrelationId &&
        snapshot.CharacterId == request.CharacterId)
        return Results.Ok(snapshot);
    return result.Status switch
    {
        CharacterSaveSnapshotStoreStatus.NotFound => Results.NotFound(),
        CharacterSaveSnapshotStoreStatus.SessionRejected => Results.Unauthorized(),
        CharacterSaveSnapshotStoreStatus.Unavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        _ => Results.Conflict()
    };
}).RequireRateLimiting("character-snapshot");

app.MapPost("/api/v1/game/characters/resolve-identity", async (
    CharacterIdentityResolveRequestV1 request,
    GameInstanceKeyAuthenticator instances,
    IAccountRepository accounts,
    JoinTicketCodec joinTickets,
    JoinTicketOptions ticketOptions,
    HttpContext context,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!request.IsValid()) return Results.BadRequest(new { error = "invalid_character_identity_request" });
    if (!instances.TryAuthenticate(context.Request, out var instanceId)) return Results.Unauthorized();
    try
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = await accounts.ResolveCharacterIdentityAsync(request.AccountId, request.SessionId,
            request.DisplayName, now, cancellationToken);
        if (result.Accepted)
        {
            var expires = now.Add(ticketOptions.Lifetime);
            var leaseTicket = joinTickets.Issue(new JoinTicketClaims
            {
                SessionId = request.SessionId,
                AccountId = request.AccountId,
                CharacterId = result.CharacterId,
                InstanceId = instanceId,
                SystemId = "character-selection",
                Endpoint = "lease-acquisition",
                IssuedAtUtc = now,
                ExpiresAtUtc = expires,
                Nonce = Guid.NewGuid().ToString("N"),
                Audience = ticketOptions.Audience,
                KeyId = ticketOptions.KeyId
            });
            var response = new CharacterIdentityResolveResultV1
            {
                CorrelationId = request.CorrelationId, Accepted = true,
                ReasonCode = result.ReasonCode, CharacterId = result.CharacterId, LeaseTicket = leaseTicket
            };
            return response.IsValid() ? Results.Ok(response) : Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        return result.ReasonCode switch
        {
            "session_rejected" => Results.Unauthorized(),
            "character_name_conflict" => Results.Conflict(new { error = result.ReasonCode }),
            "character_identity_invalid" => Results.BadRequest(new { error = result.ReasonCode }),
            _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        };
    }
    catch (Exception error) when (error is MySqlException or InvalidOperationException or OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("character-snapshot");

app.MapPost("/api/v1/game/characters/snapshot/write", async (
    CharacterSaveSnapshotWriteRequestV1 request,
    GameInstanceKeyAuthenticator instances,
    CharacterSaveSnapshotService snapshots,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!request.IsValid()) return Results.BadRequest(new { error = "invalid_character_snapshot_request" });
    if (!instances.TryAuthenticate(context.Request, out var instanceId)) return Results.Unauthorized();
    var result = await snapshots.WriteAsync(request, instanceId, cancellationToken);
    if (!result.IsValid()) return Results.StatusCode(StatusCodes.Status502BadGateway);
    if (result.Accepted) return Results.Ok(result);
    return result.ReasonCode switch
    {
        "character_lease_rejected" => Results.Conflict(result),
        "character_not_found" => Results.NotFound(result),
        "snapshot_persistence_unavailable" => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        _ => Results.Conflict(result)
    };
}).RequireRateLimiting("character-snapshot");

app.MapPost("/api/v1/game/characters/snapshot/read", async (
    CharacterSaveSnapshotReadRequestV1 request,
    GameInstanceKeyAuthenticator instances,
    CharacterSaveSnapshotService snapshots,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!request.IsValid()) return Results.BadRequest(new { error = "invalid_character_snapshot_request" });
    if (!instances.TryAuthenticate(context.Request, out var instanceId)) return Results.Unauthorized();
    var result = await snapshots.ReadAsync(request, instanceId, cancellationToken);
    return result.Status switch
    {
        CharacterSaveSnapshotReadStatus.Found => Results.Ok(result.Snapshot),
        CharacterSaveSnapshotReadStatus.LeaseRejected => Results.Conflict(),
        CharacterSaveSnapshotReadStatus.NotFound => Results.NotFound(),
        _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
    };
}).RequireRateLimiting("character-snapshot");

app.MapPost("/api/v1/game/admin/commands", async (GameAdminQueryRequest request,
    GameInstanceKeyAuthenticator instances, AdminQueryRelay relay, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!instances.TryAuthenticate(context.Request, out var instanceId)) return Results.Unauthorized();
    try { return AdminHttpResult(await relay.FromGameAsync(request, instanceId, ct)); }
    catch (Exception e) when (e is MySqlException or InvalidOperationException or HttpRequestException or
        System.Text.Json.JsonException or OperationCanceledException)
    { return Results.StatusCode(503); }
}).RequireRateLimiting("admin");

app.MapPost("/api/v1/game/admin/actions", async (GameAdminActionRequest request,
    GameInstanceKeyAuthenticator instances, AdminActionRelay relay, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!instances.TryAuthenticate(context.Request, out var instanceId)) return Results.Unauthorized();
    try { return AdminActionHttpResult(await relay.FromGameAsync(request, instanceId, ct)); }
    catch (Exception e) when (e is MySqlException or InvalidOperationException or HttpRequestException or
        System.Text.Json.JsonException or OperationCanceledException)
    { return Results.StatusCode(503); }
}).RequireRateLimiting("admin");

app.MapPost("/api/v1/game/admin/permissions/check", async (GamePermissionCheckRequest request,
    GameInstanceKeyAuthenticator instances, AdminPermissionRelay relay,
    AdminPermissionSnapshotRelay permissions, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!instances.TryAuthenticate(context.Request, out var instanceId)) return Results.Unauthorized();
    try
    {
        var allowed = await relay.CheckFromGameAsync(request, instanceId, permissions, ct);
        return allowed is null ? Results.StatusCode(503) : Results.Ok(new { allowed });
    }
    catch (Exception e) when (e is MySqlException or InvalidOperationException or HttpRequestException or
        System.Text.Json.JsonException or OperationCanceledException)
    { return Results.StatusCode(503); }
}).RequireRateLimiting("admin");

app.MapPost("/api/v1/admin/commands", async (AdminQuery query, SessionTokenCodec tokens,
    IAccountRepository accounts, AdminQueryRelay relay, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!query.IsValid()) return Results.BadRequest();
    var auth = SessionAuthorization.AuthorizeToken(context.Request.Headers.Authorization, tokens, DateTime.UtcNow);
    if (!auth.Accepted) return Results.Unauthorized();
    try
    {
        if (await accounts.FindActiveSessionAsync(auth.Claims!.SessionId, auth.Claims.AccountId, DateTime.UtcNow, ct) is null)
            return Results.Unauthorized();
        return AdminHttpResult(await relay.SendAsync(auth.Claims.AccountId, "rest", query, ct));
    }
    catch (Exception e) when (e is MySqlException or InvalidOperationException or OperationCanceledException)
    { return Results.StatusCode(503); }
}).RequireRateLimiting("admin");

app.MapPost("/api/v1/admin/actions", async (AdminActionRequestV1 request, SessionTokenCodec tokens,
    IAccountRepository accounts, AdminActionRelay relay, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var auth = SessionAuthorization.AuthorizeToken(context.Request.Headers.Authorization, tokens, DateTime.UtcNow);
    if (!auth.Accepted) return Results.Unauthorized();
    try
    {
        var claims = auth.Claims!;
        if (await accounts.FindActiveSessionAsync(claims.SessionId, claims.AccountId, DateTime.UtcNow, ct) is null)
            return Results.Unauthorized();
        return AdminActionHttpResult(await relay.FromRestAsync(claims.AccountId, claims.SessionId, request, ct));
    }
    catch (Exception e) when (e is MySqlException or InvalidOperationException or HttpRequestException or
        System.Text.Json.JsonException or OperationCanceledException)
    { return Results.StatusCode(503); }
}).RequireRateLimiting("admin");

app.MapPost("/api/v1/admin/permissions/mutations", async (GatewayPermissionMutationRequest request,
    SessionTokenCodec tokens, IAccountRepository accounts, AdminPermissionRelay relay,
    HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var auth = SessionAuthorization.AuthorizeToken(context.Request.Headers.Authorization, tokens, DateTime.UtcNow);
    if (!auth.Accepted) return Results.Unauthorized();
    try
    {
        if (await accounts.FindActiveSessionAsync(auth.Claims!.SessionId, auth.Claims.AccountId, DateTime.UtcNow, ct) is null)
            return Results.Unauthorized();
        var result = await relay.FromRestAsync(auth.Claims.AccountId, request, ct);
        return Results.Json(result, statusCode: result.Status switch
        { "ok" => 200, "denied" => 403, "invalid" => 400, "conflict" => 409, "unavailable" => 503, _ => 202 });
    }
    catch (Exception e) when (e is MySqlException or InvalidOperationException or OperationCanceledException)
    { return Results.StatusCode(503); }
}).RequireRateLimiting("admin");

app.MapGet("/api/v1/admin/permissions/snapshot", async (SessionTokenCodec tokens,
    IAccountRepository accounts, AdminPermissionSnapshotRelay relay, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var auth = SessionAuthorization.AuthorizeToken(context.Request.Headers.Authorization, tokens, DateTime.UtcNow);
    if (!auth.Accepted) return Results.Unauthorized();
    try
    {
        if (await accounts.FindActiveSessionAsync(auth.Claims!.SessionId, auth.Claims.AccountId, DateTime.UtcNow, ct) is null)
            return Results.Unauthorized();
        if (await relay.HasPermissionAsync(auth.Claims.AccountId, "permissions.manage", null, null, ct) != true) return Results.Forbid();
        var json = await relay.ReadAsync(ct);
        return json is null ? Results.StatusCode(503) : Results.Content(json, "application/json");
    }
    catch (Exception e) when (e is MySqlException or InvalidOperationException or OperationCanceledException)
    { return Results.StatusCode(503); }
}).RequireRateLimiting("admin");

app.MapPost("/api/v1/game/admin/permissions/mutations", async (GamePermissionMutationRequest request,
    GameInstanceKeyAuthenticator instances, AdminPermissionRelay relay, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!instances.TryAuthenticate(context.Request, out var instanceId)) return Results.Unauthorized();
    try
    {
        var result = await relay.FromGameAsync(request, instanceId, ct);
        return Results.Json(result, statusCode: result.Status switch
        { "ok" => 200, "denied" => 403, "invalid" => 400, "conflict" => 409, "unavailable" => 503, _ => 202 });
    }
    catch (Exception e) when (e is MySqlException or InvalidOperationException or OperationCanceledException)
    { return Results.StatusCode(503); }
}).RequireRateLimiting("admin");

app.MapGet("/api/v1/game/admin/permissions/snapshot", async (GameInstanceKeyAuthenticator instances,
    AdminPermissionSnapshotRelay relay, HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!instances.TryAuthenticate(context.Request, out _)) return Results.Unauthorized();
    var json = await relay.ReadAsync(ct);
    return json is null ? Results.StatusCode(503) : Results.Content(json, "application/json");
}).RequireRateLimiting("admin");

app.MapPost("/api/v1/game/admin/permissions/ack", async (LancerNexus.Protocol.PermissionRevisionAcknowledged ack,
    GameInstanceKeyAuthenticator instances, AdminPermissionSnapshotRelay relay, HttpContext context, CancellationToken ct) =>
{
    if (!instances.TryAuthenticate(context.Request, out var instanceId) || ack.Revision < 0) return Results.Unauthorized();
    var applied = await relay.AcknowledgeAsync(new LancerNexus.Protocol.PermissionRevisionAcknowledged
    { InstanceId = instanceId, Revision = ack.Revision, AppliedUtc = DateTimeOffset.UtcNow }, ct);
    return applied ? Results.Ok() : Results.StatusCode(503);
}).RequireRateLimiting("admin");

app.MapPost("/api/v1/game/verify-transfer-ticket", async (
    TransferTicketVerificationRequest request,
    TransferTicketAdmissionService admission,
    GameInstanceKeyAuthenticator gameInstances,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (!gameInstances.TryAuthenticate(context.Request, out var instanceId) ||
        !string.Equals(instanceId, request.TargetInstanceId, StringComparison.Ordinal))
        return Results.Unauthorized();
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

app.MapPost("/api/v1/game/release-transfer", async (
    TransferSourceReleaseRequest request,
    TransferSourceReleaseService release,
    GameInstanceKeyAuthenticator gameInstances,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (!gameInstances.TryAuthenticate(context.Request, out var instanceId))
        return Results.Unauthorized();
    var result = await release.ReleaseAsync(request, instanceId, cancellationToken);
    if (!result.Accepted)
    {
        app.Logger.LogWarning("Game transfer source release rejected: {ReasonCode}.", result.ReasonCode);
        return result.Failure switch
        {
            TransferSourceReleaseFailure.CoordinatorUnavailable or TransferSourceReleaseFailure.PersistenceUnavailable =>
                Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            TransferSourceReleaseFailure.CoordinatorInvalidResponse or TransferSourceReleaseFailure.PersistenceInvalidResponse =>
                Results.StatusCode(StatusCodes.Status502BadGateway),
            TransferSourceReleaseFailure.SnapshotStoreUnavailable =>
                Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            _ => Results.Conflict(new { error = result.ReasonCode })
        };
    }
    return Results.Ok(new { transferId = result.TransferId, instanceId, state = "SourceReleased" });
}).RequireRateLimiting("transfer");

app.MapPost("/api/v1/game/start-transfer", async (
    TransferStartRequest request,
    TransferInitiationService transfers,
    GameInstanceKeyAuthenticator gameInstances,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (!gameInstances.TryAuthenticate(context.Request, out var sourceInstanceId))
        return Results.Unauthorized();
    TransferInitiationResult result;
    try
    {
        result = await transfers.StartFromInstanceAsync(request, sourceInstanceId, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    if (!result.Succeeded)
    {
        app.Logger.LogWarning("Game transfer start rejected: {ReasonCode}.", result.ReasonCode);
        return result.Failure switch
        {
            TransferInitiationFailure.CoordinatorUnavailable or TransferInitiationFailure.ActiveLeaseUnavailable or
                TransferInitiationFailure.TicketSigningUnavailable =>
                Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            TransferInitiationFailure.CoordinatorInvalidResponse =>
                Results.StatusCode(StatusCodes.Status502BadGateway),
            TransferInitiationFailure.TargetRejected => Results.Conflict(new { error = result.ReasonCode }),
            _ => Results.BadRequest(new { error = result.ReasonCode })
        };
    }
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(result.Response);
}).RequireRateLimiting("transfer");

app.MapPost("/api/v1/game/freeze-transfer/{transferId:guid}", async (
    Guid transferId,
    TransferSourceFreezeService freeze,
    GameInstanceKeyAuthenticator gameInstances,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (!gameInstances.TryAuthenticate(context.Request, out var instanceId))
        return Results.Unauthorized();
    if (!string.Equals(context.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    if (!long.TryParse(context.Request.Headers["X-Lancer-Nexus-Lease-Version"], NumberStyles.None,
            CultureInfo.InvariantCulture, out var expectedLeaseVersion) || expectedLeaseVersion <= 0)
        return Results.BadRequest(new { error = "transfer_lease_version_invalid" });
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.ContentLength > TransferSnapshotLimits.MaxBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    using var snapshot = new MemoryStream();
    var buffer = new byte[64 * 1024];
    var total = 0;
    while (true)
    {
        var read = await context.Request.Body.ReadAsync(buffer, cancellationToken);
        if (read == 0)
            break;
        if (total > TransferSnapshotLimits.MaxBytes - read)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        await snapshot.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        total += read;
    }
    var result = await freeze.MarkFrozenAsync(transferId, instanceId, expectedLeaseVersion,
        snapshot.ToArray(), cancellationToken);
    if (!result.Accepted)
    {
        app.Logger.LogWarning("Game transfer source freeze rejected: {ReasonCode}.", result.ReasonCode);
        return result.Failure switch
        {
            TransferSourceFreezeFailure.CoordinatorUnavailable =>
                Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            TransferSourceFreezeFailure.SnapshotStoreUnavailable =>
                Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            TransferSourceFreezeFailure.PersistenceUnavailable =>
                Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            TransferSourceFreezeFailure.CoordinatorInvalidResponse =>
                Results.StatusCode(StatusCodes.Status502BadGateway),
            _ => Results.Conflict(new { error = result.ReasonCode })
        };
    }
    return Results.Ok(new { transferId = result.TransferId, instanceId, state = "SourceFrozen", snapshotBytes = total });
}).RequireRateLimiting("transfer");

app.MapPost("/internal/v1/npc-mission-authority", async (
    NpcMissionAuthorityRequestV1 request, NpcMissionAuthorityService authority,
    NpcMissionAuthorityAuthenticator authentication, HttpContext context, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!authentication.IsConfigured)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!authentication.TryAuthenticate(context.Request))
        return Results.Unauthorized();
    try
    {
        var result = await authority.DecideAsync(request, cancellationToken);
        return Results.Ok(result);
    }
    catch (Exception exception) when (exception is InvalidOperationException or MySqlConnector.MySqlException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/v1/game/transfers/{transferId:guid}", async (
    Guid transferId,
    TransferStatusService status,
    GameInstanceKeyAuthenticator gameInstances,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (!gameInstances.TryAuthenticate(context.Request, out var instanceId))
        return Results.Unauthorized();
    context.Response.Headers.CacheControl = "no-store";
    var result = await status.GetAsync(transferId, instanceId, cancellationToken);
    if (result.Accepted)
        return Results.Ok(result.Status);
    app.Logger.LogWarning("Game transfer status rejected: {ReasonCode}.", result.ReasonCode);
    return result.Failure switch
    {
        TransferSourceReleaseFailure.CoordinatorUnavailable =>
            Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        TransferSourceReleaseFailure.PersistenceUnavailable =>
            Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        TransferSourceReleaseFailure.PersistenceInvalidResponse =>
            Results.StatusCode(StatusCodes.Status502BadGateway),
        TransferSourceReleaseFailure.CoordinatorInvalidResponse =>
            Results.StatusCode(StatusCodes.Status502BadGateway),
        _ => Results.NotFound()
    };
}).RequireRateLimiting("transfer-status");

app.MapGet("/api/v1/game/transfers/{transferId:guid}/snapshot", async (
    Guid transferId,
    TransferSnapshotReadService snapshots,
    GameInstanceKeyAuthenticator gameInstances,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (!gameInstances.TryAuthenticate(context.Request, out var instanceId))
        return Results.Unauthorized();
    context.Response.Headers.CacheControl = "no-store";
    var result = await snapshots.ReadForTargetAsync(transferId, instanceId, cancellationToken);
    if (result.Status == TransferSnapshotStoreStatus.Stored && result.Record is not null)
        return Results.File(result.Record.Snapshot, "application/octet-stream");
    return result.Status switch
    {
        TransferSnapshotStoreStatus.Unavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        _ => Results.NotFound()
    };
}).RequireRateLimiting("transfer");

app.MapPost("/api/v1/game/accept-transfer", async (
    TransferTargetAcceptanceRequest request,
    TransferAcceptanceService acceptance,
    GameInstanceKeyAuthenticator gameInstances,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (!gameInstances.TryAuthenticate(context.Request, out var instanceId))
        return Results.Unauthorized();
    TransferAcceptanceResult result;
    try
    {
        result = await acceptance.AcceptAsync(request, instanceId, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    if (!result.Accepted)
    {
        app.Logger.LogWarning("Game transfer acceptance rejected: {ReasonCode}.", result.ReasonCode);
        return result.Failure switch
        {
            TransferAcceptanceFailure.CoordinatorUnavailable or TransferAcceptanceFailure.PersistenceUnavailable =>
                Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            TransferAcceptanceFailure.CoordinatorInvalidResponse =>
                Results.StatusCode(StatusCodes.Status502BadGateway),
            TransferAcceptanceFailure.PersistenceRejected =>
                Results.Conflict(new { error = result.ReasonCode }),
            _ => Results.Unauthorized()
        };
    }
    return Results.Ok(new
    {
        transferId = result.TransferId,
        instanceId,
        leaseVersion = result.LeaseVersion
    });
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

static IResult AdminHttpResult(AdminQueryResponse result) => Results.Json(result, statusCode: result.Status switch
{ "ok" => 200, "denied" => 403, "invalid" => 400, "conflict" => 409, "unavailable" => 503, "not_found" => 404, _ => 503 });

static IResult AdminActionHttpResult(AdminActionResultV1 result) => Results.Json(result, statusCode: result.Status switch
{
    AdminActionStatus.Pending => 202,
    AdminActionStatus.Completed => 200,
    AdminActionStatus.Denied => 403,
    AdminActionStatus.Invalid => 400,
    AdminActionStatus.NotFound => 404,
    AdminActionStatus.Conflict => 409,
    AdminActionStatus.Unavailable => 503,
    _ => 503
});

public partial class Program;
public sealed record JoinTicketVerificationRequest(string Ticket);
