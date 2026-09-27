using System.Security.Cryptography;
using LancerNexus.Protocol;

namespace LancerNexus.Gateway;

public enum TransferInitiationFailure
{
    None,
    InvalidRequest,
    SessionMismatch,
    ActiveLeaseUnavailable,
    TicketSigningUnavailable,
    CoordinatorUnavailable,
    CoordinatorInvalidResponse,
    TargetRejected
}

public sealed record TransferInitiationResult(
    TransferStartResult? Response,
    TransferInitiationFailure Failure,
    string ReasonCode)
{
    public bool Succeeded => Response is not null && Failure == TransferInitiationFailure.None;
}

public sealed class TransferInitiationService(
    IAccountRepository accounts,
    ICoordinatorTransferClient coordinator,
    TransferTicketCodec transferTickets,
    TransferTicketOptions ticketOptions,
    TimeProvider timeProvider)
{
    public async Task<TransferInitiationResult> StartAsync(
        TransferStartRequest request,
        SessionTokenClaims session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await StartCoreAsync(request, session.AccountId, session.SessionId, null, cancellationToken);
    }

    public Task<TransferInitiationResult> StartFromInstanceAsync(
        TransferStartRequest request, string sourceInstanceId, CancellationToken cancellationToken = default)
    {
        if (request.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(sourceInstanceId))
            return Task.FromResult(Failed(TransferInitiationFailure.InvalidRequest, "invalid_transfer_request"));
        return StartCoreAsync(request, request.AccountId, request.SessionId, sourceInstanceId, cancellationToken);
    }

    private async Task<TransferInitiationResult> StartCoreAsync(
        TransferStartRequest request, Guid accountId, Guid sessionId, string? authenticatedSourceInstanceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (request.TransferId == Guid.Empty || request.SessionId == Guid.Empty || request.CharacterId <= 0 ||
            request.TargetInstanceId is { Length: > 96 } ||
            string.IsNullOrWhiteSpace(request.TargetSystemId) || request.TargetSystemId.Length > 96 ||
            request.GroupId is { Length: > 96 } || string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > 128 || request.ExpiresUtc.Kind != DateTimeKind.Utc ||
            request.ExpiresUtc <= now || request.ExpiresUtc > now.AddMinutes(2))
            return Failed(TransferInitiationFailure.InvalidRequest, "invalid_transfer_request");
        if (request.SessionId != sessionId ||
            (request.AccountId != Guid.Empty && request.AccountId != accountId))
            return Failed(TransferInitiationFailure.SessionMismatch, "session_id_mismatch");
        if (!ticketOptions.IsConfigured)
            return Failed(TransferInitiationFailure.TicketSigningUnavailable, "transfer_ticket_signing_not_configured");

        var lease = await accounts.FindActiveCharacterLeaseAsync(
            accountId, sessionId, request.CharacterId, now, cancellationToken);
        if (lease is null || lease.LeaseVersion < 0 || lease.ValidUntilUtc <= now ||
            string.IsNullOrWhiteSpace(lease.InstanceId) ||
            (!string.IsNullOrWhiteSpace(request.TargetInstanceId) &&
             string.Equals(lease.InstanceId, request.TargetInstanceId, StringComparison.Ordinal)) ||
            (authenticatedSourceInstanceId is not null &&
             !string.Equals(lease.InstanceId, authenticatedSourceInstanceId, StringComparison.Ordinal)))
            return Failed(TransferInitiationFailure.ActiveLeaseUnavailable, "active_source_lease_unavailable");

        var prepare = new TransferPrepareRequest
        {
            TransferId = request.TransferId,
            SessionId = sessionId,
            CharacterId = request.CharacterId,
            SourceInstanceId = lease.InstanceId,
            TargetInstanceId = request.TargetInstanceId,
            TargetSystemId = request.TargetSystemId,
            GroupId = request.GroupId,
            ExpiresUtc = request.ExpiresUtc,
            IdempotencyKey = request.IdempotencyKey
        };
        var result = await coordinator.PrepareAsync(prepare, cancellationToken);
        if (!result.IsAvailable)
            return Failed(TransferInitiationFailure.CoordinatorUnavailable,
                result.Error ?? "coordinator_unavailable");
        if (result.Envelope is null || result.Envelope.Decision.TransferId != request.TransferId)
            return Failed(TransferInitiationFailure.CoordinatorInvalidResponse, "coordinator_invalid_response");
        var envelope = result.Envelope;
        if (!envelope.Decision.Accepted)
            return Failed(TransferInitiationFailure.TargetRejected, envelope.Decision.ReasonCode);
        if (envelope.State is not (TransferState.Prepared or TransferState.SourceFrozen) ||
            string.IsNullOrWhiteSpace(envelope.TargetEndpoint) ||
            string.IsNullOrWhiteSpace(envelope.TargetInstanceId) ||
            string.Equals(envelope.TargetInstanceId, lease.InstanceId, StringComparison.Ordinal) ||
            envelope.Decision.ExpiresUtc.Kind != DateTimeKind.Utc ||
            envelope.Decision.ExpiresUtc <= now || envelope.Decision.ExpiresUtc > now.AddMinutes(2))
        {
            _ = await coordinator.AbortAsync(new TransferAbort
            {
                TransferId = request.TransferId,
                ReasonCode = "coordinator_invalid_response",
                Retryable = true
            }, cancellationToken);
            return Failed(TransferInitiationFailure.CoordinatorInvalidResponse, "coordinator_invalid_response");
        }

        var confirmedLease = await accounts.FindActiveCharacterLeaseAsync(
            accountId, sessionId, request.CharacterId, now, cancellationToken);
        if (confirmedLease is null || confirmedLease.ValidUntilUtc <= now ||
            confirmedLease.LeaseVersion != lease.LeaseVersion ||
            !string.Equals(confirmedLease.InstanceId, lease.InstanceId, StringComparison.Ordinal) ||
            (authenticatedSourceInstanceId is not null &&
             !string.Equals(confirmedLease.InstanceId, authenticatedSourceInstanceId, StringComparison.Ordinal)))
        {
            _ = await coordinator.AbortAsync(new TransferAbort
            {
                TransferId = request.TransferId,
                ReasonCode = "source_lease_changed",
                Retryable = true
            }, cancellationToken);
            return Failed(TransferInitiationFailure.ActiveLeaseUnavailable, "source_lease_changed");
        }

        string ticket;
        try
        {
            ticket = transferTickets.Issue(new TransferTicketClaims
            {
                TransferId = request.TransferId,
                SessionId = sessionId,
                AccountId = accountId,
                CharacterId = request.CharacterId,
                SourceInstanceId = lease.InstanceId,
                TargetInstanceId = envelope.TargetInstanceId!,
                TargetSystemId = request.TargetSystemId,
                LeaseVersion = lease.LeaseVersion,
                IssuedAtUtc = now,
                ExpiresAtUtc = envelope.Decision.ExpiresUtc,
                Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
                Audience = ticketOptions.Audience,
                KeyId = ticketOptions.KeyId
            });
        }
        catch (InvalidOperationException)
        {
            return Failed(TransferInitiationFailure.TicketSigningUnavailable,
                "transfer_ticket_signing_not_configured");
        }

        return new TransferInitiationResult(new TransferStartResult
        {
            Prepared = new TransferPrepared
            {
                TransferId = request.TransferId,
                Accepted = true,
                TransferTicket = ticket,
                ExpiresUtc = envelope.Decision.ExpiresUtc,
                ReasonCode = envelope.Decision.ReasonCode
            },
            SourceInstanceId = lease.InstanceId,
            TargetEndpoint = envelope.TargetEndpoint,
            TargetInstanceId = envelope.TargetInstanceId,
            TargetSystemId = request.TargetSystemId,
            LeaseVersion = lease.LeaseVersion,
            Duplicate = envelope.Duplicate
        }, TransferInitiationFailure.None, "prepared");
    }

    private static TransferInitiationResult Failed(TransferInitiationFailure failure, string reasonCode) =>
        new(null, failure, reasonCode);
}
