using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineClaw.App.Supervisor;

/// <summary>One live tile: a body (main, a hive clone, a subhive body) with its job, vitals and claims.</summary>
/// <param name="Kind">"main" | "hive" | "subhive" | "script" (null = an older list that didn't say).</param>
/// <param name="Owner">The owner's login (null for main; main's login for its direct children; a hive's login for its bodies).</param>
/// <param name="Online">In game now; false = spawning, logging in or gone (the position may be unknown, then 0,0,0).</param>
/// <param name="State">The ownership tree's state ("spawning" | "running" | "releasing"), null when the tree doesn't know it.</param>
public sealed record BotTile(string Name, string Short, bool Main, int Dim, double X, double Y, double Z, int Hp, int Food, string? Job,
    string? Brain, double AgeSeconds, IReadOnlyList<string> Claims, string? Kind = null, string? Owner = null,
    MineClaw.Agent.Telemetry.BodyScriptBrief? Script = null, bool Online = true, string? State = null);

/// <summary>A rendered view: the PNG bytes as the brain/perception would get them, plus the text that rides with them.</summary>
public sealed record ViewImage(byte[] Png, int Width, int Height, int Tokens, string Legend, IReadOnlyList<string> Facts, string? Seen = null,
    string? Note = null);

/// <summary>A free-camera frame request (b1.8.1 yaw: 0 = south, 90 = west; pitch: + = down).</summary>
public sealed record CameraQuery(double X, double Y, double Z, float Yaw, float Pitch, int Dim, int W, int H);

/// <summary>A jump target for the camera ("mob chamber", "67", "4,64,6").</summary>
public sealed record LocateResult(string Name, int Dim, double X, double Y, double Z);

/// <summary>One line of the danger/event feed.</summary>
public sealed record FeedEvent(long Id, DateTimeOffset At, string Kind, string Level, string Text);

/// <summary>
/// What the supervisor endpoint reads (the main process's hive: VisionHub, the supervisor, the TUI's telemetry sources).
/// Everything is read-only. Rendering methods get the <see cref="RenderGate"/> and run their CPU work through it.
/// </summary>
public interface ISupervisorData
{
    IReadOnlyList<BotTile> Bots();
    /// <summary>A bot's live view (mode sheet|brain|eye|top|iso|side|layers). Null = no such bot online.</summary>
    Task<ViewImage?> ViewAsync(string bot, string mode, int radius, RenderGate gate, CancellationToken ct);
    /// <summary>The view with the outside cameras' x-ray forced on or off (the Explorer's toggle); Auto = the AI's rule.</summary>
    Task<ViewImage?> ViewAsync(string bot, string mode, int radius, MineClaw.Render.XRay xray, RenderGate gate, CancellationToken ct) =>
        ViewAsync(bot, mode, radius, gate, ct);
    Task<ViewImage> HiveAsync(RenderGate gate, CancellationToken ct);
    Task<ViewImage> CameraAsync(CameraQuery q, RenderGate gate, CancellationToken ct);
    /// <summary>The TUI's panels as data: usage vs caps per tier, thoughts and tool calls, chat, the hive job/claim board.</summary>
    object Status(string? selected);
    IReadOnlyList<FeedEvent> Events(long since, int max);
    LocateResult? Locate(string query);

    /// <summary>Saved designs (build from a description), newest first: id, name, status, summary, cost.</summary>
    IReadOnlyList<object> Designs() => [];
    /// <summary>A design's preview PNG (the contact sheet with the planned blocks in place), or null.</summary>
    ViewImage? DesignPicture(long id) => null;
    /// <summary>A design's bill, evaluation, critique, DSL and build progress as data, or null.</summary>
    object? DesignBill(long id) => null;
    /// <summary>A design's build plan page (steps numbered, techniques coloured, scaffolding, progress), or null.</summary>
    string? BuildPlanPage(long id) => null;
    /// <summary>A design's build plan as data (every part), or null.</summary>
    object? BuildPlanData(long id) => null;
}

public static class SupervisorJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static byte[] Bytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Options);
}
