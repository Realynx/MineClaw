using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.Agent.Telemetry;

namespace MineClaw.MemoryExplorer.LiveHive;

// The Controller panel of a Live Hive tile: whatever drives the body. A hive brain (main or a hive clone) shows its live context
// window, its turns with thinking/tool calls/usage and their sub-entries; a subhive body shows its owner's brain with the turns
// that targeted it highlighted; a script-only body shows the script debugger; a hive running a script shows both (Brain | Script).
// Wire shapes are the Agent's BrainContextModels (the Supervisor's /api/bodies/{name}/controller, /api/brains/{id}/context|turn/{n}).

/// <summary>Where the panel reads (the Supervisor; tests use a fake).</summary>
public interface IControllerSource
{
    Task<BodyControllerInfo> ControllerAsync(string body, CancellationToken ct);
    Task<BrainContextPage> ContextAsync(string brain, long since, string? body, CancellationToken ct);
    Task<BrainTurnDetail> TurnAsync(string brain, long turn, string? body, CancellationToken ct);
}

/// <summary>The live source: GETs through the Live Hive's <see cref="HiveApi"/> (its timeouts and per-route back-off).</summary>
public sealed class HiveControllerSource(HiveApi api) : IControllerSource
{
    public Task<BodyControllerInfo> ControllerAsync(string body, CancellationToken ct) =>
        api.GetJsonAsync<BodyControllerInfo>("controller", $"api/bodies/{Uri.EscapeDataString(body)}/controller", ct, track: true);

    public Task<BrainContextPage> ContextAsync(string brain, long since, string? body, CancellationToken ct) =>
        api.GetJsonAsync<BrainContextPage>("brain_ctx", $"api/brains/{Uri.EscapeDataString(brain)}/context?since={since.ToString(CultureInfo.InvariantCulture)}"
            + (body != null ? "&body=" + Uri.EscapeDataString(body) : ""), ct, track: true);

    public Task<BrainTurnDetail> TurnAsync(string brain, long turn, string? body, CancellationToken ct) =>
        api.GetJsonAsync<BrainTurnDetail>("brain_turn", $"api/brains/{Uri.EscapeDataString(brain)}/turn/{turn.ToString(CultureInfo.InvariantCulture)}"
            + (body != null ? "?body=" + Uri.EscapeDataString(body) : ""), ct);
}

/// <summary>
/// One displayed block: a message part of the context window or of a turn's detail. <see cref="Kind"/>: text | tool_use | tool_result |
/// thinking | redacted_thinking | reasoning | image | system | call (a call's header line).
/// </summary>
public sealed record ControllerBlockRow(string Kind, string Header, string Text, bool IsTarget = false, bool IsUser = false, string? Section = null)
{
    public bool IsThinking => Kind is "thinking" or "redacted_thinking" or "reasoning";
    public bool IsTool => Kind is "tool_use" or "tool_result";
    public bool IsMemory => Section == "memory";
    public bool IsJournal => Section == "journal";
    public bool IsCall => Kind == "call";
    public bool HasText => Text.Length > 0;

