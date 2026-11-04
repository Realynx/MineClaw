using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MineClaw.Agent.Telemetry;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Supervisor;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Supervisor;

/// <summary>
/// The controller routes' data in the main process. Main's brain (and the in-process bodies it drives: subhives) read this
/// process's <see cref="BrainContextCapture"/> directly; a hive clone's brain answers over the <c>brain_ctx</c> IPC request (1.5 s
/// timeout). The kind and owner of a clone come from the hive's ownership tree (<see cref="HiveTree"/>), else from what the clone
/// is doing (a brain asleep on a script = a hive running a script). Reads only.
/// </summary>
public sealed class SupervisorControllerData : IControllerSupervisorData
{
    private readonly BotHost _bot;
    private readonly string _main;
    private readonly BrainContextCapture? _capture;
    private readonly Func<HiveSupervisor?> _hive;
    private readonly Func<HiveTree?> _tree;
    private readonly ILogger? _log;
    private readonly ConcurrentDictionary<string, (DateTime At, Task<BrainContextReply?> Reply)> _recent = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan CloneTimeout { get; init; } = TimeSpan.FromSeconds(1.5);
    /// <summary>The same clone query from several viewers within this window costs one IPC request.</summary>
    public TimeSpan ShareWindow { get; init; } = TimeSpan.FromMilliseconds(250);

    public SupervisorControllerData(BotHost bot, string main, BrainContextCapture? capture, Func<HiveSupervisor?> hive, Func<HiveTree?>? tree = null,
        ILogger? log = null)
    {
        _bot = bot;
        _main = main;
        _capture = capture;
        _hive = hive;
        _tree = tree ?? (() => null);
        _log = log;
    }

    private bool IsMain(string name) => name.Equals("main", StringComparison.OrdinalIgnoreCase) || name.Equals(_main, StringComparison.OrdinalIgnoreCase);

    private static string Short(string name) => Regex.Match(name, @"_(\d+)$") is { Success: true } m ? m.Groups[1].Value : name;

    private static string Enc(string s) => Uri.EscapeDataString(s);

    // ------------------------------------------------------------------ what drives a body

    public Task<BodyControllerInfo?> ControllerAsync(string body, CancellationToken ct)
    {
        body = body.Trim();
        var opts = _bot.Runtime.Options.Llm;
        if (IsMain(body)) return Task.FromResult<BodyControllerInfo?>(MainController(opts));
        if (_bot.Bodies?.Find(body) is { } b)
        {
            if (b.IsMain) return ControllerAsync("main", ct);
            // An in-process body is driven by main's brain (a subhive): the brain's turns, filtered to this body.
            var s = b.Scripts?.Current;
            return Task.FromResult<BodyControllerInfo?>(new BodyControllerInfo(b.Short, "subhive", "main", "main", "opus", opts.MainModel, opts.MainProvider,
                s != null, s?.Name, s != null ? ["brain", "script"] : ["brain"], b.Short,
                new ControllerLinks("api/brains/main/context", $"api/bodies/{Enc(b.Short)}/script", "main")));
        }
        if (_hive() is { } sup && sup.Resolve(body) is { } clone) return Task.FromResult<BodyControllerInfo?>(CloneController(sup, clone));
        return Task.FromResult<BodyControllerInfo?>(null);
    }

    private BodyControllerInfo MainController(MineClaw.Agent.Llm.LlmOptions opts)
    {
        var s = ScriptTools.Of(_bot.Runtime)?.Controller?.Current;
        return new BodyControllerInfo("main", "main", null, "main", "opus", opts.MainModel, opts.MainProvider,
            s != null, s?.Name, s != null ? ["brain", "script"] : ["brain"], null, new ControllerLinks("api/brains/main/context", "api/bodies/main/script", null),
            _capture == null ? "no context capture in this process (Telemetry:Enabled is off)" : null);
    }

    /// <summary>The script a clone runs: from its brain state ("asleep (script cobble_loop …)"), else the one its node was given.</summary>
    private static string? ScriptOf(CloneSlot? slot, HiveNode? node) =>
        slot?.Snapshot?.Brain is { } brain && BrainScript.Match(brain) is { Success: true } bm ? bm.Groups["name"].Value : node?.Script;

