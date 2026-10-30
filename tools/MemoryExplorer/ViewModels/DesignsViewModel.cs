using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.ViewModels;

/// <summary>A design in the list.</summary>
public sealed record DesignRow(DesignEntry E)
{
    public string Title => $"#{E.Id} {E.Name}";
    public string Meta => $"{E.Status} · " + (E.Revisions == 0 ? "no revisions yet (r1 on the bot's first look)" : $"r{E.Head} · {E.Revisions} revision{(E.Revisions == 1 ? "" : "s")}")
        + (E.Requester is { Length: > 0 } r ? $" · for {r}" : "") + " · " + E.Updated.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    public string Search => $"{E.Id} {E.Name} {E.Status} {E.Requester}".ToLowerInvariant();
}

/// <summary>One revision in the history list (and a side of the diff).</summary>
public sealed record DesignRevisionRow(DesignRevisionInfo R)
{
    public int Rev => R.Rev;
    public string Label => $"r{R.Rev} · {R.Author} ({R.AuthorKind}) · {R.At.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)}" + (R.Tag is { Length: > 0 } t ? $" · [{t}]" : "");
    public string Message => $"'{R.Message}' · {R.Blocks} blocks";
    public override string ToString() => $"r{R.Rev}" + (R.Tag is { Length: > 0 } t ? $" [{t}]" : "");
}

/// <summary>
/// The Designs page: every saved design with its git-style history (Fox 10-30: "similar to Git style, so that we can detect
/// changes and do revisions and regressions"): the revision list (author, kind, message, tag), the bot's rendered preview, and
/// a diff between two revisions or between a revision and the world as the bot last saw it (the drift watcher's view): counts
/// plus every cell. Read-only: commits and reverts are the bot's (<c>design commit/revert</c>).
/// </summary>
public sealed partial class DesignsViewModel(MainWindowViewModel shell) : PageViewModel(shell)
{
    /// <summary>The right-hand side of the diff that means "the world as last seen".</summary>
    public const string WorldSide = "the world (last seen)";

    private DesignHistorySource? _source;
    private List<DesignRow> _all = [];

    public ObservableCollection<DesignRow> Designs { get; } = [];
    public ObservableCollection<DesignRevisionRow> Revisions { get; } = [];
    /// <summary>The diff's "to" side: the revisions, then <see cref="WorldSide"/> when the bot has looked.</summary>
    public ObservableCollection<object> Targets { get; } = [];
    public ObservableCollection<string> DiffCells { get; } = [];

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private DesignRow? _selected;
    [ObservableProperty] private DesignRevisionRow? _from;
    [ObservableProperty] private object? _to;
    [ObservableProperty] private string _diffSummary = "";
    [ObservableProperty] private string _worldNote = "";
    [ObservableProperty] private Bitmap? _picture;
    [ObservableProperty] private bool _hasDetail;

    public override Task LoadAsync(MemoryBank? bank, CancellationToken ct)
    {
        Use(bank == null ? null : new DesignHistorySource(bank.Db, bank.Path));
        return Task.CompletedTask;
    }

    /// <summary>Reads the designs from <paramref name="source"/> (tests pass one over a fixture db; null = nothing to show).</summary>
    public void Use(DesignHistorySource? source)
    {
        _source = source;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var keep = Selected?.E.Id;
        _all = [];
        Designs.Clear();
        ClearDetail();
        if (_source == null) { EmptyText = "Open a database to see the saved designs."; Summary = ""; return; }
        try
        {
            _all = _source.Designs().Select(e => new DesignRow(e)).ToList();
            ApplyFilter();
            Summary = $"{_all.Count} design{(_all.Count == 1 ? "" : "s")} · {_all.Sum(d => d.E.Revisions)} revisions";
            EmptyText = _all.Count == 0 ? (_source.Available ? "No saved designs in this db." : "This db has no designs table.") : null;
            Selected = Designs.FirstOrDefault(d => d.E.Id == keep) ?? Designs.FirstOrDefault();
        }
        catch (Exception ex) { EmptyText = "Couldn't read the designs: " + ex.Message; }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = Filter.Trim().ToLowerInvariant();
        Designs.Clear();
        foreach (var d in _all.Where(d => f.Length == 0 || d.Search.Contains(f, StringComparison.Ordinal))) Designs.Add(d);
    }

    private void ClearDetail()
    {
        Revisions.Clear(); Targets.Clear(); DiffCells.Clear();
        DiffSummary = WorldNote = "";
        Picture = null;
        HasDetail = false;
    }

    private bool _selecting;

    partial void OnSelectedChanged(DesignRow? value)
    {
        ClearDetail();
        if (value == null || _source == null) return;
        _selecting = true;
        try
        {
            foreach (var r in _source.Revisions(value.E.Id)) Revisions.Add(new DesignRevisionRow(r));
            foreach (var r in Revisions) Targets.Add(r);
            var world = _source.World(value.E.Id);
            if (world != null)
            {
                Targets.Insert(0, WorldSide);
                WorldNote = $"last seen by {world.SeenBy} {world.Seen.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)} at r{world.Rev}: {world.Diff}"
                            + (world.Causes.Length > 0 ? $" ({world.Causes})" : "");
            }
            Picture = Png(value.E.HasPicture ? _source.Picture(value.E.Id) : null);
            HasDetail = true;
            // Default: what HEAD lacks against the world (drift), else the last change (HEAD~1 → HEAD).
            From = Revisions.FirstOrDefault();
            To = world != null ? WorldSide : Revisions.Count > 1 ? Revisions[1] : null;
            if (world == null && Revisions.Count > 1) (From, To) = (Revisions[1], Revisions[0]);
        }
        finally { _selecting = false; }
        RefreshDiff();
    }

    partial void OnFromChanged(DesignRevisionRow? value) { if (!_selecting) RefreshDiff(); }
    partial void OnToChanged(object? value) { if (!_selecting) RefreshDiff(); }

    private void RefreshDiff()
    {
        DiffCells.Clear();
        DiffSummary = "";
        if (_source == null || Selected is not { } d || From is not { } a) return;
        (string Summary, IReadOnlyList<string> Cells)? r;
        string head;
        if (To is string s && s == WorldSide)
        {
            r = _source.AgainstWorld(d.E.Id, a.Rev);
            head = $"r{a.Rev} → world";
        }
        else if (To is DesignRevisionRow b && b.Rev != a.Rev)
        {
            r = _source.Between(d.E.Id, a.Rev, b.Rev);
            head = $"r{Math.Min(a.Rev, b.Rev)} → r{Math.Max(a.Rev, b.Rev)}";
        }
        else return;
        if (r is not { } diff) { DiffSummary = head + ": no block set to compare"; return; }
        DiffSummary = $"{head}: {diff.Summary}";
        foreach (var c in diff.Cells.Take(500)) DiffCells.Add(c);
        if (diff.Cells.Count > 500) DiffCells.Add($"… {diff.Cells.Count - 500} more");
    }

    private static Bitmap? Png(byte[]? png)
    {
        if (png == null) return null;
        try { using var ms = new MemoryStream(png); return new Bitmap(ms); } catch (Exception) { return null; }
    }

    [RelayCommand]
    private Task CopyDiff() => Shell.CopyAsync(DiffSummary + "\n" + string.Join("\n", DiffCells));
}
