using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineClaw.Hive.Ipc;

/// <summary>
/// One IPC frame: <c>{"t": type, "id": request id (0 = notification), "re": id this answers (0 = none), "d": payload, "err": error}</c>.
/// Sent length-prefixed (see <see cref="FrameCodec"/>), snake_case JSON.
/// </summary>
public sealed record HiveMessage
{
    public string T { get; init; } = "";
    public long Id { get; init; }
    public long Re { get; init; }
    public JsonElement? D { get; init; }
    public string? Err { get; init; }

    public T2? As<T2>() => D is { } d && d.ValueKind != JsonValueKind.Null ? d.Deserialize<T2>(HiveJson.Options) : default;
}

public static class HiveJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static JsonElement? ToElement(object? payload) =>
        payload == null ? null : payload is JsonElement e ? e : JsonSerializer.SerializeToElement(payload, payload.GetType(), Options);
}

/// <summary>Message types. Direction: C→M = clone to main, M→C = main to clone.</summary>
public static class Msg
{
    // lifecycle
    public const string Hello = "hello";          // C→M req  HelloMsg → WelcomeMsg
    public const string Heartbeat = "hb";         // both     (no payload; sent by each link's own heartbeat thread)
    public const string Ping = "ping";            // both req (no payload) → empty reply, answered by the link itself (liveness probe)
    public const string Bye = "bye";              // C→M      ByeMsg (planned logout finished; pipe closes next)
    public const string Recall = "recall";        // M→C      RecallMsg (deposit everything, log out)
    // tasks
    public const string Task = "task";            // M→C      TaskMsg (assign / replace)
    public const string Progress = "progress";    // C→M      ProgressMsg
    public const string Result = "result";        // C→M      ResultMsg (then the clone logs out)
    // claims
    public const string Claim = "claim";          // C→M req  ClaimMsg → ClaimReplyMsg (acquire / renew)
    public const string Release = "release";      // C→M      ReleaseMsg
    public const string Claims = "claims";        // M→C      ClaimsMsg (full mirror of live leases)
    // chat arbitration
    public const string Roster = "roster";        // M→C      RosterMsg (live clones, main names, sticky owners)
    public const string ChatAccepted = "chat_ok"; // C→M      ChatAcceptedMsg (this clone took a player's message)
    public const string Thread = "thread";        // C→M      ThreadMsg (this clone owns / released a player's replies)
    public const string Inject = "inject";        // M→C      InjectMsg (console /as test chat, so every process arbitrates it)
    public const string Note = "note";            // M→C      NoteMsg (an update for the running task: a partner joined/changed; never replaces it)
    // budget ledger
    public const string SpendAuth = "spend_auth"; // C→M req  SpendAuthMsg → SpendGrantMsg
    public const string Spend = "spend";          // C→M      SpendMsg (actual cost of a finished call)
    // telemetry (TUI)
    public const string Snapshot = "snap";        // C→M      BotSnapshot (full)
    public const string Usage = "usage";          // C→M      PromptUsageRecord
    public const string Cmd = "cmd";              // M→C req  CmdMsg → bool (TUI console line for this clone)
    // inventory
    public const string Inventory = "inv";        // C→M      InventoryMsg (live snapshot for the hive ledger)
    public const string Death = "death";          // C→M      DeathMsg (cause + lost items)
    public const string NeedsInput = "needs_input"; // C→M     NeedsInputMsg (stays online, task paused: a new cell/task, or take my items)
    public const string VoidHold = "void_hold";   // C→M      VoidHoldMsg (holding still over the void, asked for a /tp; Held=false: tp landed)
    public const string Handoff = "handoff";      // C→M req  HandoffMsg → HandoffReplyMsg (where to toss items)
    public const string HandoffAck = "handoff_ack"; // C→M req (no payload) → HandoffAckMsg (items main collected since)
    public const string Lend = "lend";            // C→M req  LendMsg → LendReplyMsg (main hands a clone an item it has spare)
    public const string LendGo = "lend_go";       // C→M req  LendGoMsg → LendDoneMsg (the clone stands next to main: toss now)
    // replies
    public const string Occupied = "occupied";    // C→M      OccupiedMsg (someone else logged in as this clone: kicked, not retrying)
    public const string View = "view";            // M→C req  (no payload) → CloneViewMsg ("send me your view": flush world memory, pose)
    public const string ScriptDebug = "script_dbg"; // M→C req ScriptDebugReq → ScriptDebugSnapshot (the live script debugger; null = none)
    public const string VisionList = "vision";    // M→C req VisionListReq → VisionListMsg (the sheets this clone's models got: "what the AI saw")
    public const string VisionPng = "vision_png"; // M→C req VisionPngReq → VisionPngMsg (one of them: the exact PNG bytes; null = gone)
    public const string Reply = "re";
}

