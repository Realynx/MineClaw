using MineClaw.Agent;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Bodies;

/// <summary>
/// Wires one body under the brain (one brain, many bodies, step 1): its activity slot's <see cref="BodyScope"/> pushes, the
/// event bridge and hunger latch, the script lane, the brainstem, body-language gestures, the stall watchdog, hand-over
/// watch and standing-order work probe. Two phases because the brain's last tools (see, do) go between them for the main
/// body: <see cref="AttachJobs"/>, then <see cref="BotHost.FinishTools"/>, then <see cref="AttachBehaviour"/>.
/// </summary>
public static partial class BodyFactory
{
    /// <summary>Both phases for a body attached after the brain's tools exist.</summary>
    public static Body Attach(BrainContext brain, Body body)
    {
        AttachJobs(brain, body);
        BotHost.FinishTools(brain);
        AttachBehaviour(brain, body);
        return body;
    }

    /// <summary>
    /// Phase 1: scope pushes on the body's ActivityManager (and the worker and script lane that drive it), the event bridge
    /// (chat in, dangers, the state header) with its hunger latch, and the script lane (the script tool, after the brain split
    /// and the header so tiers and the "[body: script]" tag stick).
    /// </summary>
    public static void AttachJobs(BrainContext brain, Body body)
    {
        RequireMain(body);
        var rt = brain.Runtime;
        var client = body.Adapter;
        var time = brain.Time;
        var log = brain.Log;
        Func<IDisposable?> scope = () => BodyScope.Push(body);
        body.Activities.StepScope = scope;
        AttachTemporaryCleanup(body);
        body.Latency ??= rt.Latency; // main's chat latency is the runtime's tracker
        // Step 1-2: the one worker drives main; a delegate run on a clone body's slot (that slot pushed the clone) keeps that
        // clone (a pool per body comes in step 3).
        if (rt.Worker != null) rt.Worker.StepScope = () => BodyScope.Current is { IsMain: false } ? null : scope();

        var bridge = new GameBridge(client, rt, log, time)
        {
            Hunger = new HungerLatch(brain.Persist ? Path.Combine(brain.AgentOptions.DataDirectory, $"hunger-latch-{client.Username}.json") : null, time),
            PortalPairText = (dim, pos) => BotHost.PortalPairText(rt.Observations.Portals, dim, pos),
        };
        bridge.Attach();
        body.Bridge = bridge;
        brain.Recovery ??= new DeathRecovery(brain.Bodies, rt.Events, time);
        brain.Recovery.Watch(body);

        var scripts = ScriptTools.Register(rt, client, brain.Skills, brain.BotOptions.Scripts, time, log); // script tool (brain+worker), after split + header
        scripts.Controller.StepScope = scope;
        body.ScriptBridge = scripts;
        scripts.Controller.Host.SafetyBusy = () => client.SafetyEngaged || brain.Recovery?.IsHolding(body) == true;
    }

    /// <summary>
    /// Phase 2, last: the [here] pre-observation, the brainstem (attached last so its event gate sees what the script gate
    /// kept), body-language gestures, the stall watchdog, the standing-order work probe and the hand-over watch.
    /// </summary>
    public static void AttachBehaviour(BrainContext brain, Body body)
    {
        RequireMain(body);
        var rt = brain.Runtime;
        var client = body.Adapter;
        var time = brain.Time;
        var log = brain.Log;
        var loggers = brain.Loggers;

        // Brainstem: deterministic controller under the brain (head turn, reflexes, speculative first step, social idle,
        // the [here] pre-observation). Attached last so its event gate sees what the script gate kept.
        var here = new Brainstem.HereBlock(client, rt.Memory, rt.Observations, brain.Landmark);
        rt.Agent.HereBlock = here.Build; // the focus body's [here] (main is the focus in step 1)
        var brainstem = new Brainstem.BrainstemController(rt, client, brain.Memory, brain.IsBot, time, loggers?.CreateLogger("MineClaw.Brainstem"))
        {
            Body = body,
        };
        brainstem.Attach(brain.Gestures, timer: brain.Persist); // in-memory (test) hosts drive SocialTick by hand
        body.Brainstem = brainstem;
        // Body language from static text matching (nod/shake/wave/bow/bob/hop/look-at; zero tokens, social priority).
        var bodyGestures = new Brainstem.Gestures(rt, client, brain.AgentOptions.Gestures, brain.IsBot, time, loggers?.CreateLogger("MineClaw.Gestures"));
        bodyGestures.Attach();
        body.Gestures = bodyGestures;
        AttachCharacter(brain, body);
        var watchdog = AttachWatchdog(rt, body, time);
        // A standing-order pass that found nothing to do backs off in C#; the world model says when there's work again.
        if (body.Orders is { } standing) standing.WorkReady = o => MineClaw.Bot.Brainstem.WorkProbe.Ready(o, client, brain.GenCells);
        if (brain.Persist) watchdog.Start(); // in-memory (test) hosts drive Tick by hand
        body.Watchdog = watchdog;
        var (handovers, pump) = AttachHandovers(client, rt, brain.IsBot, time, log, timer: brain.Persist);
        body.Handovers = handovers;
        body.HandoverPump = pump;
    }

