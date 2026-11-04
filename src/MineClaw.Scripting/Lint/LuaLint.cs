using System.Text;
using System.Text.RegularExpressions;

namespace MineClaw.Scripting.Lint;

/// <summary>
/// LEGACY Lua lint (saved Lua versions until migrated). Zero-token static checks on a script before it runs (C#, no model call). Errors: an unbounded <c>while true</c> /
/// <c>repeat ... until false</c> whose body never acts or waits, and globals the sandbox removed. Warnings: action results
/// ignored inside loops, a long script without <c>cp</c>, <c>say</c> inside a loop. A small Lua tokenizer (comments and
/// strings stripped) tracks blocks, so nesting and same-line code are handled.
/// </summary>
internal static class LuaLint
{
    /// <summary>Globals the sandbox removed (or never had): calling them is a hard error.</summary>
    public static readonly IReadOnlySet<string> RemovedGlobals = new HashSet<string>(StringComparer.Ordinal)
    {
        "pcall", "xpcall", "load", "loadfile", "loadstring", "dofile", "require", "collectgarbage", "rawset", "rawget", "rawequal",
        "rawlen", "module", "setfenv", "getfenv", "io", "debug", "coroutine", "package",
    };

    /// <summary>os.* members that exist (os.time / os.clock); any other os.x is removed.</summary>
    private static readonly HashSet<string> OsAllowed = new(StringComparer.Ordinal) { "time", "clock" };

    /// <summary>
    /// Calls that neither act nor wait (queries and plain Lua): a loop calling only these is a busy loop. Anything else — a
    /// game action, wait/upto/retry, or the script's own function (it may act) — counts as acting, so the busy-loop error
    /// never fires on a false guess; the runtime busy-loop guard still backs it up.
    /// </summary>
    private static readonly HashSet<string> PureCalls = new(StringComparer.Ordinal)
    {
        "inv", "me", "block", "near", "where", "time", "night", "players", "log", "print", "prog", "tostring", "tonumber", "type",
        "pairs", "ipairs", "select", "next", "unpack", "expect", "cp", "fail", "setmetatable", "getmetatable", "error", "assert", "on",
    };

    /// <summary>Calls whose result needs no check (they fail the script themselves, or return nothing worth checking).</summary>
    private static readonly HashSet<string> SelfChecked = new(StringComparer.Ordinal)
    {
        "wait", "log", "print", "prog", "cp", "expect", "ensure", "room", "fail", "on", "say", "emote", "wake", "upto",
    };

    private enum T { Ident, Sym, Str, Num }

    private readonly record struct Tok(T Kind, string Text, int Line);

    private sealed class Block
    {
        public required string Kind;     // while, for, repeat, function, if, do
        public required int Line;
        public bool Loop;
        public bool Unbounded;           // while true / repeat ... (until false checked at close)
        public bool Acts;
        public bool UptoFn;              // function passed to upto(): a loop body
    }

    /// <param name="actions">Game action / skill / alias names the script can call (results worth checking).</param>
    /// <param name="longScriptActions">More static action call sites than this and no <c>cp</c> → warning.</param>
    /// <param name="signatures">Binding signatures by tool name (aliases resolved through <see cref="MineClaw.Scripting.ScriptSession.Aliases"/>): a
    /// call that can't fill a required arg is an error before anything runs (live 10-27 21:12: <c>gen{}</c> failed at line 3
    /// with "bad input: missing 'at'" after 5 actions).</param>
    public static LintReport Check(string code, IEnumerable<string> actions, int longScriptActions = 6,
        IReadOnlyDictionary<string, BindingSignature>? signatures = null)
    {
        var acts = new HashSet<string>(actions, StringComparer.Ordinal);
        var pass = new CheckPass(Tokenize(code ?? ""), acts, signatures);
        pass.Run();
        return pass.Report(longScriptActions);
    }

    /// <summary>
    /// One lint walk over the tokens: a stack of open blocks (loops, functions, ifs) that learn whether they act, and the callee
    /// of each open call bracket (a function inside <c>upto(...)</c> is a loop body).
    /// </summary>
    private sealed class CheckPass(List<Tok> toks, HashSet<string> acts, IReadOnlyDictionary<string, BindingSignature>? signatures)
    {
        private readonly HashSet<string> ownFunctions = signatures is { Count: > 0 } ? DefinedNames(toks) : [];
        private readonly List<LintFinding> findings = new();
        private readonly List<Block> stack = new();
        private readonly Stack<string?> parens = new();   // callee name per open '(' / '{' call
        private int actionSites;
        private bool hasCp, hasEnsure;
        private (int Line, string Call, string Tool)? needsTool;

