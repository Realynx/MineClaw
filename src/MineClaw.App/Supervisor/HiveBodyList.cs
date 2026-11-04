using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MineClaw.Agent.Telemetry;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Supervisor;

namespace MineClaw.App.Supervisor;

/// <summary>A hive clone as the body list reads it (a <see cref="CloneSlot"/> in the live process; tests build them directly).</summary>
public sealed record SlotView(string Name, bool Connected, BotSnapshot? Snapshot, string? Task = null, bool Starting = false);

/// <summary>
/// One body the process controls, for every supervisor list (<c>/api/bots</c>, <c>/api/status</c>, <c>/api/world/entities</c>,
/// <c>/api/viewer/now</c>, <c>/api/bodies/scripts</c>).
/// </summary>
/// <param name="Kind">"main" | "hive" | "subhive" | "script".</param>
/// <param name="Owner">The owner's login: null for main, main's login for main's direct children, a hive's login for its bodies.</param>
/// <param name="Online">In game now (false = spawning, logging in or gone; its position may be unknown: <paramref name="HasPos"/>).</param>
/// <param name="State">The ownership tree's state ("spawning" | "running" | "releasing"), null when the tree doesn't know it.</param>
public sealed record BodyRow(string Name, string Short, string Kind, string? Owner, bool IsMain, bool Online, int Dim, double X, double Y, double Z,
    int Hp, int Food, string? Job, string? Brain, BodyScriptBrief? Script, DateTimeOffset At, bool HasPos = true, string? State = null);

/// <summary>
/// Every body this process controls, in one list: main, main's own in-process bodies (subhive bodies or <c>Bodies:Mode=inproc</c>
/// clones), each hive clone (in-process and process hives alike, from the supervisor's slots), each hive's own subhive bodies (from
/// the <see cref="BotSnapshot.Bodies"/> the hive streams with its snapshot), and the ownership tree's nodes not in game yet. Kind and
/// owner come from the tree when it knows the body. Reads only; cached for <see cref="Ttl"/> (several routes ask per second).
/// It never feeds the brain: the hive overview and remote <c>see</c> keep reading <see cref="VisionHub.HiveBots"/>.
/// </summary>
public sealed class HiveBodyList
{
    private static readonly Regex BrainScript = new(@"\(script (?<name>[a-z0-9_]+)(?<paused> paused)?", RegexOptions.Compiled);

    private readonly string _main;
    private readonly Func<IReadOnlyList<HiveBotState>> _hiveBots;
    private readonly Func<IEnumerable<BodyBrief>?> _local;
    private readonly Func<IEnumerable<SlotView>> _slots;
    private readonly Func<HiveTree?> _tree;
    private readonly Func<BodyScriptBrief?> _mainScript;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private (DateTimeOffset At, IReadOnlyList<BodyRow> Rows)? _cache;

