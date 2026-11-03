using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Bot.Bodies;
using MineClaw.Hive;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Hierarchy.Bus;
using MineClaw.Hive.Slots;
using MineClaw.Scripting;
using MineClaw.Scripting.Crew;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// Subhive bodies wired into a hive (docs/design/hive-hierarchy.md, "Subhive bodies"): main hosts its own next to the clone
/// processes; a hive-kind clone (an Opus or DeepSeek brain) hosts its own in its process, every account allocated, whitelisted and
/// released by main over the bus. Each body asks for a small view distance and shares its process's chunk store.
/// </summary>
internal static class SubhiveWiring
{
    /// <summary>The per-body client settings: the small view distance subhive bodies ask for.</summary>
    public static void ConfigureClients(InProcBodies host, HiveConfig cfg) =>
        host.ClientSetup = o => SubhiveClientSetup.Apply(o, host.ViewDistance, cfg.Subhives.ShareChunks);

    /// <summary>
    /// Main: its own subhive bodies (allocated → whitelisted before login → driven by main's brain → released: whitelist remove),
    /// hive clones hosting theirs over the bus, and the resource-aware ceiling (memory headroom) next to the player cap.
    /// </summary>
    public static void AttachMain(HiveHierarchy h, InProcBodies? host, HiveConfig cfg, ILogger log)
    {
        h.MaxBodiesPerSpawn = Math.Max(1, cfg.Subhives.MaxPerSpawn);
        h.LaunchGapSeconds = cfg.LaunchGapSeconds;
        h.LaunchBatch = cfg.LaunchBatch;
        h.RemoteSubhives = cfg.Subhives.Enabled;
        // Main's managed heap too: its own bodies and every in-process hive share it (GCHeapHardLimit).
        h.Resources = cfg.Resources.Enabled ? new ResourceCeiling(cfg.Resources, h.Use) { Heap = ResourceCeiling.GcHeap } : null;
        if (h.Resources is { } res) log.LogInformation("hive: resource ceiling: {State}; hard cap {Cap}", res.Describe(), cfg.MaxClones > 0 ? cfg.MaxClones : "none");
        if (host == null) return;
        h.Subhives = host;
        host.BeforeConnect = (account, ct) => h.BeforeLaunchAsync(account, ct);
        // A "not white-listed" kick: main adds the account again (the gate's scoped exception), the login retries soon.
        host.Rewhitelist = (account, ct) => h.RewhitelistAsync(account, ct);
        host.Note = h.NoteBody;
        host.Gone += (account, reason) => h.Released(account, reason);
        ConfigureClients(host, cfg);
    }

    /// <summary>
    /// A hive-kind clone's bodies: the bus inbox takes main's <c>host</c> (log this allocated account in) and <c>release</c>; every
    /// body that logged out for good is reported (<c>subhive_gone</c>); once this hive is in game it asks main for the bodies its
    /// task came with, and tells its brain what it actually got.
    /// </summary>
    /// <param name="send">A bus request to main (type, body, timeout): the clone host's pipe; tests route it in process.</param>
    /// <param name="crews">This hive's crews over bodies spawned for them: a hosted account tagged with a crew joins it, no job of its own.</param>
    /// <param name="crew">The crew (reserved in <paramref name="crews"/>) the <paramref name="wanted"/> bodies of a hive spawned with a script join.</param>
    public static Func<BusEnvelope, CancellationToken, Task<BusReply>?> AttachClone(string me, Func<string, object?, TimeSpan, Task<BusReply?>> send,
        InProcBodies bodies, AgentRuntime rt, HiveConfig cfg, int wanted, Func<bool> inGame, CancellationToken stop, ILogger log,
        HiveCrews? crews = null, string? crew = null)
    {
        ConfigureClients(bodies, cfg);
        bodies.Gone += (account, reason) => _ = send(BusTypes.SubhiveGone, new SubhiveGoneMsg(account, reason), TimeSpan.FromSeconds(5));
        if (crews != null) bodies.Gone += (account, _) => crews.Left(account);
        if (wanted > 0) _ = RequestAsync(send, rt, wanted, inGame, stop, log, crews, crew);
        return (env, _) => env.Type switch
        {
            BusTypes.Host when env.As<HostReq>() is { } h => Task.FromResult(Reply(Host(me, bodies, crews, h))),
            BusTypes.Release when env.As<ReleaseReq>() is { } r => Task.FromResult(Reply(bodies.Release(r.Id))),
            _ => null,
        };
    }