        public void Run()
        {
            for (int i = 0; i < toks.Count; i++)
            {
                var t = toks[i];
                if (t.Kind == T.Ident) i = Ident(i);
                else if (t.Kind == T.Sym) Bracket(t.Text);
            }
        }

        public LintReport Report(int longScriptActions)
        {
            if (!hasCp && actionSites > longScriptActions)
                findings.Add(new(LintSeverity.Warning, 1, "no-cp", $"{actionSites} actions and no cp\"label\" (add one before each long phase)"));
            // A job that needs a tool gets it planned up front (ensure auto-acquires: chest, hive, craft, bootstrap).
            if (needsTool is { } nt && !hasEnsure)
                findings.Add(new(LintSeverity.Warning, nt.Line, "no-ensure", $"{nt.Call}{{}} needs a {nt.Tool}: start with ensure\"{nt.Tool}\" (auto-acquires it)"));
            // One finding per rule and line is enough.
            var unique = findings.GroupBy(f => (f.Rule, f.Line)).Select(g => g.First()).OrderBy(f => f.Severity == LintSeverity.Error ? 0 : 1).ThenBy(f => f.Line).ToList();
            return new LintReport(unique);
        }

        private Tok At(int k) => k >= 0 && k < toks.Count ? toks[k] : default;

        /// <summary>A name token: a block keyword, a member access, a removed global, or a call. Returns the index to continue from.</summary>
        private int Ident(int i)
        {
            var t = toks[i];
            var next = At(i + 1);
            if (BlockKeyword(ref i, t, next)) return i;
            if (IsMemberDot(At(i - 1)))
            {
                OsMember(i, t);
                return i;
            }
            if (t.Text == "cp") hasCp = true;
            if (RemovedGlobal(t, next) || !OpensCall(next)) return i;
            return Call(i, t, next);
        }

