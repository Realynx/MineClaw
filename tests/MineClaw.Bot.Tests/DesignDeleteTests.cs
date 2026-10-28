using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28: Robin said "no" at design #9's gate (a carousel by the cobble gen: 5 gate blocks), a copy was built elsewhere as
/// #10, a restart offered #9 as "no second go needed" and main put its gate blocks by the gen again; a worker told to "remove the
/// 5 blocks just placed" couldn't find them. Fox: "You should be able to delete plans." These pin design delete/undo (only the
/// plan's own recorded placements, never a protected block), the held-by-player gate and the copy cancelling its original.
/// </summary>
public class DesignDeleteTests
{
    /// <summary>Design #9's live plan: these 5 placed (= gate) blocks, origin 1,65,1, paused, gate Waiting.</summary>
    private static readonly BlockPos[] Gate9 = [new(3, 65, 3), new(4, 65, 3), new(5, 65, 3), new(6, 65, 3), new(4, 65, 4)];

    private const string Carousel = "blueprint carousel\nsize 8x1x8\npalette C=cobblestone\nfloor C 0,0,0 7,0,7";

    private sealed record Fixture(BlueprintService S, BlueprintStore Store, WorldState W, SimBuildBody Body, Dictionary<BlockPos, int> Before);

    private static Fixture Setup(AgentDb db, ConstructionExecutor? executor = null)
    {
        var w = BlueprintTests.Island(8, 64);
        // The cobble gen right beside the carousel: walls and fluids (protected, never part of the plan).
        w.SetBlock(7, 65, 3, Blocks.Cobblestone, 0);
        w.SetBlock(8, 65, 3, Blocks.Lava, 0);
        w.SetBlock(8, 65, 5, Blocks.Water, 0);
        var store = new BlueprintStore(db);
        var body = new SimBuildBody(w, new() { [(Blocks.Cobblestone, 0)] = 64 });
        var s = new BlueprintService(new BlueprintOptions(), store, new BlueprintPreview())
        {
            Site = () => BlueprintTests.Site(w, inv: new() { [Blocks.Cobblestone] = 999 }), Crew = () => new SingleBodyCrew(body),
            ResolveSite = (at, _) => (new SitePick(new BlockPos(-4, 64, -4), at ?? "here"), null), Trusted = () => ["PoofImaFox", "kiwi"],
            Executor = executor ?? new ConstructionExecutor(new BlueprintOptions(), store),
        };
        s.StartBuild = (_, _) => null;
        // Ids 1..8 are other designs; #9 is the carousel being built at 1,65,1 (kiwi approved it).
        for (int i = 1; i <= 8; i++) store.Insert(new BlueprintRecord { Name = $"thing {i}", Dsl = "size 1x1x1\npalette C=cobblestone\nlayer 0\nC", Status = DesignStatus.Built, Bot = "maid_chan" });
        var rec = store.Insert(new BlueprintRecord { Name = "carousel", Dsl = Carousel, Status = DesignStatus.Building, Bot = "maid_chan", Requester = "kiwi" });
        Assert.Equal(9, rec.Id);
        rec.Origin = new BlockPos(1, 65, 1);
        rec.AcceptedBy = "kiwi";
        store.Update(rec);
        var (plan, _) = BlueprintNormalizer.FromText(Carousel, new BlockPos(1, 65, 1));
        var now = DateTimeOffset.UtcNow;
        foreach (var p in Gate9)
        {
            w.SetBlock(p.X, p.Y, p.Z, Blocks.Cobblestone, 0);
            w.Placed.Note(p, 0, Blocks.Cobblestone, "maid_chan", now);
        }
        var bp = new BuildPlan
        {
            BlueprintId = 9, Part = 0, Parts = 1, Version = plan!.Hash(), Format = BuildPlan.CurrentFormat, Origin = new BlockPos(1, 65, 1), State = "paused",
            Gate = new GateInfo { State = GateState.Waiting, Blocks = Gate9.ToList(), Question = "First 5 blocks down: the carousel ... Keep going?" },
            Placed = Gate9.ToList(),
        };
        s.Plans!.Save(bp);
        store.SaveBuild(new BuildRecord { BlueprintId = 9, Part = 0, Parts = 1, Owner = body.Name, State = "building", Origin = bp.Origin, Placed = 5, Total = 64 });
        var before = new Dictionary<BlockPos, int>();
        for (int x = -9; x <= 9; x++) for (int y = 62; y <= 68; y++) for (int z = -9; z <= 9; z++) before[new BlockPos(x, y, z)] = w.GetBlock(x, y, z);
        return new Fixture(s, store, w, body, before);
    }

