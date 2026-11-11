using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MineClaw.Hive.Story;

/// <summary>The work types, in the fixed column order (docs/web/story-mode.md): a lower priority number first, ties by this order.</summary>
public static class WorkTypes
{
    public const string Guard = "guard", Doctor = "doctor", Cook = "cook", Hunt = "hunt", Fish = "fish", Build = "build", Farm = "farm",
        Mine = "mine", Chop = "chop", Craft = "craft", Haul = "haul";

    public static readonly IReadOnlyList<string> All = [Guard, Doctor, Cook, Hunt, Fish, Build, Farm, Mine, Chop, Craft, Haul];

    /// <summary>The work types before Fish (added 2026-11-03): a new pawn's skills are drawn in this order, so pawns stay who they were.</summary>
    public static readonly IReadOnlyList<string> Drawn = [Guard, Doctor, Cook, Hunt, Build, Farm, Mine, Chop, Craft, Haul];

    public static int Column(string type) => All.IndexOf(type) is var i and >= 0 ? i : All.Count;
    public static bool Known(string? t) => t != null && All.Contains(t);

    private static int IndexOf(this IReadOnlyList<string> l, string v) { for (int i = 0; i < l.Count; i++) if (l[i] == v) return i; return -1; }
}

/// <summary>One hour of a pawn's day.</summary>
public static class ScheduleBlocks
{
    public const string Work = "work", Sleep = "sleep", Anything = "anything", Free = "free";
    public static bool Known(string? b) => b is Work or Sleep or Anything or Free;
}

public static class StoryJson
{
    /// <summary>snake_case, nulls written (the route contract), enums as strings.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static JsonNode? Node<T>(T v) => JsonSerializer.SerializeToNode(v, Options);
    public static string Text<T>(T v) => JsonSerializer.Serialize(v, Options);
    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>A whole number from any JSON number node (parsed text or built in code; 3.7 → 3, floor), else null.</summary>
    public static int? Int(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return (int)Math.Floor(d);
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var ed)) return (int)Math.Floor(ed);
        return null;
    }
}

/// <summary>A block position (JSON: [x,y,z]).</summary>
[JsonConverter(typeof(P3Converter))]
public readonly record struct P3(int X, int Y, int Z)
{
    public override string ToString() => $"{X},{Y},{Z}";
    public double Dist(double x, double y, double z) => Math.Sqrt((X + 0.5 - x) * (X + 0.5 - x) + (Y - y) * (Y - y) + (Z + 0.5 - z) * (Z + 0.5 - z));
    public double Dist(P3 o) => Math.Sqrt((double)(X - o.X) * (X - o.X) + (double)(Y - o.Y) * (Y - o.Y) + (double)(Z - o.Z) * (Z - o.Z));
    public P3 Add(int dx, int dy, int dz) => new(X + dx, Y + dy, Z + dz);
    public JsonArray Json() => new(X, Y, Z);
    public JsonObject Obj() => new() { ["x"] = X, ["y"] = Y, ["z"] = Z };

    public static P3? Parse(JsonNode? n)
    {
        try
        {
            if (n is JsonArray a && a.Count >= 3) return new P3(I(a[0]), I(a[1]), I(a[2]));
            if (n is JsonObject o && o["x"] != null && o["y"] != null && o["z"] != null) return new P3(I(o["x"]), I(o["y"]), I(o["z"]));
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { }
        return null;
    }

    private static int I(JsonNode? n) => StoryJson.Int(n) ?? throw new FormatException("not a number");
}

public sealed class P3Converter : JsonConverter<P3>
{
    public override P3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        P3.Parse(JsonNode.Parse(ref reader)) ?? throw new JsonException("position must be [x,y,z]");
    public override void Write(Utf8JsonWriter writer, P3 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }
}

/// <summary>An axis-aligned box of blocks (JSON: [x1,y1,z1,x2,y2,z2], any corner order).</summary>
[JsonConverter(typeof(AreaConverter))]
public readonly record struct Area(int X1, int Y1, int Z1, int X2, int Y2, int Z2)
{
    public static Area Of(int x1, int y1, int z1, int x2, int y2, int z2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Min(z1, z2), Math.Max(x1, x2), Math.Max(y1, y2), Math.Max(z1, z2));

    public bool Contains(int x, int y, int z) => x >= X1 && x <= X2 && y >= Y1 && y <= Y2 && z >= Z1 && z <= Z2;
    public bool Contains(P3 p) => Contains(p.X, p.Y, p.Z);
    /// <summary>Column-wise (any height): a zone painted on the top-down map.</summary>
    public bool ContainsXZ(double x, double z) => x >= X1 && x < X2 + 1 && z >= Z1 && z < Z2 + 1;
    public bool Overlaps(Area o) => X1 <= o.X2 && X2 >= o.X1 && Y1 <= o.Y2 && Y2 >= o.Y1 && Z1 <= o.Z2 && Z2 >= o.Z1;
    public long Volume => (long)(X2 - X1 + 1) * (Y2 - Y1 + 1) * (Z2 - Z1 + 1);
    public P3 Center => new((X1 + X2) / 2, Y1, (Z1 + Z2) / 2);

    public IEnumerable<P3> Blocks()
    {
        for (int y = Y1; y <= Y2; y++)
            for (int x = X1; x <= X2; x++)
                for (int z = Z1; z <= Z2; z++)
                    yield return new P3(x, y, z);
    }

    public static Area? Parse(JsonNode? n)
    {
        if (n is not JsonArray a || a.Count != 6) return null;
        try
        {
            var v = a.Select(x => StoryJson.Int(x) ?? throw new FormatException("not a number")).ToArray();
            return Of(v[0], v[1], v[2], v[3], v[4], v[5]);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or NullReferenceException) { return null; }
    }

    public JsonArray Json() => new(X1, Y1, Z1, X2, Y2, Z2);
}

public sealed class AreaConverter : JsonConverter<Area>
{
    public override Area Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Area.Parse(JsonNode.Parse(ref reader)) ?? throw new JsonException("area must be [x1,y1,z1,x2,y2,z2]");
    public override void Write(Utf8JsonWriter writer, Area v, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var n in new[] { v.X1, v.Y1, v.Z1, v.X2, v.Y2, v.Z2 }) writer.WriteNumberValue(n);
        writer.WriteEndArray();
    }
}

