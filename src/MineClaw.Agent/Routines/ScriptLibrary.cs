using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Memory;

namespace MineClaw.Agent.Routines;

/// <summary>One saved version of a script with its own run stats (stats never carry over between versions).</summary>
public sealed class ScriptVersion
{
    public int V { get; set; }
    public string Code { get; set; } = "";
    /// <summary>Who wrote it: "main/claude-opus-5-5" (null = migrated or legacy).</summary>
    public string? Author { get; set; }
    public DateTimeOffset? SavedAt { get; set; }
    public int Runs { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    public double TotalSeconds { get; set; }
    /// <summary>Items gained over all runs (the yield).</summary>
    public long TotalYield { get; set; }
    /// <summary>Failure reason (digits → #) → count.</summary>
    public Dictionary<string, int> FailReasons { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset? LastRun { get; set; }

    public double Rate => Runs == 0 ? 0 : (double)Successes / Runs;
    public double AvgYield => Runs == 0 ? 0 : (double)TotalYield / Runs;

    /// <summary>"62% of 8" style reliability ("new" before any run).</summary>
    public string Reliability => Runs == 0 ? "new" : $"{Rate:P0}/{Runs}";

    public IEnumerable<string> TopFailures(int n = 2) => FailReasons.OrderByDescending(kv => kv.Value).Take(n).Select(kv => $"{kv.Key} x{kv.Value}");
}

/// <summary>
/// The script part of a saved routine (stored in the routine's JSON row, so it is versioned, hive-shared, listed by
/// list_memory and removed by forget like any routine): the active code, every version with its own stats, lifetime stats.
/// </summary>
public sealed class RoutineScript
{
    /// <summary>Active code (the version <see cref="Version"/>).</summary>
    public string Code { get; set; } = "";
    /// <summary>Active version number.</summary>
    public int Version { get; set; } = 1;
    /// <summary>Older versions' code, newest first (legacy view; full history with stats is <see cref="Versions"/>).</summary>
    public List<string> Previous { get; set; } = new();
    /// <summary>Lifetime totals over all versions (per-version stats: <see cref="Versions"/>).</summary>
    public int Runs { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    public double TotalSeconds { get; set; }
    public long TotalActions { get; set; }
    public string? LastOutcome { get; set; }
    public DateTimeOffset? LastRun { get; set; }
    /// <summary>Catalog skill name once promoted (reliable current version).</summary>
    public string? PromotedAs { get; set; }
    /// <summary>Converted from save_routine tool steps.</summary>
    public bool Migrated { get; set; }

    /// <summary>Every version (oldest first), each with its own stats and author. Never pruned without trace (≤ 12 kept).</summary>
    public List<ScriptVersion> Versions { get; set; } = new();
    /// <summary>Author of the active version.</summary>
    public string? Author { get; set; }
    /// <summary>Purpose line (first comment of the code) and argument signature, for the catalog and near-duplicate checks.</summary>
    public string? Purpose { get; set; }
    public List<string> ArgNames { get; set; } = new();
    /// <summary>Why a near-duplicate was saved anyway (op=save force=reason).</summary>
    public string? ForcedReason { get; set; }
    /// <summary>Housekeeping: same code as this script (stats merged into it); kept, never deleted.</summary>
    public string? DuplicateOf { get; set; }
    /// <summary>Housekeeping: unused for StaleDays → flagged for archive (still runnable, hidden from the catalog).</summary>
    public DateTimeOffset? ArchiveFlagged { get; set; }
    /// <summary>Last auto-revert note ("v4 52% < v3 95% over 3 runs: back to v3").</summary>
    public string? RevertNote { get; set; }

    public double AvgSeconds => Runs == 0 ? 0 : TotalSeconds / Runs;
}

/// <summary>Result of a save: the routine, whether a new version was made, and the near-duplicate it collided with.</summary>
public sealed record ScriptSave(Routine? Routine, bool NewVersion, string? SimilarTo = null, double Similarity = 0)
{
    public bool Refused => Routine == null;
}

/// <summary>
/// Saved scripts on top of <see cref="MemoryStore"/> routines: versions (same name, history kept, rollback, per-version stats,
/// auto-revert to a more reliable version), <c>name@v3</c> references, near-duplicate refusal on save, the catalog, and a
/// cheap housekeeping pass. Old step routines migrate on first use: their steps become an equivalent script (same repeat /
/// stop_when semantics), so run_routine and promoted routine skills run on the script engine; steps the script API can't
/// express keep the legacy step replay.
/// </summary>
public sealed class ScriptLibrary(MemoryStore memory)
{
    public const int KeepVersions = 3;
    public const int KeepHistory = 20;

    public MemoryStore Memory { get; } = memory;

    /// <summary>Clock for SavedAt/LastRun/housekeeping (tests set it).</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>A version counts as proven (promotion; older versions stop being callable) at ≥ MinRuns runs and ≥ MinRate.</summary>
    public int MinRuns { get; set; } = 3;
    public double MinRate { get; set; } = 0.9;
    /// <summary>Auto-revert compares versions once both have at least this many runs.</summary>
    public int AutoRevertMinRuns { get; set; } = 3;

    public Routine? Get(string name) => Memory.GetRoutine(ParseRef(name).Name);

    private static readonly Regex RefRx = new(@"^(.+?)@v?(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"cobble_loop@v3" → ("cobble_loop", 3); "cobble_loop" → ("cobble_loop", null).</summary>
    public static (string Name, int? Version) ParseRef(string reference)
    {
        var t = (reference ?? "").Trim();
        var m = RefRx.Match(t);
        return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value)) : (t, null);
    }

    /// <summary>Legacy rows (no <see cref="RoutineScript.Versions"/>): synthesize the history; the active version keeps the old stats.</summary>
    public static ScriptVersion Active(RoutineScript s)
    {
        if (s.Versions.FirstOrDefault(v => v.V == s.Version) is { } a) return a;
        for (int i = s.Previous.Count - 1; i >= 0; i--)
        {
            int v = s.Version - 1 - i;
            if (v >= 1 && s.Versions.All(x => x.V != v)) s.Versions.Add(new ScriptVersion { V = v, Code = s.Previous[i] });
        }
        a = new ScriptVersion
        {
            V = s.Version, Code = s.Code, Author = s.Author, Runs = s.Runs, Successes = s.Successes, Failures = s.Failures,
            TotalSeconds = s.TotalSeconds, LastRun = s.LastRun,
        };
        s.Versions.Add(a);
        s.Versions.Sort((x, y) => x.V.CompareTo(y.V));
        return a;
    }

    public bool Proven(ScriptVersion v) => v.Runs >= Math.Max(1, MinRuns) && v.Rate >= MinRate;

    /// <summary>
    /// The code to run for <paramref name="reference"/> ("x" or "x@v3"), migrating a step routine when
    /// <paramref name="scriptable"/> allows; null when unknown. An older version is callable until the active one is proven.
    /// </summary>
    public string? CodeOf(string reference, Func<string, bool>? scriptable = null) => Resolve(reference, scriptable, out _)?.Code;

    /// <summary>Like <see cref="CodeOf"/>, with the version that will run and why a reference was refused.</summary>
    public ScriptVersion? Resolve(string reference, Func<string, bool>? scriptable, out string? error)
    {
        error = null;
        var (name, ver) = ParseRef(reference);
        var r = Memory.GetRoutine(name);
        if (r == null) { error = $"no saved script '{name}'"; return null; }
        if (r.Script is not { Code.Length: > 0 })
        {
            if (scriptable == null || Migrate(r, scriptable) is not { Script: { } ms }) { error = $"'{name}' is a step routine (run_routine)"; return null; }
            return Active(ms);
        }
        var s = r.Script;
        var active = Active(s);
        if (ver == null || ver == s.Version) return active;
        var old = s.Versions.FirstOrDefault(v => v.V == ver);
        if (old == null) { error = $"{name} has no v{ver} (versions: {string.Join(",", s.Versions.Select(v => "v" + v.V))})"; return null; }
        if (Proven(active)) { error = $"{name}@v{ver} retired: v{s.Version} is proven ({active.Reliability}); rollback to use it again"; return null; }
        return old;
    }

    /// <summary>
    /// Saves code under <paramref name="name"/>: a new version of an existing script (history kept), the same code = no new
    /// version, code equal to an older version = that version becomes active again (its stats continue).
    /// </summary>
    public Routine Save(string name, string code, string? note = null, string? author = null, string? purpose = null,
        IReadOnlyList<string>? argNames = null, string? forcedReason = null) => SaveVersion(name, code, note, author, purpose, argNames, forcedReason).Routine!;

    public ScriptSave SaveVersion(string name, string code, string? note = null, string? author = null, string? purpose = null,
        IReadOnlyList<string>? argNames = null, string? forcedReason = null)
    {
        name = ParseRef(name).Name;
        var r = Memory.GetRoutine(name) ?? new Routine { Name = name, Repeat = 1 };
        bool fresh = false;
        if (r.Script == null)
        {
            r.Script = new RoutineScript { Code = code, Version = 1, Author = author };
            r.Script.Versions.Add(new ScriptVersion { V = 1, Code = code, Author = author, SavedAt = Now() });
            fresh = true;
        }
        else
        {
            var s = r.Script;
            Active(s);
            if (!string.Equals(s.Code, code, StringComparison.Ordinal))
            {
                if (s.Versions.FirstOrDefault(v => v.Code == code) is { } same) Activate(s, same.V);
                else
                {
                    int nv = s.Versions.Count == 0 ? 1 : s.Versions.Max(v => v.V) + 1;
                    s.Versions.Add(new ScriptVersion { V = nv, Code = code, Author = author, SavedAt = Now() });
                    if (s.Versions.Count > KeepHistory) s.Versions.RemoveAt(0); // oldest, never the active one (it's last)
                    Activate(s, nv);
                }
                s.Migrated = false;
                s.RevertNote = null;
                fresh = true;
                // A new version earns its own reliability; an already-promoted skill keeps running the active code.
            }
        }
        var sc = r.Script;
        if (purpose != null) sc.Purpose = purpose;
        if (argNames != null) sc.ArgNames = argNames.ToList();
        if (forcedReason != null) sc.ForcedReason = forcedReason;
        sc.ArchiveFlagged = null;
        sc.DuplicateOf = null;
        if (!string.IsNullOrWhiteSpace(note)) r.Note = note.Trim();
        return new ScriptSave(Memory.SaveRoutine(r), fresh);
    }

    private static void Activate(RoutineScript s, int v)
    {
        var ver = s.Versions.First(x => x.V == v);
        s.Code = ver.Code;
        s.Version = v;
        s.Author = ver.Author;
        s.Previous = s.Versions.Where(x => x.V != v).OrderByDescending(x => x.V).Take(KeepVersions).Select(x => x.Code).ToList();
    }

    /// <summary>Makes an older version active again (default: the newest one below the active version).</summary>
    public (Routine? Routine, string Message) Rollback(string reference, int? version = null)
    {
        var (name, refVer) = ParseRef(reference);
        version ??= refVer;
        var r = Memory.GetRoutine(name);
        if (r?.Script is not { } s) return (null, $"no saved script '{name}'");
        Active(s);
        var target = version is { } v ? s.Versions.FirstOrDefault(x => x.V == v)
            : s.Versions.Where(x => x.V < s.Version).OrderByDescending(x => x.V).FirstOrDefault();
        if (target == null) return (null, $"{name}: no version to roll back to (versions: {string.Join(",", s.Versions.Select(x => "v" + x.V))})");
        if (target.V == s.Version) return (r, $"{name} v{target.V} is already active");
        int from = s.Version;
        Activate(s, target.V);
        s.RevertNote = $"rolled back {name} v{from} -> v{target.V}";
        Memory.SaveRoutine(r);
        return (r, $"ok: {name} v{target.V} active again (was v{from}; v{from} kept)");
    }

    /// <summary>Records one run of a saved script; returns its updated stats (null when it isn't saved).</summary>
    public RoutineScript? RecordRun(string name, bool ok, TimeSpan duration, int actions, string outcome) =>
        RecordRun(name, null, ok, duration, actions, outcome, 0, null, out _);

    /// <summary>
    /// Records one run against the version that ran (default: the active one). When the active version has fallen below the
    /// previous version's success rate (both with ≥ <see cref="AutoRevertMinRuns"/> runs), the better one becomes active
    /// again and <paramref name="revertNote"/> says so.
    /// </summary>
    public RoutineScript? RecordRun(string name, int? version, bool ok, TimeSpan duration, int actions, string outcome, int yield,
        string? failReason, out string? revertNote)
    {
        revertNote = null;
        var r = Get(name);
        if (r?.Script is not { } s) return null;
        var active = Active(s);
        var ver = version is { } vn ? s.Versions.FirstOrDefault(v => v.V == vn) ?? active : active;
        var now = Now();
        s.Runs++;
        if (ok) s.Successes++; else s.Failures++;
        s.TotalSeconds += Math.Max(0, duration.TotalSeconds);
        s.TotalActions += actions;
        s.LastOutcome = outcome.Length <= 200 ? outcome : outcome[..197] + "...";
        s.LastRun = now;
        s.ArchiveFlagged = null;
        ver.Runs++;
        if (ok) ver.Successes++; else ver.Failures++;
        ver.TotalSeconds += Math.Max(0, duration.TotalSeconds);
        ver.TotalYield += Math.Max(0, yield);
        ver.LastRun = now;
        if (!ok)
        {
            var key = FailKey(failReason ?? outcome);
            ver.FailReasons[key] = ver.FailReasons.GetValueOrDefault(key) + 1;
            if (ver.FailReasons.Count > 8) ver.FailReasons.Remove(ver.FailReasons.MinBy(kv => kv.Value).Key);
        }

        if (ver.V == s.Version && ver.Runs >= Math.Max(1, AutoRevertMinRuns))
        {
            var prev = s.Versions.Where(v => v.V < ver.V && v.Runs >= Math.Max(1, AutoRevertMinRuns)).OrderByDescending(v => v.V).FirstOrDefault();
            if (prev != null && ver.Rate < prev.Rate)
            {
                Activate(s, prev.V);
                revertNote = s.RevertNote = $"auto-reverted {r.Name} v{ver.V} ({ver.Reliability}) -> v{prev.V} ({prev.Reliability}); v{ver.V} kept";
            }
        }
        Memory.SaveRoutine(r);
        return s;
    }

    /// <summary>"goto: no route to 3,64,2" → "goto: no route to #,#,#" (≤ 50 chars): failures group by kind, not by numbers.</summary>
    public static string FailKey(string reason)
    {
        var t = Regex.Replace((reason ?? "").Replace('\n', ' ').Trim(), @"-?\d+", "#");
        t = Regex.Replace(t, @"^(?:failed|cancelled): script \S+ (?:at line # )?", "");
        return t.Length <= 50 ? t : t[..50];
    }

    public void MarkPromoted(string name, string skill)
    {
        var r = Get(name);
        if (r?.Script is not { } s || s.PromotedAs == skill) return;
        s.PromotedAs = skill;
        Memory.SaveRoutine(r);
    }

    /// <summary>All saved scripts (routines that carry code).</summary>
    public IReadOnlyList<Routine> Scripts() => Memory.Read(d => d.Routines.Where(r => r.Script is { Code.Length: > 0 }).ToList());

    /// <summary>
    /// The most similar OTHER saved script at or above <paramref name="threshold"/> (archived and merged duplicates skipped),
    /// scored by <paramref name="score"/> (new code, other script) → 0..1. Null = no near-duplicate.
    /// </summary>
    public (string Name, double Score)? Similar(string name, string code, double threshold, Func<string, RoutineScript, double> score)
    {
        var self = ParseRef(name).Name;
        (string, double)? best = null;
        foreach (var r in Scripts())
        {
            if (string.Equals(r.Name, self, StringComparison.OrdinalIgnoreCase) || r.Script is not { } s) continue;
            if (s.DuplicateOf != null || s.ArchiveFlagged != null || s.Migrated) continue;
            double v;
            try { v = score(code, s); } catch (Exception) { continue; }
            if (v >= threshold && (best == null || v > best.Value.Item2)) best = (r.Name, v);
        }
        return best;
    }

    /// <summary>
    /// The catalog the brain picks from: "cobble_loop v4 (n,cell) 95%/20 +60/run: mine the gen" per script, most used first;
    /// archived and merged duplicates left out.
    /// </summary>
    public string Catalog(int max = 12)
    {
        var rows = Scripts().Where(r => r.Script is { DuplicateOf: null, ArchiveFlagged: null, Migrated: false })
            .OrderByDescending(r => r.Script!.Runs).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        if (rows.Count == 0) return "no saved scripts";
        var sb = new StringBuilder();
        foreach (var r in rows.Take(max))
        {
            var s = r.Script!;
            var a = Active(s);
            sb.Append(r.Name).Append(" v").Append(s.Version);
            sb.Append(" (").Append(string.Join(",", s.ArgNames)).Append(')');
            sb.Append(' ').Append(a.Reliability);
            if (a.Runs > 0 && a.AvgYield >= 1) sb.Append($" +{a.AvgYield:0}/run");
            if (a.TopFailures(1).FirstOrDefault() is { } f) sb.Append(" fails: ").Append(f);
            var purpose = s.Purpose ?? r.Note;
            if (!string.IsNullOrWhiteSpace(purpose)) sb.Append(": ").Append(purpose);
            sb.Append('\n');
        }
        if (rows.Count > max) sb.Append($"(+{rows.Count - max} more)");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Cheap periodic pass (C#, zero tokens): scripts with exactly the same code (<paramref name="exactKey"/>) merge their run
    /// stats into the most used one and are marked <see cref="RoutineScript.DuplicateOf"/>; scripts unused for
    /// <paramref name="staleDays"/> are flagged for archive. Nothing is deleted. Returns a one-line summary ("" = nothing).
    /// </summary>
    public string Housekeep(int staleDays, Func<string, string> exactKey)
    {
        var now = Now();
        var notes = new List<string>();
        var live = Scripts().Where(r => r.Script is { DuplicateOf: null }).ToList();
        foreach (var g in live.GroupBy(r => SafeKey(exactKey, r.Script!.Code)).Where(g => g.Count() > 1))
        {
            var keep = g.OrderByDescending(r => r.Script!.Runs).ThenBy(r => r.Name, StringComparer.Ordinal).First();
            var ka = Active(keep.Script!);
            foreach (var dup in g.Where(r => r != keep))
            {
                var ds = dup.Script!;
                var da = Active(ds);
                ka.Runs += da.Runs; ka.Successes += da.Successes; ka.Failures += da.Failures;
                ka.TotalSeconds += da.TotalSeconds; ka.TotalYield += da.TotalYield;
                foreach (var (k, v) in da.FailReasons) ka.FailReasons[k] = ka.FailReasons.GetValueOrDefault(k) + v;
                keep.Script!.Runs += ds.Runs; keep.Script.Successes += ds.Successes; keep.Script.Failures += ds.Failures;
                ds.DuplicateOf = keep.Name;
                Memory.SaveRoutine(dup);
                notes.Add($"{dup.Name}={keep.Name}");
            }
            Memory.SaveRoutine(keep);
        }
        var stale = new List<string>();
        foreach (var r in Scripts())
        {
            var s = r.Script!;
            if (s.ArchiveFlagged != null || s.DuplicateOf != null) continue;
            Active(s);
            var last = s.LastRun ?? s.Versions.Max(v => v.SavedAt);
            if (last is { } l && now - l > TimeSpan.FromDays(Math.Max(1, staleDays)))
            {
                s.ArchiveFlagged = now;
                Memory.SaveRoutine(r);
                stale.Add(r.Name);
            }
        }
        var parts = new List<string>();
        if (notes.Count > 0) parts.Add("merged duplicates " + string.Join(", ", notes));
        if (stale.Count > 0) parts.Add($"flagged for archive (unused {staleDays}d): " + string.Join(", ", stale));
        return string.Join("; ", parts);

        static string SafeKey(Func<string, string> f, string code) { try { return f(code); } catch (Exception) { return code; } }
    }

    /// <summary>Converts a step routine to a script and saves it; null when a step's tool has no script function.</summary>
    public Routine? Migrate(Routine r, Func<string, bool> scriptable)
    {
        if (r.Script is { Code.Length: > 0 }) return r;
        var code = FromSteps(r, scriptable);
        if (code == null) return null;
        r.Script = new RoutineScript { Code = code, Migrated = true };
        r.Script.Versions.Add(new ScriptVersion { V = 1, Code = code, SavedAt = Now() });
        return Memory.SaveRoutine(r);
    }

    /// <summary>
    /// The Lua equivalent of a step routine: each step is a call with its saved args table; a failed step fails the run
    /// (reporting that step), stop_when ends it successfully; repeat 0 = until stopped (capped at 500 rounds).
    /// <c>args["repeat"]</c> overrides the saved repeat (run_routine's repeat).
    /// </summary>
    public static string? FromSteps(Routine r, Func<string, bool> scriptable)
    {
        if (r.Steps.Count == 0 || r.Steps.Any(s => !scriptable(s.Tool) || !IsIdent(s.Tool))) return null;
        var sb = new StringBuilder();
        sb.Append("-- migrated from routine ").Append(OneLine(r.Name)).Append('\n');
        sb.Append("local N, i, ok, m = args[\"repeat\"] or ").Append(r.Repeat).Append(", 0\n");
        sb.Append("while (N <= 0 and i < 500) or (N > 0 and i < N) do\n  i = i + 1\n");
        foreach (var s in r.Steps)
        {
            var fn = s.Tool == "goto" ? "go" : s.Tool; // goto is a Lua keyword
            var a = s.Args.ValueKind == JsonValueKind.Object ? LuaLiteral(s.Args) : "{}";
            sb.Append("  ok, m = ").Append(fn).Append(a).Append(" if not ok then fail() end\n");
            if (!string.IsNullOrEmpty(r.StopWhen))
                sb.Append("  if string.find(string.lower(m), ").Append(LuaString(r.StopWhen.ToLowerInvariant())).Append(", 1, true) then break end\n");
        }
        sb.Append("end\n");
        sb.Append("return i .. \" round(s), last: \" .. (m or \"\")\n");
        return sb.ToString();
    }

    /// <summary>JSON → Lua table constructor (strings escaped; keys as ["key"]).</summary>
    public static string LuaLiteral(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().Select(p => $"[{LuaString(p.Name)}]={LuaLiteral(p.Value)}")) + "}",
        JsonValueKind.Array => "{" + string.Join(",", e.EnumerateArray().Select(LuaLiteral)) + "}",
        JsonValueKind.String => LuaString(e.GetString() ?? ""),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "nil",
    };

    public static string LuaString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
            sb.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", _ when c < ' ' => " ", _ => c.ToString() });
        return sb.Append('"').ToString();
    }

    private static bool IsIdent(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string OneLine(string s) => s.Replace('\n', ' ').Replace('\r', ' ');
}
