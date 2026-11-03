using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Telemetry;
using MineClaw.Bot;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Supervisor;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Supervisor;

/// <summary>
/// The viewer routes' data in the main process, assembled from what <c>/api/status</c> (the telemetry sources), the controller
/// routes (brain context, in flight) and <c>/api/bodies/scripts</c> already read. A viewer line goes to main's router and, when
/// main's router isn't the target's, to every hive process over the <c>inject</c> broadcast (in-process and process hives alike).
/// </summary>
public sealed class LiveViewerData : IViewerData
{
    private static readonly Regex HeldIn = new(@"\bheld (?<held>.+?) (?:day|night)\b", RegexOptions.Compiled);

    private readonly BotHost _bot;
    private readonly string _main;
    private readonly IReadOnlyList<IBotTelemetrySource> _sources;
    private readonly Func<HiveSupervisor?> _hive;
    private readonly IControllerSupervisorData _controller;
    private readonly IScriptSupervisorData _scripts;
    private readonly ILogger? _log;
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime At, Task<BrainContextPage?> Page)> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime At, Task<ViewerNow?> Now)> _now = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Several viewers asking within this window share one answer (a full snapshot / context page is not free).</summary>
    public TimeSpan ShareWindow { get; init; } = TimeSpan.FromSeconds(1);

    public LiveViewerData(BotHost bot, string main, IReadOnlyList<IBotTelemetrySource> sources, Func<HiveSupervisor?> hive,
        IControllerSupervisorData controller, IScriptSupervisorData scripts, ILogger? log = null)
    {
        _bot = bot;
        _main = main;
        _sources = sources;
        _hive = hive;
        _controller = controller;
        _scripts = scripts;
        _log = log;
    }

    /// <summary>Every body this process controls (a hive's subhive bodies, dynamic hive accounts' sources); null = the fixed sources only.</summary>
    public HiveBodyList? Bodies { get; init; }

    private AgentRuntime Rt => _bot.Runtime;

    public IReadOnlyCollection<string> TrustedPlayers => Rt.Options.Self.TrustedPlayers;

    public string? Resolve(string bot)
    {
        var n = bot.Trim();
        if (n.Length == 0) return null;
        if (n.Equals("main", StringComparison.OrdinalIgnoreCase) || n.Equals(_main, StringComparison.OrdinalIgnoreCase)) return _main;
        if (_bot.Bodies?.Find(n) is { } b) return b.IsMain ? _main : b.Name;
        if (_hive()?.Resolve(n) is { } clone) return clone;
        return _sources.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Name
            ?? Bodies?.Find(n)?.Name; // a hive's own subhive body (its hive's router takes the line)
    }

    public async Task<bool> SendAsync(RemoteChat chat, CancellationToken ct)
    {
        if (Rt.Chat.OnRemoteChat(chat)) return true; // main or a body main's brain drives
        if (_hive() is not { } sup) return false;
        // Every hive process gets the same line, as with /as; exactly the target's router takes it (ChatRouter.OnRemoteChat).
        await sup.InjectAsync(new InjectMsg(chat.Text, chat.Origin, chat.Player, chat.Target, chat.AllowCommands, chat.RequestId)).ConfigureAwait(false);
        return true;
    }

    public IReadOnlyList<string> Brains()
    {
        var list = new List<string> { _main };
        if (_hive() is { } sup) list.AddRange(sup.Slots.Where(s => s.Connected).Select(s => s.Name));
        return list;
    }

    public Task<BrainContextPage?> ContextAsync(string brain, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_pages.TryGetValue(brain, out var hit) && now - hit.At < ShareWindow) return hit.Page;
        var task = SafeContextAsync(brain, 0, ct);
        _pages[brain] = (now, task);
        return task;
    }

    private async Task<BrainContextPage?> SafeContextAsync(string brain, long since, CancellationToken ct)
    {
        try { return await _controller.ContextAsync(IsMain(brain) ? "main" : brain, since, null, ct).ConfigureAwait(false); }
        catch (Exception ex) { _log?.LogDebug(ex, "viewer: context of {Brain} unavailable", brain); return null; }
    }

    public IReadOnlyDictionary<long, MemoryDocInfo> Docs(IReadOnlyCollection<long> ids)
    {
        var map = new Dictionary<long, MemoryDocInfo>();
        if (ids.Count == 0) return map;
        try
        {
            foreach (var chunk in ids.Distinct().Chunk(200))
            {
                var args = chunk.Select((id, i) => ("$i" + i, (object?)id)).ToArray();
                var sql = $"SELECT id, kind, raptor_idx FROM docs WHERE id IN ({string.Join(",", args.Select(a => a.Item1))})";
                foreach (var row in Rt.Db.Query(sql, r => (Id: r.GetInt64(0), Kind: r.GetString(1), Node: r.IsDBNull(2) ? (int?)null : r.GetInt32(2)), args))
                    map[row.Id] = new MemoryDocInfo(row.Kind, row.Node);
            }
        }
        catch (Exception ex) { _log?.LogDebug(ex, "viewer: doc lookup failed"); }
        return map;
    }

    public Task<ViewerNow?> NowAsync(string bot, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_now.TryGetValue(bot, out var hit) && now - hit.At < ShareWindow) return hit.Now;
        var task = BuildNowAsync(bot, ct);
        _now[bot] = (now, task);
        return task;
    }

    private async Task<ViewerNow?> BuildNowAsync(string bot, CancellationToken ct)
    {
        try
        {
            var source = Bodies?.SourceFor(bot, _sources) ?? _sources.FirstOrDefault(s => s.Name.Equals(bot, StringComparison.OrdinalIgnoreCase));
            var snap = source?.Snapshot(full: true);
            var info = await _controller.ControllerAsync(IsMain(bot) ? "main" : bot, ct).ConfigureAwait(false);
            // A hive's subhive body has no telemetry source and isn't a slot of main's: what its hive streams about it.
            var br = Bodies?.Find(bot);
            if (snap == null && info == null && br == null) return null;

            ViewerInFlight? inFlight = null;
            if (info?.Brain is { } brain)
            {
                // Pass the last version back: an unchanged capture answers with no turns, only the in-flight line.
                var page = await SafeContextAsync(brain, _versions.GetValueOrDefault(brain), ct).ConfigureAwait(false);
                if (page != null)
                {
                    _versions[brain] = page.Version;
                    if (page.InFlight is { } f) inFlight = new ViewerInFlight(f.Purpose, f.Model, f.Since);
                }
            }
            var t = snap?.Thought;
            var thought = t != null && (t.Output.Length > 0 || t.Thinking.Length > 0) ? new ViewerThought(t.Output, t.Thinking, t.UpdatedAt) : null;
            var tool = t?.Tools.Count > 0 ? t.Tools[^1] : null;
            var shortName = Regex.Match(bot, @"(\d+)$") is { Success: true } m ? m.Groups[1].Value : bot;
            var row = _scripts.Running().FirstOrDefault(s => s.Body.Equals(bot, StringComparison.OrdinalIgnoreCase)
                || (IsMain(bot) ? s.Short == "main" : s.Short.Equals(shortName, StringComparison.OrdinalIgnoreCase)));
            ViewerBody? body = snap is { Status: not BotStatus.Offline }
                ? new ViewerBody(snap.Hp, snap.Food, snap.Pos is { } p ? string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(p.X):0},{Math.Floor(p.Y):0},{Math.Floor(p.Z):0}") : null,
                    snap.Dimension, snap.Header is { } h && HeldIn.Match(h) is { Success: true } hm ? hm.Groups["held"].Value : null)
                : br is { Online: true, HasPos: true }
                    ? new ViewerBody(br.Hp, br.Food, string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(br.X):0},{Math.Floor(br.Y):0},{Math.Floor(br.Z):0}"), br.Dim, null)
                    : null;
            var script = row != null ? new ViewerScript(row.Script, row.Version, row.Status, row.Line, row.Paused)
                : br?.Script is { } bs ? new ViewerScript(bs.Name, bs.Version, bs.Status, bs.Line, bs.Paused) : null;
            bool hiveBody = snap == null && br is { IsMain: false }; // no stream of its own: its brain is its owner's
            return new ViewerNow(bot, DateTimeOffset.UtcNow, snap?.Brain ?? br?.Brain, hiveBody ? null : inFlight, thought,
                tool != null ? new ViewerTool(tool.Name, tool.Args, tool.At) : null, snap?.Task ?? br?.Job, script, body, br?.Kind, br?.Owner);
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "viewer: now for {Bot} failed", bot);
            return null;
        }
    }

    private bool IsMain(string name) => name.Equals("main", StringComparison.OrdinalIgnoreCase) || name.Equals(_main, StringComparison.OrdinalIgnoreCase);
}
