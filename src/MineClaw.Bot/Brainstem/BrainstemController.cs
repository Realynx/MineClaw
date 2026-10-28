using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.Memory;
using MineClaw.Bot.WorldIndex;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Brainstem;

/// <summary>
/// The brainstem: a small deterministic behaviour controller in C# that always runs underneath the LLM brain (zero tokens).
/// <list type="bullet">
/// <item><b>Instant acknowledgement</b>: addressed chat turns the head to the speaker on the next tick (+ a nod), before
///   the debounce or any model call. No text: the persona speaks through the brain.</item>
/// <item><b>Reflexes</b>: an exact command line ("stop", "come here", "follow me", "jump", "look at me", "give me 5 cobble")
///   runs at once through the normal gated tools; the brain's turn gets "[reflex] ..." and may comment or stay silent.</item>
/// <item><b>Speculative start</b>: a clear job ("grab cobble", "chop wood", "go to the gen") starts its obvious first step
///   — walking only — while the brain plans; the brain's own move adopts it (no restart), retargets it or cancels it.</item>
/// <item><b>Social idle</b>: head-tracking the conversation partner / nearest player, glancing at newcomers, looking at
///   pointed blocks, loosely following a partner who walks off (safe ground only), a "thinking" fidget on long turns.</item>
/// </list>
/// Everything obeys the <see cref="PriorityStack"/>: safety reflexes &gt; job/script &gt; speculative step &gt; social.
/// </summary>
public sealed class BrainstemController : IDisposable
{
    private readonly AgentRuntime _rt;
    private readonly IGameClient _c;
    private readonly IPlaceResolver _places;
    private readonly Func<string, bool> _isBot;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _turnedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Player, string Text, string Note, DateTimeOffset At)> _notes = new();
    private readonly HashSet<string> _near = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _timer;
    private (string Player, DateTimeOffset At)? _partner;
    private (Vec3d Point, DateTimeOffset Until)? _lookHold;
    private DateTimeOffset _lastFidget = DateTimeOffset.MinValue;
    private int _fidgets;
    private (Intent Intent, BlockPos Target)? _spec;

    private readonly ActivityManager _acts;

    /// <param name="activities">The body's activity slot (one brain, many bodies: a clone body's own); null = the runtime's.</param>
    public BrainstemController(AgentRuntime rt, IGameClient client, IPlaceResolver places, Func<string, bool>? isBot = null,
        TimeProvider? time = null, ILogger? log = null, ActivityManager? activities = null)
    {
        _rt = rt;
        _acts = activities ?? rt.Activities;
        _c = client;
        _places = places;
        _isBot = isBot ?? (_ => false);
        _time = time ?? TimeProvider.System;
        _log = log;
        Stack = new PriorityStack(
            safety: () => _c.SafetyEngaged,
            job: () => _acts.IsBusy || HolderNow() != null
                || (_c.CurrentAction != null && _acts.Filler == null),
            speculative: () => _acts.CurrentKind == ActivityKind.Speculative,
            social: () => _acts.CurrentKind == ActivityKind.Social || _lookHold is { } h && h.Until > Now);
    }

    // ------------------------------------------------------------------ one brain, many bodies

    /// <summary>The names this body answers to (null = main's chat identity, as before). A clone: "67", its login, "clone 67".</summary>
    public Func<IEnumerable<string>>? NameWords { get; init; }

    /// <summary>
    /// Which player lines this brainstem acts on (null = every line, as before): a clone's acts only on lines addressed to it,
    /// main's skips lines addressed only to other bodies. The text is the player's line.
    /// </summary>
    public Func<GameEvent, bool>? Hears { get; set; }

    /// <summary>Who holds this body (null = the runtime's body lock, main's script).</summary>
    public Func<string?>? Holder { get; init; }

    /// <summary>This body's script lane (null = the runtime's, main's).</summary>
    public Func<GameTools.ScriptBridge?>? Lane { get; init; }

    /// <summary>This body's chat → reaction latency (null = the runtime's tracker).</summary>
    public Agent.Telemetry.LatencyTracker? BodyLatency { get; init; }

    private Agent.Telemetry.LatencyTracker Latency => BodyLatency ?? _rt.Latency;

    private string? HolderNow() => Holder != null ? Holder() : _rt.Agent.BodyHolder?.Invoke();

    private GameTools.ScriptBridge? LaneNow() => Lane != null ? Lane() : GameTools.ScriptTools.Of(_rt);

    private bool HearsLine(GameEvent e)
    {
        if (Hears == null) return true;
        try { return Hears(e); } catch (Exception) { return false; }
    }

    public BrainstemOptions Options => _rt.Options.Brainstem;
    public SocialOptions Social => Options.Social;
    public PriorityStack Stack { get; }
    private DateTimeOffset Now => _time.GetUtcNow();
    private IEnumerable<string> Names => NameWords?.Invoke() ?? [_rt.Chat.Username, _rt.Chat.DisplayName, .. _rt.Chat.Aliases];

    /// <summary>The speculative step's target, while one runs (tests/debug).</summary>
    public BlockPos? SpeculativeTarget { get { lock (_lock) return _acts.CurrentKind == ActivityKind.Speculative ? _spec?.Target : null; } }

    /// <summary>Wires chat, the event gate, the speculation policy, the turn hint, gestures and the social timer.</summary>
    public void Attach(Perception.GestureTracker? gestures = null, bool timer = true)
    {
        if (!Options.Enabled) return;
        _rt.Chat.Accepted += m =>
        {
            if (m.Player is { Length: > 0 } p && HearsLine(GameEvent.Chat(p, m.Text))) OnAddressed(p);
        };
        var prevGate = _rt.Events.Gate;
        _rt.Events.Gate = e => (prevGate == null ? e : prevGate(e)) is { } kept ? OnEvent(kept) : null;
        _acts.SpeculationPolicy = Judge;
        var hint = _rt.Agent.TurnHint;
        _rt.Agent.TurnHint = b =>
        {
            var h = hint?.Invoke(b);
            var n = TakeNotes(b);
            return string.IsNullOrWhiteSpace(h) ? n : n == null ? h : h + "\n" + n;
        };
        if (gestures != null) gestures.Pointed += g => OnPointed(g.Player, g.Pos);
        if (timer && Social.Enabled)
        {
            var period = TimeSpan.FromMilliseconds(Math.Clamp(Social.TickMs, 50, 5000));
            _timer = _time.CreateTimer(_ => { try { SocialTick(); } catch (Exception ex) { _log?.LogDebug(ex, "social tick failed"); } }, null, period, period);
        }
    }

    public void Dispose() => _timer?.Dispose();

    // ------------------------------------------------------------------ 1. instant acknowledgement

    /// <summary>Addressed chat: head to the speaker now (next tick), a nod; the conversation partner is remembered.</summary>
    public bool OnAddressed(string player)
    {
        if (!Options.Enabled || _isBot(player) || player.Equals(_c.Username, StringComparison.OrdinalIgnoreCase)) return false;
        lock (_lock) _partner = (player, Now);
        if (!Options.HeadTurn || _c.World.Entities.FindPlayer(player) is not { } e) return false;
        if (!_c.FaceIdle(Eye(e.Position))) return false;
        if (Options.Nod) _c.SwingIdle();
        lock (_lock) { _turnedAt[player] = Now; _lookHold = (Eye(e.Position), Now + TimeSpan.FromSeconds(2)); }
        return true;
    }

    // ------------------------------------------------------------------ 2. reflexes + speculative start (event gate)

    /// <summary>The event gate: a typed line may run a reflex or start a speculative walk; the event itself passes on.</summary>
    internal GameEvent? OnEvent(GameEvent e)
    {
        if (!Options.Enabled || !EventQueue.IsTyped(e) || e.Kind is not (EventKind.Chat or EventKind.Whisper) || e.Player is not { } who) return e;
        if (_isBot(who) || who.Equals(_c.Username, StringComparison.OrdinalIgnoreCase)) return e;
        if (!HearsLine(e)) return e; // another body's line (one brain, many bodies)
        DateTimeOffset? turned;
        lock (_lock) turned = _turnedAt.TryGetValue(who, out var t) && Now - t < TimeSpan.FromSeconds(2) ? t : null;
        Latency.OnChat(who, e.Text, Now); // opens the measurement before the reflex runs (idempotent)
        if (turned is { } at) Latency.NoteReaction(at);
        var handled = TryReflex(who, e.Text);
        if (handled != null)
        {
            Note(who, e.Text, handled.Value.Note);
            return Options.ReflexWakesBrain ? e : e with { Wakes = false };
        }
        TrySpeculate(who, e.Text);
        return e;
    }

    /// <summary>Runs the reflex this line is (or returns null). Public for tests.</summary>
    public (Intent Intent, string Note)? TryReflex(string player, string text)
    {
        var enabled = new HashSet<string>(Options.Reflexes, StringComparer.OrdinalIgnoreCase);
        if (enabled.Count == 0) return null;
        var intent = IntentMatcher.Reflex(text, Names, enabled.Contains("give") && MayGive(player) ? HoldsItem : null);
        if (!intent.IsReflex || !enabled.Contains(intent.ReflexName)) return null;
        if (_c.SafetyEngaged && intent.Kind != IntentKind.Stop) return null; // safety owns the body: the brain hears it
        string? note = intent.Kind switch
        {
            IntentKind.Stop => Stop(),
            IntentKind.Come => Walk(player, follow: false),
            IntentKind.Follow => Walk(player, follow: true),
            IntentKind.Gesture => Gesture(player, intent.Arg!),
            IntentKind.LookAtMe => LookAt(player),
            IntentKind.Give => Give(player, intent),
            _ => null,
        };
        if (note == null) return null;
        Latency.NoteReflex(intent.ReflexName);
        _log?.LogInformation("reflex {Reflex} for {Player}: {Note}", intent.ReflexName, player, note);
        return (intent, note);
    }

    private string? Stop()
    {
        // A running script pauses itself on a trusted stop (its own gate); never kill it from here.
        if (HolderNow() != null || LaneNow()?.Controller.Current is { IsLive: true }) return null;
        if (_acts.Current is null && _c.CurrentAction == null) { _acts.Cancel("stop"); Latency.NoteAction(); return "stopped (nothing was running)"; }
        var label = _acts.Current?.Label ?? _c.CurrentAction;
        // The stop tool cancels the runtime's (main's) slot: a clone body's brainstem stops its own slot directly.
        if (!ReferenceEquals(_acts, _rt.Activities)) _acts.Cancel("stop");
        else _ = RunTool("stop", new { });
        Latency.NoteAction();
        return $"stopped {label}";
    }

    private string? Walk(string player, bool follow)
    {
        if (_c.World.Entities.FindPlayer(player) is not { } e) return null;
        if (HolderNow() != null) return null; // script holds the body: the brain decides
        var tool = follow ? "follow" : "goto";
        if (!_rt.Tools.TryGet(tool, out var t)) return null;
        var input = follow ? Json(new { player, dist = 3 }) : Json(new { to = player, dist = 2, allow_parkour = "off" });
        if (!follow && e.Position.DistanceTo(_c.World.Player.Position) <= 2.5) { LookAt(player); return $"already by {player}"; }
        _ = _acts.RunAsync([(t, input)], TimeSpan.Zero, _acts.Shutdown, follow ? ActivityKind.Job : ActivityKind.Move);
        return follow ? $"following {player}" : $"walking to {player}";
    }

    private string? Gesture(string player, string move)
    {
        // Gestures take the action lock: only on an idle body (the brain handles it otherwise).
        if (_c.CurrentAction != null || _acts.IsBusy) return null;
        if (!_rt.Tools.Contains("act")) return null;
        _ = RunTool("act", new { move, target = player });
        return $"did {move}";
    }

    private string? LookAt(string player)
    {
        if (_c.World.Entities.FindPlayer(player) is not { } e || !_c.FaceIdle(Eye(e.Position))) return null;
        lock (_lock) _lookHold = (Eye(e.Position), Now + TimeSpan.FromSeconds(4));
        Latency.NoteAction();
        return $"looking at {player}";
    }

    private string? Give(string player, Intent intent)
    {
        if (HolderNow() != null || !_rt.Tools.TryGet("skill", out var skill)) return null;
        var item = Skills.SkillJson.Item(intent.Arg);
        if (item is not { } it) return null;
        int have = _c.World.Inventory.PlayerSlots().Where(s => !s.IsEmpty && s.Id == it.Id).Sum(s => s.Count);
        if (have == 0) return null;
        int maxStack = MineClaw.Data.Items.Exists(it.Id) ? Math.Max(1, MineClaw.Data.Items.Get(it.Id).MaxStack) : 64;
        int count = intent.All ? have : Math.Min(have, intent.Count ?? Math.Min(maxStack, have));
        var args = Json(new { name = "give_items", args = new { player, item = intent.Arg, count } });
        _ = _acts.RunAsync([(skill, args)], TimeSpan.Zero, _acts.Shutdown);
        return $"giving {player} {count} {intent.Arg}";
    }

    private bool MayGive(string player)
    {
        var from = Options.GiveFrom.Length > 0 ? Options.GiveFrom : _rt.Options.Self.TrustedPlayers.Concat(_rt.Options.Self.RegularPlayers).ToArray();
        return from.Any(p => p.Equals(player, StringComparison.OrdinalIgnoreCase));
    }

    private bool HoldsItem(string name)
    {
        if (Skills.SkillJson.Item(name) is not { } it) return false;
        return _c.World.Inventory.PlayerSlots().Any(s => !s.IsEmpty && s.Id == it.Id);
    }

    /// <summary>
    /// Starts the obvious first step of a clear job (walk toward the gen/tree/player/place/chest) when the body is free.
    /// Walking only: never dig, place, take items or open containers; the client's safety applies to the walk.
    /// </summary>
    public BlockPos? TrySpeculate(string player, string text)
    {
        if (!Options.SpeculativeStart || !Stack.MayAct(BodyLayer.Speculative) || _acts.Filler != null) return null;
        var intent = IntentMatcher.Speculative(text, Names);
        if (!intent.IsSpeculative || Target(intent, player) is not { } target) return null;
        var me = _c.World.Player.Position;
        double d = Math.Sqrt(Sq(target.X + 0.5 - me.X) + Sq(target.Z + 0.5 - me.Z));
        if (d <= 3 || d > Options.MaxSpeculativeDistance) return null;
        if (!_rt.Tools.TryGet("goto", out var go)) return null;
        // No digging, no placing, no parkour: a plain reversible walk.
        var input = Json(new { to = $"{target.X},{target.Y},{target.Z}", dist = 2, build = false, allow_parkour = "off" });
        if (!_acts.TryStartFiller(ActivityKind.Speculative, [(go, input)], "spec:goto")) return null;
        lock (_lock) _spec = (intent, target);
        _log?.LogInformation("speculative {Kind}: walking toward {Target} while the brain plans", intent.Kind, target);
        return target;
    }

    private BlockPos? Target(Intent intent, string player)
    {
        var me = _c.World.Player.Position;
        switch (intent.Kind)
        {
            case IntentKind.SpecToSpeaker:
                return _c.World.Entities.FindPlayer(player) is { } e && !_c.World.IsPlayerOverVoid(e.Position) ? e.Position.ToBlockPos() : null;
            case IntentKind.SpecGenerator:
                return Nearest(IndexKinds.Generator) ?? _places.Resolve("cobble_gen");
            case IntentKind.SpecTree:
                return Nearest(IndexKinds.Tree);
            case IntentKind.SpecPlace:
                return intent.Arg is { Length: > 0 } place ? _places.Resolve(place) ?? _places.Resolve(place.Replace(' ', '_')) : null;
            case IntentKind.SpecBring:
                if (intent.Arg is { } item && HoldsItem(item))
                    return _c.World.Entities.FindPlayer(player) is { } p && !_c.World.IsPlayerOverVoid(p.Position) ? p.Position.ToBlockPos() : null;
                try
                {
                    var hit = intent.Arg == null ? null : _rt.Observations.FindItemInContainers(intent.Arg, limit: 1).FirstOrDefault();
                    if (hit?.Container.Bounds is { } b) return new BlockPos((int)b.MinX, (int)b.MinY, (int)b.MinZ);
                }
                catch (Exception) { }
                return null;
        }
        return null;

        BlockPos? Nearest(string kind)
        {
            try
            {
                var hit = _rt.Observations.Nearest(kind, me.X, me.Y, me.Z, limit: 1, maxDistance: Options.MaxSpeculativeDistance).FirstOrDefault();
                return hit.Row?.Bounds is { } b ? new BlockPos((int)Math.Floor((b.MinX + b.MaxX) / 2), (int)b.MinY, (int)Math.Floor((b.MinZ + b.MaxZ) / 2)) : null;
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// <see cref="ActivityManager.SpeculationPolicy"/>: the brain's move to (about) the same place adopts the running walk; a
    /// move nearby, or the job the walk was heading for (harvest at the gen, chop at the tree, a delegate about it),
    /// retargets it (progress kept); anything else cancels it.
    /// </summary>
    public SpeculationVerdict Judge(IReadOnlyList<(string Tool, JsonElement Input)> spec, IReadOnlyList<(string Tool, JsonElement Input)> next)
    {
        (Intent Intent, BlockPos Target)? s;
        lock (_lock) s = _spec;
        if (s is not { } cur || next.Count == 0) return SpeculationVerdict.Cancel;
        var (tool, input) = next[0];
        if (tool is "goto" or "follow")
        {
            var to = tool == "goto" ? input.Str("to") : input.Str("player");
            if (to == null || Resolve(to) is not { } p) return SpeculationVerdict.Cancel;
            double d = Math.Sqrt(Sq(p.X - cur.Target.X) + Sq(p.Z - cur.Target.Z));
            if (tool == "goto" && d <= Options.AdoptWithinBlocks) return SpeculationVerdict.Adopt;
            return d <= Options.RetargetWithinBlocks ? SpeculationVerdict.Retarget : SpeculationVerdict.Cancel;
        }
        var text = (tool + " " + input.GetRawText()).ToLowerInvariant();
        bool forGen = cur.Intent.Kind == IntentKind.SpecGenerator && (tool == "harvest_generator" || (tool is "mine" or "delegate" or "script" && (text.Contains("cobble") || text.Contains("gen"))));
        bool forTree = cur.Intent.Kind == IntentKind.SpecTree && (tool == "chop_tree" || (tool is "delegate" or "script" && (text.Contains("tree") || text.Contains("wood") || text.Contains("log"))));
        bool forBring = cur.Intent.Kind == IntentKind.SpecBring && (tool is "withdraw" or "skill" || tool == "delegate" && text.Contains(cur.Intent.Arg ?? "\u0000"));
        return forGen || forTree || forBring ? SpeculationVerdict.Retarget : SpeculationVerdict.Cancel;
    }

    private BlockPos? Resolve(string to)
    {
        if (Locations.ParseCoords(to) is { } c) return c;
        if (_c.World.Entities.FindPlayer(to) is { } e) return e.Position.ToBlockPos();
        return _places.Resolve(to);
    }

    // ------------------------------------------------------------------ notes for the brain's turn

    /// <summary>A clone body's reflex notes say whose they are ("[reflex] 67 walking to Fox"); null = main's, as before.</summary>
    public string? BodyLabel { get; init; }

    private void Note(string player, string text, string note)
    {
        if (BodyLabel is { Length: > 0 } who) note = who + " " + note;
        lock (_lock)
        {
            _notes.RemoveAll(n => Now - n.At > TimeSpan.FromMinutes(2));
            _notes.Add((player, text, note, Now));
        }
    }

    /// <summary>"[reflex] stopped harvest_generator #4" for reflexes whose chat is in this batch (drained).</summary>
    public string? TakeNotes(EventBatch batch)
    {
        lock (_lock)
        {
            if (_notes.Count == 0) return null;
            var lines = batch.Events.Where(e => e.Player != null).Select(e => (e.Player!, e.Text)).ToList();
            var mine = _notes.Where(n => lines.Any(l => l.Item1.Equals(n.Player, StringComparison.OrdinalIgnoreCase)
                && (l.Item2 == n.Text || l.Item2.Contains(n.Text, StringComparison.Ordinal)))).ToList();
            if (mine.Count == 0) return null;
            foreach (var m in mine) _notes.Remove(m);
            return "[reflex] " + string.Join("; ", mine.Select(m => m.Note).Distinct());
        }
    }

    // ------------------------------------------------------------------ 3. social idle

    private void OnPointed(string player, BlockPos pos)
    {
        if (!Social.Enabled || !Social.LookAtPointed || _isBot(player) || !Stack.MayAct(BodyLayer.Social)) return;
        var p = new Vec3d(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5);
        if (_c.FaceIdle(p)) lock (_lock) _lookHold = (p, Now + TimeSpan.FromSeconds(2.5));
    }

    /// <summary>One social tick (the timer calls it; public for tests). Returns what it did, or null.</summary>
    public string? SocialTick()
    {
        if (!Options.Enabled || !Social.Enabled || !_c.IsPlaying) return null;
        if (!Stack.MayAct(BodyLayer.Social)) return null;
        var now = Now;
        var w = _c.World;
        var me = w.Player.Position;

        // A "thinking" fidget while the brain's turn runs long and the body has nothing to do.
        if (Social.ThinkingFidgetAfterMs > 0 && _rt.Agent.TurnStartedAt is { } started
            && now - started >= TimeSpan.FromMilliseconds(Social.ThinkingFidgetAfterMs) && now - _lastFidget >= TimeSpan.FromSeconds(4))
        {
            _lastFidget = now;
            bool swing = (_fidgets++ % 3) == 2;
            if (swing ? _c.SwingIdle() : _c.FaceIdle(LookAround(w, me, _fidgets % 2 == 0 ? 25 : -25))) return "thinking";
        }

        (string Player, DateTimeOffset At)? partner;
        lock (_lock) partner = _partner is { } pp && now - pp.At <= TimeSpan.FromSeconds(Social.ConversationSeconds) ? pp : null;
        var pe = partner is { } pa ? w.Entities.FindPlayer(pa.Player) : null;

        // Loose follow: the partner walked off mid-conversation; idle body, both on safe ground, no parkour, no building.
        if (Social.LooseFollow && pe != null && _rt.Agent.TurnStartedAt == null)
        {
            double d = pe.Position.DistanceTo(me);
            if (d > Social.FollowStartBlocks && d <= Social.FollowMaxBlocks && SafeToFollow(w, me, pe.Position) && _rt.Tools.TryGet("goto", out var go))
            {
                var input = Json(new { to = pe.Name, dist = Social.FollowStopBlocks, build = false, allow_parkour = "off" });
                if (_acts.TryStartFiller(ActivityKind.Social, [(go, input)], "social:follow")) return "follow " + pe.Name;
            }
        }

        (Vec3d Point, DateTimeOffset Until)? hold;
        lock (_lock) hold = _lookHold;
        if (hold is { } h && h.Until > now) return null; // still looking at the pointed block / the speaker

        // Glance at a player who just came near.
        if (Social.Glance)
        {
            var nearby = w.Entities.Nearby(me, Social.GlanceRadius, e => e.Kind == EntityKind.Player && e.Name != null
                && !e.Name.Equals(_c.Username, StringComparison.OrdinalIgnoreCase) && !_isBot(e.Name)).ToList();
            var fresh = nearby.FirstOrDefault(e => !_near.Contains(e.Name!));
            _near.Clear();
            foreach (var e in nearby) _near.Add(e.Name!);
            if (fresh != null && _c.FaceIdle(Eye(fresh.Position)))
            {
                lock (_lock) _lookHold = (Eye(fresh.Position), now + TimeSpan.FromSeconds(1.5));
                return "glance " + fresh.Name;
            }
        }

        // Head tracking: the conversation partner, else the nearest player.
        if (Social.HeadTracking)
        {
            var target = pe != null && pe.Position.DistanceTo(me) <= 16 ? pe
                : w.Entities.Nearest(me, 8, e => e.Kind == EntityKind.Player && e.Name != null
                    && !e.Name.Equals(_c.Username, StringComparison.OrdinalIgnoreCase) && !_isBot(e.Name));
            if (target != null && _c.FaceIdle(Eye(target.Position))) return "track " + target.Name;
        }
        return null;
    }

    /// <summary>Loose follow only between safe spots: neither of us over the void, and the partner on the same level.</summary>
    internal static bool SafeToFollow(WorldState w, Vec3d me, Vec3d target) =>
        !w.IsPlayerOverVoid(me) && !w.IsPlayerOverVoid(target) && Math.Abs(target.Y - me.Y) <= 2;

    // ------------------------------------------------------------------ helpers

    private Task<ToolResult> RunTool(string name, object input) =>
        Task.Run(async () =>
        {
            using (Bodies.BodyScope.Push(Body)) // reflex tools act on this brainstem's body (null = the focus, as before)
                return await _rt.Tools.ExecuteAsync(name, Json(input), _acts.Shutdown).ConfigureAwait(false);
        });

    /// <summary>The body this brainstem drives (one brain, many bodies); null = whatever body the router focuses.</summary>
    public Bodies.IBody? Body { get; set; }

    private static Vec3d Eye(Vec3d feet) => feet + new Vec3d(0, PlayerPhysics.EyeHeight, 0);

    private static Vec3d LookAround(WorldState w, Vec3d me, double degrees)
    {
        double yaw = (w.Player.Yaw + degrees) * Math.PI / 180;
        // Beta yaw: 0 = +Z (south), 90 = -X (west).
        return new Vec3d(me.X - Math.Sin(yaw) * 4, me.Y + PlayerPhysics.EyeHeight, me.Z + Math.Cos(yaw) * 4);
    }

    private static double Sq(double v) => v * v;

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);
}
