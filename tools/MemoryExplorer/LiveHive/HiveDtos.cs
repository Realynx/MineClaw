using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineClaw.MemoryExplorer.LiveHive;

// Wire shapes of the Supervisor's read-only world routes (snake_case JSON, see src/MineClaw.App/Supervisor/SupervisorWorldRoutes.cs).

public sealed record ColumnInfoDto(int Cx, int Cz, long Hash, long Seen, string? By, bool Live);

/// <summary>The index reply; <see cref="Version"/> (the hive's game version) and <see cref="Time"/> (world ticks) come from newer
/// supervisors only.</summary>
public sealed record IndexDto(int Dim, long At, List<ColumnInfoDto> Columns, string? Version = null, long? Time = null);

public sealed record BotDto(string Name, string Short, bool Main, int Dim, double X, double Y, double Z, float? Yaw, float? Pitch, double? PoseAge,
    int Hp, int Food, string? Job, string? Brain, string? Held, List<string>? Claims, bool Live, double AgeSeconds)
{
    /// <summary>"main" / "67": what the /view route and players call it.</summary>
    public string Key => string.IsNullOrEmpty(Short) ? Name : Short;
}

public sealed record EntityDto(string Kind, string? Name, int Dim, double X, double Y, double Z, int? Id = null, int? Type = null,
    int? Item = null, int? Damage = null, int? Count = null, double? Vx = null, double? Vy = null, double? Vz = null, double? Age = null,
    float? Yaw = null, string? By = null, int? Flags = null, int? Variant = null)
{
    public const int Burning = 0x01, Sneaking = 0x02, Sprinting = 0x08;
}

/// <summary>The entities reply: full (no <c>since</c>, or <see cref="Full"/>) or a delta since the revision the client sent.</summary>
public sealed record EntitiesDto(long At, List<BotDto> Bots, List<EntityDto> Entities, long Rev = 0, bool Full = true, List<int[]>? Removed = null);

public sealed record MarkDto(string Kind, string Text, int[] Min, int[] Max, string? Owner);

public sealed record PlanDto(long Id, string Name, string Status, int Part, List<int[]> Blocks);

public sealed record OverlaysDto(DateTimeOffset At, int Dim, List<MarkDto> Claims, List<MarkDto> Places, List<MarkDto> Points, List<PlanDto> Plans,
    List<MarkDto> Dangers, List<string> Notes)
{
    public static OverlaysDto Empty(int dim) => new(DateTimeOffset.MinValue, dim, [], [], [], [], [], []);
}

public sealed record LocateDto(string Name, int Dim, double X, double Y, double Z);

public static class HiveJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static T? Parse<T>(byte[] utf8) => JsonSerializer.Deserialize<T>(utf8, Options);
}
