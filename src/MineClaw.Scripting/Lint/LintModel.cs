namespace MineClaw.Scripting;

public enum LintSeverity { Warning, Error }

public sealed record LintFinding(LintSeverity Severity, int Line, string Rule, string Message)
{
    public override string ToString() => $"L{Line} {Message}";
}

/// <summary>A Lua binding's args: positional order and the params it can't run without (see <see cref="ScriptLint.SignatureOf"/>).</summary>
public sealed record BindingSignature(IReadOnlyList<string> Params, IReadOnlySet<string> Required);

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
