using LancerNexus.Gateway;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class AccountRepositoryTests
{
    [Fact]
    public void EmailNormalization_IsStableAndCaseInsensitive()
    {
        Assert.Equal("PILOT@EXAMPLE.NET", AccountEmail.Normalize("  Pilot@example.net "));
    }

    [Fact]
    public async Task UnconfiguredRepository_FailsClosed()
    {
        var repository = new AccountRepositoryNotConfigured();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.FindByEmailAsync("pilot@example.net"));

        Assert.Contains("not configured", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmailNormalization_RejectsMalformedInput()
    {
        Assert.Throws<ArgumentException>(() => AccountEmail.Normalize("not-an-email"));
    }

    [Fact]
    public async Task LeaseTransfer_RejectsInvalidFenceInputBeforeDatabaseAccess()
    {
        var repository = new MySqlAccountRepository("Server=127.0.0.1;Port=1;Database=unused;User ID=unused;Password=unused;");

        var result = await repository.CommitCharacterLeaseTransferAsync(
            Guid.NewGuid(), Guid.NewGuid(), 12, "li01", "li02", -1, new byte[32],
            DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow);

        Assert.False(result.Accepted);
        Assert.Equal("invalid_transfer", result.ReasonCode);
        Assert.Null(result.LeaseVersion);
    }

    [Fact]
    public async Task UnconfiguredRepository_FailsClosedForLeaseReads()
    {
        var repository = new AccountRepositoryNotConfigured();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.FindActiveCharacterLeaseAsync(Guid.NewGuid(), Guid.NewGuid(), 12, DateTime.UtcNow));

        Assert.Contains("not configured", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
