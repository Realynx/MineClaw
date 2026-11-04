using System.Globalization;
using MineClaw.Agent.Memory.Grooming;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

/// <summary>Broad action categories (accuracy over coverage).</summary>
public static class ActivityCategory
{
    public const string Mined = "mined", Built = "built", Container = "container", Crafted = "crafted", Farmed = "farmed",
        Fought = "fought", Travelled = "travelled", Died = "died", Items = "items", Social = "social", Status = "status";
}

/// <summary>Raw activity kinds (one templated sentence each).</summary>
public static class ActivityKinds
{
    public const string Broke = "broke", Placed = "placed", Opened = "opened", LitFurnace = "lit_furnace", PickedUp = "picked_up",
        Dropped = "dropped", Equipped = "equipped", Hurt = "hurt", Fought = "fought", Died = "died", Slept = "slept", Rode = "rode",
        WroteSign = "wrote_sign", Joined = "joined", Left = "left", Chat = "chat", Arrived = "arrived", Departed = "departed",
        Crafted = "crafted", Took = "took", Stored = "stored", Harvested = "harvested", Planted = "planted";

    /// <summary>The category of each kind (a broken or placed crop is farming; anything unlisted is status).</summary>
    private static readonly Dictionary<string, string> Categories = new(StringComparer.Ordinal)
    {
        [Harvested] = ActivityCategory.Farmed, [Planted] = ActivityCategory.Farmed,
        [Broke] = ActivityCategory.Mined,
        [Placed] = ActivityCategory.Built, [WroteSign] = ActivityCategory.Built,
        [Opened] = ActivityCategory.Container, [Took] = ActivityCategory.Container, [Stored] = ActivityCategory.Container,
        [LitFurnace] = ActivityCategory.Crafted, [Crafted] = ActivityCategory.Crafted,
        [PickedUp] = ActivityCategory.Items, [Dropped] = ActivityCategory.Items, [Equipped] = ActivityCategory.Items,
        [Hurt] = ActivityCategory.Fought, [Fought] = ActivityCategory.Fought,
        [Died] = ActivityCategory.Died,
        [Arrived] = ActivityCategory.Travelled, [Departed] = ActivityCategory.Travelled,
        [Joined] = ActivityCategory.Social, [Left] = ActivityCategory.Social, [Chat] = ActivityCategory.Social,
    };

    public static string CategoryOf(string kind, int? blockId)
    {
        if (kind is Broke or Placed && blockId is { } b && IsCrop(b)) return ActivityCategory.Farmed;
        return kind is not null && Categories.TryGetValue(kind, out var category) ? category : ActivityCategory.Status;
    }

    public static bool IsCrop(int id) => id is Blocks.Crops or Blocks.SugarCane or Blocks.Cactus or Blocks.Melon or Blocks.Pumpkin
        or Blocks.Sapling or Blocks.MelonStem or Blocks.PumpkinStem or Blocks.BrownMushroom or Blocks.RedMushroom;
}

/// <summary>One deterministic observation of another player doing something.</summary>
public sealed record ActivityEvent(string Player, string Kind, DateTimeOffset At, double Confidence)
{
    public int? BlockId { get; init; }
    public int? ItemId { get; init; }
    public int Count { get; init; } = 1;
    public BlockPos? Pos { get; init; }
    public PlaceRef? Place { get; init; }
    /// <summary>Free detail: sign text, chat line (truncated), mob name, death cause.</summary>
    public string? Detail { get; init; }
    public string Category => ActivityKinds.CategoryOf(Kind, BlockId);
    /// <summary>Row id once stored.</summary>
    public long Id { get; init; }
    /// <summary>Dimension it happened in. b1.8 only shows players in the bot's own dimension, so this is the bot's.</summary>
    public int Dimension { get; init; }
    /// <summary>Has a place (block position or indexed place); placeless events (chat, joins, chat death lines) are global.</summary>
    public bool IsSpatial => Pos != null || Place != null;

    public string Text => ActivityText.Describe(this);
}

