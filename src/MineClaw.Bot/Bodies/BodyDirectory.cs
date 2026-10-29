using MineClaw.Agent;
using MineClaw.Agent.Bodies;
using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using MineClaw.Agent.Worker;
using MineClaw.Bot.Game;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Bodies;

/// <summary>
/// One brain, many bodies, step 3 (docs/design/one-brain-many-bodies.md section 2): the brain's view of its bodies over the
/// <see cref="BodyRegistry"/>. <see cref="Enable"/> (in-process mode only) turns on, for the one brain:
/// <list type="bullet">
/// <item><c>body</c> on the brain's tool calls and the turn's focus body (<see cref="BotAgent.Bodies"/>);</item>
/// <item>the [bodies] line (<see cref="BodyStatusBlock"/>, ≤120 tokens, uncached tail);</item>
/// <item>the wake digest for clone events (<see cref="BodyEventDigest"/>, the queue's intake);</item>
/// <item>chat naming a body is addressed to the brain (<see cref="BodyAddressing"/>), main's brainstem and lane skip lines
///   for other bodies;</item>
/// <item>the worker pool: one worker per busy body up to <c>Agent:Worker:MaxConcurrent</c>.</item>
/// </list>
/// Hive mode never calls it: every path stays as it was.
/// </summary>
public sealed class BodyDirectory : IBodyDirectory
{
    private readonly BrainContext _brain;
    private readonly BodyRegistry _reg;
    private readonly ILogger? _log;
    private BodyAddressing? _addressing;
    private string _addressingKey = "";

    private BodyDirectory(BrainContext brain, ILogger? log)
    {
        _brain = brain;
        _reg = brain.Bodies;
        _log = log;
        Digest = new BodyEventDigest(() => brain.Runtime.Agent.TurnFocus ?? StandingFocus, b => Activities(b)?.IsBusy == true, brain.Time);
    }

    /// <summary>The wake digest for clone events (the queue's intake stage).</summary>
    public BodyEventDigest Digest { get; }

    /// <summary>Idle bodies: one wake per idle stretch with why, and the collapsed "idle N" part of [bodies].</summary>
    public BodyIdleWatch? IdleWatch { get; private set; }

    /// <summary>The per-body workers (null without a worker tier).</summary>
    public WorkerPool? Workers { get; private set; }

    /// <summary>
    /// "any": the fittest body among the candidates for a job text, or (null, why). Set by the host (the hive's fitness score
    /// and failure memory); null = the first idle candidate.
    /// </summary>
    public Func<string, IReadOnlyList<IBody>, (string? Pick, string? Why)>? Picker { get; set; }

    /// <summary>Turns the multi-body brain interface on (in-process bodies). Idempotent.</summary>
    public static BodyDirectory Enable(BrainContext brain, ILogger? log = null)
    {
        if (brain.Directory is { } existing) return existing;
        var rt = brain.Runtime;
        var dir = new BodyDirectory(brain, log);
        brain.Directory = dir;
        rt.Agent.Bodies = dir;
        dir.IdleWatch = new BodyIdleWatch(brain.Bodies, rt.Events, brain.Time ?? TimeProvider.System);
        if (brain.Persist) dir.IdleWatch.Start(TimeSpan.FromSeconds(5));

        // Clone events: N bodies never wake the brain N times (the intake runs before the script gates).
        var intake = rt.Events.Intake;
        rt.Events.Intake = intake == null ? dir.Digest.Apply : e => intake(e) is { } kept ? dir.Digest.Apply(kept) : null;

        // Chat naming a body is for the one brain ("67, come here" was nobody's line without the hive's arbiter).
        var arbiter = rt.Chat.Arbiter;
        rt.Chat.Arbiter = (m, local) => (arbiter?.Invoke(m, local) ?? local) || dir.NamesAClone(m.Text, m.Kind == Agent.Chat.ChatKind.Emote);
        var named = rt.Chat.ExplicitlyAddressed;
        rt.Chat.ExplicitlyAddressed = m => named?.Invoke(m) == true || dir.NamesAClone(m.Text, m.Kind == Agent.Chat.ChatKind.Emote);

        // The focus body's [here] (a turn about 67 reads what 67 would look up first).
        var here = rt.Agent.HereBlock;
        rt.Agent.HereBlock = batch => rt.Agent.TurnFocus is { } f && dir.Find(f) is Body { Here: { } other } ? other.Build(batch) : here?.Invoke(batch);

        // Main's reflexes and main's lane skip lines for other bodies ("67 stop" never stops main).
        if (brain.Main.Brainstem is { } stem) stem.Hears = dir.MainHears;
        if (brain.Main.ScriptBridge is { } lane) lane.Addressed = text => dir.Addressed(text);

        // One worker per busy body, up to the cap, all on the shared cached worker prefix.
        if (rt.Worker is { } worker)
        {
            var pool = new WorkerPool(worker, rt.Options.Worker.MaxConcurrent,
                () => BodyScope.Current is { IsMain: false } b ? b.Short : null, dir.ConfigureWorker);
            pool.Queued += (body, text) => rt.Events.Post(GameEvent.Progress(text) with { Body = body == "main" ? null : body });
            worker.Pool = pool;
            dir.Workers = pool;
        }
        log?.LogInformation("bodies: multi-body brain interface on (body routing, focus, [bodies], digest{Pool})",
            dir.Workers != null ? $", worker pool of {dir.Workers.MaxConcurrent}" : "");
        return dir;
    }

