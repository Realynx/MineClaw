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
        AttachCloneAcquisition(brain, body);
        AttachCloneJobs(brain, body);
        AttachCloneBehaviour(brain, body);
        AttachClonePerception(brain, body, client, flusher);
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
            return Perception.BlockDescriber.GenCellsNear(world, me, 16)
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
        // Its own lane (step 4 adds crews): runs a saved script on this body; the script tool itself stays main's.
        var scripts = new ScriptBridge(rt, body.Adapter, brain.Skills, brain.BotOptions.Scripts ?? new MineClaw.Scripting.ScriptOptions(),
            time ?? TimeProvider.System, brain.Log);
        scripts.Controller.StepScope = scope;
        body.ScriptBridge = scripts;
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
