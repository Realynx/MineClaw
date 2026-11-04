using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Demo;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Memory;
using MineClaw.Bot.WorldIndex;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;
using MPos = MineClaw.Agent.Memory.BlockPos;

namespace MineClaw.Bot;

/// <summary>
/// Composition root for the game bot: creates the agent runtime with chat sent through the client, registers the
/// game tools (before the first turn, so the tool list is a stable cached prefix), applies protection/reserved items
/// and attaches the event bridge.
/// </summary>
public sealed partial class BotHost : IDisposable
{
    public required AgentRuntime Runtime { get; init; }
    public required IGameClient Client { get; init; }
    public required GameBridge Bridge { get; init; }
    public required Protection Protection { get; init; }
    /// <summary>Vision v2: the see hub, the perception tier (telemetry) and auto-vision (null parts when off).</summary>
    public Perception.Vision.VisionWiring.Result? Vision { get; init; }
    public required DemonstrationRecorder Recorder { get; init; }
    public required MemoryStoreAdapter MemoryAdapter { get; init; }
    /// <summary>Zero-token map memory (chests, generators, farms, islands...) built from the client's world copy.</summary>
    public WorldIndexer? Indexer { get; init; }
    /// <summary>Other players' activity: attributed events, rollups, digest, wake rules.</summary>
    public PlayerActivityTracker? Activity { get; init; }
    /// <summary>Remembered world (chunk baselines, "since last time", stale planning data) and the scenery ledger.</summary>
    public WorldMemory? WorldMemory { get; init; }
    /// <summary>Players' pointing gestures (punches at blocks), for the turn context.</summary>
    public Perception.GestureTracker? Gestures { get; init; }
    /// <summary>Generator dig cells (taught or regen-confirmed): never protected.</summary>
    public Perception.DigCells? DigCells { get; init; }
    /// <summary>Learned mechanisms (buttons/levers/plates → doors, piston doors), persisted as observations.</summary>
    public MechanismWatcher? Mechanisms { get; init; }
    /// <summary>The brainstem: head turn, reflexes, speculative first steps, social idle (zero tokens).</summary>
    public Brainstem.BrainstemController? Brainstem { get; init; }
    /// <summary>Body-language gestures from the bot's own chat/emotes and addressed player chat (zero tokens).</summary>
    public Brainstem.Gestures? BodyGestures { get; init; }
    /// <summary>Hive claim board this bot honours (<see cref="NullClaimBoard"/> without a hive).</summary>
    public IClaimBoard Claims { get; init; } = NullClaimBoard.Instance;
    /// <summary>The acquisition planner's sources for this bot. Seam for the hive: set <c>RequestHandoff</c> (ask main / a clone
    /// holding a spare to hand it over).</summary>
    public AcquisitionSources? Acquisition { get; init; }
    /// <summary>Hive: keeps the client's learned spawn-protection region in step with the shared protected_region fact.</summary>
    internal System.Threading.Timer? ProtectionSync { get; init; }

    /// <summary>
    /// One brain, one body (step 1 of docs/design/one-brain-many-bodies.md): <see cref="CreateBrain"/> (runtime, shared stores,
    /// tools against the body routers), then <see cref="BodyFactory"/> wires the main body. The order is the old single-body
    /// order: the brain split before the script tool, see/do after every macro and the script tool, the brainstem gate last.
    /// </summary>
    public static BotHost Create(IGameClient client, AgentOptions agentOptions, BotOptions? botOptions = null, ILlmClient? llm = null,
        ILoggerFactory? loggers = null, TimeProvider? time = null, bool persist = true,
        WorldIndexOptions? worldIndex = null, ActivityOptions? activity = null,
        IClaimBoard? claims = null, Func<IEnumerable<string>>? hiveBots = null)
    {
        var brain = CreateBrain(client, agentOptions, botOptions, llm, loggers, time, persist, worldIndex, activity, claims, hiveBots);
        var main = brain.Main;
        BodyFactory.AttachJobs(brain, main);      // bridge (+ hunger latch, state header), the script lane (+ the script tool)
        FinishTools(brain);                       // see, then do (its door covers every macro that exists now)
        BodyFactory.AttachBehaviour(brain, main); // [here], brainstem (gate last), gestures, watchdog, standing orders, hand-overs
        return new BotHost
        {
            Brain = brain, Body = main,
            Watchdog = main.Watchdog,
            Handovers = main.Handovers, HandoverPump = main.HandoverPump,
            Runtime = brain.Runtime, Client = client, Bridge = main.Bridge!, Protection = brain.Protection, Recorder = brain.Recorder,
            MemoryAdapter = brain.Memory,
            Indexer = brain.Indexer, Activity = brain.Activity, WorldMemory = brain.WorldMemory, Claims = claims ?? NullClaimBoard.Instance,
            Gestures = brain.Gestures, DigCells = brain.DigCells, Mechanisms = brain.Mechanisms, Brainstem = main.Brainstem,
            BodyGestures = main.Gestures, ProtectionSync = brain.ProtectionSync,
            Acquisition = brain.Acquisition,
            PlacedBlocks = brain.PlacedBlocks, ProtectionMarks = brain.ProtectionMarks, ProtectionCommands = brain.ProtectionCommands,
            ProtectionTelemetry = brain.ProtectionTelemetry,
            Vision = brain.Vision,
            Blueprints = brain.Blueprints,
        };
    }

