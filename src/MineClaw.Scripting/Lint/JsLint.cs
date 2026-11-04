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
        CollectTopFunctions(ast, c);
        foreach (var st in ast.Body) Walk(st, null, c);
        CheckGlobals(c);
        CheckPhases(c, longScriptActions);
        CheckCrew(ast, c);
        var unique = c.Findings.GroupBy(f => (f.Rule, f.Line)).Select(g => g.First())
            .OrderBy(f => f.Severity == LintSeverity.Error ? 0 : 1).ThenBy(f => f.Line).ToList();
        return new LintReport(unique);
    }

    /// <summary>Top-level <c>function f</c> and <c>const f = () =&gt; ...</c>: lane roles and the script's own helpers.</summary>
    private static void CollectTopFunctions(Script ast, Ctx c)
    {
        foreach (var st in ast.Body)
            if (st is FunctionDeclaration { Id: { } fid }) c.TopFunctions.Add(fid.Name);
            else if (st is VariableDeclaration vd)
                foreach (var d in vd.Declarations)
                    if (d.Id is Identifier vid && d.Init is ArrowFunctionExpression or FunctionExpression) c.TopFunctions.Add(vid.Name);
    }

    /// <summary>Unknown globals: referenced, never declared, not the SDK, a game function or standard JS.</summary>
    private static void CheckGlobals(Ctx c)
    {
        var known = new HashSet<string>(StandardGlobals, StringComparer.Ordinal);
        known.UnionWith(Sdk.SdkModules.Globals);
        known.UnionWith(c.Actions);
        var candidates = known.Concat(c.Declared).ToList();
        foreach (var g in c.Refs.Where(r => !c.Declared.Contains(r.Name)).GroupBy(r => r.Name))
            CheckGlobal(g.First(), known, candidates, c);
    }

    private static void CheckGlobal(Identifier r, HashSet<string> known, List<string> candidates, Ctx c)
    {
        if (Forbidden.TryGetValue(r.Name, out var why)) c.Add(LintSeverity.Error, Line(r), "forbidden", $"{r.Name}: {why}");
        else if (r.Name.StartsWith("__", StringComparison.Ordinal)) c.Add(LintSeverity.Error, Line(r), "forbidden", $"{r.Name}: host internals aren't reachable");
        else if (!known.Contains(r.Name))
        {
            var near = ScriptSession.Closest(r.Name, candidates, 2);
            c.Add(LintSeverity.Error, Line(r), "unknown-global", $"unknown name '{r.Name}'" + (near.Count > 0 ? $" (did you mean {string.Join(" or ", near)}?)" : ""));
        }
    }

    /// <summary>A long script without cp/machine, a pickaxe job without ensure.</summary>
    private static void CheckPhases(Ctx c, int longScriptActions)
    {
        if (!c.HasCp && c.ActionSites > longScriptActions && !c.Called.Contains("machine"))
            c.Add(LintSeverity.Warning, 1, "no-cp", $"{c.ActionSites} actions and no cp(\"label\") (add one before each long phase, or use machine())");
        if (c.NeedsPickaxe is { } np && !c.HasEnsure)
            c.Add(LintSeverity.Warning, np.Line, "no-ensure", $"{np.Call}() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)");
    }

    private static int Line(Node n) => n.Location.Start.Line;

    // ------------------------------------------------------------------ the walk

    private static void Walk(Node? n, Node? parent, Ctx c)
    {
        if (n == null || WalkScope(n, parent, c) || WalkPart(n, c) || WalkJump(n, c)) return;
        if (n is CallExpression call)
        {
            Call(call, parent, c);
            return;
        }
        if (n is Statement s && IsLoop(s))
        {
            Loop(s, c);
            return;
        }
        Diagnose(n, c);
        foreach (var ch in n.ChildNodes) Walk(ch, n, c);
    }

    /// <summary>Names and the nodes that declare them (functions, catch, class): handled here, true.</summary>
    private static bool WalkScope(Node n, Node? parent, Ctx c)
    {
        switch (n)
        {
            case Identifier id:
                c.Refs.Add(id);
                return true;
            case VariableDeclarator vd:
                Bind(vd.Id, c);
                Walk(vd.Init, vd, c);
                return true;
            case FunctionDeclaration fd:
                Declare(fd.Id, c);
                WalkFunction(fd.Params, fd.Body, fd.Id?.Name, parent, c);
                return true;
            case FunctionExpression fe:
                Declare(fe.Id, c);
                WalkFunction(fe.Params, fe.Body, null, parent, c);
                return true;
            case ArrowFunctionExpression af:
                WalkFunction(af.Params, af.Body, null, parent, c);
                return true;
            case CatchClause cc:
                Bind(cc.Param, c);
                Walk(cc.Body, cc, c);
                return true;
            case ClassDeclaration cd:
                Declare(cd.Id, c);
                Walk(cd.SuperClass, cd, c);
                Walk(cd.Body, cd, c);
                return true;
            default:
                return false;
        }
    }

    private static void Declare(Identifier? id, Ctx c)
    {
        if (id != null) c.Declared.Add(id.Name);
    }

    /// <summary>Members, properties and labels: only their computed keys and values are references.</summary>
    private static bool WalkPart(Node n, Ctx c)
    {
        switch (n)
        {
            case MemberExpression me:
                Walk(me.Object, me, c);
                if (me.Computed) Walk(me.Property, me, c);
                return true;
            case Property p:
                if (p.Computed) Walk(p.Key, p, c);
                Walk(p.Value, p, c);
                return true;
            case ClassProperty cp:
                WalkClassProperty(cp, c);
                return true;
            case LabeledStatement ls:
                Walk(ls.Body, ls, c);
                return true;
            default:
                return false;
        }
    }

    private static void WalkClassProperty(ClassProperty cp, Ctx c)
    {
        if (cp.Computed) Walk(cp.Key, cp, c);
        foreach (var ch in cp.ChildNodes) if (!ReferenceEquals(ch, cp.Key)) Walk(ch, cp, c);
    }

    /// <summary>Jumps (a break/return/throw ends the loops it leaves), debugger and import(): handled here, true.</summary>
    private static bool WalkJump(Node n, Ctx c)
    {
        switch (n)
        {
            case BreakStatement:
                if (c.Loops.Count > 0) c.Loops.Peek().Exits = true;
                return true;
            case ContinueStatement or MetaProperty:
                return true;
            case DebuggerStatement:
                c.Add(LintSeverity.Error, Line(n), "forbidden", "debugger statement");
                return true;
            case ReturnStatement or ThrowStatement:
                WalkExit(n, c);
                return true;
            case ImportExpression:
                c.Add(LintSeverity.Error, Line(n), "forbidden", "import(): no modules, everything is a global");
                return true;
            default:
                return false;
        }
    }

    /// <summary>A return/throw leaves every enclosing loop.</summary>
    private static void WalkExit(Node n, Ctx c)
    {
        foreach (var l in c.Loops) l.Exits = true;
        foreach (var ch in n.ChildNodes) Walk(ch, n, c);
    }

    private static bool IsLoop(Statement s) => s is WhileStatement or DoWhileStatement or ForStatement or ForInStatement or ForOfStatement;

    /// <summary>Findings on a node whose children are walked as usual: <c>new Promise</c>, assigning an SDK/game name.</summary>
    private static void Diagnose(Node n, Ctx c)
    {
        if (n is NewExpression { Callee: Identifier { Name: "Promise" } })
            c.Add(LintSeverity.Warning, Line(n), "promise", "new Promise: host calls are synchronous (no Promise or await needed)");
        else if (n is AssignmentExpression { Left: Identifier tgt } && IsReadOnlyName(tgt.Name, c))
            c.Add(LintSeverity.Error, Line(n), "readonly-global", $"{tgt.Name} is read-only (SDK/game function): use your own name or carry");
    }

    private static bool IsReadOnlyName(string name, Ctx c) =>
        !c.Declared.Contains(name) && (Sdk.SdkModules.Globals.Contains(name) || c.Actions.Contains(name));

    private static void WalkFunction(in NodeList<Node> ps, Node body, string? name, Node? parent, Ctx c)
    {
        foreach (var p in ps) Bind(p, c);
        bool loopFn = IsLoopBodyCall(parent);
        var outerFn = c.Function;
        if (name != null && c.TopFunctions.Contains(name) && outerFn == null) c.Function = name;
        if (loopFn) c.Loops.Push(new LoopInfo { Line = Line(body), Kind = "fn" });
        Walk(body, parent, c);
        if (loopFn) c.Loops.Pop();
        c.Function = outerFn;
    }

    /// <summary>A function passed to upto/until/retry/quota/forEach/patrol is a loop body.</summary>
    private static bool IsLoopBodyCall(Node? parent) =>
        parent is CallExpression { Callee: Identifier { Name: "upto" or "until" or "retry" or "quota" or "forEach" or "patrol" } };

    private static void Bind(Node? pattern, Ctx c)
    {
        switch (pattern)
        {
            case null: return;
            case Identifier id: c.Declared.Add(id.Name); return;
            case ObjectPattern op:
                BindObject(op, c);
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

    private static void BindObject(ObjectPattern op, Ctx c)
    {
        foreach (var prop in op.Properties)
        {
            if (prop is Property p) { if (p.Computed) Walk(p.Key, p, c); Bind(p.Value, c); }
            else Bind(prop, c);
        }
    }

    private static void Loop(Statement s, Ctx c)
    {
        var (test, body, kind) = LoopParts(s);
        bool unbounded = s is WhileStatement or DoWhileStatement && AlwaysTrue(test) || s is ForStatement { Test: null };
        var info = new LoopInfo { Line = Line(s), Kind = kind, Unbounded = unbounded };
        foreach (var ch in s.ChildNodes) if (!ReferenceEquals(ch, body)) Walk(ch, s, c);
        c.Loops.Push(info);
        Walk(body, s, c);
        c.Loops.Pop();
        if (info.Acts) foreach (var outer in c.Loops) outer.Acts = true;
        if (unbounded) CheckUnbounded(info, c);
    }

    private static (Expression? Test, Node Body, string Kind) LoopParts(Statement s) => s switch
    {
        WhileStatement w => (w.Test, (Node)w.Body, "while"),
        DoWhileStatement d => (d.Test, (Node)d.Body, "do-while"),
        ForStatement f => (f.Test, (Node)f.Body, "for"),
        ForInStatement fi => ((Expression?)null, (Node)fi.Body, "for-in"),
        ForOfStatement fo => ((Expression?)null, (Node)fo.Body, "for-of"),
        _ => ((Expression?)null, (Node)s, "loop"),
    };

    /// <summary>A constant-true loop that never acts is a busy loop; one that acts but never exits runs to a limit.</summary>
    private static void CheckUnbounded(LoopInfo info, Ctx c)
    {
        if (!info.Acts)
            c.Add(LintSeverity.Error, info.Line, "busy-loop", $"unbounded {info.Kind} loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait())");
        else if (!info.Exits)
            c.Add(LintSeverity.Warning, info.Line, "unbounded", $"{info.Kind} loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()");
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
        string? name = CalleeName(call.Callee);
        bool acts = name != null ? NamedCall(name, call, parent, c) : MemberCallActs(call, c);
        if (acts) foreach (var l in c.Loops) l.Acts = true;
        if (name == "fail") foreach (var l in c.Loops) l.Exits = true;
    }

    /// <summary>A call by plain name: SDK bookkeeping, action checks, loop warnings. True when it acts or waits.</summary>
    private static bool NamedCall(string name, CallExpression call, Node? parent, Ctx c)
    {
        int line = Line(call);
        c.Called.Add(name);
        bool acts = !PureCalls.Contains(name);
        NoteSdkCall(name, call, line, c);
        bool isAction = c.Actions.Contains(name);
        if (isAction) ActionCall(name, call, line, c);
        if (c.Loops.Count > 0) LoopCall(name, isAction, parent, line, c);
        if (name == "say" && c.Function != null) c.Says.Add((line, c.Function));
        return acts;
    }

    private static void NoteSdkCall(string name, CallExpression call, int line, Ctx c)
    {
        if (name == "cp") c.HasCp = true;
        if (name is "ensure" or "acquire" or "mineGen") c.HasEnsure = true;
        CheckCompat(name, line, c);
        if (name is "parallel" or "spawn" or "everyBody") { c.UsesCrew = true; CollectRoles(name, call, c); }
        if (name == "machine") CheckMachine(call, c);
    }

    /// <summary>The old strategy helpers (99-compat.js) still run; new code composes the primitives instead (the author's check rejects it).</summary>
    private static void CheckCompat(string name, int line, Ctx c)
    {
        if (!c.Declared.Contains(name) && !c.TopFunctions.Contains(name) && Sdk.SdkModules.Compat.TryGetValue(name, out var use))
            c.Add(LintSeverity.Warning, line, CompatRule, $"{name}() is a deprecated compat helper: {use}");
    }

    private static void ActionCall(string name, CallExpression call, int line, Ctx c)
    {
        c.ActionSites++;
        if (c.NeedsPickaxe == null && NeedsPickaxe(name, call)) c.NeedsPickaxe = (line, name);
        if (Signature(name, c.Sigs) is { } sig && Missing(sig, call) is { Count: > 0 } missing)
            c.Add(LintSeverity.Error, line, "missing-arg", $"{name}() needs {string.Join(" ", missing.Select(m => m + "="))} (args in order: {string.Join(",", sig.Params.Take(4))})");
    }

    private static void LoopCall(string name, bool isAction, Node? parent, int line, Ctx c)
    {
        if (name == "say") c.Add(LintSeverity.Warning, line, "say-in-loop", "say inside a loop (spams chat; use log or prog)");
        else if (isAction && !SelfChecked.Contains(name) && parent is ExpressionStatement)
            c.Add(LintSeverity.Warning, line, "unchecked", $"{name}() result ignored in a loop (const r = {name}(..); expect(r))");
    }

    /// <summary>Whether a member call (<c>obj.f()</c>, <c>x.y.f()</c>, <c>f()()</c>) acts or waits.</summary>
    private static bool MemberCallActs(CallExpression call, Ctx c)
    {
        if (call.Callee is MemberExpression { Object: Identifier obj, Property: Identifier prop, Computed: false })
            return ObjectCallActs(obj.Name, prop.Name, c);
        return call.Callee is MemberExpression { Property: Identifier { Name: var pn } } ? c.Actions.Contains(pn) || pn is "wait" or "run" or "then" : true;
    }

    private static bool ObjectCallActs(string obj, string prop, Ctx c)
    {
        bool acts = !PureObjects.Contains(obj) || obj == "inv" && prop is not ("free" or "has");
        if (obj == "crew") acts = prop is "recv" or "barrier";
        if (prop == "run") c.UsesCrew = true;
        return acts;
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
        if (!FillOptions(sig, args, filled)) return null;
        FillPositional(sig, args, filled);
        return sig.Params.Where(p => sig.Required.Contains(p) && !filled.Contains(p)).ToList();
    }

    /// <summary>A trailing options object fills its keys (<c>n</c> = count) and is dropped from the positional args; false when it spreads (unknowable).</summary>
    private static bool FillOptions(BindingSignature sig, List<Expression> args, HashSet<string> filled)
    {
        if (args.Count == 0 || args[^1] is not ObjectExpression oe || !IsOptions(sig, oe)) return true;
        foreach (var p in oe.Properties)
        {
            if (p is SpreadElement) return false;
            if (OptionKey(sig, p) is { } k) filled.Add(k);
        }
        args.RemoveAt(args.Count - 1);
        return true;
    }

    private static bool IsOptions(BindingSignature sig, ObjectExpression oe) =>
        !IsPosLiteral(oe) && (oe.Properties.Count == 0 || oe.Properties.OfType<Property>().Any(p => KeyName(p) is { } k && (sig.Params.Contains(k) || k == "n")));

    /// <summary>The param an options property fills (an undefined/null value fills nothing).</summary>
    private static string? OptionKey(BindingSignature sig, Node p) =>
        p is Property pp && KeyName(pp) is { } k && pp.Value is not Identifier { Name: "undefined" } and not NullLiteral
            ? (k == "n" && sig.Params.Contains("count") ? "count" : k) : null;

    /// <summary>Positional args fill params in order; a position param takes three numbers; a spread fills the rest.</summary>
    private static void FillPositional(BindingSignature sig, List<Expression> args, HashSet<string> filled)
    {
        int pi = 0;
        for (int i = 0; i < args.Count && pi < sig.Params.Count;)
        {
            var kind = Kind(args[i]);
            if (kind == ArgKind.Spread) { foreach (var p in sig.Params.Skip(pi)) filled.Add(p); break; }
            var name = sig.Params[pi];
            if (ScriptSession.PosParams.Contains(name) && ThreeNumbers(args, i))
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
    }

    private static bool ThreeNumbers(List<Expression> args, int i) =>
        i + 2 < args.Count && Kind(args[i]) == ArgKind.Num && Kind(args[i + 1]) == ArgKind.Num && Kind(args[i + 2]) == ArgKind.Num;

    private static bool IsPosLiteral(ObjectExpression oe) =>
        new[] { "x", "y", "z" }.All(k => oe.Properties.OfType<Property>().Any(p => KeyName(p) == k));

    private static string? KeyName(Property p) => p.Computed ? null : p.Key switch
    {
        Identifier id => id.Name,
        StringLiteral s => s.Value,
        _ => null,
    };

    /// <summary>The value of the object's first (non-computed) property named <paramref name="key"/>, or null.</summary>
    private static Node? PropValue(ObjectExpression o, string key) =>
        o.Properties.OfType<Property>().FirstOrDefault(p => KeyName(p) == key)?.Value;

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
        CheckMachineSpec(props, states, c);
    }

    /// <summary>The initial state and every transition (global and per state) must name a state.</summary>
    private static void CheckMachineSpec(Dictionary<string, Node> props, ObjectExpression states, Ctx c)
    {
        var names = states.Properties.OfType<Property>().Select(KeyName).Where(n => n != null).ToHashSet(StringComparer.Ordinal)!;
        if (props.TryGetValue("initial", out var init) && init is StringLiteral il && !names.Contains(il.Value))
            c.Add(LintSeverity.Error, Line(il), "machine-state", $"machine: initial '{il.Value}' isn't a state ({string.Join(",", names)})");
        if (props.TryGetValue("on", out var global)) CheckOn(global, names, c);
        foreach (var st in states.Properties.OfType<Property>())
            if (st.Value is ObjectExpression so && PropValue(so, "on") is { } on) CheckOn(on, names, c);
    }

    private static void CheckOn(Node? onNode, HashSet<string?> names, Ctx c)
    {
        if (onNode is not ObjectExpression on) return;
        foreach (var t in on.Properties.OfType<Property>())
        {
            if (KeyName(t) is { } key && !key.Contains(':') && !MachineTopics.Contains(key))
                c.Add(LintSeverity.Warning, Line(t), "machine-event", $"machine: event '{key}'? (danger|lowhp|void|died|chat|'chat:<text>'|hungry|invFull|toolBroke|hurt|night|day|after()|every()|when())");
            var target = TransitionTarget(t.Value);
            if (target != null && target.Value != "done" && !names.Contains(target.Value))
                c.Add(LintSeverity.Error, Line(target), "machine-state", $"machine: '{target.Value}' isn't a state ({string.Join(",", names)})");
        }
    }

    /// <summary><c>event: 'state'</c> or <c>event: { to: 'state', ... }</c>.</summary>
    private static StringLiteral? TransitionTarget(Node value) =>
        value is StringLiteral s ? s : value is ObjectExpression o ? PropValue(o, "to") as StringLiteral : null;

    // ------------------------------------------------------------------ crews

    private static void CollectRoles(string name, CallExpression call, Ctx c)
    {
        if (RoleArg(name, call) is { } r) c.Roles.Add((Line(r), r.Value));
        if (name != "parallel" || call.Arguments.Count == 0 || call.Arguments[0] is not ArrayExpression jobs) return;
        var bodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in jobs.Elements) ParallelJob(j, bodies, c);
    }

    /// <summary>The role literal of <c>spawn(body, 'role')</c> / <c>everyBody('role')</c>.</summary>
    private static StringLiteral? RoleArg(string name, CallExpression call) => name switch
    {
        "spawn" when call.Arguments.Count > 1 => call.Arguments[1] as StringLiteral,
        "everyBody" when call.Arguments.Count > 0 => call.Arguments[0] as StringLiteral,
        _ => null,
    };

    /// <summary>One <c>parallel</c> lane (<c>[body, 'role']</c> or <c>{ body, role }</c>): its role, and one lane per body.</summary>
    private static void ParallelJob(Node? j, HashSet<string> bodies, Ctx c)
    {
        var bodyName = LaneBody(j, c) switch
        {
            StringLiteral s => s.Value,
            CallExpression { Callee: Identifier { Name: "body" }, Arguments: [StringLiteral bs] } => bs.Value,
            _ => null,
        };
        if (bodyName != null && !bodies.Add(bodyName))
            c.Add(LintSeverity.Error, Line(j!), "crew-dup-body", $"parallel: body {bodyName} twice (one lane per body)");
    }

    /// <summary>The lane's body node; its role literal goes to <see cref="Ctx.Roles"/>.</summary>
    private static Node? LaneBody(Node? j, Ctx c)
    {
        if (j is ArrayExpression arr && arr.Elements.Count > 1)
        {
            if (arr.Elements[1] is StringLiteral role) c.Roles.Add((Line(role), role.Value));
            return arr.Elements[0];
        }
        if (j is not ObjectExpression o) return null;
        if (PropValue(o, "role") is StringLiteral orole) c.Roles.Add((Line(orole), orole.Value));
        return PropValue(o, "body");
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
        if (ast.Body.FirstOrDefault(st => !IsCrewTopLevel(st)) is { } bad)
            c.Add(LintSeverity.Error, Line(bad), "crew-toplevel", "crew script: the top level holds only functions and constants (every lane reloads it); move the work into main(args)");
    }

    private static bool IsCrewTopLevel(Statement st) => st switch
    {
        FunctionDeclaration or EmptyStatement or Directive => true,
        VariableDeclaration vd => vd.Declarations.All(d => IsConstantInit(d.Init)),
        ExpressionStatement { Expression: CallExpression { Callee: Identifier { Name: "params" or "use" or "log" } } } => true,
        _ => false,
    };

    private static bool IsConstantInit(Expression? init) => init is null or Literal or ObjectExpression or ArrayExpression or ArrowFunctionExpression
        or FunctionExpression or TemplateLiteral or UnaryExpression or CallExpression { Callee: Identifier { Name: "params" } };

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
            char k = TokenKind(t[0]);
            yield return (k, k == 's' ? t[1..^1] : t);
        }
    }

    private static char TokenKind(char first) =>
        first is '"' or '\'' or '`' ? 's' : char.IsDigit(first) ? 'n' : char.IsLetter(first) || first is '_' or '$' ? 'i' : 'p';

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
