using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

// ================================================================ the saved settings

/// <summary>A colony's gear settings (saved with its meta): policies, material tool rules, tool auto-crafting, spare tools.</summary>
public sealed class GearSettings
{
    public List<GearPolicy> Policies { get; set; } = new();
    public List<MaterialRule> Rules { get; set; } = new();
    /// <summary>A job that needs a tool nobody has crafts one through an auto bill "for tools" (default on).</summary>
    public bool AutoCraftTools { get; set; } = true;
    /// <summary>Keep this many spare tools of each of <see cref="SpareKinds"/> in storage (0 = off): auto bills, wood/stone first, iron
    /// when ingots are stored.</summary>
    public int SpareCount { get; set; }
    public List<string> SpareKinds { get; set; } = ["pickaxe", "axe", "shovel"];
    /// <summary>Tool items whose auto bill the owner cancelled: not crafted by themselves again.</summary>
    public List<string> NoCraft { get; set; } = new();
}

/// <summary>A named gear policy (the genre's outfit/assign): the tools a pawn carries, its weapon, when it wears armour, its food.</summary>
public sealed class GearPolicy
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Carry the tools the pawn's enabled work types use (mine: pickaxe + shovel, chop: axe, farm: hoe, build: pickaxe + axe).</summary>
    public bool ToolsByWork { get; set; } = true;
    /// <summary>Tool kinds carried on top of those.</summary>
    public List<string> Tools { get; set; } = new();
    /// <summary>none | best | sword | axe | bow.</summary>
    public string Weapon { get; set; } = "best";
    /// <summary>none | drafted | always | night.</summary>
    public string Armor { get; set; } = "drafted";
    /// <summary>Food items to carry (0..16).</summary>
    public int Food { get; set; }
    /// <summary>Carry one spare of each tool kind (a second pickaxe for a long dig); else extras go back to storage.</summary>
    public bool Spare { get; set; }
}

/// <summary>The owner's tool rule for a material (a block name or a group id): which tool, from which tier (never below the version's
/// floor), an enchantment wish and what to do when no enchanted tool is there.</summary>
public sealed class MaterialRule
{
    public string Material { get; set; } = "";
    public string? Tool { get; set; }
    public string? Tier { get; set; }
    /// <summary>fortune | silk_touch | any | null.</summary>
    public string? Enchant { get; set; }
    /// <summary>plain (use a plain tool ≥ the tier) | wait (wait for the enchanted one).</summary>
    public string Fallback { get; set; } = "plain";
}

/// <summary>A pawn's gear state (saved with the pawn).</summary>
public sealed class PawnGear
{
    /// <summary>Its policy's id (null = the default policy).</summary>
    public string? Policy { get; set; }
    /// <summary>What it held before it was drafted (restored on undraft).</summary>
    public string? RestoreHeld { get; set; }
    /// <summary>Armour the draft put on (taken off again on undraft unless the policy says always).</summary>
    public bool DraftArmor { get; set; }
    /// <summary>A drafted pawn's post (its last move order's cell): it walks back there after a fight.</summary>
    public P3? Post { get; set; }
    /// <summary>Gear work waiting to run (bulk gear ops, draft equip and restore), first first.</summary>
    public List<GearTask> Tasks { get; set; } = new();
}

/// <summary>One piece of gear work: what to take from which chest, what to hold, wear, take off, and what to store.</summary>
public sealed class GearTask
{
    public string Id { get; set; } = "";
    /// <summary>arm | armor | pickup | return | tools | draft | restore | policy.</summary>
    public string Op { get; set; } = "";
    public string Label { get; set; } = "";
    public List<GearFetch> Fetch { get; set; } = new();
    public string? Hold { get; set; }
    public string? HoldEnch { get; set; }
    public List<string> Wear { get; set; } = new();
    public bool Unwear { get; set; }
    /// <summary>Item names to store (return), each to its best chest.</summary>
    public List<GearPut> Store { get; set; } = new();
    /// <summary>Extra gear into a chest at the fetch step: of each item, all but <see cref="GearKeep.Keep"/> (the freshest) go.</summary>
    public List<GearKeep> Extras { get; set; } = new();
}

/// <summary>Store <paramref name="Count"/> of an item (id, meta: the record's) into <paramref name="Chest"/>, keeping <paramref name="Keep"/>
/// of that item (the freshest) in the bag.</summary>
public sealed record GearKeep(P3 Chest, int Id, int Meta, int Count, int Keep);

public sealed record GearFetch(P3 Chest, int Id, int Meta, int Count, string? Ench);
public sealed record GearPut(P3 Chest, int Id, int Meta, int Count, Dictionary<string, int>? Ench);

/// <summary>An enchanted item known to be in a chest.</summary>
public sealed record EnchStack(int Id, int Meta, Dictionary<string, int> Ench);

/// <summary>
/// Tools and gear (docs/web/story-mode.md "Tools and gear"): every dig takes at least the tool its material needs to drop anything
/// (the version's own rule; the owner's material rules may ask for more: a tier, a Fortune or Silk Touch tool), fetched from storage
/// when the pawn has none, else the target waits with why ("needs a stone pickaxe") and an auto bill "for tools" makes one. Gear
/// policies say what each pawn carries and wears; bulk gear ops (arm, armour, pick up, return, tools) spread the best items fairly over
/// a selection, reserving them so two pawns never go for the same one. Code here only plans: the pawn script does it with the usual
/// primitives and the hidden <c>equip</c>.
/// </summary>
public sealed partial class StoryColony
{
    public const string ToolsTag = "tools";
    public const string DefaultPolicy = "default";
    public static readonly IReadOnlyList<string> WeaponPrefs = ["none", "best", "sword", "axe", "bow"];
    public static readonly IReadOnlyList<string> ArmorModes = ["none", "drafted", "always", "night"];
    public static readonly IReadOnlyList<string> GearOps = ["arm", "armor", "pickup", "return", "tools"];
    /// <summary>A waiting job's tool want stays this long (its auto bill with it) after the last pawn that needed it asked.</summary>
    public static readonly TimeSpan ToolWantTtl = TimeSpan.FromMinutes(3);

    private readonly Dictionary<(ToolType Kind, int Level), DateTimeOffset> _toolWants = new();
    private readonly Dictionary<int, ToolPlan?> _toolPlans = new();
    private readonly Dictionary<string, DateTimeOffset> _policyAt = new(StringComparer.OrdinalIgnoreCase);

    private IGameData? Data => World?.Data;

    // ---------------------------------------------------------------- a pawn's gear as the director sees it

    /// <summary>The pawn's tools, weapons and armour slot by slot (from the body; a test's pawn: off its inventory list).</summary>
    internal List<GearSlot> GearOf(PawnLive live)
    {
        if (live.Gear is { } g) return g.ToList();
        var list = new List<GearSlot>();
        if (Data is not { } d) return list;
        int slot = 9;
        foreach (var s in live.Inventory)
        {
            if (StoryTools.IsGear(d, s.Id))
                for (int i = 0; i < Math.Max(1, s.Count) && i < 64; i++) list.Add(new GearSlot(slot++, s.Id, s.Meta, 1));
            else slot++;
        }
        return list;
    }

    private GearPolicy PolicyOf(PawnState p)
    {
        EnsureDefaultPolicy();
        return Meta.Gear.Policies.FirstOrDefault(x => x.Id == p.Gear.Policy) ?? Meta.Gear.Policies.First(x => x.Id == DefaultPolicy);
    }

    private void EnsureDefaultPolicy()
    {
        if (Meta.Gear.Policies.Any(x => x.Id == DefaultPolicy)) return;
        // Tools for their work, no weapon hoarding (the draft arms them with what they carry; "Arm" hands weapons out), armour when drafted.
        Meta.Gear.Policies.Insert(0, new GearPolicy { Id = DefaultPolicy, Name = "Default", Weapon = "none" });
    }

    /// <summary>What the colony's chests hold that gear work hasn't claimed yet: pending gear tasks' fetches and jobs in flight.</summary>
    /// <param name="replacing">Tasks about to be replaced (the same op again for the same pawns): what they claimed is free again.</param>
    private Dictionary<(P3 Chest, int Id, int Meta), int> GearClaims(Func<PawnState, GearTask, bool>? replacing = null)
    {
        var claims = new Dictionary<(P3, int, int), int>();
        foreach (var p in Pawns.Values)
            foreach (var t in p.Gear.Tasks)
            {
                // A task being run is counted by its job's takes below; one being replaced claims nothing.
                if (_current.TryGetValue(p.Name, out var run) && run.Source == "gear" && run.SourceId == t.Id || replacing?.Invoke(p, t) == true) continue;
                foreach (var f in t.Fetch) claims[(f.Chest, f.Id, f.Meta)] = claims.GetValueOrDefault((f.Chest, f.Id, f.Meta)) + f.Count;
            }
        foreach (var cur in _current.Values)
            foreach (var (chest, s) in cur.Takes) claims[(chest, s.Id, s.Meta)] = claims.GetValueOrDefault((chest, s.Id, s.Meta)) + s.Count;
        return claims;
    }

    /// <summary>One unit of stored gear: where it is, what, and its enchantments when the colony knows them.</summary>
    internal sealed record Unit(StorageState Store, int Id, int Meta, Dictionary<string, int>? Ench);

    /// <summary>Every unclaimed unit of stored gear matching <paramref name="pick"/>, enchanted ones named (nearest chests first).</summary>
    private List<Unit> StoredUnits(Func<int, int, bool> pick, P3 near, Dictionary<(P3 Chest, int Id, int Meta), int>? claims = null)
    {
        claims ??= _pickPawn != null ? (_pickClaims ??= GearClaims()) : GearClaims(); // read-only here: once per pick
        var list = new List<Unit>();
        foreach (var st in Storages.Where(s => ChestOpen(s.Chest)).OrderBy(s => s.Chest.Dist(near)))
            foreach (var c in st.Contents)
            {
                if (!pick(c.Id, c.Meta)) continue;
                int free = c.Count - claims.GetValueOrDefault((st.Chest, c.Id, c.Meta));
                if (free <= 0) continue;
                var ench = st.Enchanted.Where(e => e.Id == c.Id && e.Meta == c.Meta).ToList();
                for (int i = 0; i < free && i < 64; i++) list.Add(new Unit(st, c.Id, c.Meta, i < ench.Count ? ench[i].Ench : null));
            }
        return list;
    }

    private static bool Has(Dictionary<string, int>? ench, string? wish) =>
        string.IsNullOrEmpty(wish) || wish == "none" ? ench is not { Count: > 0 } : wish == "any" ? ench is { Count: > 0 } : ench?.GetValueOrDefault(wish) > 0;

    // ---------------------------------------------------------------- material rules

