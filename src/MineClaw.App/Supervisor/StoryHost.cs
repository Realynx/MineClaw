using MineClaw.Bot.Bodies;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Story;
using MineClaw.Scripting;
using MineClaw.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.App.Supervisor;

/// <summary>
/// Story Mode's bodies in main's process: the colony group's script-only accounts come from main's hive hierarchy (allocated,
/// whitelisted under the scoped exception, logged in by main's subhive host); each pawn's <c>story_pawn</c> runs as an unattended
/// lane on its own body with <see cref="ScriptRequest.NoModel"/> (no patch turn, no brain wake). The world view is main's.
/// Works the same on b1.8.1 and 1.12.2 (the version's own game data), and with in-process hives.
/// </summary>
public sealed class StoryHost : IStoryHost
{
    private readonly HiveHierarchy? _hierarchy;
    private readonly Func<BodyRegistry?> _bodies;
    private readonly Func<MineClaw.Bot.GameTools.ScriptBridge?> _scripts;
    private readonly ILogger _log;
    private readonly MineClaw.Agent.Rag.ObservationStore? _observations;

    public StoryHost(HiveHierarchy? hierarchy, Func<BodyRegistry?> bodies, Func<WorldState?> world, IGameData data,
        Func<MineClaw.Bot.GameTools.ScriptBridge?> scripts, ILogger? log = null, MineClaw.Agent.Rag.ObservationStore? observations = null)
    {
        _observations = observations;
        _hierarchy = hierarchy;
        _bodies = bodies;
        _scripts = scripts;
        _log = log ?? NullLogger.Instance;
        // Main's sight plus every online body's (pawns load the chunks around a far home that main may not).
        World = new MergedStoryWorld(world, () => (_bodies()?.All ?? []).OfType<Body>().Where(b => b.State == BodyState.Online)
            .Select(b => b.Adapter.World).ToList(), data);
    }

    public IStoryWorld? World { get; }