    /// <summary>Logs a body main allocated in; a crew's body joins its crew first and gets no job of its own (the coordinator's lane).</summary>
    private static string Host(string me, InProcBodies bodies, HiveCrews? crews, HostReq h)
    {
        if (h.Crew == null) return bodies.Spawn(me, h.Account, h.Task, h.Script);
        crews?.Arrived(h.Crew, h.Account);
        return bodies.Spawn(me, h.Account, null, null);
    }

    private static BusReply Reply(string text) => text.StartsWith("ok", StringComparison.Ordinal) ? BusReply.Of(text) : BusReply.Fail(text);

    private static async Task RequestAsync(Func<string, object?, TimeSpan, Task<BusReply?>> send, AgentRuntime rt, int wanted, Func<bool> inGame,
        CancellationToken stop, ILogger log, HiveCrews? crews = null, string? crew = null)
    {
        try
        {
            while (!inGame() && !stop.IsCancellationRequested) await Task.Delay(500, stop).ConfigureAwait(false);
            // Reset: after a crash restart the bodies this hive had died with its old process; main reuses those accounts first.
            var r = await send(BusTypes.Spawn, new SpawnReq(Kind: "subhive", Bodies: wanted, Reset: true, Crew: crew), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            var text = r == null ? "main didn't answer on the hive bus: no bodies" : r.Ok ? r.As<string>() ?? "ok" : r.Error ?? "refused";
            log.LogInformation("hive: asked main for {N} subhive bodies: {Text}", wanted, text);
            if (crew != null && crews != null)
            {
                // Spawned straight onto a crew script: the crew hands out the work; the brain hears it with its next real wake, not now.
                crews.Answer(crew, text);
                rt.Events.Post(GameEvent.Notice($"[hive] your bodies: {text}; they work your crew script as they log in (you wake on its result)",
                    EventPriority.Background));
                return;
            }
            rt.Events.Post(GameEvent.Notice($"[hive] your bodies: {text}. Drive them with body=\"<n>\"|[...]|\"all\" on do/stop/status; " +
                "for a job for all of them run a saved hive script (script{name}: one script works every body, late ones too; you sleep " +
                "until it ends); [bodies] shows them once they are in game.",
                EventPriority.Normal));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogWarning(ex, "hive: asking main for subhive bodies failed"); }
    }

    /// <summary>
    /// Main's hive scripts (<c>hive spawn kind=script bodies=N script=X</c>): ONE bodiless coordinator per script over exactly its
    /// subhive bodies in this process, no brain (<see cref="MineClaw.Hive.Crew.HiveScriptHost"/>). Main's own crews (its brain's
    /// <c>script</c> runs) no longer see the bodies a hive script owns.
    /// </summary>
    public static MineClaw.Hive.Crew.HiveScriptHost AttachScriptHives(HiveHierarchy h, MineClaw.Bot.GameTools.ScriptBridge scripts, BodyCrewPort mainCrew,
        BodyRegistry bodies, MineClaw.Agent.Tools.ToolRegistry tools, ICrewBoard board, ILogger log)
    {
        mainCrew.Excluded = account => h.Tree.Get(account)?.Crew != null;
        var host = new MineClaw.Hive.Crew.HiveScriptHost(scripts, new BodyCrewPort(bodies, tools, board), log);
        h.ScriptHives = host;
        return host;
    }

    /// <summary>A body's claim view in a hive clone: its siblings coordinate on this process's board, the rest of the hive on main's.</summary>
    public static Func<string, IClaimBoard> Layered(Hive.Claims.ClaimBoard local, IClaimBoard parent) => name => new LayeredClaimBoard(local.ViewFor(name), parent);
}

/// <summary>
/// Two boards as one view: a lease must be free on both (this process's bodies on <paramref name="local"/>, every other hive bot on
/// <paramref name="parent"/>, where this hive holds it under its own name). A refusal upstream gives the local lease back.
/// </summary>
internal sealed class LayeredClaimBoard(IClaimBoard local, IClaimBoard parent) : IClaimBoard
{
    public string Owner => local.Owner;

