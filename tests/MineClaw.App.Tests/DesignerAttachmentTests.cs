using System.Text.Json;
using MineClaw.Bot.Blueprints;
using MineClaw.Data;

namespace MineClaw.App.Tests;

/// <summary>
/// Wall torches and ladders on the Design Studio canvas hang on their wall: the AI's spelling names the side the supporting
/// block is on, and every apply turns an inverted one around (or stands a wall-less torch on the floor), saying so.
/// </summary>
public class DesignerAttachmentTests
{
    private static JsonElement E(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void An_inverted_wall_torch_is_turned_to_its_wall_and_the_tool_says_so()
    {
        var c = new DesignerCanvas();
        c.Apply([new CanvasCell(10, 1, 10, Blocks.Cobblestone, 0)]);
        // Meta 1 hangs on a block to the west (x 8): open; the block is east (x 10): meta 2.
        var (text, op, err) = DesignerTools.Run(c, "place", E(new { b = "torch:1", at = new[] { new[] { 9, 1, 10 } } }));
        Assert.False(err, text);
        Assert.Equal((Blocks.Torch, 2), c.Get(9, 1, 10));
        Assert.Equal(2, op!.Cells.Single().Meta); // the browser gets the corrected state
        Assert.Contains("turned 1 wall torch around", text);
        Assert.Single(c.LastFixed);
        // One on the right side stays as it is.
        c.Apply([new CanvasCell(11, 1, 10, Blocks.Torch, 1)]);
        Assert.Empty(c.LastFixed);
        Assert.Equal((Blocks.Torch, 1), c.Get(11, 1, 10));
    }

    [Fact]
    public void A_wall_torch_with_no_wall_over_solid_ground_stands_on_the_floor_and_one_over_air_is_left_for_the_checks()
    {
        var c = new DesignerCanvas();
        c.Apply([new CanvasCell(20, 0, 20, Blocks.Torch, 3)]); // y 0 stands on the grass (ground below)
        Assert.Equal((Blocks.Torch, 5), c.Get(20, 0, 20));
        c.Apply([new CanvasCell(30, 3, 30, Blocks.Torch, 1)]); // in the air: nothing to hang on
        Assert.Equal((Blocks.Torch, 1), c.Get(30, 3, 30));
        // A wall built after the torch, on its other side: the torch turns to it.
        c.Apply([new CanvasCell(31, 3, 30, Blocks.Planks, 0)]);
        Assert.Equal((Blocks.Torch, 2), c.Get(30, 3, 30));
        // Below ground: undisturbed earth holds a torch.
        c.Apply([new CanvasCell(40, -2, 40, 0, 0), new CanvasCell(40, -1, 40, 0, 0)]);
        c.Apply([new CanvasCell(40, -2, 40, Blocks.Torch, 2)]); // x 41 is earth
        Assert.Equal((Blocks.Torch, 2), c.Get(40, -2, 40));
    }

    [Fact]
    public void An_inverted_ladder_is_turned_around()
    {
        var c = new DesignerCanvas();
        c.Apply([new CanvasCell(5, 1, 6, Blocks.Cobblestone, 0)]); // south of the ladder
        c.Apply([new CanvasCell(5, 1, 5, Blocks.Ladder, BuildBlocks.LadderMeta(Dir.North))]);
        Assert.Equal((Blocks.Ladder, BuildBlocks.LadderMeta(Dir.South)), c.Get(5, 1, 5));
        Assert.Contains("ladder", DesignerCanvas.FixText(c.LastFixed));
    }

    [Fact]
    public void Named_spellings_say_where_the_supporting_block_is()
    {
        Assert.Equal((Blocks.Torch, 1), DesignerTools.Resolve("torch:wall=west"));
        Assert.Equal((Blocks.Torch, 2), DesignerTools.Resolve("torch:on=east"));
        Assert.Equal((Blocks.Torch, 3), DesignerTools.Resolve("torch:wall=north"));
        Assert.Equal((Blocks.Torch, 4), DesignerTools.Resolve("torch:south"));
        Assert.Equal((Blocks.Torch, 5), DesignerTools.Resolve("torch:floor"));
        Assert.Equal((Blocks.Torch, 1), DesignerTools.Resolve("torch:1")); // numeric metas still work
        Assert.Equal((Blocks.Torch, 5), DesignerTools.Resolve("torch"));
        Assert.Equal((Blocks.Ladder, BuildBlocks.LadderMeta(Dir.East)), DesignerTools.Resolve("ladder:wall=east"));
        Assert.Equal((Blocks.Ladder, BuildBlocks.LadderMeta(Dir.South)), DesignerTools.Resolve("ladder:south"));
        Assert.Throws<ArgumentException>(() => DesignerTools.Resolve("torch:wall=up"));
        // Every named torch side hangs on the block that side of it.
        foreach (var (side, dx, dz) in new[] { ("west", -1, 0), ("east", 1, 0), ("north", 0, -1), ("south", 0, 1) })
        {
            var (id, meta) = DesignerTools.Resolve("torch:wall=" + side);
            Assert.Equal((dx, dz), DesignerCanvas.WallOf(id, meta));
        }
        var doc = DesignerTools.System("poofimafox");
        Assert.DoesNotContain("1 west 2 east", doc);
        Assert.DoesNotContain("torch:1-4", doc);
        Assert.Contains("torch:wall=west hangs on the solid block to its west", doc);
    }

    [Fact]
    public void A_canvas_saved_with_floating_torches_is_settled_when_it_opens()
    {
        var c = new DesignerCanvas();
        c.Apply([new CanvasCell(10, 1, 10, Blocks.Cobblestone, 0)]);
        // An old session's encoding with the inverted torch (saved before the rule): decoded as it was.
        var states = DesignerCanvas.Decode(c.Encode(), c.Rev).Encode() + $"{Blocks.Torch}:1=1,10:9\n";
        var old = DesignerCanvas.Decode(states, 4);
        Assert.Equal((Blocks.Torch, 1), old.Get(9, 1, 10));
        var fixedCells = old.FixAllAttachments();
        Assert.Single(fixedCells);
        Assert.Equal((Blocks.Torch, 2), old.Get(9, 1, 10));
        Assert.Equal(5, old.Rev);
        Assert.Empty(old.FixAllAttachments()); // settled: nothing more
    }
}