/// <summary>An item kind: id and meta (−1 = any meta). JSON [id, meta].</summary>
public readonly record struct ItemKey(int Id, int Meta)
{
    public bool Matches(int id, int meta) => id == Id && (Meta < 0 || meta == Meta);
    public JsonArray Json() => new(Id, Meta);
}

/// <summary>One stack (JSON [id, meta, count]).</summary>
public sealed record Stack(int Id, int Meta, int Count)
{
    public JsonArray Json() => new(Id, Meta, Count);
}

// ================================================================ the persisted state

public sealed class SkillState
{
    public int Level { get; set; }
    public double Xp { get; set; }
    public bool Passion { get; set; }
}

public sealed record Trait(string Id, string Label, string Effect);

/// <summary>An order given to a pawn (or a group): move|attack|chop|mine|haul|build|hold|stop.</summary>
public sealed class Order
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public P3? At { get; set; }
    public int? Entity { get; set; }
    public Area? Area { get; set; }
    public long? Design { get; set; }
    public DateTimeOffset Given { get; set; }
    /// <summary>Per-block work an area order expanded to (mine/chop), done ones removed.</summary>
    public List<P3>? Targets { get; set; }
}

public sealed class PawnState
{
    public string Name { get; set; } = "";
    public string Display { get; set; } = "";
    public Dictionary<string, SkillState> Skills { get; set; } = new();
    public List<string> Traits { get; set; } = new();
    /// <summary>work type → 0 (off) .. 4: the effective values (its role's while it has one: StoryColony.Roles.cs).</summary>
    public Dictionary<string, int> Priorities { get; set; } = new();
    /// <summary>Its work role's id (<see cref="ColonyMeta.Roles"/>); null = Custom (its own priorities).</summary>
    public string? Role { get; set; }
    public string[] Schedule { get; set; } = new string[24];
    public bool Drafted { get; set; }
    public List<Order> Queue { get; set; } = new();
    /// <summary>Its gear policy, what it held before a draft, its post, pending gear work (StoryColony.Gear.cs).</summary>
    public PawnGear Gear { get; set; } = new();
    /// <summary>Lava collected by quarry work is cargo, including across an interrupted job or restart.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool QuarryLava { get; set; }
    public long Rev { get; set; }
}

