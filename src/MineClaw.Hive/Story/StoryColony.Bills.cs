using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Workbenches and bills (docs/web/story-mode.md "Workbenches &amp; bills"): crafting tables and furnaces in the home zone (found by
/// themselves, or built), each with an ordered list of bills. A pawn with the bill's work type on (and allowed by the bill) takes the
/// first runnable bill on the nearest free bench: one pawn per bench. It fetches the ingredients from storages within the bill's
/// radius, crafts or loads the furnace (and goes on with other work while it smelts; a later job collects), and the product goes to
/// storage per the bill. Missing ingredients suspend the bill with the reason and an alert.
/// </summary>
public sealed partial class StoryColony
{
    private void ScanBenches()
    {
        if (World is not { } w || Home() is not { } home) return;
        var areas = Zones.Where(z => z.Kind == "home").Select(z => z.Area).ToList();
        if (areas.Count == 0) areas.Add(Area.Of(home.X - 12, home.Y - 4, home.Z - 12, home.X + 12, home.Y + 6, home.Z + 12));
        foreach (var a in areas)
        {
            if (a.Volume > 100_000) continue;
            foreach (var p in a.Blocks())
                if (StoryRecipes.BenchOf(w.Block(p.X, p.Y, p.Z).Id) is { } kind && Benches.All(b => b.Pos != p))
                    Benches.Add(new WorkbenchState { Pos = p, Kind = kind, Rev = Touch() });
        }
    }

    private WorkbenchState? Bench(P3 at) => Benches.FirstOrDefault(b => b.Pos == at);

    private static readonly JsonSerializerOptions J = StoryJson.Options;

    /// <summary>add {bench, bill} | update {id, …} | remove|pause|resume {id} | reorder {bench, ids}.</summary>
    public (JsonNode? Result, string? Error) BillsOp(string op, JsonObject body)
    {
        if (World is not { } w) return (null, "no world view yet");
        lock (_lock)
        {
            var id = body["id"]?.GetValue<string>();
            BillState? Find(out WorkbenchState? bench)
            {
                foreach (var b in Benches)
                    if (b.Bills.FirstOrDefault(x => x.Id == id) is { } bill) { bench = b; return bill; }
                bench = null;
                return null;
            }
            switch (op)
            {
                case "add":
                {
                    if (P3.Parse(body["bench"]) is not { } at) return (null, "bench [x,y,z] required");
                    var bench = Bench(at);
                    if (bench == null)
                    {
                        if (StoryRecipes.BenchOf(w.Block(at.X, at.Y, at.Z).Id) is not { } kind) return (null, "no workbench there");
                        Benches.Add(bench = new WorkbenchState { Pos = at, Kind = kind, Rev = Touch() });
                    }
                    if (!StoryRecipes.Supported.Contains(bench.Kind)) return (null, $"{bench.Kind} bills aren't supported yet (no primitive)");
                    if (body["bill"] is not JsonObject spec) return (null, "bill required");
                    var (bill, err) = ParseBill(w, spec, bench, null);
                    if (bill == null) return (null, err);
                    bill.Id = NextId("bill");
                    bench.Bills.Add(bill);
                    bench.Rev = Touch();
                    WakeIdle();
                    return (new JsonObject { ["id"] = bill.Id }, null);
                }
                case "update":
                {
                    if (Find(out var bench) is not { } bill) return (null, "no such bill");
                    var (upd, err) = ParseBill(w, body, bench!, bill);
                    if (upd == null) return (null, err);
                    bench!.Rev = Touch();
                    return (new JsonObject { ["id"] = bill.Id }, null);
                }
                case "remove":
                case "pause":
                case "resume":
                {
                    if (Find(out var bench) is not { } bill) return (null, "no such bill");
                    if (op == "remove") bench!.Bills.Remove(bill);
                    else { bill.Paused = op == "pause"; if (op == "resume") { bill.SuspendedReason = null; _billFails.Remove(bill.Id); } }
                    bench!.Rev = Touch();
                    return (new JsonObject { ["id"] = bill.Id }, null);
                }
                case "reorder":
                {
                    if (P3.Parse(body["bench"]) is not { } at || Bench(at) is not { } bench) return (null, "no such bench");
                    var ids = body["ids"] is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToList() : [];
                    if (ids.Count == 0 || ids.Any(i => bench.Bills.All(b => b.Id != i))) return (null, "ids must name this bench's bills");
                    var rest = bench.Bills.Where(b => !ids.Contains(b.Id)).ToList();
                    bench.Bills = ids.Distinct().Select(i => bench.Bills.First(b => b.Id == i)).Concat(rest).ToList();
                    bench.Rev = Touch();
                    return (new JsonObject { ["ids"] = new JsonArray(bench.Bills.Select(b => (JsonNode)b.Id!).ToArray()) }, null);
                }
                default:
                    return (null, "op must be add|update|remove|pause|resume|reorder");
            }
        }
    }

    private (BillState? Bill, string? Error) ParseBill(IStoryWorld w, JsonObject spec, WorkbenchState bench, BillState? into)
    {
        var bill = into ?? new BillState();
        if (spec["recipe"]?.GetValue<string>() is { } rid)
        {
            if (StoryRecipes.Get(w.Data, rid) is not { } r || r.Workbench != bench.Kind) return (null, $"no recipe '{rid}' at a {bench.Kind}");
            bill.Recipe = r.Id;
        }
        else if (into == null) return (null, "recipe required");
        if (spec["mode"]?.GetValue<string>() is { } mode)
        {
            if (mode is not ("count" or "until" or "forever")) return (null, "mode must be count|until|forever");
            bill.Mode = mode;
        }
        if (spec["target"] is { } t) bill.Target = Math.Clamp(t.GetValue<int>(), 1, 100_000);
        if (spec["radius"] is { } rad) bill.Radius = Math.Clamp(rad.GetValue<int>(), 1, 256);
        if (spec["pawn"]?.GetValue<string>() is { } pw)
        {
            if (pw != "any" && Pawn(pw) == null) return (null, $"no pawn '{pw}'");
            bill.Pawn = pw == "any" ? "any" : Pawn(pw)!.Name;
        }
        if (spec.ContainsKey("min_skill")) bill.MinSkill = spec["min_skill"] is { } ms ? Math.Clamp(ms.GetValue<int>(), 0, 20) : null;
        if (spec["ingredients"] is JsonObject ing)
        {
            var f = ing.Deserialize<StorageFilter>(J) ?? new StorageFilter();
            if (f.Allow.Count == 0) f.Allow = ["*"];
            if (Cats is { } c && f.Allow.Concat(f.Deny).FirstOrDefault(e => !c.Valid(e)) is { } bad) return (null, $"unknown ingredient filter '{bad}'");
            bill.Ingredients = f;
        }
        if (spec["store"] is { } st)
        {
            if (st is JsonValue v && v.TryGetValue<string>(out var s) && s == "best") bill.Store = JsonValue.Create("best");
            else if (st is JsonObject so && P3.Parse(so["chest"]) is { } chest) bill.Store = new JsonObject { ["chest"] = chest.Json() };
            else return (null, "store must be \"best\" or {chest:[x,y,z]}");
        }
        if (spec["paused"] is { } pz) bill.Paused = pz.GetValue<bool>();
        return (bill, null);
    }

    private readonly Dictionary<string, int> _billFails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _billAlerted = new(StringComparer.Ordinal);

    /// <summary>Is the bill still wanted (count not reached, until-amount not in stock, forever)?</summary>
    internal bool Wanted(BillState b, StoryRecipe r) => b.Mode switch
    {
        "count" => b.Done < b.Target,
        "until" => CountItem(r.Output.Id, r.Output.Meta) < b.Target,
        _ => true,
    };

    private bool MayTake(BillState b, PawnState p, string work) =>
        (b.Pawn == "any" || b.Pawn.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) && (b.MinSkill is not { } m || (p.Skills.GetValueOrDefault(work)?.Level ?? 0) >= m);

    /// <summary>Each ingredient's source (a pawn's own stack, else a chest within the radius), or what's missing.</summary>
    private (List<(P3? Chest, Stack Stack)> Got, string? Missing) Ingredients(BillState b, StoryRecipe r, int crafts, WorkbenchState bench, PawnLive live)
    {
        var got = new List<(P3?, Stack)>();
        foreach (var input in r.Inputs)
        {
            int need = input.Count * crafts;
            bool Ok(Stack s) => input.Options.Any(o => o.Matches(s.Id, s.Meta)) && Cats!.Accepts(b.Ingredients, s.Id, s.Meta);
            if (live.Inventory.Where(Ok).Sum(s => s.Count) >= need) { got.Add((null, live.Inventory.First(Ok) with { Count = need })); continue; }
            var src = FindStored(s => Ok(s) && s.Count >= need, bench.Pos, b.Radius) ?? FindStored(Ok, bench.Pos, b.Radius);
            if (src == null) return (got, $"no {World!.Data.DisplayName(input.Options[0].Id, Math.Max(0, input.Options[0].Meta)).ToLowerInvariant()} within {b.Radius} blocks");
            got.Add((src.Value.Store.Chest, src.Value.Stack with { Count = Math.Min(need, src.Value.Stack.Count) }));
        }
        return (got, null);
    }

    /// <summary>A bill job (craft or cook), or collecting a furnace that is done, for this pawn; null = none runnable.</summary>
    private ActiveJob? BillJob(PawnState p, PawnLive live, string work)
    {
        if (World is not { } w) return null;
        var from = live.Block;
        foreach (var bench in Benches.OrderBy(b => b.Pos.Dist(from)))
        {
            var key = "bench:" + bench.Pos;
            if (!Open(key, p.Name, null)) continue;
            // A loaded furnace that is done: collect (the bill's work type).
            if (bench.LoadedBill is { } lb && bench.ReadyAt <= Now)
            {
                var loaded = bench.Bills.FirstOrDefault(b => b.Id == lb);
                var lr = loaded != null ? StoryRecipes.Get(w.Data, loaded.Recipe) : null;
                if ((lr?.WorkType ?? WorkTypes.Craft) != work) continue;
                return Job(work, "collect_furnace", "collecting the furnace", bench.Pos, key, "furnace", lb, xp: 10, count: bench.LoadedCount);
            }
            if (bench.LoadedBill != null) continue; // still smelting
            foreach (var bill in bench.Bills)
            {
                if (bill.Paused || bill.SuspendedReason != null) continue;
                if (StoryRecipes.Get(w.Data, bill.Recipe) is not { } r || r.WorkType != work || !MayTake(bill, p, work)) continue;
                if (!Wanted(bill, r)) continue;
                int left = bill.Mode == "count" ? bill.Target - bill.Done : bill.Mode == "until" ? bill.Target - CountItem(r.Output.Id, r.Output.Meta) : 64;
                int crafts = Math.Clamp((int)Math.Ceiling(Math.Max(1, left) / (double)Math.Max(1, r.Output.Count)), 1, bench.Kind == "furnace" ? 8 : 16);
                var (got, missing) = Ingredients(bill, r, crafts, bench, live);
                if (missing != null)
                {
                    bill.SuspendedReason = missing;
                    bench.Rev = Touch();
                    if (_billAlerted.GetValueOrDefault(bill.Id) != missing) { _billAlerted[bill.Id] = missing; Alert("warn", $"Bill {r.Label} suspended: {missing}"); }
                    continue;
                }
                var fetch = new JsonArray();
                var job = Job(work, bench.Kind == "furnace" ? "smelt" : "craft", r.Label + (bill.Mode == "count" ? $" ({bill.Done}/{bill.Target})" : ""), bench.Pos, key,
                    "bill", bill.Id, "working", f =>
                    {
                        f["item"] = r.ToolItem;
                        f["n"] = bench.Kind == "furnace" ? crafts : crafts * r.Output.Count;
                        f["fetch"] = fetch;
                    }, xp: 25, count: crafts * r.Output.Count);
                foreach (var (chest, st) in got.Where(g => g.Chest != null))
                {
                    fetch.Add(new JsonObject { ["from"] = chest!.Value.ToString(), ["item"] = ItemName(st.Id, st.Meta), ["n"] = st.Count });
                    job.Takes.Add((chest.Value, st));
                }
                if (bench.Kind == "furnace")
                {
                    // Fuel by preference: coal/charcoal, planks, logs (the pawn's own first, else storage).
                    string? fuel = null;
                    foreach (var (fid, per) in StoryRecipes.Fuels)
                    {
                        int need = Math.Max(1, (int)Math.Ceiling(crafts * per));
                        if (live.Inventory.Where(s => s.Id == fid).Sum(s => s.Count) >= need) { fuel = ItemName(fid, 0); break; }
                        if (FindStored(s => s.Id == fid && s.Count >= need, bench.Pos, bill.Radius) is { } fs)
                        {
                            fuel = ItemName(fid, 0);
                            fetch.Add(new JsonObject { ["from"] = fs.Store.Chest.ToString(), ["item"] = fuel, ["n"] = need });
                            job.Takes.Add((fs.Store.Chest, fs.Stack with { Count = need }));
                            break;
                        }
                    }
                    if (fuel == null)
                    {
                        bill.SuspendedReason = "no fuel (coal, planks or logs) in storage";
                        bench.Rev = Touch();
                        if (_billAlerted.GetValueOrDefault(bill.Id) != "fuel") { _billAlerted[bill.Id] = "fuel"; Alert("warn", $"Bill {r.Label} suspended: no fuel"); }
                        continue;
                    }
                    job.Json["fuel"] = fuel;
                }
                else
                {
                    // The product goes to storage right after crafting: the bill's chest, else the best one.
                    var dst = bill.Store is JsonObject so && P3.Parse(so["chest"]) is { } c ? StorageAt(c) : BestChest(r.Output.Id, r.Output.Meta, bench.Pos);
                    if (dst != null)
                    {
                        job.Json["stash"] = new JsonArray(new JsonObject { ["to"] = dst.Chest.ToString(), ["items"] = new JsonArray(ItemName(r.Output.Id, r.Output.Meta)) });
                        job.Puts.Add((dst.Chest, [r.Output with { Count = crafts * r.Output.Count }]));
                    }
                }
                return job;
            }
        }
        return null;
    }

    private void BillDone(PawnState p, ActiveJob j)
    {
        foreach (var bench in Benches)
        {
            if (j.Source == "furnace" && bench.LoadedBill == j.SourceId && j.At == bench.Pos)
            {
                if (bench.Bills.FirstOrDefault(b => b.Id == j.SourceId) is { } fb) fb.Done += bench.LoadedCount;
                bench.LoadedBill = null; bench.LoadedCount = 0; bench.ReadyAt = null;
                bench.Rev = Touch();
                _lastSource[p.Name] = "furnace"; // what it took out is stored next (deliveries)
                return;
            }
            if (bench.Bills.FirstOrDefault(b => b.Id == j.SourceId) is not { } bill) continue;
            _billFails.Remove(bill.Id);
            if (bench.Kind == "furnace")
            {
                // Loaded and left to smelt: another job collects when it's done.
                bench.LoadedBill = bill.Id;
                bench.LoadedCount = j.Count;
                bench.ReadyAt = Now + TimeSpan.FromSeconds(StoryRecipes.SmeltSeconds * Math.Max(1, j.Json["n"]?.GetValue<int>() ?? 1));
            }
            else bill.Done += j.Count;
            bench.Rev = Touch();
            return;
        }
    }

    private void BillFailed(PawnState p, ActiveJob j, string why)
    {
        foreach (var bench in Benches)
        {
            if (j.Source == "furnace" && bench.LoadedBill == j.SourceId && j.At == bench.Pos)
            {
                bench.ReadyAt = Now + TimeSpan.FromSeconds(20); // not done yet: look again later
                bench.Rev = Touch();
                return;
            }
            if (bench.Bills.FirstOrDefault(b => b.Id == j.SourceId) is not { } bill) continue;
            if ((_billFails[bill.Id] = _billFails.GetValueOrDefault(bill.Id) + 1) >= 3)
            {
                bill.SuspendedReason = why;
                Alert("warn", $"Bill {bill.Recipe} suspended: {why}", p.Name);
            }
            bench.Rev = Touch();
            return;
        }
    }

    /// <summary>Suspended bills whose ingredients may be back are retried (every storage change wakes them).</summary>
    internal void RetrySuspended()
    {
        foreach (var bench in Benches)
            foreach (var b in bench.Bills.Where(b => b.SuspendedReason != null && !_billFails.ContainsKey(b.Id)))
            {
                b.SuspendedReason = null;
                bench.Rev = Touch();
            }
    }
}
