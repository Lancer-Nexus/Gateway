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
}
