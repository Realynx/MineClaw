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
        var toks = Tokenize(code ?? "");
        var ownFunctions = signatures is { Count: > 0 } ? DefinedNames(toks) : [];
        var findings = new List<LintFinding>();
        var stack = new List<Block>();
        var parens = new Stack<string?>();   // callee name per open '(' / '{' call
        int actionSites = 0;
        bool hasCp = false, hasEnsure = false;
        (int Line, string Call, string Tool)? needsTool = null;
        bool LoopActive() => stack.Any(b => b.Loop || b.UptoFn);

        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            var prev = i > 0 ? toks[i - 1] : default;
            var next = i + 1 < toks.Count ? toks[i + 1] : default;
            if (t.Kind == T.Ident)
            {
                bool member = prev.Kind == T.Sym && prev.Text is "." or ":";
                bool isCall = next.Kind == T.Sym && next.Text is "(" or "{" || next.Kind == T.Str;
                switch (t.Text)
                {
                    case "while":
                    {
                        bool unbounded = (next.Kind == T.Ident && next.Text == "true" || next.Kind == T.Num) &&
                            i + 2 < toks.Count && toks[i + 2].Kind == T.Ident && toks[i + 2].Text == "do";
                        stack.Add(new Block { Kind = "while", Line = t.Line, Loop = true, Unbounded = unbounded });
                        // the matching "do" belongs to this block
                        for (int j = i + 1; j < toks.Count; j++) if (toks[j].Kind == T.Ident && toks[j].Text == "do") { i = j; break; }
                        continue;
                    }
                    case "for":
                        stack.Add(new Block { Kind = "for", Line = t.Line, Loop = true });
                        for (int j = i + 1; j < toks.Count; j++) if (toks[j].Kind == T.Ident && toks[j].Text == "do") { i = j; break; }
                        continue;
                    case "repeat":
                        stack.Add(new Block { Kind = "repeat", Line = t.Line, Loop = true, Unbounded = true });
                        continue;
                    case "until":
                    {
                        int k = stack.FindLastIndex(b => b.Kind == "repeat");
                        if (k >= 0)
                        {
                            var b = stack[k];
                            bool forever = next.Kind == T.Ident && next.Text == "false";
                            b.Unbounded = forever;
                            Close(b);
                            stack.RemoveRange(k, stack.Count - k);
                        }
                        continue;
                    }
                    case "function":
                        stack.Add(new Block { Kind = "function", Line = t.Line, UptoFn = parens.Contains("upto") });
                        continue;
                    case "if":
                        stack.Add(new Block { Kind = "if", Line = t.Line });
                        continue;
                    case "do":
                        stack.Add(new Block { Kind = "do", Line = t.Line });
                        continue;
                    case "end":
                        if (stack.Count > 0)
                        {
                            var b = stack[^1];
                            stack.RemoveAt(stack.Count - 1);
                            Close(b);
                        }
                        continue;
                    case "cp":
                        if (!member) hasCp = true;
                        break;
                }

                if (member)
                {
                    if (prev.Kind == T.Sym && i >= 2 && toks[i - 2].Text == "os" && !OsAllowed.Contains(t.Text))
                        findings.Add(new(LintSeverity.Error, t.Line, "removed-global", $"os.{t.Text} is not available (os.time/os.clock only)"));
                    continue;
                }
                if (RemovedGlobals.Contains(t.Text) && (isCall || next.Kind == T.Sym && next.Text == "."))
                {
                    findings.Add(new(LintSeverity.Error, t.Line, "removed-global", $"{t.Text} is not available in scripts" +
                        (t.Text is "pcall" or "xpcall" ? " (check ok,msg instead)" : "")));
                    continue;
                }
                if (!isCall) continue;

                bool isAction = acts.Contains(t.Text);
                if (t.Text is "ensure" or "acquire") hasEnsure = true;
                else if (needsTool == null && isAction && NeedsTool(t.Text, toks, i) is { } tool) needsTool = (t.Line, t.Text, tool);
                if (!PureCalls.Contains(t.Text))
                    foreach (var b in stack) b.Acts = true;
                if (isAction) actionSites++;
                if (signatures is { Count: > 0 } && !ownFunctions.Contains(t.Text) && SignatureFor(t.Text, signatures) is { } sig
                    && MissingArgs(sig, toks, i + 1) is { Count: > 0 } missing)
                    findings.Add(new(LintSeverity.Error, t.Line, "missing-arg",
                        $"{t.Text} needs {string.Join(" ", missing.Select(m => m + "="))} (args in order: {string.Join(",", sig.Params.Take(4))})"));
                if (LoopActive())
                {
                    if (t.Text == "say")
                        findings.Add(new(LintSeverity.Warning, t.Line, "say-in-loop", "say inside a loop (spams chat; use log or prog)"));
                    else if (isAction && !SelfChecked.Contains(t.Text) && StatementStart(toks, i))
                        findings.Add(new(LintSeverity.Warning, t.Line, "unchecked", $"{t.Text}{{}} result ignored in a loop (local ok,m={t.Text}{{..}} expect(ok,m))"));
                }
                if (next.Kind == T.Sym && next.Text is "(" or "{") { parens.Push(t.Text); i++; }
                continue;
            }
            if (t.Kind == T.Sym)
            {
                if (t.Text is "(" or "{") parens.Push(null);
                else if (t.Text is ")" or "}") { if (parens.Count > 0) parens.Pop(); }
            }
        }

        if (!hasCp && actionSites > longScriptActions)
            findings.Add(new(LintSeverity.Warning, 1, "no-cp", $"{actionSites} actions and no cp\"label\" (add one before each long phase)"));
        // A job that needs a tool gets it planned up front (ensure auto-acquires: chest, hive, craft, bootstrap).
        if (needsTool is { } nt && !hasEnsure)
            findings.Add(new(LintSeverity.Warning, nt.Line, "no-ensure", $"{nt.Call}{{}} needs a {nt.Tool}: start with ensure\"{nt.Tool}\" (auto-acquires it)"));
        // One finding per rule and line is enough.
        var unique = findings.GroupBy(f => (f.Rule, f.Line)).Select(g => g.First()).OrderBy(f => f.Severity == LintSeverity.Error ? 0 : 1).ThenBy(f => f.Line).ToList();
        return new LintReport(unique);

        void Close(Block b)
        {
            if (b.Loop && b.Unbounded && !b.Acts)
                findings.Add(new(LintSeverity.Error, b.Line, "busy-loop",
                    $"unbounded {b.Kind} loop never acts or waits (bound it: upto(n,fn) or for i=1,n; add wait())"));
            // An inner block that acts makes the outer one act too (already propagated on the call).
        }
    }

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
            if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Object && p.Value.TryGetProperty("type", out var ty)
                && ty.ValueKind == System.Text.Json.JsonValueKind.String && ty.GetString() == "string") strings.Add(p.Name);
        }
        var req = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var x in r.EnumerateArray())
                if (x.ValueKind == System.Text.Json.JsonValueKind.String && x.GetString() is { } n && strings.Contains(n)) req.Add(n);
        return new BindingSignature(ps, req);
    }

    private static BindingSignature? SignatureFor(string call, IReadOnlyDictionary<string, BindingSignature> sigs) =>
        sigs.TryGetValue(call, out var s) ? s
        : MineClaw.Scripting.ScriptSession.Aliases.TryGetValue(call, out var target) && sigs.TryGetValue(target, out var a) ? a : null;

    /// <summary>Names the script defines itself (<c>function f</c>, <c>local function f</c>, <c>local f =</c>, <c>f = function</c>).</summary>
    private static HashSet<string> DefinedNames(List<Tok> toks)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < toks.Count; i++)
        {
            if (toks[i].Kind == T.Ident && toks[i].Text is "function" or "local" && toks[i + 1].Kind == T.Ident && toks[i + 1].Text != "function")
                set.Add(toks[i + 1].Text);
            if (toks[i].Kind == T.Ident && toks[i].Text == "local" && i + 2 < toks.Count && toks[i + 1].Text == "function" && toks[i + 2].Kind == T.Ident)
                set.Add(toks[i + 2].Text);
        }
        return set;
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
        List<Arg> args;
        var first = toks[j];
        if (first.Kind == T.Str) args = [new Arg(ArgKind.Str, null, j, j)];
        else if (first.Kind == T.Sym && first.Text == "{") args = Elements(toks, j, "}") ?? [];
        else if (first.Kind == T.Sym && first.Text == "(")
        {
            if (Elements(toks, j, ")") is not { } list) return null;
            if (list.Count == 1 && list[0].Kind == ArgKind.Table && list[0].Name == null)
                args = Elements(toks, list[0].Start, "}") ?? [];
            else if (list.Count == 1 && list[0].Kind is ArgKind.Other or ArgKind.Multi) return null; // go(p), place(t): runtime shape
            else args = list;
        }
        else return null;

        var filled = new HashSet<string>(StringComparer.Ordinal);
        var pos = args.Where(a => a.Name == null).ToList();
        // dig{x=1,y=64,z=2}: a position table as the whole argument fills the first (position) param.
        if (sig.Params.Count > 0 && PosParamNames.Contains(sig.Params[0]) && pos.Count == 0
            && new[] { "x", "y", "z" }.All(k => args.Any(a => a.Name == k))) filled.Add(sig.Params[0]);
        foreach (var a in args.Where(a => a.Name != null && a.Kind != ArgKind.Nil))
            filled.Add(a.Name == "n" && sig.Params.Contains("count") ? "count" : a.Name!);
        // A single position table ({1,64,2} / {x=..}) as the whole call fills the first (position) param.
        int pi = 0;
        for (int i = 0; i < pos.Count && pi < sig.Params.Count;)
        {
            var name = sig.Params[pi];
            // Only a trailing call / ... can expand to several values; elsewhere it is one value.
            if (pos[i].Kind == ArgKind.Multi && i == pos.Count - 1) { foreach (var p in sig.Params.Skip(pi)) filled.Add(p); break; }
            if (PosParamNames.Contains(name) && i + 2 < pos.Count && pos[i].Kind == ArgKind.Num && pos[i + 1].Kind == ArgKind.Num && pos[i + 2].Kind == ArgKind.Num)
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
        return sig.Params.Where(p => sig.Required.Contains(p) && !filled.Contains(p)).ToList();
    }

    private static readonly HashSet<string> PosParamNames = new(StringComparer.OrdinalIgnoreCase) { "to", "at", "pos" };

    /// <summary>Top-level elements between the bracket at <paramref name="open"/> and its match (null = unbalanced).</summary>
    private static List<Arg>? Elements(List<Tok> toks, int open, string close)
    {
        var list = new List<Arg>();
        int depth = 0, blocks = 0, start = open + 1;
        for (int k = open + 1; k < toks.Count; k++)
        {
            var t = toks[k];
            if (t.Kind == T.Ident)
            {
                if (t.Text is "function" or "if" or "do" or "repeat") blocks++;
                else if (t.Text is "end" or "until") blocks--;
            }
            else if (t.Kind == T.Sym)
            {
                if (t.Text is "(" or "{" or "[") depth++;
                else if (t.Text is ")" or "}" or "]")
                {
                    if (depth == 0 && blocks <= 0)
                    {
                        if (t.Text != close) return null;
                        if (k > start) list.Add(Classify(toks, start, k - 1));
                        return list;
                    }
                    depth--;
                }
                else if (t.Text is "," or ";" && depth == 0 && blocks <= 0)
                {
                    if (k > start) list.Add(Classify(toks, start, k - 1));
                    start = k + 1;
                }
            }
        }
        return null;
    }

    private static Arg Classify(List<Tok> toks, int s, int e)
    {
        string? name = null;
        // key = value / ["key"] = value (not ==)
        if (e - s >= 2 && toks[s].Kind == T.Ident && toks[s + 1].Kind == T.Sym && toks[s + 1].Text == "=" && !(toks[s + 2].Kind == T.Sym && toks[s + 2].Text == "="))
        {
            name = toks[s].Text;
            s += 2;
        }
        else if (e - s >= 4 && toks[s].Text == "[" && toks[s + 1].Kind == T.Str && toks[s + 2].Text == "]" && toks[s + 3].Text == "=")
        {
            name = toks[s + 1].Text;
            s += 4;
        }
        int len = e - s + 1;
        var kind = len switch
        {
            1 when toks[s].Kind == T.Str => ArgKind.Str,
            1 when toks[s].Kind == T.Num => ArgKind.Num,
            2 when toks[s].Text == "-" && toks[s + 1].Kind == T.Num => ArgKind.Num,
            1 when toks[s].Kind == T.Ident && toks[s].Text == "nil" => ArgKind.Nil,
            _ when toks[s].Kind == T.Sym && toks[s].Text == "{" && MatchClose(toks, s) == e => ArgKind.Table,
            _ when toks[e].Kind == T.Sym && toks[e].Text is ")" || toks[e].Kind == T.Str && len >= 2 && toks[e - 1].Kind == T.Ident
                   || toks[e].Kind == T.Sym && toks[e].Text == "}" && len >= 2 && toks[s].Kind == T.Ident => ArgKind.Multi, // f(x), f"x", f{..}
            2 when toks[s].Text == ".." && toks[s + 1].Text == "." => ArgKind.Multi, // ... (tokenized ".." ".")
            _ => ArgKind.Other,
        };
        return new Arg(kind, name, s, e);
    }

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
        var list = new List<Tok>();
        int line = 1, i = 0, n = code.Length;
        while (i < n)
        {
            char c = code[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < n && code[i + 1] == '-')
            {
                i += 2;
                if (i < n && code[i] == '[' && LongBracket(code, i) is { } lvl)
                {
                    int close = code.IndexOf("]" + new string('=', lvl) + "]", i, StringComparison.Ordinal);
                    int end = close < 0 ? n : close + lvl + 2;
                    line += Count(code, i, end, '\n');
                    i = end;
                }
                else while (i < n && code[i] != '\n') i++;
                continue;
            }
            if (c == '[' && LongBracket(code, i) is { } level)
            {
                int close = code.IndexOf("]" + new string('=', level) + "]", i, StringComparison.Ordinal);
                int end = close < 0 ? n : close + level + 2;
                list.Add(new Tok(T.Str, "S", line));
                line += Count(code, i, end, '\n');
                i = end;
                continue;
            }
            if (c is '"' or '\'')
            {
                int start = line, from = i + 1;
                i++;
                while (i < n && code[i] != c)
                {
                    if (code[i] == '\\' && i + 1 < n) i++;
                    if (code[i] == '\n') line++;
                    i++;
                }
                // The literal's text rides along (tool-needing mine targets); every other consumer reads Str as "S".
                list.Add(new Tok(T.Str, code[from..Math.Min(i, n)], start));
                i++;
                continue;
            }
            if (char.IsDigit(c))
            {
                int s = i;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '.')) i++;
                list.Add(new Tok(T.Num, code[s..i], line));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
                list.Add(new Tok(T.Ident, code[s..i], line));
                continue;
            }
            if (c == '.' && i + 1 < n && code[i + 1] == '.') { list.Add(new Tok(T.Sym, "..", line)); i += 2; continue; }
            list.Add(new Tok(T.Sym, c.ToString(), line));
            i++;
        }
        return list;
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
            if (toks[i].Kind == T.Ident && (toks[i + 1].Kind == T.Str || toks[i + 1].Kind == T.Sym && toks[i + 1].Text is "(" or "{") &&
                !(i > 0 && toks[i - 1].Kind == T.Sym && toks[i - 1].Text is "." or ":") && toks[i].Text != "function")
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
