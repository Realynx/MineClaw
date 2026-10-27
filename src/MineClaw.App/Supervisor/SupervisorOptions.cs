namespace MineClaw.App.Supervisor;

/// <summary>
/// "Supervisor": the read-only hive-mind viewer hosted by the main bot (graphical TUI: every bot's live view, a free camera over
/// the shared world memory, the token/thought/chat/event panels). GET only: no commands, chat or movement. Reach it through an
/// SSH tunnel (tools/HiveSupervisor), so nothing is exposed beyond the LAN.
/// </summary>
public sealed class SupervisorOptions
{
    public bool Enabled { get; set; }
    /// <summary>Listen address: the CT's LAN IP (e.g. 10.1.2.73), "localhost", or "*" (every interface).</summary>
    public string Bind { get; set; } = "*";
    public int Port { get; set; } = 8765;
    /// <summary>Optional shared secret (X-Supervisor-Token header or ?token=). Empty = none. Never logged.</summary>
    public string? Token { get; set; }
    /// <summary>A bot view is rendered at most once per this (every tile and the brain view share it).</summary>
    public int CacheMs { get; set; } = 1000;
    /// <summary>Hive overview cache.</summary>
    public int HiveCacheMs { get; set; } = 2000;
    /// <summary>CPU cap: renders run one at a time on one below-normal background thread, at least this far apart (≤ 8/s).</summary>
    public int MinRenderIntervalMs { get; set; } = 120;
    /// <summary>Renders waiting beyond this answer 503 (the UI retries on its next tick).</summary>
    public int MaxQueuedRenders { get; set; } = 12;
    /// <summary>Concurrent HTTP requests handled; more wait ≤ 2 s, then 503.</summary>
    public int MaxConnections { get; set; } = 8;
    /// <summary>Default contact-sheet radius (Bot:Vision:SheetRadius is 6).</summary>
    public int Radius { get; set; } = 6;
    /// <summary>Eye tile size (the sheet's own EYE panel is ≈290 px).</summary>
    public int EyeWidth { get; set; } = 480;
    public int EyeHeight { get; set; } = 360;
    /// <summary>Free camera: largest frame and view distance.</summary>
    public int CameraMaxWidth { get; set; } = 800;
    public int CameraMaxHeight { get; set; } = 600;
    public int CameraDistance { get; set; } = 48;
    /// <summary>A clone's "send me your view" IPC (it flushes world memory to the db) at most this often per clone.</summary>
    public int PoseCacheMs { get; set; } = 2500;
}