public sealed record HelloMsg(string Name, int Pid);
public sealed record WelcomeMsg(string Main, TaskMsg? Task, RosterMsg Roster, ClaimsMsg Claims, bool Stranded, int StrandedItems);
/// <param name="Loadout">What to fetch from storage before starting ("pickaxe", "axe", "food"...): derived from the task text.</param>
/// <param name="Claims">Lease keys the clone held when it crashed (re-granted on restart when still free).</param>
/// <param name="Profile">Model profile for the clone's brain tier (null = the clone's config).</param>
/// <param name="Script">Saved script the clone starts at once with its brain asleep (null = a normal brain task).</param>
/// <param name="Args">The script's args (JSON object).</param>
/// <param name="After">When the script ends normally: "logout" (default; deposit first) or "sleep" (stay online, brain asleep).</param>
public sealed record TaskMsg(string Id, string Text, bool Restarted = false, string[]? Loadout = null, string[]? Claims = null,
    string? Profile = null, string? Script = null, string? Args = null, string? After = null)
{
    public bool SleepsAfter => string.Equals(After, "sleep", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What <c>hive spawn</c> may add to a task: a model profile, a script (saved name or inline code) and its options.</summary>
public sealed record HiveSpawnOptions(string? Profile = null, string? Script = null, string? Code = null, string? Args = null, string? After = null)
{
    /// <summary>Log in although the saved spot is over the void (a player will /tp it). Main's chat path sets the same per clone
    /// from a trusted player's line (HiveSupervisor.NotePlayerChat); not a tool argument (zero prompt bytes).</summary>
    public bool ConfirmVoid { get; init; }
    public bool HasScript => !string.IsNullOrWhiteSpace(Script) || !string.IsNullOrWhiteSpace(Code);
}
public sealed record ProgressMsg(string TaskId, string Text, double? Pct = null);
/// <param name="Online">A failure the clone stays online after (idle, safe, awaiting orders for <c>HiveConfig.FailureGraceSeconds</c>).</param>
public sealed record ResultMsg(string TaskId, bool Ok, string Text, bool Online = false);
public sealed record ClaimMsg(string Key, double TtlSeconds, string? Note = null);
public sealed record ClaimReplyMsg(bool Ok, string? Holder, long ExpiresMs);
public sealed record ReleaseMsg(string Key);
public sealed record LeaseMsg(string Key, string Owner, long ExpiresMs);
public sealed record ClaimsMsg(List<LeaseMsg> Leases);
/// <param name="Delegated">Main handed the player's request to this clone: their follow-ups are its (explicit) for the window.</param>
public sealed record StickyMsg(string Owner, long UntilMs, bool Delegated = false);
public sealed record RosterMsg(string Main, string[] MainNames, string[] Live, Dictionary<string, StickyMsg> Sticky,
    Dictionary<string, StickyMsg>? Threads = null);
/// <summary>A clone opened (or closed) a conversation-thread lease on a player (it asked them / watches their demo).</summary>
public sealed record ThreadMsg(string Player, bool Open, double TtlSeconds);
/// <param name="Spoke">True: the clone spoke to the player (continuation window), not took a line from them.</param>
public sealed record ChatAcceptedMsg(string Player, bool Spoke = false);
public sealed record InjectMsg(string Raw);
public sealed record NoteMsg(string Text);
public sealed record SpendAuthMsg(decimal EstimateUsd, string Tier);
public sealed record SpendGrantMsg(bool Ok, decimal GrantUsd, string Mode, string? Reason);
public sealed record SpendMsg(decimal Usd, string Tier, string Model);
public sealed record CmdMsg(string Line);
public sealed record ItemMsg(int Id, int Damage, int Count, string Name);
public sealed record InventoryMsg(List<ItemMsg> Items);
public sealed record DeathMsg(string Cause, List<ItemMsg> Lost);
/// <summary>The clone can't go on without main (a stalled gen cell, items it couldn't put away) and stays online waiting.</summary>
public sealed record NeedsInputMsg(string TaskId, string Text);
/// <summary>The clone spawned/was moved over the void and holds still (Held), or a /tp put it on ground again (!Held).</summary>
public sealed record VoidHoldMsg(bool Held, double X, double Y, double Z, string? Asked = null);
public sealed record HandoffMsg(int Items);
public sealed record HandoffReplyMsg(bool Ok, double X, double Y, double Z, string? Reason);
public sealed record HandoffAckMsg(int Collected);
/// <summary>A clone asks main for an item: a loadout kind ("pickaxe", "axe") or an item name ("stone_pickaxe", "torch").</summary>
public sealed record LendMsg(string Item, int Count, int[]? Ids = null);
/// <summary>Ok = main will toss <paramref name="Count"/> x <paramref name="Name"/> (item <paramref name="Id"/>) once the clone stands
/// next to it at X/Y/Z (main stands still meanwhile). Not ok: <paramref name="Reason"/>, plus other hive bots carrying a spare.</summary>
public sealed record LendReplyMsg(bool Ok, int Id, int Count, string? Name, double X, double Y, double Z, string? Reason, string[]? Holders = null);
public sealed record LendGoMsg(double X, double Y, double Z);
public sealed record LendDoneMsg(int Tossed, string? Reason);
/// <summary>A clone's live eye pose; <paramref name="Flushed"/> live columns were written/stamped in the shared world memory first.</summary>
/// <summary>"Show me your script" (the Explorer's live debugger via main's Supervisor): <see cref="Code"/> = include the source.</summary>
public sealed record ScriptDebugReq(bool Code);
/// <summary>"Which pictures did your models get?" (the Explorer's vision panel via main's Supervisor): ids above <see cref="Since"/>.</summary>
public sealed record VisionListReq(long Since = 0);
public sealed record VisionListMsg(int Capacity, List<MineClaw.Bot.Perception.Vision.VisionSheetInfo> Sheets);
public sealed record VisionPngReq(long Id);
public sealed record VisionPngMsg(long Id, byte[] Png);
public sealed record CloneViewMsg(double X, double Y, double Z, float Yaw, float Pitch, int Dim, int Flushed);
public sealed record RecallMsg(string Reason, double? DeadlineSeconds = null);
public sealed record ByeMsg(string Reason, bool Deposited, int Stranded);
/// <summary>The server kicked this clone because another session logged in with its name (a player's viewer).</summary>
public sealed record OccupiedMsg(string Reason, int Items);
