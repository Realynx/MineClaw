using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Planting bills (docs/web/story-mode.md "Farming"): a growing zone's ordered list of what to sow, like a workbench's bills. Each is
/// <c>{plant, mode: forever | until (N of the produce in storage) | count (harvest N), paused}</c>. The active bill is the first
/// unpaused one whose target isn't met: it decides what is sown on empty soil. When it is met sowing moves to the next; crops already
/// in the ground are harvested as usual (never ripped out). No active bill: the zone keeps harvesting and sows nothing. Harvests count
/// toward the first unmet count bill of what was harvested. The zone's <see cref="ZoneState.Plant"/> mirrors the first bill's plant.
/// </summary>
public sealed partial class StoryColony
{
    public static readonly IReadOnlyList<string> GrowBillModes = ["forever", "until", "count"];

    /// <summary>A harvest job's lease → the zone and plant it harvests (counted when it is done).</summary>
    private readonly Dictionary<string, (string Zone, string Plant)> _cropHarvest = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The plants the zone's bills grow (this version's, bill order, distinct); a zone from before bills: its plant.</summary>
    private static IReadOnlyList<StoryPlant> BillPlants(IStoryWorld w, ZoneState z) =>
        z.Bills == null
            ? StoryPlants.Get(w.Data, z.Plant) is { } p ? [p] : []
            : z.Bills.Select(b => StoryPlants.Get(w.Data, b.Plant)).OfType<StoryPlant>().DistinctBy(p => p.Id).ToList();

    /// <summary>The first unpaused bill whose target isn't met (a plant this version grows), else null.</summary>
    internal GrowBill? ActiveBill(IStoryWorld w, ZoneState z) =>
        z.Bills?.FirstOrDefault(b => !b.Paused && StoryPlants.Get(w.Data, b.Plant) is { } p && !BillMet(b, p));

    /// <summary>What the zone sows now: its active bill's plant (a zone from before bills: its plant); null = nothing.</summary>
    internal StoryPlant? ActivePlant(IStoryWorld w, ZoneState z) =>
        z.Bills == null ? StoryPlants.Get(w.Data, z.Plant) : ActiveBill(w, z) is { } b ? StoryPlants.Get(w.Data, b.Plant) : null;

    private bool BillMet(GrowBill b, StoryPlant p) => b.Mode switch
    {
        "until" => ProduceStored(p) >= b.Target,
        "count" => b.Done >= b.Target,
        _ => false,
    };

    /// <summary>The plant's produce the colony has (storage and pawns).</summary>
    private int ProduceStored(StoryPlant p)
    {
        var (id, meta) = StoryPlants.Produce(p);
        return CountItem(id, meta);
    }

    /// <summary>A growing zone's harvest finished: one toward the first unmet count bill of that plant.</summary>
    private void GrowHarvested(ActiveJob j)
    {
        if (j.Lease is not { } lease || !_cropHarvest.Remove(lease, out var h)) return;
        if (Zones.FirstOrDefault(z => z.Id == h.Zone) is not { Bills: { } bills } zone) return;
        if (bills.FirstOrDefault(b => !b.Paused && b.Mode == "count" && b.Plant == h.Plant && b.Done < b.Target) is not { } bill) return;
        bill.Done++;
        zone.Rev = Touch();
        _growScan.Remove(zone.Id);
    }

    /// <summary>After <c>update {plant}</c>: the first bill grows that plant (a zone without bills gets one; no plant: no bills).</summary>
    private void SyncPlantBill(ZoneState z)
    {
        if (z.Plant == null) { z.Bills = null; return; }
        if (z.Bills is not { Count: > 0 }) z.Bills = [new GrowBill { Id = NextId("gb"), Plant = z.Plant }];
        else z.Bills[0].Plant = z.Plant;
    }

    /// <summary>
    /// <c>bill_add {id, plant, mode?, target?, paused?}</c> → <c>{id, bill, warnings}</c>; <c>bill_update {id, bill, plant?, mode?,
    /// target?, paused?}</c>; <c>bill_remove|bill_pause|bill_resume {id, bill}</c>; <c>bill_reorder {id, ids:[bill]}</c>.
    /// </summary>
    private (JsonNode? Result, string? Error) GrowBillOp(string op, string? id, JsonObject? body)
    {
        body ??= new JsonObject();
        lock (_lock)
        {
            if (Zones.FirstOrDefault(z => z.Id == id) is not { } zone) return (null, "id: no such zone");
            if (zone.Kind is not ("farm" or "fish")) return (null, "only growing and fishing zones take bills");
            if (World is not { } w) return (null, "no world view yet");
            _fishScan.Remove(zone.Id);
            var edit = new GrowBillEdit(this, w, zone, body);
            if (edit.Run(op) is { } err) return (null, err);
            return (GrowBillsSaved(w, edit, op), null);
        }
    }

    /// <summary>The edited bills become the zone's (a growing zone's plant: its first bill's); the answer names them and what is active.</summary>
    private JsonObject GrowBillsSaved(IStoryWorld w, GrowBillEdit edit, string op)
    {
        var zone = edit.Zone;
        var bills = edit.Bills;
        zone.Bills = bills;
        if (!edit.Fishing) zone.Plant = bills.FirstOrDefault()?.Plant;
        zone.Rev = Touch();
        _growScan.Remove(zone.Id);
        WakeIdle();
        var res = new JsonObject
        {
            ["id"] = zone.Id, ["ids"] = new JsonArray(bills.Select(b => (JsonNode)b.Id!).ToArray()),
            ["active_bill"] = edit.Fishing ? bills.FirstOrDefault(b => !b.Paused && !FishBillMet(b))?.Id : ActiveBill(w, zone)?.Id,
            ["warnings"] = new JsonArray(edit.Warnings.Select(x => (JsonNode)x!).ToArray()),
        };
        if (op == "bill_add") res["bill"] = edit.Bill!.Id;
        return res;
    }

    /// <summary>
    /// One bill op on a growing (or fishing: plant "fish", mode forever | until (raw fish stored) | count (catches)) zone's bills, on a
    /// working list: the zone takes it only when the op succeeds.
    /// </summary>
    private sealed class GrowBillEdit(StoryColony c, IStoryWorld w, ZoneState zone, JsonObject body)
    {
        public ZoneState Zone => zone;
        public bool Fishing { get; } = zone.Kind == "fish";
        public List<GrowBill> Bills { get; private set; } = zone.Bills ?? [];
        public List<string> Warnings { get; } = new();
        /// <summary>The bill the op names (or the one it added).</summary>
        public GrowBill? Bill { get; private set; }

        /// <summary>An id as the browser sends it: a string, or a number.</summary>
        private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n is JsonValue nv && nv.TryGetValue<long>(out var l) ? l.ToString() : null;

        private static bool NamesABill(string op) => op is "bill_update" or "bill_remove" or "bill_pause" or "bill_resume";

        /// <summary>The op applied (null), or why not.</summary>
        public string? Run(string op)
        {
            Bill = Str(body["bill"]) is { } billId ? Bills.FirstOrDefault(b => b.Id == billId) : null;
            if (NamesABill(op) && Bill == null) return "bill: no such bill in the zone";
            return op switch
            {
                "bill_add" => Add(),
                "bill_update" => Update(Bill!),
                "bill_remove" => Remove(Bill!),
                "bill_pause" or "bill_resume" => Pause(Bill!, op == "bill_pause"),
                "bill_reorder" => Reorder(),
                _ => "op must be bill_add|bill_update|bill_remove|bill_pause|bill_resume|bill_reorder",
            };
        }

        private string? Add()
        {
            if (Bills.Count >= 16) return "a zone takes at most 16 bills";
            var nb = new GrowBill { Id = c.NextId("gb") };
            if (Apply(nb, true) is { } err) return err;
            Bills.Add(nb);
            Bill = nb;
            return null;
        }

        private string? Update(GrowBill bill)
        {
            var copy = new GrowBill { Id = bill.Id, Plant = bill.Plant, Mode = bill.Mode, Target = bill.Target, Done = bill.Done, Paused = bill.Paused };
            if (Apply(copy, false) is { } err) return err;
            if (copy.Mode != bill.Mode || copy.Plant != bill.Plant) copy.Done = 0; // a new goal starts from zero
            Bills[Bills.IndexOf(bill)] = copy;
            Bill = copy;
            return null;
        }

        private string? Remove(GrowBill bill)
        {
            Bills.Remove(bill);
            return null;
        }

        private static string? Pause(GrowBill bill, bool paused)
        {
            bill.Paused = paused;
            return null;
        }

        private string? Reorder()
        {
            if (body["ids"] is not JsonArray ids) return "ids: the bills in their new order";
            var order = ids.Select(Str).ToList();
            if (order.Any(i => i == null || Bills.All(b => b.Id != i))) return "ids: no such bill in the zone";
            var sorted = order.Distinct().Select(i => Bills.First(b => b.Id == i)).ToList();
            sorted.AddRange(Bills.Where(b => !sorted.Contains(b))); // any left out keep their order, after
            Bills = sorted;
            return null;
        }

        /// <summary>The body's plant, mode, target and paused onto a bill, each checked (the first error stops).</summary>
        private string? Apply(GrowBill b, bool adding) => ApplyPlant(b, adding) ?? ApplyMode(b) ?? ApplyTarget(b) ?? ApplyPaused(b);

        private string? ApplyPlant(GrowBill b, bool adding)
        {
            if (Fishing)
            {
                if (body["plant"] != null && Str(body["plant"]) != "fish") return "a fishing zone's bills fish: plant must be fish";
                b.Plant = "fish";
                return null;
            }
            if (body["plant"] == null && !adding) return null;
            if (StoryPlants.Get(w.Data, Str(body["plant"])) is not { } plant)
                return $"plant must be one of {string.Join("|", StoryPlants.For(w.Data).Select(x => x.Id))} on {w.Version}";
            if (plant.Id != b.Plant)
            {
                var (bad, warn) = c.ValidateGrow(zone, plant.Id);
                if (bad != null) return bad;
                Warnings.AddRange(warn);
            }
            b.Plant = plant.Id;
            return null;
        }

        private string? ApplyMode(GrowBill b)
        {
            if (body["mode"] == null) return null;
            var mode = Str(body["mode"]);
            if (mode == null || !GrowBillModes.Contains(mode)) return $"mode must be {string.Join("|", GrowBillModes)}";
            b.Mode = mode;
            return null;
        }

        private string? ApplyTarget(GrowBill b)
        {
            if (body["target"] == null) return null;
            if (StoryJson.Int(body["target"]) is not { } t || t < 1 || t > 100_000) return "target must be 1..100000";
            b.Target = t;
            return null;
        }

        private string? ApplyPaused(GrowBill b)
        {
            if (body["paused"] is JsonValue pv && pv.TryGetValue<bool>(out var paused)) b.Paused = paused;
            return null;
        }
    }

    /// <summary>The zone's bills in the state: each with its plant, mode, target, progress (done or stored), met, active, seeds.</summary>
    private void BillsJson(IStoryWorld w, ZoneState z, JsonObject o, int room)
    {
        var active = ActiveBill(w, z);
        o["active_bill"] = active?.Id;
        o["bills"] = new JsonArray(z.Bills!.Select(b => (JsonNode)ZoneBillJson(w, b, active, room)).ToArray());
    }

    private JsonObject ZoneBillJson(IStoryWorld w, GrowBill b, GrowBill? active, int room)
    {
        var p = StoryPlants.Get(w.Data, b.Plant);
        var j = new JsonObject
        {
            ["id"] = b.Id, ["plant"] = b.Plant, ["label"] = p?.Label ?? b.Plant, ["mode"] = b.Mode, ["target"] = b.Target, ["done"] = b.Done,
            ["paused"] = b.Paused, ["active"] = b == active,
        };
        if (p == null) { j["met"] = false; j["missing"] = $"{b.Plant} doesn't grow in {w.Version}"; return j; }
        if (b.Mode == "until") j["stored"] = ProduceStored(p);
        j["met"] = BillMet(b, p);
        int have = CountItem(p.SeedId, p.SeedId == 6 ? p.SeedMeta : -1);
        j["seeds"] = BillSeedsJson(w, p, have, b == active ? room : 0);
        return j;
    }

    private static JsonObject BillSeedsJson(IStoryWorld w, StoryPlant p, int have, int needed) => new()
    {
        ["item"] = StoryPlants.SeedName(w.Data, p), ["have"] = have, ["needed_for_empty"] = needed, ["source_hint"] = p.Source,
        ["missing"] = have == 0 ? SeedHint(p) : null,
        ["action"] = have == 0 && p.Id == "wheat" ? "seek_seeds" : have == 0 && p.Harvest == "fruit" ? "craft_seeds" : null,
    };
}
