using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lua;
using Lua.Standard;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Scripting;

/// <summary>The script-facing Lua API (see <see cref="ScriptPrompts.CheatSheet"/>). Only these globals exist besides safe Lua.</summary>
public sealed partial class ScriptSession
{
    private sealed record Handler(ScriptEventKind Kind, string? From, string? Text, LuaFunction Fn);

    private readonly List<Handler> _handlers = new();
    private readonly Queue<(Handler H, ScriptEvent E)> _pendingHandlers = new();
    private readonly List<(long Seq, ScriptEvent E)> _events = new();
    private long _eventSeq;
    private string? _waitingFor;
    private readonly Queue<DateTimeOffset> _said = new();
    private int _sayRun;
    private DateTimeOffset? _lastSay;
    private DateTimeOffset _lastWake = DateTimeOffset.MinValue;

    /// <summary>Params that take a position: <c>go{1,64,2}</c>, <c>dig{me.pos}</c>, <c>place{"torch", {x=1,y=2,z=3}}</c>.</summary>
    private static readonly HashSet<string> PosParams = new(StringComparer.OrdinalIgnoreCase) { "to", "at", "pos" };

    /// <summary>Short names for common tools/skills (terse scripts). Full tool names work too, except <c>goto</c> (a Lua keyword).</summary>
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

