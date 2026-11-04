using System.Text;
using System.Text.Json.Nodes;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="StoryDirectives.Render"/> (WP07; the line a brain gets for a Story Mode directive) over every kind and an unknown
/// one, with an area, spot targets (none, one, several) or neither; a count (none, one, many); text (none, blank, a note, a long
/// one that hits the cap); a mine filter; a build design with and without a name and a described build; a craft bill.
/// </summary>
public class StoryDirectiveRenderGoldenTests
{
    private static readonly string[] Kinds =
        ["chop", "cut", "harvest", "mine", "haul", "hunt", "deconstruct", "build", "move", "attack", "guard", "craft", "text", "dance"];

    private static IEnumerable<(string Label, JsonObject D)> Shapes()
    {
        yield return ("area", new JsonObject { ["area"] = new JsonArray(-1200, 64, 250, -1180, 70, 270) });
        yield return ("bad area", new JsonObject { ["area"] = new JsonArray(1, 2) });
        yield return ("no targets", new JsonObject { ["targets"] = new JsonArray() });
        yield return ("one target", new JsonObject { ["targets"] = new JsonArray(new JsonArray(1, 64, 2)) });
        yield return ("three targets", new JsonObject { ["targets"] = new JsonArray(1, 2, 3) });
        yield return ("nothing", new JsonObject());
        yield return ("text", new JsonObject { ["text"] = "  come to the farm please  " });
        yield return ("blank text", new JsonObject { ["text"] = "   " });
        yield return ("long text", new JsonObject { ["area"] = new JsonArray(0, 0, 0, 1, 1, 1), ["text"] = new string('w', 260) });
        yield return ("filter", new JsonObject { ["filter"] = "iron_ore", ["area"] = new JsonArray(0, 60, 0, 4, 64, 4) });
        yield return ("filter all", new JsonObject { ["filter"] = "all" });
        yield return ("build named", new JsonObject { ["build"] = new JsonObject { ["name"] = "hut", ["kind"] = "wall" } });
        yield return ("build described", new JsonObject
        {
            ["build"] = new JsonObject { ["kind"] = "wall", ["material"] = "cobblestone", ["height"] = 3, ["roof"] = new JsonObject { ["style"] = "gable" } },
        });
        yield return ("build bare", new JsonObject { ["build"] = new JsonObject { ["roof"] = new JsonObject { ["style"] = "none" }, ["material"] = 5 } });
        yield return ("build not an object", new JsonObject { ["build"] = "wall" });
        yield return ("bill", new JsonObject { ["bill"] = new JsonObject { ["recipe"] = "craft:stick", ["target"] = 8 } });
        yield return ("smelt bill", new JsonObject { ["bill"] = new JsonObject { ["recipe"] = "smelt:iron_ingot" } });
        yield return ("bill without recipe", new JsonObject { ["bill"] = new JsonObject { ["target"] = 2 } });
    }

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var kind in Kinds)
            foreach (var (label, d) in Shapes())
                foreach (var (count, design) in new (int?, long?)[] { (null, null), (1, null), (14, 57) })
                    sb.Append(kind).Append(" / ").Append(label).Append(" / ").Append(count?.ToString() ?? "-").Append(' ').Append(design?.ToString() ?? "-").Append(" -> ")
                        .Append(Line(kind, (JsonObject)d.DeepClone(), count, design)).Append('\n');
        return sb.ToString();
    }

    private static string Line(string kind, JsonObject d, int? count, long? design)
    {
        try { return StoryDirectives.Render("poofimafox", kind, d, "dir12", count, design); }
        catch (Exception ex) { return "throws " + ex.GetType().Name; }
    }

    [Fact]
    public void Render_matches_the_golden_dump() => RouteGolden.Check("story-directive-render", StoryDirectiveRenderGoldenData.Expected, Dump());
}