    /// <summary>A clone: a script runner, a subhive body (driven by its owner's brain: main's capture, or the owning hive's over IPC),
    /// or a hive with its own brain.</summary>
    private BodyControllerInfo CloneController(HiveSupervisor sup, string clone)
    {
        var slot = sup.Slots.FirstOrDefault(x => x.Name.Equals(clone, StringComparison.OrdinalIgnoreCase));
        var node = _tree()?.Get(clone);
        string? script = ScriptOf(slot, node);
        bool running = script != null && slot is { Connected: true };
        string? owner = node?.Owner ?? "main";
        string shortName = Short(clone);
        string? note = slot is not { Connected: true } ? "clone offline" : null;
        List<string> drivers = running ? ["brain", "script"] : ["brain"];
        switch (node?.Kind)
        {
            case CloneKind.Script:
                return new BodyControllerInfo(shortName, "script", owner, null, null, null, null, true, script, ["script"], null,
                    new ControllerLinks(null, $"api/bodies/{Enc(shortName)}/script", owner), note);
            case CloneKind.Subhive:
                string brainId = owner is null or "main" ? "main" : owner;
                return new BodyControllerInfo(shortName, "subhive", owner, brainId, null, null, null, running, script,
                    drivers, shortName, new ControllerLinks($"api/brains/{Enc(brainId)}/context", $"api/bodies/{Enc(shortName)}/script", owner), note);
            default:
                string? brainKind = node?.Brain is { } bk ? HiveKinds.Name(bk) : slot?.Task?.Profile;
                return new BodyControllerInfo(shortName, "hive", owner, clone, brainKind, null, null, running, script,
                    drivers, null, new ControllerLinks($"api/brains/{Enc(clone)}/context", $"api/bodies/{Enc(shortName)}/script", owner), note);
        }
    }

    private static readonly Regex BrainScript = new(@"\(script (?<name>[a-z0-9_]+)", RegexOptions.Compiled);

    // ------------------------------------------------------------------ brain context

    public async Task<BrainContextPage?> ContextAsync(string brain, long since, string? body, CancellationToken ct)
    {
        if (IsMain(brain)) return _capture?.Page(since, body);
        var reply = await CloneAsync(brain, new BrainContextQuery(since, null, body)).ConfigureAwait(false);
        return reply?.Page;
    }

    public async Task<(bool Found, BrainTurnDetail? Turn)> TurnAsync(string brain, long turn, string? body, CancellationToken ct)
    {
        if (IsMain(brain)) return _capture == null ? (false, null) : (true, _capture.Turn(turn, body));
        var reply = await CloneAsync(brain, new BrainContextQuery(0, turn, body)).ConfigureAwait(false);
        return reply == null ? (false, null) : (true, reply.Turn);
    }

    private Task<BrainContextReply?> CloneAsync(string brain, BrainContextQuery q)
    {
        if (_hive() is not { } sup || sup.Resolve(brain) is not { } clone) return Task.FromResult<BrainContextReply?>(null);
        var key = $"{clone}|{q.Since}|{q.Turn}|{q.Body}";
        var now = DateTime.UtcNow;
        if (_recent.TryGetValue(key, out var hit) && now - hit.At < ShareWindow) return hit.Reply;
        var t = FetchAsync(sup, clone, q);
        _recent[key] = (now, t);
        if (_recent.Count > 64) foreach (var k in _recent.Where(kv => now - kv.Value.At > TimeSpan.FromSeconds(10)).Select(kv => kv.Key).ToList()) _recent.TryRemove(k, out _);
        return t;
    }

    private async Task<BrainContextReply?> FetchAsync(HiveSupervisor sup, string clone, BrainContextQuery q)
    {
        try { return await sup.RequestBrainContextAsync(clone, q, CloneTimeout).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogDebug(ex, "supervisor controller: clone {Clone} brain context failed", clone);
            return null;
        }
    }
}
