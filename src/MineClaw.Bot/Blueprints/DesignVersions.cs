using System.Text.RegularExpressions;
using MineClaw.Data.Versioning;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// The game version a design is drawn for (docs/web/api.md "Design Studio", "Versions"): <c>b1.8.1</c> or <c>1.12.2</c>. A Design
/// Studio session and every design it saves record it; the DSL carries it as a <c>version X</c> statement (so every revision, an
/// export and every builder see it). Migration rule: a design whose DSL has no <c>version</c> line (everything saved before
/// 2026-11-03, captures, the brain's own blueprints) is the version of the server whose db holds it (the process's game data
/// when it is read): one bot db has only ever served one version. Such a DSL keeps the legacy rule that its palette holds only
/// <see cref="BuildBlocks"/> blocks; a <c>version</c> line opens the whole design palette of that version (<see cref="DesignPalette"/>).
/// </summary>
public static partial class DesignVersions
{
    public const string Beta = "b1.8.1", Release = "1.12.2";

    public static readonly IReadOnlyList<string> All = [Beta, Release];

    /// <summary>The server's (the world's) version: the process's game data in this flow, outside any design's scope
    /// (<see cref="Use"/> keeps it: inside a 1.12.2 design's scope on a b1.8.1 server this is still b1.8.1).</summary>
    public static string Server => _server.Value ?? GameData.Current.VersionId;

    private static readonly AsyncLocal<string?> _server = new();

    /// <summary>"b1.8.1" / "1.12.2" from a loose spelling ("beta", "b181", "1.12"), or null.</summary>
    public static string? Normalize(string? v) => Spellings.GetValueOrDefault((v ?? "").Trim().ToLowerInvariant());

    /// <summary>Every accepted spelling (lower case, trimmed) of each version.</summary>
    private static readonly Dictionary<string, string> Spellings = new()
    {
        ["b1.8.1"] = Beta, ["b181"] = Beta, ["beta"] = Beta, ["b1.8"] = Beta, ["beta 1.8.1"] = Beta, ["beta1.8.1"] = Beta,
        ["1.12.2"] = Release, ["1.12"] = Release, ["1122"] = Release, ["release"] = Release,
    };

    /// <summary>The game data of a version.</summary>
    public static IGameData Data(string version) => Normalize(version) == Release ? Release1122GameData.Instance : Beta181GameData.Instance;

    /// <summary>Scopes the version's game data to this flow (every static table answers in it) when it isn't already current;
    /// dispose to return. Null/unknown = no change.</summary>
    public static IDisposable Use(string? version)
    {
        var v = Normalize(version);
        if (v == null || v == GameData.Current.VersionId) return Nothing.Instance;
        var outer = _server.Value;
        _server.Value = Server;
        return new Scope(GameData.Override(Data(v)), outer);
    }

    private sealed class Scope(IDisposable data, string? outer) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            data.Dispose();
            _server.Value = outer;
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }

    [GeneratedRegex(@"^\s*version\s+(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex VersionLine();

    /// <summary>The version a DSL names (<c>version 1.12.2</c>), or null when it names none (a legacy design: the server's).</summary>
    public static string? OfDsl(string? dsl) => dsl != null && VersionLine().Match(dsl) is { Success: true } m ? Normalize(m.Groups[1].Value) : null;

    /// <summary>A design's version: its DSL's line, else the server's (the migration rule).</summary>
    public static string Of(BlueprintRecord rec) => OfDsl(rec.Dsl) ?? Server;

    /// <summary>The DSL with its <c>version</c> line set to <paramref name="version"/> (added after the size line, or first).</summary>
    public static string WithVersion(string dsl, string version)
    {
        var line = "version " + version;
        if (VersionLine().IsMatch(dsl)) return VersionLine().Replace(dsl, line, 1);
        var lines = dsl.Replace("\r", "").Split('\n').ToList();
        int at = lines.FindIndex(l => l.TrimStart().StartsWith("size", StringComparison.OrdinalIgnoreCase));
        lines.Insert(at >= 0 ? at + 1 : 0, line);
        return string.Join("\n", lines);
    }
}
