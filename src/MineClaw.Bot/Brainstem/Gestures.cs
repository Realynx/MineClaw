using MineClaw.Agent;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Chat;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Brainstem;

/// <summary>
/// Body language under the brain (zero tokens, no model call): the bot nods when it agrees, shakes its head when it can't
/// or is sorry, waves and looks at whoever it greets, bows for thanks, bobs/hops when excited, plays its own "/me bows" or
/// "*waves*", turns its head to a player who talks to it, and nods at a player's yes/no to its own question.
/// <list type="bullet">
/// <item><b>Triggers</b>: <see cref="ChatRouter.Said"/> (the bot's own text, before its lines go out, so the look-at comes
///   first) and <see cref="ChatRouter.Accepted"/> (addressed player chat). Matching is <see cref="GestureClassifier"/>.</item>
/// <item><b>Priority</b>: the lowest, social layer. Nothing during a safety reflex/fight, a recent danger, a hostile nearby,
///   low health, off the ground (falling, pillaring, jumping, swimming) or within <see cref="GestureOptions.VoidMargin"/> of a
///   deadly edge (bridging, edge work). Head gestures (nod, shake, look-at) and the arm wave overlay a walk or a job: the
///   client sends their look only in the movement packet, never in physics, so the walking heading is untouched, and any
///   dig/place/use cancels them first. Body gestures (sneak, hop) need an idle body (no action, job, script or filler);
///   otherwise a bow dips the head only and excitement waves; a hop also needs ground under all four corners.</item>
/// <item><b>Debounce</b>: one expressive gesture per <see cref="GestureOptions.MinIntervalMs"/>; looking at a speaker has
///   its own per-player interval and never replaces a gesture that is playing.</item>
/// </list>
/// </summary>
public sealed class Gestures
{
    private readonly AgentRuntime _rt;
    private readonly IGameClient _c;
    private readonly GestureOptions _o;
    private readonly Func<string, bool> _isBot;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _lookedAt = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _last = DateTimeOffset.MinValue, _danger = DateTimeOffset.MinValue, _asked = DateTimeOffset.MinValue;
    private (string Player, DateTimeOffset At)? _partner;
    private int _excited;

    public Gestures(AgentRuntime rt, IGameClient client, GestureOptions? options = null, Func<string, bool>? isBot = null,
        TimeProvider? time = null, ILogger? log = null)
    {
        _rt = rt;
        _c = client;
        _o = options ?? rt.Options.Gestures;
        _isBot = isBot ?? (_ => false);
        _time = time ?? TimeProvider.System;
        _log = log;
        Classifier = new GestureClassifier(_o.Table, _o.ReplaceDefaults);
    }

    public GestureClassifier Classifier { get; }

    /// <summary>Why the last gesture didn't play ("void", "danger", "debounced", ...), for tests/debug.</summary>
    public string? LastSuppressed { get; private set; }

    /// <summary>Gestures played by name (telemetry/tests).</summary>
    public List<string> Played { get; } = new();

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>On for this login: Enabled, not in <see cref="GestureOptions.DisabledFor"/>, and clones only with <see cref="GestureOptions.Clones"/>.</summary>
    public bool Enabled
    {
        get
        {
            if (!_o.Enabled) return false;
            var me = _c.Username;
            if (_o.DisabledFor.Any(n => n.Equals(me, StringComparison.OrdinalIgnoreCase))) return false;
            return _o.Clones || !IsClone(me);
        }
    }

    private bool IsClone(string me)
    {
        var hive = _rt.Options.Hive;
        if (hive.CloneFor(me) != null) return true;
        return hive.MainUsername is { Length: > 0 } main && !main.Equals(me, StringComparison.OrdinalIgnoreCase);
    }

    public void Attach()
    {
        _rt.Chat.Said += t => Guard(() => OnSaid(t));
        _rt.Chat.Accepted += m => { if (m.Player is { Length: > 0 } p) Guard(() => OnHeard(p, m.Text)); };
        _c.Danger += OnDanger;
    }

    /// <summary>A real danger (not hunger, not "recovered") cancels the gesture and keeps the body quiet for a while.</summary>
    public void OnDanger(DangerEvent d)
    {
        if (d.Kind is DangerKind.Hunger or DangerKind.Recovered) return;
        lock (_lock) _danger = Now;
        _c.CancelGesture();
    }

    private void Guard(Func<string?> f)
    {
        try { if (f() is { } did) _log?.LogDebug("gesture: {Gesture}", did); }
        catch (Exception ex) { _log?.LogDebug(ex, "gesture failed"); } // body language never breaks chat
    }

