using System.Globalization;
using MineClaw.Data;

namespace MineClaw.Data.Tests;

/// <summary>
/// Percentages must read the same on every host. "P0" follows the current culture: "91%" for en-US (the Windows dev box)
/// but "91 %" for the invariant culture a Linux service runs under (LANG=C.UTF-8), so chat, prompts and tests changed with
/// the machine (four tests failed on Linux for that reason alone).
/// </summary>
public class PercentTests
{
    public static TheoryData<string> Cultures => new() { "", "en-US", "fr-FR", "de-DE" };

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Percent_reads_the_same_in_every_culture(string culture)
    {
        InCulture(culture, () =>
        {
            Assert.Equal("91%", Percent.Of(0.91));
            Assert.Equal("0%", Percent.Of(0));
            Assert.Equal("100%", Percent.Of(1.0));
            Assert.Equal("67%", Percent.Of(2 / 3.0));
            Assert.Equal("5%", Percent.Of(0.05f));
        });
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Drop_chance_reads_the_same_in_every_culture(string culture)
    {
        var sapling = new Drop(Blocks.Sapling, 0, 1, 1, 0.05f);
        InCulture(culture, () => Assert.EndsWith("(5%)", sapling.ToString()));
    }

    private static void InCulture(string name, Action body)
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = name == "" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name);
            body();
        }
        finally { CultureInfo.CurrentCulture = old; }
    }
}
