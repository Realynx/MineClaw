using System.Text.RegularExpressions;

/// <summary>One spatial question with a grader for the model's ANSWER line.</summary>
sealed record Question(string Id, string Text, string Expected, Func<string, bool> Grade);

/// <summary>
/// Ten questions about the island in data/alice-snap/mineclaw3.db, bot at 4,64,-3. Ground truth read off the full-res grid
/// (`dotnet run --project tools/VisionAB -- grid`): house x7..16 z-12..-4 with doors at 12..13,-4; double chest 14..15,-5;
/// furnaces 11,-11 12,-11 8,-8; raised 6x8 block area x-1..4 z-16..-9; 3-wide walkway z-1..1 west of x-4 to the door row at
/// x=-9 (void at z=-2 there); gen lava x5..6 z7..8 with a 2x2 pool at x-1..0 z8..9.
/// </summary>
static class QuestionSet
{
    private static string N(string s) => s.ToUpperInvariant();
    private static bool Has(string s, params string[] all) => all.All(a => Regex.IsMatch(s, $@"(?<![\d-]){Regex.Escape(a)}(?!\d)"));
    private static bool Dir(string s, string d) => Regex.IsMatch(N(s), $@"\b({d}|{d switch { "N" => "NORTH", "E" => "EAST", "S" => "SOUTH", _ => "WEST" }})\b");

    public static readonly List<Question> All =
    [
        new("q1", "which direction from you is the cobble generator? answer N, E, S or W", "S", a => Dir(a, "S") && !Dir(a, "N")),
        new("q2", "west of x=-4 there's a walkway out to the door row. how many blocks wide is it (north-south)? answer a number", "3", a => Has(a, "3")),
        new("q3", "is there void directly south of the house with the chests in it? answer yes or no", "no", a => N(a).Contains("NO")),
        new("q4", "standing at the west door row (x=-9, z=-1), how many blocks north until the void? answer a number", "1", a => Has(a, "1") && !Has(a, "2")),
        new("q5", "I want 2 more rows of floor along the north side of that west walkway (west of x=-4). which z values do the 2 new rows go at? answer z1,z2",
            "-2,-3", a => Has(a, "-2") && Has(a, "-3")),
        new("q6", "the small 2x2 water pool near the generator: is it east or west of the generator's lava? answer E or W", "W", a => Dir(a, "W") && !Dir(a, "E")),
        new("q7", "the double chest in the house is against which wall of the house? answer N, E, S or W", "S (SE corner)", a => Dir(a, "S") || N(a).Contains("SE")),
        new("q8", "where is the house's front door: which wall, and at which x? answer wall,x", "S, x=12..13", a => Dir(a, "S") && (Has(a, "12") || Has(a, "13"))),
        new("q9", "the raised block area NNW of you (around x=1, z=-12): how big is it? answer EWxNS in blocks", "6x8", a => Has(a, "6") && Has(a, "8")),
        new("q10", "how many furnaces are inside the house? answer a number", "3", a => Has(a, "3")),
    ];
}
