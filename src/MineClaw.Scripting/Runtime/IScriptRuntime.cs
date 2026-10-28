using System.Text.Json.Nodes;

namespace MineClaw.Scripting.Runtime;

/// <summary>
/// One language binding of a <see cref="ScriptSession"/>. The session owns control (thread, safe points, pauses, limits, events,
/// checkpoints, outcome); a runtime only turns its language's values into the session's neutral calls and back. JS (Jint) is
/// the runtime for every new script; Lua is the legacy runtime (removal plan in ARCHITECTURE.md).
/// </summary>
internal interface IScriptRuntime : IDisposable
{
    /// <summary>Runs the whole script on the calling (script) thread; up to 3 returned values (JSON scalars; null = nil).</summary>
    IReadOnlyList<JsonNode?> Run(string code, CancellationToken ct);
    /// <summary>The script line of the current host call (0 = unknown).</summary>
    int CurrentLine();
    /// <summary>An engine failure (syntax, runtime error, depth limit) → its terse reason and line; null = not the engine's.</summary>
    (string Reason, int Line)? Describe(Exception ex);
    /// <summary><c>carry</c> as JSON (≤ 2,000 chars) or null.</summary>
    string? CarryJson();
    /// <summary>A few scalar variables for the state line ("n=3 carry.x=2"), or null.</summary>
    string? Vars();
}

/// <summary>Picks the runtime for a run.</summary>
internal static class ScriptRuntimes
{
    public static IScriptRuntime Create(ScriptSession s) => s.Request.Lang switch
    {
        ScriptLanguage.Lua => new Lua.LuaRuntime(s),
        _ => new Js.JsRuntime(s),
    };
}
