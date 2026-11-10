using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

namespace MineClaw.Scripting.Runtime.Js;

/// <summary>
/// The JavaScript runtime (Jint, ES2023, strict). Execution model: host calls are SYNCHRONOUS on the script's own thread —
/// <c>mine("cobblestone", 16)</c> blocks this thread only, returns <c>{ok, msg, n}</c>, and is the safe point where pauses,
/// interrupts, handlers and machine transitions happen (exactly the Lua semantics). The script body runs inside an async
/// function, so <c>await</c> is accepted (and harmless) and top-level <c>return</c> works; promise jobs drain synchronously.
/// Sandbox: no CLR access, no require/import/eval/Function, strict mode, recursion, array-size and regex-time limits, plus the
/// session's budget hook between statements (statements, busy loop, memory between host calls, stop requests). Host signals
/// (stop, limits, unwinds) are CLR exceptions that no try/catch in the script can see. The SDK (<see cref="Sdk.SdkModules"/>) is
/// loaded before the script in its own sources ("sdk/…"), so script line numbers are untouched.
/// </summary>
internal sealed partial class JsRuntime(ScriptSession s) : IScriptRuntime, IPatchRuntime
{
    private Engine _e = null!;
    private ObjectInstance _carry = null!;
    private readonly List<JsValue> _fns = new();
    private readonly Dictionary<int, Crew.ILane> _lanes = new();
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);
    private int _nextLane;

    /// <summary>JS reserved words (never a game function's global name).</summary>
    internal static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "enum", "export", "extends",
        "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", "return", "super", "switch", "this",
        "throw", "true", "try", "typeof", "var", "void", "while", "with", "yield", "let", "static", "implements", "interface", "package",
        "private", "protected", "public", "await", "arguments", "eval", "undefined", "NaN", "Infinity",
    };

    private static readonly Regex IdentRx = new(@"^[A-Za-z_$][\w$]*$", RegexOptions.Compiled);

    /// <summary>The SDK modules parsed once per process (each run only evaluates them).</summary>
    private static readonly Lazy<IReadOnlyList<Prepared<Acornima.Ast.Script>>> Prepared = new(() =>
        Sdk.SdkModules.All.Select(m => Engine.PrepareScript(m.Source, "sdk/" + m.Name, strict: true)).ToList());

    public IReadOnlyList<JsonNode?> Run(string code, CancellationToken ct)
    {
        _e = new Engine(o =>
        {
            o.Strict();
            o.LimitRecursion(Math.Max(16, s.Options.MaxCallDepth));
            o.DisableStringCompilation();
            o.Constraint(new JsBudget(s));
            o.Constraints.MaxArraySize = (uint)Math.Max(1000, s.Options.MaxArraySize);
            o.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(Math.Max(50, s.Options.RegexTimeoutMs));
            o.Constraints.PromiseTimeout = TimeSpan.FromSeconds(2);
            o.Culture = CultureInfo.InvariantCulture;
        });
        Install();
        var result = _e.Evaluate(Wrap(code, s.Request.Role), "script");
        result = result.UnwrapIfPromise();
        return Returned(result);
    }

    public void Dispose()
    {
        foreach (var l in _lanes.Values) try { if (!l.Completion.IsCompleted) l.Cancel("coordinator ended"); } catch (Exception) { }
        try { EndCrew(); } catch (Exception) { }
        try { _e?.Dispose(); } catch (Exception) { }
    }

    /// <summary>
    /// The script body inside an async function (same line numbers: the opener shares line 1). After the top level: a crew lane
    /// calls its role; a script that defines <c>main</c> (and never calls it) runs <c>main(args)</c>.
    /// </summary>
    internal static string Wrap(string code, string? role)
    {
        string tail;
        if (role != null && IdentRx.IsMatch(role))
            tail = $"if (typeof {role} !== 'function') fail('no role function {role}'); return {role}(args);";
        else if (AutoMain(code))
            tail = "if (typeof main === 'function') return main(args);";
        else tail = "";
        return WrapPrefix + code + "\n;" + tail + "\n})()";
    }

    /// <summary>What <see cref="Wrap"/> puts before the script on line 1 (the debugger corrects line-1 columns by it).</summary>
    internal const string WrapPrefix = "(async function(){";

    private static readonly Regex MainCall = new(@"\bmain\s*\(", RegexOptions.Compiled);
    private static readonly Regex MainDef = new(@"\bfunction\s+main\s*\(|\bmain\s*=\s*(async\s*)?(\(|function|[A-Za-z_$])", RegexOptions.Compiled);

    private static bool AutoMain(string code) => MainDef.IsMatch(code) && MainCall.Matches(code).Count <= 1;

    private List<JsonNode?> Returned(JsValue v)
    {
        if (v.IsUndefined() || v.IsNull()) return [];
        if (OkMsg(v) is { } o)
            return [JsonValue.Create(o.Get("ok").AsBoolean()), JsonValue.Create(JsConvert.Text(o.Get("msg")))];
        if (v.IsString() || v.IsNumber() || v.IsBoolean()) return [JsConvert.ToNode(v, 0, args: false)];
        return [JsonValue.Create(ClippedJson(v))];
    }

    /// <summary>A returned {ok: bool, msg} object (not an array), else null.</summary>
    private static ObjectInstance? OkMsg(JsValue v) =>
        v is ObjectInstance o && v is not JsArray && o.Get("ok").IsBoolean() && o.HasProperty("msg") ? o : null;

    private static string ClippedJson(JsValue v)
    {
        var json = JsConvert.ToNode(v, 0, args: false)?.ToJsonString() ?? "";
        return json.Length <= 300 ? json : json[..297] + "...";
    }

    // ------------------------------------------------------------------ location, errors

    private static readonly Regex ScriptLoc = new(@"(?<![\w/])script:(\d+):\d+", RegexOptions.Compiled);

    public int CurrentLine()
    {
        try
        {
            var stack = _e.Advanced.StackTrace;
            _lastStack = stack; // the debugger's call stack at this host call (no extra work: the line came from it anyway)
            var m = ScriptLoc.Match(stack);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }
        catch (Exception) { return 0; }
    }

    public (string Reason, int Line)? Describe(Exception ex)
    {
        switch (ex)
        {
            case JavaScriptException je: return DescribeJsError(je);
            case PromiseRejectedException pr: return DescribeRejection(pr.RejectedValue);
            case RecursionDepthOverflowException: return ($"call depth limit ({s.Options.MaxCallDepth})", CurrentLine());
            case MemoryLimitExceededException me: return ("memory limit (" + ScriptSession.Short(ScriptSession.OneLine(me.Message), 80) + ")", CurrentLine());
            case Acornima.ParseErrorException pe: return ("syntax: " + Clean(pe.Description), pe.LineNumber);
            case JintException jx when jx is not JavaScriptException: return ("error: " + Clean(jx.Message), CurrentLine());
            default: return null;
        }
    }

    private (string Reason, int Line) DescribeJsError(JavaScriptException je)
    {
        var name = je.Error is ObjectInstance eo ? JsConvert.Text(eo.Get("name")) : "";
        int line = PatchLine(je.JavaScriptStackTrace) ?? JsErrorLine(je);
        var msg = Clean(je.Message);
        return ((name == "SyntaxError" || msg.StartsWith("Unexpected", StringComparison.Ordinal) ? "syntax: " : "error: ") + msg, line);
    }

    /// <summary>The script's own line, a patch's line (its wrapper adds one), else the first script frame of the stack.</summary>
    private static int JsErrorLine(JavaScriptException je) =>
        je.Location.SourceFile == "script" ? je.Location.Start.Line
        : je.Location.SourceFile == "patch" ? Math.Max(1, je.Location.Start.Line - 1)
        : LineIn(je.JavaScriptStackTrace);

    private static (string Reason, int Line) DescribeRejection(JsValue v)
    {
        if (v.IsString() && v.AsString().StartsWith("Timeout of", StringComparison.Ordinal))
            return ("error: a Promise never settled (host calls are synchronous: no new Promise/setTimeout needed)", 0);
        if (v is ObjectInstance o)
        {
            var msg = JsConvert.Text(o.Get("message"));
            var stack = o.Get("stack");
            int line = stack.IsString() ? PatchLine(stack.AsString()) ?? LineIn(stack.AsString()) : 0;
            return ("error: " + Clean(msg.Length > 0 ? msg : JsConvert.Text(v)), line);
        }
        return ("error: thrown " + ScriptSession.Short(JsConvert.Text(v), 100), 0);
    }

    private static readonly Regex PatchLoc = new(@"(?<![\w/])patch:(\d+):\d+", RegexOptions.Compiled);

    /// <summary>Inside a patch: the patch's own line (its wrapper adds one), else null.</summary>
    private static int? PatchLine(string? stack)
    {
        var p = PatchLoc.Match(stack ?? "");
        return p.Success ? Math.Max(1, int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture) - 1) : null;
    }

    /// <summary>The script line in a stack text; inside a patch (no script frame) the patch's own line (its wrapper adds one).</summary>
    private static int LineIn(string? stack)
    {
        if (string.IsNullOrEmpty(stack)) return 0;
        var m = ScriptLoc.Match(stack);
        if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var p = PatchLoc.Match(stack);
        return p.Success ? Math.Max(1, int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture) - 1) : 0;
    }

    private static string Clean(string msg) => ScriptSession.Short(ScriptSession.OneLine(Regex.Replace(msg ?? "", @"\s*\(script:\d+:\d+\)$", "")), 120);

    public string? CarryJson()
    {
        if (_carry == null) return null;
        var json = (JsConvert.ToNode(_carry, 0, args: false) as JsonObject ?? new JsonObject()).ToJsonString();
        return json.Length <= 2000 ? json : null;
    }

    public string? Vars()
    {
        if (_carry == null) return null;
        var parts = new List<string>();
        foreach (var key in _carry.GetOwnPropertyKeys(Types.String))
        {
            if (parts.Count >= 4) break;
            var v = _carry.Get(key);
            if (v.IsNumber() || v.IsBoolean()) parts.Add($"carry.{key}={JsConvert.Text(v)}");
            else if (v.IsString()) parts.Add($"carry.{key}='{ScriptSession.Short(v.AsString(), 16)}'");
        }
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    // ------------------------------------------------------------------ install

    private void Install()
    {
        var h = new JsObject(_e);
        InstallHost(h);
        var def = new ClrFunction(_e, "def", (_, a) =>
        {
            var name = a[0].AsString();
            Define(name, JsConvert.Arg(a, 1));
            return JsValue.Undefined;
        });
        foreach (var prepared in Prepared.Value)
        {
            var fn = _e.Evaluate(prepared);
            _e.Invoke(fn, h, def);
        }
        // Game functions after the SDK (SDK names win, like the old builtins): aliases, tools, skills — snake_case and camelCase.
        foreach (var c in s.Callables(Free))
        {
            var callable = c;
            var f = Host(c.Global, a => R(s.Invoke(callable, MapArgs(callable.Params, a))));
            Define(c.Global, f);
            var camel = Camel(c.Global);
            if (camel != c.Global && Free(camel)) Define(camel, f);
        }
    }

    private bool Free(string name) => IdentRx.IsMatch(name) && !Reserved.Contains(name) && !_taken.Contains(name);

    private void Define(string name, JsValue value)
    {
        _taken.Add(name);
        _e.Global.DefineOwnProperty(name, new PropertyDescriptor(value, writable: false, enumerable: false, configurable: false));
    }

    internal static string Camel(string snake) => Regex.Replace(snake, "_([a-z0-9])", m => m.Groups[1].Value.ToUpperInvariant());

    private void H(ObjectInstance h, string name, Func<JsValue[], JsValue> f) =>
        h.FastSetProperty(name, new PropertyDescriptor(Host(name, f), false, false, false));

    /// <summary>A host function whose result is the script's own data (a copy of what it passed in): its allocations still count.</summary>
    private void Own(ObjectInstance h, string name, Func<JsValue[], JsValue> f) =>
        h.FastSetProperty(name, new PropertyDescriptor(new ClrFunction(_e, name, (_, a) => f(a)), false, false, false));

    /// <summary>
    /// Every host function the script can call: its allocations (the SDK's C# work, JSON → JS) are credited back to the memory
    /// guard, which counts only what script code allocates between actions (<see cref="ScriptSession.HostExit"/>).
    /// </summary>
    private ClrFunction Host(string name, Func<JsValue[], JsValue> f) =>
        new(_e, name, (_, a) =>
        {
            var mark = s.HostEnter();
            try { return f(a); }
            finally { s.HostExit(mark); }
        });

    private JsValue R(ActResult r)
    {
        var o = new JsObject(_e);
        o.Set("ok", r.Ok ? JsBoolean.True : JsBoolean.False);
        o.Set("msg", new JsString(r.Msg ?? ""));
        o.Set("n", new JsNumber(ScriptSession.CountOf(r.Msg)));
        return o;
    }

    private JsValue V(JsonNode? n) => JsConvert.FromNode(_e, n);

    private static JsValue S(string? x) => x == null ? JsValue.Null : new JsString(x);

    /// <summary>
    /// JS call → JSON input: positional values, plus a trailing plain object as named options when it is empty or names one of
    /// the params (<c>take("coal", 8, {at:"1,64,2"})</c>, <c>deposit({items:["log"]})</c>); otherwise it stays positional
    /// (<c>move({item:"log", n:"all", to:"chest"})</c> fills the array param). Trailing undefined args are dropped.
    /// </summary>
    private static JsonElement MapArgs(IReadOnlyList<(string Name, string? Type)> ps, JsValue[] a, int skip = 0)
    {
        int n = a.Length;
        while (n > skip && a[n - 1].IsUndefined()) n--;
        JsonObject? named = null;
        if (n > skip && JsConvert.IsPlainObject(a[n - 1]) && JsConvert.ToNode(a[n - 1], 0) is JsonObject last && !ArgMapper.IsPos(last)
            && (last.Count == 0 || last.Any(kv => ps.Any(p => p.Name == kv.Key) || kv.Key == "n" && ps.Any(p => p.Name == "count"))))
        {
            named = last;
            n--;
        }
        var pos = new List<JsonNode?>();
        for (int i = skip; i < n; i++) pos.Add(JsConvert.ToNode(a[i], 1));
        return ArgMapper.Map(ps, pos, named);
    }

    private static string? Str(JsValue[] a, int i) => JsConvert.Str(JsConvert.Arg(a, i));
    private static double? Num(JsValue[] a, int i) => JsConvert.Num(JsConvert.Arg(a, i));

    private void InstallHost(ObjectInstance h)
    {
        // ---- results, control
        // fail(): a patch may fix the cause first (resume = it returns); otherwise the script fails as before.
        H(h, "fail", a => { s.FailPoint(Str(a, 0)); return JsValue.Undefined; });
        H(h, "onInterrupt", a =>
        {
            var kind = Str(a, 0) ?? throw ScriptSession.Bug("onInterrupt(kind, fn) needs a kind");
            var fn = JsConvert.Arg(a, 1) as Function ?? throw ScriptSession.Bug("onInterrupt(kind, fn) needs a function");
            s.OnInterrupt(kind, i =>
            {
                var r = _e.Invoke(fn, PatchEvent(i), CurrentMachine() ?? JsValue.Null).UnwrapIfPromise();
                return r.IsString() ? r.AsString() : r.IsUndefined() || r.IsNull() ? null : JsConvert.Text(r);
            });
            return JsValue.Undefined;
        });
        H(h, "log", a => { s.Log(string.Join(" ", a.Select(JsConvert.Text))); return JsValue.Undefined; });
        H(h, "prog", a => { s.Progress(Num(a, 0), JsConvert.Arg(a, 1).IsUndefined() ? null : JsConvert.Text(a[1])); return JsValue.Undefined; });
        H(h, "wait", a => R(s.Wait(Num(a, 0) ?? 1)));
        H(h, "idle", a => R(s.Wait(Num(a, 0) ?? 0.05, counts: false)));
        H(h, "waitFor", a => R(s.WaitFor(Str(a, 0) ?? "", JsConvert.ToNode(JsConvert.Arg(a, 1), 1), Num(a, 2))));
        H(h, "say", a => R(s.Chat(Str(a, 0) ?? "", emote: false)));
        H(h, "emote", a => R(s.Chat(Str(a, 0) ?? "", emote: true)));
        H(h, "ask", a => S(s.Ask(Str(a, 0), Str(a, 1) ?? "", Num(a, 2))));
        H(h, "wake", a => s.Wake(Str(a, 0)) ? JsBoolean.True : JsBoolean.False);
        H(h, "cp", a =>
        {
            var fn = JsConvert.Arg(a, 1);
            Action? stage = fn is Function ? () => _e.Invoke(fn) : null;
            return s.Cp(Str(a, 0) ?? "", stage) ? JsBoolean.True : JsBoolean.False;
        });
        H(h, "on", a => { On(Str(a, 0) ?? "", JsConvert.Arg(a, 1), JsConvert.Arg(a, 2), null); return JsValue.Undefined; });
        H(h, "skill", a =>
        {
            var name = Str(a, 0) ?? throw ScriptSession.Bug("skill(name, ...args) needs a name");
            return R(s.SkillCall(name, ps => MapArgs(ps, a, skip: 1)));
        });
        Own(h, "params", a => Params(JsConvert.Arg(a, 0)));
        _carry = JsConvert.FromJson(_e, s.Request.CarryJson) as ObjectInstance ?? new JsObject(_e);
        h.FastSetProperty("carry", new PropertyDescriptor(_carry, false, false, false));
        h.FastSetProperty("args", new PropertyDescriptor(Freeze(RunArgs()), false, false, false));

        // ---- queries
        H(h, "me", a => V(s.Me(Str(a, 0) ?? "")));
        H(h, "inv", a => new JsNumber(s.CountItem(Str(a, 0) ?? throw ScriptSession.Bug("inv(item) needs an item name"))));
        H(h, "free", _ => new JsNumber(s.FreeSlots()));
        H(h, "block", a =>
        {
            World.BlockPos? p = a.Length >= 3 && a.Take(3).All(x => x.IsNumber())
                ? new World.BlockPos((int)Math.Floor(a[0].AsNumber()), (int)Math.Floor(a[1].AsNumber()), (int)Math.Floor(a[2].AsNumber()))
                : ScriptSession.PosOf(JsConvert.ToNode(JsConvert.Arg(a, 0), 1));
            return new JsString(s.BlockAt(p ?? throw ScriptSession.Bug("block(x,y,z) needs a position")));
        });
        // light(x,y,z|pos) / canSpawn(x,y,z|pos, now?): the b1.8.1 light model (WorldState.Light) and hostile spawn rule.
        (World.BlockPos Pos, int Next) PosArgs(JsValue[] a, string what)
        {
            if (a.Length >= 3 && a.Take(3).All(x => x.IsNumber()))
                return (new World.BlockPos((int)Math.Floor(a[0].AsNumber()), (int)Math.Floor(a[1].AsNumber()), (int)Math.Floor(a[2].AsNumber())), 3);
            return (ScriptSession.PosOf(JsConvert.ToNode(JsConvert.Arg(a, 0), 1)) ?? throw ScriptSession.Bug($"{what}(x,y,z) needs a position"), 1);
        }
        H(h, "light", a => V(s.LightAt(PosArgs(a, "light").Pos)));
        H(h, "canSpawn", a =>
        {
            var (p, next) = PosArgs(a, "canSpawn");
            return s.CanSpawn(p, TypeConverter.ToBoolean(JsConvert.Arg(a, next))) ? JsBoolean.True : JsBoolean.False;
        });
        H(h, "near", a => V(s.Nearest(Str(a, 0) ?? throw ScriptSession.Bug("near(kind) needs a block/player/place name"), Num(a, 1))));
        H(h, "find", a => V(s.FindAll(Str(a, 0) ?? throw ScriptSession.Bug("find(query) needs a name (block, mob, player or place)"),
            Num(a, 1), Num(a, 2) is { } lim ? (int)lim : null)));
        H(h, "item", a => V(s.ItemInfo(Str(a, 0) ?? throw ScriptSession.Bug("item(name) needs a name"))));
        H(h, "sources", a => V(s.Sources(Str(a, 0) ?? throw ScriptSession.Bug("gather(what) needs an item name"))));
        H(h, "hostile", a => V(Hostile(Num(a, 0) ?? 8)));
        H(h, "where", a => S(s.Where(Str(a, 0) ?? throw ScriptSession.Bug("where(item) needs an item name"))));
        H(h, "night", _ => s.Night() ? JsBoolean.True : JsBoolean.False);
        H(h, "time", _ => new JsNumber(s.TimeOfDay()));
        H(h, "players", _ => V(s.Players()));

        // ---- reactive core
        H(h, "signal", a => s.Signals.Ensure(Str(a, 0) ?? "", JsConvert.ToNode(JsConvert.Arg(a, 1), 0, args: false)) ? JsBoolean.True : JsBoolean.False);
        H(h, "when", a =>
        {
            var fn = JsConvert.Arg(a, 0) as Function ?? throw ScriptSession.Bug("when(fn) needs a function");
            return new JsString(s.Signals.AddWhen(() => TypeConverter.ToBoolean(_e.Invoke(fn))));
        });
        H(h, "timeout", a =>
        {
            var sec = Num(a, 0) ?? throw ScriptSession.Bug("timeout(s, fn) needs seconds");
            var fn = JsConvert.Arg(a, 1) as Function ?? throw ScriptSession.Bug("timeout(s, fn) needs a function");
            var box = s.WithTimeout(sec, () => new StrongBox(_e.Invoke(fn)));
            return box?.Value ?? R(ActResult.Fail($"timeout after {ScriptSession.Fmt(sec)}s"));
        });
        H(h, "machine", a => new JsString(RunMachine(Str(a, 0) ?? "m", Str(a, 1) ?? "", JsConvert.Arg(a, 2), JsConvert.Arg(a, 3))));

        InstallCrew(h);
        InstallCharacter(h); // JsRuntime.Character.cs: the character module's host calls
        InstallStory(h); // JsRuntime.Story.cs: Story Mode pawns (story.next / story.report)
    }

    private sealed class StrongBox(JsValue v) { public JsValue Value { get; } = v; }

    private ObjectInstance Freeze(JsValue v)
    {
        var o = v as ObjectInstance ?? new JsObject(_e);
        _e.Intrinsics.Object.Get("freeze").Call(_e.Intrinsics.Object, [o]);
        return o;
    }

    /// <summary>The run's own values: args, overlaid by params (role lanes: the role's args).</summary>
    private JsonObject RunValues()
    {
        var o = new JsonObject();
        foreach (var json in new[] { s.Request.ArgsJson, s.Request.ParamsJson })
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                if (JsonNode.Parse(json) is JsonObject src)
                    foreach (var (k, v) in src) if (!k.StartsWith("__", StringComparison.Ordinal)) o[k] = v?.DeepClone();
            }
            catch (JsonException) { }
        }
        return o;
    }

    private JsValue RunArgs() => V(RunValues());

    /// <summary><c>params(defaults)</c>: defaults &lt; the saved tunables &lt; this run's values; recorded for telemetry.</summary>
    private JsValue Params(JsValue defaults)
    {
        var merged = JsConvert.ToNode(defaults, 0, args: false) as JsonObject ?? new JsonObject();
        if (s.Host.StoredParams?.Invoke(s.Name) is { Length: > 0 } stored)
            try { if (JsonNode.Parse(stored) is JsonObject so) foreach (var (k, v) in so) merged[k] = v?.DeepClone(); } catch (JsonException) { }
        foreach (var (k, v) in RunValues()) merged[k] = v?.DeepClone();
        s.ParamsJson = merged.ToJsonString();
        return _params = ParamsObject(merged);
    }

    private ObjectInstance? _params;
    private readonly Dictionary<string, JsValue> _paramValues = new(StringComparer.Ordinal);
    private bool _patching;

    /// <summary>
    /// The params object: read-only to the script (an assignment throws a TypeError, as on a frozen object), writable inside a
    /// patch (<c>P.n = 32</c> retunes the rest of this run; the effective params telemetry follows).
    /// </summary>
    private ObjectInstance ParamsObject(JsonObject merged)
    {
        var o = new JsObject(_e);
        _paramValues.Clear();
        foreach (var (k, v) in merged)
        {
            var key = k;
            _paramValues[key] = Freeze2(V(v));
            var get = new ClrFunction(_e, "get " + key, (_, _) => _paramValues.TryGetValue(key, out var cur) ? cur : JsValue.Undefined);
            var set = new ClrFunction(_e, "set " + key, (_, a) =>
            {
                if (!_patching) throw new JavaScriptException(_e.Intrinsics.TypeError, $"Cannot assign to read only property '{key}' of params (tune it with script op=save params)");
                _paramValues[key] = JsConvert.Arg(a, 0);
                SyncParams();
                return JsValue.Undefined;
            });
            o.DefineOwnProperty(key, new GetSetPropertyDescriptor(get, set, enumerable: true, configurable: false));
        }
        o.PreventExtensions();
        return o;
    }

    private JsValue Freeze2(JsValue v) => v is ObjectInstance ? Freeze(v) : v;

    private void SyncParams()
    {
        var o = new JsonObject();
        foreach (var (k, v) in _paramValues) o[k] = JsConvert.ToNode(v, 0, args: false);
        s.ParamsJson = o.ToJsonString();
    }

    // ------------------------------------------------------------------ patches

    private readonly List<ObjectInstance> _machineCtx = new();

    private JsValue? CurrentMachine() => _machineCtx.Count > 0 ? _machineCtx[^1] : null;

    private JsValue PatchEvent(ScriptInterrupt i)
    {
        var ev = new JsObject(_e);
        ev.Set("kind", new JsString(i.Kind));
        ev.Set("text", new JsString(i.Text));
        if (i.Player is { } p) ev.Set("from", new JsString(p));
        ev.Set("line", new JsNumber(s.Line));
        return ev;
    }

    /// <summary>A patch: <c>async (ev, P, m, bb) =&gt; { js }</c> evaluated in this engine (the script's globals, carry, SDK), on the script thread.</summary>
    public string? RunPatch(string js, ScriptInterrupt i)
    {
        var fn = _e.Evaluate("(async function (ev, P, m, bb) {" + '\n' + js + '\n' + "})", "patch");
        var m = CurrentMachine();
        var bb = m is ObjectInstance mo ? mo.Get("bb") : JsValue.Null;
        _patching = true;
        try
        {
            var r = _e.Invoke(fn, PatchEvent(i), _params ?? (JsValue)JsValue.Null, m ?? JsValue.Null, bb).UnwrapIfPromise();
            return r.IsString() ? r.AsString() : null;
        }
        finally { _patching = false; }
    }

    private JsonObject? Hostile(double radius)
    {
        var w = s.Host.World;
        var feet = w.Player.Position;
        // Only one that can get at the body: a cave mob under the floor isn't a reason for a script to react.
        if (w.Entities.Nearby(feet, Math.Clamp(radius, 1, 32), e => e.IsHostileMob && MineClaw.Client.Combat.CombatGeometry.CanGetAt(w, e, feet), 1)
                .FirstOrDefault() is not { } m) return null;
        return ScriptSession.PosNode((int)Math.Floor(m.Position.X), (int)Math.Floor(m.Position.Y), (int)Math.Floor(m.Position.Z),
            m.DisplayName.ToLowerInvariant(), m.Position.DistanceTo(feet));
    }

    /// <summary>
    /// <c>on(topic, filter?, fn)</c> (and the policies' handlers): built-in signal topics are switched on, timers start now, when()
    /// topics become active. fn(ev) runs at the next safe point; its string answer ("pause"/"wake"/"stop") passes it on.
    /// </summary>
    private void On(string topic, JsValue filter, JsValue fnV, object? owner)
    {
        if (fnV is not Function fn) throw ScriptSession.Bug("on(topic, [filter,] fn) needs a function");
        topic = topic.Trim();
        var node = JsConvert.ToNode(filter, 1);
        if (Events.GameEventCatalog.IsEvent(topic)) // typed game events: C# filters (player, held, block, pos, region, cooldown...)
        {
            s.OnGameEvent(topic, node, e => { var r = _e.Invoke(fn, Event(e)); return r.IsString() ? r.AsString() : null; }, owner);
            return;
        }
        string? from = ScriptSession.Field(node, "from") ?? ScriptSession.Field(node, "player"), text = ScriptSession.Field(node, "text");
        if (ScriptSession.AsString(node) is { } fs) text = fs;
        if (topic.StartsWith("after:", StringComparison.Ordinal) || topic.StartsWith("every:", StringComparison.Ordinal)) s.Signals.AddTimer(topic, owner);
        else if (topic.StartsWith("when:", StringComparison.Ordinal)) s.Signals.Activate(topic, true);
        else s.Signals.Ensure(topic);
        s.On(topic, from, text, e =>
        {
            var ret = _e.Invoke(fn, Event(e));
            return ret.IsString() ? ret.AsString() : null;
        }, owner: owner);
    }

    private JsValue Event(ScriptEvent e)
    {
        var ev = new JsObject(_e);
        ev.Set("kind", new JsString(e.Kind.ToString().ToLowerInvariant()));
        ev.Set("topic", new JsString(e.Name));
        ev.Set("text", new JsString(e.Text));
        if (e.Player is { } p) ev.Set("from", new JsString(p));
        AddData(ev, e);
        return ev;
    }
}