    public bool Matches(string q) => Header.Contains(q, StringComparison.OrdinalIgnoreCase) || Text.Contains(q, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A system prompt block (collapsed by default; the cached prefix is marked).</summary>
public sealed record ControllerSystemRow(string Header, string Text, bool InCachedPrefix, bool IsBreakpoint);

/// <summary>One turn of the list: collapsed to its summary; expanded = the calls in full (fetched on demand).</summary>
public sealed partial class ControllerTurnRow : ObservableObject
{
    public ControllerTurnRow(CtxTurnSummary turn, int depth) { Depth = depth; Update(turn); }

    public CtxTurnSummary Turn { get; private set; } = null!;
    public long Id => Turn.Id;
    public int Depth { get; }
    public Avalonia.Thickness Indent => new(Depth * 18, 0, 0, 0);
    public bool IsSubEntry => Depth > 0;
    public ObservableCollection<ControllerBlockRow> Rows { get; } = [];
    /// <summary>The detail as last fetched (copy as JSON).</summary>
    public BrainTurnDetail? Detail { get; set; }
    public long DetailVersion { get; set; } = -1;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _detailState = "";
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private string _lane = "";
    [ObservableProperty] private bool _inFlight;
    [ObservableProperty] private bool _targets;
    [ObservableProperty] private bool _hasThinking;
    [ObservableProperty] private bool _hasError;

    public void Update(CtxTurnSummary t)
    {
        Turn = t;
        Lane = t.Lane;
        Title = $"#{t.Id} {LaneLabel(t)} · {t.Preview}";
        InFlight = t.InFlight;
        Targets = t.Targets;
        HasThinking = t.HasThinking;
        HasError = t.Error != null;
        Meta = ControllerPanelViewModel.MetaLine(t);
    }

    private static string LaneLabel(CtxTurnSummary t) => t.Lane switch
    {
        "brain" => "brain turn",
        "worker" => "worker task",
        "author" => "author job (" + t.Purpose + ")",
        "memory" => "ask_memory",
        "reply" => t.Purpose.Replace('_', ' '),
        "compaction" => "compaction",
        "perception" => "perception",
        _ => t.Purpose,
    };
}

/// <summary>
/// The Controller panel of one body. Polls ≈ 1.4 Hz while open (<see cref="Interval"/>: the context page since the last version, so
/// an idle brain costs a few bytes), backs off on errors (0.5 s … 5 s, jittered), and shows "stale" when nothing arrived for
/// <see cref="StaleAfter"/>. Read-only.
/// </summary>
public sealed partial class ControllerPanelViewModel : ObservableObject, IDisposable
{
    private readonly IControllerSource _source;
    private readonly Func<IScriptDebugSource>? _scripts;
    private readonly Action<Action> _post;
    private readonly Random _jitter = new();
    private readonly Dictionary<long, CtxTurnSummary> _turns = new();
    private CancellationTokenSource? _cts;
    private DateTime _infoAt = DateTime.MinValue;
    private int _fails;
    private long _version;
    private List<ControllerBlockRow> _allMessages = [];
    private CtxInFlight? _inFlight;
    private volatile ControllerTurnRow[] _expanded = [];

    public ControllerPanelViewModel(IControllerSource source, string body, string title, Func<IScriptDebugSource>? scripts = null, Action<Action>? post = null)
    {
        _source = source;
        _scripts = scripts;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        Body = body;
        Title = title;
    }

    public string Body { get; }
    public string Title { get; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(700);
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan InfoEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>"Open the full hive view" of a subhive's owner (the Live Hive opens another panel).</summary>
    public event Action<string>? OpenOwnerRequested;

    public BodyControllerInfo? Info { get; private set; }
    public ObservableCollection<ControllerTurnRow> Turns { get; } = [];
    public ObservableCollection<ControllerSystemRow> System { get; } = [];
    public ObservableCollection<ControllerBlockRow> Messages { get; } = [];

    [ObservableProperty] private string _kindText = "connecting…";
    [ObservableProperty] private string _brainText = "";
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _staleText = "";
    [ObservableProperty] private bool _isStale;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrain), nameof(ShowScriptPane))]
    private bool _hasBrain;
    [ObservableProperty] private bool _hasScript;
    [ObservableProperty] private bool _showTabs;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrain), nameof(ShowScriptPane))]
    private int _selectedTab;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrain), nameof(ShowScriptPane))]
    private ScriptDebugViewModel? _script;
    /// <summary>The Brain pane is shown (a brain, and the Brain tab).</summary>
    public bool ShowBrain => HasBrain && SelectedTab == 0;
    /// <summary>The script debugger is shown (script-only, or the Script tab).</summary>
    public bool ShowScriptPane => Script != null && (SelectedTab == 1 || !HasBrain);
    [ObservableProperty] private string _thinkingText = "";
    [ObservableProperty] private bool _isThinking;
    [ObservableProperty] private string _systemHeader = "";
    [ObservableProperty] private string _windowHeader = "";
    [ObservableProperty] private bool _systemExpanded;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _searchNote = "";
    [ObservableProperty] private bool _onlyTargets;
    [ObservableProperty] private string? _filter;
    [ObservableProperty] private bool _hasFilter;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _hasOwner;
    // the token bar
    [ObservableProperty] private double _fill;
    [ObservableProperty] private double _cachedFill;
    [ObservableProperty] private double _compactMark;
    [ObservableProperty] private string _tokenText = "";
    [ObservableProperty] private bool _nearLimit;

    public DateTime LastOkAt { get; private set; } = DateTime.MinValue;
    public int Updates { get; private set; }
    public long Version => _version;
    public CtxWindow? Window { get; private set; }

    // ------------------------------------------------------------------ polling

    public bool Polling => _cts != null;

    public void Start()
    {
        if (_cts == null)
        {
            var cts = _cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(cts.Token));
        }
        if (SelectedTab == 1 || !HasBrain) Script?.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        Script?.Stop();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
                wait = Interval;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                int n = Interlocked.Increment(ref _fails);
                var retry = (ex as HiveApiException)?.RetryAfter;
                wait = retry ?? TimeSpan.FromSeconds(Math.Min(5, 0.5 * Math.Pow(2, n - 1)) * (0.75 + _jitter.NextDouble() * 0.5));
                var msg = ex.Message;
                _post(() => Failed(msg));
            }
            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One poll: the controller info (first, then every few seconds), the context page since our version, and the detail of
    /// expanded turns that changed. Tests call it directly.</summary>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        var info = Info;
        if (info == null || DateTime.UtcNow - _infoAt > InfoEvery)
        {
            info = await _source.ControllerAsync(Body, ct).ConfigureAwait(false);
            _infoAt = DateTime.UtcNow;
            var i = info;
            _post(() => ApplyInfo(i));
        }
        if (info.Brain is { } brain && info.Tabs.Contains("brain"))
        {
            var page = await _source.ContextAsync(brain, Volatile.Read(ref _version), info.Filter, ct).ConfigureAwait(false);
            _post(() => Apply(page, DateTime.UtcNow));
            // Expanded turns whose summary changed (an in-flight turn growing): refresh their detail, two per poll at most.
            var stale = _expanded.Where(t => t.DetailVersion != t.Turn.Version).Take(2).Select(t => (Row: t, Version: t.Turn.Version)).ToList();
            foreach (var (row, v) in stale)
            {
                var d = await _source.TurnAsync(brain, row.Id, info.Filter, ct).ConfigureAwait(false);
                _post(() => ApplyDetail(row, d, v));
            }
        }
        else _post(() => { LastOkAt = DateTime.UtcNow; Tick(LastOkAt); });
        Interlocked.Exchange(ref _fails, 0);
    }

    /// <summary>Expands (fetching its calls) or collapses a turn.</summary>
    public async Task ToggleAsync(ControllerTurnRow row, CancellationToken ct = default)
    {
        row.IsExpanded = !row.IsExpanded;
        _expanded = Turns.Where(t => t.IsExpanded).ToArray();
        if (!row.IsExpanded || Info?.Brain is not { } brain) return;
        row.DetailState = "loading…";
        try
        {
            long v = row.Turn.Version;
            var d = await _source.TurnAsync(brain, row.Id, Info.Filter, ct).ConfigureAwait(false);
            _post(() => ApplyDetail(row, d, v));
        }
        catch (Exception ex) { var m = ex.Message; _post(() => row.DetailState = m); }
    }

    /// <summary>The turn's full detail as indented JSON (the "Copy as JSON" button); fetched when not loaded yet.</summary>
    public async Task<string> TurnJsonAsync(ControllerTurnRow row, CancellationToken ct = default)
    {
        var d = row.Detail;
        if (d == null && Info?.Brain is { } brain) d = await _source.TurnAsync(brain, row.Id, Info.Filter, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(d ?? (object)row.Turn, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(HiveJson.Options) { WriteIndented = true };

    [RelayCommand]
    private void OpenOwner()
    {
        if (Info?.Links.Owner is { } owner) OpenOwnerRequested?.Invoke(owner);
    }

    [RelayCommand]
    private void ToggleSystem() => SystemExpanded = !SystemExpanded;

    // ------------------------------------------------------------------ applying (UI thread)

    public void Failed(string message)
    {
        ErrorText = message;
        Tick(DateTime.UtcNow);
    }

    public void ApplyInfo(BodyControllerInfo info)
    {
        Info = info;
        HasBrain = info.Brain != null && info.Tabs.Contains("brain");
        HasScript = info.Tabs.Contains("script");
        ShowTabs = HasBrain && HasScript;
        if (!HasBrain && HasScript) SelectedTab = 1;
        else if (!HasScript && SelectedTab == 1) SelectedTab = 0;
        KindText = info.Kind switch
        {
            "main" => "main hive · its own brain",
            "hive" => $"hive · its own brain" + (info.Owner is { } o && o != "main" ? $" · owned by {o}" : ""),
            "subhive" => $"subhive · driven by {(info.Owner ?? "main")}'s brain",
            "script" => $"script-only · runs {info.Script ?? "a script"}" + (info.Owner is { } so ? $" · owned by {so}" : ""),
            _ => info.Kind,
        };
        BrainText = info.Brain == null ? "no brain of its own" : string.Join(" · ", new[] { info.BrainKind, info.Model, info.Provider }.Where(s => !string.IsNullOrEmpty(s)));
        NoteText = info.Note ?? "";
        Filter = info.Filter;
        HasFilter = info.Filter != null;
        FilterText = info.Filter != null ? $"highlighting the turns and tool calls that targeted {info.Filter}" : "";
        if (info.Filter != null && Updates == 0) OnlyTargets = true;
        HasOwner = info.Kind == "subhive" && info.Links.Owner != null;
        if (HasScript && Script == null && _scripts != null) Script = new ScriptDebugViewModel(_scripts(), Body, Title, _post);
        else if (!HasScript && Script != null) { Script.Dispose(); Script = null; }
        if (Script != null && Polling && (SelectedTab == 1 || !HasBrain)) Script.Start();
    }

    partial void OnSelectedTabChanged(int value)
    {
        if (Script == null) return;
        if (value == 1 && Polling) Script.Start();
        else if (value != 1 && HasBrain) Script.Stop(); // a hidden debugger stops polling
    }

    public void Apply(BrainContextPage page, DateTime now)
    {
        LastOkAt = now;
        Updates++;
        ErrorText = "";
        if (page.Note is { Length: > 0 } n && !page.Enabled) NoteText = n;
        bool full = Volatile.Read(ref _version) == 0 || page.Note == "full" || page.Version < Volatile.Read(ref _version);
        if (full) _turns.Clear();
        foreach (var id in page.Evicted) _turns.Remove(id);
        foreach (var t in page.Turns) _turns[t.Id] = t;
        Volatile.Write(ref _version, page.Version);
        RebuildTurns();
        _inFlight = page.InFlight;
        if (page.Window is { } w) ApplyWindow(w);
        Tick(now);
    }

    public void ApplyDetail(ControllerTurnRow row, BrainTurnDetail detail, long version)
    {
        row.Detail = detail;
        row.DetailVersion = version;
        row.Rows.Clear();
        foreach (var call in detail.Calls)
        {
            row.Rows.Add(new ControllerBlockRow("call", CallHeader(call), call.Error ?? ""));
            foreach (var m in call.Added) foreach (var b in m.Blocks) row.Rows.Add(Row(m, b));
            foreach (var b in call.Response) row.Rows.Add(Row(null, b));
        }
        foreach (var c in detail.Children) row.Rows.Add(new ControllerBlockRow("call", $"↳ #{c.Id} {c.Lane} · {c.Preview}", MetaLine(c)));
        row.DetailState = detail.Calls.Count == 0 ? "no calls kept" : "";
    }

    /// <summary>Turn rows in order, each brain turn followed by its sub-entries (worker, author, memory...).</summary>
    private void RebuildTurns()
    {
        var all = _turns.Values.OrderByDescending(t => t.Id).ToList(); // newest first; sub-entries right under their parent
        var ids = all.Select(t => t.Id).ToHashSet();
        var ordered = new List<(CtxTurnSummary T, int Depth)>();
        var children = all.Where(t => t.Parent is { } p && ids.Contains(p) && t.Lane != "brain").ToLookup(t => t.Parent!.Value);
        void Add(CtxTurnSummary t, int depth)
        {
            ordered.Add((t, depth));
            foreach (var c in children[t.Id]) Add(c, depth + 1);
        }
        foreach (var t in all.Where(t => !(t.Parent is { } p && ids.Contains(p) && t.Lane != "brain"))) Add(t, 0);
        // in place: keep the rows (their expanded state and detail) of turns still there
        var byId = Turns.ToDictionary(r => r.Id);
        var rows = new List<ControllerTurnRow>(ordered.Count);
        foreach (var (t, depth) in ordered)
        {
            if (byId.TryGetValue(t.Id, out var row) && row.Depth == depth) row.Update(t);
            else row = new ControllerTurnRow(t, depth);
            rows.Add(row);
        }
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < Turns.Count) { if (!ReferenceEquals(Turns[i], rows[i])) Turns[i] = rows[i]; }
            else Turns.Add(rows[i]);
        }
        while (Turns.Count > rows.Count) Turns.RemoveAt(Turns.Count - 1);
        _expanded = Turns.Where(t => t.IsExpanded).ToArray();
        ApplyTurnFilter();
    }

    private void ApplyWindow(CtxWindow w)
    {
        Window = w;
        System.Clear();
        foreach (var s in w.System)
            System.Add(new ControllerSystemRow($"#{s.Index} · ~{K(s.Tokens)} tok" + (s.Cache ? $" · cache breakpoint ({s.Ttl ?? "5m"})" : "")
                + (s.InCachedPrefix ? " · cached prefix" : " · uncached"), s.Text, s.InCachedPrefix, s.Cache));
        long systemTokens = w.System.Sum(s => (long)s.Tokens);
        SystemHeader = $"System prompt · {w.System.Count} block{(w.System.Count == 1 ? "" : "s")} · ~{K(systemTokens)} tok · {w.ToolCount} tools ~{K(w.ToolTokens)} tok"
            + (w.CachedPrefixTokens > 0 ? $" · cached prefix ~{K(w.CachedPrefixTokens)} tok" : " · no cache breakpoint")
            + (w.Usage is { } u ? $" (this call: read {K(u.CacheRead)}, wrote {K(u.CacheWrite)})" : "");
        _allMessages = [];
        foreach (var m in w.Messages) foreach (var b in m.Blocks) _allMessages.Add(Row(m, b));
        WindowHeader = $"call #{w.CallSeq} · turn #{w.TurnId} · {w.Model} · {w.Messages.Count} messages" + (w.InFlight ? " · in flight" : "");
        // token bar: the real prompt size of the last call (the estimate while in flight) against the context limit
        long used = w.PromptTokens > 0 ? w.PromptTokens : w.EstimatedTokens;
        double limit = Math.Max(1, w.Limit);
        Fill = Math.Clamp(used / limit, 0, 1);
        CachedFill = Math.Clamp(w.CachedPrefixTokens / limit, 0, 1);
        CompactMark = w.CompactAt > 0 ? Math.Clamp(w.CompactAt / limit, 0, 1) : 0;
        NearLimit = Fill > 0.85;
        TokenText = $"{K(used)}{(w.PromptTokens > 0 ? "" : " (est.)")} / {K(w.Limit)} tok ({Fill:P0}) · cached prefix ~{K(w.CachedPrefixTokens)} · compacts at {K(w.CompactAt)} history";
        ApplySearch();
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();
    partial void OnOnlyTargetsChanged(bool value) => ApplyTurnFilter();

    private void ApplySearch()
    {
        var q = SearchText.Trim();
        var rows = q.Length == 0 ? _allMessages : _allMessages.Where(r => r.Matches(q)).ToList();
        Replace(Messages, rows);
        SearchNote = q.Length == 0 ? "" : $"{rows.Count} of {_allMessages.Count} blocks match";
        ApplyTurnFilter();
    }

    private void ApplyTurnFilter()
    {
        var q = SearchText.Trim();
        foreach (var r in Turns)
            r.IsVisible = (!OnlyTargets || Filter == null || r.Targets) && (q.Length == 0 || r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Rows.Any(x => x.Matches(q)));
    }

    /// <summary>Recomputes the stale badge and the "now thinking…" timer (UI thread; the Live Hive's HUD timer calls it).</summary>
    public void Tick(DateTime now)
    {
        double age = LastOkAt == DateTime.MinValue ? 0 : (now - LastOkAt).TotalSeconds;
        bool stale = LastOkAt == DateTime.MinValue ? ErrorText.Length > 0 : age > StaleAfter.TotalSeconds;
        IsStale = stale;
        StaleText = !stale ? "" : LastOkAt == DateTime.MinValue ? "no answer yet" + (ErrorText.Length > 0 ? " · " + ErrorText : "")
            : $"stale {age:0} s" + (ErrorText.Length > 0 ? " · " + ErrorText : "");
        if (_inFlight is { } f)
        {
            IsThinking = true;
            double s = Math.Max(0, (DateTimeOffset.UtcNow - f.Since).TotalSeconds);
            ThinkingText = $"now thinking… {s:0.0} s · {f.Lane} ({f.Purpose}) · {f.Model}" + (f.Body != null ? $" · for {f.Body}" : "");
        }
        else { IsThinking = false; ThinkingText = ""; }
        Script?.Tick(now);
    }

    // ------------------------------------------------------------------ formatting

    /// <summary>One block as a row: role/kind header (with the thinking label), its text.</summary>
    public static ControllerBlockRow Row(CtxMessage? m, CtxBlock b)
    {
        bool user = m?.Role == "user";
        string who = m == null ? "assistant" : m.Role;
        string head = b.Kind switch
        {
            "text" when b.Label is { } l => l == "journal" ? "compaction summary (journal)" : $"{who} · [{l}]" + (l == "memory" ? " · retrieved memory" : ""),
            "text" => m?.Tag == "turn" ? "user · turn input" : $"{who} · text",
            "tool_use" => $"→ tool call {b.Name}" + (b.Id != null ? $" ({b.Id})" : ""),
            "tool_result" => (b.IsError ? "← tool error" : "← tool result") + (b.Id != null ? $" ({b.Id})" : "") + (b.Label != null ? " " + b.Label : ""),
            "thinking" => "thinking · " + (b.Label ?? BrainContextCapture.ThinkingLabel) + (b.Bytes is > 0 ? $" · signed ({b.Bytes} bytes)" : ""),
            "redacted_thinking" => "thinking · " + (b.Label ?? BrainContextCapture.RedactedThinkingLabel) + (b.Bytes is { } n ? $" · {n} bytes" : ""),
            "reasoning" => "thinking · " + (b.Label ?? BrainContextCapture.ReasoningLabel),
            "image" => b.Label ?? "image",
            _ => b.Kind,
        };
        string text = b.Kind == "tool_use" ? Pretty(b.Input) : b.Text ?? (b.Kind == "redacted_thinking" ? "(encrypted: the API returns no readable text for this block)" : "");
        return new ControllerBlockRow(b.Kind, head, text, b.Target, user, b.Label);
    }

    private static string CallHeader(CtxCall c)
    {
        var sb = new StringBuilder();
        sb.Append($"call #{c.Seq} · round {c.Round} · {c.Tier} · {c.Model}");
        if (c.InFlight) sb.Append(" · in flight");
        else if (c.StopReason != null) sb.Append(" · ").Append(c.StopReason);
        if (c.Usage is { } u)
            sb.Append($" · in {K(u.In)} out {K(u.Out)} cache read {K(u.CacheRead)} write {K(u.CacheWrite)} · ${u.Cost:0.0000} · {Ms(u.LatencyMs)}");
        if (c.Effort != null) sb.Append(" · effort ").Append(c.Effort);
        if (c.Body != null) sb.Append(" · for ").Append(c.Body);
        sb.Append($" · +{c.Context} msgs of history");
        return sb.ToString();
    }

    public static string MetaLine(CtxTurnSummary t) =>
        $"{t.Tier} · {t.Model} · {t.Calls} call{(t.Calls == 1 ? "" : "s")}"
        + $" · in {K(t.In)} out {K(t.Out)} cache read {K(t.CacheRead)} write {K(t.CacheWrite)} · ${t.Cost:0.0000} · {Ms(t.LatencyMs)}"
        + (t.Tools.Count > 0 ? " · tools " + string.Join(", ", t.Tools) : "")
        + (t.HasThinking ? " · thinking" : "") + (t.Body != null ? " · for " + t.Body : "") + (t.Error != null ? " · " + t.Error : "")
        + (t.InFlight ? " · in flight" : "");

    private static string Pretty(string? json)
    {
        if (string.IsNullOrEmpty(json)) return "{}";
        try { using var d = JsonDocument.Parse(json); return JsonSerializer.Serialize(d.RootElement, new JsonSerializerOptions { WriteIndented = true }); }
        catch (JsonException) { return json; }
    }

    private static string K(long n) => n >= 10_000 ? (n / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k"
        : n >= 1000 ? (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);

    private static string Ms(long ms) => ms >= 1000 ? (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s" : ms + " ms";

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i < target.Count) { if (!EqualityComparer<T>.Default.Equals(target[i], items[i])) target[i] = items[i]; }
            else target.Add(items[i]);
        }
        while (target.Count > items.Count) target.RemoveAt(target.Count - 1);
    }

    public void Dispose()
    {
        Stop();
        Script?.Dispose();
    }
}
