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
            bool fishing = zone.Kind == "fish"; // fishing bills: plant "fish", mode forever | until (raw fish stored) | count (catches)
            if (World is not { } w) return (null, "no world view yet");
            var bills = zone.Bills ?? [];
            _fishScan.Remove(zone.Id);
            var warnings = new List<string>();
            string? billId = Str(body["bill"]);
            GrowBill? bill = billId == null ? null : bills.FirstOrDefault(b => b.Id == billId);
            if (op is "bill_update" or "bill_remove" or "bill_pause" or "bill_resume" && bill == null) return (null, "bill: no such bill in the zone");
            string? Apply(GrowBill b, bool adding)
            {
                if (fishing)
                {
                    if (body["plant"] != null && Str(body["plant"]) != "fish") return "a fishing zone's bills fish: plant must be fish";
                    b.Plant = "fish";
                }
                else if (body["plant"] != null || adding)
                {
                    if (StoryPlants.Get(w.Data, Str(body["plant"])) is not { } plant)
                        return $"plant must be one of {string.Join("|", StoryPlants.For(w.Data).Select(x => x.Id))} on {w.Version}";
                    if (plant.Id != b.Plant)
                    {
                        var (bad, warn) = ValidateGrow(zone, plant.Id);
                        if (bad != null) return bad;
                        warnings.AddRange(warn);
                    }
                    b.Plant = plant.Id;
                }
                if (body["mode"] != null)
                {
                    var mode = Str(body["mode"]);
                    if (mode == null || !GrowBillModes.Contains(mode)) return $"mode must be {string.Join("|", GrowBillModes)}";
                    b.Mode = mode;
                }
                if (body["target"] != null)
                {
                    if (StoryJson.Int(body["target"]) is not { } t || t < 1 || t > 100_000) return "target must be 1..100000";
                    b.Target = t;
                }
                if (body["paused"] is JsonValue pv && pv.TryGetValue<bool>(out var paused)) b.Paused = paused;
                return null;
            }
            switch (op)
            {
                case "bill_add":
                {
                    if (bills.Count >= 16) return (null, "a zone takes at most 16 bills");
                    var nb = new GrowBill { Id = NextId("gb") };
                    if (Apply(nb, true) is { } err) return (null, err);
                    bills.Add(nb);
                    bill = nb;
                    break;
                }
                case "bill_update":
                {
                    var copy = new GrowBill { Id = bill!.Id, Plant = bill.Plant, Mode = bill.Mode, Target = bill.Target, Done = bill.Done, Paused = bill.Paused };
                    if (Apply(copy, false) is { } err) return (null, err);
                    if (copy.Mode != bill.Mode || copy.Plant != bill.Plant) copy.Done = 0; // a new goal starts from zero
                    bills[bills.IndexOf(bill)] = copy;
                    bill = copy;
                    break;
                }
                case "bill_remove":
                    bills.Remove(bill!);
                    break;
                case "bill_pause" or "bill_resume":
                    bill!.Paused = op == "bill_pause";
                    break;
                case "bill_reorder":
                {
                    if (body["ids"] is not JsonArray ids) return (null, "ids: the bills in their new order");
                    var order = ids.Select(Str).ToList();
                    if (order.Any(i => i == null || bills.All(b => b.Id != i))) return (null, "ids: no such bill in the zone");
                    var sorted = order.Distinct().Select(i => bills.First(b => b.Id == i)).ToList();
                    sorted.AddRange(bills.Where(b => !sorted.Contains(b))); // any left out keep their order, after
                    bills = sorted;
                    break;
                }
                default:
                    return (null, "op must be bill_add|bill_update|bill_remove|bill_pause|bill_resume|bill_reorder");
            }
            zone.Bills = bills;
            if (!fishing) zone.Plant = bills.FirstOrDefault()?.Plant;
            zone.Rev = Touch();
            _growScan.Remove(zone.Id);
            WakeIdle();
            var res = new JsonObject
            {
                ["id"] = zone.Id, ["ids"] = new JsonArray(bills.Select(b => (JsonNode)b.Id!).ToArray()),
                ["active_bill"] = fishing ? bills.FirstOrDefault(b => !b.Paused && !FishBillMet(b))?.Id : ActiveBill(w, zone)?.Id, ["warnings"] = new JsonArray(warnings.Select(x => (JsonNode)x!).ToArray()),
            };
            if (op == "bill_add") res["bill"] = bill!.Id;
            return (res, null);
        }

        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n is JsonValue nv && nv.TryGetValue<long>(out var l) ? l.ToString() : null;
    }

    /// <summary>The zone's bills in the state: each with its plant, mode, target, progress (done or stored), met, active, seeds.</summary>
    private void BillsJson(IStoryWorld w, ZoneState z, JsonObject o, int room)
    {
        var active = ActiveBill(w, z);
        o["active_bill"] = active?.Id;
        o["bills"] = new JsonArray(z.Bills!.Select(b =>
        {
            var p = StoryPlants.Get(w.Data, b.Plant);
            var j = new JsonObject
            {
                ["id"] = b.Id, ["plant"] = b.Plant, ["label"] = p?.Label ?? b.Plant, ["mode"] = b.Mode, ["target"] = b.Target, ["done"] = b.Done,
                ["paused"] = b.Paused, ["active"] = b == active,
            };
            if (p == null) { j["met"] = false; j["missing"] = $"{b.Plant} doesn't grow in {w.Version}"; return (JsonNode)j; }
            if (b.Mode == "until") j["stored"] = ProduceStored(p);
            j["met"] = BillMet(b, p);
            int have = CountItem(p.SeedId, p.SeedId == 6 ? p.SeedMeta : -1);
            int needed = b == active ? room : 0;
            j["seeds"] = new JsonObject
            {
                ["item"] = StoryPlants.SeedName(w.Data, p), ["have"] = have, ["needed_for_empty"] = needed, ["source_hint"] = p.Source,
                ["missing"] = have == 0 ? SeedHint(p) : null,
                ["action"] = have == 0 && p.Id == "wheat" ? "seek_seeds" : have == 0 && p.Harvest == "fruit" ? "craft_seeds" : null,
            };
            return j;
        }).ToArray());
    }
}
