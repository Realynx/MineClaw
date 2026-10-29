using System.Text.Json;
using MineClaw.Hive.Ipc;

namespace MineClaw.Hive.Hierarchy.Bus;

/// <summary>
/// The off-channel hive bus: every inter-hive message, never game chat. Envelopes ride the existing IPC frame (<c>t = "bus"</c>)
/// between processes and are routed by main; in-process endpoints call <see cref="HiveBus.RouteAsync"/> directly.
/// </summary>
/// <param name="Type">One of <see cref="BusTypes"/>.</param>
/// <param name="From">Sender endpoint: "main" or a hive's account.</param>
/// <param name="To">"main", a hive's account, or "*" (every hive except the sender).</param>
/// <param name="Token">The sender's bus token (handed out by main at registration / in the IPC welcome).</param>
public sealed record BusEnvelope(string Type, string From, string To = HiveTree.Root, JsonElement? Body = null, string? Token = null, long Seq = 0)
{
    public T? As<T>() => Body is { } b && b.ValueKind != JsonValueKind.Null ? b.Deserialize<T>(HiveJson.Options) : default;

    public static BusEnvelope Make(string type, string from, string to, object? body, string? token = null) =>
        new(type, from, to, HiveJson.ToElement(body), token);
}

public sealed record BusReply(bool Ok, JsonElement? Body = null, string? Error = null)
{
    public T? As<T>() => Body is { } b && b.ValueKind != JsonValueKind.Null ? b.Deserialize<T>(HiveJson.Options) : default;
    public static BusReply Fail(string why) => new(false, null, why);
    public static BusReply Of(object? body) => new(true, HiveJson.ToElement(body));
}

/// <summary>Bus message types. Requests go to main (the admin root), which decides; the rest are notifications.</summary>
public static class BusTypes
{
    // requests (→ main)
    public const string Whitelist = "whitelist";     // WhitelistReq: only for an account main manages and the asker owns
    public const string Spawn = "spawn";             // SpawnReq → text (owner = the asker)
    public const string Assign = "assign";           // AssignReq → text (a child the asker owns)
    public const string Handover = "handover";       // AssignReq → text (move a job to a child the asker owns)
    public const string Release = "release";         // ReleaseReq → text (the asker's subtree only)
    public const string List = "list";               // (no body) → text (the asker's subtree)
    // notifications
    public const string Status = "status";           // StatusMsg (progress of a hive's own work)
    public const string Result = "result";           // ResultBusMsg
    public const string Observation = "observation"; // ObservationMsg (shared: something every hive should know)
    public const string NeedHelp = "need_help";      // NeedHelpMsg
    // shared memory (one store, main writes)
    public const string MemWrite = "mem_write";      // MemWriteMsg → MemWriteReply (main grooms + stores)
    public const string MemChanged = "mem_changed";  // MemChangedMsg (main → every hive: reload now)
    // subhive bodies a hive hosts in its own process (main allocates + whitelists the account, the hive logs it in)
    public const string Host = "host";               // HostReq (main → the owning hive: log this body in; main also sends release here)
    public const string SubhiveGone = "subhive_gone"; // SubhiveGoneMsg (hive → main: that body logged out for good: whitelist remove)

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Whitelist, Spawn, Assign, Handover, Release, List, Status, Result, Observation, NeedHelp, MemWrite, MemChanged, Host, SubhiveGone],
        StringComparer.Ordinal);

    /// <summary>Types only main may send.</summary>
    public static readonly IReadOnlySet<string> MainOnly = new HashSet<string>([MemChanged, Host], StringComparer.Ordinal);
}

public sealed record WhitelistReq(string Op, string Name);
/// <param name="Bodies">How many (subhive bodies; or bodies a new hive-kind clone hosts).</param>
/// <param name="Reset">A hive (re)starting asks for its bodies: its old subhive accounts (gone with its old process) are reused first.</param>
public sealed record SpawnReq(string? Kind = null, string? Brain = null, string? Task = null, string? Script = null, string? Args = null,
    string? After = null, string? Name = null, int? Bodies = null, bool Reset = false);
public sealed record HostReq(string Account, string? Task = null, string? Script = null);
public sealed record SubhiveGoneMsg(string Account, string Reason);
public sealed record AssignReq(string To, string Task);
public sealed record ReleaseReq(string Id);
public sealed record StatusMsg(string Text, double? Pct = null);
public sealed record ResultBusMsg(bool Ok, string Text);
public sealed record ObservationMsg(string Text, string? Topic = null);
public sealed record NeedHelpMsg(string Text);

/// <summary>Who wrote a memory doc: provenance is metadata on the one shared store, never a separate store.</summary>
public sealed record MemProvenance(string Hive, string? Body = null, string? Brain = null)
{
    public string Tag => Brain is { Length: > 0 } b ? $"{Hive}/{b}" : Hive;
}

public sealed record MemDocMsg(string Ref, string Kind, string Title, string Body)
{
    public string? Tag { get; init; }
    public string? Source { get; init; }
    public double[]? Bounds { get; init; }
    public int Dimension { get; init; }
    public bool Embed { get; init; } = true;
    public double Weight { get; init; } = 1.0;
    public int Level { get; init; }
    public long? AtMs { get; init; }
    public Dictionary<string, string>? Meta { get; init; }
}

public sealed record MemWriteMsg(List<MemDocMsg> Docs, MemProvenance By);
public sealed record MemWriteReply(long[] Ids);
public sealed record MemChangedMsg(string[] Refs, long[] Ids, string By);
