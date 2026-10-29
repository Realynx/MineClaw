using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace MineClaw.Scripting.Lint;

/// <summary>
/// Zero-token static checks on a JS script (Acornima AST, the parser Jint itself uses) before it is saved or run. Errors reject
/// it (the author loop fixes them): syntax, unknown globals (with the closest name), forbidden constructs (eval/Function/
/// require/import/timers/the hidden host), a constant-true loop that never acts or waits, required tool args that a literal call
/// can't fill, a machine whose targets/initial aren't states, crew scripts whose top level does work or whose roles don't exist.
/// Warnings ride along: unchecked action results in loops, a long script without cp/machine, a tool job without ensure,
/// say in a loop or a lane, an acting loop that never ends, a call to a deprecated compat helper (with what replaces it).
/// </summary>
internal static class JsLint
{
    /// <summary>JS globals a script may use besides the SDK and the game functions.</summary>
    public static readonly IReadOnlySet<string> StandardGlobals = new HashSet<string>(StringComparer.Ordinal)
    {
        "Object", "Array", "Math", "JSON", "String", "Number", "Boolean", "Date", "RegExp", "Map", "Set", "WeakMap", "WeakSet", "Symbol",
        "Error", "TypeError", "RangeError", "SyntaxError", "ReferenceError", "Promise", "Proxy", "Reflect", "parseInt", "parseFloat",
        "isNaN", "isFinite", "Infinity", "NaN", "undefined", "BigInt", "encodeURIComponent", "decodeURIComponent", "Intl", "arguments",
    };

    /// <summary>Forbidden names, with why (and what to use instead).</summary>
    private static readonly Dictionary<string, string> Forbidden = new(StringComparer.Ordinal)
    {
        ["eval"] = "eval is disabled", ["Function"] = "Function() is disabled", ["require"] = "no modules: everything is a global",
        ["importNamespace"] = "no CLR access", ["process"] = "no process", ["globalThis"] = "globals are read-only: use carry/args",
        ["setTimeout"] = "no timers: use wait(s), or after(ms) in a machine", ["setInterval"] = "no timers: use a loop with wait(s) or every(ticks)",
        ["setImmediate"] = "no timers", ["fetch"] = "no network", ["XMLHttpRequest"] = "no network", ["WebAssembly"] = "not available",
    };

    /// <summary>Calls that neither act nor wait: a constant-true loop calling only these is a busy loop.</summary>
    private static readonly HashSet<string> PureCalls = new(StringComparer.Ordinal)
    {
        "inv", "block", "near", "hostile", "where", "time", "night", "players", "pos", "dist", "ok", "expect", "log", "print", "prog", "cp",
        "fail", "on", "params", "signal", "when", "after", "every", "String", "Number", "Boolean", "parseInt", "parseFloat", "isNaN",
        "isFinite", "body", "bodies", "policy", "keepFed", "fleeVoidEdges", "defendIfAttacked", "depositWhenFull", "replaceToolWhenBroken",
        "pauseAtNight", "use", "find", "item",
    };

    private static readonly HashSet<string> PureObjects = new(StringComparer.Ordinal) { "Math", "JSON", "Object", "Array", "Number", "String", "console", "inv", "Date", "me" };

    /// <summary>Calls whose result needs no check (they fail the script themselves, or return nothing worth checking).</summary>
    private static readonly HashSet<string> SelfChecked = new(StringComparer.Ordinal)
    {
        "wait", "waitFor", "waitfor", "log", "print", "prog", "cp", "expect", "ensure", "room", "fail", "on", "say", "emote", "wake", "upto",
        "until", "retry", "seq", "sel", "timeout", "store", "restock", "mineGen", "harvestFarm", "chopTrees", "deliver", "waitForDay",
        "guardArea", "machine", "use", "parallel", "spawn", "everyBody", "signal", "gather", "stash", "handTo", "makeRoom", "patrol", "quota", "forEach",
    };

    /// <summary>The lint rule on a call to a deprecated compat helper (<see cref="Sdk.SdkModules.Compat"/>).</summary>
    public const string CompatRule = "compat";

    private static readonly HashSet<string> GenCalls = new(StringComparer.Ordinal) { "gen", "harvest_generator", "harvestGenerator" };

