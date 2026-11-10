using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Which furnaces the auto bills may use: one with fuel to hand (stored within 256 blocks after the fetches on their way, or carried by
/// a pawn in game). The stock is gathered once per pass for every furnace (live 11-10: asked per furnace and per fuel, 106 ms a pass).
/// </summary>
public sealed class StoryFurnaceFuelTests
{
    private static readonly P3 Near = new(0, 64, 9), Far = new(300, 64, 9);

    /// <summary>Two furnaces, sand in a chest by them, a build that wants glass; <paramref name="coalAt"/>: a chest with 8 coal.</summary>
    private static (StoryColony C, FakeStoryHost H) Rig(P3? coalAt, bool pocket = false)
    {
        var (c, h, w) = StoryRig.Make(pawns: 2, data: Release1122GameData.Instance);
        foreach (var at in new[] { Near, Far })
        {
            c.StorageOp("set", [at], null, null, null);
            w.Set(at.X, at.Y, at.Z, 54);
        }
        c.AddContents(c.StorageAt(Near)!, new Stack(12, 0, 8));
        if (coalAt is { } ca) c.AddContents(c.StorageAt(ca)!, new Stack(263, 0, 8));
        if (pocket) h.Lives[c.P(1)] = FakeStoryHost.Live(0, 64, 0, inv: [new Stack(263, 0, 8)]);
        for (int i = 0; i < 2; i++)
        {
            var at = new P3(3 + i * 2, 64, 0);
            w.Set(at.X, at.Y, at.Z, 61);
            Assert.Null(c.WorkbenchOp("designate", at).Error);
        }
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["material"] = "glass",
            ["blocks"] = new JsonArray(Enumerable.Range(0, 8).Select(i => (JsonNode)new JsonArray(i, 64, 20)).ToArray()),
        }), null).Error);
        c.RefreshMaterials();
        return (c, h);
    }

    private static int Smelting(StoryColony c) => c.Benches.Count(b => b.Tasks.Count > 0);

    [Fact]
    public void Coal_in_a_chest_nearby_fuels_every_furnace() => Assert.Equal(2, Smelting(Rig(Near).C));

    [Fact]
    public void Coal_only_in_a_pawns_pocket_fuels_them() => Assert.Equal(2, Smelting(Rig(null, pocket: true).C));

    [Fact]
    public void Coal_only_beyond_256_blocks_fuels_none() => Assert.Equal(0, Smelting(Rig(Far).C));

    [Fact]
    public void No_coal_anywhere_fuels_none() => Assert.Equal(0, Smelting(Rig(null).C));
}
