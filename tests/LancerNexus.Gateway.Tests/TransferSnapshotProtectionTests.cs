using System.Security.Cryptography;
using LancerNexus.Gateway;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class TransferSnapshotProtectionTests
{
    [Fact]
    public void SnapshotRoundTripsOnlyWithItsTransferAssociatedData()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:TransferSnapshotCurrentKeyId"] = "key-v1",
            ["Gateway:TransferSnapshotKeys:key-v1"] = Convert.ToBase64String(key)
        }).Build();
        var protection = new TransferSnapshotProtection(configuration);
        var transferId = Guid.NewGuid();
        var snapshot = RandomNumberGenerator.GetBytes(4096);

        Assert.True(protection.TryProtect(transferId, snapshot, out var keyId, out var protectedSnapshot));
        Assert.Equal("key-v1", keyId);
        Assert.NotEqual(snapshot, protectedSnapshot);
        Assert.True(protection.TryUnprotect(transferId, keyId, protectedSnapshot, out var restored));
        Assert.Equal(snapshot, restored);
        Assert.False(protection.TryUnprotect(Guid.NewGuid(), keyId, protectedSnapshot, out _));

        protectedSnapshot[12] ^= 0x80;
        Assert.False(protection.TryUnprotect(transferId, keyId, protectedSnapshot, out _));
    }

    [Fact]
    public void MissingOrInvalidKeyFailsClosed()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:TransferSnapshotCurrentKeyId"] = "key-v1",
            ["Gateway:TransferSnapshotKeys:key-v1"] = Convert.ToBase64String(new byte[16])
        }).Build();
        var protection = new TransferSnapshotProtection(configuration);

        Assert.False(protection.TryProtect(Guid.NewGuid(), new byte[] { 1 }, out _, out _));
    }
}