    [Fact]
    public async Task Delete_takes_down_exactly_the_plans_own_blocks_cancels_it_and_hides_the_design()
    {
        using var db = AgentDb.Open(null);
        var f = Setup(db);
        // Only a trusted player's word deletes (like a build's go).
        var (no, why) = await f.S.DeleteAsync("9", "robinchan", CancellationToken.None);
        Assert.False(no);
        Assert.Contains("trusted", why);
        Assert.Equal(Blocks.Cobblestone, f.W.GetBlock(3, 65, 3));

        var (ok, text) = await f.S.DeleteAsync("9", "kiwi", CancellationToken.None);
        Assert.True(ok, text);
        Assert.Equal("deleted design #9: removed 5 blocks at 3,65,3 4,65,3 5,65,3 6,65,3 4,65,4; plan cancelled", text);
        foreach (var (p, id) in f.Before) Assert.Equal(Gate9.Contains(p) ? 0 : id, f.W.GetBlock(p)); // those 5, nothing else (the gen stands)
        Assert.Equal(64 + 5, f.Body.Count(Blocks.Cobblestone, 0)); // refunded

        // Nothing resumes it: no plan, no lane, no unfinished build, no startup wake or pending notice; gone from the list.
        Assert.Empty(f.S.Plans!.ForBlueprint(9));
        Assert.Empty(f.S.Plans.AtGate());
        Assert.DoesNotContain(f.Store.Unfinished(f.Body.Name), b => b.BlueprintId == 9);
        Assert.Null(f.S.NotifyPending(f.Body.Name, notice: false));
        Assert.Null(f.S.ResumePending(f.Body.Name));
        Assert.DoesNotContain(f.Store.List(20), r => r.Id == 9);
        Assert.Null(f.Store.Find("carousel"));
        // The record (DSL history) stays for audit.
        var rec = f.Store.Get(9)!;
        Assert.Equal(DesignStatus.Deleted, rec.Status);
        Assert.Equal(Carousel, rec.Dsl);
        var (again, againText) = await f.S.DeleteAsync("9", "kiwi", CancellationToken.None);
        Assert.False(again);
        Assert.Contains("already deleted", againText);
    }

    [Fact]
    public async Task Undo_and_delete_never_dig_a_protected_block_even_one_in_the_ledger()
    {
        using var db = AgentDb.Open(null);
        var f = Setup(db);
        // The gen wall at 7,65,3 got into the ledger (say we patched it once): an undo still leaves it, and the gen's fluids.
        var bp = f.S.Plans!.Get(9, 0)!;
        bp.Placed.Add(new BlockPos(7, 65, 3));
        f.W.Placed.Note(new BlockPos(7, 65, 3), 0, Blocks.Cobblestone, "maid_chan", DateTimeOffset.UtcNow);
        f.S.Plans.Save(bp);
        f.S.KeepOnUndo = (p, _) => p.X >= 7 && p.X <= 8 && p.Z >= 3 && p.Z <= 5; // the generator's walls and fluids
        var (ok, text) = await f.S.UndoAsync("9", CancellationToken.None);
        Assert.True(ok, text);
        Assert.Contains("took down 5 blocks at 3,65,3 4,65,3 5,65,3 6,65,3 4,65,4", text);
        Assert.Contains("left 1 (protected or refused) at 7,65,3", text);
        foreach (var (p, id) in f.Before) Assert.Equal(Gate9.Contains(p) ? 0 : id, f.W.GetBlock(p));
        Assert.Equal(Blocks.Lava, f.W.GetBlock(8, 65, 3));
        // The lighter op: the plan is reset (no resume offer), the design stays buildable.
        Assert.Empty(f.S.Plans.ForBlueprint(9));
        Assert.Null(f.S.NotifyPending(f.Body.Name, notice: false));
        Assert.Equal(DesignStatus.Accepted, f.Store.Get(9)!.Status);
    }