    /// <summary>The brain this host was built from (runtime, shared stores, body routers).</summary>
    public BrainContext? Brain { get; init; }
    /// <summary>The main body (step 1: the registry's only one).</summary>
    public Body? Body { get; init; }
    /// <summary>The bodies (step 1: exactly main).</summary>
    public BodyRegistry? Bodies => Brain?.Bodies;

    /// <summary>
    /// The brain: the agent runtime with chat sent through the main body's account, the shared stores and protection, the main
    /// body's perception, and every tool, registered once against the body routers (they act on <see cref="BodyScope.Current"/>,
    /// else the focus) before the first turn so the tool list is a stable cached prefix. Ends with the brain split and the
    /// blueprint tools; the per-body wiring follows (<see cref="BodyFactory"/>).
    /// </summary>
    public static BrainContext CreateBrain(IGameClient client, AgentOptions agentOptions, BotOptions? botOptions = null, ILlmClient? llm = null,
        ILoggerFactory? loggers = null, TimeProvider? time = null, bool persist = true,
        WorldIndexOptions? worldIndex = null, ActivityOptions? activity = null,
        IClaimBoard? claims = null, Func<IEnumerable<string>>? hiveBots = null) =>
        new BrainBuilder(client, agentOptions, botOptions ?? new BotOptions(), llm, loggers, time, persist, worldIndex, activity, claims, hiveBots).Build();

    /// <summary>
    /// The brain's last tools, after every macro and the script tool exist: vision v2 (see = contact sheet + role layer; with a
    /// perception model the brain gets verified text) and <c>do</c> (the brain's door to the single-step macros). Vision is the
    /// brain's eyes through the main body (other bodies are seen through the shared world memory: <c>see focus=67</c>).
    /// </summary>
    public static void FinishTools(BrainContext brain)
    {
        if (brain.ToolsFinished) return;
        var rt = brain.Runtime;
        var client = brain.Main.Adapter;
        var indexer = brain.Indexer;
        var digCells = brain.DigCells;
        var vision = Perception.Vision.VisionWiring.Attach(rt, client, brain.Memory, brain.BotOptions, brain.Protection,
            p => digCells.IsDigCell(p) || indexer?.IsConfirmedDigCell(p) == true, indexer, brain.Gestures, brain.GenCells, brain.HiveBots,
            brain.Time, brain.Log, brain.Persist);
        if (brain.Blueprints != null) brain.Blueprints.Vision = vision; // the preview's roles/atlas and the perception critic
        Agent.Tools.DoTool.Register(rt.Tools);
        brain.Vision = vision;
        brain.ToolsFinished = true;
    }

    /// <summary>Build from a description: the design service, its tools and the build crew seam (null when disabled).</summary>
    public MineClaw.Bot.Blueprints.BlueprintWiring? Blueprints { get; init; }
    /// <summary>Rule 1 persistence: own/hive placed blocks in the shared db.</summary>
    public PlacedBlockStore? PlacedBlocks { get; init; }
    /// <summary>Trusted permits and never-break marks (shared db).</summary>
    public ProtectionMarks? ProtectionMarks { get; init; }
    /// <summary>Chat → permits/marks.</summary>
    public ProtectionCommands? ProtectionCommands { get; init; }
    /// <summary>Refusals counted by reason.</summary>
    public ProtectionTelemetry? ProtectionTelemetry { get; init; }

