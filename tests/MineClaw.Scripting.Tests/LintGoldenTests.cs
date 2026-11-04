using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Tools;
using MineClaw.Scripting;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// Pins both linters (JS and legacy Lua) and the library's code facts over <see cref="LintGoldenCorpus"/>: every finding's
/// severity, line, rule and message (model-facing text) in both languages, with and without signatures, plus shape, exact key,
/// called names, purpose, arg names, params, compat calls and similarity. Each snippet's line prefixes (half-written scripts:
/// open blocks, unbalanced brackets) are linted too and pinned by a hash. A refactor must leave <see cref="LintGoldenData"/>
/// unchanged. Regenerate (only for an intended change) by setting WP09_GOLDEN_OUT to a file path and copying it in.
/// </summary>
public class LintGoldenTests
{
    private static readonly string[] Acts =
        ["mine", "gen", "harvest_generator", "deposit", "withdraw", "take", "craft", "go", "goto", "dig", "place", "chop", "chop_tree", "attack", "eat", "say", "store"];

    private static readonly Dictionary<string, BindingSignature> Sigs = new()
    {
        ["harvest_generator"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("at", "output block").Int("count", "up to 20000")
            .Str("store", required: false).Str("stand", required: false).Build())!,
        ["place"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("block").Str("at", "x,y,z").Build())!,
        ["dig"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("at", "x,y,z").Build())!,
        ["goto"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("to").Num("dist", required: false).Build())!,
        ["mine"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("block").Int("count", required: false).Int("radius", required: false).Build())!,
        ["withdraw"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("item").Int("count", required: false).Str("at", required: false).Build())!,
        ["deposit"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("at", required: false).StrArray("items", required: false).Build())!,
        ["chop_tree"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("at", required: false).Build())!,
        ["craft"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("item").Int("count", required: false).Build())!,
    };

    private static string Findings(LintReport r) =>
        r.Findings.Count == 0 ? "-" : string.Join(" | ", r.Findings.Select(f => $"{(f.Severity == LintSeverity.Error ? "E" : "W")}{f.Line} {f.Rule}: {f.Message}"));

    private static void Lint(StringBuilder sb, string code)
    {
        sb.Append("  js   ").Append(Try(() => Findings(ScriptLint.Check(code, Acts, 6, Sigs, ScriptLanguage.Js)))).Append('\n');
        sb.Append("  js0  ").Append(Try(() => Findings(ScriptLint.Check(code, Acts.Take(5), 2, null, ScriptLanguage.Js)))).Append('\n');
        sb.Append("  lua  ").Append(Try(() => Findings(ScriptLint.Check(code, Acts, 6, Sigs, ScriptLanguage.Lua)))).Append('\n');
        sb.Append("  lua0 ").Append(Try(() => Findings(ScriptLint.Check(code, Acts.Take(5), 2, null, ScriptLanguage.Lua)))).Append('\n');
    }

    /// <summary>A fact, or the exception type it throws (an unterminated JS quote throws in <c>Tokens</c> today).</summary>
    private static string Try(Func<string> f)
    {
        try { return f(); }
        catch (Exception e) { return "!" + e.GetType().Name; }
    }

    private static string Facts(string code, string prev)
    {
        var sb = new StringBuilder();
        sb.Append(Try(() => string.Join(" ", ScriptLint.Shape(code)))).Append('\n');
        sb.Append(Try(() => ScriptLint.ExactKey(code))).Append('\n');
        sb.Append(Try(() => string.Join(",", ScriptLint.CalledNames(code).Order(StringComparer.Ordinal)))).Append('\n');
        sb.Append(Try(() => ScriptLint.Purpose(code) ?? "-")).Append('\n');
        sb.Append(Try(() => string.Join(",", ScriptLint.ArgNames(code)))).Append('\n');
        sb.Append(Try(() => string.Join(",", ScriptLint.Params(code).Select(p => p.Name + "=" + (p.Default ?? "?"))))).Append('\n');
        if (ScriptLanguages.Detect(code) == ScriptLanguage.Js)
            sb.Append(Try(() => string.Join(",", ScriptLint.CompatCalls(code).Select(c => c.Name + ":" + c.Use)))).Append('\n');
        sb.Append(Try(() => ScriptLint.Similarity(code, prev).ToString("R", CultureInfo.InvariantCulture))).Append('\n');
        return sb.ToString();
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    internal static string Dump()
    {
        var sb = new StringBuilder();
        var facts = new StringBuilder();
        var prefixes = new StringBuilder();
        string prev = "";
        for (int i = 0; i < LintGoldenCorpus.Snippets.Length; i++)
        {
            var code = LintGoldenCorpus.Snippets[i].ReplaceLineEndings("\n");
            sb.Append("== ").Append(i).Append(' ').Append(ScriptLanguages.Detect(code)).Append('\n');
            Lint(sb, code);
            facts.Append(Facts(code, prev));
            var lines = code.Split('\n');
            for (int n = 1; n < lines.Length; n++)
            {
                var part = string.Join("\n", lines.Take(n));
                prefixes.Append(i).Append(':').Append(n).Append('\n');
                Lint(prefixes, part);
            }
            prev = code;
        }
        sb.Append("facts ").Append(Hash(facts.ToString())).Append('\n');
        sb.Append("prefixes ").Append(Hash(prefixes.ToString())).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void Lint_and_code_facts_match_the_golden_dump()
    {
        var actual = Dump().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP09_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(LintGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
