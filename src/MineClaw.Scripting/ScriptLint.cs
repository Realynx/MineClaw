using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Scripting.Lint;

namespace MineClaw.Scripting;

/// <summary>
/// Static checks and the library's code facts (purpose, args, similarity), one entry point for both languages: JS
/// (<see cref="JsLint"/>, every new script) and legacy Lua (<see cref="LuaLint"/>). The language is the caller's, else detected.
/// </summary>
public static class ScriptLint
{
    /// <summary>The warning on a call to a deprecated compat helper (<c>99-compat.js</c>): the author's check rejects a draft with one.</summary>
    public const string CompatRule = JsLint.CompatRule;

    /// <summary>The deprecated compat helpers JS <paramref name="code"/> calls (lint's view: your own function of that name isn't one), each with what replaces it.</summary>
    public static IReadOnlyList<(string Name, string Use)> CompatCalls(string code) =>
        JsLint.Check(code, []).Warnings.Where(w => w.Rule == CompatRule).Select(w => w.Message[..w.Message.IndexOf('(')])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(n => (n, Sdk.SdkModules.Compat[n])).ToList();

    /// <summary>Lua globals the sandbox removed (legacy).</summary>
    public static IReadOnlySet<string> RemovedGlobals => LuaLint.RemovedGlobals;

    /// <param name="actions">Game action / skill / alias names the script can call (results worth checking).</param>
    /// <param name="longScriptActions">More static action call sites than this and no <c>cp</c> → warning.</param>
    /// <param name="signatures">Binding signatures by tool name: a call that can't fill a required arg is an error before anything runs.</param>
    public static LintReport Check(string code, IEnumerable<string> actions, int longScriptActions = 6,
        IReadOnlyDictionary<string, BindingSignature>? signatures = null, ScriptLanguage? language = null) =>
        (language ?? ScriptLanguages.Detect(code)) == ScriptLanguage.Lua
            ? LuaLint.Check(code, actions, longScriptActions, signatures)
            : JsLint.Check(code, actions, longScriptActions, signatures);

    /// <summary>A binding's signature from the input schema of the tool it forwards to (see <see cref="LuaLint.SignatureOf"/>).</summary>
    public static BindingSignature? SignatureOf(System.Text.Json.JsonElement schema) => LuaLint.SignatureOf(schema);

    private static bool IsLua(string code) => ScriptLanguages.Detect(code) == ScriptLanguage.Lua;

    /// <summary>Normalized token stream: comments dropped, strings → S, numbers → N (so a script differing only in literals matches).</summary>
    public static IReadOnlyList<string> Shape(string code) =>
        IsLua(code) ? LuaLint.Shape(code) : JsLint.Tokens(code).Select(t => t.Kind switch { 's' => "S", 'n' => "N", _ => t.Text }).ToList();

    /// <summary>Exact-duplicate key: comments and whitespace dropped, literals kept.</summary>
    public static string ExactKey(string code)
    {
        if (IsLua(code)) return LuaLint.ExactKey(code);
        var sb = new StringBuilder();
        foreach (var (k, t) in JsLint.Tokens(code)) sb.Append(k == 's' ? "\"" + t + "\"" : t).Append(' ');
        return sb.ToString();
    }

    /// <summary>Called names (actions, helpers): the script's "purpose tags" next to its first comment line.</summary>
    public static IReadOnlySet<string> CalledNames(string code)
    {
        if (IsLua(code)) return LuaLint.CalledNames(code);
        var toks = JsLint.Tokens(code).ToList();
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < toks.Count; i++)
            if (IsJsCallee(toks, i)) set.Add(toks[i].Text);
        return set;
    }

    /// <summary>Keywords followed by '(' that aren't calls.</summary>
    private static readonly HashSet<string> NotCallees = new(StringComparer.Ordinal) { "function", "if", "while", "for", "switch", "catch" };

    /// <summary>A name followed by '(' that isn't a member (<c>a.f(</c>) or a keyword.</summary>
    private static bool IsJsCallee(List<(char Kind, string Text)> toks, int i) =>
        toks[i].Kind == 'i' && toks[i + 1].Text == "(" && !(i > 0 && toks[i - 1].Text == ".") && !NotCallees.Contains(toks[i].Text);

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

    /// <summary>The first comment line ("// purpose: mine the gen" / "-- purpose: ..." → "mine the gen"), or null.</summary>
    public static string? Purpose(string code)
    {
        foreach (var raw in (code ?? "").Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0) continue;
            if (CommentBody(l) is not { } body) return null;
            var p = Regex.Replace(body.Trim(), @"^(?:purpose|does|job)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
            if (p.StartsWith("migrated from routine", StringComparison.Ordinal)) return null;
            return p.Length == 0 ? null : (p.Length <= 60 ? p : p[..57] + "...");
        }
        return null;
    }

    /// <summary>The text of a comment line (<c>--</c>, <c>//</c> or <c>/* */</c>), or null when the line isn't a comment.</summary>
    private static string? CommentBody(string l) =>
        l.StartsWith("--", StringComparison.Ordinal) ? l.TrimStart('-')
        : l.StartsWith("//", StringComparison.Ordinal) ? l.TrimStart('/')
        : l.StartsWith("/*", StringComparison.Ordinal) ? l.TrimStart('/', '*').Replace("*/", "") : null;

    /// <summary>Arg names the script reads (<c>args.n</c>, <c>args["cell"]</c>) and the tunables it declares (<c>params({n: 16})</c>), in order.</summary>
    public static IReadOnlyList<string> ArgNames(string code)
    {
        if (IsLua(code)) return LuaLint.ArgNames(code);
        var names = new List<string>();
        foreach (var (n, _) in JsLint.DeclaredParams(code)) if (!names.Contains(n)) names.Add(n);
        var stripped = Regex.Replace(code ?? "", @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(stripped, @"\bargs\s*(?:\.\s*([A-Za-z_$][\w$]*)|\[\s*[""']([A-Za-z_$][\w$]*)[""']\s*\])"))
        {
            var n = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!names.Contains(n)) names.Add(n);
        }
        return names;
    }

    /// <summary>The declared tunables with their defaults ("n=64", "at='auto'"), for the catalog.</summary>
    public static IReadOnlyList<(string Name, string? Default)> Params(string code) => IsLua(code) ? [] : JsLint.DeclaredParams(code);
}
