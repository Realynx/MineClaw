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

    public StoryHost(HiveHierarchy? hierarchy, Func<BodyRegistry?> bodies, Func<WorldState?> world, IGameData data,
        Func<MineClaw.Bot.GameTools.ScriptBridge?> scripts, ILogger? log = null)
    {
        _hierarchy = hierarchy;
        _bodies = bodies;
        _scripts = scripts;
        _log = log ?? NullLogger.Instance;
        World = new WorldStateStoryWorld(world, data);
    }

    public IStoryWorld? World { get; }

    public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer) =>
        _hierarchy is { } h ? h.SpawnGroup(colony, n, StoryColonies.PawnScript, prefer) : ([], "the hive is off: Story Mode needs Hive:Enabled and Hive:Subhives:Enabled");

    public int Release(string colony) => _hierarchy?.ReleaseGroup(colony, "story colony released") ?? 0;

    private Body? Find(string account) => _bodies()?.Find(account) as Body;

    public PawnLive Live(string account)
    {
        if (Find(account) is not { } b || b.State != BodyState.Online) return PawnLive.Offline;
        var w = b.Adapter.World;
        var inv = new List<Stack>();
        for (int slot = 9; slot <= 44; slot++)
        {
            var s = w.Inventory.GetPlayerSlot(slot);
            if (s.IsEmpty) continue;
            int i = inv.FindIndex(x => x.Id == s.Id && x.Meta == s.Damage);
            if (i >= 0) inv[i] = inv[i] with { Count = inv[i].Count + s.Count }; else inv.Add(new Stack(s.Id, s.Damage, s.Count));
        }
        var held = w.Inventory.HeldItem(w.Player.HeldSlot);
        var p = w.Player.Position;
        bool running = b.ScriptBridge?.Controller.Current is { IsLive: true };
        return new PawnLive(true, w.Player.Health, w.Player.Food, p.X, p.Y, p.Z, w.Dimension, held.IsEmpty ? null : World!.Data.Item(held.Id).Name, inv,
            running, w.Inventory.EmptySlotCount());
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

    public void Interrupt(string account, string why)
    {
        if (Find(account)?.ScriptBridge?.Controller.Current is { } s) s.Stop(why);
    }
}