    // ------------------------------------------------------------------ triggers

    /// <summary>The bot's own outgoing text: its /me or its words → a gesture. Returns what played, or null.</summary>
    public string? OnSaid(string text)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains('?')) lock (_lock) _asked = Now;
        var hit = Classifier.ClassifyOutgoing(text);
        if (hit is not { } h) return null;
        bool emote = h.Rule.StartsWith("emote", StringComparison.Ordinal);
        if (emote ? !_o.Emotes : !_o.OwnChat) return null;
        return Play(h.Gesture, TargetOf(text));
    }

    /// <summary>A player's addressed line: a yes/no to the bot's question → a small nod; else look at them.</summary>
    public string? OnHeard(string player, string text)
    {
        if (!Enabled || _isBot(player) || player.Equals(_c.Username, StringComparison.OrdinalIgnoreCase)) return null;
        DateTimeOffset asked;
        lock (_lock) { _partner = (player, Now); asked = _asked; }
        if (_o.AnswerNodSeconds > 0 && Now - asked <= TimeSpan.FromSeconds(_o.AnswerNodSeconds) && GestureClassifier.IsYesNo(StripNames(text)))
        {
            lock (_lock) _asked = DateTimeOffset.MinValue; // one acknowledgement per question
            return Play("ack", player);
        }
        if (!_o.LookAtSpeaker) return null;
        lock (_lock)
        {
            if (_lookedAt.TryGetValue(player, out var at) && Now - at < TimeSpan.FromMilliseconds(_o.LookAtIntervalMs)) return Suppress("debounced");
            if (_c.CurrentGesture != null) return Suppress("gesture playing");
        }
        if (WhyNot(body: false) is { } why) return Suppress(why);
        if (LookPlan(player) is not { } look || !_c.PlayGesture(look)) return Suppress("refused");
        lock (_lock) { _lookedAt[player] = Now; Played.Add("look"); }
        return "look " + player;
    }

    // ------------------------------------------------------------------ playing

    /// <summary>
    /// Plays a gesture by name (nod|shake|wave|greet|bow|bob|hop|excite|look|ack) toward <paramref name="target"/> (a player
    /// name, for look-at), after the safety rules and the debounce. Returns the plan's name, or null (see <see cref="LastSuppressed"/>).
    /// </summary>
    public string? Play(string gesture, string? target = null)
    {
        if (!Enabled) return Suppress("disabled");
        lock (_lock)
            if (Now - _last < TimeSpan.FromMilliseconds(_o.MinIntervalMs)) return Suppress("debounced");
        if (WhyNot(body: false) is { } why) return Suppress(why);
        bool bodyFree = BodyFree && WhyNot(body: true) == null;
        var plan = Build(gesture, target, bodyFree);
        if (plan == null) return Suppress("no plan");
        if (!_c.PlayGesture(plan)) return Suppress("refused");
        lock (_lock) { _last = Now; _lookedAt[target ?? ""] = Now; Played.Add(plan.Name); }
        LastSuppressed = null;
        _log?.LogInformation("gesture {Gesture} ({Plan}){Target}", gesture, plan.Name, target is null ? "" : " → " + target);
        return plan.Name;
    }

    private GesturePlan? Build(string gesture, string? target, bool bodyFree)
    {
        float pitch = _c.World.Player.Physics.Pitch;
        switch (gesture)
        {
            case "nod": return GestureMoves.Nod(basePitch: pitch);
            case "ack": return With(GestureMoves.Nod(cycles: 1, amplitude: 22, basePitch: pitch), LookPlan(target));
            case "shake": return GestureMoves.Shake();
            case "wave": return GestureMoves.Wave();
            case "greet": return With(GestureMoves.Wave(), LookPlan(target ?? Partner()));
            case "look": return LookPlan(target ?? Partner());
            case "bow": return GestureMoves.Bow(sneak: bodyFree);
            case "bob": return bodyFree ? GestureMoves.CrouchBob() : GestureMoves.Wave(2);
            case "hop": return bodyFree ? (HopSafe() ? GestureMoves.Hop() : GestureMoves.CrouchBob(2)) : GestureMoves.Wave(2);
            case "excite":
                if (!bodyFree) return GestureMoves.Wave(2);
                return (_excited++ % 2 == 1 && HopSafe()) ? GestureMoves.Hop() : GestureMoves.CrouchBob();
            default: return null;
        }
    }

    private static GesturePlan With(GesturePlan a, GesturePlan? look) => look == null ? a : GestureMoves.Merge(look, a);

    /// <summary>Head toward a visible player's eyes (≤ 16 blocks), or null.</summary>
    private GesturePlan? LookPlan(string? player)
    {
        if (player is not { Length: > 0 } || _c.World.Entities.FindPlayer(player) is not { } e) return null;
        var me = _c.World.Player;
        if (e.Position.DistanceTo(me.Position) > 16) return null;
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(me.EyePosition, e.Position + new Vec3d(0, PlayerPhysics.EyeHeight, 0));
        return GestureMoves.LookAt(yaw, pitch);
    }

    // ------------------------------------------------------------------ safety

    /// <summary>Nothing else drives the body: no client action, no activity/filler, no script holding it.</summary>
    public bool BodyFree => _c.CurrentAction == null && !_rt.Activities.IsBusy && _rt.Activities.Filler == null
        && _rt.Agent.BodyHolder?.Invoke() == null;

    /// <summary>Why no gesture may play now (null = ok). <paramref name="body"/> adds the sneak/hop checks.</summary>
    public string? WhyNot(bool body)
    {
        if (!_c.IsPlaying) return "not playing";
        if (_c.SafetyEngaged) return "safety";
        var w = _c.World;
        var p = w.Player;
        if (p.IsDead || p.Health <= _o.MinHealth) return "hurt";
        lock (_lock) if (Now - _danger < TimeSpan.FromSeconds(_o.DangerQuietSeconds)) return "danger";
        if (_o.DangerRadius > 0 && w.Entities.Nearest(p.Position, _o.DangerRadius, e => e.IsHostileMob) != null) return "danger";
        var phys = p.Physics;
        if (!p.OnGround || phys.InWater || phys.InLava || Math.Abs(phys.Velocity.Y) > 0.1) return "airborne";
        var pos = p.Position;
        int x = (int)Math.Floor(pos.X), y = (int)Math.Floor(pos.Y + 1e-6), z = (int)Math.Floor(pos.Z);
        if (w.IsPlayerOverVoid(pos)) return "void";
        if (_o.VoidMargin > 0 && Safety.EdgeDistance(w, x, y, z, _o.VoidMargin) <= _o.VoidMargin) return "void";
        if (body && (_c.CurrentAction != null || !BodyFree)) return "body busy";
        return null;
    }

    /// <summary>A hop needs ground under all four corners of the hitbox and headroom.</summary>
    public bool HopSafe()
    {
        var w = _c.World;
        var pos = w.Player.Position;
        int y = (int)Math.Floor(pos.Y + 1e-6);
        foreach (var (dx, dz) in new[] { (-0.3, -0.3), (-0.3, 0.3), (0.3, -0.3), (0.3, 0.3) })
            if (!BlockShapes.HasCollision(w, (int)Math.Floor(pos.X + dx), y - 1, (int)Math.Floor(pos.Z + dz))) return false;
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
            if (BlockShapes.HasCollision(w, (int)Math.Floor(pos.X) + dx, y + 2, (int)Math.Floor(pos.Z) + dz)) return false;
        return true;
    }

    // ------------------------------------------------------------------ helpers

    private string? Suppress(string why)
    {
        LastSuppressed = why;
        return null;
    }

    private string? Partner()
    {
        lock (_lock) return _partner is { } p && Now - p.At <= TimeSpan.FromSeconds(60) ? p.Player : null;
    }

    /// <summary>The player a line names (login or nickname, visible), else the conversation partner.</summary>
    private string? TargetOf(string text)
    {
        var me = _c.World.Player.Position;
        var near = _c.World.Entities.Nearby(me, 16, e => e.Kind == EntityKind.Player && e.Name != null
                     && !e.Name.Equals(_c.Username, StringComparison.OrdinalIgnoreCase)).Select(e => e.Name!).ToList();
        // Exact accounts win over nicknames: "robinchan" / "robin-chan" is never Robin.
        if (near.FirstOrDefault(n => PlayerNames.MentionsLogin(text, n)) is { } login) return login;
        var bare = PlayerNames.WithoutLogins(text, near);
        if (near.FirstOrDefault(n => _rt.Chat.NicknamesOf(n).Any(nick => AddressingText.Mentions(bare, nick))) is { } nicked) return nicked;
        return Partner();
    }

    /// <summary>"claudechan yes" → "yes".</summary>
    private string StripNames(string text)
    {
        var t = text.Trim();
        foreach (var n in new[] { _rt.Chat.Username, _rt.Chat.DisplayName }.Concat(_rt.Chat.Aliases))
            if (n is { Length: > 0 } && t.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                return t[n.Length..].TrimStart(',', ':', ' ', '~');
        return t;
    }
}