    public TimeSpan Ttl { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>A hive clone's telemetry source by login (dynamic accounts have no fixed roster entry); null = none.</summary>
    public Func<string, IBotTelemetrySource?>? Remote { get; init; }

    public HiveBodyList(string main, Func<IReadOnlyList<HiveBotState>> hiveBots, Func<IEnumerable<BodyBrief>?>? local = null,
        Func<IEnumerable<SlotView>>? slots = null, Func<HiveTree?>? tree = null, Func<BodyScriptBrief?>? mainScript = null, TimeProvider? time = null)
    {
        _main = main;
        _hiveBots = hiveBots;
        _local = local ?? (() => null);
        _slots = slots ?? (() => []);
        _tree = tree ?? (() => null);
        _mainScript = mainScript ?? (() => null);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The live process's list: the hub's hive bots, the bot's body registry, the supervisor's slots and the tree.</summary>
    public static HiveBodyList For(VisionHub hub, BotHost bot, string main, Func<HiveSupervisor?> hive, Func<HiveTree?> tree)
    {
        var sources = new ConditionalWeakTable<CloneSlot, RemoteBotSource>();
        return new HiveBodyList(main, hub.HiveBots, () => bot.Bodies is { } reg ? BodyBriefs.Of(reg.All) : null,
            () => hive()?.Slots.Select(s => new SlotView(s.Name, s.Connected, s.Snapshot, s.Task?.Text, s.State == CloneState.Starting)) ?? [],
            tree, () => BodyBriefs.Script(ScriptTools.Of(bot.Runtime)?.Controller.Current))
        {
            Remote = name => hive()?.Slot(name) is { } slot ? sources.GetValue(slot, s => new RemoteBotSource(s)) : null,
        };
    }

    public bool IsMain(string name) => name.Equals("main", StringComparison.OrdinalIgnoreCase) || name.Equals(_main, StringComparison.OrdinalIgnoreCase);

    /// <summary>The row named by login or short ("102", "main"); null = not a body of this process.</summary>
    public BodyRow? Find(string bot)
    {
        var n = bot.Trim();
        if (n.Length == 0) return null;
        var rows = Rows();
        if (IsMain(n)) return rows.FirstOrDefault(r => r.IsMain);
        return rows.FirstOrDefault(r => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(r => !r.IsMain && r.Short.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The fixed sources plus a remote one for every hive clone that has none (dynamic accounts), main first as given.</summary>
    public IReadOnlyList<IBotTelemetrySource> Sources(IReadOnlyList<IBotTelemetrySource> fixedSources)
    {
        if (Remote is not { } remote) return fixedSources;
        var list = fixedSources.ToList();
        var have = new HashSet<string>(list.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rows())
            if (r.Kind is "hive" or "script" && !r.IsMain && have.Add(r.Name) && remote(r.Name) is { } s) list.Add(s);
        return list;
    }

    /// <summary>A body's telemetry source: a fixed one, else a hive clone's remote one; null for subhive bodies (no brain, no stream).</summary>
    public IBotTelemetrySource? SourceFor(string bot, IReadOnlyList<IBotTelemetrySource> fixedSources)
    {
        var name = IsMain(bot) ? _main : Find(bot)?.Name ?? bot;
        return fixedSources.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? (Remote is { } r && !IsMain(name) ? r(name) : null);
    }

    public IReadOnlyList<BodyRow> Rows()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
            if (_cache is { } c && now - c.At < Ttl) return c.Rows;
        var rows = Build(now);
        lock (_lock) _cache = (now, rows);
        return rows;
    }

    private IReadOnlyList<BodyRow> Build(DateTimeOffset now)
    {
        var rows = new RowList(this, Try(_tree, null), now);
        var hive = Try(_hiveBots, []);
        var slots = Try(() => _slots().ToList(), []);
        var slotOf = slots.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // main first, then its own in-process bodies
        rows.AddMain(hive, _mainScript);
        rows.AddLocal(Try(_local, null));
        // each hive clone, followed by its own subhive bodies
        rows.AddHubClones(hive, slotOf);
        rows.AddSlots(slots);
        // the tree's nodes not in game yet (a hive's bodies still being allocated, logging in, or releasing)
        rows.AddTreeNodes();
        return rows.List;
    }

    /// <summary>A source's answer, or <paramref name="fallback"/> when it throws (every source is read best-effort).</summary>
    private static T Try<T>(Func<T> source, T fallback)
    {
        try { return source(); }
        catch (Exception) { return fallback; }
    }

    /// <summary>The rows being built: each body once (the first source to name it wins), kind and owner from the tree when it knows them.</summary>
    private sealed class RowList(HiveBodyList owner, HiveTree? tree, DateTimeOffset now)
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private string Main => owner._main;

        public List<BodyRow> List { get; } = new();

        private string? OwnerOf(string name, string fallback)
        {
            if (tree?.Get(name) is not { } n) return fallback;
            return OwnerName(n);
        }

        private string OwnerName(HiveNode n) => n.Owner.Equals(HiveTree.Root, StringComparison.OrdinalIgnoreCase) ? Main : n.Owner;

        private string KindOf(string name, string fallback) => tree?.Get(name) is { } n ? HiveKinds.Name(n.Kind) : fallback;

        private string Driven(string? o) => "driven by " + (o == null || owner.IsMain(o) ? "main" : HiveVision.ShortName(o));

        private BodyRow FromBrief(BodyBrief b, string fallbackOwner)
        {
            var o = OwnerOf(b.Name, fallbackOwner);
            return new BodyRow(b.Name, HiveVision.ShortName(b.Name), KindOf(b.Name, "subhive"), o, false, b.Playing, b.Dimension, b.X, b.Y, b.Z, b.Hp, b.Food,
                b.Task, Driven(o), b.Script, b.At, true, tree?.Get(b.Name)?.State);
        }

        public void AddMain(IReadOnlyList<HiveBotState> hive, Func<BodyScriptBrief?> mainScript)
        {
            if (hive.FirstOrDefault(b => b.IsMain) is not { } m || !_seen.Add(m.Name)) return;
            var ms = Try(mainScript, null);
            List.Add(new BodyRow(m.Name, "main", "main", null, true, true, m.Dim, R(m.X), R(m.Y), R(m.Z), m.Hp, m.Food, m.Job, m.Brain, ms, m.At));
        }

        public void AddLocal(IEnumerable<BodyBrief>? local)
        {
            foreach (var b in local ?? [])
                if (!owner.IsMain(b.Name) && _seen.Add(b.Name)) List.Add(FromBrief(b, Main));
        }

        /// <summary>A connected hive's own subhive bodies, streamed with its snapshot.</summary>
        private void AddBodiesOf(SlotView s)
        {
            if (!s.Connected || s.Snapshot?.Bodies is not { } bodies) return;
            foreach (var b in bodies)
                if (_seen.Add(b.Name)) List.Add(FromBrief(b, s.Name));
        }

        public void AddHubClones(IReadOnlyList<HiveBotState> hive, Dictionary<string, SlotView> slotOf)
        {
            foreach (var hb in hive.Where(b => !b.IsMain))
            {
                if (!_seen.Add(hb.Name)) continue;
                slotOf.TryGetValue(hb.Name, out var s);
                List.Add(new BodyRow(hb.Name, hb.Short, KindOf(hb.Name, "hive"), OwnerOf(hb.Name, Main), false, true, hb.Dim, R(hb.X), R(hb.Y), R(hb.Z), hb.Hp,
                    hb.Food, hb.Job, hb.Brain, SlotScript(hb.Brain), hb.At, true, tree?.Get(hb.Name)?.State));
                if (s != null) AddBodiesOf(s);
            }
        }

        /// <summary>Connected without a position yet, or launching: listed (offline) when the tree or a task says it's meant to be here.</summary>
        public void AddSlots(List<SlotView> slots)
        {
            foreach (var s in slots)
            {
                var node = tree?.Get(s.Name);
                if (!_seen.Contains(s.Name) && (s.Connected || s.Starting || node != null))
                {
                    _seen.Add(s.Name);
                    List.Add(OfflineSlot(s, node));
                }
                AddBodiesOf(s);
            }
        }

        private BodyRow OfflineSlot(SlotView s, HiveNode? node)
        {
            var snap = s.Snapshot;
            return new BodyRow(s.Name, HiveVision.ShortName(s.Name), KindOf(s.Name, "hive"), OwnerOf(s.Name, Main), false, false, snap?.Dimension ?? 0,
                snap?.Pos?.X ?? 0, snap?.Pos?.Y ?? 0, snap?.Pos?.Z ?? 0, snap?.Hp ?? 0, snap?.Food ?? 0, SlotJob(s, node),
                snap?.Brain, SlotScript(snap?.Brain), snap?.At ?? now, snap?.Pos != null, node?.State);
        }

        private static string? SlotJob(SlotView s, HiveNode? node) => s.Snapshot?.Task ?? s.Task ?? node?.Task ?? node?.State;

        public void AddTreeNodes()
        {
            if (tree == null) return;
            foreach (var n in tree.All().OrderBy(n => n.Account, StringComparer.OrdinalIgnoreCase))
                if (_seen.Add(n.Account)) List.Add(TreeNode(n));
        }

        private BodyRow TreeNode(HiveNode n)
        {
            var o = OwnerName(n);
            var kind = HiveKinds.Name(n.Kind);
            return new BodyRow(n.Account, n.Short, kind, o, false, false, 0, 0, 0, 0, 0, 0, n.Kind == CloneKind.Script ? n.Script : n.Task ?? n.State,
                kind == "subhive" ? Driven(o) : null, null, n.Since == default ? now : n.Since, false, n.State);
        }
    }

    /// <summary>A clone's script from its brain state ("asleep (script cobble_loop 34%)"): no line over this path.</summary>
    private static BodyScriptBrief? SlotScript(string? brain) =>
        brain != null && BrainScript.Match(brain) is { Success: true } m
            ? new BodyScriptBrief(m.Groups["name"].Value, m.Groups["paused"].Success ? "paused" : "running", 0, m.Groups["paused"].Success, null)
            : null;

    private static double R(double v) => Math.Round(v, 2);
}
