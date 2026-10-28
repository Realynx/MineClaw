using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.Agent.Routines;
using MineClaw.MemoryExplorer.Controls;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.ViewModels;

/// <summary>A row of the library list.</summary>
public sealed record ScriptRow(ScriptLibraryEntry E)
{
    public string Name => E.Name;
    public string VersionText => E.Versions > 1 ? $"v{E.Version} of {E.Versions}" : $"v{E.Version}";
    public string LangText => E.Lang == "js" ? (E.HasLuaHistory ? "JS · Lua history" : "JS") : "Lua";
    public string AuthorText => ScriptLabels.Author(E.AuthorKind, E.Author);
    public string ParamsText => E.Params.Count == 0 ? "—" : string.Join(", ", E.Params.Select(ScriptLabels.Param));
    public string LastRunText => E.LastRun is { } t ? t.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "never";
    public string RateText => E.Runs == 0 ? "no runs" : $"{E.SuccessRate:P0} of {E.Runs}";
    public string TimeText => E.Runs == 0 ? "" : $"avg {E.AvgSeconds:0.#} s" + (E.Timings is { Length: > 0 } t ? " · " + t : "");
    public string UsedByText => E.UsedBy.Count == 0 ? "" : string.Join(" · ", E.UsedBy);
    public bool HasUsers => E.UsedBy.Count > 0;
    public string LintText => E.Lint.Status switch
    {
        "errors" => $"{E.Lint.Errors} lint error{(E.Lint.Errors == 1 ? "" : "s")}",
        "warnings" => $"{E.Lint.Warnings} warning{(E.Lint.Warnings == 1 ? "" : "s")}",
        "ok" => "lint ok",
        _ => "not linted",
    };
    public bool LintBad => E.Lint.Status == "errors";
    public bool LintWarn => E.Lint.Status == "warnings";
    public string Flags => string.Join(" · ", new[] { E.Archived ? "archived" : null, E.PromotedAs is { } p ? "skill " + p : null, E.DuplicateOf is { } d ? "duplicate of " + d : null }
        .Where(x => x != null));
    public string Search => $"{E.Name} {E.Purpose} {E.Author} {string.Join(" ", E.UsedBy)} {string.Join(" ", (E.Handlers ?? []).Select(h => h.Kind))}".ToLowerInvariant();
    /// <summary>The script's learned interrupt handlers, shown as its children in the list.</summary>
    public IReadOnlyList<ScriptHandlerRow> Handlers { get; } = (E.Handlers ?? []).Select(h => new ScriptHandlerRow(h)).ToList();
    public bool HasHandlers => Handlers.Count > 0;
}

/// <summary>A learned interrupt handler (a child of its script): kind, situation, stats, the JS it runs and what happens after.</summary>
public sealed record ScriptHandlerRow(ScriptHandlerInfo H)
{
    public string Kind => H.Kind;
    /// <summary>"↳ lowhp · hp #/# zombie · 5 ok / 1 failed · resume".</summary>
    public string Label => $"↳ {H.Kind} · {Situation} · {Stats} · {H.Then}" + (H.Demoted ? " · demoted" : "");
    public string Situation
    {
        get
        {
            var parts = H.Signature.Split('|');
            var text = parts.Length > 1 ? parts[1] : H.Signature;
            return text.Length <= 48 ? text : text[..45] + "...";
        }
    }
    public string Stats => H.Failures == 0 ? $"{H.Successes} ok" : $"{H.Successes} ok / {H.Failures} failed";
    public bool Demoted => H.Demoted;
    /// <summary>Demoted handlers are shown faded (the patch turn runs again for their situation).</summary>
    public double Fade => H.Demoted ? 0.5 : 1.0;
    public string Detail => (H.Note is { Length: > 0 } n ? n + " · " : "") + (H.Author ?? "patch") + (H.Version is { } v ? $" · learned on v{v}" : "")
        + (H.LearnedAt is { } t ? " · " + t.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "")
        + (H.LastUsed is { } u ? " · last used " + u.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "");
    public string Where => H.Signature.Split('|') is { Length: > 2 } p && p[2].Length > 0 ? "at `" + p[2] + "`" : "";
    public string Js => H.Js;
}

