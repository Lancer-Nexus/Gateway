using System.Security.Cryptography;
using LancerNexus.Gateway;
using LancerNexus.Protocol;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class CharacterSaveSnapshotServiceTests
{
    private static readonly byte[] TokenBytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly string LeaseToken = Convert.ToBase64String(TokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task WritePassesOnlyTokenHashAndReturnsStoredRevision()
    {
        var store = new SnapshotStore(new(CharacterSaveSnapshotStoreStatus.Stored, 4));
        var request = ValidWrite();

        var result = await new CharacterSaveSnapshotService(store, TimeProvider.System)
            .WriteAsync(request, "li-01");

        Assert.True(result.IsValid());
        Assert.True(result.Accepted);
        Assert.Equal(4, result.Revision);
        Assert.Equal(SHA256.HashData(TokenBytes), store.TokenHash);
        Assert.NotEqual(TokenBytes, store.TokenHash);
    }

    [Fact]
    public async Task WriteMapsDuplicateAndLeaseFenceRejection()
    {
        var duplicate = await new CharacterSaveSnapshotService(
            new SnapshotStore(new(CharacterSaveSnapshotStoreStatus.Duplicate, 2)), TimeProvider.System)
            .WriteAsync(ValidWrite(), "li-01");
        var rejected = await new CharacterSaveSnapshotService(
            new SnapshotStore(new(CharacterSaveSnapshotStoreStatus.LeaseRejected)), TimeProvider.System)
            .WriteAsync(ValidWrite(), "li-01");

        Assert.True(duplicate.Accepted);
        Assert.Equal("duplicate", duplicate.ReasonCode);
        Assert.False(rejected.Accepted);
        Assert.Equal("character_lease_rejected", rejected.ReasonCode);
    }

    private static CharacterSaveSnapshotWriteRequestV1 ValidWrite() => new()
    {
        CorrelationId = Guid.NewGuid(),
        CharacterId = 44,
        LeaseToken = LeaseToken,
        LeaseVersion = 3,
        Revision = 4,
        Payload = [1, 2, 3]
    };

    private sealed class SnapshotStore(CharacterSaveSnapshotStoreResult result) : ICharacterSaveSnapshotStore
    {
        public byte[]? TokenHash { get; private set; }
        public Task<CharacterSaveSnapshotStoreResult> WriteAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
            CharacterSaveSnapshotWriteRequestV1 request, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            TokenHash = leaseTokenHash.ToArray();
            return Task.FromResult(result);
        }

        public Task<CharacterSaveSnapshotStoreResult> ReadAsync(string instanceId, ReadOnlyMemory<byte> leaseTokenHash,
            CharacterSaveSnapshotReadRequestV1 request, DateTime nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
