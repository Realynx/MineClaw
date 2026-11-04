using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.ViewModels;

public sealed record TraitRow(string Id, string Text, string? Player, double Strength, string StrengthText, string Meta, string? Reason)
{
    public string PlayerText => string.IsNullOrEmpty(Player) ? "" : " · about " + Player;
}
public sealed record TraitGroup(string Section, IReadOnlyList<TraitRow> Entries)
{
    public string Header => $"{Section} · {Entries.Count}";
}
public sealed record HistoryItem(string Version, string When, string Op, string Section, string Text, string Reason, string Source);
public sealed record MoodItem(string Axis, double Value, string ValueText, double Bar);
public sealed record AbstractionItem(string Label, string Text);

/// <summary>The grown personality (SelfProfile) per scope: traits by section with strength, the versioned history, mood
/// aggregates and the self-tree abstractions reflection reads.</summary>
public sealed partial class PersonalityViewModel(MainWindowViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<string> Scopes { get; } = [];
    public ObservableCollection<TraitGroup> Groups { get; } = [];
    public ObservableCollection<HistoryItem> History { get; } = [];
    public ObservableCollection<MoodItem> Mood { get; } = [];
    public ObservableCollection<AbstractionItem> Abstractions { get; } = [];

    [ObservableProperty] private string? _scope;
    [ObservableProperty] private string _summary = "";

    public override async Task LoadAsync(MemoryBank? bank, CancellationToken ct)
    {
        Scopes.Clear();
        Groups.Clear(); History.Clear(); Mood.Clear(); Abstractions.Clear();
        if (bank == null) { EmptyText = "Open a database to see the bot's personality."; return; }
        var scopes = await Task.Run(() => new MemoryQueries(bank.Db).SelfScopes(), ct);
        foreach (var s in scopes) Scopes.Add(s);
        if (Scopes.Count == 0) { EmptyText = "This db has no self-profile yet."; Scope = null; return; }
        EmptyText = null;
        var keep = Scope;
        Scope = null;
        Scope = keep != null && Scopes.Contains(keep) ? keep : Scopes.Contains("main") ? "main" : Scopes[0];
    }

    partial void OnScopeChanged(string? value) => _ = LoadScopeAsync(value);

    private async Task LoadScopeAsync(string? scope)
    {
        Groups.Clear(); History.Clear(); Mood.Clear(); Abstractions.Clear();
        if (scope == null || Bank is not { } bank) return;
        IsLoading = true;
        try
        {
            var (entries, hist, mood, abs) = await Task.Run(() =>
            {
                var q = new MemoryQueries(bank.Db);
                return (q.SelfEntries(scope), q.SelfHistory(scope), q.SelfAggregates(scope, "mood"), q.SelfAbstractions(scope));
            });
            if (scope != Scope) return;
            foreach (var g in entries.GroupBy(e => e.Type).OrderBy(g => SectionRank(g.Key)))
                Groups.Add(new TraitGroup(Capital(g.Key), g.Select(TraitRowOf).ToList()));
            foreach (var h in hist) History.Add(HistoryItemOf(h));
            foreach (var m in mood.Where(m => m.Key.Length == 0 || m.Key == "_")) Mood.Add(MoodItemOf(m));
            foreach (var a in abs) Abstractions.Add(new AbstractionItem($"L{a.Layer} · {a.Leaves} notes", a.Text));
            Summary = $"{entries.Count} entries · {hist.Count} versions" + (hist.Count > 0 ? $" · last change {hist[0].At.ToLocalTime():yyyy-MM-dd HH:mm} ({hist[0].Source})" : "");
        }
        finally { IsLoading = false; }
    }

    private static readonly string[] SectionOrder = ["trait", "like", "dislike", "opinion", "joke", "nickname", "relationship", "milestone"];

    /// <summary>Known sections in their fixed order, any other after them.</summary>
    private static int SectionRank(string section) => Array.IndexOf(SectionOrder, section) is var i && i < 0 ? 99 : i;

    private static TraitRow TraitRowOf(MemoryRow e)
    {
        var p = e.Props.ToDictionary(x => x.Key, x => x.Value);
        return new TraitRow(e.Id, e.Text, p.GetValueOrDefault("player"), Math.Clamp(e.Confidence ?? 0, 0, 1) * 100,
            (e.Confidence ?? 0).ToString("0.00", CultureInfo.InvariantCulture),
            $"#{e.Id} · seen {p.GetValueOrDefault("observations", "1")}× · reinforced {e.UpdatedText}", p.GetValueOrDefault("reason"));
    }

    private static HistoryItem HistoryItemOf(MemoryQueries.HistoryRow h) =>
        new("v" + h.Version, h.At.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), h.Op, h.Section ?? "", h.Text ?? "", h.Reason ?? "", h.Source);

    private static MoodItem MoodItemOf(MemoryQueries.MoodRow m) =>
        new(m.Dim, m.Value, m.Value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture), Math.Clamp((m.Value + 1) / 2 * 100, 0, 100));

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..] + (s.EndsWith('s') ? "" : "s");
}