public sealed record ScriptVersionRow(ScriptVersionInfo V)
{
    public string Label => $"v{V.V} · {(V.Lang == "js" ? "JS" : "Lua")} · {ScriptLabels.Author(V.AuthorKind, V.Author)}" + (V.Active ? " · active" : "");
    public string Stats => (V.SavedAt is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " · " : "")
        + (V.Runs == 0 ? "no runs" : $"{V.Successes}/{V.Runs} ok · avg {V.AvgSeconds:0.#} s") + $" · {V.Lines} lines"
        + (V.TopFailures.Count > 0 ? " · " + string.Join(", ", V.TopFailures) : "");
    public override string ToString() => $"v{V.V} ({(V.Lang == "js" ? "JS" : "Lua")})";
}

public sealed record ScriptParamRow(string Name, string Default, string Tuned)
{
    public bool IsTuned => Tuned.Length > 0;
}

public sealed record ScriptLintRow(int Line, string Text, bool IsError);

public sealed record ScriptRunRow(string When, string Result, string Detail, bool Ok);

/// <summary>One line of the source view: number, highlighted spans, lint mark, current-line flag (the debugger moves it).</summary>
public sealed partial class CodeLineRow(int no, IReadOnlyList<CodeSpan> spans, string? lint = null, bool lintError = false) : ObservableObject
{
    public int No { get; } = no;
    public string NoText { get; } = no.ToString(CultureInfo.InvariantCulture);
    public IReadOnlyList<CodeSpan> Spans { get; } = spans;
    public string? Lint { get; } = lint;
    public bool HasLint => Lint != null;
    public bool LintError { get; } = lintError;
    [ObservableProperty] private bool _isCurrent;

    public static List<CodeLineRow> Build(string? code, bool lua, IReadOnlyList<ScriptLintLine>? lint = null)
    {
        var lines = CodeHighlighter.Lines(code, lua);
        var byLine = (lint ?? []).GroupBy(f => f.Line).ToDictionary(g => g.Key, g => g.ToList());
        return lines.Select((spans, i) =>
        {
            var marks = byLine.GetValueOrDefault(i + 1);
            return new CodeLineRow(i + 1, spans, marks == null ? null : string.Join("; ", marks.Select(m => m.Message)), marks?.Any(m => m.Severity == "error") == true);
        }).ToList();
    }
}

/// <summary>A diff line with its text highlighted.</summary>
public sealed record ScriptDiffRow(DiffLine Line, IReadOnlyList<CodeSpan> Spans)
{
    public static List<ScriptDiffRow> Build(string? oldCode, bool oldLua, string? newCode, bool newLua, bool hunksOnly = true)
    {
        var lines = ScriptDiff.Lines(oldCode, newCode);
        var oldHl = CodeHighlighter.Lines(oldCode, oldLua);
        var newHl = CodeHighlighter.Lines(newCode, newLua);
        var shown = hunksOnly ? ScriptDiff.Hunks(lines) : lines;
        return shown.Select(l => new ScriptDiffRow(l, l.Kind switch
        {
            DiffKind.Removed when l.OldNo is { } o && o - 1 < oldHl.Count => oldHl[o - 1],
            DiffKind.Same or DiffKind.Added when l.NewNo is { } n && n - 1 < newHl.Count => newHl[n - 1],
            _ => [new CodeSpan(l.Text, l.Kind == DiffKind.Gap ? CodeKind.Comment : CodeKind.Plain)],
        })).ToList();
    }
}

internal static class ScriptLabels
{
    public static string Author(string kind, string? author) => kind switch
    {
        "opus" => "Opus" + (author is { } a && a.Contains('/') ? $" ({a[..a.IndexOf('/')]})" : ""),
        "migration" => "Lua migration",
        "legacy" => "legacy (Lua)",
        _ => author ?? "unknown",
    };