    /// <summary>
    /// These phases wire the main body only: the bridge sets the brain's state header, the script lane registers the script
    /// tool and the brainstem sets [here]. In-process clone bodies get their own variants (<see cref="CreateClone"/>).
    /// </summary>
    private static void RequireMain(Body body)
    {
        if (!body.IsMain) throw new NotSupportedException($"body {body.Name}: a clone body is built by BodyFactory.CreateClone (Bodies:Mode=inproc)");
    }

    private static void AttachTemporaryCleanup(Body body)
    {
        if (body.Client is { } client)
            body.Activities.CleanupJob = async ct => (await client.CleanupTemporaryBlocksAsync(ct).ConfigureAwait(false)).Message;
    }

    private static readonly string[] PassiveTools = ["follow", "guard", "watch_demo", "ask_player", "ask_stand", "wait", "sit", "look", "see"];

    /// <summary>Stall watchdog: a job/script/order that claims to work while the body hasn't changed for 45 s wakes the brain.</summary>
    private static Brainstem.StallWatchdog AttachWatchdog(AgentRuntime rt, Body body, TimeProvider? time)
    {
        var client = body.Adapter;
        var activities = body.Activities;
        var scripts = body.ScriptBridge;
        string? last = null;
        activities.StepFinished += (tool, _, r, _) => last = $"{tool}: {r.Text}";
        Brainstem.ClaimedWork? Work()
        {
            if (!client.IsPlaying) return null;
            if (scripts?.Controller.Current is { } s && s.Status == MineClaw.Scripting.ScriptStatus.Running)
            {
                var state = s.State();
                bool waiting = state.Contains("wait", StringComparison.OrdinalIgnoreCase) || state.Contains("sleep", StringComparison.OrdinalIgnoreCase);
                return new($"script {s.Name}", waiting);
            }
            if (activities.Current is { } a && a.Kind is ActivityKind.Job or ActivityKind.Move)
            {
                bool passive = a.Steps.Count > 0 && a.Steps.All(st => PassiveTools.Contains(st.Tool, StringComparer.OrdinalIgnoreCase));
                return new($"{a.Label} #{a.Id}", passive);
            }
            return null;
        }
        string Snapshot() => StallSignature(client, body.Name);
        Func<string?> LastResult = () => rt.Worker?.IsRunning == true && rt.Worker.Trail is { } trail ? trail : last;
        string? tag = body.IsMain ? null : body.Short;
        if (!body.IsMain) LastResult = () => last; // the one worker's trail is main's
        return new Brainstem.StallWatchdog(Work, Snapshot, LastResult, e => rt.Events.Post(tag != null && e.Body == null ? e with { Body = tag } : e), time);
    }

    /// <summary>What the stall watchdog compares: where the body is, what it carries, what it does, and the pulse of a crew build it
    /// coordinates (<see cref="Blueprints.BuildPulse"/>: the crew placing blocks or the slicer planning while this body stands still).</summary>
    internal static string StallSignature(IGameClient client, string body)
    {
        var p = client.World.Player.Position;
        var inv = client.World.Inventory.PlayerSlots();
        int h = 17;
        foreach (var st in inv) h = h * 31 + (st.IsEmpty ? 0 : st.Id * 1000 + st.Count);
        return $"{Math.Round(p.X * 2)},{Math.Round(p.Y * 2)},{Math.Round(p.Z * 2)}|{h}|{client.CurrentAction}|{Blueprints.BuildPulse.Of(body)}";
    }

