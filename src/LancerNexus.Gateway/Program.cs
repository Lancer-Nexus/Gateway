using LancerNexus.Gateway;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
var coordinatorOptions = CoordinatorGatewayOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(coordinatorOptions);
builder.Services.AddHttpClient<CoordinatorPlacementClient>();

var app = builder.Build();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});

app.MapHealthChecks("/health/ready");

app.MapGet("/api/v1/capabilities", () => Results.Ok(new
{
    service = "gateway",
    protocolVersion = LancerNexus.Protocol.ProtocolConstants.ProtocolVersion,
    capabilities = new[] { "health_v1", "protocol_v1", "coordinator_placement_v1" }
}));

app.MapPost("/api/v1/placement", async (
    LancerNexus.Protocol.PlacementRequest request,
    LancerNexus.Gateway.CoordinatorPlacementClient coordinator,
    CancellationToken cancellationToken) =>
{
    if (request.RequestId == Guid.Empty || request.SessionId == Guid.Empty ||
        string.IsNullOrWhiteSpace(request.TargetSystem) ||
        string.IsNullOrWhiteSpace(request.IdempotencyKey))
        return Results.BadRequest(new { error = "invalid_placement_request" });

    var result = await coordinator.PlaceAsync(request, cancellationToken);
    if (!result.IsAvailable)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (result.Envelope is null)
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    return result.Envelope.Decision.Accepted
        ? Results.Ok(result.Envelope)
        : Results.Conflict(result.Envelope);
});

app.Run();

public partial class Program;