public sealed class ZoneState
{
    public string Id { get; set; } = "";
    /// <summary>chop | mine | farm | fish | home | forbidden | dump | preserve | guard (storage is chests).</summary>
    public string Kind { get; set; } = "";
    /// <summary>The cells' bounding box (x, z) and the zone's level range (y: what it was painted at; the union when zones merge).</summary>
    public Area Area { get => _area; set { _area = value; if (_rows == null) _cells = null; } }
    /// <summary>
    /// The zone's columns as normalised row runs <c>[z, x1, x2]</c> (docs/web/story-mode.md "Zones"); null = a legacy rectangle (the
    /// whole <see cref="Area"/>), migrated to rows when the store loads it.
    /// </summary>
    public List<int[]>? Rows { get => _rows; set { _rows = value; _cells = null; } }
    /// <summary>A name the browser gives it ("Main house", "Farmhouse"); null = none.</summary>
    public string? Name { get; set; }
    public long Rev { get; set; }
    /// <summary>
    /// A growing zone's plant: the first planting bill's plant (a <see cref="StoryPlants"/> id, farm zones only; what painting calls the
    /// zone's type). Null with no bills = a plain farm zone (ripe crops, bare farmland). Older saves only have this (migrated to a bill).
    /// </summary>
    public string? Plant { get; set; }
    /// <summary>A growing zone's planting bills, in order (null = not a growing zone). The first unpaused unmet one decides what is sown.</summary>
    public List<GrowBill>? Bills { get; set; }
    /// <summary>A growing zone: pawns till and sow it.</summary>
    public bool Sow { get; set; } = true;
    /// <summary>A growing zone: pawns harvest what is ripe.</summary>
    public bool Harvest { get; set; } = true;
    /// <summary>A fishing zone: the fish caught there so far.</summary>
    public int Catches { get; set; }
    /// <summary>A mine zone: <c>quarry</c> (dig everything to bedrock, a perimeter stair; StoryColony.Quarry.cs) or <c>ores</c> (only its
    /// ores); null = ores (zones from before quarries).</summary>
    public string? Mode { get; set; }
    /// <summary>A quarry: the y its digging starts at (its highest ground when first seen; the stair's first step).</summary>
    public int? QuarryTop { get; set; }
    /// <summary>A quarry: the layer it has reached (0 = the top), a hint its scan starts from.</summary>
    public int QuarryLayer { get; set; }
    /// <summary>A quarry: the cells its finished <c>place</c> jobs put a block in that is no stair step (seals, plugs), and on an upgrade
    /// the pawns' blocks already in or beside it. Its own: never swept as strays (StoryColony.QuarrySweep.cs). Null = none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<P3>? QuarryPlaced { get; set; }
    /// <summary>A quarry: its lowest bottom (one over the bedrock) when every column of it was last in view; stands in for a column out
    /// of view, which reads as air (no bedrock). Null = not seen whole yet.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? QuarryBottom { get; set; }
    /// <summary>Preserve: the zone keeps its blocks as its snapshot has them (StoryColony.Preserve.cs); null = off. Always on for a zone of
    /// kind <c>preserve</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PreserveState? Preserve { get; set; }

    private Area _area;
    private List<int[]>? _rows;
    private ZoneCells? _cells;

    /// <summary>The columns as a hash set (built once from <see cref="Rows"/>, or the legacy rectangle).</summary>
    [JsonIgnore]
    public ZoneCells Cells => _cells ??= _rows != null ? ZoneCells.FromRows(_rows) : ZoneCells.Rect(_area);

    /// <summary>Is the column (x, z) in the zone?</summary>
    public bool Contains(int x, int z) => Cells.Contains(x, z);

    /// <summary>Is the block in the zone: its column, within the level range (the keep-out test, as before cell sets).</summary>
    public bool Contains(P3 p) => p.Y >= _area.Y1 && p.Y <= _area.Y2 && Cells.Contains(p.X, p.Z);

    /// <summary>New cells (and level range): rows normalised, the area made their bounding box.</summary>
    public void SetCells(ZoneCells cells, int y1, int y2)
    {
        var (x1, z1, x2, z2) = cells.Bounds();
        _area = Area.Of(x1, y1, z1, x2, y2, z2);
        _rows = cells.Rows();
        _cells = cells;
    }

