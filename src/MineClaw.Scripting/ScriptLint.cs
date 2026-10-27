using System.Text;
using System.Text.RegularExpressions;

namespace MineClaw.Scripting;

public enum LintSeverity { Warning, Error }

public sealed record LintFinding(LintSeverity Severity, int Line, string Rule, string Message)
{
    public override string ToString() => $"L{Line} {Message}";
}

/// <summary>Lint result: errors reject the run (the brain fixes and resubmits); warnings ride along with the run result.</summary>
public sealed record LintReport(IReadOnlyList<LintFinding> Findings)
{
    public bool HasErrors => Findings.Any(f => f.Severity == LintSeverity.Error);
    public IEnumerable<LintFinding> Errors => Findings.Where(f => f.Severity == LintSeverity.Error);
    public IEnumerable<LintFinding> Warnings => Findings.Where(f => f.Severity == LintSeverity.Warning);

    /// <summary>"lint: L3 ...; L7 ..." (capped), or "" when there is nothing of that severity.</summary>
    public string Format(LintSeverity severity, int max = 220)
    {
        var parts = Findings.Where(f => f.Severity == severity).Select(f => f.ToString()).ToList();
        if (parts.Count == 0) return "";
        var s = (severity == LintSeverity.Error ? "lint errors (fix and resubmit): " : "lint: ") + string.Join("; ", parts);
        return s.Length <= max ? s : s[..(max - 3)] + "...";
    }
}

/// <summary>
/// Zero-token static checks on a script before it runs (C#, no model call). Errors: an unbounded <c>while true</c> /
/// <c>repeat ... until false</c> whose body never acts or waits, and globals the sandbox removed. Warnings: action results
/// ignored inside loops, a long script without <c>cp</c>, <c>say</c> inside a loop. A small Lua tokenizer (comments and
/// strings stripped) tracks blocks, so nesting and same-line code are handled.
/// </summary>
public static class ScriptLint
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
    public static LintReport Check(string code, IEnumerable<string> actions, int longScriptActions = 6)
    {
        var acts = new HashSet<string>(actions, StringComparer.Ordinal);
        var toks = Tokenize(code ?? "");
        var findings = new List<LintFinding>();
        var stack = new List<Block>();
        var parens = new Stack<string?>();   // callee name per open '(' / '{' call
        int actionSites = 0;
        bool hasCp = false;
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
                if (!PureCalls.Contains(t.Text))
                    foreach (var b in stack) b.Acts = true;
                if (isAction) actionSites++;
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
                int start = line;
                i++;
                while (i < n && code[i] != c)
                {
                    if (code[i] == '\\' && i + 1 < n) i++;
                    if (code[i] == '\n') line++;
                    i++;
                }
                i++;
                list.Add(new Tok(T.Str, "S", start));
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
