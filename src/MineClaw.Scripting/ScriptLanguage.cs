using System.Text.RegularExpressions;

namespace MineClaw.Scripting;

/// <summary>
/// The language a script is written in. New authoring is <see cref="Js"/> only; <see cref="Lua"/> is the legacy runtime that
/// runs saved scripts not yet migrated (behind <see cref="ScriptOptions.LegacyLua"/>; removal plan in ARCHITECTURE.md).
/// </summary>
public enum ScriptLanguage { Js, Lua }

public static class ScriptLanguages
{
    /// <summary>"js" / "lua" as stored on a saved version (null = a version saved before JS existed: Lua).</summary>
    public static string Tag(ScriptLanguage l) => l == ScriptLanguage.Lua ? "lua" : "js";

    public static ScriptLanguage FromTag(string? tag) =>
        string.Equals(tag?.Trim(), "js", StringComparison.OrdinalIgnoreCase) || string.Equals(tag?.Trim(), "javascript", StringComparison.OrdinalIgnoreCase)
            ? ScriptLanguage.Js : ScriptLanguage.Lua;

    private static readonly Regex LuaMarkers = new(
        @"(^|\n)\s*--|\blocal\s+[A-Za-z_]|\bthen\b|\belseif\b|~=|\bend\s*(\n|$|\))|\.\.\s*[""'A-Za-z_(]|\bfunction\s*\([^)]*\)\s*\n?[^{]*\bend\b|[A-Za-z_]\s*\{\s*['""]|[A-Za-z_]['""][^'""\n]*['""]\s*(\n|$|[A-Za-z_])",
        RegexOptions.Compiled);

    private static readonly Regex JsMarkers = new(
        @"\b(const|let|var)\s+[A-Za-z_$]|=>|===|!==|\bfunction\s*\*?\s*[A-Za-z_$]*\s*\([^)]*\)\s*\{|(^|\n)\s*//|;\s*(\n|$)|\bawait\b|\bnull\b|\bundefined\b",
        RegexOptions.Compiled);

    /// <summary>
    /// The language of code that arrives without one (a legacy row, inline code): code that parses as JavaScript is JavaScript
    /// (typical Lua — <c>local x</c>, <c>go{"x"}</c>, <c>end</c> — never does); otherwise the stronger set of markers wins, a tie
    /// is JS (the parser's error then reaches the author).
    /// </summary>
    public static ScriptLanguage Detect(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return ScriptLanguage.Js;
        if (Lint.JsLint.Parse(code, out _) != null) return ScriptLanguage.Js;
        int lua = LuaMarkers.Matches(code).Count, js = JsMarkers.Matches(code).Count;
        return lua > js ? ScriptLanguage.Lua : ScriptLanguage.Js;
    }
}
