using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace MineClaw.Scripting.Sdk;

/// <summary>One SDK module: an embedded <c>Sdk/js/NN-name.js</c> file, evaluated before every JS script.</summary>
public sealed record SdkModule(string Name, string Summary, string Source);

/// <summary>
/// The JS standard library, one small module per concern (core, world, flow, machine, policies, jobs, crew). Each module is
/// <c>(H, def) =&gt; { … }</c>: <c>H</c> is the host's raw calls (never visible to scripts), <c>def(name, value)</c> defines a
/// read-only global. The <c>/** … */</c> comment in front of each <c>def</c> is that function's documentation: the SDK reference
/// the author reads (<see cref="SdkReference"/>) is generated from these files, so code and docs can't drift.
/// </summary>
public static class SdkModules
{
    private static readonly Lazy<IReadOnlyList<SdkModule>> Loaded = new(Load);

    public static IReadOnlyList<SdkModule> All => Loaded.Value;

    private static readonly Regex Header = new(@"^//\s*module:\s*(\w+)\s*\|\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static IReadOnlyList<SdkModule> Load()
    {
        var asm = typeof(SdkModules).Assembly;
        var list = new List<SdkModule>();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.Contains(".Sdk.js.", StringComparison.Ordinal) && n.EndsWith(".js", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            list.Add(Parse(res[(res.IndexOf(".Sdk.js.", StringComparison.Ordinal) + 8)..^3], reader.ReadToEnd()));
        }
        return list;
    }

    /// <summary>One module from its file name (no extension, e.g. "20-world") and source (the <c>// module: name | summary</c> header).</summary>
    public static SdkModule Parse(string file, string source)
    {
        var src = source.Replace("\r\n", "\n");
        var m = Header.Match(src);
        return new SdkModule(m.Success ? m.Groups[1].Value : file, m.Success ? m.Groups[2].Value.Trim() : "", src);
    }

    /// <summary>The modules of an <c>Sdk/js</c> folder on disk (same order and parsing as the embedded ones): tools that build docs
    /// from the source tree read the files directly, so an edited module shows up without recompiling.</summary>
    public static IReadOnlyList<SdkModule> FromDirectory(string dir) =>
        Directory.GetFiles(dir, "*.js").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Select(f => Parse(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f))).ToList();

    private static readonly Regex DefRx = new(@"\bdef\('([A-Za-z_$][\w$]*)'", RegexOptions.Compiled);

    /// <summary>Every global the SDK defines (lint's known names; the reference must document each public one).</summary>
    public static IReadOnlySet<string> Globals { get; } = new HashSet<string>(
        Loaded.Value.SelectMany(m => DefRx.Matches(m.Source).Select(x => x.Groups[1].Value)), StringComparer.Ordinal);
}

/// <summary>
/// The SDK reference for the author's cached context, generated from the modules' doc comments (one line per function) plus the
/// game functions this bot exposes (from the host's actions and skills, with their positional params).
/// </summary>
public static class SdkReference
{
    private static readonly Regex DocRx = new(@"/\*\*(.*?)\*/\s*(?:const\s+\w+\s*=|def\(')", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>"module: summary" then "- name(args) - what it does" lines, for every documented SDK function.</summary>
    public static string Build()
    {
        var sb = new StringBuilder();
        foreach (var m in SdkModules.All)
        {
            sb.Append("## ").Append(m.Name).Append(": ").Append(m.Summary).Append('\n');
            foreach (Match d in DocRx.Matches(m.Source))
            {
                var text = Regex.Replace(d.Groups[1].Value, @"^\s*\*\s?", "", RegexOptions.Multiline);
                text = Regex.Replace(text, @"\s+", " ").Trim();
                if (text.Length > 0) sb.Append("- ").Append(text).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>One documented SDK function (or group, e.g. "night() ...; time() ..."): its module, the names it documents and its text.</summary>
    public sealed record Entry(string Module, string ModuleSummary, IReadOnlyList<string> Names, string Text);

    /// <summary>Every doc comment of the modules, in source order (the per-function reference a knowledge pack carries).</summary>
    public static IReadOnlyList<Entry> Entries(IEnumerable<SdkModule>? modules = null)
    {
        var list = new List<Entry>();
        foreach (var m in modules ?? SdkModules.All)
            foreach (Match d in DocRx.Matches(m.Source))
            {
                var text = Regex.Replace(d.Groups[1].Value, @"^\s*\*\s?", "", RegexOptions.Multiline);
                text = Regex.Replace(text, @"\s+", " ").Trim();
                if (text.Length == 0) continue;
                var names = Regex.Matches(d.Groups[1].Value, @"(?<![\w.$])([A-Za-z_$][\w$]*)(?=\(|\.|\s+-)").Select(x => x.Groups[1].Value)
                    .Where(n => n is not ("null" or "true" or "false" or "undefined" or "this")).Distinct(StringComparer.Ordinal).ToList();
                list.Add(new Entry(m.Name, m.Summary, names, text));
            }
        return list;
    }

    /// <summary>The documented SDK names (each doc comment's first identifier).</summary>
    public static IReadOnlySet<string> Documented()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in SdkModules.All)
            foreach (Match d in DocRx.Matches(m.Source))
                foreach (Match id in Regex.Matches(d.Groups[1].Value, @"(?<![\w.$])([A-Za-z_$][\w$]*)(?=\(|\.|\s+-)"))
                    set.Add(id.Groups[1].Value);
        return set;
    }

    /// <summary>"Game functions -> {ok,msg,n}: go(to) mine(block,count,radius) gen(at,count,store,stand) ..." from the host.</summary>
    public static string GameFunctions(IEnumerable<(string Name, IReadOnlyList<string> Params)> callables, int max = 1400)
    {
        var sb = new StringBuilder("Game functions -> {ok,msg,n} (args positional in this order, or one {name:value} object; snake_case names work too):\n");
        foreach (var (name, ps) in callables)
        {
            var piece = $"{name}({string.Join(",", ps.Take(5))}) ";
            if (sb.Length + piece.Length > max) { sb.Append("..."); break; }
            sb.Append(piece);
        }
        return sb.ToString().TrimEnd();
    }
}