    private static readonly HashSet<string> MachineTopics = new(new[]
    {
        "danger", "lowhp", "void", "died", "chat", "join", "hungry", "invFull", "toolBroke", "hurt", "night", "day",
        "player", "block", "item", "mob", "me", "time",
    }.Concat(Events.GameEventCatalog.Names), StringComparer.Ordinal);

    private sealed class Ctx
    {
        public required HashSet<string> Actions;
        public required IReadOnlyDictionary<string, BindingSignature>? Sigs;
        public readonly List<LintFinding> Findings = new();
        public readonly HashSet<string> Declared = new(StringComparer.Ordinal);
        public readonly List<Identifier> Refs = new();
        public readonly HashSet<string> TopFunctions = new(StringComparer.Ordinal);
        public readonly HashSet<string> Called = new(StringComparer.Ordinal);
        public int ActionSites;
        public bool HasCp, HasEnsure, UsesCrew;
        public (int Line, string Call)? NeedsPickaxe;
        public readonly List<(int Line, string Role)> Roles = new();
        public readonly List<(int Line, string Function)> Says = new();
        public int LoopDepth;
        public string? Function;          // the enclosing top-level function (lane roles)
        public readonly Stack<LoopInfo> Loops = new();
        public void Add(LintSeverity s, int line, string rule, string msg) => Findings.Add(new LintFinding(s, line, rule, msg));
    }

    private sealed class LoopInfo
    {
        public required int Line;
        public required string Kind;
        public bool Unbounded;
        public bool Acts;
        public bool Exits;
    }

    public static Script? Parse(string code, out LintFinding? error)
    {
        error = null;
        try
        {
            return new Parser(new ParserOptions { AllowReturnOutsideFunction = true, AllowAwaitOutsideFunction = true })
                .ParseScript(code ?? "", "script", strict: true);
        }
        catch (ParseErrorException pe)
        {
            error = new LintFinding(LintSeverity.Error, pe.LineNumber, "syntax", "syntax: " + pe.Description);
            return null;
        }
    }

    /// <param name="actions">Game function names the script can call (snake_case; camelCase forms are added here).</param>
    public static LintReport Check(string code, IEnumerable<string> actions, int longScriptActions = 6, IReadOnlyDictionary<string, BindingSignature>? signatures = null)
    {
        var acts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in actions) { acts.Add(a); acts.Add(Runtime.Js.JsRuntime.Camel(a)); }
        var ast = Parse(code, out var err);
        if (ast == null) return new LintReport([err!]);
        var c = new Ctx { Actions = acts, Sigs = signatures };
        foreach (var st in ast.Body)
            if (st is FunctionDeclaration { Id: { } fid }) c.TopFunctions.Add(fid.Name);
            else if (st is VariableDeclaration vd)
                foreach (var d in vd.Declarations)
                    if (d.Id is Identifier vid && d.Init is ArrowFunctionExpression or FunctionExpression) c.TopFunctions.Add(vid.Name);
        foreach (var st in ast.Body) Walk(st, null, c);

        // Unknown globals: referenced, never declared, not the SDK, a game function or standard JS.
        var known = new HashSet<string>(StandardGlobals, StringComparer.Ordinal);
        known.UnionWith(Sdk.SdkModules.Globals);
        known.UnionWith(acts);
        var candidates = known.Concat(c.Declared).ToList();
        foreach (var g in c.Refs.Where(r => !c.Declared.Contains(r.Name)).GroupBy(r => r.Name))
        {
            var r = g.First();
            if (Forbidden.TryGetValue(r.Name, out var why)) c.Add(LintSeverity.Error, Line(r), "forbidden", $"{r.Name}: {why}");
            else if (r.Name.StartsWith("__", StringComparison.Ordinal)) c.Add(LintSeverity.Error, Line(r), "forbidden", $"{r.Name}: host internals aren't reachable");
            else if (!known.Contains(r.Name))
            {
                var near = ScriptSession.Closest(r.Name, candidates, 2);
                c.Add(LintSeverity.Error, Line(r), "unknown-global", $"unknown name '{r.Name}'" + (near.Count > 0 ? $" (did you mean {string.Join(" or ", near)}?)" : ""));
            }
        }

