using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MineClaw.Hive.Story;

/// <summary>The work types, in the fixed column order (docs/web/story-mode.md): a lower priority number first, ties by this order.</summary>
public static class WorkTypes
{
    public const string Guard = "guard", Doctor = "doctor", Cook = "cook", Hunt = "hunt", Build = "build", Farm = "farm", Mine = "mine",
        Chop = "chop", Craft = "craft", Haul = "haul";

    public static readonly IReadOnlyList<string> All = [Guard, Doctor, Cook, Hunt, Build, Farm, Mine, Chop, Craft, Haul];

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
    /// <summary>work type → 0 (off) .. 4.</summary>
    public Dictionary<string, int> Priorities { get; set; } = new();
    public string[] Schedule { get; set; } = new string[24];
    public bool Drafted { get; set; }
    public List<Order> Queue { get; set; } = new();
    public long Rev { get; set; }
}

public sealed class ZoneState
{
    public string Id { get; set; } = "";
    /// <summary>chop | mine | farm | home | forbidden (storage is chests: see <see cref="StorageState"/>).</summary>
    public string Kind { get; set; } = "";
    public Area Area { get; set; }
    /// <summary>A name the browser gives it ("Main house", "Farmhouse"); null = none.</summary>
    public string? Name { get; set; }
    public long Rev { get; set; }
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
    public static int Rank(string? p) => p == null ? 1 : All.ToList().IndexOf(p) is var i and >= 0 ? i : 1;
    public static bool Known(string? p) => p != null && All.Contains(p);
}

public sealed class StorageState
{
    public P3 Chest { get; set; }
    public P3? DoubleWith { get; set; }
    public string? Label { get; set; }
    public string Priority { get; set; } = "normal";
    public StorageFilter Filter { get; set; } = new();
    public List<Stack> Contents { get; set; } = new();
    public int FreeSlots { get; set; } = 27;
    public DateTimeOffset? KnownAt { get; set; }
    public long Rev { get; set; }
    public int Slots => DoubleWith != null ? 54 : 27;
}

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
    public string State { get; set; } = "todo";
    public long Rev { get; set; }
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
    public string? SuspendedReason { get; set; }
    public StorageFilter Ingredients { get; set; } = new();
    public int Radius { get; set; } = 32;
    public string Pawn { get; set; } = "any";
    public int? MinSkill { get; set; }
    /// <summary>"best" or a chest [x,y,z].</summary>
    public JsonNode? Store { get; set; } = JsonValue.Create("best");
}

public sealed class WorkbenchState
{
    public P3 Pos { get; set; }
    /// <summary>crafting_table | furnace | brewing_stand | anvil | enchanting_table.</summary>
    public string Kind { get; set; } = "";
    public string? Label { get; set; }
    public List<BillState> Bills { get; set; } = new();
    /// <summary>A furnace loaded and left to smelt: the bill and when it is ready to collect.</summary>
    public string? LoadedBill { get; set; }
    public int LoadedCount { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
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
}