    [Fact]
    public async Task A_no_at_the_gate_holds_the_build_so_no_restart_offers_or_resumes_it()
    {
        using var db = AgentDb.Open(null);
        var gates = new BuildGates();
        var store = new BlueprintStore(db);
        var asked = new List<string>();
        var ex = new ConstructionExecutor(new BlueprintOptions { GateMinutes = 1 }, store)
        {
            Gates = gates,
            Ask = q =>
            {
                asked.Add(q);
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 400 && !gates.IsWaiting(9); i++) await Task.Delay(5);
                    gates.Answer(9, BuildGate.Parse("no", "kiwi", default, 'x')!);
                });
                return Task.CompletedTask;
            },
        };
        var f = Setup(db, ex);
        // A fresh build of #9 (its old plan gone): gates, kiwi says no.
        f.S.Plans!.Delete(9);
        foreach (var p in Gate9) f.W.SetBlock(p.X, p.Y, p.Z, 0, 0);
        var (_, text) = await f.S.BuildAsync("9", null, null, null, CancellationToken.None);
        Assert.Single(asked);
        Assert.Contains("kiwi said \"no\"", text);
        Assert.Contains("undo with design undo{id=9}", text);
        Assert.Equal("kiwi", f.S.HeldBy(9));
        // The restart: never offered as "no second go needed", never resumed on its own.
        var pending = f.S.NotifyPending(f.Body.Name, notice: false)!;
        Assert.Contains("is held: kiwi said no at its gate", pending);
        Assert.DoesNotContain("no second go needed", pending);
        Assert.Null(f.S.ResumePending(f.Body.Name));
        // A resume anyway (a worker's build{bp}) stops before any block.
        int placed = f.Body.Placed.Count;
        var (_, again) = await f.S.BuildAsync("9", null, null, null, CancellationToken.None);
        Assert.Contains("held: kiwi said no", again);
        Assert.Equal(placed, f.Body.Placed.Count);
        Assert.Single(asked);
        // A trusted fresh go lifts it.
        f.S.ClearHold(9);
        Assert.Null(f.S.HeldBy(9));
    }

    [Fact]
    public void Copying_a_design_to_a_new_site_cancels_the_originals_unfinished_build_and_names_its_blocks()
    {
        using var db = AgentDb.Open(null);
        var f = Setup(db);
        var (copy, text) = f.S.Reuse(f.Store.Get(9)!, "over there", "kiwi");
        Assert.NotNull(copy);
        Assert.Equal(10, copy!.Id);
        Assert.Contains("#9's 5 blocks still stand at 3,65,3 4,65,3 5,65,3 6,65,3 4,65,4; undo with design undo{id=9}", text);
        Assert.Equal(DesignStatus.Cancelled, f.Store.Get(9)!.Status);
        Assert.DoesNotContain(f.Store.Unfinished(f.Body.Name), b => b.BlueprintId == 9);
        Assert.DoesNotContain(f.S.Plans!.AtGate(), p => p.BlueprintId == 9);
        Assert.Equal(5, f.S.PlacedCount(9)); // the ledger stays for the undo
        foreach (var p in Gate9) Assert.Equal(Blocks.Cobblestone, f.W.GetBlock(p)); // nothing dug by the copy
    }
}