        private bool BlockKeyword(ref int i, Tok t, Tok next)
        {
            switch (t.Text)
            {
                case "while":
                    bool unbounded = UnboundedWhile(i, next);
                    OpenLoop(ref i, "while", unbounded, t.Line);
                    return true;
                case "for":
                    OpenLoop(ref i, "for", false, t.Line);
                    return true;
                case "repeat":
                    stack.Add(new Block { Kind = "repeat", Line = t.Line, Loop = true, Unbounded = true });
                    return true;
                case "until":
                    CloseRepeat(next);
                    return true;
                case "function":
                    stack.Add(new Block { Kind = "function", Line = t.Line, UptoFn = parens.Contains("upto") });
                    return true;
                case "if":
                case "do":
                    stack.Add(new Block { Kind = t.Text, Line = t.Line });
                    return true;
                case "end":
                    CloseInnermost();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary><c>while true do</c> / <c>while 1 do</c>.</summary>
        private bool UnboundedWhile(int i, Tok next) =>
            (next.Kind == T.Ident && next.Text == "true" || next.Kind == T.Num) &&
            i + 2 < toks.Count && toks[i + 2].Kind == T.Ident && toks[i + 2].Text == "do";

        /// <summary>A loop block; the matching "do" belongs to it (skipped).</summary>
        private void OpenLoop(ref int i, string kind, bool unbounded, int line)
        {
            stack.Add(new Block { Kind = kind, Line = line, Loop = true, Unbounded = unbounded });
            for (int j = i + 1; j < toks.Count; j++) if (toks[j].Kind == T.Ident && toks[j].Text == "do") { i = j; break; }
        }

        private void CloseRepeat(Tok next)
        {
            int k = stack.FindLastIndex(b => b.Kind == "repeat");
            if (k < 0) return;
            var b = stack[k];
            b.Unbounded = next.Kind == T.Ident && next.Text == "false";
            Close(b);
            stack.RemoveRange(k, stack.Count - k);
        }

        private void CloseInnermost()
        {
            if (stack.Count == 0) return;
            var b = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            Close(b);
        }

        private void Close(Block b)
        {
            if (b.Loop && b.Unbounded && !b.Acts)
                findings.Add(new(LintSeverity.Error, b.Line, "busy-loop",
                    $"unbounded {b.Kind} loop never acts or waits (bound it: upto(n,fn) or for i=1,n; add wait())"));
            // An inner block that acts makes the outer one act too (already propagated on the call).
        }

        private void OsMember(int i, Tok t)
        {
            if (i >= 2 && toks[i - 2].Text == "os" && !OsAllowed.Contains(t.Text))
                findings.Add(new(LintSeverity.Error, t.Line, "removed-global", $"os.{t.Text} is not available (os.time/os.clock only)"));
        }

        private bool RemovedGlobal(Tok t, Tok next)
        {
            if (!RemovedGlobals.Contains(t.Text) || !(OpensCall(next) || next.Kind == T.Sym && next.Text == ".")) return false;
            findings.Add(new(LintSeverity.Error, t.Line, "removed-global", $"{t.Text} is not available in scripts" +
                (t.Text is "pcall" or "xpcall" ? " (check ok,msg instead)" : "")));
            return true;
        }

        private int Call(int i, Tok t, Tok next)
        {
            bool isAction = acts.Contains(t.Text);
            TrackTool(i, t, isAction);
            if (!PureCalls.Contains(t.Text))
                foreach (var b in stack) b.Acts = true;
            if (isAction) actionSites++;
            CheckArgs(i, t);
            if (stack.Any(b => b.Loop || b.UptoFn)) LoopWarnings(i, t, isAction);
            if (next.Kind != T.Sym || next.Text is not ("(" or "{")) return i;
            parens.Push(t.Text);
            return i + 1;
        }

        private void TrackTool(int i, Tok t, bool isAction)
        {
            if (t.Text is "ensure" or "acquire") hasEnsure = true;
            else if (needsTool == null && isAction && NeedsTool(t.Text, toks, i) is { } tool) needsTool = (t.Line, t.Text, tool);
        }

        private void CheckArgs(int i, Tok t)
        {
            if (signatures is not { Count: > 0 } || ownFunctions.Contains(t.Text) || SignatureFor(t.Text, signatures) is not { } sig) return;
            if (MissingArgs(sig, toks, i + 1) is not { Count: > 0 } missing) return;
            findings.Add(new(LintSeverity.Error, t.Line, "missing-arg",
                $"{t.Text} needs {string.Join(" ", missing.Select(m => m + "="))} (args in order: {string.Join(",", sig.Params.Take(4))})"));
        }

        private void LoopWarnings(int i, Tok t, bool isAction)
        {
            if (t.Text == "say")
                findings.Add(new(LintSeverity.Warning, t.Line, "say-in-loop", "say inside a loop (spams chat; use log or prog)"));
            else if (isAction && !SelfChecked.Contains(t.Text) && StatementStart(toks, i))
                findings.Add(new(LintSeverity.Warning, t.Line, "unchecked", $"{t.Text}{{}} result ignored in a loop (local ok,m={t.Text}{{..}} expect(ok,m))"));
        }

        private void Bracket(string text)
        {
            if (text is "(" or "{") parens.Push(null);
            else if (text is ")" or "}" && parens.Count > 0) parens.Pop();
        }
    }

    /// <summary>The token opens a call: <c>f(</c>, <c>f{</c>, <c>f"s"</c>.</summary>
    private static bool OpensCall(Tok next) => next.Kind == T.Sym && next.Text is "(" or "{" || next.Kind == T.Str;

    /// <summary>The token before a name makes it a member (<c>a.b</c>, <c>a:b</c>).</summary>
    private static bool IsMemberDot(Tok prev) => prev.Kind == T.Sym && prev.Text is "." or ":";

    /// <summary>Actions that dig cobble/stone: the generator always, mine/dig when the literal target needs a pickaxe.</summary>
    private static readonly HashSet<string> GenCalls = new(StringComparer.Ordinal) { "gen", "harvest_generator" };

    private static string? NeedsTool(string call, List<Tok> toks, int i)
    {
        if (GenCalls.Contains(call)) return "pickaxe";
        if (call != "mine") return null;
        // mine"cobblestone" / mine{"stone",16}: the first literal after the call (an unknown target = no finding).
        int j = i + 1;
        if (j < toks.Count && toks[j].Kind == T.Sym && toks[j].Text is "{" or "(") j++;
        if (j >= toks.Count || toks[j].Kind != T.Str) return null;
        return NeedsPickaxe(toks[j].Text) ? "pickaxe" : null;
    }

    /// <summary>The block named needs a pickaxe to drop anything, in the process's version (the facades follow
    /// <c>GameData.Current</c>): bare hands harvest nothing, a pickaxe does.</summary>
    internal static bool NeedsPickaxe(string name)
    {
        var b = MineClaw.Data.Blocks.ByName(name.Trim());
        return b != null && !MineClaw.Data.Tools.CanHarvest(b.Id, -1) && MineClaw.Data.Tools.CanHarvest(b.Id, MineClaw.Data.Items.DiamondPickaxe);
    }

    // ------------------------------------------------------------------ required binding args

    /// <summary>
    /// A binding's signature from the input schema of the tool it forwards to: <see cref="BindingSignature.Params"/> = the
    /// positional order (the same order <c>MapArgs</c> fills), <see cref="BindingSignature.Required"/> = the schema's required
    /// string params. Required numbers/booleans are left out: the game tools always default them (<c>count</c> → 16...),
    /// while a missing required string (<c>at</c>, <c>item</c>, <c>block</c>, <c>to</c>) fails the call ("missing 'at'").
    /// Null when the schema has no properties.
    /// </summary>
    public static BindingSignature? SignatureOf(System.Text.Json.JsonElement schema)
    {
        if (schema.ValueKind != System.Text.Json.JsonValueKind.Object || !schema.TryGetProperty("properties", out var props)
            || props.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        var ps = new List<string>();
        var strings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in props.EnumerateObject())
        {
            ps.Add(p.Name);
            if (IsStringProperty(p.Value)) strings.Add(p.Name);
        }
        return new BindingSignature(ps, RequiredStrings(schema, strings));
    }

    private static bool IsStringProperty(System.Text.Json.JsonElement v) =>
        v.ValueKind == System.Text.Json.JsonValueKind.Object && v.TryGetProperty("type", out var ty)
        && ty.ValueKind == System.Text.Json.JsonValueKind.String && ty.GetString() == "string";

    /// <summary>The schema's <c>required</c> names that are string params.</summary>
    private static HashSet<string> RequiredStrings(System.Text.Json.JsonElement schema, HashSet<string> strings)
    {
        var req = new HashSet<string>(StringComparer.Ordinal);
        if (!schema.TryGetProperty("required", out var r) || r.ValueKind != System.Text.Json.JsonValueKind.Array) return req;
        foreach (var x in r.EnumerateArray())
            if (x.ValueKind == System.Text.Json.JsonValueKind.String && x.GetString() is { } n && strings.Contains(n)) req.Add(n);
        return req;
    }

    private static BindingSignature? SignatureFor(string call, IReadOnlyDictionary<string, BindingSignature> sigs) =>
        sigs.TryGetValue(call, out var s) ? s
        : MineClaw.Scripting.ScriptSession.Aliases.TryGetValue(call, out var target) && sigs.TryGetValue(target, out var a) ? a : null;

    /// <summary>Names the script defines itself (<c>function f</c>, <c>local function f</c>, <c>local f =</c>, <c>f = function</c>).</summary>
    private static HashSet<string> DefinedNames(List<Tok> toks)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < toks.Count; i++)
            if (DefinedAt(toks, i) is { } name) set.Add(name);
        return set;
    }