    /// <summary>A new zone with these settings (kind, name, growing settings and bills, fresh bill ids kept) and no cells.</summary>
    public ZoneState CopySettings(string id) => new()
    {
        Id = id, Kind = Kind, Name = Name, Plant = Plant, Sow = Sow, Harvest = Harvest, Mode = Mode, QuarryTop = QuarryTop, QuarryLayer = QuarryLayer,
        Preserve = Preserve?.Copy(),
        QuarryPlaced = QuarryPlaced?.ToList(), QuarryBottom = QuarryBottom,
        Bills = Bills?.Select(b => new GrowBill { Id = b.Id, Plant = b.Plant, Mode = b.Mode, Target = b.Target, Done = b.Done, Paused = b.Paused }).ToList(),
    };
}

/// <summary>
/// A growing zone's planting bill (docs/web/story-mode.md "Farming"): what to sow and for how long. forever; until (Target of the
/// produce in storage); count (harvest Target, <see cref="Done"/> so far). The zone sows the first unpaused bill whose target isn't met.
/// </summary>
public sealed class GrowBill
{
    public string Id { get; set; } = "";
    public string Plant { get; set; } = "";
    /// <summary>forever | until | count.</summary>
    public string Mode { get; set; } = "forever";
    public int Target { get; set; } = 64;
    public int Done { get; set; }
    public bool Paused { get; set; }
}

/// <summary>One designated target: a block (tree base, crop, ore…) or an entity (a drop, an animal).</summary>
public sealed class DesignationState
{
    public string Id { get; set; } = "";
    /// <summary>chop | cut | harvest | mine | haul | hunt | deconstruct | forbid.</summary>
    public string Kind { get; set; } = "";
    public P3? At { get; set; }
    public int? Entity { get; set; }
    /// <summary>A tree's log column(s); a crop's replant item.</summary>
    public List<P3>? Blocks { get; set; }
    public string? Replant { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset Created { get; set; }
    public int Fails { get; set; }
    /// <summary>open | failed (it failed <see cref="StoryColony.DesignationFailLimit"/> times: kept on the map with its <see cref="Reason"/>,
    /// retried after a rest; cancel it to drop it).</summary>
    public string Status { get; set; } = "open";
    /// <summary>Why it last failed (null while it hasn't).</summary>
    public string? Reason { get; set; }
    /// <summary>Placed by the colony, not the owner (seek_seeds' grass cuts): its cells are the colony's choice, so a plant in a cave is
    /// left alone (surface work). An owner's mark is work wherever it is.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Auto { get; set; }
    /// <summary>Ore exposed by authorized mining; keeps its original work policy and parent cancellation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public OreWorkOrigin? OreOrigin { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Ore { get; set; }
    /// <summary>Why it waits without failing ("needs a stone pickaxe"): no pawn that picked it had or could fetch the tool; not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? Wait { get; set; }
    public long Rev { get; set; }
}

public sealed class StorageFilter
{
    public List<string> Allow { get; set; } = ["*"];
    public List<string> Deny { get; set; } = new();
}

public static class StoragePriority
{
    public static readonly IReadOnlyList<string> All = ["low", "normal", "preferred", "important", "critical"];
    /// <summary>The priority's place in <see cref="All"/> (unknown: normal's). Asked per chest per stack by the haul search: no list
    /// copy per call (it made one, live 11-10).</summary>
    public static int Rank(string? p)
    {
        if (p == null) return 1;
        for (int i = 0; i < All.Count; i++)
            if (string.Equals(All[i], p, StringComparison.Ordinal)) return i;
        return 1;
    }
    public static bool Known(string? p) => p != null && All.Contains(p);
}

public sealed class StorageState
{
    public bool Missing { get; set; }
    public P3 Chest { get => _chest; set { if (_chest != value) { _chest = value; CellsMoved(); } } }
    public P3? DoubleWith { get => _doubleWith; set { if (_doubleWith != value) { _doubleWith = value; CellsMoved(); } } }
    private P3 _chest;
    private P3? _doubleWith;
    private static long _cellsVersion;

    /// <summary>Bumped whenever any storage's chest or double half moves: StoryColony.StorageAt's cell map is made again.</summary>
    internal static long CellsVersion => Interlocked.Read(ref _cellsVersion);

