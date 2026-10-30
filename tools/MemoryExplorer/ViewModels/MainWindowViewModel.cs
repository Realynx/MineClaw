using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly StartupOptions _startup;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _fetchCts;

    public MainWindowViewModel(StartupOptions startup)
    {
        _startup = startup;
        Config = LoadConfig(startup.Config);
        Supervisor = new SupervisorEndpoint(Config.SupervisorUrl);
        Overview = new OverviewViewModel(this);
        Tree = new RaptorTreeViewModel(this);
        Search = new SearchViewModel(this);
        Browser = new MemoryBrowserViewModel(this);
        Personality = new PersonalityViewModel(this);
        Edit = new EditSessionViewModel(this);
        Shards = new ShardsViewModel(this);
        Scripts = new ScriptsViewModel(this);
        Designs = new DesignsViewModel(this);
        LiveHive = new LiveHiveViewModel();
        Pages =
        [
            new NavItem("overview", "Overview", "IconOverview", Overview),
            new NavItem("tree", "RAPTOR tree", "IconTree", Tree),
            new NavItem("search", "Search", "IconSearch", Search),
            new NavItem("memory", "Memory", "IconTable", Browser),
            new NavItem("personality", "Personality", "IconPerson", Personality),
            new NavItem("shards", "Shards", "IconShard", Shards),
            new NavItem("scripts", "Scripts", "IconCode", Scripts),
            new NavItem("designs", "Designs", "IconCube", Designs),
            new NavItem("audit", "Edits & audit", "IconHistory", Edit),
            new NavItem("live", "Live Hive", "IconVideo", LiveHive),
        ];
        _selectedPage = Pages.FirstOrDefault(p => p.Key == startup.Page) ?? Pages[0];
        RefreshRecent();
    }

    private static ExplorerConfig LoadConfig(string? file)
    {
        try { return ExplorerConfig.Load(file); }
        catch (Exception) { return ExplorerConfig.Load(); }
    }

    public ExplorerConfig Config { get; private set; }
    public SupervisorEndpoint Supervisor { get; }
    public OverviewViewModel Overview { get; }
    public RaptorTreeViewModel Tree { get; }
    public SearchViewModel Search { get; }
    public MemoryBrowserViewModel Browser { get; }
    public PersonalityViewModel Personality { get; }
    public EditSessionViewModel Edit { get; }
    public ShardsViewModel Shards { get; }
    public ScriptsViewModel Scripts { get; }
    public DesignsViewModel Designs { get; }
    public LiveHiveViewModel LiveHive { get; }
    public ObservableCollection<NavItem> Pages { get; }
    public ObservableCollection<RecentDb> Recent { get; } = [];

    [ObservableProperty] private MemoryBank? _bank;
    [ObservableProperty] private NavItem _selectedPage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _sourceTitle = "No database";
    [ObservableProperty] private string _sourceDetail = "Open a MineClaw db or fetch a snapshot from alice";
    [ObservableProperty] private string _snapshotText = "";
    [ObservableProperty] private string _themeLabel = "System";
    [ObservableProperty] private string _globalQuery = "";
    [ObservableProperty] private bool _isFetching;
    /// <summary>"knowledge b1.8.1" chip in the header: the db's active knowledge pack (memory shards swap it).</summary>
    [ObservableProperty] private string _knowledgePackText = "";
    [ObservableProperty] private string? _knowledgePackTip;

    public string EmbeddingsStatus => Config.EmbeddingsStatus;
    public bool HasBank => Bank != null;
    public object? CurrentPage => SelectedPage?.Page;
    public string ModeBadge => Edit.EditMode ? "EDIT MODE" : "READ-ONLY";
    public bool IsEditMode => Edit.EditMode;

    /// <summary>The view supplies the clipboard and a file picker (keeps this VM free of window plumbing).</summary>
    public Func<string, Task>? Clipboard { get; set; }
    public Func<Task<string?>>? PickDatabase { get; set; }
    public Func<Task<string?>>? PickConfig { get; set; }
    /// <summary>Focus the global search box (Ctrl+K).</summary>
    public event Action? FocusSearchRequested;
    /// <summary>Ask the user to confirm (title, message) → true when confirmed.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    partial void OnSelectedPageChanged(NavItem? oldValue, NavItem newValue)
    {
        (oldValue?.Page as PageViewModel)?.Deactivate();
        if (oldValue?.Page is LiveHiveViewModel lo) lo.Deactivate();
        (newValue?.Page as PageViewModel)?.Activate();
        if (newValue?.Page is LiveHiveViewModel ln) ln.Activate();
        OnPropertyChanged(nameof(CurrentPage));
    }

    partial void OnBankChanged(MemoryBank? value) => OnPropertyChanged(nameof(HasBank));

    internal void NotifyEditMode()
    {
        OnPropertyChanged(nameof(ModeBadge));
        OnPropertyChanged(nameof(IsEditMode));
    }

    public async Task InitializeAsync()
    {
        var db = _startup.Db ?? ExplorerPaths.KnownDatabases().FirstOrDefault();
        if (db != null) await OpenAsync(db);
        else Status = "No database found under data/. Open one or fetch a snapshot from alice.";
    }

    public void Navigate(string key)
    {
        if (Pages.FirstOrDefault(p => p.Key == key) is { } p) SelectedPage = p;
    }

    public void RefreshRecent()
    {
        Recent.Clear();
        foreach (var p in ExplorerPaths.KnownDatabases().Take(12))
            Recent.Add(new RecentDb(p, ExplorerPaths.Display(p), MemoryBank.FormatBytes(MemoryBank.FileSize(p)),
                MemoryBank.SnapshotTime(p).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)));
    }

    [RelayCommand]
    public async Task OpenAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsBusy = true;
        Status = $"Opening {ExplorerPaths.Display(path)} read-only…";
        try
        {
            // A different file ends the edit session; reloading the same file (after an edit) keeps it.
            if (Bank == null || !string.Equals(Path.GetFullPath(path), Bank.Path, StringComparison.OrdinalIgnoreCase)) await Edit.CloseSessionAsync();
            var bank = await MemoryBank.OpenAsync(path, Config, cts.Token);
            var old = Bank;
            Bank = bank;
            old?.Dispose();
            UpdateHeader();
            Status = "Loading views…";
            foreach (var page in Pages.Select(p => p.Page).OfType<PageViewModel>())
            {
                try { await page.LoadAsync(bank, cts.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Status = $"{page.GetType().Name}: {ex.Message}"; }
            }
            Status = bank.HasTree
                ? $"Loaded {ExplorerPaths.Display(path)}: {bank.Raptor!.Index.NodeCount:N0} tree nodes, {bank.Rag.Count():N0} docs"
                : $"Loaded {ExplorerPaths.Display(path)} ({bank.TreeNote})";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = "Open failed: " + ex.Message; }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsBusy = false;
        }
    }

    /// <summary>Reloads the current bank (after an edit or rebuild).</summary>
    public Task ReloadAsync() => Bank is { } b ? OpenAsync(b.Path) : Task.CompletedTask;

    private void UpdateHeader()
    {
        if (Bank is not { } b) return;
        SourceTitle = b.Kind switch
        {
            SourceKind.LocalDev => "Local dev db",
            SourceKind.Snapshot => "Snapshot from alice",
            SourceKind.Seed => "Clean-state seed",
            _ => Path.GetFileName(b.Path),
        };
        SourceDetail = $"{ExplorerPaths.Display(b.Path)} · {MemoryBank.FormatBytes(b.SizeBytes)} · schema v{b.Db.SchemaVersion}";
        var pack = MineClaw.Agent.Admin.Shards.KnowledgePack.Active(b.Db);
        KnowledgePackText = "knowledge " + pack.Version;
        KnowledgePackTip = pack.IsDefault ? "Active knowledge pack: the built-in b1.8.1 seed (recipes, items, blocks)"
            : $"Active knowledge pack: {pack.Version} ({pack.Name}, installed {pack.Installed?.ToLocalTime():yyyy-MM-dd HH:mm}). Swap it from the Shards page.";
        var age = DateTimeOffset.UtcNow - b.SnapshotAt;
        SnapshotText = (b.Kind == SourceKind.Snapshot ? "snapshot " : "modified ") + b.SnapshotAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                       $" ({Ago(age)})";
    }

    public static string Ago(TimeSpan t) => t.TotalSeconds < 90 ? "just now" : t.TotalMinutes < 90 ? $"{(int)t.TotalMinutes} min ago"
        : t.TotalHours < 48 ? $"{(int)t.TotalHours} h ago" : $"{(int)t.TotalDays} d ago";

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickDatabase == null) return;
        var p = await PickDatabase();
        if (p != null) await OpenAsync(p);
    }

    [RelayCommand]
    private async Task OpenConfigAsync()
    {
        if (PickConfig == null) return;
        var p = await PickConfig();
        if (p == null) return;
        try
        {
            Config = ExplorerConfig.Load(p);
            OnPropertyChanged(nameof(EmbeddingsStatus));
            Status = $"Config layered in: {Path.GetFileName(p)} ({Config.EmbeddingsStatus})";
            if (Bank != null) await ReloadAsync();
        }
        catch (Exception ex) { Status = "Config failed: " + ex.Message; }
    }

    [RelayCommand]
    private async Task FetchSnapshotAsync()
    {
        if (IsFetching) { _fetchCts?.Cancel(); return; }
        IsFetching = true;
        var cts = _fetchCts = new CancellationTokenSource();
        try
        {
            var svc = new SnapshotService(new ProcessRunner());
            var progress = new Progress<string>(s => Status = s);
            var r = await svc.FetchAsync(progress, cts.Token);
            RefreshRecent();
            await OpenAsync(r.Path);
            Status = $"Snapshot via {r.Method}: {ExplorerPaths.Display(r.Path)} ({MemoryBank.FormatBytes(r.Bytes)})";
        }
        catch (OperationCanceledException) { Status = "Snapshot cancelled"; }
        catch (Exception ex) { Status = "Snapshot failed: " + ex.Message; }
        finally { IsFetching = false; }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var app = Application.Current;
        if (app == null) return;
        var current = app.RequestedThemeVariant;
        (app.RequestedThemeVariant, ThemeLabel) = current == ThemeVariant.Default || current == null
            ? (ThemeVariant.Light, "Light")
            : current == ThemeVariant.Light ? (ThemeVariant.Dark, "Dark") : (ThemeVariant.Default, "System");
    }

    [RelayCommand]
    private void FocusSearch() => FocusSearchRequested?.Invoke();

    [RelayCommand]
    private async Task GlobalSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(GlobalQuery)) { Navigate("search"); return; }
        Search.Query = GlobalQuery;
        Navigate("search");
        await Search.RunSearchAsync();
    }

    public async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text) || Clipboard == null) return;
        await Clipboard(text);
        Status = text.Length > 60 ? $"Copied {text.Length:N0} characters" : $"Copied \"{text.ReplaceLineEndings(" ")}\"";
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _fetchCts?.Cancel();
        LiveHive.Dispose();
        Edit.Dispose();
        Bank?.Dispose();
    }
}

public sealed record RecentDb(string Path, string Display, string Size, string When);