    /// <summary>Posts settled hand-overs / kept promises as wake events (the timer does this live; tests call it by hand).</summary>
    internal static int PostHandovers(Brainstem.HandoverWatch h, AgentRuntime rt, IGameClient client, string? body = null)
    {
        int n = 0;
        foreach (var (text, player) in h.Poll(id => client.World.Inventory.CountItem(id)))
        {
            // A kept promise wakes the brain even while a script has the body (it said it would act on this).
            bool promise = text.Contains("you said you'd", StringComparison.Ordinal);
            rt.Events.Post(new Agent.Events.GameEvent(Agent.Events.EventKind.Notice, text, player, Agent.Events.EventPriority.Normal) { MustWake = promise, Body = body });
            n++;
        }
        return n;
    }

    /// <summary>
    /// Live 10-27 21:04: Fox tossed 21 wheat after "come here I'll give it to you, make bread"; the bot had said "I'll make bread
    /// the second you hand it over" and then idled 40 s. Item spawns at a player's eye height / in front of them are attributed,
    /// the bot's pickups of them become one debounced event, and the bot's own "I'll X when Y" lines become pending promises.
    /// </summary>
    private static (Brainstem.HandoverWatch, System.Threading.Timer?) AttachHandovers(IGameClient client, AgentRuntime rt, Func<string, bool> isBot,
        TimeProvider? time, ILogger? log, bool timer, string? body = null)
    {
        var watch = new Brainstem.HandoverWatch(time);
        var w = client.World;
        string self = client.Username;
        IEnumerable<Brainstem.HandoverWatch.PlayerView> Others() => w.Entities.All()
            .Where(e => e.Kind == MineClaw.World.EntityKind.Player && e.Name is { Length: > 0 } n && !n.Equals(self, StringComparison.OrdinalIgnoreCase) && !isBot(n))
            .Select(e => new Brainstem.HandoverWatch.PlayerView(e.Name!, e.Position, e.Yaw, e.Pitch));
        w.Entities.Spawned += e =>
        {
            try
            {
                if (e.Kind != MineClaw.World.EntityKind.Item || e.Item.IsEmpty) return;
                // A toss near the bot is a hand-over (counted at once, debounced); one far away is someone else's business.
                bool near = e.Position.DistanceTo(w.Player.Position) <= BotHost.HandoverReach;
                if (watch.OnItemSpawn(e.Id, e.Position, Others(), near ? e.Item.Id : 0, near ? e.Item.Count : 0) is { } who)
                    log?.LogDebug("hand-over: {Item} x{Count} thrown by {Who}{Far}", e.Item.Id, e.Item.Count, who, near ? "" : " (far: counted only on pickup)");
            }
            catch (Exception ex) { log?.LogDebug(ex, "hand-over spawn failed"); }
        };
        w.BlockChanged += c => { if (c.NewId == 0 && c.OldId != 0) watch.OnBlockGone(c.Pos); };
        if (client is MinecraftGameClient mg)
            mg.Inner.ItemCollected += (collector, entity, stack) =>
            {
                if (collector != w.Player.EntityId) { watch.OnTakenByOther(entity); return; }
                if (stack is not { IsEmpty: false } s) return;
                watch.OnPickup(entity, s.Id, s.Count);
            };
        if (body == null) // main's account speaks and hears: the player lines and promises are main's
        {
            rt.Chat.Accepted += m => { if (m.Player is { Length: > 0 } p && !isBot(p)) watch.OnPlayerLine(p, m.Text); };
            rt.Chat.Said += t =>
            {
                try
                {
                    if (watch.OnBotLine(t, id => w.Inventory.CountItem(id)) is { } p)
                        log?.LogInformation("promise: \"{Intent}\" when {Cond}{Item}", p.Intent, p.Condition,
                            p.ItemId is { } id ? $" (watching for {MineClaw.Data.Items.DisplayName(id, 0)}{(p.From is { } f ? " from " + f : "")})" : "");
                }
                catch (Exception ex) { log?.LogDebug(ex, "promise parse failed"); }
            };
        }
        var pump = timer
            ? new System.Threading.Timer(_ => { try { PostHandovers(watch, rt, client, body); } catch (Exception) { /* next tick */ } }, null, 500, 250)
            : null;
        return (watch, pump);
    }
}