    private static void CellsMoved() => Interlocked.Increment(ref _cellsVersion);
    public string? Label { get; set; }
    public string Priority { get; set; } = "normal";
    public StorageFilter Filter { get; set; } = new();
    public List<StorageSignState> Signs { get; set; } = new();
    public StorageFilter? PendingFilter { get; set; }
    public string? PendingPriority { get; set; }
    public bool PendingUnregister { get; set; }
    public List<StorageSignRemoval> SignRemovals { get; set; } = new();
    [JsonIgnore] public StorageFilter EffectiveFilter => Signs.Count == 0 ? Filter : new StorageFilter
    {
        Allow = Signs.SelectMany(s => s.Allow).Distinct().ToList(),
        Deny = Signs.SelectMany(s => s.Deny).Distinct().ToList(),
    };
    public List<Stack> Contents { get; set; } = new();
    public int FreeSlots { get; set; } = 27;
    public DateTimeOffset? KnownAt { get; set; }
    /// <summary>When a body last looked inside (the bot's memory of the chest); null = nobody has (StoryColony.StockCheck.cs).</summary>
    public DateTimeOffset? SeenAt { get; set; }
    /// <summary>Enchanted gear known to be in this chest (put there by the colony's pawns): the contents list can't tell them apart.</summary>
    public List<EnchStack> Enchanted { get; set; } = new();
    public long Rev { get; set; }
    public int Slots => DoubleWith != null ? 54 : 27;
}

public sealed record StorageSignState(P3 At, IReadOnlyList<string> Lines, List<string> Allow, List<string> Deny);
public sealed record StorageSignRemoval(P3 At, P3 Chest, IReadOnlyList<string> Lines, string Designation);

/// <summary>One build queue item (Architect): its expansion is cached as <see cref="Ghosts"/>.</summary>
public sealed class BuildState
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>queued | active | paused | blocked | done.</summary>
    public string Status { get; set; } = "queued";
    public string? Reason { get; set; }
    public string Material { get; set; } = "";
    public int Order { get; set; }
    public DateTimeOffset Created { get; set; }
    /// <summary>The item as it was asked (for the record; ghosts are the truth).</summary>
    public JsonObject? Spec { get; set; }
    public List<GhostState> Ghosts { get; set; } = new();
    public long Rev { get; set; }
    public int BlocksTotal => Ghosts.Count(g => g.Job);
    public int BlocksDone => Ghosts.Count(g => g.Job && g.State == "done");
    /// <summary>Items whose auto-crafting the owner cancelled (an auto bill's cancel): the build waits for them instead.</summary>
    public List<string> NoCraft { get; set; } = new();
    /// <summary>What the build still needs, per item (the materials pass: have, crafting, short, via); not saved.</summary>
    [JsonIgnore] public List<BuildMaterial> Materials { get; set; } = new();
}

/// <summary>One item a build still needs: how many, how many the colony has for it (reserved in queue order), how many are being
/// crafted for it (auto bills), how many are short (raw materials missing), the recipe chain and the raw shortfall.</summary>
public sealed record BuildMaterial(string Item, string Label, int Need, int Have, int Crafting, int Short, string Via, IReadOnlyList<string> Chain,
    IReadOnlyList<string> Raw)
{
    public JsonObject Json() => new()
    {
        ["item"] = Item, ["label"] = Label, ["need"] = Need, ["have"] = Have, ["crafting"] = Crafting, ["short"] = Short, ["via"] = Via,
        ["chain"] = new JsonArray(Chain.Select(c => (JsonNode)c!).ToArray()), ["raw"] = new JsonArray(Raw.Select(c => (JsonNode)c!).ToArray()),
    };

    public string Key => $"{Item}|{Need}|{Have}|{Crafting}|{Short}|{Via}|{string.Join(";", Chain)}|{string.Join(";", Raw)}";
}