    public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer) =>
        _hierarchy is { } h ? h.SpawnGroup(colony, n, StoryColonies.PawnScript, prefer) : ([], "the hive is off: Story Mode needs Hive:Enabled and Hive:Subhives:Enabled");

    public int Release(string colony) => _hierarchy?.ReleaseGroup(colony, "story colony released") ?? 0;

    public string? ReleasePawn(string account, string why)
    {
        if (_hierarchy is not { } h) return "the hive is off";
        var r = h.Release(account, HiveTree.Root, why);
        return r.StartsWith("ok", StringComparison.OrdinalIgnoreCase) ? null : r;
    }

    /// <summary>Main is in game (null = assume so).</summary>
    public Func<bool>? MainInGame { get; init; }
    public bool InGame => MainInGame?.Invoke() ?? true;

    public PawnSeat Seat(string account)
    {
        if (_hierarchy is not { } h) return new(false, "the hive is off");
        var note = h.BodyNote(account);
        if (h.Tree.Get(account) is not { } node) return new(false, note);
        if (node.State == "releasing") return new(true, "logging out");
        var body = Find(account);
        if (body is { State: BodyState.Online }) return new(true, null);
        if (note != null) return new(true, note);
        return new(true, body?.State switch
        {
            BodyState.Retired => "retired for this session (crashed too often)",
            BodyState.Occupied => "someone else is logged in as it",
            BodyState.Yielding => "logging out",
            BodyState.Offline => "reconnecting",
            _ => "connecting",
        });
    }

    public (int Free, int Kept)? Seats() => _hierarchy?.Seats();

    public IReadOnlyList<(string Account, string? Why)> Bring(string colony, IReadOnlyList<string> accounts, bool overCap) =>
        _hierarchy is { } h ? h.BringGroup(colony, accounts, StoryColonies.PawnScript, overCap)
            : accounts.Select(a => (a, (string?)"the hive is off: Story Mode needs Hive:Enabled and Hive:Subhives:Enabled")).ToList();

    private Body? Find(string account) => _bodies()?.Find(account) as Body;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (WorldState World, IStoryWorld View)> _views = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The pawn body's own view (its connection's world): a dig it reported done is checked there first.</summary>
    public IStoryWorld? ViewOf(string account)
    {
        if (Find(account) is not { State: BodyState.Online } b || World is not { } main) return null;
        var w = b.Adapter.World;
        if (_views.TryGetValue(account, out var v) && ReferenceEquals(v.World, w)) return v.View;
        var view = new WorldStateStoryWorld(() => w, main.Data);
        _views[account] = (w, view);
        return view;
    }

    public PawnLive Live(string account)
    {
        if (Find(account) is not { } b || b.State != BodyState.Online) return PawnLive.Offline;
        var w = b.Adapter.World;
        var inv = new List<Stack>();
        for (int slot = 9; slot <= 44; slot++)
        {
            var s = w.Inventory.GetPlayerSlot(slot);
            if (s.IsEmpty) continue;
            // A 1.12.2 potion's type is NBT (its damage is 0): the colony tells water bottles from awkward or swiftness potions by meta.
            int meta = MineClaw.Data.Brewing.IsPotionItem(s.Id) && MineClaw.Data.Brewing.TypeId(s.Tag?.Potion) is >= 0 and var pt ? pt : s.Damage;
            int i = inv.FindIndex(x => x.Id == s.Id && x.Meta == meta);
            if (i >= 0) inv[i] = inv[i] with { Count = inv[i].Count + s.Count }; else inv.Add(new Stack(s.Id, meta, s.Count));
        }
        var held = w.Inventory.HeldItem(w.Player.HeldSlot);
        var p = w.Player.Position;
        bool running = b.ScriptBridge?.Controller.Current is { IsLive: true };
        return new PawnLive(true, w.Player.Health, w.Player.Food, p.X, p.Y, p.Z, w.Dimension, held.IsEmpty ? null : World!.Data.Item(held.Id).Name, inv,
            running, w.Inventory.EmptySlotCount(), w.Player.Snapshot.XpLevel);
    }

    public string? StartPawn(string account, string colony)
    {
        if (Find(account) is not { ScriptBridge: { } lane } b) return "no such body";
        if (b.State != BodyState.Online) return "not online";
        if (lane.Controller.Current is { IsLive: true }) return null;
        var v = lane.Library.Resolve(StoryColonies.PawnScript, null, out var why);
        if (v == null) return why ?? "no saved story_pawn";
        var (s, err) = lane.StartLane(new ScriptRequest
        {
            Name = StoryColonies.PawnScript, Code = v.Code, Language = ScriptLanguage.Js, Role = "pawn", CrewId = colony, Version = v.V, Author = v.Author,
            Coordinator = colony + " director (no model)", Unattended = true, NoModel = true,
        }, b);
        if (s == null) return err ?? "can't start";
        _log.LogDebug("story {Colony}: {Pawn} runs story_pawn v{V}", colony, account, v.V);
        return null;
    }

    /// <summary>The bot's memory of a chest (the world index's last seen contents, either half of a double chest), or null.</summary>
    public IReadOnlyList<Stack>? KnownChest(P3 at)
    {
        if (_observations is not { } obs) return null;
        try
        {
            foreach (var p in new[] { at, at.Add(-1, 0, 0), at.Add(0, 0, -1), at.Add(1, 0, 0), at.Add(0, 0, 1) })
                if (obs.Get("chest", MineClaw.Agent.Rag.ObservationStore.KeyFor(p.X, p.Y, p.Z)) is { } row)
                    return obs.Contents(row.Id).Select(c => new Stack(c.ItemId, c.Damage, c.Count)).ToList();
        }
        catch (Exception ex) { _log.LogDebug(ex, "story: chest memory at {At}", at); }
        return null;
    }

    /// <summary>
    /// The op command path for Story Mode's teleports (<see cref="Teleport"/>): arms the line in the command gate's scoped exception and
    /// sends it on main's chat (the gate's blocklist still applies). Returns null when sent, else why not. Null = no such path.
    /// </summary>
    public Func<string, string, Task<string?>>? OpCommand { get; init; }

    public string? Teleport(string account, P3 to, string why)
    {
        if (OpCommand is not { } send) return "no op command path";
        // b1.8.1's /tp (mc-dev ServerConfigurationManager) takes two player names, never coordinates: the pawn walks.
        if (World?.Version == "b1.8.1") return "b1.8.1's /tp takes no coordinates";
        if (Find(account) is not { State: BodyState.Online }) return "not online";
        var line = $"/tp {account} {to.X} {to.Y} {to.Z}";
        _ = Task.Run(async () =>
        {
            try
            {
                if (await send(line, why).ConfigureAwait(false) is { } err) _log.LogWarning("story: {Line} not sent: {Err}", line, err);
            }
            catch (Exception ex) { _log.LogWarning(ex, "story: {Line} failed", line); }
        });
        return null;
    }

    public void Interrupt(string account, string why)
    {
        if (Find(account)?.ScriptBridge?.Controller.Current is { } s) s.Stop(why);
    }
}
