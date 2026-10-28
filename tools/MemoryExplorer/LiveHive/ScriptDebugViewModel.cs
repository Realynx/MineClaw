using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MineClaw.MemoryExplorer.ViewModels;
using MineClaw.Scripting;

namespace MineClaw.MemoryExplorer.LiveHive;

// Wire shapes of the Supervisor's script routes (src/MineClaw.App/Supervisor/SupervisorScriptRoutes.cs); the snapshot itself is
// the Scripting layer's ScriptDebugSnapshot.

/// <summary><c>/api/bodies/{name}/script</c>: whether the body runs a script, and the debugger snapshot (the last run's if not).</summary>
public sealed record BodyScriptDto(string Body, bool Running, ScriptDebugSnapshot? Snapshot, string? Note = null);

/// <summary>One body of <c>/api/bodies/scripts</c> (the tiles' badge feed).</summary>
public sealed record LiveScriptRowDto(string Body, string Short, string Script, string Status, int Line = 0, bool Paused = false, int? Version = null,
    string? Role = null);

public sealed record LiveScriptsDto(DateTimeOffset At, List<LiveScriptRowDto> Bodies);

/// <summary>Where the debugger panel reads its snapshots (the Supervisor; tests use a fake).</summary>
public interface IScriptDebugSource
{
    Task<BodyScriptDto> GetAsync(string body, bool code, CancellationToken ct);
}

/// <summary>The live source: one GET per poll through the Live Hive's <see cref="HiveApi"/> (its timeouts and per-route back-off).</summary>
public sealed class HiveScriptDebugSource(HiveApi api) : IScriptDebugSource
{
    public Task<BodyScriptDto> GetAsync(string body, bool code, CancellationToken ct) =>
        api.GetJsonAsync<BodyScriptDto>("script", $"api/bodies/{Uri.EscapeDataString(body)}/script{(code ? "?code=1" : "")}", ct, track: true);
}

public sealed record FrameRow(string Function, string Where, bool IsScript, bool IsHost);
public sealed record ValueRow(string Name, string Value, string Note = "");
public sealed record StepRow(string Text, string When);
public sealed record TimingRow(string Action, string Calls, string Total, string Avg, bool HasFails);

/// <summary>
/// The live script debugger panel of one body (opened from a tile's "script" badge): the script's source with the current
/// line highlighted while it steps, the call stack, the state machine (state, history, blackboard), params with their values,
/// the locals at the current frame, the checkpoint, the pause state and reason, the action in flight with timings, crew lanes
/// and the last log lines. Polls ≈ 4 Hz while open (<see cref="Interval"/>), backs off on errors (0.5 s … 5 s, jittered), and
/// shows "stale" when no fresh snapshot arrived for <see cref="StaleAfter"/>. Read-only.
/// TODO(debugger pause/resume): a Pause/Resume button posting to the admin-write route (Supervisor:AllowAdminWrites + token
/// header) once it exists; <see cref="ScriptDebugSnapshot.Controls"/> says when the server offers it. Stepping and param edits
/// stay out until then.
/// </summary>
public sealed partial class ScriptDebugViewModel : ObservableObject, IDisposable
{
    private readonly IScriptDebugSource _source;
    private readonly Action<Action> _post;
    private readonly Random _jitter = new();
    private CancellationTokenSource? _cts;
    private int _codeSession = -1;
    private string? _codeKey;
    private int _fails;

    public ScriptDebugViewModel(IScriptDebugSource source, string body, string title, Action<Action>? post = null)
    {
        _source = source;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        Body = body;
        Title = title;
    }