    /// <summary>The name defined at <paramref name="i"/>: <c>function f</c> / <c>local f</c> → f, <c>local function f</c> → f.</summary>
    private static string? DefinedAt(List<Tok> toks, int i)
    {
        if (toks[i].Kind != T.Ident || toks[i].Text is not ("function" or "local")) return null;
        var next = toks[i + 1];
        if (next.Kind == T.Ident && next.Text != "function") return next.Text;
        return toks[i].Text == "local" && next.Text == "function" && i + 2 < toks.Count && toks[i + 2].Kind == T.Ident ? toks[i + 2].Text : null;
    }

    private enum ArgKind { Str, Num, Nil, Table, Multi, Other }

    private sealed record Arg(ArgKind Kind, string? Name, int Start, int End);

    /// <summary>
    /// Required params a call can't fill, simulating the binding's Lua → JSON mapping: one table literal = its array part
    /// positional + its named keys (<c>n</c> = count); otherwise the arguments are positional; a position param takes three
    /// numbers. A call whose args can't be known statically (one variable/table expression, a trailing call or <c>...</c>
    /// that may expand) is never flagged. Null = not a checkable call.
    /// </summary>
    private static List<string>? MissingArgs(BindingSignature sig, List<Tok> toks, int j)
    {
        if (sig.Required.Count == 0 || j >= toks.Count) return null;
        if (CallArgs(toks, j) is not { } args) return null;
        var filled = new HashSet<string>(StringComparer.Ordinal);
        var pos = args.Where(a => a.Name == null).ToList();
        FillNamed(sig, args, pos, filled);
        FillPositional(sig, pos, filled);
        return sig.Params.Where(p => sig.Required.Contains(p) && !filled.Contains(p)).ToList();
    }

