using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class CoordinatorTransferClientTests
{
    [Fact]
    public async Task Prepare_ForwardsAuthenticatedIdempotentTransferRequest()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);
        var request = Request();

        var result = await client.PrepareAsync(request);

        Assert.True(result.IsAvailable);
        Assert.True(result.Envelope!.Decision.Accepted);
        Assert.Equal(request.TransferId, result.Envelope.Decision.TransferId);
        Assert.Equal(new string('k', 32), handler.Authorization!.Parameter);
        Assert.Equal("https://coordinator.example/internal/v1/transfers/prepare", handler.RequestUri!.ToString());
        Assert.Contains(request.IdempotencyKey, handler.Body!, StringComparison.Ordinal);
        Assert.Contains(request.TargetInstanceId, handler.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_RejectsResponseForAnotherTransfer()
    {
        using var handler = new CaptureHandler(responseTransferId: Guid.NewGuid());
        using var httpClient = new HttpClient(handler);
        var result = await CreateClient(httpClient).PrepareAsync(Request());

        Assert.True(result.IsAvailable);
        Assert.Null(result.Envelope);
        Assert.Equal("coordinator_transfer_id_mismatch", result.Error);
    }

    [Fact]
    public async Task Commit_UsesFencedLeaseVersionRoute()
    {
        using var handler = new CaptureHandler(stateResponse: true);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);
        var transferId = Guid.NewGuid();

        var result = await client.CommitAsync(transferId, 9);

        Assert.True(result.IsAvailable);
        Assert.True(result.Outcome!.Accepted);
        Assert.Equal(TransferState.Committed, result.Outcome.State);
        Assert.Equal($"https://coordinator.example/internal/v1/transfers/{transferId:D}/commit/9", handler.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Authorization!.Scheme);
    }

    private static CoordinatorTransferClient CreateClient(HttpClient httpClient) => new(
        httpClient,
        new CoordinatorGatewayOptions(new Uri("https://coordinator.example/"), new string('k', 32),
            TimeSpan.FromSeconds(5)));

    private static TransferPrepareRequest Request() => new()
    {
        TransferId = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        CharacterId = 42,
        SourceInstanceId = "li01-instance",
        TargetInstanceId = "li02-instance",
        TargetSystemId = "li02",
        ExpiresUtc = DateTime.UtcNow.AddSeconds(60),
        IdempotencyKey = "jump-li01-li02-001"
    };

    private sealed class CaptureHandler(Guid? responseTransferId = null, bool stateResponse = false) : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Body { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            RequestUri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            object payload;
            if (stateResponse)
            {
                payload = new CoordinatorTransferOutcome(true, "accepted", TransferState.Committed, false);
            }
            else
            {
                var transfer = JsonSerializer.Deserialize<TransferPrepareRequest>(
                    Body!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                payload = new CoordinatorTransferEnvelope(
                    new TransferPrepared
                    {
                        TransferId = responseTransferId ?? transfer.TransferId,
                        Accepted = true,
                        ExpiresUtc = transfer.ExpiresUtc,
                        ReasonCode = "prepared"
                    },
                    TransferState.Prepared,
                    "10.0.0.2:2300",
                    false);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
        }
    }
}