    public static string Param(ScriptParamInfo p) =>
        p.Name + (p.Default != null ? "=" + p.Default : "") + (p.Tuned != null && p.Tuned != p.Default ? $" → {p.Tuned}" : "");
}

/// <summary>
/// The Scripts page: the saved script library (from the opened db or live from the Supervisor) with versions, authors, params,
/// reliability and timings, and who runs each one; the selected script's source (highlighted, lint marks), its version history
/// with a diff between any two versions (a migrated script's Lua original included), declared params, lint and recent runs.
/// Read-only.
/// </summary>
public sealed partial class ScriptsViewModel(MainWindowViewModel shell) : PageViewModel(shell)
{
    public const string SourceDb = "This database";
    public const string SourceLive = "Live (Supervisor)";
    public IReadOnlyList<string> SourceOptions { get; } = [SourceDb, SourceLive];

    private IScriptLibrarySource? _source;
    private List<ScriptRow> _all = [];
    private readonly Dictionary<(string, int), ScriptVersionDetail> _versions = new();
    private CancellationTokenSource? _detailCts;

    public ObservableCollection<ScriptRow> Scripts { get; } = [];
    public ObservableCollection<ScriptVersionRow> Versions { get; } = [];
    public ObservableCollection<CodeLineRow> Code { get; } = [];
    public ObservableCollection<ScriptDiffRow> Diff { get; } = [];
    public ObservableCollection<ScriptParamRow> Params { get; } = [];
    public ObservableCollection<ScriptLintRow> Lint { get; } = [];
    public ObservableCollection<ScriptRunRow> Runs { get; } = [];
    /// <summary>The selected script's learned interrupt handlers (the Handlers tab).</summary>
    public ObservableCollection<ScriptHandlerRow> Handlers { get; } = [];

    [ObservableProperty] private string _selectedSource = SourceDb;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _sourceNote = "";
    [ObservableProperty] private ScriptRow? _selected;
    [ObservableProperty] private ScriptVersionRow? _selectedVersion;
    [ObservableProperty] private ScriptVersionRow? _compareVersion;
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailMeta = "";
    [ObservableProperty] private string _lintSummary = "";
    [ObservableProperty] private string _diffSummary = "";
    [ObservableProperty] private string _unifiedDiff = "";
    [ObservableProperty] private string _codeText = "";
    [ObservableProperty] private string _revertNote = "";
    [ObservableProperty] private string _migrationNote = "";
    [ObservableProperty] private bool _showDiff;
    [ObservableProperty] private bool _hasDetail;

    public bool CanDiff => Versions.Count > 1;

    public override async Task LoadAsync(MemoryBank? bank, CancellationToken ct)
    {
        if (SelectedSource == SourceDb) await UseAsync(bank == null ? null : new DbScriptLibrarySource(bank.Db, ExplorerPaths.Display(bank.Path)), ct);
    }

    partial void OnSelectedSourceChanged(string value) => _ = SwitchSourceAsync();

    private async Task SwitchSourceAsync()
    {
        try
        {
            if (SelectedSource == SourceLive) await UseAsync(new LiveScriptLibrarySource(new SupervisorEndpoint(Shell.Config.SupervisorUrl)), CancellationToken.None);
            else await UseAsync(Bank == null ? null : new DbScriptLibrarySource(Bank.Db, ExplorerPaths.Display(Bank.Path)), CancellationToken.None);
        }
        catch (Exception ex) { SourceNote = ex.Message; }
    }

    /// <summary>Reads the library from <paramref name="source"/> (tests pass a fake; null = nothing to show).</summary>
    public async Task UseAsync(IScriptLibrarySource? source, CancellationToken ct)
    {
        (_source as IDisposable)?.Dispose();
        _source = source;
        _versions.Clear();
        await RefreshAsync(ct);
    }

