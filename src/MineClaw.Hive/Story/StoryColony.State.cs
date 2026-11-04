using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// The colony state the browser draws (<c>GET /api/story/colonies/{id}?since=rev</c>): everything, or only what changed after
/// <c>since</c> (pawns always: they move), with <c>removed</c> naming what went. snake_case, nulls written.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>The list row (<c>GET /api/story</c>).</summary>
    public JsonObject Row()
    {
        lock (_lock)
            return new JsonObject
            {
                ["id"] = Id, ["name"] = Meta.Name, ["created"] = Meta.Created, ["paused"] = Meta.Paused, ["active"] = Meta.Active,
                ["pawns"] = new JsonArray(Pawns.Keys.Select(k => (JsonNode)k!).ToArray()), ["version"] = World?.Version,
            };
    }

    public JsonObject State(long? since = null)
    {
        lock (_lock)
        {
            EnsureMaterials(); // builds' materials and auto bills current (it may stamp a new revision: before "rev")
            long s = since ?? -1;
            bool full = since == null;
            bool New(long rev) => full || rev > s;
            var w = World;
            int hour = HourNow;
            var o = new JsonObject
            {
                ["id"] = Id, ["rev"] = _rev, ["since"] = since, ["full"] = full, ["at"] = Now, ["time_of_day"] = w?.TimeOfDay, ["time_frozen"] = w?.TimeFrozen, ["hour"] = hour,
                ["paused"] = Meta.Paused, ["active"] = Meta.Active, ["home"] = Home()?.Json(), ["tie_break"] = Meta.TieBreak, ["options"] = OptionsJson(),
                ["version"] = w?.Version, ["work_types"] = new JsonArray(WorkTypes.All.Select(t => (JsonNode)t!).ToArray()),
                ["pawns"] = new JsonArray(Pawns.Values.Select(p => (JsonNode)PawnJson(p)).ToArray()),
                ["seats"] = SeatsJson(),
                ["zones"] = new JsonArray(Zones.Where(z => New(z.Rev) || z.Plant != null || z.Bills != null || z.Kind == "fish").Select(z => (JsonNode)ZoneJson(z)).ToArray()),
                ["beds"] = BedsJson(),
                ["designations"] = new JsonArray(Designations.Where(d => New(d.Rev)).Select(d => (JsonNode)DesignationJson(d)).ToArray()),
                ["orders"] = new JsonArray(Pawns.Values.Where(p => New(p.Rev)).SelectMany(p => p.Queue.Select(q => (JsonNode)OrderJson(q, p.Name))).ToArray()),
                ["alerts"] = new JsonArray(Alerts.Where(a => New(a.Rev)).TakeLast(50).Select(a => (JsonNode)new JsonObject
                {
                    ["at"] = a.At, ["level"] = a.Level, ["text"] = a.Text, ["pawn"] = a.Pawn,
                }).ToArray()),
                ["builds"] = new JsonArray(Builds.Where(b => New(b.Rev) || b.Ghosts.Any(g => New(g.Rev))).OrderBy(b => b.Order).Select(b => (JsonNode)new JsonObject
                {
                    ["id"] = b.Id, ["kind"] = b.Kind, ["label"] = b.Label, ["status"] = b.Status, ["reason"] = b.Reason, ["blocks_total"] = b.BlocksTotal,
                    ["blocks_done"] = b.BlocksDone, ["material"] = b.Material, ["order"] = b.Order, ["created"] = b.Created,
                    ["materials"] = new JsonArray(b.Materials.Select(m => (JsonNode)m.Json()).ToArray()),
                    ["no_craft"] = new JsonArray(b.NoCraft.Select(n => (JsonNode)n!).ToArray()),
                    ["station"] = _station is { } st && st.Build == b.Id ? new JsonObject { ["kind"] = st.Kind, ["pos"] = st.Spot.Json() } : null,
                }).ToArray()),
                ["ghosts"] = new JsonObject
                {
                    ["rev"] = _rev,
                    ["blocks"] = new JsonArray(Builds.SelectMany(b => b.Ghosts.Where(g => New(g.Rev)).Select(g => (JsonNode)new JsonArray(
                        // The 8th element: dig (Id/Meta = the block expected there: the live world's when queued) or place.
                        g.At.X, g.At.Y, g.At.Z, g.Id, g.Meta, b.Id, g.State, g.Remove ? "dig" : "place"))).ToArray()),
                },
                ["storages"] = new JsonArray(Storages.Where(st => New(st.Rev)).Select(st => (JsonNode)new JsonObject
                {
                    ["chest"] = st.Chest.Json(), ["double_with"] = st.DoubleWith?.Json(), ["label"] = st.Label, ["priority"] = st.Priority,
                    ["filter"] = new JsonObject
                    {
                        ["allow"] = new JsonArray(st.Filter.Allow.Select(a => (JsonNode)a!).ToArray()),
                        ["deny"] = new JsonArray(st.Filter.Deny.Select(a => (JsonNode)a!).ToArray()),
                    },
                    ["contents"] = new JsonArray(st.Contents.Select(c => (JsonNode)c.Json()).ToArray()), ["free_slots"] = st.FreeSlots, ["known_at"] = st.KnownAt,
                }).ToArray()),
                ["workbenches"] = new JsonArray(Benches.Where(b => New(b.Rev)).Select(b => (JsonNode)new JsonObject
                {
                    ["pos"] = b.Pos.Json(), ["kind"] = b.Kind, ["label"] = b.Label,
                    ["supported"] = w != null ? StoryRecipes.SupportedIn(w.Data, b.Kind) : StoryRecipes.Supported.Contains(b.Kind),
                    ["loaded"] = b.LoadedBill, ["ready_at"] = b.ReadyAt, ["station"] = StationInfo(b),
                    ["bills"] = new JsonArray(b.Bills.Select(x => (JsonNode)BillJson(x)).ToArray()),
                }).ToArray()),
            };
            if (full && w != null) o["categories"] = StoryCategories.For(w.Data).Json();
            if (full && w != null) o["plants"] = StoryPlants.Json(w.Data); // what growing zones can grow here
            if (!full)
            {
                var removed = new JsonObject();
                foreach (var g in _removed.Where(r => r.Rev > s).GroupBy(r => r.Kind))
                    removed[g.Key == "workbench" ? "workbenches" : g.Key + "s"] =new JsonArray(g.Select(r => (JsonNode)r.Id!).ToArray());
                o["removed"] = removed;
            }
            return o;
        }
    }

    private JsonObject PawnJson(PawnState p)
    {
        var live = Host.Live(p.Name);
        var cur = _current.GetValueOrDefault(p.Name);
        string state = !live.Online ? "offline" : p.Drafted && (cur == null || cur.Type is "order" or "idle") ? "drafted" : cur?.State ?? "idle";
        if (state == "working" && cur?.Kind == "go") state = "moving";
        return new JsonObject
        {
            ["name"] = p.Name, ["display"] = p.Display, ["kind"] = "pawn", ["online"] = live.Online, ["hp"] = live.Hp, ["food"] = live.Food,
            ["pos"] = new JsonArray(Math.Round(live.X, 2), Math.Round(live.Y, 2), Math.Round(live.Z, 2)), ["dim"] = live.Dim, ["drafted"] = p.Drafted,
            ["state"] = state,
            ["leaving"] = _leaving.ContainsKey(p.Name),
            ["offline_reason"] = OfflineReason(p, live),
            ["job"] = cur == null ? null : new JsonObject
            {
                ["type"] = cur.Type, ["label"] = cur.Label, ["target"] = cur.At?.Json(), ["progress"] = cur.Progress, ["since"] = cur.Since,
            },
            ["queue"] = new JsonArray(p.Queue.Select(q => (JsonNode)OrderJson(q, null)).ToArray()),
            ["skills"] = new JsonObject(p.Skills.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, new JsonObject
            {
                ["level"] = kv.Value.Level, ["xp"] = Math.Round(kv.Value.Xp, 1), ["passion"] = kv.Value.Passion,
            }))),
            ["traits"] = new JsonArray(p.Traits.Select(StoryCharacter.TraitOf).Where(t => t != null).Select(t => (JsonNode)new JsonObject
            {
                ["id"] = t!.Id, ["label"] = t.Label, ["effect"] = t.Effect,
            }).ToArray()),
            ["priorities"] = new JsonObject(WorkTypes.All.Select(t => new KeyValuePair<string, JsonNode?>(t, p.Priorities.GetValueOrDefault(t)))),
            ["schedule"] = new JsonArray(p.Schedule.Select(b => (JsonNode)(b ?? ScheduleBlocks.Anything)!).ToArray()),
            ["held"] = live.Held,
            ["inventory"] = new JsonArray(live.Inventory.Select(s => (JsonNode)s.Json()).ToArray()),
            ["xp_level"] = live.XpLevel,
        };
    }

    /// <summary>{online, wanted, cap_free, kept_for_regulars}: how many pawns are in, how many the colony wants, the server's seats.</summary>
    private JsonObject SeatsJson()
    {
        (int Free, int Kept)? s = null;
        try { s = Host.Seats(); } catch (Exception) { /* unknown */ }
        return new JsonObject
        {
            ["online"] = Pawns.Values.Count(p => Host.Live(p.Name).Online),
            ["wanted"] = Math.Max(Meta.Wanted, Pawns.Count),
            ["pending"] = Math.Max(0, Meta.Wanted - Pawns.Keys.Count(k => !_leaving.ContainsKey(k))), // asked for, not here yet ("joining…")
            ["cap_free"] = s?.Free, ["kept_for_regulars"] = s?.Kept,
        };
    }

    private static JsonObject OrderJson(Order q, string? pawn)
    {
        JsonNode? target = q.Entity is { } e ? new JsonObject { ["entity"] = e } : q.Area is { } a ? new JsonObject { ["area"] = a.Json() } : q.At?.Obj();
        var o = new JsonObject { ["id"] = q.Id, ["kind"] = q.Kind, ["target"] = target, ["design"] = q.Design, ["given"] = q.Given, ["left"] = q.Targets?.Count };
        if (pawn != null) o["pawn"] = pawn;
        return o;
    }

    private JsonObject DesignationJson(DesignationState d) => new()
    {
        ["id"] = d.Id, ["kind"] = d.Kind, ["work_type"] = WorkOf(d.Kind) is { Length: > 0 } w ? w : null,
        ["target"] = d.Entity is { } e ? new JsonObject { ["entity"] = e } : d.At?.Json(),
        ["blocks"] = d.Blocks == null ? null : new JsonArray(d.Blocks.Select(b => (JsonNode)b.Json()).ToArray()),
        ["label"] = d.Label, ["created"] = d.Created, ["status"] = d.Status, ["reason"] = d.Reason, ["fails"] = d.Fails, ["retry_at"] = RetryAt(d),
    };

    private static JsonObject BillJson(BillState b) => new()
    {
        ["id"] = b.Id, ["recipe"] = b.Recipe, ["mode"] = b.Mode, ["target"] = b.Target, ["done"] = b.Done, ["paused"] = b.Paused,
        ["suspended_reason"] = b.SuspendedReason,
        ["ingredients"] = new JsonObject
        {
            ["allow"] = new JsonArray(b.Ingredients.Allow.Select(a => (JsonNode)a!).ToArray()),
            ["deny"] = new JsonArray(b.Ingredients.Deny.Select(a => (JsonNode)a!).ToArray()),
        },
        ["radius"] = b.Radius, ["pawn"] = b.Pawn, ["min_skill"] = b.MinSkill, ["store"] = b.Store?.DeepClone(),
        ["for_build"] = b.ForBuild, ["for_item"] = b.ForItem, ["step"] = b.ForBuild != null ? b.Step : null,
        ["option"] = b.Option, ["prefer"] = b.Prefer, ["prefer_only"] = b.PreferOnly ? true : null, ["xp_needed"] = b.XpNeeded, ["last"] = b.Last,
    };
}
