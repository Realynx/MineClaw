using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lua;
using Lua.Standard;
using MineClaw.World;

namespace MineClaw.Scripting.Runtime.Lua;

/// <summary>
/// LEGACY runtime (LuaCSharp 0.5.7, Lua 5.2): runs saved Lua versions that the one-time migration hasn't translated to JS yet,
/// behind <see cref="ScriptOptions.LegacyLua"/>. No new features land here; removal steps are in ARCHITECTURE.md. Globals are
/// the old cheat-sheet API (see <see cref="LuaPrelude"/>); every one forwards to the session's neutral API.
/// </summary>
internal sealed class LuaRuntime(ScriptSession s) : IScriptRuntime
{
    private const int HookEvery = 10;

    private LuaState? _lua;
    private CancellationToken _ct;
    private readonly HashSet<string> _baseGlobals = new(StringComparer.Ordinal);

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

    public IReadOnlyList<JsonNode?> Run(string code, CancellationToken ct)
    {
        _ct = ct;
        _lua = BuildState();
        var result = _lua.DoStringAsync(code, "script", ct).AsTask().GetAwaiter().GetResult();
        return result.Take(3).Select(Scalar).ToList();
    }

    public void Dispose() { }

    private static JsonNode? Scalar(LuaValue v) => v.Type switch
    {
        LuaValueType.String => JsonValue.Create(v.Read<string>()),
        LuaValueType.Number => JsonValue.Create(v.Read<double>()),
        LuaValueType.Boolean => JsonValue.Create(v.Read<bool>()),
        LuaValueType.Nil => null,
        _ => JsonValue.Create(v.ToString()),
    };

    // ------------------------------------------------------------------ location, errors

