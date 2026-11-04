using System.Text;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the small model- and player-facing readers of the build slicer: <see cref="BuildSkips.Category"/> (every reason group, in
/// both world modes) and <see cref="BuildGate.Parse"/> (every verdict, turn, mirror and move form). A refactor must leave
/// <see cref="BuildingGoldenData.Expected"/> unchanged. Regenerate (only for an intended change) by setting WP02_GOLDEN_OUT.
/// </summary>
public class BuildingGoldenTests
{
    public static readonly string?[] Reasons =
    [
        null, "", "   ", "not reached: cancelled", "Not Reached", "waiting on supports", "waits for an earlier step",
        BuildSkips.AboveReach + " (I can't climb)", BuildSkips.NotNear, "no support within 6 scaffold blocks of 1,2,3",
        "no stand reaches scaffold cell 4,5,6", "no stand within reach of a support face", "no stand", "needs a water bucket to go 6 down",
        "water descent blocked at 1,2,3", "climbing back up fails", "no edge with open space for a column", "it would wall me in",
        "I'm not standing on anything", "protected", "protected by policy", "someone built there", "not mine: someone's",
        "not ours", "wrong block", "missing cobblestone slab (need 40, have 0): ask", "missing planks", "missing #12 thing 1,2,3 here",
        "waits for another lane's step", "needs step #12 (skipped)", "waited too long: nothing ready", "goto: no path",
        "jammed", "nothing solid on any side (1,2,3)", "placed at -1,64,-3 but it fell; retry later", "x" + new string('y', 80),
        "  waiting on supports  ", "#4 failed 12 times", "MISSING Glass", "Someone",
    ];

    public static readonly string[] Answers =
    [
        "", "  ", "yes", "Yes please", "ok, keep going", "looks good!", "that's it thanks", "keep going", "carry on please",
        "no", "nope", "stop", "wait", "hold on", "not there", "wrong spot", "undo", "undo it", "take it all down", "tear it down",
        "wrong way", "other direction", "backwards", "reverse it", "turn it around", "180", "turn it", "turn it left", "rotate right",
        "counterclockwise", "anti-clockwise", "turn counter-clockwise", "flip it", "mirrored", "mirror it", "move 2 east",
        "move it 3 north", "shift it two west", "move a block up", "put it over 1 down", "push the hut five south",
        "3 blocks further east", "2 down", "move left", "move it right", "turn it and move 2 east", "wrong way and flip it",
        "yes but move 1 up", "move 99 west", "whatever", "turn", "move it to the east", "move 4 more north and 2 east",
    ];

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var mode in new[] { WorldMode.SkyblockWorld, WorldMode.Normal })
            using (WorldMode.Override(mode))
                foreach (var r in Reasons)
                    sb.Append("cat ").Append(mode.Name).Append(" [").Append(r ?? "<null>").Append("] => ").Append(BuildSkips.Category(r)).Append('\n');
        foreach (var a in Answers)
        {
            var g = BuildGate.Parse(a, "PoofImaFox", new BlockPos(1, 64, -2), 'z');
            sb.Append("gate [").Append(a).Append("] => ").Append(g == null ? "null" : g.ToString()).Append('\n');
        }
        DumpQuestions(sb);
        return sb.ToString();
    }

    /// <summary>The gate question over designs along x and z, gate blocks at either end (or none), landmark and place lookups that
    /// name the build itself, a place inside its footprint, a preview link and a corrected (turned) plan.</summary>
    private static void DumpQuestions(StringBuilder sb)
    {
        var designs = new (string Name, string Dsl, BlockPos At)[]
        {
            ("bridge", "size 6x1x1\npalette C=cobblestone\nlayer 0\nCCCCCC", new BlockPos(0, 64, 0)),
            ("mob drop tower", "size 2x5x7\npalette C=cobblestone\nbox C 0,0,0 1,4,6", new BlockPos(3, 64, -3)),
            ("dot", "size 1x1x1\npalette C=cobblestone\nlayer 0\nC", new BlockPos(5, 64, 5)),
        };
        Func<BlockPos, string?>[] landmarks =
        [
            _ => null,
            p => p.X < 3 ? "by the well" : p.Z < 0 ? "north of the farm" : "at mob_drop_tower",
            p => p.X + p.Z > 3 ? "near the bridge" : "by the well",
        ];
        Func<BlockPos, NamedPlace?>?[] places =
        [
            null,
            p => p.X < 3 ? new NamedPlace("well", new BlockPos(-4, 64, 0)) : new NamedPlace("farm", new BlockPos(20, 64, 3)),
            p => new NamedPlace("Mob Drop Tower", new BlockPos(0, 64, 0)),
            p => new NamedPlace("hangout", new BlockPos(4, 64, 0)),
            p => p.Z > 0 ? new NamedPlace("chests", new BlockPos(3, 64, 9)) : null,
        ];
        foreach (var (name, dsl, at) in designs)
        {
            var plan = BlueprintNormalizer.FromText(dsl, at).Plan!;
            var cells = plan.Blocks.Keys.OrderBy(p => p.X).ThenBy(p => p.Z).ThenBy(p => p.Y).ToList();
            var gates = new List<List<BlockPos>> { new(), cells.Take(3).ToList(), Enumerable.Reverse(cells).Take(2).ToList() };
            foreach (var gate in gates)
                for (int l = 0; l < landmarks.Length; l++)
                    for (int k = 0; k < places.Length; k++)
                        foreach (var turned in new[] { false, true })
                        {
                            var bp = new BuildPlan { Gate = { Blocks = gate.ToList() }, Transform = turned ? new BuildTransform(1) : BuildTransform.None };
                            var q = BuildGate.Question(bp, plan, name, landmarks[l], places[k], k == 1 ? "/view/buildplan/12" : null);
                            sb.Append($"q {name} g{gate.Count} l{l} p{k} {(turned ? "t" : "-")} => ").Append(q).Append('\n');
                        }
        }
    }

    [Fact]
    public void Skip_categories_and_gate_answers_match_the_golden_dump()
    {
        var actual = Dump();
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(BuildingGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
