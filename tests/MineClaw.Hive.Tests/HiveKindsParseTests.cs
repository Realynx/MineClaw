using MineClaw.Hive.Hierarchy;

namespace MineClaw.Hive.Tests;

/// <summary>Pins <see cref="HiveKinds.ParseKind"/> (WP07: the switch became a table): every word, case and '_' spellings, blanks, unknowns.</summary>
public class HiveKindsParseTests
{
    [Theory]
    [InlineData("hive", CloneKind.Hive)] [InlineData("brain", CloneKind.Hive)] [InlineData(" HIVE ", CloneKind.Hive)] [InlineData("Brain", CloneKind.Hive)]
    [InlineData("subhive", CloneKind.Subhive)] [InlineData("sub-hive", CloneKind.Subhive)] [InlineData("sub_hive", CloneKind.Subhive)] [InlineData("sub", CloneKind.Subhive)]
    [InlineData("body", CloneKind.Subhive)] [InlineData("Sub_Hive", CloneKind.Subhive)]
    [InlineData("script", CloneKind.Script)] [InlineData("script-only", CloneKind.Script)] [InlineData("script_only", CloneKind.Script)] [InlineData("scriptonly", CloneKind.Script)]
    [InlineData("\tSCRIPT\n", CloneKind.Script)]
    public void Known_words_parse(string s, CloneKind kind) => Assert.Equal(((CloneKind?)kind, (string?)null), HiveKinds.ParseKind(s));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")]
    public void Blank_is_no_kind(string? s) => Assert.Equal(((CloneKind?)null, (string?)null), HiveKinds.ParseKind(s));

    [Theory]
    [InlineData("hives", "kind 'hives' is hive|subhive|script")] [InlineData(" Sub Hive ", "kind 'sub hive' is hive|subhive|script")]
    [InlineData("SCRIPT__ONLY", "kind 'script--only' is hive|subhive|script")] [InlineData("bodies", "kind 'bodies' is hive|subhive|script")]
    public void Unknown_words_say_so(string s, string error) => Assert.Equal(((CloneKind?)null, (string?)error), HiveKinds.ParseKind(s));
}