    /// <summary>Globals the API owns (skills with these names are only reachable via skill()).</summary>
    public static readonly IReadOnlySet<string> BuiltinNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "skill", "inv", "me", "block", "near", "where", "time", "night", "players", "say", "emote", "wait", "waitfor", "log", "print",
        "prog", "cp", "on", "ask", "fail", "wake", "carry", "args", "os", "string", "table", "math",
        "retry", "upto", "expect", "room", "ensure",
    };

    private static readonly Regex Ident = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly HashSet<string> LuaKeywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in", "local", "nil", "not", "or",
        "repeat", "return", "then", "true", "until", "while",
    };

    // ------------------------------------------------------------------ events (any thread)

    /// <summary>True if this script would take <paramref name="e"/> itself (an on() handler or a waitfor/ask listening): the brain stays asleep.</summary>
    public bool HasHandler(ScriptEvent e)
    {
        lock (_lock)
        {
            if (!IsLive || e.Kind is ScriptEventKind.Stop or ScriptEventKind.Join) return false;
            if (e.Kind == ScriptEventKind.Chat && _waitingFor is "chat" or "ask") return true;
            if (e.Kind == ScriptEventKind.Danger && _waitingFor == "danger") return true;
            return _handlers.Any(x => x.Kind == e.Kind && Matches(x.From, x.Text, e));
        }
    }

    /// <summary>
    /// An event while the script runs: feeds waitfor and on() handlers; otherwise applies the interrupt policy
    /// (danger/stop pause and abort the interruptible action; addressed chat pauses with auto-resume).
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
                h = _handlers.FirstOrDefault(x => x.Kind == e.Kind && Matches(x.From, x.Text, e));
            if (h != null)
            {
                _pendingHandlers.Enqueue((h, e));
                if (e.Kind == ScriptEventKind.Danger && !_currentAtomic && _abortCts != null) { _abortedForPause = true; cut = _abortCts; }
            }
        }
        if (h != null)
        {
            try { cut?.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }
        switch (e.Kind)
        {
            case ScriptEventKind.Danger: RequestPause(PauseKind.Danger, "danger: " + Short(e.Text, 40), abort: true); break;
            case ScriptEventKind.Stop: RequestPause(PauseKind.Stop, $"{e.Player ?? "player"} said stop", abort: true); break;
            case ScriptEventKind.Chat when _o.PauseOnChat: RequestPause(PauseKind.Chat, $"chat from {e.Player ?? "someone"}", abort: false); break;
        }
    }

    private static bool Matches(string? from, string? text, ScriptEvent e) =>
        (from == null || string.Equals(from, e.Player, StringComparison.OrdinalIgnoreCase)) &&
        (text == null || e.Text.Contains(text, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Runs an on() handler. Its return: nil = handled (brain stays asleep), "pause", "wake" (brain; script keeps going), "stop".</summary>
    private void RunHandler(Handler h, ScriptEvent e)
    {
        var ev = new LuaTable();
        ev["kind"] = e.Kind.ToString().ToLowerInvariant();
        ev["text"] = e.Text;
        if (e.Player is { } p) ev["from"] = p;
        var ret = _lua!.CallAsync(h.Fn, [ev], _hardCt).AsTask().GetAwaiter().GetResult();
        TouchSegment();
        var s = ret.Length > 0 && ret[0].TryRead<string>(out var str) ? str.Trim().ToLowerInvariant() : null;
        var kind = h.Kind.ToString().ToLowerInvariant();
        switch (s)
        {
            case "pause": RequestPause(h.Kind == ScriptEventKind.Danger ? PauseKind.Danger : PauseKind.Script, $"on('{kind}') passed on: {Short(e.Text, 40)}", abort: false); break;
            case "wake": WakeBrain($"on('{kind}') passed on: {Short(e.Text, 60)}", force: true); break;
            case "stop": Stop($"stopped by on('{kind}')"); break;
        }
    }

    private bool WakeBrain(string reason, bool force = false)
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

    // ------------------------------------------------------------------ sandbox

    private LuaState BuildState()
    {
        var s = LuaState.Create();
        s.OpenBasicLibrary();
        s.OpenStringLibrary();
        s.OpenTableLibrary();
        s.OpenMathLibrary();
        var env = s.Environment;
        // No code loading, files, raw access, error swallowing (pcall would catch limits) or GC control.
        foreach (var k in new[] { "load", "loadfile", "loadstring", "dofile", "require", "collectgarbage", "rawset", "rawget", "rawequal", "rawlen",
                     "pcall", "xpcall", "module", "package", "io", "os", "debug", "coroutine", "print", "_VERSION" })
            env[k] = LuaValue.Nil;
        if (env["string"].TryRead<LuaTable>(out var str))
        {
            str["dump"] = LuaValue.Nil;
            var rep = str["rep"];
            int max = Math.Max(1000, _o.MaxStringLength);
            str["rep"] = F("rep", c =>
            {
                double n = Num(c, 1, 0);
                int len = c.GetArgument(0).TryRead<string>(out var x) ? x.Length : 0;
                int sep = c.ArgumentCount > 2 && c.GetArgument(2).TryRead<string>(out var y) ? y.Length : 0;
                if (n * (len + sep) > max) throw Bug("string too long");
                var r = _lua!.CallAsync(rep, c.Arguments, _hardCt).AsTask().GetAwaiter().GetResult();
                return c.Return(r.Length > 0 ? r[0] : LuaValue.Nil);
            });
            var format = str["format"];
            str["format"] = F("format", c =>
            {
                if (c.GetArgument(0).TryRead<string>(out var f) && Regex.IsMatch(f, @"%[-+ #0]*\d{3,}")) throw Bug("format width too big");
                var r = _lua!.CallAsync(format, c.Arguments, _hardCt).AsTask().GetAwaiter().GetResult();
                return c.Return(r.Length > 0 ? r[0] : LuaValue.Nil);
            });
        }
        var os = new LuaTable
        {
            ["time"] = F("time", c => c.Return((double)_time.GetUtcNow().ToUnixTimeSeconds())),
            ["clock"] = F("clock", c => c.Return(_active.Elapsed.TotalSeconds)),
        };
        env["os"] = Locked(os);
        s.SetHook(new LuaFunction("hook", (_, _) => { Hook(); return new ValueTask<int>(0); }), "", HookEvery);
        _lua = s;
        Install(env);
        // Reliability helpers (retry/upto/expect/room/ensure) in their own chunk: user line numbers stay intact.
        s.DoStringAsync(ScriptPrelude.Lua, "prelude", _hardCt).AsTask().GetAwaiter().GetResult();
        foreach (var kv in env) if (kv.Key.TryRead<string>(out var name)) _baseGlobals.Add(name);
        return s;
    }

    private static LuaFunction F(string name, Func<LuaFunctionExecutionContext, int> body) =>
        new(name, (c, _) => new ValueTask<int>(body(c)));

    /// <summary>A read-only view: writes fail, rawset is gone and setmetatable refuses the protected metatable.</summary>
    private static LuaTable Locked(LuaTable inner, LuaFunction? call = null, LuaFunction? index = null)
    {
        var view = new LuaTable();
        var mt = new LuaTable
        {
            ["__index"] = index != null ? (LuaValue)index : inner,
            ["__newindex"] = new LuaFunction("ro", (_, _) => throw new ScriptFailException("read-only")),
            ["__metatable"] = "locked",
        };
        if (call != null) mt["__call"] = call;
        view.Metatable = mt;
        return view;
    }

    // ------------------------------------------------------------------ install

    private void Install(LuaTable env)
    {
        var taken = new HashSet<string>(BuiltinNames, StringComparer.Ordinal);
        var byName = _host.Actions.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        void ActionFn(string global, ScriptAction a)
        {
            if (!Ident.IsMatch(global) || LuaKeywords.Contains(global) || !taken.Add(global)) return;
            env[global] = F(global, c => Ret(c, RunAction(a.Name, a.Atomic, ct => _host.Act(a.Name, MapArgs(Params(a), c), ct))));
        }
        void SkillFn(string global, string skill)
        {
            if (!Ident.IsMatch(global) || LuaKeywords.Contains(global) || !taken.Add(global)) return;
            env[global] = F(global, c => Ret(c, RunAction(skill, _host.SkillIsAtomic(skill), ct => _host.Skill(skill, MapArgs(SkillParams(skill), c), ct))));
        }

        IReadOnlyCollection<string> skills;
        try { skills = _host.Skills(); } catch (Exception) { skills = []; }
        foreach (var (alias, target) in Aliases)
        {
            if (byName.TryGetValue(target, out var a)) ActionFn(alias, a);
            else if (skills.Contains(target)) SkillFn(alias, target);
        }
        foreach (var a in _host.Actions) ActionFn(a.Name, a);
        foreach (var sk in skills) SkillFn(sk, sk);

        env["skill"] = F("skill", c =>
        {
            if (!c.GetArgument(0).TryRead<string>(out var asked)) throw Bug("skill(name, {args}) needs a name");
            var name = asked.Trim();
            // skill("toss", ..) / skill("go", ..): an alias works here too; an action name runs the action.
            if (!skills.Contains(name) && Aliases.TryGetValue(name, out var target)) name = target;
            if (!skills.Contains(name) && byName.TryGetValue(name, out var act))
                return Ret(c, RunAction(act.Name, act.Atomic, ct => _host.Act(act.Name, MapArgs(Params(act), c, skip: 1), ct)));
            if (!skills.Contains(name) && skills.Count > 0)
            {
                var near = Closest(name, skills.Concat(byName.Keys).Concat(Aliases.Keys));
                return Ret(c, RunAction(name, true, _ => Task.FromResult(ActResult.Fail($"no skill '{asked}'" +
                    (near.Count > 0 ? $"; closest: {string.Join(", ", near)}" : "; use find_skill")))));
            }
            return Ret(c, RunAction(name, _host.SkillIsAtomic(name), ct => _host.Skill(name, MapArgs(SkillParams(name), c, skip: 1), ct)));
        });

        // ---- queries
        var invFns = new LuaTable
        {
            ["free"] = F("free", c => { SafePoint(); return c.Return((double)_host.World.Inventory.EmptySlotCount()); }),
            ["has"] = F("has", c => { SafePoint(); return c.Return(CountItem(c.GetArgument(0)) >= Num(c, 1, 1)); }),
        };
        env["inv"] = Locked(invFns, call: F("inv", c => { SafePoint(); return c.Return((double)CountItem(c.GetArgument(1))); }));

        env["me"] = Locked(new LuaTable(), index: F("me", c =>
        {
            SafePoint();
            var key = c.GetArgument(1).TryRead<string>(out var k) ? k : "";
            var w = _host.World;
            var p = w.Player.Position;
            int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y + 1e-6), z = (int)Math.Floor(p.Z);
            return key switch
            {
                "hp" => c.Return((double)w.Player.Health),
                "food" => c.Return((double)w.Player.Food),
                "dim" => c.Return((double)w.Dimension),
                "pos" => c.Return(PosTable(x, y, z)),
                "x" => c.Return((double)x),
                "y" => c.Return((double)y),
                "z" => c.Return((double)z),
                "night" => c.Return(w.IsNight),
                "name" => c.Return(_host.Username),
                _ => c.Return(LuaValue.Nil),
            };
        }));
        env["block"] = F("block", c =>
        {
            SafePoint();
            var p = PosArgs(c, 0) ?? throw Bug("block(x,y,z) needs a position");
            return c.Return(Blocks.Get(_host.World.GetBlock(p.X, p.Y, p.Z)).Name);
        });
        env["near"] = F("near", c => { SafePoint(); return c.Return(Nearest(c.GetArgument(0), c.ArgumentCount > 1 ? c.GetArgument(1) : LuaValue.Nil)); });
        env["where"] = F("where", c =>
        {
            SafePoint();
            if (!c.GetArgument(0).TryRead<string>(out var item)) throw Bug("where\"item\" needs an item name");
            string? r;
            try { r = _host.WhereIs(item, _stopCts.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { r = null; }
            return c.Return(r is null ? LuaValue.Nil : r);
        });
        env["time"] = F("time", c => { SafePoint(); return c.Return((double)_host.World.TimeOfDay); });
        env["night"] = F("night", c => { SafePoint(); return c.Return(_host.World.IsNight); });
        env["players"] = F("players", c => { SafePoint(); return c.Return(Players()); });

        // ---- control
        env["say"] = F("say", c => Ret(c, Chat(c.GetArgument(0), emote: false)));
        env["emote"] = F("emote", c => Ret(c, Chat(c.GetArgument(0), emote: true)));
        env["wait"] = F("wait", c =>
        {
            double s = Math.Clamp(Num(c, 0, 1), 0.05, _o.MaxWaitSeconds);
            return Ret(c, RunAction("wait", false, async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(s), _time, ct).ConfigureAwait(false);
                return ActResult.Success($"waited {s:0.#}s");
            }));
        });
        env["waitfor"] = F("waitfor", c => Ret(c, WaitFor(c.GetArgument(0), Arg(c, 1), Arg(c, 2))));
        env["ask"] = F("ask", c => c.Return(Ask(c.GetArgument(0), Arg(c, 1), Arg(c, 2))));
        env["wake"] = F("wake", c =>
        {
            SafePoint();
            Locate();
            return c.Return(WakeBrain(c.GetArgument(0).TryRead<string>(out var r) ? r : "script asks for you"));
        });
        var log = F("log", c =>
        {
            var msg = Short(string.Join(" ", c.Arguments.ToArray().Select(Str)), 160);
            lock (_lock) { Logs.Add(msg); if (Logs.Count > 20) Logs.RemoveAt(0); }
            _host.Log?.Invoke($"script {Name}: {msg}");
            return c.Return();
        });
        env["log"] = log;
        env["print"] = log;
        env["prog"] = F("prog", c =>
        {
            if (c.GetArgument(0).TryRead<double>(out var pct)) ProgressPct = Math.Clamp(pct, 0, 100);
            if (Arg(c, 1).Type != LuaValueType.Nil) ProgressNote = Short(Str(Arg(c, 1)), 40);
            return c.Return();
        });
        env["cp"] = F("cp", c => c.Return(CheckpointCall(c.GetArgument(0), Arg(c, 1))));
        env["on"] = F("on", c =>
        {
            On(c.GetArgument(0), Arg(c, 1), Arg(c, 2));
            return c.Return();
        });
        env["fail"] = F("fail", c =>
        {
            Locate();
            var m = Arg(c, 0);
            if (m.Type == LuaValueType.Table) throw Bug("fail(msg) takes a string reason; return a result as a string instead");
            // Live 10-27: scans "returned" their data through fail ("4,6=cobblestone 5,6=water ..."): a run that worked
            // then read as failed. Say so, so the next script uses return / log.
            if (m.Type == LuaValueType.String && Regex.Matches(m.Read<string>(), @"-?\d+(?:,-?\d+){1,2}\s*[=:]\s*[a-z_]+").Count >= 2)
                throw new ScriptFailException("fail() is for errors, not data: return \"...\" for results, log() for debugging: " + Short(Str(m), 100));
            throw new ScriptFailException(m.Type == LuaValueType.Nil ? LastFailure ?? "fail()" : Short(Str(m), 120));
        });
        env["carry"] = FromJson(Request.CarryJson) ?? new LuaTable();
        env["args"] = FromJson(Request.ArgsJson) ?? new LuaTable();
    }

    private static LuaValue Arg(LuaFunctionExecutionContext c, int i) => i < c.ArgumentCount ? c.GetArgument(i) : LuaValue.Nil;

    private static int Ret(LuaFunctionExecutionContext c, ActResult r) => c.Return(r.Ok, r.Msg ?? "", (double)CountOf(r.Msg));

    private static ScriptFailException Bug(string msg) => new(msg);

    // ------------------------------------------------------------------ actions

    /// <summary>
    /// One host action: safe point → limits → run (blocking this script thread only) → safe point. An action cut short by a
    /// danger pause is re-run after resume, so interrupts are invisible to the script.
    /// </summary>
    private ActResult RunAction(string label, bool atomic, Func<CancellationToken, Task<ActResult>> run)
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
        if (Actions >= _o.MaxActions) Limit($"action limit ({_o.MaxActions})");
        Actions++;
        ActResult r;
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
        SafePoint();
        if (!r.Ok) { LastFailure = $"{label}: {Short(OneLine(r.Msg), 80)}"; _lastFailureMsg = r.Msg; }
        LastActionFailed = !r.Ok;
        SnapshotVars();
        TouchSegment();
        return r;
    }

    private static readonly Regex NumRx = new(@"(?<![\d,.\-])(\d+)(?![\d,])", RegexOptions.Compiled);

    /// <summary>First plain integer of a result ("mined 12 cobblestone (inv 44)" → 12); coordinates are skipped.</summary>
    public static int CountOf(string? msg)
    {
        if (string.IsNullOrEmpty(msg)) return 0;
        var m = NumRx.Match(msg);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    private ActResult Chat(LuaValue text, bool emote)
    {
        var t = Str(text).Replace('\r', ' ').Replace('\n', ' ').Trim();
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

    /// <summary>ask(player, q, sec) → the player's reply or nil. ask("brain", q) pauses and wakes the brain (needs_input); returns its answer.</summary>
    private LuaValue Ask(LuaValue player, LuaValue question, LuaValue seconds)
    {
        var q = Regex.Replace(Short(Str(question).Replace('\n', ' ').Trim(), 180), @"(^|\s)/+", "$1");
        if (q.Length == 0) throw Bug("ask(player, question, sec) needs a question");
        string? who = player.TryRead<string>(out var pl) && pl.Length > 0 ? pl : null;
        if (who != null && who.Equals("brain", StringComparison.OrdinalIgnoreCase))
        {
            SafePoint();
            Locate();
            var answer = PauseHere(PauseKind.Script, "needs_input: " + q);
            return answer is null ? LuaValue.Nil : answer;
        }
        double s = Math.Clamp(seconds.TryRead<double>(out var sec) ? sec : 60, 5, Math.Min(180, _o.MaxWaitSeconds));
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
        return reply is null ? LuaValue.Nil : reply;
    }

    private ActResult WaitFor(LuaValue kindV, LuaValue filter, LuaValue seconds)
    {
        var kind = kindV.TryRead<string>(out var k0) ? k0.Trim().ToLowerInvariant() : "";
        double s = Math.Clamp(seconds.TryRead<double>(out var sec) ? sec : 60, 0.1, _o.MaxWaitSeconds);
        string? from = Field(filter, "from") ?? Field(filter, "player"), text = Field(filter, "text");
        Func<long, (bool Hit, string Msg)> check;
        switch (kind)
        {
            case "chat" or "danger" or "join":
            {
                var k = kind switch { "chat" => ScriptEventKind.Chat, "danger" => ScriptEventKind.Danger, _ => ScriptEventKind.Join };
                if (filter.TryRead<string>(out var fs)) { if (kind == "join") from = fs; else text = fs; }
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
                var item = filter.Type == LuaValueType.Table ? Get(filter, "item", 1) : filter;
                var nV = filter.Type == LuaValueType.Table ? Get(filter, "n", 2) : LuaValue.Nil;
                var (id, dmg) = ResolveItem(item);
                int n = nV.TryRead<double>(out var nn) ? (int)nn : _host.World.Inventory.CountItem(id, dmg) + 1;
                check = _ => { int cnt = _host.World.Inventory.CountItem(id, dmg); return (cnt >= n, $"{cnt} {Str(item)}"); };
                break;
            }
            case "block":
            {
                var p = PosOf(filter) ?? throw Bug("waitfor('block', {x,y,z,name=}) needs a position");
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
            default: throw Bug($"waitfor kind '{kind}'? use chat|item|block|day|night|danger|join");
        }
        long since;
        lock (_lock) { since = _eventSeq; _waitingFor = kind; }
        try
        {
            return RunAction("waitfor", false, async ct =>
            {
                var end = _time.GetUtcNow() + TimeSpan.FromSeconds(s);
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
    private LuaValue CheckpointCall(LuaValue labelV, LuaValue fn)
    {
        SafePoint();
        if (!labelV.TryRead<string>(out var raw) || raw.Length == 0) throw Bug("cp\"label\" needs a label");
        var label = Short(raw, 40);
        bool stage = fn.Type == LuaValueType.Function;
        if (_resumeTarget != null)
        {
            if (label != _resumeTarget) { if (stage) return false; }
            else _resumeTarget = null;
        }
        if (_resumeTarget == null)
        {
            Checkpoint = label;
            lock (_lock) _passed.Add(label);
            try { if (SerializeCarry() is { } snap) lock (_lock) CheckpointCarry[label] = snap; } catch (Exception) { }
        }
        if (!stage) return true;
        _lua!.CallAsync(fn, [], _hardCt).AsTask().GetAwaiter().GetResult();
        TouchSegment();
        return true;
    }

    private void On(LuaValue kindV, LuaValue a, LuaValue b)
    {
        var kind = kindV.TryRead<string>(out var kk) ? kk.Trim().ToLowerInvariant() : "";
        var fnV = b.Type == LuaValueType.Nil ? a : b;
        var filter = b.Type == LuaValueType.Nil ? LuaValue.Nil : a;
        if (!fnV.TryRead<LuaFunction>(out var fn)) throw Bug("on(kind, [filter,] fn) needs a function");
        var k = kind switch
        {
            "danger" => ScriptEventKind.Danger,
            "chat" => ScriptEventKind.Chat,
            _ => throw Bug($"on('{kind}')? use danger|chat"),
        };
        string? from = Field(filter, "from") ?? Field(filter, "player"), text = Field(filter, "text");
        if (filter.TryRead<string>(out var fs)) text = fs;
        lock (_lock)
        {
            if (_handlers.Count >= 8) throw Bug("too many on() handlers");
            _handlers.Add(new Handler(k, from, text, fn));
        }
    }

    // ------------------------------------------------------------------ queries

    private int CountItem(LuaValue item)
    {
        var (id, dmg) = ResolveItem(item);
        return _host.World.Inventory.CountItem(id, dmg);
    }

    private (int Id, int? Dmg) ResolveItem(LuaValue item)
    {
        if (!item.TryRead<string>(out var name)) { Locate(); throw Bug("item name must be a string"); }
        if (Items.ByNameWithDamage(name) is { } i) return (i.Item.Id, i.Damage >= 0 ? i.Damage : null);
        if (Blocks.ByName(name) is { } b) return (Items.ItemFormOf(b.Id), null);
        Locate();
        throw Bug($"unknown item '{Short(name, 30)}'");
    }

    private LuaValue Nearest(LuaValue kindV, LuaValue radiusV)
    {
        if (!kindV.TryRead<string>(out var kind)) throw Bug("near\"kind\" needs a block/player/place name");
        double r = radiusV.TryRead<double>(out var rr) ? Math.Clamp(rr, 1, 64) : 24;
        var w = _host.World;
        var feet = w.Player.Position;
        if (Blocks.ByName(kind) is { } b && !string.Equals(kind, "player", StringComparison.OrdinalIgnoreCase))
        {
            var hit = w.FindBlocks(feet, r, (id, _) => id == b.Id, 64).Where(f => !_host.IsProtected(f.Pos, f.Id))
                .OrderBy(f => f.DistanceSquared).Cast<FoundBlock?>().FirstOrDefault();
            if (hit is { } h) return PosTable(h.Pos.X, h.Pos.Y, h.Pos.Z, b.Name, Math.Sqrt(h.DistanceSquared));
        }
        var ent = w.Entities.Nearby(feet, r, x => x.Name != null && string.Equals(x.Name, kind, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (ent != null)
            return PosTable((int)Math.Floor(ent.Position.X), (int)Math.Floor(ent.Position.Y), (int)Math.Floor(ent.Position.Z), ent.Name, ent.Position.DistanceTo(feet));
        if (_host.NearestKnown(kind) is { } k)
            return PosTable(k.X, k.Y, k.Z, kind, new Vec3d(k.X + 0.5, k.Y, k.Z + 0.5).DistanceTo(feet));
        return LuaValue.Nil;
    }

    private LuaTable Players()
    {
        var w = _host.World;
        var feet = w.Player.Position;
        var names = (_host.OnlinePlayers?.Invoke() ?? w.Entities.OnlinePlayers)
            .Where(n => !string.Equals(n, _host.Username, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        var list = new LuaTable();
        int i = 1;
        foreach (var n in names)
        {
            var o = new LuaTable { ["name"] = n };
            var ent = w.Entities.Nearby(feet, 256, x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (ent != null) o["d"] = Math.Round(ent.Position.DistanceTo(feet), 1);
            list[i++] = o;
        }
        return list;
    }

    private static LuaTable PosTable(int x, int y, int z, string? name = null, double? d = null)
    {
        var t = new LuaTable { ["x"] = x, ["y"] = y, ["z"] = z };
        if (name != null) t["name"] = name;
        if (d is { } dist) t["d"] = Math.Round(dist, 1);
        return t;
    }

    // ------------------------------------------------------------------ conversions

    internal static string Str(LuaValue v) => v.Type switch
    {
        LuaValueType.Nil => "",
        LuaValueType.String => v.Read<string>(),
        LuaValueType.Number => Fmt(v.Read<double>()),
        LuaValueType.Boolean => v.Read<bool>() ? "true" : "false",
        _ => v.ToString(),
    };

    private static string Fmt(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("0.###", CultureInfo.InvariantCulture);

    private static double Num(LuaFunctionExecutionContext c, int i, double def) =>
        i < c.ArgumentCount && c.GetArgument(i).TryRead<double>(out var d) ? d : def;

    private static LuaValue Get(LuaValue t, string key, int index)
    {
        if (!t.TryRead<LuaTable>(out var tab)) return LuaValue.Nil;
        var v = tab[key];
        return v.Type != LuaValueType.Nil ? v : tab[index];
    }

    private static string? Field(LuaValue t, string key) =>
        t.TryRead<LuaTable>(out var tab) && tab[key].Type != LuaValueType.Nil ? Str(tab[key]) : null;

    /// <summary>A position from "x,y,z", {x,y,z} (array) or {x=,y=,z=}.</summary>
    private static BlockPos? PosOf(LuaValue v)
    {
        if (v.TryRead<string>(out var s))
        {
            var parts = s.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 3 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) && int.TryParse(parts[2], out var z)
                ? new BlockPos(x, y, z) : null;
        }
        if (!v.TryRead<LuaTable>(out var t)) return null;
        if (t["x"].TryRead<double>(out var ox) && t["y"].TryRead<double>(out var oy) && t["z"].TryRead<double>(out var oz))
            return new BlockPos((int)Math.Floor(ox), (int)Math.Floor(oy), (int)Math.Floor(oz));
        if (t.ArrayLength == 3 && t[1].TryRead<double>(out var ax) && t[2].TryRead<double>(out var ay) && t[3].TryRead<double>(out var az))
            return new BlockPos((int)Math.Floor(ax), (int)Math.Floor(ay), (int)Math.Floor(az));
        return null;
    }

    /// <summary>block(x,y,z) / block{x,y,z} / block(pos) / block"x,y,z".</summary>
    private static BlockPos? PosArgs(LuaFunctionExecutionContext c, int i)
    {
        if (c.ArgumentCount >= i + 3 && c.GetArgument(i).TryRead<double>(out var x) && c.GetArgument(i + 1).TryRead<double>(out var y)
            && c.GetArgument(i + 2).TryRead<double>(out var z))
            return new BlockPos((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
        return i < c.ArgumentCount ? PosOf(c.GetArgument(i)) : null;
    }

    private static string FmtPos(BlockPos p) => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}");

    private static IReadOnlyList<(string Name, string? Type)> Params(ScriptAction a) => a.Params.Select(p => (p, (string?)null)).ToList();

    private IReadOnlyList<(string Name, string? Type)> SkillParams(string skill)
    {
        var list = new List<(string, string?)>();
        if (_host.SkillSchema(skill) is { } schema && schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject())
                list.Add((p.Name, p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null));
        return list;
    }

    /// <summary>
    /// Lua call → JSON input. One table = its array part positional (by <paramref name="ps"/> order) + its named keys; otherwise
    /// the arguments themselves are positional. A to/at/pos param takes three numbers or a position table ("x,y,z"); an array
    /// param takes all remaining tables (<c>move{{item="log",n="all",to="wood"}}</c> → moves). <c>n</c> means count.
    /// </summary>
    private static JsonElement MapArgs(IReadOnlyList<(string Name, string? Type)> ps, LuaFunctionExecutionContext c, int skip = 0)
    {
        var o = new JsonObject();
        var pos = new List<LuaValue>();
        LuaTable? named = null;
        if (c.ArgumentCount == skip + 1 && c.GetArgument(skip).TryRead<LuaTable>(out var t) && !(ps.Count > 0 && PosParams.Contains(ps[0].Name) && IsPosTable(t)))
        {
            named = t;
            for (int i = 1; i <= 64 && t[i].Type != LuaValueType.Nil; i++) pos.Add(t[i]);
        }
        else
            for (int i = skip; i < c.ArgumentCount; i++) pos.Add(c.GetArgument(i));

        int pi = 0;
        for (int i = 0; i < pos.Count && pi < ps.Count;)
        {
            var (name, type) = ps[pi];
            var v = pos[i];
            if (type == "array" && v.Type == LuaValueType.Table && !IsPosTable(v.Read<LuaTable>()))
            {
                var arr = new JsonArray();
                for (; i < pos.Count; i++) arr.Add(ToNode(pos[i], 1));
                o[name] = arr;
                pi++;
                continue;
            }
            if (PosParams.Contains(name))
            {
                if (i + 2 < pos.Count && pos[i].Type == LuaValueType.Number && pos[i + 1].Type == LuaValueType.Number && pos[i + 2].Type == LuaValueType.Number)
                {
                    o[name] = $"{Fmt(pos[i].Read<double>())},{Fmt(pos[i + 1].Read<double>())},{Fmt(pos[i + 2].Read<double>())}";
                    i += 3;
                    pi++;
                    continue;
                }
                if (v.Type == LuaValueType.Table && PosOf(v) is { } p) { o[name] = FmtPos(p); i++; pi++; continue; }
            }
            o[name] = ToNode(v, 1);
            i++;
            pi++;
        }
        if (named != null)
            foreach (var kv in named)
            {
                if (!kv.Key.TryRead<string>(out var key)) continue;
                if (key == "n" && ps.Any(p => p.Name == "count")) key = "count";
                o[key] = PosParams.Contains(key) && PosOf(kv.Value) is { } p ? FmtPos(p) : ToNode(kv.Value, 1);
            }
        return System.Text.Json.JsonSerializer.SerializeToElement(o);
    }

    private static bool IsPosTable(LuaTable t) =>
        PosOf(t) != null && (t["x"].Type == LuaValueType.Number || (t.ArrayLength == 3 && t.HashMapCount == 0));

    /// <summary>Lua value → JSON (functions dropped, depth ≤ 6). Pure arrays → arrays; nested <c>n</c> → count.</summary>
    private static JsonNode? ToNode(LuaValue v, int depth, bool args = true)
    {
        if (depth > 6) return null;
        switch (v.Type)
        {
            case LuaValueType.String: return JsonValue.Create(v.Read<string>());
            case LuaValueType.Boolean: return JsonValue.Create(v.Read<bool>());
            case LuaValueType.Number:
            {
                var d = v.Read<double>();
                return double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 1e15 ? JsonValue.Create((long)d) : JsonValue.Create(double.IsFinite(d) ? d : 0);
            }
            case LuaValueType.Table:
            {
                var t = v.Read<LuaTable>();
                if (t.ArrayLength > 0 && t.HashMapCount == 0)
                {
                    var ja = new JsonArray();
                    for (int i = 1; i <= t.ArrayLength && i <= 64; i++) ja.Add(ToNode(t[i], depth + 1, args));
                    return ja;
                }
                var jo = new JsonObject();
                int n = 0;
                foreach (var kv in t)
                {
                    if (n++ >= 32) break;
                    var key = kv.Key.Type == LuaValueType.String ? kv.Key.Read<string>() : Str(kv.Key);
                    if (args && key == "n" && depth >= 1) key = "count";
                    if (ToNode(kv.Value, depth + 1, args) is { } child) jo[key] = child;
                }
                return jo;
            }
            default: return null;
        }
    }

    private static LuaValue FromNode(JsonElement e, int depth = 0)
    {
        if (depth > 8) return LuaValue.Nil;
        switch (e.ValueKind)
        {
            case JsonValueKind.String: return e.GetString()!;
            case JsonValueKind.Number: return e.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Array:
            {
                var t = new LuaTable();
                int i = 1;
                foreach (var x in e.EnumerateArray()) t[i++] = FromNode(x, depth + 1);
                return t;
            }
            case JsonValueKind.Object:
            {
                var t = new LuaTable();
                foreach (var p in e.EnumerateObject()) t[p.Name] = FromNode(p.Value, depth + 1);
                return t;
            }
            default: return LuaValue.Nil;
        }
    }

    private static LuaTable? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && FromNode(doc.RootElement).TryRead<LuaTable>(out var t) ? t : null;
        }
        catch (Exception) { return null; }
    }

    private string? SerializeCarry()
    {
        if (_lua is null || !_lua.Environment["carry"].TryRead<LuaTable>(out var carry)) return null;
        var node = ToNode(carry, 0, args: false);
        var json = node is JsonObject ? node.ToJsonString() : "{}";
        return json.Length <= 2000 ? json : null;
    }

    /// <summary>The first few scalar globals the script made (Lua locals aren't visible without the debug library) and carry fields.</summary>
    private void SnapshotVars()
    {
        if (_lua is null) return;
        try
        {
            var parts = new List<string>();
            void Add(string name, LuaValue v)
            {
                if (parts.Count >= 4) return;
                if (v.Type is LuaValueType.Number or LuaValueType.Boolean) parts.Add($"{name}={Str(v)}");
                else if (v.Type == LuaValueType.String) parts.Add($"{name}='{Short(v.Read<string>(), 16)}'");
            }
            foreach (var kv in _lua.Environment)
                if (kv.Key.TryRead<string>(out var k) && !_baseGlobals.Contains(k)) Add(k, kv.Value);
            if (_lua.Environment["carry"].TryRead<LuaTable>(out var carry))
                foreach (var kv in carry)
                    if (kv.Key.TryRead<string>(out var k)) Add("carry." + k, kv.Value);
            Vars = parts.Count > 0 ? string.Join(" ", parts) : null;
        }
        catch (Exception) { }
    }

    internal static string Short(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 3)] + "...";
}
