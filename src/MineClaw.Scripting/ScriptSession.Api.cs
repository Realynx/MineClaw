using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Scripting;

/// <summary>
/// The language-neutral script API: events and handlers, actions, chat, ask, waits, checkpoints and queries. Runtimes (JS,
/// legacy Lua) convert their values to these calls (JSON nodes in, JSON nodes out) and never touch the host themselves.
/// </summary>
public sealed partial class ScriptSession
{
    /// <summary>
    /// An event listener: on() handlers, machine transitions, policies. The first registered one whose <see cref="Match"/> takes
    /// an event runs it at the next safe point; <see cref="Cuts"/> also aborts the in-flight interruptible action (it re-runs after
    /// the handler unless the handler unwinds). <see cref="Run"/>'s answer: null = handled, "pause", "wake", "stop".
    /// </summary>
    internal sealed record Handler(string Label, Func<ScriptEvent, bool> Match, Func<ScriptEvent, string?> Run, bool Cuts, object? Owner = null);

    private readonly List<Handler> _handlers = new();
    private readonly Queue<(Handler H, ScriptEvent E)> _pendingHandlers = new();
    private readonly List<(long Seq, ScriptEvent E)> _events = new();
    private long _eventSeq;
    private string? _waitingFor;
    private readonly Queue<DateTimeOffset> _said = new();
    private int _sayRun;
    private DateTimeOffset? _lastSay;
    private DateTimeOffset _lastWake = DateTimeOffset.MinValue;

    /// <summary>Params that take a position: <c>go(1,64,2)</c>, <c>dig(me.pos)</c>, <c>place("torch", {x:1,y:2,z:3})</c>.</summary>
    internal static readonly HashSet<string> PosParams = new(StringComparer.OrdinalIgnoreCase) { "to", "at", "pos" };