/// <summary>Activity settings ("Bot:Activity"). Confidence bands drive the digest: high = stated, medium = "probably", low = omitted.</summary>
public sealed class ActivityOptions
{
    public bool Enabled { get; set; } = true;
    public int PassIntervalMs { get; set; } = 250;
    public double HighConfidence { get; set; } = 0.7;
    public double MediumConfidence { get; set; } = 0.45;
    /// <summary>Events below this aren't even logged.</summary>
    public double MinLogConfidence { get; set; } = 0.25;
    public double Reach { get; set; } = 5.5;
    public double SwingWindowSeconds { get; set; } = 1.0;
    /// <summary>Sessions: consecutive similar events at one place merge until a gap this long.</summary>
    public int SessionGapSeconds { get; set; } = 180;
    public int SessionMaxMinutes { get; set; } = 20;
    /// <summary>Identical raw events (same player/kind/block/place) within this window merge into one with a count.</summary>
    public double MergeWindowSeconds { get; set; } = 3;
    public int MaxEventsPerPlayerPerMinute { get; set; } = 40;
    public int MaxEventsPerMinute { get; set; } = 200;
    /// <summary>Raw events older than this that are rolled up are deleted (rollups stay).</summary>
    public int RetainDays { get; set; } = 7;
    /// <summary>Chat text stored with chat events (0 = only that they spoke).</summary>
    public int ChatChars { get; set; } = 60;
    /// <summary>Staying at an unknown structure this long marks it for the brain to ask about.</summary>
    public int LingerSeconds { get; set; } = 60;
    /// <summary>Vanilla/Bukkit-style death lines (group 1 = player, rest = cause). Servers that never print them are fine.</summary>
    public string[] DeathPatterns { get; set; } =
    [
        @"^(\w+) (fell out of the world|was slain by \w+|was killed by .+|drowned|tried to swim in lava|burned to death|went up in flames|blew up|was blown up by .+|hit the ground too hard|fell from a high place|suffocated in a wall|starved to death|was shot by .+|was pricked to death|died)\.?$",
        @"^(?:\[\w+\]\s*)?(\w+) (died|was killed|fell into the void)(?:\W.*)?$",
    ];
    public DangerOptions Danger { get; set; } = new();
    public WakeOptions Wake { get; set; } = new();
}

/// <summary>Danger state-machine settings. Times in milliseconds unless named otherwise.</summary>
public sealed class DangerOptions
{
    /// <summary>Falling: downward speed above this (blocks/s) with no solid block below...</summary>
    public double FallSpeed { get; set; } = 4;
    /// <summary>...for this long (8 ticks).</summary>
    public int FallEnterMs { get; set; } = 400;
    public int ExitMs { get; set; } = 1000;
    public int CooldownSeconds { get; set; } = 60;
    /// <summary>Hurt followed by horizontal motion toward a void edge within this window = knockback danger.</summary>
    public int KnockbackWindowMs { get; set; } = 1000;
    public double KnockbackSpeed { get; set; } = 3;
    /// <summary>Sneaking or placing/breaking at an edge within this window = intentional edge work: only real falls alarm.</summary>
    public int EdgeWorkSeconds { get; set; } = 10;
    public double MobRadius { get; set; } = 3;
    public int MobSessionEndSeconds { get; set; } = 8;
    public int MobEscalateHits { get; set; } = 3;
    public int MobEscalateSeconds { get; set; } = 20;
    /// <summary>Deaths from different sources within this window are one death.</summary>
    public int DeathDedupeSeconds { get; set; } = 5;
    /// <summary>A void-fall wake is held this long (island height to kill depth ≈ 5 s); a death inside the window
    /// merges into ONE wake ("fell into the void and died"). Non-void dangers wake immediately.</summary>
    public int VoidFallCoalesceSeconds { get; set; } = 6;
    /// <summary>A death within this long after an emitted fall wake is posted as a Normal-priority follow-up.</summary>
    public int FollowUpSeconds { get; set; } = 60;
}

/// <summary>Which detections wake the brain (most never do; they wait in the digest).</summary>
public sealed class WakeOptions
{
    public bool PlayerDeath { get; set; } = true;
    public bool PlayerDanger { get; set; } = true;
    public bool SignMentionsBot { get; set; } = true;
    public bool NewUnknownStructure { get; set; } = true;
    public bool ManagedChestChanged { get; set; } = true;
    public bool LingerAtUnknownStructure { get; set; } = true;
    /// <summary>Per (player, rule) cooldown.</summary>
    public int CooldownSeconds { get; set; } = 60;
    /// <summary>Global cap on wakes from this subsystem per 5 minutes.</summary>
    public int MaxWakesPer5Min { get; set; } = 6;
}