    /// <summary>The call's arguments starting at its first token (a string, a table or a paren list); null = not checkable.</summary>
    private static List<Arg>? CallArgs(List<Tok> toks, int j)
    {
        var first = toks[j];
        if (first.Kind == T.Str) return [new Arg(ArgKind.Str, null, j, j)];
        if (first.Kind != T.Sym) return null;
        return first.Text switch
        {
            "{" => Elements(toks, j, "}") ?? [],
            "(" => ParenArgs(toks, j),
            _ => null,
        };
    }

    private static List<Arg>? ParenArgs(List<Tok> toks, int j)
    {
        if (Elements(toks, j, ")") is not { } list) return null;
        if (list.Count != 1) return list;
        var only = list[0];
        if (only.Kind == ArgKind.Table && only.Name == null) return Elements(toks, only.Start, "}") ?? [];
        return only.Kind is ArgKind.Other or ArgKind.Multi ? null : list; // go(p), place(t): runtime shape
    }

    private static readonly string[] XyzKeys = ["x", "y", "z"];

    /// <summary>Named keys fill their params (<c>n</c> = count); a position table as the whole argument fills the first param.</summary>
    private static void FillNamed(BindingSignature sig, List<Arg> args, List<Arg> pos, HashSet<string> filled)
    {
        // dig{x=1,y=64,z=2}: a position table as the whole argument fills the first (position) param.
        if (sig.Params.Count > 0 && PosParamNames.Contains(sig.Params[0]) && pos.Count == 0
            && XyzKeys.All(k => args.Any(a => a.Name == k))) filled.Add(sig.Params[0]);
        foreach (var a in args.Where(a => a.Name != null && a.Kind != ArgKind.Nil))
            filled.Add(a.Name == "n" && sig.Params.Contains("count") ? "count" : a.Name!);
    }

    /// <summary>Positional args fill params in order; a position param takes three numbers; a trailing call / ... fills the rest.</summary>
    private static void FillPositional(BindingSignature sig, List<Arg> pos, HashSet<string> filled)
    {
        int pi = 0;
        for (int i = 0; i < pos.Count && pi < sig.Params.Count;)
        {
            var name = sig.Params[pi];
            // Only a trailing call / ... can expand to several values; elsewhere it is one value.
            if (pos[i].Kind == ArgKind.Multi && i == pos.Count - 1) { foreach (var p in sig.Params.Skip(pi)) filled.Add(p); break; }
            if (PosParamNames.Contains(name) && ThreeNumbers(pos, i))
            {
                filled.Add(name);
                i += 3;
                pi++;
                continue;
            }
            if (pos[i].Kind != ArgKind.Nil) filled.Add(name);
            i++;
            pi++;
        }
    }

    private static bool ThreeNumbers(List<Arg> pos, int i) =>
        i + 2 < pos.Count && pos[i].Kind == ArgKind.Num && pos[i + 1].Kind == ArgKind.Num && pos[i + 2].Kind == ArgKind.Num;

    private static readonly HashSet<string> PosParamNames = new(StringComparer.OrdinalIgnoreCase) { "to", "at", "pos" };

