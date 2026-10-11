using System.Text;
using LancerNexus.Gateway;
using Xunit;

namespace LancerNexus.Gateway.Tests;

public sealed class SaveGameFactionReputationParserTests
{
    [Fact]
    public void ParsesPlayerHouseValuesAndNormalizesFactionIds()
    {
        var payload = Encoding.Latin1.GetBytes("""
            [Player]
            name = 50696c6f74
            house = 0.75, LI_N_GRP
            house = -0.25, fc_rh_grp
            [mplayer]
            house = 0, ignored_group
            """);

        Assert.True(SaveGameFactionReputationParser.TryParse(payload, out var reputations));
        Assert.Collection(reputations,
            item =>
            {
                Assert.Equal("fc_rh_grp", item.FactionId);
                Assert.Equal(-0.25f, item.Value);
            },
            item =>
            {
                Assert.Equal("li_n_grp", item.FactionId);
                Assert.Equal(0.75f, item.Value);
            });
    }

    [Theory]
    [InlineData("[Player]\nhouse = not-a-number, li_n_grp\n")]
    [InlineData("[Player]\nhouse = 1.01, li_n_grp\n")]
    [InlineData("[Player]\nhouse = 0.5, invalid faction\n")]
    [InlineData("[Player]\nhouse = 0.5, li_n_grp\nhouse = -0.5, li_n_grp\n")]
    [InlineData("[mplayer]\nhouse = 0.5, li_n_grp\n")]
    public void RejectsMalformedOrMissingPlayerReputationData(string saveGame)
    {
        Assert.False(SaveGameFactionReputationParser.TryParse(Encoding.Latin1.GetBytes(saveGame), out _));
    }
}
