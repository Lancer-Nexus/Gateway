using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class CharacterLeaseAcquisitionServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] TokenBytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly string LeaseToken = Convert.ToBase64String(TokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static readonly string SigningKey = new('j', 32);

    [Fact]
    public async Task AcquireUsesTicketIdentityAndHashesTheLeaseToken()
    {
        var claims = Claims(Now);
        var store = new LeaseStore(new(true, "acquired", 1, Now.AddMinutes(15)));
        var service = Service(store);
        var request = Request(claims);

        var result = await service.AcquireAsync(request, claims.InstanceId);

        Assert.True(result.Accepted);
        Assert.True(result.IsValid());
        Assert.Equal(request.CorrelationId, result.CorrelationId);
        Assert.Equal(claims.AccountId, store.AccountId);
        Assert.Equal(claims.SessionId, store.SessionId);
        Assert.Equal(claims.CharacterId, store.CharacterId);
        Assert.Equal(claims.InstanceId, store.InstanceId);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(TokenBytes), store.LeaseTokenHash);
    }

    [Fact]
    public async Task AcquireRejectsTicketForDifferentInstanceOrSelectedCharacter()
    {
        var claims = Claims(Now);
        var store = new LeaseStore(new(true, "acquired", 1, Now.AddMinutes(15)));
        var service = Service(store);
        var request = Request(claims);

        var wrongInstance = await service.AcquireAsync(request, "other-instance");
        var wrongCharacter = await service.AcquireAsync(request with { CharacterId = 74 }, claims.InstanceId);

        Assert.Equal("join_ticket_instance_mismatch", wrongInstance.ReasonCode);
        Assert.Equal("join_ticket_character_mismatch", wrongCharacter.ReasonCode);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task AcquireSupportsUnboundTicketWhenSelectedCharacterBelongsToSessionAccount()
    {
        var claims = Claims(Now, null);
        var store = new LeaseStore(new(true, "acquired", 1, Now.AddMinutes(15)));

        var result = await Service(store).AcquireAsync(Request(claims), claims.InstanceId);

        Assert.True(result.Accepted);
        Assert.Equal(73, store.CharacterId);
    }

    [Fact]
    public async Task ReleaseUsesAuthenticatedInstanceAndHashesExactLeaseToken()
    {
        var store = new LeaseStore(new(true, "acquired", 1, Now.AddMinutes(15)));
        var service = new CharacterLeaseReleaseService(store, new FixedTimeProvider(Now));
        var request = new CharacterLeaseReleaseRequestV1
        {
            CorrelationId = Guid.NewGuid(),
            CharacterId = 73,
            SessionId = Guid.NewGuid(),
            LeaseToken = LeaseToken,
            LeaseVersion = 4
        };

        var result = await service.ReleaseAsync(request, "liberty-01");

        Assert.True(result.Released);
        Assert.Equal(request.CorrelationId, result.CorrelationId);
        Assert.Equal(4, result.LeaseVersion);
        Assert.Equal("liberty-01", store.ReleaseInstanceId);
        Assert.Equal(request.SessionId, store.ReleaseSessionId);
        Assert.Equal(73, store.ReleaseCharacterId);
        Assert.Equal(4, store.ReleaseLeaseVersion);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(TokenBytes), store.ReleaseTokenHash);
    }

    private static CharacterLeaseAcquisitionService Service(LeaseStore store)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:JoinTicketSigningKey"] = SigningKey,
            ["Gateway:JoinTicketKeyId"] = "join-01",
            ["Gateway:JoinTicketAudience"] = "game-server",
            ["Gateway:CharacterLeaseLifetimeSeconds"] = "900"
        }).Build();
        var codec = new JoinTicketCodec(JoinTicketOptions.FromConfiguration(configuration));
        return new CharacterLeaseAcquisitionService(codec, store, configuration, new FixedTimeProvider(Now));
    }

    private static CharacterLeaseAcquireRequestV1 Request(JoinTicketClaims claims) => new()
    {
        CorrelationId = Guid.NewGuid(),
        JoinTicket = new JoinTicketCodec(new JoinTicketOptions(SigningKey, "join-01", "game-server",
            TimeSpan.FromMinutes(2))).Issue(claims),
        LeaseToken = LeaseToken,
        CharacterId = 73
    };

    private static JoinTicketClaims Claims(DateTime now, long? characterId = 73) => new()
    {
        SessionId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        CharacterId = characterId,
        InstanceId = "liberty-01",
        SystemId = "li01",
        Endpoint = "10.20.0.31:2300",
        IssuedAtUtc = now,
        ExpiresAtUtc = now.AddMinutes(2),
        Nonce = Guid.NewGuid().ToString("N"),
        Audience = "game-server",
        KeyId = "join-01"
    };

    private sealed class LeaseStore(CharacterLeaseAcquireResult result) : ICharacterLeaseAcquisitionStore
    {
        public Guid? AccountId { get; private set; }
        public Guid? SessionId { get; private set; }
        public long? CharacterId { get; private set; }
        public string? InstanceId { get; private set; }
        public byte[]? LeaseTokenHash { get; private set; }
        public int Calls { get; private set; }
        public string? ReleaseInstanceId { get; private set; }
        public Guid? ReleaseSessionId { get; private set; }
        public long? ReleaseCharacterId { get; private set; }
        public long? ReleaseLeaseVersion { get; private set; }
        public byte[]? ReleaseTokenHash { get; private set; }

        public Task<CharacterLeaseAcquireResult> AcquireCharacterLeaseAsync(Guid accountId, Guid sessionId,
            long characterId, string instanceId, byte[] leaseTokenHash, DateTime validUntilUtc, DateTime nowUtc,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            AccountId = accountId;
            SessionId = sessionId;
            CharacterId = characterId;
            InstanceId = instanceId;
            LeaseTokenHash = leaseTokenHash;
            return Task.FromResult(result);
        }

        public Task<CharacterLeaseReleaseResult> ReleaseCharacterLeaseAsync(long characterId, Guid sessionId,
            string instanceId, byte[] leaseTokenHash, long leaseVersion, DateTime nowUtc,
            CancellationToken cancellationToken = default)
        {
            ReleaseCharacterId = characterId;
            ReleaseSessionId = sessionId;
            ReleaseInstanceId = instanceId;
            ReleaseTokenHash = leaseTokenHash;
            ReleaseLeaseVersion = leaseVersion;
            return Task.FromResult(new CharacterLeaseReleaseResult(true, "released", leaseVersion));
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