    /// <summary>Top-level elements between the bracket at <paramref name="open"/> and its match (null = unbalanced).</summary>
    private static List<Arg>? Elements(List<Tok> toks, int open, string close)
    {
        var list = new List<Arg>();
        int depth = 0, blocks = 0, start = open + 1;
        for (int k = open + 1; k < toks.Count; k++)
        {
            var t = toks[k];
            if (t.Kind != T.Sym) { blocks += BlockDelta(t); continue; }
            bool top = depth == 0 && blocks <= 0;
            int d = BracketDelta(t.Text);
            if (d < 0 && top) return t.Text == close ? AddElement(list, toks, start, k) : null;
            depth += d;
            if (top && t.Text is "," or ";")
            {
                AddElement(list, toks, start, k);
                start = k + 1;
            }
        }
        return null;
    }

    private static List<Arg> AddElement(List<Arg> list, List<Tok> toks, int start, int k)
    {
        if (k > start) list.Add(Classify(toks, start, k - 1));
        return list;
    }

    /// <summary>+1 for a keyword that opens a block, -1 for one that closes it.</summary>
    private static int BlockDelta(Tok t) => t.Kind != T.Ident ? 0 : t.Text switch
    {
        "function" or "if" or "do" or "repeat" => 1,
        "end" or "until" => -1,
        _ => 0,
    };

    private static int BracketDelta(string s) => s switch
    {
        "(" or "{" or "[" => 1,
        ")" or "}" or "]" => -1,
        _ => 0,
    };

    private static Arg Classify(List<Tok> toks, int s, int e)
    {
        string? name = null;
        // key = value / ["key"] = value (not ==)
        if (IsNameKey(toks, s, e))
        {
            name = toks[s].Text;
            s += 2;
        }
        else if (IsBracketKey(toks, s, e))
        {
            name = toks[s + 1].Text;
            s += 4;
        }
        return new Arg(KindOf(toks, s, e), name, s, e);
    }

    private static bool IsNameKey(List<Tok> toks, int s, int e) =>
        e - s >= 2 && toks[s].Kind == T.Ident && IsSym(toks[s + 1], "=") && !IsSym(toks[s + 2], "=");

    private static bool IsBracketKey(List<Tok> toks, int s, int e) =>
        e - s >= 4 && toks[s].Text == "[" && toks[s + 1].Kind == T.Str && toks[s + 2].Text == "]" && toks[s + 3].Text == "=";

    private static bool IsSym(Tok t, string text) => t.Kind == T.Sym && t.Text == text;

    private static ArgKind KindOf(List<Tok> toks, int s, int e)
    {
        int len = e - s + 1;
        if (LiteralKind(toks, s, len) is { } literal) return literal;
        if (IsSym(toks[s], "{") && MatchClose(toks, s) == e) return ArgKind.Table;
        // f(x), f"x", f{..}, or ... (tokenized ".." ".")
        if (IsCallTail(toks, s, e, len) || len == 2 && toks[s].Text == ".." && toks[s + 1].Text == ".") return ArgKind.Multi;
        return ArgKind.Other;
    }

    private static ArgKind? LiteralKind(List<Tok> toks, int s, int len) => len switch
    {
        1 => toks[s].Kind switch
        {
            T.Str => ArgKind.Str,
            T.Num => ArgKind.Num,
            T.Ident when toks[s].Text == "nil" => ArgKind.Nil,
            _ => null,
        },
        2 when toks[s].Text == "-" && toks[s + 1].Kind == T.Num => ArgKind.Num,
        _ => null,
    };

    private static bool IsCallTail(List<Tok> toks, int s, int e, int len) =>
        toks[e].Kind == T.Sym && toks[e].Text is ")" || toks[e].Kind == T.Str && len >= 2 && toks[e - 1].Kind == T.Ident
        || toks[e].Kind == T.Sym && toks[e].Text == "}" && len >= 2 && toks[s].Kind == T.Ident;

    private static int MatchClose(List<Tok> toks, int open)
    {
        int depth = 0;
        for (int k = open; k < toks.Count; k++)
        {
            if (toks[k].Kind != T.Sym) continue;
            if (toks[k].Text is "(" or "{" or "[") depth++;
            else if (toks[k].Text is ")" or "}" or "]" && --depth == 0) return k;
        }
        return -1;
    }

    private static readonly HashSet<string> ExprKeywords = new(StringComparer.Ordinal)
    {
        "return", "not", "and", "or", "local", "in", "elseif", "while", "until", "if", "for",
    };