        if (!c.HasCp && c.ActionSites > longScriptActions && !c.Called.Contains("machine"))
            c.Add(LintSeverity.Warning, 1, "no-cp", $"{c.ActionSites} actions and no cp(\"label\") (add one before each long phase, or use machine())");
        if (c.NeedsPickaxe is { } np && !c.HasEnsure)
            c.Add(LintSeverity.Warning, np.Line, "no-ensure", $"{np.Call}() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)");
        CheckCrew(ast, c);
        var unique = c.Findings.GroupBy(f => (f.Rule, f.Line)).Select(g => g.First())
            .OrderBy(f => f.Severity == LintSeverity.Error ? 0 : 1).ThenBy(f => f.Line).ToList();
        return new LintReport(unique);
    }

    private static int Line(Node n) => n.Location.Start.Line;

    // ------------------------------------------------------------------ the walk

    private static void Walk(Node? n, Node? parent, Ctx c)
    {
        switch (n)
        {
            case null: return;
            case Identifier id:
                c.Refs.Add(id);
                return;
            case VariableDeclarator vd:
                Bind(vd.Id, c);
                Walk(vd.Init, vd, c);
                return;
            case FunctionDeclaration fd:
                if (fd.Id != null) c.Declared.Add(fd.Id.Name);
                WalkFunction(fd.Params, fd.Body, fd.Id?.Name, parent, c);
                return;
            case FunctionExpression fe:
                if (fe.Id != null) c.Declared.Add(fe.Id.Name);
                WalkFunction(fe.Params, fe.Body, null, parent, c);
                return;
            case ArrowFunctionExpression af:
                WalkFunction(af.Params, af.Body, null, parent, c);
                return;
            case CatchClause cc:
                if (cc.Param != null) Bind(cc.Param, c);
                Walk(cc.Body, cc, c);
                return;
            case ClassDeclaration cd:
                if (cd.Id != null) c.Declared.Add(cd.Id.Name);
                Walk(cd.SuperClass, cd, c);
                Walk(cd.Body, cd, c);
                return;
            case MemberExpression me:
                Walk(me.Object, me, c);
                if (me.Computed) Walk(me.Property, me, c);
                return;
            case Property p:
                if (p.Computed) Walk(p.Key, p, c);
                Walk(p.Value, p, c);
                return;
            case ClassProperty cp:
                if (cp.Computed) Walk(cp.Key, cp, c);
                foreach (var ch in cp.ChildNodes) if (!ReferenceEquals(ch, cp.Key)) Walk(ch, cp, c);
                return;
            case LabeledStatement ls:
                Walk(ls.Body, ls, c);
                return;
            case BreakStatement or ContinueStatement or MetaProperty or DebuggerStatement:
                if (n is BreakStatement && c.Loops.Count > 0) c.Loops.Peek().Exits = true;
                if (n is DebuggerStatement) c.Add(LintSeverity.Error, Line(n), "forbidden", "debugger statement");
                return;
            case ReturnStatement or ThrowStatement:
                foreach (var l in c.Loops) l.Exits = true;
                foreach (var ch in n.ChildNodes) Walk(ch, n, c);
                return;
            case ImportExpression:
                c.Add(LintSeverity.Error, Line(n), "forbidden", "import(): no modules, everything is a global");
                return;
            case NewExpression ne when ne.Callee is Identifier { Name: "Promise" }:
                c.Add(LintSeverity.Warning, Line(n), "promise", "new Promise: host calls are synchronous (no Promise or await needed)");
                break;
            case AssignmentExpression ae when ae.Left is Identifier tgt && !c.Declared.Contains(tgt.Name)
                                             && (Sdk.SdkModules.Globals.Contains(tgt.Name) || c.Actions.Contains(tgt.Name)):
                c.Add(LintSeverity.Error, Line(n), "readonly-global", $"{tgt.Name} is read-only (SDK/game function): use your own name or carry");
                break;
            case CallExpression call:
                Call(call, parent, c);
                return;
            case WhileStatement or DoWhileStatement or ForStatement or ForInStatement or ForOfStatement:
                Loop((Statement)n, c);
                return;
        }
        foreach (var ch in n.ChildNodes) Walk(ch, n, c);
    }

    private static void WalkFunction(in NodeList<Node> ps, Node body, string? name, Node? parent, Ctx c)
    {
        foreach (var p in ps) Bind(p, c);
        // A function passed to upto/until/retry/quota/forEach/patrol is a loop body.
        bool loopFn = parent is CallExpression { Callee: Identifier { Name: "upto" or "until" or "retry" or "quota" or "forEach" or "patrol" } };
        var outerFn = c.Function;
        if (name != null && c.TopFunctions.Contains(name) && outerFn == null) c.Function = name;
        if (loopFn) c.Loops.Push(new LoopInfo { Line = Line(body), Kind = "fn" });
        Walk(body, parent, c);
        if (loopFn) c.Loops.Pop();
        c.Function = outerFn;
    }

    private static void Bind(Node? pattern, Ctx c)
    {
        switch (pattern)
        {
            case null: return;
            case Identifier id: c.Declared.Add(id.Name); return;
            case ObjectPattern op:
                foreach (var prop in op.Properties)
                {
                    if (prop is Property p) { if (p.Computed) Walk(p.Key, p, c); Bind(p.Value, c); }
                    else Bind(prop, c);
                }
                return;
            case ArrayPattern ap:
                foreach (var e in ap.Elements) Bind(e, c);
                return;
            case AssignmentPattern asp:
                Bind(asp.Left, c);
                Walk(asp.Right, asp, c);
                return;
            case RestElement re:
                Bind(re.Argument, c);
                return;
            default:
                Walk(pattern, null, c);
                return;
        }
    }

    private static void Loop(Statement s, Ctx c)
    {
        var (test, body, kind) = s switch
        {
            WhileStatement w => (w.Test, (Node)w.Body, "while"),
            DoWhileStatement d => (d.Test, (Node)d.Body, "do-while"),
            ForStatement f => (f.Test, (Node)f.Body, "for"),
            ForInStatement fi => ((Expression?)null, (Node)fi.Body, "for-in"),
            ForOfStatement fo => ((Expression?)null, (Node)fo.Body, "for-of"),
            _ => ((Expression?)null, (Node)s, "loop"),
        };
        bool unbounded = s is WhileStatement or DoWhileStatement && AlwaysTrue(test) || s is ForStatement { Test: null };
        var info = new LoopInfo { Line = Line(s), Kind = kind, Unbounded = unbounded };
        foreach (var ch in s.ChildNodes) if (!ReferenceEquals(ch, body)) Walk(ch, s, c);
        c.Loops.Push(info);
        Walk(body, s, c);
        c.Loops.Pop();
        if (info.Acts) foreach (var outer in c.Loops) outer.Acts = true;
        if (!unbounded) return;
        if (!info.Acts)
            c.Add(LintSeverity.Error, info.Line, "busy-loop", $"unbounded {kind} loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait())");
        else if (!info.Exits)
            c.Add(LintSeverity.Warning, info.Line, "unbounded", $"{kind} loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()");
    }

    private static bool AlwaysTrue(Expression? e) => e switch
    {
        BooleanLiteral b => b.Value,
        NumericLiteral n => n.Value != 0,
        StringLiteral s => s.Value.Length > 0,
        _ => false,
    };

    // ------------------------------------------------------------------ calls

    private static string? CalleeName(Expression callee) => callee switch
    {
        Identifier id => id.Name,
        _ => null,
    };

    private static void Call(CallExpression call, Node? parent, Ctx c)
    {
        Walk(call.Callee, call, c);
        foreach (var a in call.Arguments) Walk(a, call, c);
        int line = Line(call);
        string? name = CalleeName(call.Callee);
        bool acts;
        if (name != null)
        {
            c.Called.Add(name);
            acts = !PureCalls.Contains(name);
            if (name == "cp") c.HasCp = true;
            if (name is "ensure" or "acquire" or "mineGen") c.HasEnsure = true;
            // The old strategy helpers (99-compat.js) still run; new code composes the primitives instead (the author's check rejects it).
            if (!c.Declared.Contains(name) && !c.TopFunctions.Contains(name) && Sdk.SdkModules.Compat.TryGetValue(name, out var use))
                c.Add(LintSeverity.Warning, line, CompatRule, $"{name}() is a deprecated compat helper: {use}");
            if (name is "parallel" or "spawn" or "everyBody") { c.UsesCrew = true; CollectRoles(name, call, c); }
            if (name == "machine") CheckMachine(call, c);
            bool isAction = c.Actions.Contains(name);
            if (isAction)
            {
                c.ActionSites++;
                if (c.NeedsPickaxe == null && NeedsPickaxe(name, call)) c.NeedsPickaxe = (line, name);
                if (Signature(name, c.Sigs) is { } sig && Missing(sig, call) is { Count: > 0 } missing)
                    c.Add(LintSeverity.Error, line, "missing-arg", $"{name}() needs {string.Join(" ", missing.Select(m => m + "="))} (args in order: {string.Join(",", sig.Params.Take(4))})");
            }
            if (c.Loops.Count > 0)
            {
                if (name == "say") c.Add(LintSeverity.Warning, line, "say-in-loop", "say inside a loop (spams chat; use log or prog)");
                else if (isAction && !SelfChecked.Contains(name) && parent is ExpressionStatement)
                    c.Add(LintSeverity.Warning, line, "unchecked", $"{name}() result ignored in a loop (const r = {name}(..); expect(r))");
            }
            if (name == "say" && c.Function != null) c.Says.Add((line, c.Function));
        }
        else if (call.Callee is MemberExpression { Object: Identifier obj, Property: Identifier prop, Computed: false })
        {
            acts = !PureObjects.Contains(obj.Name) || obj.Name == "inv" && prop.Name is not ("free" or "has");
            if (obj.Name == "crew") acts = prop.Name is "recv" or "barrier";
            if (prop.Name == "run" && call.Callee is MemberExpression) c.UsesCrew = true;
        }
        else acts = call.Callee is MemberExpression { Property: Identifier { Name: var pn } } ? c.Actions.Contains(pn) || pn is "wait" or "run" or "then" : true;
        if (acts) foreach (var l in c.Loops) l.Acts = true;
        if (name == "fail") foreach (var l in c.Loops) l.Exits = true;
    }

    private static bool NeedsPickaxe(string call, CallExpression e)
    {
        if (GenCalls.Contains(call)) return true;
        if (call != "mine" || e.Arguments.Count == 0 || e.Arguments[0] is not StringLiteral s) return false;
        return LuaLint.NeedsPickaxe(s.Value);
    }

    private static BindingSignature? Signature(string call, IReadOnlyDictionary<string, BindingSignature>? sigs)
    {
        if (sigs is not { Count: > 0 }) return null;
        if (sigs.TryGetValue(call, out var s)) return s;
        var snake = Regex.Replace(call, "([A-Z])", m => "_" + m.Value.ToLowerInvariant());
        if (sigs.TryGetValue(snake, out s)) return s;
        return ScriptSession.Aliases.TryGetValue(call, out var target) && sigs.TryGetValue(target, out var a) ? a : null;
    }

    private enum ArgKind { Str, Num, Nil, Obj, Spread, Other }

    /// <summary>Required string params a literal call can't fill (mirrors the runtime's arg mapping); null = not checkable.</summary>
    private static List<string>? Missing(BindingSignature sig, CallExpression call)
    {
        if (sig.Required.Count == 0) return null;
        var args = call.Arguments.ToList();
        // One non-literal argument (a variable, a call): it may be an options object; known only at runtime.
        if (args.Count == 1 && args[0] is Identifier or MemberExpression or CallExpression or ConditionalExpression or LogicalExpression) return null;
        var filled = new HashSet<string>(StringComparer.Ordinal);
        if (args.Count > 0 && args[^1] is ObjectExpression oe && !IsPosLiteral(oe)
            && (oe.Properties.Count == 0 || oe.Properties.OfType<Property>().Any(p => KeyName(p) is { } k && (sig.Params.Contains(k) || k == "n"))))
        {
            foreach (var p in oe.Properties)
            {
                if (p is SpreadElement) return null;
                if (p is Property pp && KeyName(pp) is { } k && pp.Value is not Identifier { Name: "undefined" } and not NullLiteral)
                    filled.Add(k == "n" && sig.Params.Contains("count") ? "count" : k);
            }
            args.RemoveAt(args.Count - 1);
        }
        int pi = 0;
        for (int i = 0; i < args.Count && pi < sig.Params.Count;)
        {
            var kind = Kind(args[i]);
            if (kind == ArgKind.Spread) { foreach (var p in sig.Params.Skip(pi)) filled.Add(p); break; }
            var name = sig.Params[pi];
            if (ScriptSession.PosParams.Contains(name) && i + 2 < args.Count && Kind(args[i]) == ArgKind.Num && Kind(args[i + 1]) == ArgKind.Num && Kind(args[i + 2]) == ArgKind.Num)
            {
                filled.Add(name);
                i += 3;
                pi++;
                continue;
            }
            if (kind != ArgKind.Nil) filled.Add(name);
            i++;
            pi++;
        }
        return sig.Params.Where(p => sig.Required.Contains(p) && !filled.Contains(p)).ToList();
    }

    private static bool IsPosLiteral(ObjectExpression oe) =>
        new[] { "x", "y", "z" }.All(k => oe.Properties.OfType<Property>().Any(p => KeyName(p) == k));

    private static string? KeyName(Property p) => p.Computed ? null : p.Key switch
    {
        Identifier id => id.Name,
        StringLiteral s => s.Value,
        _ => null,
    };

    private static ArgKind Kind(Node a) => a switch
    {
        StringLiteral or TemplateLiteral => ArgKind.Str,
        NumericLiteral => ArgKind.Num,
        UnaryExpression { Argument: NumericLiteral } => ArgKind.Num,
        NullLiteral or Identifier { Name: "undefined" } => ArgKind.Nil,
        ObjectExpression or ArrayExpression => ArgKind.Obj,
        SpreadElement => ArgKind.Spread,
        _ => ArgKind.Other,
    };

    // ------------------------------------------------------------------ machines

    private static void CheckMachine(CallExpression call, Ctx c)
    {
        if (call.Arguments.Count == 0 || call.Arguments[0] is not ObjectExpression spec) return;
        var props = spec.Properties.OfType<Property>().ToDictionary(p => KeyName(p) ?? "", p => p.Value, StringComparer.Ordinal);
        if (!props.TryGetValue("states", out var statesNode) || statesNode is not ObjectExpression states) return;
        var names = states.Properties.OfType<Property>().Select(KeyName).Where(n => n != null).ToHashSet(StringComparer.Ordinal)!;
        if (props.TryGetValue("initial", out var init) && init is StringLiteral il && !names.Contains(il.Value))
            c.Add(LintSeverity.Error, Line(il), "machine-state", $"machine: initial '{il.Value}' isn't a state ({string.Join(",", names)})");
        void CheckOn(Node? onNode)
        {
            if (onNode is not ObjectExpression on) return;
            foreach (var t in on.Properties.OfType<Property>())
            {
                if (KeyName(t) is { } key && !key.Contains(':') && !MachineTopics.Contains(key))
                    c.Add(LintSeverity.Warning, Line(t), "machine-event", $"machine: event '{key}'? (danger|lowhp|void|died|chat|'chat:<text>'|hungry|invFull|toolBroke|hurt|night|day|after()|every()|when())");
                var target = t.Value is StringLiteral s ? s : t.Value is ObjectExpression o
                    ? o.Properties.OfType<Property>().FirstOrDefault(p => KeyName(p) == "to")?.Value as StringLiteral : null;
                if (target != null && target.Value != "done" && !names.Contains(target.Value))
                    c.Add(LintSeverity.Error, Line(target), "machine-state", $"machine: '{target.Value}' isn't a state ({string.Join(",", names)})");
            }
        }
        if (props.TryGetValue("on", out var global)) CheckOn(global);
        foreach (var st in states.Properties.OfType<Property>())
            if (st.Value is ObjectExpression so && so.Properties.OfType<Property>().FirstOrDefault(p => KeyName(p) == "on") is { } onp) CheckOn(onp.Value);
    }

    // ------------------------------------------------------------------ crews

    private static void CollectRoles(string name, CallExpression call, Ctx c)
    {
        if (name == "spawn" && call.Arguments.Count > 1 && call.Arguments[1] is StringLiteral r) c.Roles.Add((Line(r), r.Value));
        if (name == "everyBody" && call.Arguments.Count > 0 && call.Arguments[0] is StringLiteral er) c.Roles.Add((Line(er), er.Value));
        if (name != "parallel" || call.Arguments.Count == 0 || call.Arguments[0] is not ArrayExpression jobs) return;
        var bodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in jobs.Elements)
        {
            Node? bodyNode = null;
            if (j is ArrayExpression arr && arr.Elements.Count > 1)
            {
                if (arr.Elements[1] is StringLiteral role) c.Roles.Add((Line(role), role.Value));
                bodyNode = arr.Elements[0];
            }
            else if (j is ObjectExpression o)
            {
                if (o.Properties.OfType<Property>().FirstOrDefault(p => KeyName(p) == "role")?.Value is StringLiteral role) c.Roles.Add((Line(role), role.Value));
                bodyNode = o.Properties.OfType<Property>().FirstOrDefault(p => KeyName(p) == "body")?.Value;
            }
            var bodyName = bodyNode switch
            {
                StringLiteral s => s.Value,
                CallExpression { Callee: Identifier { Name: "body" }, Arguments: [StringLiteral bs] } => bs.Value,
                _ => null,
            };
            if (bodyName != null && !bodies.Add(bodyName))
                c.Add(LintSeverity.Error, Line(j!), "crew-dup-body", $"parallel: body {bodyName} twice (one lane per body)");
        }
    }

    private static void CheckCrew(Script ast, Ctx c)
    {
        if (!c.UsesCrew) return;
        foreach (var (line, role) in c.Roles)
            if (!c.TopFunctions.Contains(role))
                c.Add(LintSeverity.Error, line, "crew-unknown-role", $"role '{role}' isn't a top-level function (lanes call it by name)");
        var roles = c.Roles.Select(r => r.Role).ToHashSet(StringComparer.Ordinal);
        foreach (var (line, fn) in c.Says.Where(x => roles.Contains(x.Function)))
            c.Add(LintSeverity.Warning, line, "crew-say", $"say in lane role {fn}: lanes report through their result (only main's account talks)");
        // Every lane reloads the script: its top level may only define functions and constants.
        foreach (var st in ast.Body)
        {
            bool ok = st switch
            {
                FunctionDeclaration or EmptyStatement or Directive => true,
                VariableDeclaration vd => vd.Declarations.All(d => d.Init is null or Literal or ObjectExpression or ArrayExpression or ArrowFunctionExpression
                    or FunctionExpression or TemplateLiteral or UnaryExpression or CallExpression { Callee: Identifier { Name: "params" } }),
                ExpressionStatement { Expression: CallExpression { Callee: Identifier { Name: "params" or "use" or "log" } } } => true,
                _ => false,
            };
            if (!ok)
            {
                c.Add(LintSeverity.Error, Line(st), "crew-toplevel", "crew script: the top level holds only functions and constants (every lane reloads it); move the work into main(args)");
                break;
            }
        }
    }

    // ------------------------------------------------------------------ similarity tokens

    private static readonly Regex Tok = new(@"//[^\n]*|/\*.*?\*/|`(?:\\.|[^`\\])*`|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|\d[\w.]*|[A-Za-z_$][\w$]*|===|!==|=>|&&|\|\||[^\s]",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Tokens with comments dropped: (kind, text). Kinds: s = string, n = number, i = identifier, p = punctuation.</summary>
    public static IEnumerable<(char Kind, string Text)> Tokens(string code)
    {
        foreach (Match m in Tok.Matches(code ?? ""))
        {
            var t = m.Value;
            if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal)) continue;
            char k = t[0] is '"' or '\'' or '`' ? 's' : char.IsDigit(t[0]) ? 'n' : char.IsLetter(t[0]) || t[0] is '_' or '$' ? 'i' : 'p';
            yield return (k, k == 's' ? t[1..^1] : t);
        }
    }

    /// <summary>The keys of a top-level <c>params({...})</c> literal, in order, with their default values as JSON text.</summary>
    public static IReadOnlyList<(string Name, string? Default)> DeclaredParams(string code)
    {
        var ast = Parse(code, out _);
        if (ast == null) return [];
        foreach (var n in ast.DescendantNodes())
            if (n is CallExpression { Callee: Identifier { Name: "params" } } call && call.Arguments.Count > 0 && call.Arguments[0] is ObjectExpression o)
                return o.Properties.OfType<Property>().Where(p => KeyName(p) != null)
                    .Select(p => (KeyName(p)!, p.Value is Literal l ? l.Raw : null)).ToList();
        return [];
    }
}