    public bool IsFree(ClaimKey key) => local.IsFree(key) && parent.IsFree(key);

    public async Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
    {
        var l = await local.TryClaimAsync(key, ttl, note, ct).ConfigureAwait(false);
        if (!l.Ok) return l;
        var p = await parent.TryClaimAsync(key, ttl, note, ct).ConfigureAwait(false);
        if (p.Ok) return l;
        await local.ReleaseAsync(key, ct).ConfigureAwait(false);
        return p;
    }

    public async Task ReleaseAsync(ClaimKey key, CancellationToken ct = default)
    {
        await local.ReleaseAsync(key, ct).ConfigureAwait(false);
        // Upstream it's the hive's lease: give it back only when no sibling holds one like it.
        if (local.IsFree(key)) await parent.ReleaseAsync(key, ct).ConfigureAwait(false);
    }

    public string? HolderOf(ClaimKey key) => local.HolderOf(key) ?? parent.HolderOf(key);
}

/// <summary>Several crew ports as one: main's in-process subhive bodies and its clone processes' lanes, on one shared board.</summary>
internal sealed class CompositeCrewPort(ICrewBoard board, params ICrewPort[] ports) : ICrewPort
{
    public ICrewBoard Board { get; } = board;

    public IReadOnlyList<string> Bodies() => ports.SelectMany(p => p.Bodies()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private ICrewPort For(string body) =>
        ports.FirstOrDefault(p => p.Bodies().Contains(body, StringComparer.OrdinalIgnoreCase)) ?? ports[0];

    public BodyInfo? Info(string body) => For(body).Info(body);
    public int Count(string body, string item) => For(body).Count(body, item);
    public ILane Start(LaneSpec spec) => For(spec.Body).Start(spec);
    public Task<ActResult> ActAsync(string body, string action, System.Text.Json.JsonElement args, CancellationToken ct) => For(body).ActAsync(body, action, args, ct);
    public int Pending() => ports.Sum(p => p.Pending());
    public string? HeldBy(string body) => For(body).HeldBy(body);
    public bool Hold(string body, string crew) => For(body).Hold(body, crew);
}

/// <summary>
/// Subhive body client options: the small view distance, and one chunk store per process (overlapping views held once). Process-wide
/// by design: main's bodies and every in-process hive's bodies all see the one server, so a column two of them see is the same column
/// (keyed by dimension and position, ref-counted per view; each world keeps its own loaded keys and events). Keyed by the version
/// profile: a store binds one column height (128 b1.8.1, 256 1.12.2), and one process plays one version (tests run both).
/// </summary>
internal static class SubhiveClientSetup
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MineClaw.Client.Versioning.GameVersionProfile, MineClaw.World.Chunks.SharedChunkStore> Stores =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>The process's shared store for <paramref name="profile"/>'s version (lazily made).</summary>
    public static MineClaw.World.Chunks.SharedChunkStore StoreFor(MineClaw.Client.Versioning.GameVersionProfile profile) =>
        Stores.GetOrAdd(profile, _ => new MineClaw.World.Chunks.SharedChunkStore());

    /// <summary>The b1.8.1 store (the default profile); <see cref="StoreFor"/> for the version a client speaks.</summary>
    public static MineClaw.World.Chunks.SharedChunkStore Store => StoreFor(MineClaw.Client.Versioning.GameVersionProfiles.Beta181);

    public static MineClaw.Client.ClientOptions Apply(MineClaw.Client.ClientOptions o, int? viewDistance, bool share) =>
        o with { ViewDistance = viewDistance ?? o.ViewDistance, ChunkStore = share ? StoreFor(o.Profile) : o.ChunkStore };
}