/// <summary>One block of a build: what goes where, and its state (todo | leased | done | blocked).</summary>
public sealed class GhostState
{
    public P3 At { get; set; }
    public int Id { get; set; }
    public int Meta { get; set; }
    /// <summary>The item a pawn places (a door's item, not its block); null = dig (remove).</summary>
    public string? Item { get; set; }
    public string? Facing { get; set; }
    /// <summary>False = placed with another ghost (a door's top half): drawn, never a job of its own.</summary>
    public bool Job { get; set; } = true;
    public bool Remove { get; set; }
    /// <summary>todo | leased | blocked | done | unsupported (a block pawns can't place yet: shown and alerted, never a job) | misrotated
    /// (placed, but turned another way than the design: shown over the block with the reason, finished for the build).</summary>
    public string State { get; set; } = "todo";
    /// <summary>Why an unsupported ghost can't be placed ("crops are planted, not placed"), or how a misrotated one stands.</summary>
    public string? Reason { get; set; }
    public long Rev { get; set; }
    /// <summary>What an open cell holds now when it isn't the design's block (the reconcile pass, StoryColony.Reconcile.cs): <c>clear</c>
    /// (another block: dug out first) or <c>misrotated</c> (its block turned another way: taken back and placed again), with the words
    /// the map shows. Not saved: the next pass finds it again.</summary>
    [JsonIgnore] public string? Holds { get; set; }
    [JsonIgnore] public string? HoldsWords { get; set; }
}

public sealed class BillState
{
    public string Id { get; set; } = "";
    public string Recipe { get; set; } = "";
    /// <summary>count | until | forever.</summary>
    public string Mode { get; set; } = "count";
    public int Target { get; set; } = 1;
    public int Done { get; set; }
    public bool Paused { get; set; }
    /// <summary>Across all workbenches: 1 highest, 2 high, 3 normal, 4 low. Equal priorities keep the bench's bill order.</summary>
    public int Priority { get; set; } = 3;
    public string? SuspendedReason { get; set; }
    /// <summary>A forever bill's expected ingredient/fuel shortage; retried when stock changes, without an error alert.</summary>
    public string? WaitingReason { get; set; }
    public StorageFilter Ingredients { get; set; } = new();
    public int Radius { get; set; } = 32;
    public string Pawn { get; set; } = "any";
    public int? MinSkill { get; set; }
    /// <summary>"best" or a chest [x,y,z].</summary>
    public JsonNode? Store { get; set; } = JsonValue.Create("best");
    /// <summary>An auto bill (docs/web/story-mode.md "Builds craft what they need"): the build it crafts for; null = the owner's bill.
    /// It follows the build's shortfall (target = done + what is still to make) and goes when the build no longer needs it; the owner
    /// can only cancel it (the build then waits for the item).</summary>
    public string? ForBuild { get; set; }
    /// <summary>The build's item an auto bill's chain ends in (cancelling the bill makes the build wait for that item).</summary>
    public string? ForItem { get; set; }
    /// <summary>An auto bill's place in its build's recipe chain (lower first: ingredients before products).</summary>
    public int Step { get; set; }
    /// <summary>A craft task's reason in words ("for mining: Cleo Z. (+1 spare)", "for the stone bricks of build b3").</summary>
    public string? Reason { get; set; }
    /// <summary>An enchant bill's highest offer to take (1–3: its lapis and the levels it costs); null = 3.</summary>
    public int? Option { get; set; }
    /// <summary>An enchant bill's preferred enchantment (fortune, silk_touch...): the offer hinting it first.</summary>
    public string? Prefer { get; set; }
    /// <summary>Only an offer hinting <see cref="Prefer"/> (else the highest one the pawn can pay).</summary>
    public bool PreferOnly { get; set; }
    /// <summary>The XP levels the last try needed (an offer's level, the anvil's price): pawns with fewer don't take it.</summary>
    public int? XpNeeded { get; set; }
    /// <summary>What the last job made ("enchanted diamond pickaxe: Fortune III", "brewed 3 Potion of Swiftness").</summary>
    public string? Last { get; set; }
    /// <summary>A furnace bill's fuel override (StoryFuels.Choices); null = the bench's setting.</summary>
    public string? Fuel { get; set; }
}