    /// <summary>A body's worker: bound to that body (scope, state header, gains from its ledger).</summary>
    private void ConfigureWorker(string shortName, WorkerAgent w)
    {
        w.StepScope = () => Find(shortName) is { } b ? BodyScope.Push(b) : null;
        w.StateHeader = () => StateHeader(shortName) ?? "";
        w.GainsTracker = () =>
        {
            var ledger = (Find(shortName) as Body)?.Ledger;
            var t0 = (_brain.Time ?? TimeProvider.System).GetUtcNow();
            return () => ledger?.GainsSince(t0);
        };
    }

    // ================================================================ IBodyDirectory

    public bool Active => _reg.Accounts.Count > 0;

    private IBody? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Equals("main", StringComparison.OrdinalIgnoreCase)) return _reg.Main;
        return _reg.Find(name);
    }

    public IReadOnlyList<string> Online()
    {
        var list = new List<string> { "main" };
        foreach (var b in _reg.All)
            if (!b.IsMain && b.Adapter.IsPlaying && b.State is BodyState.Online) list.Add(b.Short);
        return list;
    }

    public IReadOnlyList<string> Known() => ["main", .. _reg.Accounts.Select(a => Body.ShortName(a, false))];

    public string? Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().TrimStart('@', '#');
        if (n.Equals("main", StringComparison.OrdinalIgnoreCase) || n.Equals(_reg.Main.Name, StringComparison.OrdinalIgnoreCase)) return "main";
        var rt = _brain.Runtime;
        if (n.Equals(rt.Chat.DisplayName, StringComparison.OrdinalIgnoreCase) || rt.Chat.Aliases.Contains(n, StringComparer.OrdinalIgnoreCase)) return "main";
        if (_reg.Find(n) is { } b) return b.IsMain ? "main" : b.Short;
        return _reg.Resolve(n) is { } login ? Body.ShortName(login, false) : null;
    }

    public ActivityManager? Activities(string body) => Find(body)?.Activities;

    public string? Holder(string body) => Find(body) is Body { ScriptBridge: { } lane } ? lane.BodyState() : null;

    public IDisposable? Push(string body) => Find(body) is { } b ? BodyScope.Push(b) : null;

    public string? GuardKey(string body) => Find(body) is { IsMain: false } b ? b.Name : null;

    public string? Stop(string body)
    {
        if (Find(body) is not Body b) return null;
        var what = b.Activities.Cancel("stop");
        if (b.ScriptBridge is { } lane && lane.Controller.Current is { } s)
        {
            what ??= "script " + s.Name;
            lane.OnStopTool();
        }
        return what;
    }

    public string? Pick(string job, IReadOnlyList<string> candidates, out string? why)
    {
        why = null;
        var bodies = candidates.Select(Find).Where(b => b != null && !b.IsMain).Select(b => b!).ToList();
        if (bodies.Count == 0) { why = "no clone body is in game (bodies op=spawn)"; return null; }
        if (Picker is { } pick)
        {
            try
            {
                var (p, w) = pick(job, bodies);
                why = w;
                return p == null ? null : Canonical(p);
            }
            catch (Exception ex) { _log?.LogDebug(ex, "bodies: picker failed"); }
        }
        return bodies.FirstOrDefault(b => !b.Activities.IsBusy)?.Short ?? bodies[0].Short;
    }

    public string? StandingFocus => _reg.Focus is { IsMain: false } f ? f.Short : null;

    public IReadOnlyList<string> Addressed(string text, bool emote = false)
    {
        var a = Addressing();
        return a.Addressed(text, emote).Select(n => n.Equals(a.Main, StringComparison.OrdinalIgnoreCase) ? "main" : Body.ShortName(n, false)).ToList();
    }

    /// <summary>The line names a clone body (so it is the brain's line, even without main's name in it).</summary>
    public bool NamesAClone(string text, bool emote = false)
    {
        try { return Addressing().AddressedClones(text, emote).Count > 0; }
        catch (Exception) { return false; }
    }

    /// <summary>Main's brainstem hears a line unless it names other bodies only (or is a whisper to a clone's account).</summary>
    public bool MainHears(GameEvent e)
    {
        if (GameEvent.BodyTag(e.Body) != null) return false;
        var named = Addressed(e.Text, e.Kind == EventKind.Emote);
        return named.Count == 0 || named.Contains("main", StringComparer.OrdinalIgnoreCase);
    }

    private BodyAddressing Addressing()
    {
        var rt = _brain.Runtime;
        var accounts = _reg.Accounts;
        string[] mainNames = [rt.Chat.DisplayName, .. rt.Chat.Aliases];
        var key = string.Join("|", accounts) + "#" + string.Join("|", mainNames);
        var cur = Volatile.Read(ref _addressing);
        if (cur != null && key == _addressingKey) return cur;
        var fresh = new BodyAddressing(_reg.Main.Name, mainNames, accounts);
        _addressingKey = key;
        Volatile.Write(ref _addressing, fresh);
        return fresh;
    }

    public string StatusBlock(string? focus)
    {
        var parts = new List<BodyStatusBlock.Part>();
        var live = _reg.All;
        parts.Add(new BodyStatusBlock.Part("main", true, Line(_reg.Main)));
        // Idle bodies collapse into one part with why ("idle 7 (101,105,…): chop_tree: exhausted: …"): 20 bodies fit the line.
        var idle = IdleWatch?.Idle().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        int idleOnline = 0;
        foreach (var acc in _reg.Accounts)
        {
            var shortName = Body.ShortName(acc, false);
            var b = live.FirstOrDefault(x => x.Name.Equals(acc, StringComparison.OrdinalIgnoreCase));
            bool online = b != null && b.Adapter.IsPlaying && b.State is BodyState.Online;
            if (online && idle.Contains(shortName) && !string.Equals(shortName, focus, StringComparison.OrdinalIgnoreCase)) { idleOnline++; continue; }
            parts.Add(new BodyStatusBlock.Part(shortName, online, online ? Line(b!) : ""));
        }
        if (idleOnline > 0 && IdleWatch?.Summary() is { } summary && summary.StartsWith("idle ", StringComparison.Ordinal))
            parts.Add(new BodyStatusBlock.Part("idle", true, summary["idle ".Length..]));
        return BodyStatusBlock.Render(parts, focus);
    }

    /// <summary>"gen #4 @2 E of cobble_gen hp18 f12 stone_pickaxe free8" / "DANGER hp6 ..." / "idle ..." (≤ ~25 tokens).</summary>
    internal string Line(IBody b)
    {
        try
        {
            var w = b.Adapter.World;
            var p = w.Player.Position;
            var job = b.Scripts?.Current is { } s ? $"script {s.Name}" + (s.ProgressPct is { } pct ? $" {pct:0}%" : "")
                : b.Activities.Current is { } a ? $"{Short(a.Label)} #{a.Id}" : "idle";
            string where;
            try { where = _brain.Landmark(p.X, p.Y, p.Z) ?? $"{(int)Math.Floor(p.X)},{(int)Math.Floor(p.Y)},{(int)Math.Floor(p.Z)}"; }
            catch (Exception) { where = $"{(int)Math.Floor(p.X)},{(int)Math.Floor(p.Y)},{(int)Math.Floor(p.Z)}"; }
            int hp = w.Player.Health, food = w.Player.Food;
            var slots = w.Inventory.PlayerSlots();
            int free = 0;
            for (int i = 9; i < slots.Length && i <= 44; i++) if (slots[i].IsEmpty) free++;
            var held = w.Inventory.HeldItem(w.Player.HeldSlot);
            var tool = !held.IsEmpty && MineClaw.Data.Items.Exists(held.Id) && MineClaw.Data.Items.Get(held.Id).MaxDamage > 0
                ? " " + MineClaw.Data.Items.Get(held.Id).Name : "";
            var danger = hp is > 0 and <= 6 ? "DANGER " : "";
            var hold = (b as Body) is { } body && _reg.VoidHoldOf(body.Name) is not null ? " HOLD over void" : "";
            return $"{danger}{job} @{where} hp{hp} f{food}{tool} free{free}{hold}";
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "bodies: status line of {Body} failed", b.Name);
            return "?";
        }
    }

    private static string Short(string label) => label switch
    {
        "harvest_generator" => "gen",
        "chop_tree" => "chop",
        WorkerAgent.ToolName => "worker",
        _ => label,
    };

    public string? StateHeader(string body)
    {
        if (Find(body) is not { IsMain: false } b) return null;
        var h = GameBridge.StateHeader(b.Adapter);
        return (b as Body)?.ScriptBridge?.HeaderTag() is { } tag ? h + " | " + tag : h;
    }

    public void Started(IReadOnlyList<string> bodies) => Digest.BeginGroup(bodies);
}