    [RelayCommand]
    private Task Refresh() { _versions.Clear(); return RefreshAsync(CancellationToken.None); }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var keep = Selected?.Name;
        _all = [];
        Scripts.Clear();
        ClearDetail();
        if (_source == null) { EmptyText = "Open a database (or switch to Live) to see the saved scripts."; Summary = ""; return; }
        IsLoading = true;
        try
        {
            var list = await _source.ListAsync(ct);
            _all = list.Select(e => new ScriptRow(e)).ToList();
            ApplyFilter();
            int js = list.Count(e => e.Lang == "js"), lua = list.Count - js;
            Summary = $"{list.Count} saved script{(list.Count == 1 ? "" : "s")} · {js} JS, {lua} Lua · {list.Count(e => e.UsedBy.Count > 0)} in use · {_source.Label}";
            SourceNote = _source.IsLive ? "live: lint uses the bot's real game functions" : "offline lint: game function names not checked";
            EmptyText = list.Count == 0 ? "No saved scripts in this source." : null;
            Selected = Scripts.FirstOrDefault(s => s.Name == keep) ?? Scripts.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EmptyText = "Couldn't read the scripts: " + ex.Message; }
        finally { IsLoading = false; }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = Filter.Trim().ToLowerInvariant();
        Scripts.Clear();
        foreach (var r in _all.Where(r => f.Length == 0 || r.Search.Contains(f, StringComparison.Ordinal))) Scripts.Add(r);
    }

    partial void OnSelectedChanged(ScriptRow? value) => _ = SelectAsync(value);

    private void ClearDetail()
    {
        Versions.Clear(); Code.Clear(); Diff.Clear(); Params.Clear(); Lint.Clear(); Runs.Clear(); Handlers.Clear();
        DetailTitle = DetailMeta = LintSummary = DiffSummary = UnifiedDiff = CodeText = RevertNote = MigrationNote = "";
        HasDetail = false;
        OnPropertyChanged(nameof(CanDiff));
    }

    private bool _selecting;

    private async Task SelectAsync(ScriptRow? row)
    {
        ClearDetail();
        if (row == null || _source == null) return;
        var d = await DetailAsync(row.Name, null);
        if (d == null || Selected != row) return;
        _selecting = true;
        try
        {
            foreach (var v in d.History.OrderByDescending(v => v.V)) Versions.Add(new ScriptVersionRow(v));
            OnPropertyChanged(nameof(CanDiff));
            SelectedVersion = Versions.FirstOrDefault(v => v.V.V == d.Version.V);
            CompareVersion = Versions.FirstOrDefault(v => v.V.V < d.Version.V);
        }
        finally { _selecting = false; }
        Show(d);
        await RefreshDiffAsync();
    }

    partial void OnSelectedVersionChanged(ScriptVersionRow? value)
    {
        if (_selecting || value == null || Selected == null) return;
        _ = ShowVersionAsync(Selected.Name, value.V.V);
    }

    partial void OnCompareVersionChanged(ScriptVersionRow? value)
    {
        if (!_selecting) _ = RefreshDiffAsync();
    }

    private async Task ShowVersionAsync(string name, int v)
    {
        if (await DetailAsync(name, v) is { } d && Selected?.Name == name) { Show(d); await RefreshDiffAsync(); }
    }

    private async Task<ScriptVersionDetail?> DetailAsync(string name, int? v)
    {
        if (_source == null) return null;
        if (v is { } vv && _versions.TryGetValue((name, vv), out var cached)) return cached;
        _detailCts?.Cancel();
        var cts = _detailCts = new CancellationTokenSource();
        try
        {
            var d = await _source.VersionAsync(name, v, cts.Token);
            if (d != null) _versions[(name, d.Version.V)] = d;
            return d;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { DetailMeta = "Couldn't load it: " + ex.Message; return null; }
    }

    private void Show(ScriptVersionDetail d)
    {
        HasDetail = true;
        var v = d.Version;
        DetailTitle = $"{d.Script.Name} · v{v.V}";
        DetailMeta = $"{(v.Lang == "js" ? "JavaScript" : "Lua (legacy)")} · {ScriptLabels.Author(v.AuthorKind, v.Author)}"
            + (v.SavedAt is { } t ? " · saved " + t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "")
            + (d.Script.Purpose is { Length: > 0 } purpose ? " · " + purpose : "") + (d.Script.UsedBy.Count > 0 ? " · used by " + string.Join(", ", d.Script.UsedBy) : "");
        CodeText = d.Code;
        Code.Clear();
        foreach (var l in CodeLineRow.Build(d.Code, v.Lang == "lua", d.Lint.Findings)) Code.Add(l);
        Params.Clear();
        foreach (var p in d.Params) Params.Add(new ScriptParamRow(p.Name, p.Default ?? "", p.Tuned ?? ""));
        Lint.Clear();
        foreach (var f in d.Lint.Findings) Lint.Add(new ScriptLintRow(f.Line, $"L{f.Line} {f.Message}", f.Severity == "error"));
        LintSummary = d.Lint.Status switch
        {
            "ok" => "lint: clean",
            "errors" => $"lint: {d.Lint.Errors} error(s), {d.Lint.Warnings} warning(s)",
            "warnings" => $"lint: {d.Lint.Warnings} warning(s)",
            _ => "lint: not available",
        } + (d.Lint.Note is { } n ? " · " + n : "");
        Runs.Clear();
        foreach (var r in d.Runs)
            Runs.Add(new ScriptRunRow(r.At.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                (r.Ok ? "ok" : r.Status ?? "failed") + $" · {r.Seconds:0.#} s" + (r.Actions is { } a ? $" · {a} actions" : "") + (r.FailLine is { } fl ? $" · line {fl}" : ""),
                r.Outcome ?? string.Join(" · ", new[] { r.Params, r.Timings, r.Yield is > 0 ? $"+{r.Yield} items" : null, r.Body }.Where(x => !string.IsNullOrEmpty(x))),
                r.Ok));
        Handlers.Clear();
        foreach (var h in d.Script.Handlers ?? []) Handlers.Add(new ScriptHandlerRow(h));
        RevertNote = d.RevertNote ?? "";
        MigrationNote = d.Script.Migration is { } m ? "Lua migration: " + m : "";
    }

    /// <summary>Diff of the shown version against <see cref="CompareVersion"/> (older on the left).</summary>
    private async Task RefreshDiffAsync()
    {
        Diff.Clear();
        DiffSummary = UnifiedDiff = "";
        if (Selected is not { } row || SelectedVersion is not { } cur || CompareVersion is not { } other || cur.V.V == other.V.V) return;
        var a = await DetailAsync(row.Name, Math.Min(cur.V.V, other.V.V));
        var b = await DetailAsync(row.Name, Math.Max(cur.V.V, other.V.V));
        if (a == null || b == null) return;
        foreach (var r in ScriptDiffRow.Build(a.Code, a.Version.Lang == "lua", b.Code, b.Version.Lang == "lua")) Diff.Add(r);
        var (add, del) = ScriptDiff.Stats(ScriptDiff.Lines(a.Code, b.Code));
        DiffSummary = $"v{a.Version.V} ({(a.Version.Lang == "js" ? "JS" : "Lua")}) → v{b.Version.V} ({(b.Version.Lang == "js" ? "JS" : "Lua")}): +{add} −{del} lines";
        UnifiedDiff = ScriptDiff.Unified(ScriptDiff.Lines(a.Code, b.Code), $"{row.Name} v{a.Version.V}", $"{row.Name} v{b.Version.V}");
    }

    [RelayCommand]
    private Task CopyCode() => Shell.CopyAsync(CodeText);

    [RelayCommand]
    private Task CopyDiff() => Shell.CopyAsync(UnifiedDiff);
}
