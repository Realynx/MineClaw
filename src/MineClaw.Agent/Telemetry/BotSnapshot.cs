using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineClaw.Agent.Telemetry;

/// <summary>Connection state of a hive member as shown by the TUI.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BotStatus>))]
public enum BotStatus { Offline, Connecting, Online, Dead, Paused }

public readonly record struct BotPos(double X, double Y, double Z)
{
    public override string ToString() => $"{(int)Math.Floor(X)},{(int)Math.Floor(Y)},{(int)Math.Floor(Z)}";
}

/// <summary>Spend of one tier (opus / worker / memory / other) over a day or month.</summary>
public sealed record TierUsage(string Tier, decimal Usd, int Calls, double CacheHit, long PromptTokens, long OutputTokens);

/// <summary>What the token/cost panel needs for one bot.</summary>
public sealed record UsageSnapshot
{
    public decimal DailyCapUsd { get; init; }
    public decimal MonthlyCapUsd { get; init; }
    public string Mode { get; init; } = "Normal";
    public decimal TodayUsd { get; init; }
    public decimal MonthUsd { get; init; }
    public double TodayCacheHit { get; init; }
    public IReadOnlyList<TierUsage> Today { get; init; } = [];
    public IReadOnlyList<TierUsage> Month { get; init; } = [];
    public int CallsLastHour { get; init; }
    /// <summary>Calls per clock hour, oldest first (24 entries; last = current hour).</summary>
    public int[] CallsPerHour { get; init; } = [];
    public PromptUsageRecord? Last { get; init; }
}

/// <summary>A hive member or player/mob/item on the map, relative to the grid centre (the bot's feet block).</summary>
/// <param name="Kind">"player", "mob" (hostile), "item".</param>
public sealed record ViewMarker(string Kind, string? Name, int Dx, int Dy, int Dz);

/// <summary>
/// Compact top-down + side view around a bot, sized 2R+1 square. <see cref="Top"/>: row 0 = north (−Z), column 0 = west
/// (−X); one terrain glyph per column (see the TUI legend; ' ' = void/air). <see cref="Side"/>: the X/Y slice through the bot
/// looking north, row 0 = <see cref="SideTopY"/>. Markers are separate so the viewer can colour/overlay them.
/// </summary>
public sealed record ViewportGrid
{
    public int Radius { get; init; }
    public int Cx { get; init; }
    public int Cy { get; init; }
    public int Cz { get; init; }
    public string[] Top { get; init; } = [];
    public string[] Side { get; init; } = [];
    public int SideTopY { get; init; }
    public IReadOnlyList<ViewMarker> Markers { get; init; } = [];
}

/// <summary>
/// Everything the TUI shows about one bot. Serializable (System.Text.Json, snake_case via <see cref="Json"/>) so hive clones
/// in other processes can stream it to the main process.
/// </summary>
public sealed record BotSnapshot
{
    public string Name { get; init; } = "";
    public BotStatus Status { get; init; }
    public DateTimeOffset At { get; init; }
    public BotPos? Pos { get; init; }
    /// <summary>Dimension of <see cref="Pos"/>: 0 overworld, -1 nether.</summary>
    public int Dimension { get; init; }
    public int Hp { get; init; }
    public int Food { get; init; }
    public string? Task { get; init; }
    /// <summary>The state header line the brain sees (pos, hp, held item, day/night).</summary>
    public string? Header { get; init; }
    public string BudgetMode { get; init; } = "Normal";
    public ThoughtSnapshot? Thought { get; init; }
    public UsageSnapshot? Usage { get; init; }
    public ViewportGrid? Viewport { get; init; }
    /// <summary>Recent chat lines (oldest first, capped).</summary>
    public IReadOnlyList<string> Chat { get; init; } = [];

    public static BotSnapshot Offline(string name) => new() { Name = name, Status = BotStatus.Offline, At = DateTimeOffset.UtcNow };

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static BotSnapshot? FromJson(string json) => JsonSerializer.Deserialize<BotSnapshot>(json, Json);
}

/// <summary>
/// One bot as seen by the TUI. The main bot is served in-process; hive clones (separate processes) are served by an IPC
/// transport that deserializes streamed <see cref="BotSnapshot"/>s and <see cref="PromptUsageRecord"/>s.
/// </summary>
public interface IBotTelemetrySource
{
    /// <summary>Login name (stable key).</summary>
    string Name { get; }

    /// <summary>Latest snapshot. <paramref name="full"/> = include thought context and viewport (the selected bot only);
    /// false is the cheap form for the bot strip. Must be fast and thread-safe; never throws.</summary>
    BotSnapshot Snapshot(bool full);

    /// <summary>Each finished LLM call of this bot.</summary>
    event Action<PromptUsageRecord>? UsageRecorded;

    /// <summary>Runs a console line for this bot (plain text = chat, "/..." = console command). Returns false if unreachable.</summary>
    Task<bool> SendAsync(string line, CancellationToken ct);
}