    private static readonly Regex LuaLoc = new(@"\[string ""script""\]:(\d+):\s*(.*)$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TraceLoc = new(@"\[string ""script""\]:(\d+):", RegexOptions.Compiled);

    public int CurrentLine()
    {
        try
        {
            var m = TraceLoc.Match(_lua!.GetTraceback().ToString());
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        catch (Exception) { return 0; }
    }

    public (string Reason, int Line)? Describe(Exception ex)
    {
        switch (ex)
        {
            case LuaCompileException ce:
            {
                var (msg, line) = LuaMessage(ce.Message);
                return ("syntax: " + msg, line);
            }
            case LuaRuntimeException re:
            {
                var (msg, line) = LuaMessage(re.Message);
                // Our own exception wrapped by the VM: the location only (the session reads the inner exception's reason).
                if (re.InnerException is ScriptStopException or ScriptLimitException or ScriptFailException) return (null!, line);
                return ("error: " + msg, line);
            }
            default: return null;
        }
    }

    /// <summary>"Lua-CSharp: [string "script"]:2: attempt to..." → ("attempt to...", 2).</summary>
    private static (string Msg, int Line) LuaMessage(string msg)
    {
        var m = LuaLoc.Match(msg);
        if (!m.Success) return (ScriptSession.Short(ScriptSession.OneLine(msg.Replace("Lua-CSharp: ", "")), 120), 0);
        return (ScriptSession.Short(ScriptSession.OneLine(m.Groups[2].Value), 120), int.Parse(m.Groups[1].Value));
    }

    // ------------------------------------------------------------------ sandbox

    private LuaState BuildState()
    {
        var st = LuaState.Create();
        st.OpenBasicLibrary();
        st.OpenStringLibrary();
        st.OpenTableLibrary();
        st.OpenMathLibrary();
        var env = st.Environment;
        // No code loading, files, raw access, error swallowing (pcall would catch limits) or GC control.
        foreach (var k in new[] { "load", "loadfile", "loadstring", "dofile", "require", "collectgarbage", "rawset", "rawget", "rawequal", "rawlen",
                     "pcall", "xpcall", "module", "package", "io", "os", "debug", "coroutine", "print", "_VERSION" })
            env[k] = LuaValue.Nil;
        if (env["string"].TryRead<LuaTable>(out var str))
        {
            str["dump"] = LuaValue.Nil;
            var rep = str["rep"];
            int max = Math.Max(1000, s.Options.MaxStringLength);
            str["rep"] = F("rep", c =>
            {
                double n = Num(c, 1, 0);
                int len = c.GetArgument(0).TryRead<string>(out var x) ? x.Length : 0;
                int sep = c.ArgumentCount > 2 && c.GetArgument(2).TryRead<string>(out var y) ? y.Length : 0;
                if (n * (len + sep) > max) throw ScriptSession.Bug("string too long");
                var r = _lua!.CallAsync(rep, c.Arguments, _ct).AsTask().GetAwaiter().GetResult();
                return c.Return(r.Length > 0 ? r[0] : LuaValue.Nil);
            });
            var format = str["format"];
            str["format"] = F("format", c =>
            {
                if (c.GetArgument(0).TryRead<string>(out var f) && Regex.IsMatch(f, @"%[-+ #0]*\d{3,}")) throw ScriptSession.Bug("format width too big");
                var r = _lua!.CallAsync(format, c.Arguments, _ct).AsTask().GetAwaiter().GetResult();
                return c.Return(r.Length > 0 ? r[0] : LuaValue.Nil);
            });
        }
        var os = new LuaTable
        {
            ["time"] = F("time", c => c.Return((double)s.Time.GetUtcNow().ToUnixTimeSeconds())),
            ["clock"] = F("clock", c => c.Return(s.Elapsed.TotalSeconds)),
        };
        env["os"] = Locked(os);
        st.SetHook(new LuaFunction("hook", (_, _) => { Hook(); return new ValueTask<int>(0); }), "", HookEvery);
        _lua = st;
        Install(env);
        // Reliability helpers (retry/upto/expect/room/ensure) in their own chunk: user line numbers stay intact.
        st.DoStringAsync(LuaPrelude.Code, "prelude", _ct).AsTask().GetAwaiter().GetResult();
        foreach (var kv in env) if (kv.Key.TryRead<string>(out var name)) _baseGlobals.Add(name);
        return st;
    }

    private void Hook()
    {
        s.Budget(HookEvery);
        if (_lua!.CallStackFrameCount > s.Options.MaxCallDepth) s.DepthLimit();
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
        foreach (var c in s.Callables(g => Ident.IsMatch(g) && !LuaKeywords.Contains(g) && taken.Add(g)))
        {
            var callable = c;
            env[c.Global] = F(c.Global, ctx => Ret(ctx, s.Invoke(callable, MapArgs(callable.Params, ctx))));
        }

        env["skill"] = F("skill", c =>
        {
            if (!c.GetArgument(0).TryRead<string>(out var asked)) throw ScriptSession.Bug("skill(name, {args}) needs a name");
            return Ret(c, s.SkillCall(asked, ps => MapArgs(ps, c, skip: 1)));
        });

        // ---- queries
        var invFns = new LuaTable
        {
            ["free"] = F("free", c => c.Return((double)s.FreeSlots())),
            ["has"] = F("has", c => c.Return(s.CountItem(ItemName(c.GetArgument(0))) >= Num(c, 1, 1))),
        };
        env["inv"] = Locked(invFns, call: F("inv", c => c.Return((double)s.CountItem(ItemName(c.GetArgument(1))))));

        env["me"] = Locked(new LuaTable(), index: F("me", c =>
            c.Return(FromNode(s.Me(c.GetArgument(1).TryRead<string>(out var k) ? k : "")))));
        env["block"] = F("block", c =>
        {
            var p = PosArgs(c, 0) ?? throw ScriptSession.Bug("block(x,y,z) needs a position");
            return c.Return(s.BlockAt(p));
        });
        env["near"] = F("near", c =>
        {
            if (!c.GetArgument(0).TryRead<string>(out var kind)) throw ScriptSession.Bug("near\"kind\" needs a block/player/place name");
            double? r = c.ArgumentCount > 1 && c.GetArgument(1).TryRead<double>(out var rr) ? rr : null;
            return c.Return(FromNode(s.Nearest(kind, r)));
        });
        env["where"] = F("where", c =>
        {
            if (!c.GetArgument(0).TryRead<string>(out var item)) throw ScriptSession.Bug("where\"item\" needs an item name");
            return c.Return(s.Where(item) is { } r ? r : LuaValue.Nil);
        });
        env["time"] = F("time", c => c.Return((double)s.TimeOfDay()));
        env["night"] = F("night", c => c.Return(s.Night()));
        env["players"] = F("players", c => c.Return(FromNode(s.Players())));

        // ---- control
        env["say"] = F("say", c => Ret(c, s.Chat(Str(c.GetArgument(0)), emote: false)));
        env["emote"] = F("emote", c => Ret(c, s.Chat(Str(c.GetArgument(0)), emote: true)));
        env["wait"] = F("wait", c => Ret(c, s.Wait(Num(c, 0, 1))));
        env["waitfor"] = F("waitfor", c =>
        {
            var kind = c.GetArgument(0).TryRead<string>(out var k0) ? k0 : "";
            double? sec = Arg(c, 2).TryRead<double>(out var sv) ? sv : null;
            return Ret(c, s.WaitFor(kind, ToNode(Arg(c, 1), 1), sec));
        });
        env["ask"] = F("ask", c =>
        {
            string? who = c.GetArgument(0).TryRead<string>(out var pl) && pl.Length > 0 ? pl : null;
            double? sec = Arg(c, 2).TryRead<double>(out var sv) ? sv : null;
            return c.Return(s.Ask(who, Str(Arg(c, 1)), sec) is { } a ? a : LuaValue.Nil);
        });
        env["wake"] = F("wake", c => c.Return(s.Wake(c.GetArgument(0).TryRead<string>(out var r) ? r : null)));
        var log = F("log", c =>
        {
            s.Log(string.Join(" ", c.Arguments.ToArray().Select(Str)));
            return c.Return();
        });
        env["log"] = log;
        env["print"] = log;
        env["prog"] = F("prog", c =>
        {
            s.Progress(c.GetArgument(0).TryRead<double>(out var pct) ? pct : null, Arg(c, 1).Type != LuaValueType.Nil ? Str(Arg(c, 1)) : null);
            return c.Return();
        });
        env["cp"] = F("cp", c =>
        {
            if (!c.GetArgument(0).TryRead<string>(out var label) || label.Length == 0) throw ScriptSession.Bug("cp\"label\" needs a label");
            var fn = Arg(c, 1);
            Action? stage = fn.Type == LuaValueType.Function ? () => _lua!.CallAsync(fn.Read<LuaFunction>(), [], _ct).AsTask().GetAwaiter().GetResult() : null;
            return c.Return(s.Cp(label, stage));
        });
        env["on"] = F("on", c =>
        {
            On(c.GetArgument(0), Arg(c, 1), Arg(c, 2));
            return c.Return();
        });
        env["fail"] = F("fail", c =>
        {
            var m = Arg(c, 0);
            if (m.Type == LuaValueType.Table) { s.Locate(); throw ScriptSession.Bug("fail(msg) takes a string reason; return a result as a string instead"); }
            throw s.Fail(m.Type == LuaValueType.Nil ? null : Str(m));
        });
        env["carry"] = FromJson(s.Request.CarryJson) ?? new LuaTable();
        env["args"] = FromJson(s.Request.ArgsJson ?? s.Request.ParamsJson) ?? new LuaTable();
    }

    private string ItemName(LuaValue v)
    {
        if (v.TryRead<string>(out var n)) return n;
        s.Locate();
        throw ScriptSession.Bug("item name must be a string");
    }

    private void On(LuaValue kindV, LuaValue a, LuaValue b)
    {
        var kind = kindV.TryRead<string>(out var kk) ? kk.Trim().ToLowerInvariant() : "";
        var fnV = b.Type == LuaValueType.Nil ? a : b;
        var filter = b.Type == LuaValueType.Nil ? LuaValue.Nil : a;
        if (!fnV.TryRead<LuaFunction>(out var fn)) throw ScriptSession.Bug("on(kind, [filter,] fn) needs a function");
        if (kind is not ("danger" or "chat")) throw ScriptSession.Bug($"on('{kind}')? use danger|chat");
        var node = ToNode(filter, 1);
        string? from = ScriptSession.Field(node, "from") ?? ScriptSession.Field(node, "player"), text = ScriptSession.Field(node, "text");
        if (filter.TryRead<string>(out var fs)) text = fs;
        var k = kind == "danger" ? ScriptEventKind.Danger : ScriptEventKind.Chat;
        s.AddHandler(new ScriptSession.Handler($"on('{kind}')", e => e.Kind == k && ScriptSession.Matches(from, text, e), e =>
        {
            var ev = new LuaTable();
            ev["kind"] = e.Kind.ToString().ToLowerInvariant();
            ev["text"] = e.Text;
            if (e.Player is { } p) ev["from"] = p;
            var ret = _lua!.CallAsync(fn, [ev], _ct).AsTask().GetAwaiter().GetResult();
            return ret.Length > 0 && ret[0].TryRead<string>(out var str) ? str : null;
        }, Cuts: k == ScriptEventKind.Danger));
    }

    private static LuaValue Arg(LuaFunctionExecutionContext c, int i) => i < c.ArgumentCount ? c.GetArgument(i) : LuaValue.Nil;

    private static int Ret(LuaFunctionExecutionContext c, ActResult r) => c.Return(r.Ok, r.Msg ?? "", (double)ScriptSession.CountOf(r.Msg));

    // ------------------------------------------------------------------ conversions

    internal static string Str(LuaValue v) => v.Type switch
    {
        LuaValueType.Nil => "",
        LuaValueType.String => v.Read<string>(),
        LuaValueType.Number => ScriptSession.Fmt(v.Read<double>()),
        LuaValueType.Boolean => v.Read<bool>() ? "true" : "false",
        _ => v.ToString(),
    };

    private static double Num(LuaFunctionExecutionContext c, int i, double def) =>
        i < c.ArgumentCount && c.GetArgument(i).TryRead<double>(out var d) ? d : def;

    /// <summary>block(x,y,z) / block{x,y,z} / block(pos) / block"x,y,z".</summary>
    private static BlockPos? PosArgs(LuaFunctionExecutionContext c, int i)
    {
        if (c.ArgumentCount >= i + 3 && c.GetArgument(i).TryRead<double>(out var x) && c.GetArgument(i + 1).TryRead<double>(out var y)
            && c.GetArgument(i + 2).TryRead<double>(out var z))
            return new BlockPos((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
        return i < c.ArgumentCount ? ScriptSession.PosOf(ToNode(c.GetArgument(i), 1)) : null;
    }

    /// <summary>
    /// Lua call → JSON input. One table = its array part positional + its named keys; otherwise the arguments themselves are
    /// positional (then <see cref="ArgMapper"/> fills the params). A single position table is one positional value.
    /// </summary>
    private static JsonElement MapArgs(IReadOnlyList<(string Name, string? Type)> ps, LuaFunctionExecutionContext c, int skip = 0)
    {
        var pos = new List<JsonNode?>();
        JsonObject? named = null;
        if (c.ArgumentCount == skip + 1 && c.GetArgument(skip).TryRead<LuaTable>(out var t) && !FirstIsPosTable(ps, t))
            named = SplitTable(t, pos);
        else
            for (int i = skip; i < c.ArgumentCount; i++) pos.Add(ToNode(c.GetArgument(i), 1));
        return ArgMapper.Map(ps, pos, named);
    }

    /// <summary>The single table is a position for a leading to/at/pos param (then it is one positional value).</summary>
    private static bool FirstIsPosTable(IReadOnlyList<(string Name, string? Type)> ps, LuaTable t) =>
        ps.Count > 0 && ScriptSession.PosParams.Contains(ps[0].Name) && IsPosTable(t);

    /// <summary>A table's array part (≤ 64) as positional values; returns its string keys as the named values.</summary>
    private static JsonObject SplitTable(LuaTable t, List<JsonNode?> pos)
    {
        for (int i = 1; i <= 64 && t[i].Type != LuaValueType.Nil; i++) pos.Add(ToNode(t[i], 1));
        var named = new JsonObject();
        foreach (var kv in t)
            if (kv.Key.TryRead<string>(out var key)) named[key] = ToNode(kv.Value, 1);
        return named;
    }

    private static bool IsPosTable(LuaTable t) =>
        ScriptSession.PosOf(ToNode(t, 1)) != null && (t["x"].Type == LuaValueType.Number || (t.ArrayLength == 3 && t.HashMapCount == 0));

    /// <summary>Lua value → JSON (functions dropped, depth ≤ 6). Pure arrays → arrays; nested <c>n</c> → count.</summary>
    private static JsonNode? ToNode(LuaValue v, int depth, bool args = true)
    {
        if (depth > 6) return null;
        return v.Type switch
        {
            LuaValueType.String => JsonValue.Create(v.Read<string>()),
            LuaValueType.Boolean => JsonValue.Create(v.Read<bool>()),
            LuaValueType.Number => LuaNumberNode(v.Read<double>()),
            LuaValueType.Table => LuaTableNode(v.Read<LuaTable>(), depth, args),
            _ => null,
        };
    }

    private static JsonNode LuaNumberNode(double d) =>
        double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 1e15 ? JsonValue.Create((long)d) : JsonValue.Create(double.IsFinite(d) ? d : 0);

    /// <summary>A pure array (first 64 items) as an array, anything else as an object (first 32 keys, nils dropped).</summary>
    private static JsonNode LuaTableNode(LuaTable t, int depth, bool args)
    {
        if (t.ArrayLength > 0 && t.HashMapCount == 0) return LuaArrayNode(t, depth, args);
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

    private static JsonArray LuaArrayNode(LuaTable t, int depth, bool args)
    {
        var ja = new JsonArray();
        for (int i = 1; i <= t.ArrayLength && i <= 64; i++) ja.Add(ToNode(t[i], depth + 1, args));
        return ja;
    }

    private static LuaValue FromNode(JsonNode? e, int depth = 0)
    {
        if (depth > 8 || e == null) return LuaValue.Nil;
        switch (e)
        {
            case JsonArray a:
            {
                var t = new LuaTable();
                int i = 1;
                foreach (var x in a) t[i++] = FromNode(x, depth + 1);
                return t;
            }
            case JsonObject o:
            {
                var t = new LuaTable();
                foreach (var (k, v) in o) t[k] = FromNode(v, depth + 1);
                return t;
            }
            case JsonValue v:
                return LuaScalarFrom(v);
            default: return LuaValue.Nil;
        }
    }

    private static LuaValue LuaScalarFrom(JsonValue v) => v.GetValueKind() switch
    {
        JsonValueKind.String => v.GetValue<string>(),
        JsonValueKind.Number => ScriptSession.AsNumber(v) ?? 0,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => LuaValue.Nil,
    };

    private static LuaTable? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) is JsonObject o && FromNode(o).TryRead<LuaTable>(out var t) ? t : null;
        }
        catch (Exception) { return null; }
    }

    public string? CarryJson()
    {
        if (_lua is null || !_lua.Environment["carry"].TryRead<LuaTable>(out var carry)) return null;
        var node = ToNode(carry, 0, args: false);
        var json = node is JsonObject ? node.ToJsonString() : "{}";
        return json.Length <= 2000 ? json : null;
    }

    /// <summary>The first few scalar globals the script made (Lua locals aren't visible without the debug library) and carry fields.</summary>
    public string? Vars()
    {
        if (_lua is null) return null;
        var parts = new List<string>();
        void Add(string name, LuaValue v)
        {
            if (parts.Count >= 4) return;
            if (v.Type is LuaValueType.Number or LuaValueType.Boolean) parts.Add($"{name}={Str(v)}");
            else if (v.Type == LuaValueType.String) parts.Add($"{name}='{ScriptSession.Short(v.Read<string>(), 16)}'");
        }
        foreach (var kv in _lua.Environment)
            if (kv.Key.TryRead<string>(out var k) && !_baseGlobals.Contains(k)) Add(k, kv.Value);
        if (_lua.Environment["carry"].TryRead<LuaTable>(out var carry))
            foreach (var kv in carry)
                if (kv.Key.TryRead<string>(out var k)) Add("carry." + k, kv.Value);
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }
}
