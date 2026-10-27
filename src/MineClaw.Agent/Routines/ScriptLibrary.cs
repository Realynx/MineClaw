using System.Text;
using System.Text.Json;
using MineClaw.Agent.Memory;

namespace MineClaw.Agent.Routines;

/// <summary>
/// The script part of a saved routine (stored in the routine's JSON row, so it is versioned, hive-shared, listed by
/// list_memory and removed by forget like any routine): JS code, previous versions and run stats.
/// </summary>
public sealed class RoutineScript
{
    public string Code { get; set; } = "";
    public int Version { get; set; } = 1;
    /// <summary>Older versions, newest first (at most <see cref="ScriptLibrary.KeepVersions"/>).</summary>
    public List<string> Previous { get; set; } = new();
    public int Runs { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    public double TotalSeconds { get; set; }
    public long TotalActions { get; set; }
    public string? LastOutcome { get; set; }
    public DateTimeOffset? LastRun { get; set; }
    /// <summary>Catalog skill name once promoted (≥ N successful runs).</summary>
    public string? PromotedAs { get; set; }
    /// <summary>Converted from save_routine tool steps.</summary>
    public bool Migrated { get; set; }

    public double AvgSeconds => Runs == 0 ? 0 : TotalSeconds / Runs;
}

/// <summary>
/// Saved scripts on top of <see cref="MemoryStore"/> routines. Old step routines migrate on first use: their steps become
/// an equivalent script (same repeat / stop_when semantics), so run_routine and promoted routine skills run on the script
/// engine; steps the script API can't express keep the legacy step replay.
/// </summary>
public sealed class ScriptLibrary(MemoryStore memory)
{
    public const int KeepVersions = 3;

    public MemoryStore Memory { get; } = memory;

    public Routine? Get(string name) => Memory.GetRoutine(name);

    /// <summary>Script code of a saved routine (migrating a step routine when <paramref name="scriptable"/> allows), or null.</summary>
    public string? CodeOf(string name, Func<string, bool>? scriptable = null)
    {
        var r = Get(name);
        if (r == null) return null;
        if (r.Script is { Code.Length: > 0 } s) return s.Code;
        return scriptable != null ? Migrate(r, scriptable)?.Script?.Code : null;
    }

    /// <summary>Saves (or re-versions) a script. Same code = no new version.</summary>
    public Routine Save(string name, string code, string? note = null)
    {
        var r = Get(name) ?? new Routine { Name = name, Repeat = 1 };
        var s = r.Script ??= new RoutineScript { Code = code };
        if (!string.Equals(s.Code, code, StringComparison.Ordinal))
        {
            if (s.Code.Length > 0) s.Previous.Insert(0, s.Code);
            if (s.Previous.Count > KeepVersions) s.Previous.RemoveRange(KeepVersions, s.Previous.Count - KeepVersions);
            s.Code = code;
            s.Version++;
            s.Migrated = false;
            // A new version earns its own promotion record; an already-promoted skill keeps running the latest code.
        }
        if (!string.IsNullOrWhiteSpace(note)) r.Note = note.Trim();
        return Memory.SaveRoutine(r);
    }

    /// <summary>Records one run of a saved script; returns its updated stats (null when it isn't saved).</summary>
    public RoutineScript? RecordRun(string name, bool ok, TimeSpan duration, int actions, string outcome)
    {
        var r = Get(name);
        if (r?.Script is not { } s) return null;
        s.Runs++;
        if (ok) s.Successes++; else s.Failures++;
        s.TotalSeconds += Math.Max(0, duration.TotalSeconds);
        s.TotalActions += actions;
        s.LastOutcome = outcome.Length <= 200 ? outcome : outcome[..197] + "...";
        s.LastRun = DateTimeOffset.UtcNow;
        Memory.SaveRoutine(r);
        return s;
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

    /// <summary>Converts a step routine to a script and saves it; null when a step's tool has no script function.</summary>
    public Routine? Migrate(Routine r, Func<string, bool> scriptable)
    {
        if (r.Script is { Code.Length: > 0 }) return r;
        var code = FromSteps(r, scriptable);
        if (code == null) return null;
        r.Script = new RoutineScript { Code = code, Migrated = true };
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