    /// <summary>A remembered structure by name ("main house", "the house" → the one whose name contains it), as a box.</summary>
    /// <summary>
    /// Hand-off intents from chat (live 10-27 15:48: bread dropped "for 67" was auto-eaten the tick it was picked up): the bot's
    /// own "I'll pass it to 67" or a player's "pass this to 69" / "bread for 67" opens a short intent on the client, so what the
    /// bot picks up next is held for that target (auto-eat skips it unless hp is critical) until delivered or timed out.
    /// </summary>
    private static void AttachHandoffIntents(IGameClient client, AgentRuntime rt, Func<IEnumerable<string>>? hiveBots, ILogger? log)
    {
        if (client is not MinecraftGameClient mg) return;
        IEnumerable<string> Names() => (hiveBots?.Invoke() ?? []).Concat(client.World.Entities.OnlinePlayers)
            .Where(n => !n.Equals(rt.Chat.Username, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Self() => [rt.Chat.Username, rt.Chat.DisplayName, .. rt.Chat.Aliases];
        void On(string? text, string who)
        {
            try
            {
                if (text is null || Perception.HandoffIntent.Detect(text, Names(), Self()) is not { } hit) return;
                mg.Inner.Handoffs.Open(hit.Target, hit.ItemId);
                log?.LogInformation("hand-off intent ({Who}): pickups{Item} are for {Target}", who,
                    hit.ItemId is { } id ? " of " + MineClaw.Data.Items.DisplayName(id, 0) : "", hit.Target);
            }
            catch (Exception ex) { log?.LogWarning(ex, "hand-off intent failed"); }
        }
        rt.Chat.Accepted += m => On(m.Text, m.Player ?? "?");
        rt.Chat.Said += t => On(t, "me");
    }

    private static (MineClaw.World.BlockPos Min, MineClaw.World.BlockPos Max)? NamedStructure(AgentRuntime rt, IGameClient client, string name)
    {
        int dim = client.World.Dimension;
        var n = name.Trim();
        var s = rt.Memory.Read(d => d.Structures.Where(x => x.Min != null && x.Dimension == dim)
            .OrderBy(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(n, StringComparison.OrdinalIgnoreCase)
                                 || n.Contains(x.Name, StringComparison.OrdinalIgnoreCase)));
        if (s?.Min is not { } a) return null;
        var b = s.Max ?? a;
        return (new MineClaw.World.BlockPos(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
            new MineClaw.World.BlockPos(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
    }

    /// <summary>
    /// This account is op (Bot:Protection:ServerOp, main's account): spawn protection doesn't bind IT, so it is exempt from the
    /// remembered fact. The fact itself stays in the shared memory for every other hive account (clones and bodies aren't op);
    /// a fact that never named the accounts it binds is dropped, as before (nothing says anyone else is bound).
    /// </summary>
    /// <summary>Startup: Bot:Protection:SpawnProtectionOff drops the remembered fact for everyone; else an op account is exempt.</summary>
    public static void ApplySpawnProtectionAtStartup(AgentRuntime rt, ILogger? log, ProtectionOptions protection, string? account)
    {
        if (protection.SpawnProtectionOff) ForgetSpawnProtection(rt, log);
        else if (protection.ServerOp) ForgetSpawnProtection(rt, log, account);
    }

    public static void ForgetSpawnProtection(AgentRuntime rt, ILogger? log, string? account = null)
    {
        try
        {
            int dropped = 0, exempt = 0;
            foreach (int dim in new[] { 0, -1 })
            {
                if (SpawnProtectionFact.Read(rt.Observations, dim) is not { } fact) continue;
                if (account != null && fact.Affects.Any(a => !a.Equals(account, StringComparison.OrdinalIgnoreCase)))
                {
                    fact.WithExempt(account).Write(rt.Observations, MineClaw.World.ChunkColumn.MaxHeight);
                    exempt++;
                    continue;
                }
                SpawnProtectionFact.Retract(rt.Observations, dim);
                dropped++;
            }
            int notes = dropped > 0 ? RetractSpawnProtectionNotes(rt) : 0;
            if (dropped + notes > 0) log?.LogInformation("op: forgot the remembered spawn protection ({Rows} region(s), {Notes} note(s))", dropped, notes);
            if (exempt > 0) log?.LogInformation("op: {Account} is exempt from the remembered spawn protection; it still binds the other hive accounts", account);
        }
        catch (Exception ex) { log?.LogWarning(ex, "forgetting spawn protection failed"); }
    }

    /// <summary>Stall watchdog: a job/script/order that claims to work while the body hasn't changed for 45 s wakes the brain.</summary>
    public Brainstem.StallWatchdog? Watchdog { get; init; }

    /// <summary>The recorded pair whose end in <paramref name="dim"/> is where the bot came out (within 4 blocks), as text.</summary>
    public static string? PortalPairText(MineClaw.Agent.Rag.PortalLinks links, int dim, MineClaw.World.Vec3d pos)
    {
        foreach (var l in links.All())
            if (l.From(dim) is { } p && p.From.Distance(pos.X, pos.Y, pos.Z) <= 4)
                return $"portal pair recorded: {MineClaw.Agent.Rag.WorldDim.Name(p.To.Dim)} {p.To.Pos} <-> {MineClaw.Agent.Rag.WorldDim.Name(p.From.Dim)} {p.From.Pos}";
        return null;
    }

    /// <summary>
    /// Self-reliant prerequisite planning (ensure, loadouts, skill preconditions, mine/harvest): what the acquisition planner
    /// may draw on. Asking players only while a human is online and this bot may speak; the hive fills
    /// <see cref="AcquisitionSources.RequestHandoff"/> on <see cref="Acquisition"/>.
    /// </summary>
    /// <summary>Purges taught dig cells the loaded world shows are lava/water/other blocks (logged), a few times after startup.</summary>
    private static async Task PurgeNonCellsAsync(IGameClient client, Perception.DigCells cells, Perception.DigCellCorrections corrections,
        TimeProvider time, ILogger? log)
    {
        for (int i = 0; i < 15; i++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), time).ConfigureAwait(false);
                if (!client.IsPlaying) continue;
                var w = client.World;
                foreach (var (cell, why) in cells.PurgeNonCells(p => w.IsLoaded(p.X, p.Z) ? (w.GetBlock(p), w.GetMeta(p)) : null,
                             (p, why) => corrections.RetractBad(p, why)))
                    log?.LogInformation("dig cell {Cell} purged: {Why}", cell, why);
            }
            catch (Exception ex) { log?.LogDebug("dig cell world purge skipped: {Err}", ex.Message); }
        }
    }

    private static AcquisitionSources AttachAcquisition(IGameClient client, AgentRuntime rt,
        Func<MineClaw.World.BlockPos, IReadOnlyList<MineClaw.World.BlockPos>> genCells, Perception.DigCells digCells, WorldIndexer? indexer, ILogger? log) =>
        GameTools.Acquisition.Attach(client, new AcquisitionSources
        {
            Observations = rt.Observations,
            CellsNear = genCells,
            KnownCell = digCells.IsDigCell,
            ConfirmedCell = p => indexer?.IsConfirmedDigCell(p) == true,
            GeneratorCells = () =>
            {
                var me = client.World.Player.Position.ToBlockPos();
                return // Geometry (lava + water beside it) is a generator only on skyblock; a normal world knows its gens by regen/teaching.
                    (MineClaw.Data.Versioning.WorldMode.Current.GeneratorStone ? Perception.BlockDescriber.GenCellsNear(client.World, me, 16) : [])
                    .Concat(indexer?.GeneratorCellsNear(me, 32) ?? [])
                    .Concat(digCells.Taught().Where(t => t.DistanceSquaredTo(me) <= 48 * 48))
                    .Distinct().ToList();
            },
            MinTrees = client is MinecraftGameClient mg ? mg.Inner.MinTrees : MineClaw.Client.Trees.TreeStewardship.DefaultMinTrees,
            Announce = plan => log?.LogInformation("acquire: {Plan}", plan),
            AskPlayer = async (question, timeout, ct) =>
            {
                if (rt.Agent.HumansOnline?.Invoke() != true) return null;
                if (rt.Agent.SpeechGate is { } gate && gate(new Agent.Events.EventBatch([]), question, false) == null) return null; // a muted clone
                // Another hive bot asked the players this a few minutes ago: no second ask (its answer is in chat already).
                if (Agent.Human.AskedBefore.For(rt.Db, rt.Chat, TimeProvider.System)(question, null) is { } prior)
                {
                    log?.LogInformation("acquire: not asking again: {Prior}", prior);
                    return null;
                }
                var wait = rt.Chat.WaitForReplyAsync(null, timeout, ct);
                await rt.Chat.SayAsync(question, ct: ct).ConfigureAwait(false);
                return await wait.ConfigureAwait(false);
            },
        });

    /// <summary>
    /// Spawn protection learned by the client (logs that won't break near spawn) → one <c>protected_region</c> observation
    /// per dimension, so memory/where_is/the worker know about it; cleared when the client clears it.
    /// </summary>
    /// <remarks>Public for in-process bodies (each body's client feeds the same shared fact under its own account).</remarks>
    public static void WireProtectedRegion(MineClaw.Client.MinecraftClient c, AgentRuntime rt, ILogger? log,
        IClaimBoard? claims = null, Func<IEnumerable<string>>? hiveBots = null)
    {
        var account = c.Options.Username;
        // One body per work target (chop_tree's tree leases) on the same board as the chest/block leases.
        if (claims != null) c.TargetClaims = claims;
        // An undone edit on a block another hive bot holds a lease on, or right next to another hive bot, is a race with it
        // (two bots on one tree crown), never evidence of spawn protection.
        if (claims != null || hiveBots != null)
            c.EditContested = p => IsContested(c, p, claims, hiveBots);
        // Any hive account's successful edit inside disproves this inferred region for everyone, including its saved notes.
        c.EditSucceededNearSpawn += (p, dim) =>
        {
            try { NoteEditWorked(rt, account, p, dim, c.World.WorldHeight, log); }
            catch (Exception ex) { log?.LogWarning(ex, "protected_region update failed"); }
        };
        c.ProtectedRegionChanged += r =>
        {
            try
            {
                if (r == null || c.SpawnProtectionDisabled) return; // successes update the fact through EditSucceededNearSpawn
                var fact = SpawnProtectionFact.Learned(SpawnProtectionFact.Read(rt.Observations, r.Dimension), r, account);
                fact.Write(rt.Observations, c.World.WorldHeight);
                log?.LogInformation("spawn protection fact: {Text}", fact.Text());
            }
            catch (Exception ex) { log?.LogWarning(ex, "protected_region observation failed"); }
        };
    }

    /// <summary><paramref name="account"/>'s edit at <paramref name="p"/> worked: the shared fact learns from it (see <see cref="SpawnProtectionFact.AfterSuccess"/>).</summary>
    internal static void NoteEditWorked(AgentRuntime rt, string account, MineClaw.World.BlockPos p, int dim, int worldHeight, ILogger? log)
    {
        if (SpawnProtectionFact.Read(rt.Observations, dim) is not { } fact) return;
        var (next, changed) = fact.AfterSuccess(account, p, dim);
        if (!changed) return;
        if (next == null)
        {
            SpawnProtectionFact.Retract(rt.Observations, dim);
            int n = RetractSpawnProtectionNotes(rt);
            log?.LogInformation("edit by {Account} at {Pos} worked inside the remembered spawn protection, disproved it for the hive: retracted ({Notes} note(s))",
                account, p, n);
            return;
        }
        next.Write(rt.Observations, worldHeight);
        log?.LogInformation("spawn protection fact updated after edit by {Account} at {Pos}", account, p);
    }

    /// <summary>
    /// Remembered world + scenery ledger: unknown builds already announced/asked (or there, unchanged, last time) stay quiet
    /// across restarts; real changes while away become one "since last time" activity line.
    /// </summary>
    private static WorldMemory? StartWorldMemory(IGameClient client, AgentRuntime rt, WorldIndexer? indexer, WorldIndexOptions? o,
        TimeProvider? time, ILoggerFactory? loggers)
    {
        var opts = (o ?? new WorldIndexOptions()).Remembered;
        if (indexer == null || !opts.Enabled) return null;
        var store = new SqliteWorldMemoryStore(rt.Db, client.Username); // seen_by: whose eyes (main or a clone) last saw a column
        var wm = new WorldMemory(client.World, store, opts, time, loggers?.CreateLogger("MineClaw.WorldMemory"));
        WorldMemory.GateAnnouncements(indexer, wm, new SceneryLedger(store, time));
        wm.SinceLastTime += line => rt.Events.Post(Agent.Events.GameEvent.Activity(line)); // clones drop Activity (main's job)
        wm.Start();
        return wm;
    }

    /// <summary>Another hive bot holds a lease on <paramref name="p"/>, or stands within 4 blocks of it.</summary>
    internal static bool IsContested(MineClaw.Client.MinecraftClient c, MineClaw.World.BlockPos p, IClaimBoard? claims, Func<IEnumerable<string>>? hiveBots)
    {
        if (claims != null && !claims.IsFree(ClaimKey.Block(p, c.World.Dimension))) return true;
        foreach (var name in hiveBots?.Invoke() ?? [])
        {
            if (string.IsNullOrEmpty(name) || name.Equals(c.Options.Username, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.World.Entities.FindPlayer(name) is { } e
                && Math.Abs(e.Position.X - (p.X + 0.5)) <= 4.5 && Math.Abs(e.Position.Z - (p.Z + 0.5)) <= 4.5 && Math.Abs(e.Position.Y - p.Y) <= 6)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The client's learned region vs the shared protected_region fact: gone from the db (a success in another process retracted
    /// it) → forget it here too; present but not held here (learned by another hive process) → adopt it, keeping its time.
    /// </summary>
    public static void SyncProtectedRegion(MineClaw.Client.MinecraftClient c, AgentRuntime rt, ILogger? log)
    {
        try
        {
            if (c.SpawnProtectionDisabled)
            {
                c.ForgetProtectedRegion("spawn protection is disabled by the owner");
                SpawnProtectionFact.Retract(rt.Observations, c.World.Dimension);
                return;
            }
            var account = c.Options.Username;
            var fact = SpawnProtectionFact.Read(rt.Observations, c.World.Dimension);
            if (c.ProtectedRegion is { } mine)
            {
                // Grace: my own fresh region may not be written yet (the event dispatcher upserts it).
                if (fact == null && DateTime.UtcNow - mine.LearnedAt > TimeSpan.FromSeconds(30) && c.ForgetProtectedRegion("retracted in the shared memory"))
                    log?.LogInformation("spawn protection retracted elsewhere in the hive: forgot {Region}", mine);
                else if (fact != null && !fact.AppliesTo(account) && c.ForgetProtectedRegion("this account is exempt (op)"))
                    log?.LogInformation("spawn protection doesn't bind {Account} (exempt): forgot {Region}", account, mine);
                return;
            }
            if (fact == null || !fact.AppliesTo(account)) return;
            var row = rt.Observations.Get(SpawnProtectionFact.Kind, SpawnProtectionFact.Key, dimension: c.World.Dimension);
            var center = fact.Center with { Y = fact.Center.Y != 0 ? fact.Center.Y : c.World.Spawn.Y };
            var r = (fact with { Center = center }).ToRegion(2, row?.LastSeen.UtcDateTime ?? DateTime.UtcNow);
            if (c.AdoptProtectedRegion(r)) log?.LogInformation("adopted the hive's spawn protection fact: {Region}", r);
        }
        catch (Exception ex) { log?.LogDebug(ex, "protected_region sync failed"); }
    }

    /// <summary>Forgets notes claiming spawn protection blocks the bots (count removed).</summary>
    internal static int RetractSpawnProtectionNotes(AgentRuntime rt)
    {
        int n = 0;
        foreach (var phrase in new[] { "spawn protection", "spawn-protection", "spawn protected" }) n += rt.Memory.Forget("note", phrase);
        return n;
    }

    /// <summary>Mechanism learning (cause → effect from block changes), persisted links loaded into the world's graph,
    /// demonstrations and "what opens this?" answers from chat. The client's goto plans with the graph.</summary>
    private static MechanismWatcher StartMechanisms(IGameClient client, AgentRuntime rt, TimeProvider? time, ILoggerFactory? loggers)
    {
        var log = loggers?.CreateLogger("MineClaw.Mechanisms");
        var w = new MechanismWatcher(client.World, rt.Observations, time, log, client.Username);
        try { w.Load(); }
        catch (Exception ex) { log?.LogWarning(ex, "loading mechanisms failed"); }
        w.Attach(client).Start();
        rt.Chat.Accepted += m => w.NoteChat(m.Player, m.Text);
        w.Learned += o =>
        {
            if (o.IsNew || o.Demonstrated)
                rt.Events.Post(Agent.Events.GameEvent.Notice("learned mechanism: " + o.Link.Describe(), Agent.Events.EventPriority.Background));
        };
        return w;
    }

    /// <summary>World indexer + activity tracker (background passes; never on the network thread, never the LLM).</summary>
    private static (WorldIndexer?, PlayerActivityTracker?) StartWorldIndex(IGameClient client, AgentRuntime rt, MemoryStoreAdapter memory,
        WorldIndexOptions? o, ActivityOptions? a, TimeProvider? time, ILoggerFactory? loggers, Func<IEnumerable<string>>? hiveBots)
    {
        o ??= new WorldIndexOptions();
        a ??= new ActivityOptions();
        if (!o.Enabled) return (null, null);
        IWorldIndexFeed feed = client is MinecraftGameClient mg ? new ClientIndexFeed(mg.Inner) : new ManualIndexFeed();
        IDimensionSource dims = client is MinecraftGameClient mgd ? new LiveDimensionSource(mgd.Inner) : new WorldDimensionSource(client.World);
        var indexer = new WorldIndexer(client.World, new ObservationStoreSink(rt.Observations), feed, o, client.Username, time,
            loggers?.CreateLogger("MineClaw.WorldIndex"), dims) { IsKnownArea = memory.IsKnownArea };
        indexer.Start();
        if (!a.Enabled) return (indexer, null);
        var tracker = new PlayerActivityTracker(client.World, new GameClientPlayerEvents(client), new ActivityLog(rt.Db, rt.Rag, a, time),
            indexer, a, client.Username,
            botNames: () => new[] { client.Username, rt.Chat.DisplayName }.Concat(rt.Chat.Aliases).Concat(hiveBots?.Invoke() ?? []),
            botBusy: () => client.CurrentAction != null, time: time, logger: loggers?.CreateLogger("MineClaw.Activity"), dimensions: dims)
        { TaughtStructureAt = memory.TaughtStructureAt };
        tracker.Danger.IsOtherBot = rt.Chat.IsOtherBot;
        tracker.Wake += e => rt.Events.Post(e);
        var lastEvidence = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        tracker.StructureEvidence += (name, r) =>
        {
            lock (lastEvidence)
            {
                if (lastEvidence.TryGetValue(name, out var t) && r.End - t < TimeSpan.FromHours(1)) return;
                lastEvidence[name] = r.End;
            }
            rt.Memory.AddNote(name, "seen: " + r.Text);
        };
        rt.Agent.TurnDigest = since => tracker.Digest.Since(since, rt.Options.TurnDigestMaxChars, prefix: "");
        ActivityTools.Register(rt.Tools, tracker.Log!, time);
        if (!rt.Options.AskMemory.Tools.Contains(ActivityTools.ToolName)) rt.Options.AskMemory.Tools = [.. rt.Options.AskMemory.Tools, ActivityTools.ToolName];
        tracker.Start();
        return (indexer, tracker);
    }

    /// <summary>A toss within this many blocks of the bot counts as a hand-over to it.</summary>
    public const double HandoverReach = 10;

    /// <summary>Hand-overs and spoken promises (zero tokens): "Fox gave you 21 wheat; you said you'd make bread when ...".</summary>
    public Brainstem.HandoverWatch? Handovers { get; init; }
    private System.Threading.Timer? HandoverPump { get; init; }

    /// <summary>Posts settled hand-overs / kept promises as wake events (the timer does this live; tests call it by hand).</summary>
    public int PollHandovers() => Handovers is { } h ? BodyFactory.PostHandovers(h, Runtime, Client) : 0;

    public void Dispose()
    {
        Brain?.Recovery?.Dispose();
        HandoverPump?.Dispose();
        ProtectionSync?.Dispose();
        PlacedBlocks?.Dispose();
        Watchdog?.Dispose();
        Bridge.Dispose();
        Brainstem?.Dispose();
        Gestures?.Dispose();
        Activity?.Dispose();
        Mechanisms?.Dispose();
        WorldMemory?.Dispose(); // final flush: the next join compares against exactly this
        Indexer?.Dispose();
    }
}
