using System.Reflection;
using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The haul search's speed-up (StoryColony.ChestChoice.cs) answers as before, and the two hauling loops of 11-10 are gone: a re-sort
/// of tools no longer takes the hauler's own along, and a chest nobody could get to for a look rests from looks for everyone.
/// </summary>
public sealed class StoryHaulChoiceTests
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static MethodInfo M(string name, params Type[] args) => typeof(StoryColony).GetMethod(name, Members, args)!;
    private static void SetField(object o, string name, object? v) => typeof(StoryColony).GetField(name, Members)!.SetValue(o, v);

    /// <summary>A pick's scope as <c>Pick</c> opens it: this pawn picking, the chest choice remembered.</summary>
    private static T InPick<T>(StoryColony c, string? pawn, Func<T> f)
    {
        SetField(c, "_pickPawn", pawn);
        M("BeginChestChoice").Invoke(c, null);
        try { return f(); }
        finally { M("EndChestChoice").Invoke(c, null); SetField(c, "_pickPawn", null); }
    }

    /// <summary>The old way, as it was written before 11-10: every chest asked, then ordered (the reference the answers must match).</summary>
    private sealed class Reference(StoryColony c)
    {
        private readonly MethodInfo _decon = M("ChestDeconstructing", typeof(P3)), _open = M("ChestOpen", typeof(P3)),
            _accepts = M("Accepts", typeof(StorageState), typeof(int), typeof(int)), _room = M("HasRoom", typeof(StorageState), typeof(int), typeof(int)),
            _denied = M("Denied", typeof(StorageState), typeof(int), typeof(int)), _rank = M("StorageRank", typeof(StorageState));

        private bool B(MethodInfo m, params object?[] a) => (bool)m.Invoke(c, a)!;

        public StorageState? Best(int id, int meta, P3 from, StorageState? except) =>
            c.Storages.Where(s => s != except && !B(_decon, s.Chest) && B(_open, s.Chest) && B(_accepts, s, id, meta) && B(_room, s, id, meta))
                .OrderByDescending(s => (int)_rank.Invoke(null, [s])!).ThenBy(s => s.Chest.Dist(from)).FirstOrDefault();

        public StorageState? Fallback(int id, int meta, P3 from, StorageState? except) =>
            c.Storages.Where(s => s != except && s.Signs.Count == 0 && !B(_decon, s.Chest) && B(_open, s.Chest) && B(_room, s, id, meta) && !B(_denied, s, id, meta))
                .OrderBy(s => s.Filter.Allow.Contains("*") ? 0 : 1).ThenBy(s => StoragePriority.Rank(s.Priority)).ThenBy(s => s.Chest.Dist(from))
                .FirstOrDefault();
    }

    /// <summary>
    /// Sixty chests in a grid (so many are the same distance from a point), of every priority and many filters; some full, some
    /// missing, one with a sign, one under a stone block (its lid shut), one marked for deconstruction, some the first pawn couldn't
    /// reach, one nobody could.
    /// </summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, Clock T) Colony()
    {
        var clock = new Clock();
        var (c, h, w) = StoryRig.Make(3, time: clock);
        string[] priorities = ["low", "normal", "preferred", "important", "critical"];
        List<string>[] allows = [["*"], ["*"], ["food"], ["stone_building"], ["tools"], [], ["ores_metals", "stone_building"], ["4"], ["*"]];
        var chests = new List<P3>();
        for (int i = 0; i < 60; i++)
        {
            var at = new P3(10 + i % 10 * 3, 64, 10 + i / 10 * 3);
            w.Set(at.X, at.Y, at.Z, 54);
            chests.Add(at);
            var filter = new StorageFilter { Allow = allows[i % allows.Length], Deny = i % 7 == 3 ? ["stone_stone"] : [] };
            Assert.Null(c.StorageOp("set", [at], filter, priorities[i * 7 % 5], null).Error);
            var s = c.StorageAt(at)!;
            if (i % 6 == 1) c.AddContents(s, new Stack(4, 0, 64 * 27)); // full of cobblestone
            else if (i % 6 == 4) { c.AddContents(s, new Stack(4, 0, 64 * 26 + 10)); c.AddContents(s, new Stack(3, 0, 1)); } // a part stack
        }
        w.Set(chests[13].X, chests[13].Y + 1, chests[13].Z, 1); // its lid shut
        var signed = c.StorageAt(chests[22])!;
        var sign = chests[22].Add(0, 0, -1);
        w.Set(sign.X, sign.Y, sign.Z, 68);
        signed.Signs = [new StorageSignState(sign, ["[storage]", "food"], ["food"], [])];
        Assert.Null(c.Designate("add", "deconstruct", null, [chests[31]], null).Error);
        var p0 = c.Pawns[c.P(0)];
        foreach (int k in new[] { 5, 17, 40, 41 })
            Assert.True(c.ContainerUnreachable(p0, $"deposit into chest ({chests[k]}): can't reach chest at {chests[k]}: no walkable spot that sees it"));
        foreach (var p in c.Pawns.Values)
            c.ContainerUnreachable(p, $"can't reach chest at {chests[50]}: no walkable spot that sees it"); // nobody gets there
        c.Meta.UnfilteredTakesAnything = true;
        return (c, h, w, clock);
    }

    [Fact]
    public void Chests_are_chosen_as_before_in_a_pick_and_outside_one()
    {
        var (c, _, _, _) = Colony();
        var reference = new Reference(c);
        var fallback = M("Fallback", typeof(int), typeof(int), typeof(P3), typeof(StorageState));
        (int Id, int Meta)[] items = [(4, 0), (3, 0), (263, 0), (274, 0), (297, 0), (17, 0), (17, 2), (280, 0), (15, 0), (1, 0), (35, 14)];
        var froms = new List<P3>();
        for (int x = 4; x <= 46; x += 3) for (int z = 4; z <= 34; z += 5) froms.Add(new P3(x, 64, z)); // many on the grid's symmetry lines
        StorageState?[] excepts = [null, c.Storages[0], c.Storages[3], c.Storages[24], c.Storages[59]];
        int asked = 0, differ = 0;
        foreach (var pawn in new string?[] { null, c.P(0), c.P(1) })
            InPick(c, pawn, () =>
            {
                foreach (var (id, meta) in items)
                    foreach (var from in froms)
                        foreach (var except in excepts)
                        {
                            asked++;
                            SetField(c, "_pickPawn", pawn);
                            var want = reference.Best(id, meta, from, except);
                            var got = c.BestChest(id, meta, from, except);
                            if (!ReferenceEquals(want, got)) differ++;
                            Assert.Same(want, got);
                            Assert.Same(reference.Fallback(id, meta, from, except), fallback.Invoke(c, [id, meta, from, except]));
                        }
                return 0;
            });
        // Outside a pick (a report, the state): worked out per call, the same answers.
        foreach (var (id, meta) in items)
            foreach (var from in froms.Take(20))
                Assert.Same(reference.Best(id, meta, from, null), c.BestChest(id, meta, from));
        Assert.Equal(0, differ);
        Assert.True(asked > 5_000);
    }

    [Fact]
    public void A_chest_that_changes_during_a_pick_is_seen_at_once()
    {
        var (c, _, _, _) = Colony();
        var from = new P3(10, 64, 10);
        InPick(c, c.P(2), () =>
        {
            var first = c.BestChest(4, 0, from)!;
            c.StorageOp("set", [first.Chest], new StorageFilter { Allow = ["food"] }, null, null); // no longer takes cobblestone
            var next = c.BestChest(4, 0, from)!;
            Assert.NotSame(first, next);
            Assert.Same(new Reference(c).Best(4, 0, from, null), next);
            c.AddContents(next, new Stack(3, 0, 64 * 54)); // full of dirt
            Assert.Same(new Reference(c).Best(4, 0, from, null), c.BestChest(4, 0, from));
            Assert.Same(new Reference(c).Best(3, 0, from, null), c.BestChest(3, 0, from));
            return 0;
        });
    }

    // ---------------------------------------------------------------- a re-sort of tools keeps the hauler's own

    private static readonly P3 Source = new(2, 64, 0), Target = new(8, 64, 0);

    private static (StoryColony C, FakeStoryHost H) SortRig()
    {
        var (c, h, w) = StoryRig.Make(1);
        c.DoneGrace = TimeSpan.Zero;
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Haul ? 1 : 0;
        foreach (var at in new[] { Source, Target }) w.Set(at.X, at.Y, at.Z, 54);
        c.StorageOp("set", [Source], new StorageFilter { Allow = ["*"] }, "normal", null);
        c.StorageOp("set", [Target], new StorageFilter { Allow = ["*"] }, "important", null); // pulls what it accepts
        return (c, h);
    }

    /// <summary>
    /// Live 11-10: "Stone Pickaxe: deposited (chest …)" over and over. A re-sort trip names what it moves, and the pawn's deposit puts
    /// every one of that name in the chest: the hauler's own stone pickaxe went with the one it moved. Now the moved gear goes as a store
    /// that keeps as many as the pawn carried before; the rest of the trip is stashed as before.
    /// </summary>
    [Fact]
    public void A_resort_of_tools_the_hauler_carries_keeps_its_own()
    {
        var (c, h) = SortRig();
        c.AddContents(c.StorageAt(Source)!, new Stack(274, 0, 2)); // two stone pickaxes
        c.AddContents(c.StorageAt(Source)!, new Stack(4, 0, 20));
        h.Lives[c.P()] = FakeStoryHost.Live(4, 64, 2); // carries a stone pickaxe of its own
        var j = c.RebalanceJob(c.Pawns[c.P()], h.Live(c.P()))!;
        Assert.NotNull(j);
        var stash = j.Json["stash"]!.AsArray();
        Assert.Single(stash);
        Assert.Equal(Target.ToString(), stash[0]!["to"]!.GetValue<string>());
        var items = stash[0]!["items"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Single(items); // the cobblestone only
        Assert.DoesNotContain("stone_pickaxe", items);
        var store = Assert.Single(j.Json["store"]!.AsArray())!;
        Assert.Equal(Target.ToString(), store["to"]!.GetValue<string>());
        Assert.Equal(c.ItemName(274, 0), store["item"]!.GetValue<string>());
        Assert.Equal(1, store["keep"]!.GetValue<int>());
        // The ledger is unchanged: both pickaxes leave the source and go into the target.
        Assert.Contains(j.Puts, p => p.Chest == Target && p.Stacks.Any(s => s.Id == 274 && s.Count == 2));
        Assert.Contains(j.Takes, t => t.Chest == Source && t.Stack.Id == 274 && t.Stack.Count == 2);
    }

    [Fact]
    public void A_resort_of_tools_the_hauler_does_not_carry_is_stashed_as_before()
    {
        var (c, h) = SortRig();
        c.AddContents(c.StorageAt(Source)!, new Stack(274, 0, 2));
        h.Lives[c.P()] = FakeStoryHost.Bare(4, 64, 2, new Stack(4, 0, 3));
        var j = c.RebalanceJob(c.Pawns[c.P()], h.Live(c.P()))!;
        Assert.Null(j.Json["store"]);
        Assert.Equal(["stone_pickaxe"], j.Json["stash"]![0]!["items"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    /// <summary>
    /// The store keeps that many STACKS (EquipTool's store: "the keep freshest"). Gear that stacks merges with the moved ones as it is
    /// taken: a hunter with 5 arrows takes 30 more and carries one stack of 35, so a store keeping 5 would keep that one and deposit
    /// nothing, while the ledger put 30 arrows in the chest. Stacking gear stays in the stash (as before the own-gear store); only gear
    /// that never stacks (a pickaxe, here beside the arrows) goes as a store.
    /// </summary>
    [Theory]
    [InlineData(30, 5)]  // one merged stack after the take
    [InlineData(64, 10)] // two stacks after the take (64 and 10): a keep of 10 would keep both
    public void A_resort_of_stacking_gear_the_hauler_carries_is_stashed_as_before(int moved, int carried)
    {
        var (c, h) = SortRig();
        c.AddContents(c.StorageAt(Source)!, new Stack(262, 0, moved)); // arrows: weapons
        c.AddContents(c.StorageAt(Source)!, new Stack(274, 0, 2));
        h.Lives[c.P()] = FakeStoryHost.Bare(4, 64, 2, new Stack(262, 0, carried), new Stack(274, 0, 1));
        var j = c.RebalanceJob(c.Pawns[c.P()], h.Live(c.P()))!;
        Assert.NotNull(j);
        Assert.Contains(j.Takes, t => t.Chest == Source && t.Stack.Id == 262 && t.Stack.Count == moved);
        Assert.Contains(j.Puts, p => p.Chest == Target && p.Stacks.Any(s => s.Id == 262 && s.Count == moved));
        var stash = Assert.Single(j.Json["stash"]!.AsArray())!;
        Assert.Equal(Target.ToString(), stash["to"]!.GetValue<string>());
        var items = stash["items"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Single(items); // the arrows (by their haul name), not the pickaxe
        Assert.DoesNotContain("stone_pickaxe", items);
        var store = Assert.Single(j.Json["store"]!.AsArray())!; // the pickaxe only: it never stacks, so a stack is a pickaxe
        Assert.Equal(c.ItemName(274, 0), store["item"]!.GetValue<string>());
        Assert.Equal(1, store["keep"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- looks at a chest nobody gets to

    private static readonly P3 Far = new(20, 64, 20);

    private static (StoryColony C, Clock T) LookRig()
    {
        var clock = new Clock();
        var (c, h, w) = StoryRig.Make(3, time: clock);
        c.DoneGrace = TimeSpan.Zero;
        h.KnowsChests = true;
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Haul ? 1 : 0;
        w.Set(Far.X, Far.Y, Far.Z, 54);
        c.StorageOp("set", [Far], new StorageFilter { Allow = ["*"] }, "normal", null);
        return (c, clock);
    }

    /// <summary>The pawn's haul upkeep: the look at <see cref="Far"/> it gets now, or null (whatever else it gets).</summary>
    private static JsonObject? LookAtFar(StoryColony c, int pawn)
    {
        var j = c.Next(pawn);
        if (j["kind"]?.GetValue<string>() == "look" && j["at"]?.GetValue<string>() == Far.ToString()) return j;
        c.Report(c.P(pawn), j["id"]!.GetValue<string>(), "done", null, null);
        return null;
    }

    private static void CantReach(StoryColony c, int pawn, JsonObject j) =>
        Assert.True(c.Report(c.P(pawn), j["id"]!.GetValue<string>(), "failed", $"can't reach chest at {Far.X},{Far.Y},{Far.Z}: no walkable spot that sees it (no path)", null));

    /// <summary>
    /// Live 11-10: "look … failed: can't reach chest at …" for the same chests again and again, a pawn at a time (each pawn's rest only
    /// kept that one away; the chest was looked at again every 2 minutes). A look that can't get to its chest now rests looks there
    /// for everyone: 2, then 5, then 10 minutes; a look or a delivery that gets there ends it.
    /// </summary>
    [Fact]
    public void A_chest_no_pawn_gets_to_rests_from_looks_for_everyone()
    {
        var (c, clock) = LookRig();
        var first = LookAtFar(c, 0)!;
        CantReach(c, 0, first);
        clock.Now += TimeSpan.FromMinutes(2.5);
        var second = LookAtFar(c, 1)!; // the first rest (2 minutes, as before) is over
        CantReach(c, 1, second);
        clock.Now += TimeSpan.FromMinutes(2.5);
        Assert.Null(LookAtFar(c, 2)); // before: looked at again 2 minutes on; now the second miss rests it 5 minutes
        clock.Now += TimeSpan.FromMinutes(3);
        var third = LookAtFar(c, 2)!;
        Assert.True(c.Report(c.P(2), third["id"]!.GetValue<string>(), "done", null, null)); // got there: the rest is over
        clock.Now += TimeSpan.FromMinutes(2.5);
        Assert.Equal(0, ((System.Collections.IDictionary)typeof(StoryColony).GetField("_lookRest", Members)!.GetValue(c)!).Count);
    }
}
