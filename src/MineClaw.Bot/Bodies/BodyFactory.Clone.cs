using MineClaw.Agent.Events;
using MineClaw.Agent.Human;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.WorldIndex;
using MineClaw.Client;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Bodies;

/// <summary>
/// Step 2 of one brain, many bodies: a clone body in main's process (<c>Bodies:Mode=inproc</c>). Its own connection, activity
/// slot, standing orders, script lane, stall watchdog, hand-over watch and hunger latch; a contributor world indexer and a
/// remembered world pumped by the one shared flusher; the shared protection, claims (its own view) and placed-block store.
/// No brainstem or chat of its own: only main's account speaks, and only whispers to the clone reach the brain.
/// </summary>
public static partial class BodyFactory
{
    /// <summary>Builds and wires a clone body over <paramref name="client"/> (not connected yet: the registry's runner logs it in).</summary>
    /// <param name="claims">This body's view of the shared claim board (its login is the owner), or null without a board.</param>
    /// <param name="flusher">The shared world-memory flusher (null = the body's memory runs its own loop).</param>
    public static Body CreateClone(BrainContext brain, MinecraftClient client, IClaimBoard? claims = null, WorldMemoryFlusher? flusher = null)
    {
        var rt = brain.Runtime;
        var time = brain.Time;
        var name = client.Options.Username;
        // Only main's account chats: a clone's adapter never sends a line (Lua say, acks and replies all go through main).
        var adapter = new MinecraftGameClient(client) { OutgoingFilter = _ => false };
        IGameClient game = claims == null ? adapter : new ClaimAwareGameClient(adapter, claims);
        var activities = new ActivityManager(rt.Tools, rt.Events, time)
        {
            Shutdown = rt.Activities.Shutdown,
            QueueBehindMoves = rt.Activities.QueueBehindMoves,
        };
        var orders = new StandingOrders(rt.Memory, rt.Tools, activities, rt.Events, name, rt.Options.Idle, time,
            brain.Loggers?.CreateLogger("MineClaw.Orders"));
        var body = new Body(name, isMain: false, adapter, game, activities, orders);
        activities.Body = body.Short;
        rt.Agent.Guard.Watch(activities, name); // its failures are its own (keyed by body)
        body.Latency = new LatencyTracker(time) { Bot = name };

        ProtectClone(brain, adapter, client, claims);
        // Spawn protection is learned per account into the one shared fact, and adopted by every body it binds (not op).
        BotHost.WireProtectedRegion(client, rt, brain.Log, claims, brain.HiveBots);
        if (brain.Persist)
            body.Owned.Add(new System.Threading.Timer(_ => BotHost.SyncProtectedRegion(client, rt, brain.Log), null,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        else BotHost.SyncProtectedRegion(client, rt, brain.Log);
        AttachCloneAcquisition(brain, body);
        AttachCloneJobs(brain, body);
        AttachCloneBehaviour(brain, body);
        AttachClonePerception(brain, body, client, flusher);
        AttachCloneReflexes(brain, body, client);
        client.HoldingOverVoid += p => brain.Bodies.OnVoidHold(body, p);
        client.VoidHoldEnded += p => brain.Bodies.OnVoidHoldEnded(body, p);
        return body;
    }

    /// <summary>The shared protection policy on the clone's client (same rules as main: one Protection, one DigCells, one set of marks).</summary>
    private static void ProtectClone(BrainContext brain, MinecraftGameClient adapter, MinecraftClient c, IClaimBoard? claims)
    {
        var protection = brain.Protection;
        adapter.IsProtected = protection.IsProtected;
        c.ExplainProtection = protection.Explain;
        c.SoftOverride = protection.OverridesSoft;
        if (brain.ProtectionTelemetry is { } telemetry) c.CountRefusal = r => telemetry.Count(r);
        if (brain.ProtectionMarks is { } marks) c.NeverBreak = p => marks.HasNever && marks.NeverAt(p, c.World.Dimension) != null;
        var digCells = brain.DigCells;
        var indexer = brain.Indexer;
        c.MineSource = (p, _) => digCells.IsDigCell(p) || indexer?.IsConfirmedDigCell(p) == true;
        // An undone edit next to another body (two bodies on one tree crown) is a race, never spawn protection.
        if (claims != null || brain.HiveBots != null) c.EditContested = p => BotHost.IsContested(c, p, claims, brain.HiveBots);
        adapter.ReservedItems = new HashSet<int>(); // a clone keeps nothing back: everything goes into storage before logout
    }

    /// <summary>
    /// The body's own acquisition sources on its adapter (where <see cref="Acquisition.Of"/> looks for the body in scope), so
    /// ensure/acquire/craft on this body see the shared chests and search the gen around it, exactly like main.
    /// </summary>
    internal static AcquisitionSources AttachCloneAcquisition(BrainContext brain, Body body)
    {
        var indexer = brain.Indexer;
        var digCells = brain.DigCells;
        var world = body.Adapter.World;
        var sources = brain.Acquisition.ForBody(() =>
        {
            var me = world.Player.Position.ToBlockPos();
            return (MineClaw.Data.Versioning.WorldMode.Current.GeneratorStone ? Perception.BlockDescriber.GenCellsNear(world, me, 16) : [])
                .Concat(indexer?.GeneratorCellsNear(me, 32) ?? [])
                .Concat(digCells.Taught().Where(t => t.DistanceSquaredTo(me) <= 48 * 48))
                .Distinct().ToList();
        });
        return Acquisition.Attach(body.Adapter, sources);
    }

    /// <summary>Scope pushes, the clone bridge (events tagged with the body, whispers only), its hunger latch and script lane.</summary>
    private static void AttachCloneJobs(BrainContext brain, Body body)
    {
        var rt = brain.Runtime;
        var time = brain.Time;
        Func<IDisposable?> scope = () => BodyScope.Push(body);
        body.Activities.StepScope = scope;
        var bridge = new GameBridge(body.Adapter, rt, brain.Log, time)
        {
            Body = body.Short,
            Hunger = new HungerLatch(brain.Persist ? Path.Combine(brain.AgentOptions.DataDirectory, $"hunger-latch-{body.Name}.json") : null, time),
            PortalPairText = (dim, pos) => BotHost.PortalPairText(rt.Observations.Portals, dim, pos),
        };
        bridge.Attach();
        body.Bridge = bridge;
        // Its own lane (step 4 adds crews): the script tool routes calls for this body here (BodyScope). A brain-run script
        // holds THIS body's slot; its interrupts are this body's dangers and chat addressed to it (a plain trusted stop too).
        var scripts = new ScriptBridge(rt, body.Adapter, brain.Skills, brain.BotOptions.Scripts ?? new MineClaw.Scripting.ScriptOptions(),
            time ?? TimeProvider.System, brain.Log)
        {
            Body = body.Short,
            Activities = body.Activities,
            Addressed = text => brain.Directory?.Addressed(text) ?? [],
        };
        scripts.Controller.StepScope = scope;
        ScriptTools.AttachLaneGate(rt, scripts);
        body.ScriptBridge = scripts;
    }

    /// <summary>
    /// The clone's brainstem and reports, at parity with a hive clone process (step 3a): survival (auto-eat, low-health retreat),
    /// EdgeGuard (void/lava) and reflex defence already run inside its MinecraftClient; here its fight/chop/void-step reports
    /// reach the one event queue tagged with the body, its inventory ledger records where items went, and a brainstem of its
    /// own runs chat reflexes ("67 stop", "67 come here"), the speculative first step and social idle for lines addressed to
    /// it (main's account still does all the talking).
    /// </summary>
    private static void AttachCloneReflexes(BrainContext brain, Body body, MinecraftClient client)
    {
        var rt = brain.Runtime;
        var time = brain.Time;
        CombatTools.AttachEvents(client, rt.Events, body.Short);
        TreeTools.AttachReports(client, brain.Indexer, rt.Events, body.Short);
        IntentionalReset.Attach(rt, body.Adapter, brain.Log, body.Short);
        var ledger = Ledger.InventoryLedgerTools.Create(client, brain.Log, time, body.Short);
        body.Ledger = ledger;
        if (ledger.Pump is { } pump) body.Owned.Add(pump);

        body.Here = new Brainstem.HereBlock(body.Adapter, rt.Memory, rt.Observations, brain.Landmark);
        var shortName = body.Short;
        var brainstem = new Brainstem.BrainstemController(rt, body.Adapter, brain.Memory, brain.IsBot, time,
            brain.Loggers?.CreateLogger("MineClaw.Brainstem"), body.Activities)
        {
            Body = body,
            NameWords = () => [shortName, body.Name, "maid chan " + shortName, "maid " + shortName, "clone " + shortName, "#" + shortName],
            Holder = () => body.ScriptBridge?.BodyState(),
            Lane = () => body.ScriptBridge,
            BodyLatency = body.Latency,
            BodyLabel = shortName,
            // Only lines for this body: a whisper to its account, or chat naming it (BodyAddressing); never main's lines.
            Hears = e => body.Adapter.IsPlaying && (GameEvent.BodyTag(e.Body) is { } to
                ? to.Equals(shortName, StringComparison.OrdinalIgnoreCase)
                : brain.Directory?.Addressed(e.Text).Contains(shortName, StringComparer.OrdinalIgnoreCase) == true),
        };
        brainstem.Attach(gestures: null, timer: brain.Persist);
        body.Brainstem = brainstem;
        body.Owned.Add(brainstem);
    }

    /// <summary>Stall watchdog, the standing-order work probe and idle resume, and the hand-over watch (item tosses to it).</summary>
    private static void AttachCloneBehaviour(BrainContext brain, Body body)
    {
        var rt = brain.Runtime;
        var time = brain.Time;
        var watchdog = AttachWatchdog(rt, body, time);
        if (brain.Persist) watchdog.Start();
        body.Watchdog = watchdog;
        if (body.Orders is { } orders)
        {
            orders.WorkReady = o => MineClaw.Bot.Brainstem.WorkProbe.Ready(o, body.Adapter, brain.GenCells);
            // Main's agent resumes main's order after its turns; a clone's order resumes from here when the body is idle.
            if (brain.Persist)
                body.Owned.Add(new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (!orders.Held && body.Adapter.IsPlaying && body.Scripts?.Current == null) orders.ResumeIfIdle("idle", rt.Activities.Shutdown);
                    }
                    catch (Exception ex) { brain.Log?.LogDebug(ex, "body {Body}: standing order resume failed", body.Name); }
                }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20)));
        }
        var (handovers, pump) = AttachHandovers(body.Adapter, rt, brain.IsBot, time, brain.Log, timer: brain.Persist, body: body.Short);
        body.Handovers = handovers;
        body.HandoverPump = pump;
    }

    /// <summary>
    /// The clone's eyes for the shared world: a contributor indexer (chests it opens, single block changes, gen regen it sees,
    /// forwarded to main's indexer), its remembered world (chunks into the shared db, seen_by = its name), its placements into
    /// the shared placed-block store, and the chest "gone since memory" notes.
    /// </summary>
    private static void AttachClonePerception(BrainContext brain, Body body, MinecraftClient client, WorldMemoryFlusher? flusher)
    {
        var rt = brain.Runtime;
        var time = brain.Time;
        brain.PlacedBlocks?.Watch(client.World.Placed);
        if (brain.Indexer is not { } mainIndex) return;
        var indexer = new WorldIndexer(client.World, new ObservationStoreSink(rt.Observations), new ClientIndexFeed(client),
            new WorldIndexOptions { Contributor = true }, body.Name, time, brain.Loggers?.CreateLogger("MineClaw.WorldIndex"), new LiveDimensionSource(client))
        {
            IsKnownArea = brain.Memory.IsKnownArea,
        };
        indexer.RegenConfirmed += mainIndex.AdoptRegen;
        indexer.Start();
        body.Indexer = indexer;
        ChestChanges.AttachVanished(rt, body.Adapter, indexer, brain.IsBot, time, brain.Log, body.Short);
        if (brain.WorldMemory is not { } mainMemory) return;
        var memory = new WorldMemory(client.World, new SqliteWorldMemoryStore(rt.Db, body.Name), mainMemory.Options, time,
            brain.Loggers?.CreateLogger("MineClaw.WorldMemory"));
        if (flusher != null) flusher.Add(memory);
        else memory.Start();
        body.WorldMemory = memory;
    }

    /// <summary>Tears a clone body's wiring down (process end): timers, the bridge, the indexer, a final world-memory flush.</summary>
    public static void DisposeClone(Body body, WorldMemoryFlusher? flusher = null)
    {
        foreach (var d in body.Owned) { try { d.Dispose(); } catch (Exception) { } }
        body.Owned.Clear();
        try { body.HandoverPump?.Dispose(); } catch (Exception) { }
        try { body.Watchdog?.Dispose(); } catch (Exception) { }
        try { body.Bridge?.Dispose(); } catch (Exception) { }
        try { body.Scripts?.Dispose(); } catch (Exception) { }
        if (body.WorldMemory is { } wm)
        {
            flusher?.Remove(wm, flush: false);
            try { wm.Dispose(); } catch (Exception) { }
        }
        try { body.Indexer?.Dispose(); } catch (Exception) { }
    }
}