/// <summary>Deterministic sentence templates.</summary>
public static class ActivityText
{
    public static string Block(int id) => Blocks.Exists(id) ? Blocks.Get(id).Name.Replace('_', ' ') : "block";
    public static string Item(int id) => Items.Exists(id) ? Items.Get(id).Name.Replace('_', ' ') : Block(id);
    private static string P(BlockPos p) => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}");

    /// <summary>" at the cobble gen" / " on main island" / " at 3,64,-2".</summary>
    public static string At(PlaceRef? place, BlockPos? pos) =>
        place != null ? (place.Kind == IndexKinds.Island ? " on " + place.Name : " at the " + place.Name) : pos is { } p ? " at " + P(p) : "";

    public static string Qty(int n) => n >= 10 ? "~" + (n >= 100 ? n / 10 * 10 : n) : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The pieces of an event's sentence.</summary>
    private readonly record struct Parts(ActivityEvent E, string P, string At, string Blk, string Itm, string N);

    /// <summary>One sentence template per kind; any other kind reads "{player} {kind}{at}".</summary>
    private static readonly Dictionary<string, Func<Parts, string>> Sentences = new(StringComparer.Ordinal)
    {
        [ActivityKinds.Broke] = s => $"{s.P} {ActivityVerbs.Broken(s.Blk)} {s.N}{s.Blk}{s.At}",
        [ActivityKinds.Placed] = s => $"{s.P} placed {s.N}{s.Blk}{s.At}",
        [ActivityKinds.Harvested] = s => $"{s.P} harvested {s.N}{s.Blk}{s.At}",
        [ActivityKinds.Planted] = s => $"{s.P} planted {s.N}{s.Blk}{s.At}",
        [ActivityKinds.Opened] = s => $"{s.P} opened the {s.E.Place?.Name ?? s.Blk}" + (s.E.Place == null && s.E.Pos is { } op ? " at " + P(op) : ""),
        [ActivityKinds.Took] = s => $"{s.P} took {s.N}{s.Itm} from the {s.E.Place?.Name ?? "chest"}",
        [ActivityKinds.Stored] = s => $"{s.P} put {s.N}{s.Itm} in the {s.E.Place?.Name ?? "chest"}",
        [ActivityKinds.LitFurnace] = s => $"{s.P} lit the furnace{s.At}",
        [ActivityKinds.PickedUp] = s => $"{s.P} picked up {(s.E.Count > 1 ? s.E.Count + " " : "")}{s.Itm}",
        [ActivityKinds.Dropped] = s => $"{s.P} dropped {(s.E.Count > 1 ? s.E.Count + " " : "")}{s.Itm}{s.At}",
        [ActivityKinds.Equipped] = s => $"{s.P} switched to {s.Itm}",
        [ActivityKinds.Crafted] = s => $"{s.P} crafted {s.Itm}{s.At}",
        [ActivityKinds.Hurt] = s => $"{s.P} got hurt{s.At}",
        [ActivityKinds.Fought] = s => $"{s.P} fought a {s.E.Detail ?? "mob"}, {s.E.Count} hit{(s.E.Count == 1 ? "" : "s")} taken{s.At}",
        [ActivityKinds.Died] = s => $"{s.P} {s.E.Detail ?? "died"}{(s.E.Place != null ? s.At : "")}",
        [ActivityKinds.Slept] = s => $"{s.P} slept in a bed{s.At}",
        [ActivityKinds.Rode] = s => $"{s.P} got in a {s.E.Detail ?? "vehicle"}",
        [ActivityKinds.WroteSign] = s => $"{s.P} placed sign \"{s.E.Detail}\"{s.At}",
        [ActivityKinds.Joined] = s => $"{s.P} joined",
        [ActivityKinds.Left] = s => $"{s.P} left",
        [ActivityKinds.Chat] = s => s.E.Detail is { Length: > 0 } d ? $"{s.P} said \"{d}\"" : $"{s.P} chatted",
        [ActivityKinds.Arrived] = s => $"{s.P} arrived{s.At}",
        [ActivityKinds.Departed] = s => $"{s.P} left{(s.E.Place != null ? " the " + s.E.Place.Name : s.At)}",
    };

    public static string Describe(ActivityEvent e)
    {
        string p = e.Player, at = At(e.Place, e.Pos);
        string blk = e.BlockId is { } b ? Block(b) : "block", itm = e.ItemId is { } i ? Item(i) : "item";
        string n = e.Count > 1 ? Qty(e.Count) + " " : "";
        return e.Kind is not null && Sentences.TryGetValue(e.Kind, out var sentence) ? sentence(new Parts(e, p, at, blk, itm, n)) : $"{p} {e.Kind}{at}";
    }
}