    /// <summary>
    /// True when the call at <paramref name="i"/> starts a statement (its result is thrown away): after a block keyword, a
    /// closing bracket, a literal or a plain name; not after '=', ',', '(', an operator or an expression keyword.
    /// </summary>
    private static bool StatementStart(List<Tok> toks, int i)
    {
        if (i == 0) return true;
        var p = toks[i - 1];
        return p.Kind switch
        {
            T.Ident => !ExprKeywords.Contains(p.Text),
            T.Sym => p.Text is ")" or "}" or "]" or ";",
            _ => true,
        };
    }

    private static List<Tok> Tokenize(string code)
    {
        var lexer = new Lexer(code);
        while (lexer.More) lexer.Next();
        return lexer.Tokens;
    }

    /// <summary>The Lua tokenizer: comments dropped, strings and long brackets as one token, line numbers kept.</summary>
    private sealed class Lexer(string code)
    {
        public readonly List<Tok> Tokens = new();
        private readonly int n = code.Length;
        private int line = 1, i;

        public bool More => i < n;

        public void Next()
        {
            char c = code[i];
            if (Trivia(c)) return;
            if (c == '[' && LongBracket(code, i) is { } level) LongString(level);
            else if (c is '"' or '\'') Quoted(c);
            else Word(c);
        }

        /// <summary>Whitespace (counting lines) or a comment: skipped.</summary>
        private bool Trivia(char c)
        {
            if (c == '-' && i + 1 < n && code[i + 1] == '-')
            {
                Comment();
                return true;
            }
            if (!char.IsWhiteSpace(c)) return false;
            if (c == '\n') line++;
            i++;
            return true;
        }

        private void Comment()
        {
            i += 2;
            if (i < n && code[i] == '[' && LongBracket(code, i) is { } lvl) SkipLong(lvl);
            else while (i < n && code[i] != '\n') i++;
        }

        /// <summary>Skips a long bracket of <paramref name="level"/> from <see cref="i"/> to its close (or the end), counting lines.</summary>
        private void SkipLong(int level)
        {
            int close = code.IndexOf("]" + new string('=', level) + "]", i, StringComparison.Ordinal);
            int end = close < 0 ? n : close + level + 2;
            line += Count(code, i, end, '\n');
            i = end;
        }

        private void LongString(int level)
        {
            Tokens.Add(new Tok(T.Str, "S", line));
            SkipLong(level);
        }

        private void Quoted(char quote)
        {
            int start = line, from = i + 1;
            i++;
            while (i < n && code[i] != quote)
            {
                if (code[i] == '\\' && i + 1 < n) i++;
                if (code[i] == '\n') line++;
                i++;
            }
            // The literal's text rides along (tool-needing mine targets); every other consumer reads Str as "S".
            Tokens.Add(new Tok(T.Str, code[from..Math.Min(i, n)], start));
            i++;
        }

        private void Word(char c)
        {
            if (char.IsDigit(c)) Run(T.Num, ch => char.IsLetterOrDigit(ch) || ch == '.');
            else if (char.IsLetter(c) || c == '_') Run(T.Ident, ch => char.IsLetterOrDigit(ch) || ch == '_');
            else Symbol(c);
        }

        private void Run(T kind, Func<char, bool> part)
        {
            int s = i;
            while (i < n && part(code[i])) i++;
            Tokens.Add(new Tok(kind, code[s..i], line));
        }

        private void Symbol(char c)
        {
            if (c == '.' && i + 1 < n && code[i + 1] == '.')
            {
                Tokens.Add(new Tok(T.Sym, "..", line));
                i += 2;
                return;
            }
            Tokens.Add(new Tok(T.Sym, c.ToString(), line));
            i++;
        }
    }

    private static int? LongBracket(string s, int i)
    {
        if (s[i] != '[') return null;
        int j = i + 1, lvl = 0;
        while (j < s.Length && s[j] == '=') { lvl++; j++; }
        return j < s.Length && s[j] == '[' ? lvl : null;
    }

    private static int Count(string s, int from, int to, char c)
    {
        int k = 0;
        for (int i = from; i < Math.Min(to, s.Length); i++) if (s[i] == c) k++;
        return k;
    }