public sealed class WorkbenchState
{
    public P3 Pos { get; set; }
    /// <summary>Last confirmed missing in a loaded world view; stays missing across restarts until its block is seen again.</summary>
    public bool Missing { get; set; }
    /// <summary>crafting_table | furnace | brewing_stand | anvil | enchanting_table.</summary>
    public string Kind { get; set; } = "";
    public string? Label { get; set; }
    /// <summary>The owner's bills, in the owner's order (docs/web/story-mode.md "Bills").</summary>
    public List<BillState> Bills { get; set; } = new();
    /// <summary>The colony's own craft tasks here (<see cref="BillState.ForBuild"/> set: a build, tools, a need), never shown as bills
    /// (StoryColony.CraftTasks.cs).</summary>
    public List<BillState> Tasks { get; set; } = new();
    /// <summary>A furnace loaded and left to smelt: the bill and when it is ready to collect.</summary>
    public string? LoadedBill { get; set; }
    public int LoadedCount { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    /// <summary>The recipe a furnace was loaded with (its bill may be gone by the time it is collected: it still is).</summary>
    public string? LoadedRecipe { get; set; }
    /// <summary>When the furnace was loaded (its expected progress: an item every 10 s).</summary>
    public DateTimeOffset? LoadedAt { get; set; }
    /// <summary>A brewing stand's fuel (brews left) as the colony last left it; null = unknown.</summary>
    public int? Fuel { get; set; }
    /// <summary>A furnace's fuel choice (StoryFuels.Choices); null = auto.</summary>
    public string? FuelSetting { get; set; }
    /// <summary>Items a furnace can still smelt on the fuel the colony left in its fuel slot (whole fuel items, not yet lit).</summary>
    public double FuelCredit { get; set; }
    /// <summary>When the fuel a furnace lit burns out (it burns on with nothing to smelt): until then what is left of it counts too
    /// (<c>StoryColony.FuelCreditNow</c>); null = nothing burning that the colony knows of.</summary>
    public DateTimeOffset? BurnUntil { get; set; }
    /// <summary>A loaded furnace found with its input and no fuel (a collect that found nothing to collect): that many items wait for a
    /// refuel job (0 = none).</summary>
    public int RefuelCount { get; set; }
    /// <summary>The waiting input as the furnace showed it ("oak wood"), for the refuel's label.</summary>
    public string? RefuelItem { get; set; }
    public long Rev { get; set; }
}

public sealed record AlertState(DateTimeOffset At, string Level, string Text, string? Pawn, long Rev);

/// <summary>A colony's saved settings and identity (the story_colonies row).</summary>
public sealed class ColonyMeta
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public bool Paused { get; set; }
    /// <summary>Spawned and not released (it resumes after a restart).</summary>
    public bool Active { get; set; } = true;
    public P3? Home { get; set; }
    public int Wanted { get; set; }
    /// <summary>column (genre default: ties by column order) | skill (ties by the pawn's skill first).</summary>
    public string TieBreak { get; set; } = "column";
    public int Seq { get; set; }
    /// <summary>Bed assignments: a bed's foot ("x,y,z") → the pawn that sleeps there (one bed per pawn, one pawn per bed).</summary>
    public Dictionary<string, string> Beds { get; set; } = new();
    /// <summary>A build that needs a crafting table (or furnace) the colony hasn't got: a pawn crafts one and places it near home
    /// (default on). Off: an alert asks the owner to place or designate one.</summary>
    public bool AutoPlaceBenches { get; set; } = true;
    /// <summary>Workbenches near home and in houses join the colony by themselves. Off by default: only designated benches (the owner's
    /// "Use as colony crafting table", a bill added at a bench, a bench the colony placed) are used, never another player's.</summary>
    public bool DetectBenches { get; set; }
    /// <summary>A pawn stuck where nothing gets it out (no stairs to dig, no blocks to pillar, nobody to bring some) is teleported home
    /// through the op command path (default on; never on b1.8.1, whose /tp takes no coordinates).</summary>
    public bool RescueTeleport { get; set; } = true;
    /// <summary>Loot no chest's filter accepts goes into any chest with room that doesn't deny it (default on). Off: it stays with the
    /// pawn (or on the ground) until a chest accepts it.</summary>
    public bool UnfilteredTakesAnything { get; set; } = true;
    /// <summary>Legacy saved setting. Chest organization now always retains items until a safe destination exists.</summary>
    public bool KeepDisallowed { get; set; }
    /// <summary>Blocks a pawn keeps to nerd-pole with (dirt first, then cobblestone, netherrack) out of its store trips: a kind it
    /// carries no more of than this stays with it (default 16; 0 = none). Fox 11-04: high blocks are reached from a pillar.</summary>
    public int PillarStack { get; set; } = 16;
    /// <summary>Seconds a stuck pawn tries to get out by itself (or 2 failed escape steps) before the colony teleports it home
    /// (default 20, at least 5). Fox 11-04: "When pawns are stuck, they should teleport sooner."</summary>
    public int StuckTeleportAfterS { get; set; } = 20;
    /// <summary>Gear policies, material tool rules, tool auto-crafting and spare tools (StoryColony.Gear.cs).</summary>
    public GearSettings Gear { get; set; } = new();
    /// <summary>What pawns do with nothing to do, in free hours and while resting: recreation (default: varied pastimes), wander (strolls
    /// only) or stay (hold where they are).</summary>
    public string Idle { get; set; } = "recreation";
    /// <summary>Movement speed of the colony's pawns: normal | fast (×2) | faster (×3) (StoryColony.Speed.cs).</summary>
    public string Speed { get; set; } = "normal";
    /// <summary>On 1.12.2 with an op account, Fast / Faster also ask the server for a Speed effect on the pawns (default on).</summary>
    public bool SpeedEffect { get; set; } = true;
    /// <summary>Pawns may pour lava from buckets (Architect "Lava source"; docs/web/story-mode.md "Water"). Off by default; never in a
    /// home zone or beside something that burns.</summary>
    public bool AllowLava { get; set; }
    /// <summary>The 11-04 canal fix reopened this colony's blocked water cells once on load (StoryColony.BuildRetry.cs); false for older saves.</summary>
    public bool WaterRetried { get; set; }
    /// <summary>The 11-04 "nothing beside or under" fix moved pour cells above their own dug cells down into them, once (StoryColony.BuildRetry.cs).</summary>
    public bool WaterCellsFixed { get; set; }
    /// <summary>Work roles: named priority templates (StoryColony.Roles.cs). Null = an older save: the built-in roles are seeded.</summary>
    public List<WorkRole>? Roles { get; set; }
    /// <summary>Blocks the pawns put down for themselves and haven't taken back yet (StoryColony.TempBlocks.cs); null = none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TempBlockState>? TempBlocks { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<OreFrontier>? OreFrontiers { get; set; }
    /// <summary>Cancelled automatic ore extensions stay cancelled across neighboring digs and restarts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<P3>? CancelledOre { get; set; }
    /// <summary>The pawns' blocks already in or beside quarries were taken as theirs once (StoryColony.QuarrySweep.cs); false for older saves.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool QuarrySealsAdopted { get; set; }
}

