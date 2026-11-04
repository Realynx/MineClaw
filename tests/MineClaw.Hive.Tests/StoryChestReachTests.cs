using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Unreachable chests (live 11-04: 334 of 462 failed searches in 5 minutes were "can't reach chest at 218,64,400 … no path to use"):
/// a pawn that can't reach a chest leaves it out of its plans; when no pawn can, the colony hears it once and its contents aren't stock.
/// </summary>
public sealed class StoryChestReachTests
{
    private static readonly P3 Near = new(0, 64, 9), Far = new(12, 64, 9);

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(int pawns, int ores)
    {
        var (c, h, w) = StoryRig.Make(pawns);
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Mine ? 1 : 0;
        int i = 0;
        foreach (var p in c.Pawns.Keys) h.Lives[p] = FakeStoryHost.Bare(i++, 64, 0);
        for (int x = 0; x < ores; x++) w.Set(3 + x * 2, 64, 3, 15); // iron ore: a stone pickaxe each time
        c.Designate("add", "mine", Area.Of(3, 64, 3, 3 + ores * 2, 64, 3), null, null);
        return (c, h, w);
    }

    private static void Chest(StoryColony c, P3 at, params Stack[] stock)
    {
        Assert.Null(c.StorageOp("set", [at], null, null, null).Error);
        foreach (var s in stock) c.AddContents(c.StorageAt(at)!, s);
    }

    private static string? From(JsonObject j) => j["fetch"] is JsonArray a && a.Count > 0 ? a[0]!["from"]!.GetValue<string>() : null;

    private static string Unreach(P3 at) => $"can't reach chest at {at}: no path to use it (no route)";

    [Fact]
    public void A_pawn_that_cant_reach_a_chest_takes_its_pickaxe_from_another()
    {
        var (c, _, _) = Rig(1, 1);
        Chest(c, Near, new Stack(274, 0, 2));
        Chest(c, Far, new Stack(274, 0, 2));
        var j = c.Next();
        Assert.Equal(Near.ToString(), From(j));
        Assert.True(c.Report(c.P(), j["id"]!.GetValue<string>(), "failed", Unreach(Near), null));
        Assert.Equal(0, c.Designations.Single().Fails); // the chest's trouble, not the ore's
        var again = c.Next();
        Assert.Equal(Far.ToString(), From(again)); // that chest is left out of its plans
        Assert.DoesNotContain("resting", again["label"]!.GetValue<string>());
    }

    [Fact]
    public void A_chest_no_pawn_reaches_is_said_once_and_each_pawn_tries_it_once()
    {
        var (c, _, _) = Rig(3, 10);
        Chest(c, Near, new Stack(274, 0, 10)); // the only pickaxes, behind a wall
        int tries = 0;
        for (int round = 0; round < 10; round++)
            for (int i = 0; i < 3; i++)
            {
                var j = c.Next(i);
                if (From(j) == Near.ToString())
                {
                    tries++;
                    c.Report(c.P(i), j["id"]!.GetValue<string>(), "failed", Unreach(Near), null);
                }
                else c.Report(c.P(i), j["id"]!.GetValue<string>(), "done", null, null);
            }
        Assert.Equal(3, tries); // one search each, not one per ore per pawn (the A* that ran to its budget 334 times in 5 minutes)
        Assert.Single(c.Alerts, a => a.Text == $"Chest at {Near} is unreachable — pawns can't get to it");
        Assert.All(c.Designations, d => Assert.Equal(0, d.Fails));
        Assert.StartsWith("needs a stone pickaxe", c.Designations.First().Wait); // its pickaxes aren't stock: the ores wait for one
    }
}