    public string Body { get; }
    public string Title { get; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Pause/resume from the panel (not in this version: the server offers no control yet).</summary>
    public bool CanControl => Controls.Count > 0;
    public IReadOnlyList<string> Controls { get; private set; } = [];

    public ObservableCollection<CodeLineRow> Lines { get; } = [];
    public ObservableCollection<FrameRow> Stack { get; } = [];
    public ObservableCollection<StepRow> History { get; } = [];
    public ObservableCollection<ValueRow> Blackboard { get; } = [];
    public ObservableCollection<ValueRow> Params { get; } = [];
    public ObservableCollection<ValueRow> Locals { get; } = [];
    public ObservableCollection<TimingRow> Timings { get; } = [];
    public ObservableCollection<ValueRow> Lanes { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    [ObservableProperty] private string _scriptName = "";
    [ObservableProperty] private string _statusText = "connecting…";
    [ObservableProperty] private string _statusKind = "idle";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _pauseText = "";
    [ObservableProperty] private int _currentLine;
    [ObservableProperty] private int _currentIndex = -1;
    [ObservableProperty] private bool _lineLive;
    [ObservableProperty] private string _whereText = "";
    [ObservableProperty] private string _actionText = "";
    [ObservableProperty] private string _actionsText = "";
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _checkpointText = "";
    [ObservableProperty] private string _machineText = "";
    [ObservableProperty] private bool _hasMachine;
    [ObservableProperty] private string _localsNote = "";
    [ObservableProperty] private string _paramsNote = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _failureText = "";
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _hasLanes;
    [ObservableProperty] private string _staleText = "";
    [ObservableProperty] private bool _isStale;

    /// <summary>When the last snapshot arrived (UTC; MinValue = never).</summary>
    public DateTime LastOkAt { get; private set; } = DateTime.MinValue;
    /// <summary>Snapshots applied (tests, the HUD).</summary>
    public int Updates { get; private set; }
    /// <summary>The last snapshot (UI thread).</summary>
    public ScriptDebugSnapshot? Snapshot { get; private set; }

    // ------------------------------------------------------------------ polling

    public bool Polling => _cts != null;

    public void Start()
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
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

    /// <summary>One request + apply (the loop's step; tests call it directly).</summary>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        bool code = Volatile.Read(ref _codeSession) < 0;
        var reply = await _source.GetAsync(Body, code, ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _fails, 0);
        _post(() => Apply(reply, DateTime.UtcNow));
    }

    // ------------------------------------------------------------------ applying a snapshot (UI thread)

    public void Failed(string message)
    {
        ErrorText = message;
        Tick(DateTime.UtcNow);
    }

    public void Apply(BodyScriptDto reply, DateTime now)
    {
        LastOkAt = now;
        Updates++;
        ErrorText = "";
        NoteText = reply.Note ?? "";
        var s = reply.Snapshot;
        Snapshot = s;
        if (s == null)
        {
            ScriptName = "";
            StatusText = reply.Running ? "running" : "no script running";
            StatusKind = "idle";
            IsRunning = IsPaused = false;
            Tick(now);
            return;
        }
        // The source comes once per run (and again when a different run or version shows up).
        var key = $"{s.SessionId}:{s.Name}:{s.Version}";
        if (s.Code != null && key != _codeKey)
        {
            _codeKey = key;
            Volatile.Write(ref _codeSession, s.SessionId);
            Lines.Clear();
            foreach (var l in CodeLineRow.Build(s.Code, s.Lang == "lua")) Lines.Add(l);
            CurrentIndex = -1;
        }
        else if (s.Code == null && key != _codeKey) Volatile.Write(ref _codeSession, -1); // ask for the new run's code next time

        ScriptName = s.Name + (s.Version is { } v ? $" v{v}" : "") + (s.Role is { } role ? $" · lane {role}" : "");
        IsRunning = s.Live;
        IsPaused = s.Paused;
        StatusKind = s.Paused ? "paused" : s.Live ? "running" : s.Status;
        StatusText = (s.Live ? (s.Paused ? "paused" : s.PausePending ? "pausing at the next safe point" : "running") : s.Status)
            + (s.Line > 0 ? $" · line {s.Line}" : "");
        PauseText = s.Paused ? $"paused ({s.PauseKind}): {s.PauseReason}" + (s.PausedSeconds is { } ps ? $" · {Dur(ps)}" : "") : "";
        LineLive = s.LineLive;
        SetCurrent(s.Line);
        WhereText = s.LineLive ? $"line {s.Line}:{s.Column}" + (s.Snippet is { } sn ? $"  `{sn}`" : "")
            : s.Line > 0 ? $"line {s.Line} (last host call)" + (s.InSdk is { } sdk ? $" · now in {sdk}" : "") : "";

        Replace(Stack, s.Stack.Select(f => new FrameRow(f.Function, $"{f.File}:{f.Line}", f.Script, IsHostFrame(f, s))).ToList());
        ActionText = ActionLine(s);
        ActionsText = $"{s.Actions} action{(s.Actions == 1 ? "" : "s")}";
        ElapsedText = Dur(s.ElapsedSeconds);
        Replace(Timings, s.Timings.Select(t => new TimingRow(t.Action, $"{t.Calls}x" + (t.Fails > 0 ? $" ({t.Fails} failed)" : ""), $"{t.Seconds:0.0} s",
            $"{t.AvgMs:0} ms", t.Fails > 0)).ToList());

        CheckpointText = s.Checkpoint is { } cp ? $"cp '{cp}'" + (s.CheckpointTotal > 0 ? $" ({s.CheckpointsPassed}/{s.CheckpointTotal} passed)" : "")
            : s.CheckpointTotal > 0 ? $"no checkpoint yet (0/{s.CheckpointTotal})" : "no checkpoints";
        if (s.Machine is { } m)
        {
            HasMachine = true;
            MachineText = $"{(m.Id == "m" ? "" : m.Id + ": ")}{m.State}  ·  {m.Transitions} transition{(m.Transitions == 1 ? "" : "s")}"
                + (m.BlackboardAgeSeconds is > 1 ? $"  ·  blackboard {m.BlackboardAgeSeconds:0} s old" : "");
            Replace(History, m.History.AsEnumerable().Reverse().Select(h => new StepRow($"{h.From} → {h.To}  ({h.Why})", $"{h.At:0.0} s")).ToList());
            Replace(Blackboard, Rows(m.Blackboard));
        }
        else
        {
            HasMachine = false;
            MachineText = "no state machine";
            History.Clear();
            Blackboard.Clear();
        }
        Replace(Params, Rows(s.Params));
        ParamsNote = s.Params == null ? "this script declares no params()" : "";
        if (s.Args is JsonObject { Count: > 0 } args)
            foreach (var row in Rows(args).Where(r => Params.All(p => p.Name != r.Name))) Params.Add(row with { Note = "arg" });
        Replace(Locals, s.Locals?.Select(l => new ValueRow(l.Name, l.Value, l.Scope)).ToList() ?? []);
        LocalsNote = s.LocalsNote ?? (s.LocalsAgeSeconds is > 1 ? $"sampled {s.LocalsAgeSeconds:0} s ago" : "");
        Replace(Lanes, s.Lanes.Select(l => new ValueRow($"lane {l.Id} · {l.Body}", l.Status)).ToList());
        HasLanes = Lanes.Count > 0 || s.Crew != null;
        ProgressText = s.ProgressPct is { } pct ? $"{pct:0}%" + (s.ProgressNote is { } pn ? " " + pn : "") : s.ProgressNote ?? "";
        FailureText = s.Live ? (s.LastFailure is { } lf ? "last failure: " + lf : "") : s.Outcome ?? "";
        Replace(Logs, s.Logs.ToList());
        Controls = s.Controls;
        OnPropertyChanged(nameof(CanControl));
        Tick(now);
    }

    /// <summary>Recomputes the stale badge (UI thread; also called by the Live Hive's 2 Hz HUD timer).</summary>
    public void Tick(DateTime now)
    {
        double age = LastOkAt == DateTime.MinValue ? 0 : (now - LastOkAt).TotalSeconds;
        bool stale = LastOkAt == DateTime.MinValue ? ErrorText.Length > 0 : age > StaleAfter.TotalSeconds;
        IsStale = stale;
        StaleText = !stale ? "" : LastOkAt == DateTime.MinValue ? "no snapshot yet" : $"stale {age:0} s" + (ErrorText.Length > 0 ? " · " + ErrorText : "");
    }

    private void SetCurrent(int line)
    {
        if (line == CurrentLine && CurrentIndex == line - 1) return;
        if (CurrentLine > 0 && CurrentLine <= Lines.Count) Lines[CurrentLine - 1].IsCurrent = false;
        CurrentLine = line;
        if (line > 0 && line <= Lines.Count) { Lines[line - 1].IsCurrent = true; CurrentIndex = line - 1; }
        else CurrentIndex = -1;
    }

    /// <summary>The host function frame (innermost; its location is the caller's call site): "go" for goto.</summary>
    private static bool IsHostFrame(ScriptFrame f, ScriptDebugSnapshot s) => s.Stack.Count > 1 && ReferenceEquals(f, s.Stack[0]) && s.Action is { Running: true };

    /// <summary>"mineGen → dig 5,64,7 · 1.2 s" (the SDK helper the script called, then the host action in flight).</summary>
    public static string ActionLine(ScriptDebugSnapshot s)
    {
        if (s.Action is not { } a) return s.Live ? "between actions" : "";
        string? helper = null;
        int firstScript = -1;
        for (int i = 0; i < s.Stack.Count; i++) if (s.Stack[i].Script) { firstScript = i; break; }
        if (firstScript > 1 && !s.Stack[firstScript - 1].Script) helper = s.Stack[firstScript - 1].Function;
        var head = (helper != null ? helper + " → " : "") + a.Label + (a.Args is { Length: > 0 } args ? " " + args : "");
        return head + (a.Running ? $" · {Dur(a.Seconds)}" + (a.Atomic ? " · atomic" : "") : $" · done in {Dur(a.Seconds)}" + (a.Ok == false ? " (failed)" : ""));
    }

    private static List<ValueRow> Rows(JsonNode? node)
    {
        if (node is not JsonObject o) return node == null ? [] : [new ValueRow("(value)", Short(node.ToJsonString()))];
        return o.Select(kv => new ValueRow(kv.Key, Short(kv.Value?.ToJsonString() ?? "null"))).ToList();
    }

    private static string Short(string s) => s.Length <= 120 ? s : s[..117] + "...";

    private static string Dur(double seconds) =>
        seconds >= 3600 ? $"{(int)(seconds / 3600)}h{(int)(seconds % 3600 / 60):00}m"
        : seconds >= 60 ? $"{(int)(seconds / 60)}m{(int)(seconds % 60):00}s"
        : seconds.ToString(seconds < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";

    /// <summary>Updates a list in place (no flicker when nothing changed).</summary>
    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i < target.Count) { if (!EqualityComparer<T>.Default.Equals(target[i], items[i])) target[i] = items[i]; }
            else target.Add(items[i]);
        }
        while (target.Count > items.Count) target.RemoveAt(target.Count - 1);
    }

    public void Dispose() => Stop();
}