public sealed record OreWorkOrigin(string Source, string Id, string Type);
public sealed record OreFrontier(P3 At, int Ore, OreWorkOrigin Origin);

/// <summary>
/// A block a pawn put down for itself (a reach or escape pillar step, a build's scaffold, a walk's pillar or bridge, a shelter box, a
/// fall clutch): never a directive, so it never stays. Fox 11-05: "Bots should delete whatever they put down unless it was a directive
/// to build it." Kept until the world shows the cell without it; one its pawn left behind becomes a deconstruct designation.
/// </summary>
public sealed class TempBlockState
{
    public P3 At { get; set; }
    /// <summary>The block placed there (0 = not seen in the view yet).</summary>
    public int Id { get; set; }
    public string Pawn { get; set; } = "";
    /// <summary>reach pillar | escape pillar | build scaffold | scaffold (a walk's, a shelter's, a clutch) | stray (found on load).</summary>
    public string Kind { get; set; } = "scaffold";
    public DateTimeOffset Since { get; set; }
    /// <summary>The cleanup designation queued for it (null = its pawn may still take it back).</summary>
    public string? Designation { get; set; }
    /// <summary>Cleanups that couldn't reach it: each rests it longer (<see cref="StoryColony.TempUnreachBackoff"/>); it stays marked.</summary>
    public int Misses { get; set; }
    /// <summary>The owner cancelled cleanup: keep the block and do not rediscover it on reload.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Cancelled { get; set; }
    /// <summary>A cleanup left to the owner (refused as protected): retained so the stray scan cannot recreate it on restart, and never
    /// reopened. Saves from before 11-11 also hold "out of reach" here; such an entry is reopened on load (a block out of reach is
    /// retried now).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeferredReason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RetryAfter { get; set; }
    /// <summary>Its block was taken from the colony's view, not from its pawn's report (the placement wasn't seen yet when it was
    /// registered): it may be somebody else's, so a cleaner's body is never taught it as the hive's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Adopted { get; set; }
}