    /// <summary>Short names for common tools/skills (terse scripts). Full tool names work too, except <c>goto</c> (a keyword).</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["go"] = "goto", ["chop"] = "chop_tree", ["take"] = "withdraw", ["use"] = "use_block", ["collect"] = "collect_drops",
        ["gen"] = "harvest_generator", ["sort"] = "organize_chest", ["move"] = "move_items",
        // Giving things to a player: what players and models actually say ("toss me the logs", "give foxy 5 wood").
        ["toss"] = "give_items", ["give"] = "give_items", ["drop"] = "drop_items",
    };

    /// <summary>
    /// Up to <paramref name="max"/> names closest to <paramref name="name"/> (substring hits first, then edit distance ≤ 3),
    /// for "no skill 'x'; closest: a, b" errors.
    /// </summary>
    public static IReadOnlyList<string> Closest(string name, IEnumerable<string> candidates, int max = 3)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        if (n.Length == 0) return [];
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => (c, d: Score(n, c.ToLowerInvariant())))
            .Where(t => t.d <= 3)
            .OrderBy(t => t.d).ThenBy(t => t.c.Length).ThenBy(t => t.c, StringComparer.Ordinal)
            .Take(max).Select(t => t.c).ToList();

        static int Score(string a, string b)
        {
            if (a == b) return 0;
            if (b.Contains(a) || a.Contains(b)) return 1;
            var parts = a.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && b.Split('_').Any(bp => parts.Contains(bp))) return 2;
            int[] prev = new int[b.Length + 1], cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }
    }

    // ------------------------------------------------------------------ events (any thread)

    /// <summary>True if this script would take <paramref name="e"/> itself (a handler or a waitfor/ask listening): the brain stays asleep.</summary>
    public bool HasHandler(ScriptEvent e)
    {
        lock (_lock)
        {
            if (!IsLive || e.Kind is ScriptEventKind.Stop or ScriptEventKind.Join) return false;
            if (e.Kind == ScriptEventKind.Chat && _waitingFor is "chat" or "ask") return true;
            if (e.Kind == ScriptEventKind.Danger && _waitingFor == "danger") return true;
            return _handlers.Any(x => SafeMatch(x, e));
        }
    }

    /// <summary>
    /// An event while the script runs: feeds waitfor and handlers; otherwise applies the interrupt policy (danger/stop pause and
    /// abort the interruptible action; addressed chat pauses with auto-resume when configured).
    /// </summary>
    public void Deliver(ScriptEvent e)
    {
        Handler? h = null;
        CancellationTokenSource? cut = null;
        lock (_lock)
        {
            if (!IsLive) return;
            _events.Add((++_eventSeq, e));
            if (_events.Count > 32) _events.RemoveAt(0);
            if (e.Kind == ScriptEventKind.Join) return;
            if (e.Kind == ScriptEventKind.Chat && _waitingFor is "chat" or "ask") return;
            if (e.Kind == ScriptEventKind.Danger && _waitingFor == "danger") return;
            if (e.Kind != ScriptEventKind.Stop)
                h = _handlers.FirstOrDefault(x => SafeMatch(x, e));
            // A signal already waiting for the same handler is not queued twice (timers coalesce while an action runs).
            if (h != null && e.Kind == ScriptEventKind.Signal && _pendingHandlers.Any(p => ReferenceEquals(p.H, h) && p.E.Topic == e.Topic)) return;
            if (h != null)
            {
                _pendingHandlers.Enqueue((h, e));
                // A repeating timer never cuts the running action (it would restart it every period); its handler runs between actions.
                bool repeating = e.Topic?.StartsWith("every:", StringComparison.Ordinal) == true;
                if (h.Cuts && !repeating && !_currentAtomic && _abortCts != null) { _abortedForPause = true; cut = _abortCts; }
            }
        }
        if (h != null)
        {
            try { cut?.Cancel(); } catch (ObjectDisposedException) { }
            _wake.Set();
            return;
        }
        switch (e.Kind)
        {
            case ScriptEventKind.Danger: RequestPause(PauseKind.Danger, "danger: " + Short(e.Text, 40), abort: true); break;
            case ScriptEventKind.Stop: RequestPause(PauseKind.Stop, $"{e.Player ?? "player"} said stop", abort: true); break;
            case ScriptEventKind.Chat when _o.PauseOnChat: RequestPause(PauseKind.Chat, $"chat from {e.Player ?? "someone"}", abort: false); break;
        }
    }

    private static bool SafeMatch(Handler h, ScriptEvent e)
    {
        try { return h.Match(e); } catch (Exception) { return false; }
    }

    internal static bool Matches(string? from, string? text, ScriptEvent e) =>
        (from == null || string.Equals(from, e.Player, StringComparison.OrdinalIgnoreCase)) &&
        (text == null || e.Text.Contains(text, StringComparison.OrdinalIgnoreCase));

    internal void AddHandler(Handler h)
    {
        lock (_lock)
        {
            if (_handlers.Count >= 24) throw Bug("too many on() handlers");
            _handlers.Add(h);
        }
    }

    /// <summary>Drops every handler of <paramref name="owner"/> (a machine that ended, a finished timeout) and its pending runs.</summary>
    internal void RemoveHandlers(object owner)
    {
        lock (_lock)
        {
            _handlers.RemoveAll(h => ReferenceEquals(h.Owner, owner));
            var keep = _pendingHandlers.Where(p => !ReferenceEquals(p.H.Owner, owner)).ToList();
            _pendingHandlers.Clear();
            foreach (var p in keep) _pendingHandlers.Enqueue(p);
        }
    }

    private bool TakeHandler(out Handler? h, out ScriptEvent? e)
    {
        lock (_lock)
        {
            if (_pendingHandlers.TryDequeue(out var x)) { h = x.H; e = x.E; return true; }
        }
        h = null;
        e = null;
        return false;
    }

    /// <summary>Runs a handler at a safe point. Its answer: null = handled (brain stays asleep), "pause", "wake" (script keeps going), "stop".</summary>
    private void RunHandler(Handler h, ScriptEvent e)
    {
        var ret = h.Run(e);
        TouchSegment();
        switch (ret?.Trim().ToLowerInvariant())
        {
            case "pause": RequestPause(e.Kind == ScriptEventKind.Danger ? PauseKind.Danger : PauseKind.Script, $"{h.Label} passed on: {Short(e.Text, 40)}", abort: false); break;
            case "wake": WakeBrain($"{h.Label} passed on: {Short(e.Text, 60)}", force: true); break;
            case "stop": Stop($"stopped by {h.Label}"); break;
        }
    }

    internal bool WakeBrain(string reason, bool force = false)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (!force && now - _lastWake < TimeSpan.FromSeconds(_o.MinWakeIntervalSeconds)) return false;
            _lastWake = now;
        }
        SnapshotVars();
        _host.Wake(this, Short(OneLine(reason), 120));
        return true;
    }

    /// <summary>
    /// <c>on(topic, [filter,] fn)</c>: a handler for "danger" (and its topics lowhp/void/died), "chat" (filter from/text, or
    /// "chat:&lt;text&gt;"), a signal name, ... Legacy Lua accepts danger|chat only (its runtime checks).
    /// </summary>
    internal void On(string topic, string? from, string? text, Func<ScriptEvent, string?> fn, bool? cuts = null, object? owner = null)
    {
        var t = (topic ?? "").Trim();
        if (t.Length == 0) throw Bug("on(topic, fn) needs a topic: danger|chat|hungry|invFull|toolBroke|night|day|...");
        bool isDanger = new ScriptEvent(ScriptEventKind.Danger, "", Topic: "danger").Matches(t) || t is "lowhp" or "void" or "died";
        AddHandler(new Handler($"on('{t}')", e => e.Matches(t) && Matches(from, text, e), fn, cuts ?? (isDanger || !t.StartsWith("chat", StringComparison.OrdinalIgnoreCase)), owner));
    }

    // ------------------------------------------------------------------ actions

    /// <summary>
    /// One host action: safe point → limits → run (blocking this script thread only) → safe point. An action cut short by a
    /// danger pause or a handler is re-run after it, so interrupts are invisible to the script. <paramref name="counts"/> =
    /// false for engine-internal waits (machine idle), which don't use up the action limit.
    /// </summary>
    internal ActResult RunAction(string label, bool atomic, Func<CancellationToken, Task<ActResult>> run, bool counts = true)
    {
        SafePoint();
        if (_host.ActionVeto() is { } veto) { Stop("stopped: " + veto); ThrowIfStopping(); }
        Locate();
        if (_resumeTarget != null)
        {
            if (++_skipped > _o.MaxSkippedActions) throw Bug($"checkpoint '{_resumeTarget}' not reached");
            TouchSegment();
            return ActResult.Success("skipped (resume)");
        }
        if (counts)
        {
            if (Actions >= _o.MaxActions) Limit($"action limit ({_o.MaxActions})");
            Actions++;
        }
        ActResult r;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            CancellationToken ct;
            lock (_lock)
            {
                _abortCts?.Dispose();
                _abortCts = atomic ? null : CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token, _hardCt);
                _currentAtomic = atomic;
                _abortedForPause = false;
                ct = _abortCts?.Token ?? _hardCt;
            }
            try
            {
                using (_owner.StepScope?.Invoke()) // explicit around every step, not only inherited from the thread
                    r = run(ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { r = ActResult.Fail("cancelled"); }
            catch (ScriptFailException) { throw; }
            catch (Exception ex) { r = ActResult.Fail($"{ex.GetType().Name}: {OneLine(ex.Message)}"); }
            bool again;
            lock (_lock)
            {
                again = _abortedForPause && _stopReason == null;
                _abortedForPause = false;
                _currentAtomic = false;
            }
            TouchSegment();
            if (!again) break;
            SafePoint();
            Locate();
        }
        if (counts) RecordTiming(label, sw.Elapsed, r.Ok);
        SafePoint();
        if (!r.Ok) { LastFailure = $"{label}: {Short(OneLine(r.Msg), 80)}"; _lastFailureMsg = r.Msg; }
        LastActionFailed = !r.Ok;
        SnapshotVars();
        TouchSegment();
        return r;
    }

    private void RecordTiming(string label, TimeSpan took, bool ok)
    {
        lock (_lock)
        {
            var t = _timings.GetValueOrDefault(label);
            _timings[label] = new ActionTiming(t.Calls + 1, t.Fails + (ok ? 0 : 1), t.Total + took);
        }
    }

    /// <summary>A game tool by its <see cref="ScriptAction"/>.</summary>
    internal ActResult Act(ScriptAction a, JsonElement args) => RunAction(a.Name, a.Atomic, ct => _host.Act(a.Name, args, ct));

    /// <summary>A catalog skill.</summary>
    internal ActResult RunSkill(string skill, JsonElement args) => RunAction(skill, _host.SkillIsAtomic(skill), ct => _host.Skill(skill, args, ct));

    /// <summary>A script-callable game function: its global name, the tool/skill it runs and its positional params.</summary>
    internal sealed record Callable(string Global, string Target, ScriptAction? Action, IReadOnlyList<(string Name, string? Type)> Params);

    private IReadOnlyCollection<string>? _skillNames;

    internal IReadOnlyCollection<string> SkillNames()
    {
        if (_skillNames != null) return _skillNames;
        try { _skillNames = _host.Skills(); } catch (Exception) { _skillNames = []; }
        return _skillNames;
    }

    /// <summary>
    /// Every game function a script can call as a global: aliases (go, gen, take...), the action tools, then catalog skills.
    /// <paramref name="free"/> says whether a global name may be used (not a keyword, not a builtin, not taken).
    /// </summary>
    internal IReadOnlyList<Callable> Callables(Func<string, bool> free)
    {
        var list = new List<Callable>();
        var byName = _host.Actions.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var skills = SkillNames();
        void Add(string global, string target, ScriptAction? a)
        {
            if (!free(global)) return;
            list.Add(new Callable(global, target, a, a != null ? ActionParams(a) : SkillParams(target)));
        }
        foreach (var (alias, target) in Aliases)
        {
            if (byName.TryGetValue(target, out var a)) Add(alias, target, a);
            else if (skills.Contains(target)) Add(alias, target, null);
        }
        foreach (var a in _host.Actions) Add(a.Name, a.Name, a);
        foreach (var sk in skills) Add(sk, sk, null);
        return list;
    }

    internal ActResult Invoke(Callable c, JsonElement args) => c.Action is { } a ? Act(a, args) : RunSkill(c.Target, args);

    /// <summary>
    /// <c>skill("name", args)</c>: a catalog skill, an alias ("toss", "go") or an action name; an unknown one fails with the
    /// closest names. <paramref name="map"/> builds the JSON input once the params are known.
    /// </summary>
    internal ActResult SkillCall(string asked, Func<IReadOnlyList<(string Name, string? Type)>, JsonElement> map)
    {
        var name = (asked ?? "").Trim();
        var skills = SkillNames();
        var byName = _host.Actions.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        if (!skills.Contains(name) && Aliases.TryGetValue(name, out var target)) name = target;
        if (!skills.Contains(name) && byName.TryGetValue(name, out var act)) return Act(act, map(ActionParams(act)));
        if (!skills.Contains(name) && skills.Count > 0)
        {
            var near = Closest(name, skills.Concat(byName.Keys).Concat(Aliases.Keys));
            return RunAction(name, true, _ => Task.FromResult(ActResult.Fail($"no skill '{asked}'" +
                (near.Count > 0 ? $"; closest: {string.Join(", ", near)}" : "; use find_skill"))));
        }
        return RunSkill(name, map(SkillParams(name)));
    }

    internal static IReadOnlyList<(string Name, string? Type)> ActionParams(ScriptAction a) => a.Params.Select(p => (p, (string?)null)).ToList();

    internal IReadOnlyList<(string Name, string? Type)> SkillParams(string skill)
    {
        var list = new List<(string, string?)>();
        if (_host.SkillSchema(skill) is { } schema && schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject())
                list.Add((p.Name, p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null));
        return list;
    }

    private static readonly Regex NumRx = new(@"(?<![\d,.\-])(\d+)(?![\d,])", RegexOptions.Compiled);

    /// <summary>First plain integer of a result ("mined 12 cobblestone (inv 44)" → 12); coordinates are skipped.</summary>
    public static int CountOf(string? msg)
    {
        if (string.IsNullOrEmpty(msg)) return 0;
        var m = NumRx.Match(msg);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    /// <summary><c>wait(s)</c> (clamped). <paramref name="counts"/> = false for engine-internal idle waits.</summary>
    internal ActResult Wait(double seconds, bool counts = true)
    {
        double s = Math.Clamp(seconds, 0.05, _o.MaxWaitSeconds);
        // A wait cut by a handler resumes for the time left, never from the start (a frequent event can't stretch it forever).
        DateTimeOffset? end = null;
        return RunAction("wait", false, async ct =>
        {
            end ??= _time.GetUtcNow() + TimeSpan.FromSeconds(s);
            var left = end.Value - _time.GetUtcNow();
            if (left > TimeSpan.Zero) await Task.Delay(left, _time, ct).ConfigureAwait(false);
            return ActResult.Success($"waited {s:0.#}s");
        }, counts);
    }

    internal ActResult Chat(string text, bool emote)
    {
        var t = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        // No server commands from scripts: a word starting with '/' loses it (chat splitting can't surface one either).
        t = Regex.Replace(t, @"(^|\s)/+", "$1").Trim();
        if (t.Length == 0) return ActResult.Fail("nothing to say");
        if (t.Length > 200) t = t[..200];
        // Live 10-27 (B15): a hive clone's say() threw "muted" and aborted real work (a chop script after 30 s, +4 oak wood).
        // Muted chat is report output for the brain, never a failure.
        if (_host.Muted?.Invoke(t) == true)
        {
            var said = Short(t, 160);
            lock (Reports) { Reports.Add(said); if (Reports.Count > 8) Reports.RemoveAt(0); }
            _host.Log?.Invoke($"script {Name}: [muted, reported] {said}");
            return ActResult.Success("not said (chat muted): it goes back to the brain in this script's result");
        }
        string? limited = null;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            while (_said.Count > 0 && now - _said.Peek() > TimeSpan.FromMinutes(1)) _said.Dequeue();
            if (_said.Count >= _o.MaxSayPerMinute) return ActResult.Fail("chat rate limit");
            // Live 10-27: a scan loop say-ed every block ("4,64,6 cobblestone"...) into public chat. A run gets a couple of
            // lines, spaced out; anything beyond goes to the log (debug output belongs there).
            if (_sayRun >= Math.Max(0, _o.MaxSayPerRun)) limited = $"say limit ({_o.MaxSayPerRun}/run)";
            else if (_lastSay is { } last && now - last < TimeSpan.FromSeconds(_o.SayMinIntervalSeconds)) limited = $"say limit (1 per {_o.SayMinIntervalSeconds:0}s)";
            else { _said.Enqueue(now); _sayRun++; _lastSay = now; }
        }
        if (limited != null)
        {
            var msg = Short(t, 160);
            lock (_lock) { Logs.Add(msg); if (Logs.Count > 20) Logs.RemoveAt(0); }
            _host.Log?.Invoke($"script {Name}: [{limited}, logged] {msg}");
            return ActResult.Success($"logged, not said ({limited}): use log() for data, say() only to talk to players");
        }
        return RunAction(emote ? "emote" : "say", atomic: true, ct => emote ? _host.Emote(t, ct) : _host.Say(t, ct));
    }

    /// <summary>ask(player, q, sec) → the player's reply or null. ask("brain", q) pauses and wakes the brain (needs_input); returns its answer.</summary>
    internal string? Ask(string? player, string question, double? seconds)
    {
        var q = Regex.Replace(Short((question ?? "").Replace('\n', ' ').Trim(), 180), @"(^|\s)/+", "$1");
        if (q.Length == 0) throw Bug("ask(player, question, sec) needs a question");
        string? who = string.IsNullOrEmpty(player) ? null : player;
        if (who != null && who.Equals("brain", StringComparison.OrdinalIgnoreCase))
        {
            SafePoint();
            Locate();
            return PauseHere(PauseKind.Script, "needs_input: " + q);
        }
        double s = Math.Clamp(seconds ?? 60, 5, Math.Min(180, _o.MaxWaitSeconds));
        string? reply = null;
        lock (_lock) _waitingFor = "ask";
        try
        {
            RunAction("ask", false, async ct =>
            {
                reply = await _host.Ask(who, q, TimeSpan.FromSeconds(s), ct).ConfigureAwait(false);
                return reply != null ? ActResult.Success(reply) : ActResult.Fail("no answer");
            });
        }
        finally { lock (_lock) _waitingFor = null; }
        return reply;
    }

    /// <summary>
    /// <c>waitfor(kind, filter, sec)</c>: chat|danger|join (filter = text / {from,text}), item ("log" / {item,n} / [item,n]),
    /// block ({x,y,z,name}), day, night, or any signal topic the script registered.
    /// </summary>
    internal ActResult WaitFor(string kindRaw, JsonNode? filter, double? seconds)
    {
        var kind = (kindRaw ?? "").Trim().ToLowerInvariant();
        double s = Math.Clamp(seconds ?? 60, 0.1, _o.MaxWaitSeconds);
        string? from = Field(filter, "from") ?? Field(filter, "player"), text = Field(filter, "text");
        Func<long, (bool Hit, string Msg)> check;
        switch (kind)
        {
            case "chat" or "danger" or "join":
            {
                var k = kind switch { "chat" => ScriptEventKind.Chat, "danger" => ScriptEventKind.Danger, _ => ScriptEventKind.Join };
                if (AsString(filter) is { } fs) { if (kind == "join") from = fs; else text = fs; }
                check = since =>
                {
                    lock (_lock)
                        foreach (var (seq, ev) in _events)
                            if (seq > since && (ev.Kind == k || (k == ScriptEventKind.Danger && ev.Kind == ScriptEventKind.Stop)) && Matches(from, text, ev))
                                return (true, ev.Text);
                    return (false, "");
                };
                break;
            }
            case "item":
            {
                var item = filter is JsonObject o ? o["item"] : filter is JsonArray a && a.Count > 0 ? a[0] : filter;
                var nV = filter is JsonObject o2 ? (o2["n"] ?? o2["count"]) : filter is JsonArray a2 && a2.Count > 1 ? a2[1] : null;
                var name = AsString(item) ?? throw Bug("item name must be a string");
                var (id, dmg) = ResolveItem(name);
                int n = AsNumber(nV) is { } nn ? (int)nn : _host.World.Inventory.CountItem(id, dmg) + 1;
                check = _ => { int cnt = _host.World.Inventory.CountItem(id, dmg); return (cnt >= n, $"{cnt} {name}"); };
                break;
            }
            case "block":
            {
                var p = PosOf(filter) ?? throw Bug("waitfor('block', {x,y,z,name}) needs a position");
                var want = Field(filter, "name");
                int start = _host.World.GetBlock(p.X, p.Y, p.Z);
                check = _ =>
                {
                    int id = _host.World.GetBlock(p.X, p.Y, p.Z);
                    var name = Blocks.Get(id).Name;
                    return (want != null ? string.Equals(name, Blocks.ByName(want)?.Name ?? want, StringComparison.OrdinalIgnoreCase) : id != start, name);
                };
                break;
            }
            case "day": check = _ => (!_host.World.IsNight, "day"); break;
            case "night": check = _ => (_host.World.IsNight, "night"); break;
            default:
                if (Signals.Has(kindRaw!.Trim()))
                {
                    var topic = kindRaw.Trim();
                    check = since =>
                    {
                        lock (_lock)
                            foreach (var (seq, ev) in _events)
                                if (seq > since && ev.Matches(topic)) return (true, ev.Text);
                        return (false, "");
                    };
                    break;
                }
                throw Bug($"waitfor kind '{kind}'? use chat|item|block|day|night|danger|join or a signal");
        }
        long since;
        lock (_lock) { since = _eventSeq; _waitingFor = kind; }
        try
        {
            DateTimeOffset? deadline = null;
            return RunAction("waitfor", false, async ct =>
            {
                var end = deadline ??= _time.GetUtcNow() + TimeSpan.FromSeconds(s);
                while (true)
                {
                    var (hit, msg) = check(since);
                    if (hit) return ActResult.Success(msg);
                    if (_time.GetUtcNow() >= end) return ActResult.Fail($"timeout {s:0.#}s waiting for {kind}");
                    await Task.Delay(TimeSpan.FromMilliseconds(100), _time, ct).ConfigureAwait(false);
                }
            });
        }
        finally { lock (_lock) _waitingFor = null; }
    }

    /// <summary>cp"label" marks a checkpoint (carry snapshot for resume_from); cp("label", fn) is a stage skipped when resuming past it.</summary>
    internal bool Cp(string raw, Action? stage)
    {
        SafePoint();
        if (string.IsNullOrEmpty(raw)) throw Bug("cp(\"label\") needs a label");
        var label = Short(raw, 40);
        if (_resumeTarget != null)
        {
            if (label != _resumeTarget) { if (stage != null) return false; }
            else _resumeTarget = null;
        }
        if (_resumeTarget == null) Mark(label, _runtime?.CarryJson());
        if (stage == null) return true;
        stage();
        TouchSegment();
        return true;
    }

    /// <summary>Records a passed checkpoint: the label, its snapshot (memory + the durable store, so a restart resumes here).</summary>
    internal void Mark(string label, string? snapshot)
    {
        Checkpoint = label;
        lock (_lock) _passed.Add(label);
        if (snapshot == null) return;
        lock (_lock) CheckpointCarry[label] = snapshot;
        try { _host.Checkpoints?.Save(Name, Lane, label, snapshot); }
        catch (Exception ex) { _host.Log?.Invoke($"script {Name}: checkpoint save failed: {ex.Message}"); }
    }

    /// <summary>Whose checkpoints these are: the lane's body, else this process's account (a hive clone's own row key).</summary>
    internal string Lane => _host.Body ?? _host.Username;

    /// <summary>A machine resuming at its checkpoint ends the fast-forward (actions run again from here).</summary>
    internal void ReachedResumeTarget(string label)
    {
        if (_resumeTarget == label) _resumeTarget = null;
    }

    /// <summary>The live <c>carry</c> as JSON (machine snapshots).</summary>
    internal string? CurrentCarry() => _runtime?.CarryJson();

    private int _timeouts;

    /// <summary>
    /// <c>timeout(sec, fn)</c>: runs <paramref name="body"/>; when the deadline passes first, the in-flight interruptible action is
    /// cut and the body unwound at the next safe point. Null = timed out.
    /// </summary>
    internal T? WithTimeout<T>(double seconds, Func<T> body) where T : class
    {
        var owner = new object();
        var topic = "timeout#" + (++_timeouts).ToString(CultureInfo.InvariantCulture);
        Signals.AddDeadline(topic, TimeSpan.FromSeconds(Math.Clamp(seconds, 0.05, _o.MaxWaitSeconds * 6.0)), owner);
        AddHandler(new Handler("timeout", e => e.Topic == topic, _ => throw new Sdk.ScriptUnwind(owner, "", "timeout"), Cuts: true, Owner: owner));
        try { return body(); }
        catch (Sdk.ScriptUnwind u) when (ReferenceEquals(u.Target, owner)) { return null; }
        finally
        {
            RemoveHandlers(owner);
            Signals.RemoveOwner(owner);
        }
    }

    internal void Log(string msg)
    {
        msg = Short(msg, 160);
        lock (_lock) { Logs.Add(msg); if (Logs.Count > 20) Logs.RemoveAt(0); }
        _host.Log?.Invoke($"script {Name}: {msg}");
    }

    internal void Progress(double? pct, string? note)
    {
        if (pct is { } p && double.IsFinite(p)) ProgressPct = Math.Clamp(p, 0, 100);
        if (note != null) ProgressNote = Short(note, 40);
    }

    internal bool Wake(string? reason)
    {
        SafePoint();
        Locate();
        return WakeBrain(reason ?? "script asks for you");
    }

    /// <summary><c>fail(msg)</c>: ends the script as failed (nil msg = the last failed action's reason).</summary>
    internal Exception Fail(string? msg)
    {
        Locate();
        // Live 10-27: scans "returned" their data through fail ("4,6=cobblestone 5,6=water ..."): a run that worked
        // then read as failed. Say so, so the next script uses return / log.
        if (msg != null && Regex.Matches(msg, @"-?\d+(?:,-?\d+){1,2}\s*[=:]\s*[a-z_]+").Count >= 2)
            return new ScriptFailException("fail() is for errors, not data: return \"...\" for results, log() for debugging: " + Short(msg, 100));
        return new ScriptFailException(msg == null ? LastFailure ?? "fail()" : Short(msg, 120));
    }

    internal static ScriptFailException Bug(string msg) => new(msg);

    // ------------------------------------------------------------------ queries

    internal int FreeSlots() { SafePoint(); return _host.World.Inventory.EmptySlotCount(); }

    internal int CountItem(string name)
    {
        SafePoint();
        var (id, dmg) = ResolveItem(name);
        return _host.World.Inventory.CountItem(id, dmg);
    }

    internal (int Id, int? Dmg) ResolveItem(string? name)
    {
        if (name == null) { Locate(); throw Bug("item name must be a string"); }
        if (Items.ByNameWithDamage(name) is { } i) return (i.Item.Id, i.Damage >= 0 ? i.Damage : null);
        if (Blocks.ByName(name) is { } b) return (Items.ItemFormOf(b.Id), null);
        Locate();
        throw Bug($"unknown item '{Short(name, 30)}'");
    }

    /// <summary><c>me.x</c>: hp, food, dim, pos, x, y, z, night, name, body.</summary>
    internal JsonNode? Me(string key)
    {
        SafePoint();
        var w = _host.World;
        var p = w.Player.Position;
        int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y + 1e-6), z = (int)Math.Floor(p.Z);
        return key switch
        {
            "hp" => w.Player.Health,
            "food" => w.Player.Food,
            "dim" => w.Dimension,
            "pos" => PosNode(x, y, z),
            "x" => x,
            "y" => y,
            "z" => z,
            "night" => w.IsNight,
            "name" => _host.Username,
            "body" => _host.Body ?? "main",
            _ => null,
        };
    }

    internal string BlockAt(BlockPos p)
    {
        SafePoint();
        return Blocks.Get(_host.World.GetBlock(p.X, p.Y, p.Z)).Name;
    }

    /// <summary><c>near("chest", r)</c>: nearest unprotected block of that name in view, a named entity, else memory ({x,y,z,name,d}).</summary>
    internal JsonObject? Nearest(string kind, double? radius)
    {
        SafePoint();
        double r = radius is { } rr ? Math.Clamp(rr, 1, 64) : 24;
        var w = _host.World;
        var feet = w.Player.Position;
        if (Blocks.ByName(kind) is { } b && !string.Equals(kind, "player", StringComparison.OrdinalIgnoreCase))
        {
            var hit = w.FindBlocks(feet, r, (id, _) => id == b.Id, 64).Where(f => !_host.IsProtected(f.Pos, f.Id))
                .OrderBy(f => f.DistanceSquared).Cast<FoundBlock?>().FirstOrDefault();
            if (hit is { } h) return PosNode(h.Pos.X, h.Pos.Y, h.Pos.Z, b.Name, Math.Sqrt(h.DistanceSquared));
        }
        var ent = w.Entities.Nearby(feet, r, x => x.Name != null && string.Equals(x.Name, kind, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (ent != null)
            return PosNode((int)Math.Floor(ent.Position.X), (int)Math.Floor(ent.Position.Y), (int)Math.Floor(ent.Position.Z), ent.Name, ent.Position.DistanceTo(feet));
        if (_host.NearestKnown(kind) is { } k)
            return PosNode(k.X, k.Y, k.Z, kind, new Vec3d(k.X + 0.5, k.Y, k.Z + 0.5).DistanceTo(feet));
        return null;
    }

    internal string? Where(string item)
    {
        SafePoint();
        try { return _host.WhereIs(item, _stopCts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return null; }
    }

    internal long TimeOfDay() { SafePoint(); return _host.World.TimeOfDay; }
    internal bool Night() { SafePoint(); return _host.World.IsNight; }

    internal JsonArray Players()
    {
        SafePoint();
        var w = _host.World;
        var feet = w.Player.Position;
        var names = (_host.OnlinePlayers?.Invoke() ?? w.Entities.OnlinePlayers)
            .Where(n => !string.Equals(n, _host.Username, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        var list = new JsonArray();
        foreach (var n in names)
        {
            var o = new JsonObject { ["name"] = n };
            var ent = w.Entities.Nearby(feet, 256, x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (ent != null) o["d"] = Math.Round(ent.Position.DistanceTo(feet), 1);
            list.Add(o);
        }
        return list;
    }

    internal static JsonObject PosNode(int x, int y, int z, string? name = null, double? d = null)
    {
        var t = new JsonObject { ["x"] = x, ["y"] = y, ["z"] = z };
        if (name != null) t["name"] = name;
        if (d is { } dist) t["d"] = Math.Round(dist, 1);
        return t;
    }

    // ------------------------------------------------------------------ neutral value helpers

    internal static string? AsString(JsonNode? v) => v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String ? jv.GetValue<string>() : null;

    internal static double? AsNumber(JsonNode? v)
    {
        if (v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.Number) return null;
        if (jv.TryGetValue<double>(out var d)) return d;
        if (jv.TryGetValue<float>(out var f)) return f;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<short>(out var sh)) return sh;
        if (jv.TryGetValue<byte>(out var b)) return b;
        if (jv.TryGetValue<decimal>(out var m)) return (double)m;
        return double.TryParse(jv.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    internal static string? Field(JsonNode? t, string key) => t is JsonObject o && o[key] is { } v && v.GetValueKind() != JsonValueKind.Null ? Str(v) : null;

    /// <summary>A position from "x,y,z", [x,y,z] or {x,y,z}.</summary>
    internal static BlockPos? PosOf(JsonNode? v)
    {
        if (AsString(v) is { } s)
        {
            var parts = s.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 3 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z) ? new BlockPos(x, y, z) : null;
        }
        if (v is JsonObject o && AsNumber(o["x"]) is { } ox && AsNumber(o["y"]) is { } oy && AsNumber(o["z"]) is { } oz)
            return new BlockPos((int)Math.Floor(ox), (int)Math.Floor(oy), (int)Math.Floor(oz));
        if (v is JsonArray a && a.Count == 3 && AsNumber(a[0]) is { } ax && AsNumber(a[1]) is { } ay && AsNumber(a[2]) is { } az)
            return new BlockPos((int)Math.Floor(ax), (int)Math.Floor(ay), (int)Math.Floor(az));
        return null;
    }

    internal static string FmtPos(BlockPos p) => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}");
}