    /// <summary>The owner's rule for a block: its own, else its group's, else null.</summary>
    private MaterialRule? RuleFor(IGameData d, int blockId)
    {
        if (Meta.Gear.Rules.Count == 0) return null;
        if (blockId == 74) blockId = 73;
        var name = d.BlockExists(blockId) ? d.Block(blockId).Name : blockId.ToString();
        return Meta.Gear.Rules.FirstOrDefault(r => r.Material.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? (StoryTools.GroupOf(blockId) is { } g ? Meta.Gear.Rules.FirstOrDefault(r => r.Material == g.Id) : null);
    }

    /// <summary><c>POST …/material-rules</c>: {op:"set", rule:{material, tool?, tier?, enchant?, fallback?}} | {op:"remove", material}.</summary>
    public (JsonNode? Result, string? Error) MaterialRulesOp(JsonObject body)
    {
        if (World is not { } w) return (null, "no world view yet");
        var op = Str(body["op"]) ?? "";
        lock (_lock)
        {
            if (op == "remove") return RemoveMaterialRule(Str(body["material"]) ?? "");
            if (op != "set") return (null, "op must be set|remove");
            if (body["rule"] is not JsonObject r) return (null, "rule required");
            var (rule, err) = ParseMaterialRule(w, r);
            if (err != null) return (null, err);
            Meta.Gear.Rules.RemoveAll(x => x.Material.Equals(rule!.Material, StringComparison.OrdinalIgnoreCase));
            Meta.Gear.Rules.Add(rule!);
            Touch();
            _toolPlans.Clear();
            WakeIdle();
            return (new JsonObject { ["rule"] = RuleJson(w.Data, rule!) }, null);
        }
    }

    private (JsonNode? Result, string? Error) RemoveMaterialRule(string material)
    {
        int n = Meta.Gear.Rules.RemoveAll(r => r.Material.Equals(material, StringComparison.OrdinalIgnoreCase));
        if (n > 0) { Touch(); _toolPlans.Clear(); WakeIdle(); }
        return (new JsonObject { ["removed"] = n > 0 }, null);
    }

    /// <summary>What a rule is for: a material group or one block (its name), the blocks it covers, and how an answer names it.</summary>
    private sealed record RuleTarget(string Material, List<int> Members, string Label);

    /// <summary>A rule as the browser sent it, checked in order: material, tool, tier, enchant, fallback, then what can harvest it.</summary>
    private static (MaterialRule? Rule, string? Error) ParseMaterialRule(IStoryWorld w, JsonObject r)
    {
        var d = w.Data;
        var (target, err) = RuleTargetOf(d, Str(r["material"])?.Trim() ?? "");
        if (err != null) return (null, err);
        var (kind, level, toolErr) = RuleToolOf(r);
        if (toolErr != null) return (null, toolErr);
        var (enchant, fallback, restErr) = RuleRestOf(w, r);
        if (restErr != null) return (null, restErr);
        if (RuleHarvestError(d, target!, kind, level) is { } harvestErr) return (null, harvestErr);
        return (new MaterialRule
        {
            Material = target!.Material, Tool = kind is { } kk ? StoryTools.KindName(kk) : null, Tier = StoryTools.TierName(level), Enchant = enchant,
            Fallback = fallback,
        }, null);
    }

    private static (RuleTarget? Target, string? Error) RuleTargetOf(IGameData d, string material)
    {
        var group = StoryTools.Groups.FirstOrDefault(g => g.Id == material);
        int? block = group == null ? StoryTools.BlockOf(d, material) : null;
        if (group == null && block == null) return (null, $"unknown material '{material}'");
        if (block is { } bid) material = d.Block(bid).Name;
        List<int> members = group != null ? group.Members.Where(d.BlockExists).ToList() : [block!.Value];
        return (new RuleTarget(material, members, group != null ? group.Label.ToLowerInvariant() : material), null);
    }

    /// <summary>The rule's tool kind (null = any) and tier level (−1 = any).</summary>
    private static (ToolType? Kind, int Level, string? Error) RuleToolOf(JsonObject r)
    {
        var tool = Str(r["tool"]);
        ToolType? kind = tool == null ? null : StoryTools.Kind(tool);
        if (tool != null && kind == null) return (null, -1, $"tool must be {string.Join("|", StoryTools.Kinds)}");
        var tier = Str(r["tier"]);
        int level = StoryTools.Level(tier);
        if (tier != null && level < 0) return (null, -1, $"tier must be {string.Join("|", StoryTools.Tiers)}");
        return (kind, level, null);
    }

    /// <summary>The rule's enchantment (b1.8.1 has none) and its fallback when no such tool is there (plain | wait).</summary>
    private static (string? Enchant, string Fallback, string? Error) RuleRestOf(IStoryWorld w, JsonObject r)
    {
        var enchant = Str(r["enchant"]);
        if (enchant != null && !StoryTools.Enchants.Contains(enchant)) return (null, "", $"enchant must be {string.Join("|", StoryTools.Enchants)}");
        if (enchant != null && w.Version == "b1.8.1") return (null, "", "b1.8.1 has no enchantments");
        var fallback = Str(r["fallback"]) ?? "plain";
        if (fallback is not ("plain" or "wait")) return (null, "", "fallback must be plain|wait");
        return (enchant, fallback, null);
    }

    /// <summary>
    /// The floor: never below what the material needs to drop anything (a group: its lowest member's; each member keeps its own); and
    /// the rule's tool must harvest every member.
    /// </summary>
    private static string? RuleHarvestError(IGameData d, RuleTarget target, ToolType? kind, int level)
    {
        int floor = target.Members.Select(m => StoryTools.For(d, m).Floor).DefaultIfEmpty(-1).Min();
        if (level >= 0 && level < floor)
        {
            var first = target.Members.First(m => StoryTools.For(d, m).Floor == floor);
            return $"{target.Label} needs at least {StoryTools.Describe(StoryTools.For(d, first).Kind, floor)}";
        }
        foreach (var m in target.Members)
            if (MemberHarvestError(d, m, kind, level) is { } err) return err;
        return null;
    }

    private static string? MemberHarvestError(IGameData d, int m, ToolType? kind, int level)
    {
        var need = StoryTools.For(d, m);
        if (need.Hand || kind is not { } k) return null;
        int lv = Math.Max(Math.Max(level, need.Floor), 0);
        if (StoryTools.ItemFor(k, lv) is not { } id || !d.ItemExists(id) || !d.CanHarvest(m, id))
            return $"{StoryTools.Describe(k, lv)} can't harvest {d.Block(m).Name} (it needs {need.Text})";
        return null;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private JsonObject RuleJson(IGameData d, MaterialRule r)
    {
        var group = StoryTools.Groups.FirstOrDefault(g => g.Id == r.Material);
        var block = group == null ? StoryTools.BlockOf(d, r.Material) : null;
        string? tool = r.Tool;
        if (tool == null)
        {
            var members = group != null ? group.Members.Where(d.BlockExists).ToList() : block is { } b ? [b] : [];
            var needs = members.Select(m => StoryTools.For(d, m)).ToList();
            var t = needs.Select(n => n.Hand ? n.Fast : n.Kind).FirstOrDefault(x => x != ToolType.None);
            if (t == ToolType.None) t = needs.Select(n => n.Keep).FirstOrDefault(x => x != ToolType.None);
            tool = t == ToolType.None ? "pickaxe" : StoryTools.KindName(t); // the wire always names one
        }
        return new JsonObject
        {
            ["material"] = r.Material, ["label"] = group?.Label ?? (block is { } bb ? d.Block(bb).DisplayName : r.Material), ["tool"] = tool,
            ["tier"] = r.Tier, ["enchant"] = r.Enchant, ["fallback"] = r.Fallback, ["floor"] = StoryTools.TierName(StoryTools.FloorOf(d, r.Material)),
        };
    }

    // ---------------------------------------------------------------- policies

    /// <summary><c>POST …/policies</c>: {op:"set", policy} → {id} | {op:"remove", id} | {op:"assign", id, pawns}.</summary>
    public (JsonNode? Result, string? Error) PoliciesOp(JsonObject body)
    {
        var op = Str(body["op"]) ?? "";
        if (op == "assign") return AssignPolicy(body);
        lock (_lock)
        {
            EnsureDefaultPolicy();
            if (op == "remove") return RemovePolicy(Str(body["id"]));
            if (op != "set") return (null, "op must be set|remove|assign");
            if (body["policy"] is not JsonObject spec) return (null, "policy required");
            return SetPolicy(spec);
        }
    }

    private (JsonNode? Result, string? Error) AssignPolicy(JsonObject body)
    {
        var id = Str(body["id"]);
        var names = body["pawns"] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToList() : null;
        var (ps, err) = Resolve(names);
        if (err != null) return (null, err);
        lock (_lock)
        {
            EnsureDefaultPolicy();
            if (Meta.Gear.Policies.FirstOrDefault(x => x.Id == id) is not { } pol) return (null, "id: no such policy");
            foreach (var p in ps) { p.Gear.Policy = pol.Id == DefaultPolicy ? null : pol.Id; p.Rev = Touch(); _policyAt.Remove(p.Name); }
            return (new JsonObject { ["id"] = pol.Id, ["pawns"] = new JsonArray(ps.Select(p => (JsonNode)p.Name!).ToArray()) }, null);
        }
    }

    private (JsonNode? Result, string? Error) RemovePolicy(string? id)
    {
        if (id == DefaultPolicy) return (null, "the default policy can't be removed");
        if (Meta.Gear.Policies.FirstOrDefault(x => x.Id == id) is not { } gone) return (null, "id: no such policy");
        Meta.Gear.Policies.Remove(gone);
        foreach (var p in Pawns.Values.Where(p => p.Gear.Policy == gone.Id)) { p.Gear.Policy = null; p.Rev = Touch(); }
        Touch();
        return (new JsonObject { ["id"] = gone.Id, ["removed"] = true }, null);
    }

    /// <summary>A new policy, or the one <c>id</c> names updated: fields not given keep their value (a new one's defaults).</summary>
    private (JsonNode? Result, string? Error) SetPolicy(JsonObject spec)
    {
        var pid = Str(spec["id"]);
        var pol = pid == null ? null : Meta.Gear.Policies.FirstOrDefault(x => x.Id == pid);
        if (pid != null && pol == null) return (null, "id: no such policy");
        var draft = new GearPolicy();
        if ((PolicyName(spec, pol, draft) ?? PolicyTools(spec, pol, draft) ?? PolicyArms(spec, pol, draft) ?? PolicyAmounts(spec, pol, draft)) is { } err)
            return (null, err);
        pol ??= new GearPolicy { Id = NextId("pol") };
        if (!Meta.Gear.Policies.Contains(pol)) Meta.Gear.Policies.Add(pol);
        pol.Name = draft.Name; pol.Tools = draft.Tools; pol.Weapon = draft.Weapon; pol.Armor = draft.Armor; pol.Food = draft.Food;
        pol.ToolsByWork = draft.ToolsByWork; pol.Spare = draft.Spare;
        Touch();
        _policyAt.Clear();
        return (new JsonObject { ["id"] = pol.Id }, null);
    }

    private static string? PolicyName(JsonObject spec, GearPolicy? pol, GearPolicy draft)
    {
        var name = Str(spec["name"]) ?? pol?.Name;
        if (name is not { Length: >= 1 and <= 40 }) return "name: 1..40 characters";
        draft.Name = name;
        return null;
    }

    private static string? PolicyTools(JsonObject spec, GearPolicy? pol, GearPolicy draft)
    {
        draft.Tools = pol?.Tools ?? new List<string>();
        if (spec["tools"] is JsonArray ta) return PolicyToolList(ta, draft);
        return spec["tools"] != null ? "tools must be [kind]" : null;
    }

    private static string? PolicyToolList(JsonArray ta, GearPolicy draft)
    {
        var tools = new List<string>();
        foreach (var t in ta)
        {
            var k = t is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts.Trim().ToLowerInvariant() : null;
            if (k == null || StoryTools.Kind(k) == null) return $"tools: each one of {string.Join("|", StoryTools.Kinds)}";
            if (!tools.Contains(k)) tools.Add(k);
        }
        draft.Tools = tools;
        return null;
    }

    private static string? PolicyArms(JsonObject spec, GearPolicy? pol, GearPolicy draft)
    {
        draft.Weapon = Str(spec["weapon"]) ?? pol?.Weapon ?? "best";
        if (!WeaponPrefs.Contains(draft.Weapon)) return $"weapon must be {string.Join("|", WeaponPrefs)}";
        draft.Armor = Str(spec["armor"]) ?? pol?.Armor ?? "drafted";
        return ArmorModes.Contains(draft.Armor) ? null : $"armor must be {string.Join("|", ArmorModes)}";
    }

    private static string? PolicyAmounts(JsonObject spec, GearPolicy? pol, GearPolicy draft)
    {
        draft.Food = pol?.Food ?? 0;
        if (spec["food"] != null)
        {
            if (StoryJson.Int(spec["food"]) is not { } f || f is < 0 or > 16) return "food must be 0..16";
            draft.Food = f;
        }
        return PolicyFlags(spec, pol, draft);
    }

    private static string? PolicyFlags(JsonObject spec, GearPolicy? pol, GearPolicy draft)
    {
        if (PolicyFlag(spec["spare"], pol?.Spare ?? false, "spare must be true|false", out var spare) is { } serr) return serr;
        draft.Spare = spare;
        if (PolicyFlag(spec["tools_by_work"], pol?.ToolsByWork ?? true, "tools_by_work must be true|false", out var byWork) is { } berr) return berr;
        draft.ToolsByWork = byWork;
        return null;
    }

    /// <summary>A true|false field: given (a JSON value) it must be a bool; not given, the current value stays.</summary>
    private static string? PolicyFlag(JsonNode? n, bool current, string error, out bool value)
    {
        value = current;
        if (n is not JsonValue v) return null;
        if (!v.TryGetValue<bool>(out var b)) return error;
        value = b;
        return null;
    }

    private JsonArray PoliciesJson()
    {
        EnsureDefaultPolicy();
        return new JsonArray(Meta.Gear.Policies.Select(x => (JsonNode)new JsonObject
        {
            ["id"] = x.Id, ["name"] = x.Name, ["default"] = x.Id == DefaultPolicy, ["tools_by_work"] = x.ToolsByWork,
            ["tools"] = new JsonArray(x.Tools.Select(t => (JsonNode)t!).ToArray()), ["weapon"] = x.Weapon, ["armor"] = x.Armor, ["food"] = x.Food, ["spare"] = x.Spare,
        }).ToArray());
    }

    private JsonArray RulesJson() => World is { } w
        ? new JsonArray(Meta.Gear.Rules.Select(r => (JsonNode)RuleJson(w.Data, r)).ToArray()) : new JsonArray();

    /// <summary>The gear options (<c>POST …/options</c>): auto-crafting tools, spare tools.</summary>
    public string? SetGearOptions(bool? autoCraftTools, JsonObject? spare)
    {
        int? count = null;
        List<string>? kinds = null;
        if (spare != null)
        {
            if (spare["count"] != null && (StoryJson.Int(spare["count"]) is not { } c || c is < 0 or > 8)) return "spare_tools.count must be 0..8";
            count = StoryJson.Int(spare["count"]);
            if (spare["kinds"] is JsonArray ka)
            {
                kinds = new List<string>();
                foreach (var k in ka)
                {
                    var s = k is JsonValue kv && kv.TryGetValue<string>(out var ks) ? ks.Trim().ToLowerInvariant() : null;
                    if (s == null || StoryTools.Kind(s) == null) return $"spare_tools.kinds: each one of {string.Join("|", StoryTools.Kinds)}";
                    if (!kinds.Contains(s)) kinds.Add(s);
                }
            }
            else if (spare["kinds"] != null) return "spare_tools.kinds must be [kind]";
        }
        lock (_lock)
        {
            if (autoCraftTools is { } a) Meta.Gear.AutoCraftTools = a;
            if (count is { } n) Meta.Gear.SpareCount = n;
            if (kinds != null) Meta.Gear.SpareKinds = kinds;
            Touch();
            MaterialsChanged();
        }
        return null;
    }

    /// <summary>The options as they stand (saved first).</summary>
    public JsonObject OptionsNow()
    {
        Save(force: true);
        lock (_lock) return OptionsJson();
    }

    internal JsonObject GearOptionsJson() => new()
    {
        ["auto_craft_tools"] = Meta.Gear.AutoCraftTools,
        ["spare_tools"] = new JsonObject { ["count"] = Meta.Gear.SpareCount, ["kinds"] = new JsonArray(Meta.Gear.SpareKinds.Select(k => (JsonNode)k!).ToArray()) },
    };

    // ---------------------------------------------------------------- the tool a dig takes

    /// <summary>What a pawn digs a block with: the tool to hold (carried), or to fetch first, or why the target waits.</summary>
    /// <param name="Swap">Carried tools the one taken replaces: stored into the same chest at the fetch (never two of a kind for nothing).</param>
    internal sealed record ToolPlan(string Need, string? Hold, string? HoldEnch, Unit? Take, string? TakeEnch, string? Wait, IReadOnlyList<GearSlot>? Swap = null);

    /// <summary>
    /// The tool a dig of <paramref name="blockId"/> takes for this pawn (null = nothing to do about tools: the hand drops it and no rule
    /// asks for one). The minimum is the version's floor (never below it, whatever the rule says), raised by the owner's rule; a rule's
    /// enchantment wish takes an enchanted tool carried or stored, else waits (fallback wait) or takes a plain one. Plain tools: a fresh
    /// one carried (the lowest tier that does, never a worn one while a fresh one is around), else the nearest stored, else a worn one
    /// carried. None anywhere: the target waits ("needs a stone pickaxe") and an auto bill makes one.
    /// </summary>
    internal ToolPlan? PlanTool(PawnState p, PawnLive live, int blockId)
    {
        if (World is not { } w || blockId <= 0) return null;
        if (_toolPlans.TryGetValue(blockId, out var cached)) return cached;
        var plan = PlanToolCore(p, live, w.Data, blockId);
        _toolPlans[blockId] = plan;
        return plan;
    }

    private ToolPlan? PlanToolCore(PawnState p, PawnLive live, IGameData d, int blockId)
    {
        var need = StoryTools.For(d, blockId);
        var rule = RuleFor(d, blockId);
        var kind = rule?.Tool is { } rt && StoryTools.Kind(rt) is { } rk ? rk : need.Hand ? ToolType.None : need.Kind;
        if (kind == ToolType.None) return null; // the hand drops it and nobody asked for a tool
        int min = Math.Max(Math.Max(need.Floor, StoryTools.Level(rule?.Tier)), 0);
        string? ench = d.VersionId == "b1.8.1" ? null : rule?.Enchant;
        string label = StoryTools.Describe(kind, min);
        bool Fits(int id) => StoryTools.ToolOf(d, id) == kind && StoryTools.LevelOf(d, id) >= min && (need.Hand || d.CanHarvest(blockId, id));
        var carried = GearOf(live).Where(g => g.Slot >= 9 && Fits(g.Id)).ToList();
        bool Fresh(int id, int meta) => !StoryTools.Worn(d, id, meta);
        ToolPlan HoldIt(GearSlot g, string? e) => new(label, ItemName(g.Id, g.Meta), e ?? (g.Enchanted ? "any" : "none"), null, null, null);
        Unit? StoredBest(Func<Unit, bool> ok) => StoredUnits((id, meta) => Fits(id) && Fresh(id, meta), live.Block).Where(ok)
            .OrderBy(u => StoryTools.LevelOf(d, u.Id)).ThenBy(u => u.Meta).FirstOrDefault();
        if (ench != null)
        {
            if (carried.Where(g => g.Has(ench) && Fresh(g.Id, g.Meta)).OrderBy(g => g.Meta).FirstOrDefault() is { } eg) return HoldIt(eg, ench);
            if (StoredBest(u => Has(u.Ench, ench)) is { } eu) return new ToolPlan(label, ItemName(eu.Id, eu.Meta), ench, eu, ench, null);
            if (rule!.Fallback == "wait") return new ToolPlan(label, null, null, null, null, $"waiting for {label} with {ench.Replace('_', ' ')}");
        }
        // Plain: the lowest tier that does (good tools stay good), an enchanted one only when that's all there is.
        if (carried.Where(g => Fresh(g.Id, g.Meta)).OrderBy(g => g.Enchanted ? 1 : 0).ThenBy(g => StoryTools.LevelOf(d, g.Id)).ThenBy(g => g.Meta)
                .FirstOrDefault() is { } pg && (!pg.Enchanted || ench != null || StoredBest(u => u.Ench == null) == null))
            return HoldIt(pg, pg.Enchanted ? "any" : "none");
        if (StoredBest(u => u.Ench == null) is { } su)
        {
            // The plain tools of that kind it carries and the new one replaces (a wooden pickaxe for iron, a worn one) go back at once.
            int lv = StoryTools.LevelOf(d, su.Id);
            var swap = GearOf(live).Where(g => g.Slot >= 9 && StoryTools.ToolOf(d, g.Id) == kind && !g.Enchanted && StoryTools.LevelOf(d, g.Id) <= lv).ToList();
            return new ToolPlan(label, ItemName(su.Id, su.Meta), "none", su, "none", null, swap.Count > 0 ? swap : null);
        }
        if (carried.OrderBy(g => g.Meta).FirstOrDefault() is { } wg) return HoldIt(wg, null); // only worn ones: still better than losing the drop
        if (need.Hand && rule?.Fallback != "wait") return null; // a rule's wish only: the hand will do
        NoteToolWant(kind, min);
        return new ToolPlan(label, null, null, null, null, $"needs {label}");
    }

    private void NoteToolWant(ToolType kind, int level)
    {
        bool fresh = !_toolWants.ContainsKey((kind, level));
        _toolWants[(kind, level)] = Now;
        if (fresh) MaterialsChanged();
    }

    /// <summary>The block a dig job will find at a cell (the world's; a build's own expected material when the view can't tell).</summary>
    private int DigBlockAt(P3 at, int expected = 0)
    {
        if (World is not { } w) return expected;
        if (!w.Loaded(at.X, at.Z)) return expected;
        return w.Block(at.X, at.Y, at.Z).Id;
    }

    /// <summary>A dig job carries its tool: held before the work (<c>tool</c>, <c>tool_ench</c>) and fetched with it when stored.</summary>
    private static void ApplyTool(ActiveJob job, ToolPlan? plan, StoryColony c)
    {
        if (plan?.Hold is not { } hold) return;
        job.Json["tool"] = hold;
        job.Json["tool_ench"] = plan.HoldEnch;
        if (plan.Take is { } u)
        {
            var fetch = job.Json["fetch"] as JsonArray ?? new JsonArray();
            fetch.Add(new JsonObject { ["from"] = u.Store.Chest.ToString(), ["item"] = c.ItemName(u.Id, u.Meta), ["n"] = 1, ["ench"] = plan.TakeEnch ?? "none" });
            job.Json["fetch"] = fetch;
            job.Takes.Add((u.Store.Chest, new Stack(u.Id, u.Meta, 1)));
            if (u.Ench != null) c._enchTakes[job.Id] = (u.Store.Chest, new EnchStack(u.Id, u.Meta, u.Ench));
            if (plan.Swap is { Count: > 0 } swap)
            {
                // Into the chest it takes the new one from; of the taken item's id the new (freshest) one stays.
                var store = job.Json["store"] as JsonArray ?? new JsonArray();
                foreach (var g in swap.GroupBy(x => x.Id))
                    store.Add(new JsonObject { ["to"] = u.Store.Chest.ToString(), ["item"] = c.ItemName(g.Key, 0), ["keep"] = g.Key == u.Id ? 1 : 0 });
                job.Json["store"] = store;
                job.Puts.Add((u.Store.Chest, swap.Select(x => new Stack(x.Id, x.Meta, x.Count)).ToList()));
            }
        }
    }

    private readonly Dictionary<string, (P3 Chest, EnchStack Item)> _enchTakes = new(StringComparer.Ordinal);

    /// <summary>The job, carrying its tool plan (<see cref="ApplyTool"/>).</summary>
    private ActiveJob WithTool(ToolPlan? plan, ActiveJob job)
    {
        ApplyTool(job, plan, this);
        return job;
    }

    /// <summary>A build cell no pawn can dig for want of a tool: the build waits and says why (no alert, no failure).</summary>
    private void NoteBuildWait(BuildState b, string why)
    {
        why = WaitText(why)!;
        if (b.Status is "paused" or "done" || b.Status == "waiting" && b.Reason == why) return;
        b.Status = "waiting";
        b.Reason = why;
        b.Rev = Touch();
    }

    /// <summary>A build that waited for a tool goes on.</summary>
    private void ClearBuildWait(BuildState b)
    {
        if (b.Status != "waiting" || b.Reason is not { } r || !(r.StartsWith("needs ", StringComparison.Ordinal) || r.StartsWith("waiting for ", StringComparison.Ordinal))) return;
        b.Status = "active";
        b.Reason = null;
        b.Rev = Touch();
    }

    /// <summary>A designation no pawn could take for want of a tool shows why (no failure, no alert).</summary>
    private void NoteWait(DesignationState d, string? why)
    {
        why = WaitText(why);
        var status = why != null && d.Status is "open" or "waiting" ? "waiting" : why == null && d.Status == "waiting" ? "open" : d.Status;
        if (d.Wait == why && d.Status == status) return;
        d.Wait = why;
        d.Status = status;
        d.Rev = Bump();
    }

    /// <summary>What the colony is doing about a missing tool, per wait ("needs a stone pickaxe" → "crafting one" | "no cobblestone
    /// for it" | "no crafting table for it" | "auto-crafting tools is off"): the materials pass's tool step says.</summary>
    private readonly Dictionary<string, string> _toolWantNote = new(StringComparer.Ordinal);

    private string? WaitText(string? why) =>
        why != null && why.StartsWith("needs ", StringComparison.Ordinal) && _toolWantNote.TryGetValue(why, out var note) ? why + " — " + note : why;

    /// <summary>A job ended: the enchanted tool it took leaves the chest's record (whatever the outcome: it is in a pocket or not
    /// in the chest any more).</summary>
    private void GearTakesEnded(ActiveJob j, bool done)
    {
        if (!_enchTakes.Remove(j.Id, out var t) || !done) return;
        if (StorageAt(t.Chest) is { } st)
        {
            int i = st.Enchanted.FindIndex(e => e.Id == t.Item.Id && e.Meta == t.Item.Meta);
            if (i >= 0) { st.Enchanted.RemoveAt(i); st.Rev = Touch(); }
        }
    }

    // ---------------------------------------------------------------- auto bills "for tools"

    /// <summary>
    /// The materials pass's tool step (before the builds: a missing tool holds work up): each tool a waiting job needs, and the spare
    /// tools kept in storage, become auto bills "for tools" on the colony's crafting table when the unreserved stock makes them (a
    /// wooden pickaxe asked for is made of stone when there is cobblestone; spares are iron only from stored ingots, else stone, else
    /// wood; never smelted for). Returns the pool after their reservations.
    /// </summary>
    private Dictionary<(int Id, int Meta), int> ToolCrafts(IStoryWorld w, Dictionary<(int Id, int Meta), int> pool, Dictionary<(int Id, int Meta), int> reserved,
        List<Want> wants, bool hasTable)
    {
        foreach (var k in _toolWants.Where(kv => Now - kv.Value >= ToolWantTtl).Select(kv => kv.Key).ToList()) _toolWants.Remove(k);
        var g = Meta.Gear;
        NoteToolWants(g, hasTable);
        if (!g.AutoCraftTools || !hasTable) return pool;
        foreach (var ask in ToolAsks(g)) pool = CraftToolAsk(w.Data, g, ask, pool, reserved, wants);
        return pool;
    }

    /// <summary>Each wanted tool's wait says, to begin with, why nothing is made for it (the craft step below says better).</summary>
    private void NoteToolWants(GearSettings g, bool hasTable)
    {
        _toolWantNote.Clear();
        foreach (var ((kind, lvl), _) in _toolWants)
            _toolWantNote[$"needs {StoryTools.Describe(kind, lvl)}"] = !g.AutoCraftTools ? "auto-crafting tools is off" : !hasTable ? "no crafting table for it" : "no materials for it";
    }

    /// <summary>The tools to have: one of each a waiting job needs, then the spares kept per kind.</summary>
    private List<(ToolType Kind, int Level, int N, bool Spare)> ToolAsks(GearSettings g)
    {
        var asks = new List<(ToolType Kind, int Level, int N, bool Spare)>();
        foreach (var ((kind, lvl), _) in _toolWants) asks.Add((kind, lvl, 1, false));
        if (g.SpareCount > 0)
            foreach (var k in g.SpareKinds)
                if (StoryTools.Kind(k) is { } kk) asks.Add((kk, 0, g.SpareCount, true));
        return asks;
    }

    /// <summary>One asked-for tool the colony lacks: an auto bill for it at the lowest level the stock makes (the pool after it).</summary>
    private Dictionary<(int Id, int Meta), int> CraftToolAsk(IGameData d, GearSettings g, (ToolType Kind, int Level, int N, bool Spare) ask,
        Dictionary<(int Id, int Meta), int> pool, Dictionary<(int Id, int Meta), int> reserved, List<Want> wants)
    {
        var (kind, lvl, n, spare) = ask;
        int missing = n - ToolStock(d, kind, lvl);
        var noteKey = $"needs {StoryTools.Describe(kind, lvl)}";
        if (missing <= 0) return pool;
        int[] levels = spare ? [2, 1, 0] : lvl == 0 ? [1, 0] : [lvl];
        if (!spare) NoteToolShort(d, g, kind, levels[^1], noteKey, pool);
        foreach (var level in levels)
        {
            if (ToolBill(d, g, kind, level, missing, pool, reserved, wants) is not { } after) continue;
            if (!spare) _toolWantNote[noteKey] = "crafting one";
            return after;
        }
        return pool;
    }

    /// <summary>All the colony's fresh tools of a kind at a level or better: storage and every pawn's pocket (a pocketed pickaxe counts;
    /// live 11-04: one pawn held 13).</summary>
    private int ToolStock(IGameData d, ToolType kind, int lvl)
    {
        int have = Storages.Sum(s => s.Contents.Where(c => StoryTools.ToolOf(d, c.Id) == kind && StoryTools.LevelOf(d, c.Id) >= lvl && !StoryTools.Worn(d, c.Id, c.Meta)).Sum(c => c.Count));
        foreach (var pn in Pawns.Keys)
            have += GearOf(Host.Live(pn)).Count(x => x.Slot >= 9 && StoryTools.ToolOf(d, x.Id) == kind && StoryTools.LevelOf(d, x.Id) >= lvl && !StoryTools.Worn(d, x.Id, x.Meta));
        return have;
    }

    /// <summary>Why it can't be made, said with the wait: the raw material it is short of (or its bill was cancelled).</summary>
    private void NoteToolShort(IGameData d, GearSettings g, ToolType kind, int level, string noteKey, Dictionary<(int Id, int Meta), int> pool)
    {
        if (StoryTools.ItemFor(kind, level) is not { } want || !d.ItemExists(want)) return;
        var plan = PlanFor((want, 0), 1, pool);
        _toolWantNote[noteKey] = g.NoCraft.Contains(d.Item(want).Name) ? "its auto bill was cancelled"
            : plan.Missing.Count > 0 ? $"no {string.Join(", ", plan.Missing.Select(m => d.DisplayName(m.Id, Math.Max(0, m.Damage)).ToLowerInvariant()))} for it"
            : "no materials for it";
    }

    /// <summary>The tool's auto bills when the pool makes <paramref name="missing"/> of it with no smelting (iron only from ingots):
    /// their materials reserved, the pool after them returned; null = not this level.</summary>
    private Dictionary<(int Id, int Meta), int>? ToolBill(IGameData d, GearSettings g, ToolType kind, int level, int missing, Dictionary<(int Id, int Meta), int> pool,
        Dictionary<(int Id, int Meta), int> reserved, List<Want> wants)
    {
        if (StoryTools.ItemFor(kind, level) is not { } id || !d.ItemExists(id)) return null;
        var name = d.Item(id).Name;
        if (g.NoCraft.Contains(name)) return null;
        if (Feasible((id, 0), missing, pool) is not { } f || f.Plan.Steps.Any(s => s.IsSmelt)) return null; // never smelted for (iron only from ingots)
        foreach (var u in f.Plan.Used) Reserve(reserved, (u.Id, u.Damage), u.Count);
        int step = 0;
        foreach (var s in f.Plan.Steps)
            if (RecipeOf(d, s, intermediate: (s.Recipe?.OutputId ?? -1) != id) is { } r) wants.Add(new Want(ToolsTag, -1, name, r, s.OutputCount, step++, "crafting_table"));
        return f.After;
    }

    /// <summary>The owner cancelled a tool's auto bill: that tool isn't made by itself again (until the owner turns it back on by
    /// changing the tool options).</summary>
    internal void ToolCraftCancelled(string item)
    {
        if (!Meta.Gear.NoCraft.Contains(item)) Meta.Gear.NoCraft.Add(item);
        foreach (var b in Benches) b.Bills.RemoveAll(x => x.ForBuild == ToolsTag && x.ForItem == item && b.LoadedBill != x.Id);
        Touch();
    }

    // ---------------------------------------------------------------- gear tasks (bulk ops, draft equip and restore)

    /// <summary>The job for a pawn's first gear task (they run before orders and work), or null.</summary>
    internal ActiveJob? GearJob(PawnState p, PawnLive live)
    {
        while (p.Gear.Tasks.Count > 0)
        {
            var t = p.Gear.Tasks[0];
            if (t.Fetch.Count == 0 && t.Hold == null && t.Wear.Count == 0 && !t.Unwear && t.Store.Count == 0 && t.Extras.Count == 0) { p.Gear.Tasks.RemoveAt(0); p.Rev = Touch(); continue; }
            return TaskJob(p, t, "gear");
        }
        return null;
    }

    private ActiveJob TaskJob(PawnState p, GearTask t, string source)
    {
        var at = t.Fetch.FirstOrDefault()?.Chest ?? t.Extras.FirstOrDefault()?.Chest ?? t.Store.FirstOrDefault()?.Chest;
        var job = Job("gear", "equip", t.Label, at, "gear:" + p.Name, source, t.Id, p.Drafted ? "drafted" : "working", f =>
        {
            if (t.Fetch.Count > 0)
                f["fetch"] = new JsonArray(t.Fetch.Select(x => (JsonNode)new JsonObject
                {
                    ["from"] = x.Chest.ToString(), ["item"] = ItemName(x.Id, x.Meta), ["n"] = x.Count,
                    ["ench"] = StoryTools.IsGear(Data!, x.Id) ? x.Ench ?? "none" : null,
                }).ToArray());
            if (t.Hold != null) { f["hold"] = t.Hold; f["hold_ench"] = t.HoldEnch; }
            if (t.Extras.Count > 0)
                f["store"] = new JsonArray(t.Extras.GroupBy(x => (x.Chest, x.Id)).Select(g => (JsonNode)new JsonObject
                {
                    ["to"] = g.Key.Chest.ToString(), ["item"] = ItemName(g.Key.Id, 0), ["keep"] = g.First().Keep,
                }).ToArray());
            if (t.Wear.Count > 0) f["wear"] = new JsonArray(t.Wear.Select(x => (JsonNode)x!).ToArray());
            if (t.Unwear) f["unwear"] = true;
            if (t.Store.Count > 0)
                f["stash"] = new JsonArray(t.Store.GroupBy(x => x.Chest).Select(g => (JsonNode)new JsonObject
                {
                    ["to"] = g.Key.ToString(), ["items"] = new JsonArray(g.Select(x => ItemName(x.Id, x.Meta)).Distinct().Select(n => (JsonNode)n!).ToArray()),
                }).ToArray());
        }, xp: 0);
        foreach (var x in t.Fetch) job.Takes.Add((x.Chest, new Stack(x.Id, x.Meta, x.Count)));
        foreach (var g in t.Store.GroupBy(x => x.Chest)) job.Puts.Add((g.Key, g.Select(x => new Stack(x.Id, x.Meta, x.Count)).ToList()));
        foreach (var g in t.Extras.GroupBy(x => x.Chest)) job.Puts.Add((g.Key, g.Select(x => new Stack(x.Id, x.Meta, x.Count)).ToList()));
        return job;
    }

    /// <summary>A gear or policy job ended: the task goes (done or not: a failed fetch isn't retried in a loop), enchanted items'
    /// records follow them, a failure is said once.</summary>
    internal void GearEnded(PawnState p, ActiveJob j, bool ok, string? note)
    {
        var t = p.Gear.Tasks.FirstOrDefault(x => x.Id == j.SourceId);
        if (t != null) p.Gear.Tasks.Remove(t);
        else if (j.Source == "policy") t = _policyTasks.Remove(p.Name, out var pt) ? pt : null;
        p.Rev = Touch();
        if (t == null) return;
        if (ok)
        {
            foreach (var x in t.Fetch.Where(x => x.Ench is { } e && e != "none"))
                if (StorageAt(x.Chest) is { } st && st.Enchanted.FindIndex(e => e.Id == x.Id && e.Meta == x.Meta && Has(e.Ench, x.Ench)) is var i and >= 0)
                { st.Enchanted.RemoveAt(i); st.Rev = Touch(); }
            foreach (var x in t.Store.Where(x => x.Ench is { Count: > 0 }))
                if (StorageAt(x.Chest) is { } st) { st.Enchanted.Add(new EnchStack(x.Id, x.Meta, x.Ench!)); st.Rev = Touch(); }
        }
        else if (j.Source == "gear") Alert("info", $"{p.Display} couldn't finish \"{t.Label}\": {note ?? "failed"}", p.Name);
        else _policyAt[p.Name] = Now + TimeSpan.FromMinutes(2); // a policy fetch that failed rests
    }

    private readonly Dictionary<string, GearTask> _policyTasks = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- bulk gear ops

    /// <summary>Pawns in the order fair assignment serves them: the better fighter first (guard/hunt skill), then the one with guard
    /// higher up its priorities, then by name (deterministic).</summary>
    private static List<PawnState> CombatOrder(IEnumerable<PawnState> pawns) => pawns
        .OrderByDescending(p => Math.Max(p.Skills.GetValueOrDefault(WorkTypes.Guard)?.Level ?? 0, p.Skills.GetValueOrDefault(WorkTypes.Hunt)?.Level ?? 0))
        .ThenBy(p => p.Priorities.GetValueOrDefault(WorkTypes.Guard) is var g and > 0 ? g : 9)
        .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// <c>POST …/gear</c> {op: arm|armor|pickup|return|tools, pawns, item?, count?}: one op for a whole selection. The best items go one
    /// per pawn before anyone gets a second (arm: one weapon each, best fighters first; armour: the best piece of each slot goes to a
    /// different pawn), each fetched item reserved for its pawn. Every pawn gets a gear task (it runs before orders and work) and the
    /// answer says what each got and what was missing, plus one summary line.
    /// </summary>
    public (JsonNode? Result, string? Error) GearOp(string op, IReadOnlyList<PawnState> pawns, JsonObject body)
    {
        if (!GearOps.Contains(op)) return (null, $"op must be {string.Join("|", GearOps)}");
        if (World is not { } w) return (null, "no world view yet");
        var d = w.Data;
        (int Id, int Meta)? item = null;
        int count = 1;
        if (op == "pickup" && ParsePickup(d, body, out item, out count) is { } perr) return (null, perr);
        lock (_lock)
        {
            var run = NewGearRun(op, pawns, d);
            RunGearOp(run, item, count);
            return (GearCommit(run, item), null);
        }
    }

    /// <summary>The run for a bulk op: fighters first for arm and armour; what the same op's pending tasks claimed is free again.</summary>
    private GearRun NewGearRun(string op, IReadOnlyList<PawnState> pawns, IGameData d)
    {
        var claims = GearClaims((p, t) => t.Op == op && pawns.Contains(p));
        var order = op == "arm" || op == "armor" ? CombatOrder(pawns) : pawns.ToList();
        return new GearRun(op, d, Home() ?? new P3(0, 64, 0), claims, order, NextId);
    }

    private void RunGearOp(GearRun run, (int Id, int Meta)? item, int count)
    {
        switch (run.Op)
        {
            case "arm": GearArm(run); break;
            case "armor": GearArmor(run); break;
            case "pickup": GearPickup(run, item!.Value, count); break;
            case "return": GearReturn(run); break;
            case "tools": GearTools(run); break;
        }
    }

    /// <summary>A pickup's item ([id, meta] or a name) and count (1..64); the error, or null.</summary>
    private static string? ParsePickup(IGameData d, JsonObject body, out (int Id, int Meta)? item, out int count)
    {
        count = 1;
        item = PickupItem(d, body["item"]);
        if (item is not { } it0 || !d.ItemExists(it0.Id)) return "item must be [id, meta] (or an item name)";
        if (body["count"] != null && (StoryJson.Int(body["count"]) is not { } c || c is < 1 or > 64)) return "count must be 1..64";
        count = StoryJson.Int(body["count"]) ?? 1;
        return null;
    }

    private static (int Id, int Meta)? PickupItem(IGameData d, JsonNode? n)
    {
        if (n is JsonArray ia && ia.Count == 2 && StoryJson.Int(ia[0]) is { } iid && StoryJson.Int(ia[1]) is { } im) return (iid, im);
        return Str(n) is { } iname && d.ItemByNameWithDamage(iname) is { } hit ? (hit.Item.Id, hit.Damage) : null;
    }

    /// <summary>
    /// One bulk gear op in progress: the pawns in serving order, a task for each, what each got and what it was missing, and the
    /// stock claimed so far (an item fetched for one pawn isn't offered to the next).
    /// </summary>
    private sealed class GearRun
    {
        public readonly string Op;
        public readonly IGameData D;
        public readonly P3 Home;
        public readonly Dictionary<(P3 Chest, int Id, int Meta), int> Claims;
        public readonly List<PawnState> Order;
        public readonly Dictionary<string, GearTask> Tasks;
        public readonly Dictionary<string, List<string>> Got, Missing;

        public GearRun(string op, IGameData d, P3 home, Dictionary<(P3 Chest, int Id, int Meta), int> claims, List<PawnState> order, Func<string, string> nextId)
        {
            Op = op; D = d; Home = home; Claims = claims; Order = order;
            Tasks = order.ToDictionary(p => p.Name, p => new GearTask { Id = nextId("g"), Op = op }, StringComparer.OrdinalIgnoreCase);
            Got = order.ToDictionary(p => p.Name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
            Missing = order.ToDictionary(p => p.Name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public void Claim(Unit u) => Claims[(u.Store.Chest, u.Id, u.Meta)] = Claims.GetValueOrDefault((u.Store.Chest, u.Id, u.Meta)) + 1;

        /// <summary>An item as the answer names it, with its enchantments ("Diamond Sword (sharpness 2)").</summary>
        public string Label(int id, int meta, IReadOnlyDictionary<string, int>? ench) =>
            D.DisplayName(id, meta) + (ench is { Count: > 0 } ? " (" + string.Join(", ", ench.Select(e => e.Key.Replace('_', ' ') + " " + e.Value)) + ")" : "");

        /// <summary>A stored unit fetched for the pawn's task (claimed, labelled as got).</summary>
        public GearTask Fetch(PawnState p, Unit u)
        {
            Claim(u);
            var t = Tasks[p.Name];
            t.Fetch.Add(new GearFetch(u.Store.Chest, u.Id, u.Meta, 1, EnchWish(u.Ench)));
            Got[p.Name].Add(Label(u.Id, u.Meta, u.Ench));
            return t;
        }
    }

    /// <summary>Where a pawn's fetches start from: its body, or home while it is offline.</summary>
    private P3 GearFrom(GearRun run, PawnLive live) => live.Online ? live.Block : run.Home;

    /// <summary>Arm: one weapon each by the pawn's preference, best fighters first; a stored one only when it beats the pawn's own.</summary>
    private void GearArm(GearRun run)
    {
        var d = run.D;
        foreach (var p in run.Order)
        {
            var live = Host.Live(p.Name);
            var pref = PolicyOf(p).Weapon is "none" ? "best" : PolicyOf(p).Weapon;
            var own = GearOf(live).Where(g => g.Slot >= 9 && StoryTools.WeaponScore(d, g.Id, pref) > 0)
                .OrderByDescending(g => StoryTools.WeaponScore(d, g.Id, pref)).ThenBy(g => g.Meta).FirstOrDefault();
            int ownScore = own == null ? 0 : StoryTools.WeaponScore(d, own.Id, pref);
            var best = StoredUnits((id, meta) => StoryTools.WeaponScore(d, id, pref) > 0 && !StoryTools.Worn(d, id, meta), GearFrom(run, live), run.Claims)
                .OrderByDescending(u => StoryTools.WeaponScore(d, u.Id, pref)).ThenBy(u => u.Meta).FirstOrDefault();
            if (best != null && StoryTools.WeaponScore(d, best.Id, pref) > ownScore) ArmWith(run, p, best);
            else if (own != null) ArmOwn(run, p, own);
            else run.Missing[p.Name].Add("a weapon");
        }
    }

    private void ArmWith(GearRun run, PawnState p, Unit best)
    {
        var t = run.Fetch(p, best);
        t.Hold = ItemName(best.Id, best.Meta);
        t.HoldEnch = EnchWish(best.Ench);
    }

    private void ArmOwn(GearRun run, PawnState p, GearSlot own)
    {
        var t = run.Tasks[p.Name];
        t.Hold = ItemName(own.Id, own.Meta);
        t.HoldEnch = own.Enchanted ? "any" : "none";
        run.Got[p.Name].Add(run.Label(own.Id, own.Meta, own.Ench?.ToDictionary(e => e.Key, e => e.Value)) + " (own)");
    }

    private static readonly string[] ArmorSlots = ["head", "chest", "legs", "feet"];

    /// <summary>Armour slot by slot; who is served first rotates per slot, so the best helmet and the best chestplate go to different pawns.</summary>
    private void GearArmor(GearRun run)
    {
        var order = run.Order;
        for (int si = 0; si < ArmorSlots.Length; si++)
        {
            int first = si % Math.Max(1, order.Count);
            foreach (var p in order.Skip(first).Concat(order.Take(first)).ToList()) ArmorSlotFor(run, p, ArmorSlots[si]);
        }
    }

    /// <summary>One armour slot for one pawn: a better stored piece, else a better one in its bag, else (wearing none) missing.</summary>
    private void ArmorSlotFor(GearRun run, PawnState p, string slot)
    {
        var d = run.D;
        var live = Host.Live(p.Name);
        var gear = GearOf(live);
        var worn = gear.FirstOrDefault(g => g.Slot is >= 5 and <= 8 && StoryTools.ArmorSlot(g.Id) == slot);
        var bag = gear.Where(g => g.Slot >= 9 && StoryTools.ArmorSlot(g.Id) == slot).OrderByDescending(g => StoryTools.ArmorRank(g.Id)).FirstOrDefault();
        int have = Math.Max(worn == null ? 0 : StoryTools.ArmorRank(worn.Id), bag == null ? 0 : StoryTools.ArmorRank(bag.Id));
        var best = StoredUnits((id, meta) => StoryTools.ArmorSlot(id) == slot && !StoryTools.Worn(d, id, meta), GearFrom(run, live), run.Claims)
            .OrderByDescending(u => StoryTools.ArmorRank(u.Id)).ThenBy(u => u.Meta).FirstOrDefault();
        if (best != null && StoryTools.ArmorRank(best.Id) > have) run.Fetch(p, best).Wear.Add(ItemName(best.Id, best.Meta));
        else if (bag != null && (worn == null || StoryTools.ArmorRank(bag.Id) > StoryTools.ArmorRank(worn.Id)))
        {
            run.Tasks[p.Name].Wear.Add(ItemName(bag.Id, bag.Meta));
            run.Got[p.Name].Add(run.Label(bag.Id, bag.Meta, null) + " (own)");
        }
        else if (worn == null) run.Missing[p.Name].Add("a " + ArmorPieceName(slot));
    }

    private static string ArmorPieceName(string slot) => slot switch { "head" => "helmet", "chest" => "chestplate", "legs" => "pair of leggings", _ => "pair of boots" };

    /// <summary>Pick up: up to <paramref name="count"/> of the item each (any meta when it is negative), worn or held when it is gear.</summary>
    private void GearPickup(GearRun run, (int Id, int Meta) item, int count)
    {
        var d = run.D;
        var (iid, imeta) = item;
        var name = d.DisplayName(iid, Math.Max(0, imeta));
        foreach (var p in run.Order)
        {
            var live = Host.Live(p.Name);
            var units = StoredUnits((id, meta) => id == iid && (imeta < 0 || meta == imeta), GearFrom(run, live), run.Claims).Take(count).ToList();
            var t = run.Tasks[p.Name];
            foreach (var g in units.GroupBy(u => (u.Store.Chest, u.Id, u.Meta)))
            {
                foreach (var u in g) run.Claim(u);
                t.Fetch.Add(new GearFetch(g.Key.Chest, g.Key.Id, g.Key.Meta, g.Count(), StoryTools.IsGear(d, g.Key.Id) ? EnchWish(g.First().Ench) : null));
            }
            if (units.Count > 0) run.Got[p.Name].Add($"{units.Count} {name}");
            if (units.Count < count) run.Missing[p.Name].Add($"{count - units.Count} {name}");
            if (units.Count > 0 && StoryTools.IsGear(d, iid)) PickedGear(d, t, iid, Math.Max(0, imeta), units[0]);
        }
    }

    /// <summary>Picked-up gear goes on: armour worn, a weapon held.</summary>
    private void PickedGear(IGameData d, GearTask t, int id, int meta, Unit first)
    {
        if (StoryTools.ArmorSlot(id) != null) t.Wear.Add(ItemName(id, meta));
        else if (StoryTools.IsWeapon(d, id)) { t.Hold = ItemName(id, meta); t.HoldEnch = EnchWish(first.Ench); }
    }

    /// <summary>Return: armour off, every piece of gear into the best chest for it.</summary>
    private void GearReturn(GearRun run)
    {
        foreach (var p in run.Order)
        {
            var live = Host.Live(p.Name);
            var gear = GearOf(live);
            var t = run.Tasks[p.Name];
            if (gear.Any(g => g.Slot is >= 5 and <= 8)) t.Unwear = true;
            foreach (var g in gear) ReturnOne(run, p, t, g, GearFrom(run, live));
            if (gear.Count == 0) run.Missing[p.Name].Add("nothing to return");
        }
    }

    private void ReturnOne(GearRun run, PawnState p, GearTask t, GearSlot g, P3 from)
    {
        if (BestChest(g.Id, g.Meta, from) is not { } st) { run.Missing[p.Name].Add($"a chest for {run.D.DisplayName(g.Id, g.Meta)}"); return; }
        var ench = g.Ench?.ToDictionary(e => e.Key, e => e.Value);
        t.Store.Add(new GearPut(st.Chest, g.Id, g.Meta, g.Count, ench));
        run.Got[p.Name].Add(run.Label(g.Id, g.Meta, g.Ench?.ToDictionary(e => e.Key, e => e.Value)) + " stored");
    }

    private static readonly ToolType[] BulkToolKinds = [ToolType.Pickaxe, ToolType.Axe, ToolType.Shovel, ToolType.Hoe, ToolType.Shears, ToolType.Sword];

    /// <summary>Tools: kind by kind, the pawns that use it most first (their priority for the work that needs it, then skill).</summary>
    private void GearTools(GearRun run)
    {
        var wantsBy = run.Order.ToDictionary(p => p.Name, p => KindsFor(p), StringComparer.OrdinalIgnoreCase);
        foreach (var kind in BulkToolKinds)
        {
            var users = run.Order.Where(p => wantsBy[p.Name].Contains(kind)).OrderBy(p => UsePriority(p, kind)).ThenByDescending(p => UseSkill(p, kind))
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var p in users) ToolFor(run, p, kind);
        }
        foreach (var p in run.Order) if (wantsBy[p.Name].Count == 0) run.Missing[p.Name].Add("no tools in its policy");
    }

    /// <summary>One tool kind for one pawn that has no fresh one: the best stored (plain before enchanted), else missing.</summary>
    private void ToolFor(GearRun run, PawnState p, ToolType kind)
    {
        var d = run.D;
        var live = Host.Live(p.Name);
        if (GearOf(live).Any(g => g.Slot >= 9 && StoryTools.ToolOf(d, g.Id) == kind && !StoryTools.Worn(d, g.Id, g.Meta))) return;
        var best = StoredUnits((id, meta) => StoryTools.ToolOf(d, id) == kind && !StoryTools.Worn(d, id, meta), GearFrom(run, live), run.Claims)
            .OrderBy(u => u.Ench is { Count: > 0 } ? 1 : 0).ThenByDescending(u => StoryTools.LevelOf(d, u.Id)).ThenBy(u => u.Meta).FirstOrDefault();
        if (best == null) { run.Missing[p.Name].Add(StoryTools.Describe(kind, -1)); return; }
        run.Fetch(p, best);
    }

    /// <summary>Each pawn's task goes in (replacing a pending one of the same op) when it has anything to do; the answer per pawn and
    /// its summary line.</summary>
    private JsonObject GearCommit(GearRun run, (int Id, int Meta)? item)
    {
        var results = new JsonArray();
        foreach (var p in run.Order)
        {
            var t = run.Tasks[p.Name];
            t.Label = GearTaskLabel(run, item);
            if (HasGearWork(t)) QueueGearTask(p, t, run.Op);
            results.Add(new JsonObject
            {
                ["pawn"] = p.Name, ["display"] = p.Display,
                ["got"] = new JsonArray(run.Got[p.Name].Select(x => (JsonNode)x!).ToArray()),
                ["missing"] = new JsonArray(run.Missing[p.Name].Select(x => (JsonNode)x!).ToArray()),
            });
        }
        var parts = run.Order.Select(p => GearSummaryPart(run, p)).ToList();
        return new JsonObject { ["results"] = results, ["summary"] = $"{GearVerb(run.Op)}: " + string.Join("; ", parts) };
    }

    private static string GearTaskLabel(GearRun run, (int Id, int Meta)? item) => run.Op switch
    {
        "arm" => "arming", "armor" => "putting on armour", "pickup" => "picking up " + run.D.DisplayName(item!.Value.Id, Math.Max(0, item.Value.Meta)).ToLowerInvariant(),
        "return" => "returning gear", _ => "fetching tools",
    };

    private static string GearVerb(string op) => op switch { "arm" => "Arm", "armor" => "Armour", "pickup" => "Pick up", "return" => "Return gear", _ => "Tools" };

    private static bool HasGearWork(GearTask t) => t.Fetch.Count > 0 || t.Hold != null || t.Wear.Count > 0 || t.Unwear || t.Store.Count > 0;

    private void QueueGearTask(PawnState p, GearTask t, string op)
    {
        p.Gear.Tasks.RemoveAll(x => x.Op == op); // the same op again replaces the pending one
        p.Gear.Tasks.Add(t);
        p.Rev = Touch();
        if (_current.TryGetValue(p.Name, out var cur) && cur.Source is "none") Supersede(p, "gear: " + t.Label);
    }

    /// <summary>"Display: what it got (missing …)"; nothing to return isn't called missing.</summary>
    private static string GearSummaryPart(GearRun run, PawnState p)
    {
        var got = run.Got[p.Name];
        var miss = run.Missing[p.Name];
        return $"{p.Display}: " + (got.Count > 0 ? string.Join(", ", got) : "nothing")
            + (miss.Count > 0 && miss[0] != "nothing to return" ? $" (missing {string.Join(", ", miss)})" : "");
    }

    private static string? EnchWish(IReadOnlyDictionary<string, int>? ench) => ench is { Count: > 0 } e ? e.Keys.First() : "none";

    /// <summary>The tool kinds a pawn's policy has it carry: its own list, plus its enabled work types' when "tools by work".</summary>
    private HashSet<ToolType> KindsFor(PawnState p)
    {
        var pol = PolicyOf(p);
        var set = pol.Tools.Select(StoryTools.Kind).Where(k => k != null).Select(k => k!.Value).ToHashSet();
        if (pol.ToolsByWork)
            foreach (var t in WorkTypes.All.Where(t => p.Priorities.GetValueOrDefault(t) is >= 1 and <= 4))
                foreach (var k in StoryTools.ForWork(t)) set.Add(k);
        return set;
    }

    private static int UsePriority(PawnState p, ToolType kind) =>
        WorkTypes.All.Where(t => StoryTools.ForWork(t).Contains(kind)).Select(t => p.Priorities.GetValueOrDefault(t)).Where(v => v is >= 1 and <= 4).DefaultIfEmpty(9).Min();

    private static int UseSkill(PawnState p, ToolType kind) =>
        WorkTypes.All.Where(t => StoryTools.ForWork(t).Contains(kind)).Select(t => p.Skills.GetValueOrDefault(t)?.Level ?? 0).DefaultIfEmpty(0).Max();

    // ---------------------------------------------------------------- the policy kept up (idle, before work)

    /// <summary>How often a pawn's gear is checked against its policy (when nothing more urgent is on).</summary>
    public static readonly TimeSpan PolicyEvery = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The pawn's gear against its policy: tools its work needs that it lacks (or only has worn), a weapon, armour to wear now (always,
    /// at night) or take off (not now), food. Fetched from storage only (never crafted here), unclaimed items only. Null = nothing to do.
    /// </summary>
    internal ActiveJob? PolicyJob(PawnState p, PawnLive live)
    {
        if (World is not { } w || p.Drafted || !live.Online) return null;
        if (_policyAt.TryGetValue(p.Name, out var next) && next > Now) return null;
        _policyAt[p.Name] = Now + PolicyEvery;
        var d = w.Data;
        var pol = PolicyOf(p);
        var gear = GearOf(live);
        var claims = GearClaims();
        var t = new GearTask { Id = NextId("g"), Op = "policy", Label = "gearing up" };
        var from = live.Block;
        gear = KeepUpExtras(p, live, t, gear, from);
        KeepUpTools(p, d, gear, t, from, claims);
        KeepUpWeapon(pol, d, gear, t, from, claims);
        KeepUpArmor(p, pol, d, gear, t, from, claims);
        KeepUpFood(pol, w, live, t, from, claims);
        if (NothingToGear(t)) return null;
        _policyTasks[p.Name] = t;
        return TaskJob(p, t, "policy");
    }

    private static bool NothingToGear(GearTask t) => t.Fetch.Count == 0 && t.Wear.Count == 0 && !t.Unwear && t.Store.Count == 0 && t.Extras.Count == 0;

    /// <summary>A stored unit the policy fetches: claimed (no other plan counts it), one of it fetched.</summary>
    private static void KeepUpFetch(Dictionary<(P3 Chest, int Id, int Meta), int> claims, GearTask t, Unit u)
    {
        claims[(u.Store.Chest, u.Id, u.Meta)] = claims.GetValueOrDefault((u.Store.Chest, u.Id, u.Meta)) + 1;
        t.Fetch.Add(new GearFetch(u.Store.Chest, u.Id, u.Meta, 1, EnchWish(u.Ench)));
    }

    /// <summary>
    /// What it carries beyond its policy goes back first: colony stock other pawns can take (live 11-04: one pawn held 13 stone pickaxes,
    /// crafted for spares and kept as its "own tools"). The gear the rest of the check sees: what stays.
    /// </summary>
    private List<GearSlot> KeepUpExtras(PawnState p, PawnLive live, GearTask t, List<GearSlot> gear, P3 from)
    {
        if (ExtrasOf(p, live, from) is not { Count: > 0 } extras) return gear;
        t.Extras.AddRange(extras);
        t.Label = "returning extra gear";
        return gear.Where(g => !extras.Any(x => x.Id == g.Id)).Concat(gear.Where(g => extras.Any(x => x.Id == g.Id))
            .GroupBy(g => g.Id).SelectMany(grp => grp.OrderBy(g => g.Meta).Take(extras.First(x => x.Id == grp.Key).Keep))).ToList();
    }

    /// <summary>Each tool kind its work needs that it lacks (or only has worn): the best fresh one stored; the worn one goes back.</summary>
    private void KeepUpTools(PawnState p, IGameData d, List<GearSlot> gear, GearTask t, P3 from, Dictionary<(P3 Chest, int Id, int Meta), int> claims)
    {
        foreach (var kind in KindsFor(p))
        {
            var mine = gear.Where(g => g.Slot >= 9 && StoryTools.ToolOf(d, g.Id) == kind).ToList();
            if (mine.Any(g => !StoryTools.Worn(d, g.Id, g.Meta))) continue;
            var best = StoredUnits((id, meta) => StoryTools.ToolOf(d, id) == kind && !StoryTools.Worn(d, id, meta), from, claims)
                .OrderBy(u => u.Ench is { Count: > 0 } ? 1 : 0).ThenByDescending(u => StoryTools.LevelOf(d, u.Id)).ThenBy(u => u.Meta).FirstOrDefault();
            if (best == null) continue;
            KeepUpFetch(claims, t, best);
            // The worn one goes back to storage (a worn tool never breaks mid-job while a fresh one is around).
            foreach (var old in mine)
                if (BestChest(old.Id, old.Meta, from) is { } st) t.Store.Add(new GearPut(st.Chest, old.Id, old.Meta, 1, old.Ench?.ToDictionary(e => e.Key, e => e.Value)));
        }
    }

    /// <summary>No fresh weapon of its policy's kind carried: the best stored one (a "best" policy takes any; the others a real one).</summary>
    private void KeepUpWeapon(GearPolicy pol, IGameData d, List<GearSlot> gear, GearTask t, P3 from, Dictionary<(P3 Chest, int Id, int Meta), int> claims)
    {
        if (pol.Weapon == "none" || gear.Any(g => g.Slot >= 9 && StoryTools.WeaponScore(d, g.Id, pol.Weapon) > 0 && !StoryTools.Worn(d, g.Id, g.Meta))) return;
        if (StoredUnits((id, meta) => StoryTools.WeaponScore(d, id, pol.Weapon) > 0 && !StoryTools.Worn(d, id, meta), from, claims)
                .OrderByDescending(u => StoryTools.WeaponScore(d, u.Id, pol.Weapon)).FirstOrDefault() is not { } wu
            || pol.Weapon != "best" && StoryTools.WeaponScore(d, wu.Id, pol.Weapon) < 200) return;
        KeepUpFetch(claims, t, wu);
    }

    /// <summary>Armour on now (always, or at night by a night policy), else off when it shouldn't be worn (kept in the bag).</summary>
    private void KeepUpArmor(PawnState p, GearPolicy pol, IGameData d, List<GearSlot> gear, GearTask t, P3 from, Dictionary<(P3 Chest, int Id, int Meta), int> claims)
    {
        bool night = HourNow is >= 13 and <= 22; // dusk..dawn (hour 0 = 06:00)
        bool wearNow = pol.Armor == "always" || pol.Armor == "night" && night;
        var worn = gear.Where(g => g.Slot is >= 5 and <= 8).ToList();
        if (wearNow) KeepUpWear(d, gear, worn, t, from, claims);
        else if (worn.Count > 0 && (pol.Armor is "none" or "night") && !p.Gear.DraftArmor) t.Unwear = true; // not now: off, kept in the bag
    }

    private static readonly string[] ArmorSlotOrder = ["head", "chest", "legs", "feet"];

    /// <summary>Each armour slot it doesn't wear: its best piece in the bag, else the best stored one, fetched.</summary>
    private void KeepUpWear(IGameData d, List<GearSlot> gear, List<GearSlot> worn, GearTask t, P3 from, Dictionary<(P3 Chest, int Id, int Meta), int> claims)
    {
        foreach (var slot in ArmorSlotOrder)
        {
            if (worn.Any(g => StoryTools.ArmorSlot(g.Id) == slot)) continue;
            if (gear.Where(g => g.Slot >= 9 && StoryTools.ArmorSlot(g.Id) == slot).OrderByDescending(g => StoryTools.ArmorRank(g.Id)).FirstOrDefault() is { } bag)
                t.Wear.Add(ItemName(bag.Id, bag.Meta));
            else if (StoredUnits((id, meta) => StoryTools.ArmorSlot(id) == slot && !StoryTools.Worn(d, id, meta), from, claims)
                         .OrderByDescending(u => StoryTools.ArmorRank(u.Id)).FirstOrDefault() is { } au)
            {
                KeepUpFetch(claims, t, au);
                t.Wear.Add(ItemName(au.Id, au.Meta));
            }
        }
    }

    /// <summary>Less than half its policy's food carried: stored food up to the policy's amount.</summary>
    private void KeepUpFood(GearPolicy pol, IStoryWorld w, PawnLive live, GearTask t, P3 from, Dictionary<(P3 Chest, int Id, int Meta), int> claims)
    {
        if (pol.Food <= 0 || w.Data is not { } fd) return;
        int food = live.Inventory.Where(s => fd.Food(s.Id, s.Meta) != null && !Raw(w, s.Id)).Sum(s => s.Count);
        if (food * 2 >= pol.Food
            || StoredUnits((id, meta) => fd.Food(id, meta) != null && !Raw(w, id), from, claims).Take(pol.Food - food).ToList() is not { Count: > 0 } fu) return;
        foreach (var g in fu.GroupBy(u => (u.Store.Chest, u.Id, u.Meta)))
            t.Fetch.Add(new GearFetch(g.Key.Chest, g.Key.Id, g.Key.Meta, g.Count(), null));
    }

    /// <summary>
    /// The gear a pawn keeps (docs/web/story-mode.md "Tools and gear"): what it wears; per tool kind its policy (or a material rule)
    /// asks for, the best fresh one (plus a plain one beside an enchanted one when a rule marks a material, plus one spare when the
    /// policy says so); its best weapon; per armour slot it doesn't wear, its best piece unless its armour mode is none. The rest is extra.
    /// </summary>
    internal HashSet<GearSlot> KeepOf(PawnState p, PawnLive live)
    {
        var keep = new HashSet<GearSlot>();
        if (Data is not { } d) return keep;
        var pol = PolicyOf(p);
        var gear = GearOf(live);
        foreach (var g in gear.Where(g => g.Slot is >= 5 and <= 8)) keep.Add(g);
        var kinds = KindsFor(p);
        foreach (var r in Meta.Gear.Rules) if (StoryTools.Kind(r.Tool) is { } rk) kinds.Add(rk);
        if (_current.TryGetValue(p.Name, out var cur) && cur.Json["tool"] is JsonValue tv && tv.TryGetValue<string>(out var tn)
            && d.ItemByName(tn) is { } ti && StoryTools.ToolOf(d, ti.Id) is var tk and not ToolType.None) kinds.Add(tk);
        bool marked = Meta.Gear.Rules.Any(r => r.Enchant != null);
        foreach (var kind in kinds)
        {
            var mine = gear.Where(g => g.Slot >= 9 && StoryTools.ToolOf(d, g.Id) == kind)
                .OrderBy(g => StoryTools.Worn(d, g.Id, g.Meta) ? 1 : 0).ThenByDescending(g => StoryTools.LevelOf(d, g.Id)).ThenBy(g => g.Enchanted ? 0 : 1)
                .ThenBy(g => g.Meta).ToList();
            if (mine.Count == 0) continue;
            keep.Add(mine[0]);
            if (marked && mine[0].Enchanted && mine.FirstOrDefault(g => !g.Enchanted && !StoryTools.Worn(d, g.Id, g.Meta)) is { } plain) keep.Add(plain);
            if (pol.Spare && mine.Skip(1).FirstOrDefault(g => !keep.Contains(g) && !StoryTools.Worn(d, g.Id, g.Meta)) is { } spareOne) keep.Add(spareOne);
        }
        var pref = pol.Weapon is "none" ? "best" : pol.Weapon;
        if (gear.Where(g => g.Slot >= 9 && StoryTools.WeaponScore(d, g.Id, pref) > 0 && StoryTools.ToolOf(d, g.Id) != ToolType.Axe)
                .OrderByDescending(g => StoryTools.WeaponScore(d, g.Id, pref)).ThenBy(g => g.Meta).FirstOrDefault() is { } weapon) keep.Add(weapon);
        if (pol.Armor != "none")
            foreach (var slot in new[] { "head", "chest", "legs", "feet" })
            {
                if (gear.Any(g => g.Slot is >= 5 and <= 8 && StoryTools.ArmorSlot(g.Id) == slot)) continue;
                if (gear.Where(g => g.Slot >= 9 && StoryTools.ArmorSlot(g.Id) == slot).OrderByDescending(g => StoryTools.ArmorRank(g.Id)).FirstOrDefault() is { } a)
                    keep.Add(a);
            }
        return keep;
    }

    /// <summary>The pawn's extra gear (see <see cref="KeepOf"/>), per item: how many go into which chest and how many of it stay.</summary>
    internal List<GearKeep> ExtrasOf(PawnState p, PawnLive live, P3 from)
    {
        var list = new List<GearKeep>();
        if (Data is not { } d) return list;
        var keep = KeepOf(p, live);
        // One stray tool of a kind its work doesn't use (a pickaxe a hauler found) may stay, unless another pawn needs that kind and
        // storage has none: then it goes at once. More than one is a hoard: always back (live 11-04: 13 stone pickaxes in one pocket).
        var gearNow = GearOf(live);
        foreach (var kind in gearNow.Where(g => g.Slot >= 9 && StoryTools.IsTool(d, g.Id)).Select(g => StoryTools.ToolOf(d, g.Id)).Distinct().ToList())
        {
            if (gearNow.Any(g => keep.Contains(g) && g.Slot >= 9 && StoryTools.ToolOf(d, g.Id) == kind) || Demand(p, kind)) continue;
            if (gearNow.Where(g => g.Slot >= 9 && StoryTools.ToolOf(d, g.Id) == kind).OrderBy(g => StoryTools.Worn(d, g.Id, g.Meta) ? 1 : 0)
                    .ThenByDescending(g => StoryTools.LevelOf(d, g.Id)).ThenBy(g => g.Meta).FirstOrDefault() is { } stray) keep.Add(stray);
        }
        foreach (var grp in gearNow.Where(g => g.Slot >= 9).GroupBy(g => (g.Id, g.Meta)))
        {
            int extra = grp.Sum(g => g.Count) - grp.Where(keep.Contains).Sum(g => g.Count);
            if (extra <= 0) continue;
            if (BestChest(grp.Key.Id, grp.Key.Meta, from) is not { } st) continue; // nowhere to put it: kept (no chest takes it)
            int keepOfId = gearNow.Where(g => g.Slot >= 9 && g.Id == grp.Key.Id && keep.Contains(g)).Sum(g => g.Count);
            list.Add(new GearKeep(st.Chest, grp.Key.Id, grp.Key.Meta, extra, keepOfId));
        }
        return list;
    }

    /// <summary>Another online pawn's policy wants this tool kind, it has no fresh one, and storage has none to give it.</summary>
    private bool Demand(PawnState p, ToolType kind)
    {
        if (Data is not { } d) return false;
        bool stored = StoredUnits((id, meta) => StoryTools.ToolOf(d, id) == kind && !StoryTools.Worn(d, id, meta), Home() ?? default).Count > 0;
        if (stored) return false;
        foreach (var o in Pawns.Values)
        {
            if (o == p || !KindsFor(o).Contains(kind)) continue;
            var ol = Host.Live(o.Name);
            if (!ol.Online) continue;
            if (!GearOf(ol).Any(g => g.Slot >= 9 && StoryTools.ToolOf(d, g.Id) == kind && !StoryTools.Worn(d, g.Id, g.Meta))) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- drafting: arm on draft, restore on undraft

    /// <summary>
    /// Drafted: the best weapon carried goes in the hand (what was held is remembered) and, when the policy says so (when drafted,
    /// always, at night by night), the armour carried goes on. Undrafted: the old item back in the hand, the draft's armour off again
    /// (unless the policy says always), the post and any player permits gone.
    /// </summary>
    internal void OnDraftChanged(PawnState p, bool drafted)
    {
        if (World is not { } w) return;
        var d = w.Data;
        var live = Host.Live(p.Name);
        p.Gear.Tasks.RemoveAll(t => t.Op is "draft" or "restore");
        var pol = PolicyOf(p);
        var gear = GearOf(live);
        if (drafted)
        {
            var t = new GearTask { Id = NextId("g"), Op = "draft", Label = "arming for the draft" };
            var pref = pol.Weapon == "none" ? "best" : pol.Weapon;
            var weapon = gear.Where(g => g.Slot >= 9 && StoryTools.WeaponScore(d, g.Id, pref) > 0 && g.Id != 261).OrderByDescending(g => StoryTools.WeaponScore(d, g.Id, pref))
                .ThenBy(g => g.Meta).FirstOrDefault();
            var heldName = live.Held;
            p.Gear.RestoreHeld = heldName != null && (weapon == null || heldName != ItemName(weapon.Id, weapon.Meta)) ? heldName : null;
            if (weapon != null && heldName != ItemName(weapon.Id, weapon.Meta)) { t.Hold = ItemName(weapon.Id, weapon.Meta); t.HoldEnch = weapon.Enchanted ? "any" : "none"; }
            bool night = HourNow >= 13 && HourNow <= 22;
            if (pol.Armor is "drafted" or "always" || pol.Armor == "night" && night)
            {
                foreach (var slot in new[] { "head", "chest", "legs", "feet" })
                {
                    if (gear.Any(g => g.Slot is >= 5 and <= 8 && StoryTools.ArmorSlot(g.Id) == slot)) continue;
                    if (gear.Where(g => g.Slot >= 9 && StoryTools.ArmorSlot(g.Id) == slot).OrderByDescending(g => StoryTools.ArmorRank(g.Id)).FirstOrDefault() is { } a)
                        t.Wear.Add(ItemName(a.Id, a.Meta));
                }
                p.Gear.DraftArmor = t.Wear.Count > 0 && pol.Armor == "drafted";
            }
            if (t.Hold != null || t.Wear.Count > 0) p.Gear.Tasks.Insert(0, t);
        }
        else
        {
            var t = new GearTask { Id = NextId("g"), Op = "restore", Label = "putting the weapon away" };
            if (p.Gear.RestoreHeld is { } back && live.Inventory.Any(s => ItemName(s.Id, s.Meta) == back)) t.Hold = back;
            if (p.Gear.DraftArmor && pol.Armor != "always") t.Unwear = true;
            p.Gear.RestoreHeld = null;
            p.Gear.DraftArmor = false;
            p.Gear.Post = null;
            RevokePermits(p);
            if (t.Hold != null || t.Unwear) p.Gear.Tasks.Insert(0, t);
        }
        p.Rev = Touch();
    }

    // ---------------------------------------------------------------- state JSON

    private JsonObject GearItemJson(IGameData d, GearSlot g) => new()
    {
        ["item"] = new JsonArray(g.Id, g.Meta), ["name"] = ItemName(g.Id, g.Meta), ["label"] = d.DisplayName(g.Id, g.Meta), ["kind"] = StoryTools.GearKind(d, g.Id),
        ["tier"] = StoryTools.ItemTier(d, g.Id), ["durability"] = StoryTools.Durability(d, g.Id, g.Meta) is { } dur ? Math.Round(dur, 3) : null,
        ["ench"] = new JsonObject((g.Ench ?? new Dictionary<string, int>()).Select(e => new KeyValuePair<string, JsonNode?>(e.Key, e.Value))),
        ["slot"] = g.Slot is >= 5 and <= 8 ? StoryTools.ArmorSlot(g.Id) : null,
    };

    /// <summary>A pawn's equipment for the browser: held, armour, tools, weapons, with durability and enchantments.</summary>
    internal JsonObject EquipmentJson(PawnLive live)
    {
        if (Data is not { } d || !live.Online) return new JsonObject { ["held"] = null, ["armor"] = new JsonArray(), ["tools"] = new JsonArray(), ["weapons"] = new JsonArray() };
        var gear = GearOf(live);
        GearSlot? held = live.HeldSlot >= 36 ? gear.FirstOrDefault(g => g.Slot == live.HeldSlot) : null;
        if (held == null && live.Held is { } hn && d.ItemByName(hn) is { } hi)
            held = gear.FirstOrDefault(g => g.Id == hi.Id) ?? new GearSlot(-1, hi.Id, 0, 1);
        return new JsonObject
        {
            ["held"] = held == null ? null : GearItemJson(d, held),
            ["armor"] = new JsonArray(gear.Where(g => g.Slot is >= 5 and <= 8).Select(g => (JsonNode)GearItemJson(d, g)).ToArray()),
            ["tools"] = new JsonArray(gear.Where(g => g.Slot >= 9 && StoryTools.IsTool(d, g.Id)).Select(g => (JsonNode)GearItemJson(d, g)).ToArray()),
            ["weapons"] = new JsonArray(gear.Where(g => g.Slot >= 9 && StoryTools.IsWeapon(d, g.Id)).Select(g => (JsonNode)GearItemJson(d, g)).ToArray()),
        };
    }

    internal static JsonArray GearTasksJson(PawnState p) =>
        new(p.Gear.Tasks.Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["op"] = t.Op, ["label"] = t.Label }).ToArray());

    internal static JsonArray EnchantedJson(StorageState s) =>
        new(s.Enchanted.Select(e => (JsonNode)new JsonObject
        {
            ["item"] = new JsonArray(e.Id, e.Meta), ["ench"] = new JsonObject(e.Ench.Select(x => new KeyValuePair<string, JsonNode?>(x.Key, x.Value))),
        }).ToArray());

    /// <summary>The colony's gear lists for the state: policies, material rules (and, full only, the rules editor's catalog).</summary>
    internal void GearStateJson(JsonObject o, bool full)
    {
        BenchLoadAlert(); // many bills, one bench, a crafter idle: say so (StoryColony.Ledger.cs)
        o["policies"] = PoliciesJson();
        o["material_rules"] = RulesJson();
        if (full && World is { } w) o["materials"] = StoryTools.Catalog(w.Data);
    }
}