    // ------------------------------------------------------------------ similarity (near-duplicate detection)

    private static readonly Regex CommentRx = new(@"--\[(=*)\[.*?\]\1\]|--[^\n]*", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Normalized token stream: comments dropped, strings → S, numbers → N (so a script differing only in literals matches).</summary>
    public static IReadOnlyList<string> Shape(string code) =>
        Tokenize(code ?? "").Select(t => t.Kind switch { T.Str => "S", T.Num => "N", _ => t.Text }).ToList();

    /// <summary>Exact-duplicate key: comments and whitespace dropped, literals kept.</summary>
    public static string ExactKey(string code)
    {
        var sb = new StringBuilder();
        foreach (var t in Tokenize(CommentRx.Replace(code ?? "", ""))) sb.Append(t.Kind == T.Str ? "S" : t.Text).Append(' ');
        // strings matter for exact duplicates: keep their text
        return sb.Append('|').Append(string.Join("|", Regex.Matches(CommentRx.Replace(code ?? "", ""), @"""[^""]*""|'[^']*'").Select(m => m.Value))).ToString();
    }

    /// <summary>Called names (actions, helpers): the script's "purpose tags" next to its first comment line.</summary>
    public static IReadOnlySet<string> CalledNames(string code)
    {
        var toks = Tokenize(code ?? "");
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < toks.Count; i++)
            if (toks[i].Kind == T.Ident && OpensCall(toks[i + 1]) && !(i > 0 && IsMemberDot(toks[i - 1])) && toks[i].Text != "function")
                set.Add(toks[i].Text);
        return set;
    }

    /// <summary>
    /// 0..1 similarity: Jaccard over token 3-grams of the normalized shape (85%) plus Jaccard of purpose tags (called names +
    /// purpose words, 15%).
    /// </summary>
    public static double Similarity(string a, string b, IEnumerable<string>? tagsA = null, IEnumerable<string>? tagsB = null)
    {
        double code = Jaccard(Grams(Shape(a)), Grams(Shape(b)));
        var ta = new HashSet<string>(CalledNames(a).Concat(tagsA ?? []), StringComparer.OrdinalIgnoreCase);
        var tb = new HashSet<string>(CalledNames(b).Concat(tagsB ?? []), StringComparer.OrdinalIgnoreCase);
        double tags = ta.Count == 0 && tb.Count == 0 ? 1 : Jaccard(ta, tb);
        return 0.85 * code + 0.15 * tags;

        static HashSet<string> Grams(IReadOnlyList<string> t)
        {
            var g = new HashSet<string>(StringComparer.Ordinal);
            if (t.Count < 3) { if (t.Count > 0) g.Add(string.Join(" ", t)); return g; }
            for (int i = 0; i + 2 < t.Count; i++) g.Add(t[i] + " " + t[i + 1] + " " + t[i + 2]);
            return g;
        }

        static double Jaccard(HashSet<string> x, HashSet<string> y)
        {
            if (x.Count == 0 && y.Count == 0) return 1;
            int inter = x.Count(y.Contains);
            return (double)inter / (x.Count + y.Count - inter);
        }
    }

    /// <summary>The first comment line ("-- purpose: mine the gen" → "mine the gen"), or null.</summary>
    public static string? Purpose(string code)
    {
        foreach (var raw in (code ?? "").Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0) continue;
            if (!l.StartsWith("--", StringComparison.Ordinal)) return null;
            var p = Regex.Replace(l.TrimStart('-').Trim(), @"^(?:purpose|does|job)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
            if (p.StartsWith("migrated from routine", StringComparison.Ordinal)) return null;
            return p.Length == 0 ? null : (p.Length <= 60 ? p : p[..57] + "...");
        }
        return null;
    }

    /// <summary>Arg names the script reads (<c>args.n</c>, <c>args["cell"]</c>), in first-use order: its call signature.</summary>
    public static IReadOnlyList<string> ArgNames(string code)
    {
        var names = new List<string>();
        foreach (Match m in Regex.Matches(CommentRx.Replace(code ?? "", ""), @"\bargs\s*(?:\.\s*([A-Za-z_]\w*)|\[\s*[""']([A-Za-z_]\w*)[""']\s*\])"))
        {
            var n = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!names.Contains(n)) names.Add(n);
        }
        return names;
    }
}
