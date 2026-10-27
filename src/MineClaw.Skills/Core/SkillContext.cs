using System.Text.Json;
using MineClaw.Client;
using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

public sealed record SkillOptions
{
    /// <summary>Close doors/gates/trapdoors the bot opened to pass (restore the state it found them in).</summary>
    public bool RestoreDoors { get; init; } = true;
    /// <summary>How long to wait for the server's block change (0x35) after a right-click.</summary>
    public TimeSpan BlockChangeTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>Default search radius for "nearest X".</summary>
    public double SearchRadius { get; init; } = 24;
    /// <summary>Max chars of a skill result (the tool layer caps again).</summary>
    public int MaxResultChars { get; init; } = 300;
    public SurvivalOptions Survival { get; init; } = new();
}

/// <summary>Everything a running skill can use: the game port, the world copy, the registry (to compose other skills)
/// and shared helpers. One per skill invocation tree.</summary>
public sealed class SkillContext(IGameActions game, SkillRegistry registry, SkillOptions? options = null)
{
    public IGameActions Game { get; } = game;
    public SkillRegistry Registry { get; } = registry;
    public SkillOptions Options { get; } = options ?? new SkillOptions();
    public WorldState World => Game.World;

    /// <summary>Optional log sink (tests, debug).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Hive leases: containers/areas are claimed before use (single bot: everything is free).</summary>
    public MineClaw.World.Claims.IClaimBoard Claims { get; init; } = MineClaw.World.Claims.NullClaimBoard.Instance;
    /// <summary>Container names/labels/categories and snapshots (Bot layer: WorldIndex-backed).</summary>
    public Inventory.IContainerDirectory Directory { get; init; } = new Inventory.ContainerDirectory();
    /// <summary>Remembered chest layouts (Bot layer: memory-backed).</summary>
    public Inventory.ILayoutStore Layouts { get; init; } = new Inventory.InMemoryLayoutStore();
    /// <summary>Portal pairs and other per-bot skill state that outlives one call.</summary>
    public SkillState State { get; init; } = new();

    public Vec3d Feet => World.Player.Position;
    public BlockPos Me => new((int)Math.Floor(Feet.X), (int)Math.Floor(Feet.Y + 1e-6), (int)Math.Floor(Feet.Z));
    public bool InNether => World.Dimension == -1;
    public Dim Dimension => InNether ? Dim.Nether : Dim.Overworld;

    public int Count(int itemId, int? damage = null) => World.Inventory.CountItem(itemId, damage);
    public bool Has(int itemId, int? damage = null) => Count(itemId, damage) > 0;
    public int? FirstOwned(params int[] ids) => ids.Cast<int?>().FirstOrDefault(i => Has(i!.Value));

    public int Block(BlockPos p) => World.GetBlock(p);
    public int Meta(BlockPos p) => World.GetMeta(p);
    public string BlockName(BlockPos p) => Blocks.Get(Block(p)).Name;

    public bool Protected(BlockPos p) => Game.IsProtected(p, Block(p));

    /// <summary>Runs another skill as part of this one (same token; preconditions checked).</summary>
    public Task<SkillResult> RunAsync(string skill, object? args, CancellationToken ct) =>
        Registry.RunAsync(this, skill, args is null ? SkillJson.Empty : args as JsonElement? ?? JsonSerializer.SerializeToElement(args), ct);

    /// <summary>Waits until block <paramref name="p"/> satisfies <paramref name="cond"/> (id, meta) — i.e. until the
    /// server's block change arrived. False on timeout.</summary>
    public Task<bool> WaitBlockAsync(BlockPos p, Func<int, int, bool> cond, CancellationToken ct, TimeSpan? timeout = null) =>
        Game.WaitUntilAsync(() => cond(World.GetBlock(p), World.GetMeta(p)), timeout ?? Options.BlockChangeTimeout, ct);

    /// <summary>Survival gate (hp/food/food-in-inventory) for risky work; null = fine.</summary>
    public string? SurvivalVeto(string what)
    {
        var p = World.Player;
        bool hasFood = SurvivalReflex.HasSafeFood(World.Inventory.PlayerSlots(), Game.ReservedItems);
        return SurvivalReflex.GateReason(what, p.Health, p.Food, hasFood, Options.Survival);
    }

    /// <summary>Walks within reach of <paramref name="p"/> (no-op when already there).</summary>
    public async Task<ActionResult> ReachAsync(BlockPos p, CancellationToken ct, double? reach = null)
    {
        double r = reach ?? Game.Reach - 0.6;
        var eye = World.Player.EyePosition;
        if (new Vec3d(p.X + 0.5, p.Y + 0.5, p.Z + 0.5).DistanceTo(eye) <= r) return ActionResult.Success("in reach");
        return await Game.GotoAsync(new GoalWithinReach(p, r), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Nearest blocks matching (id, meta) within <paramref name="radius"/>, protected ones excluded.</summary>
    public List<FoundBlock> Find(Func<int, int, bool> match, double? radius = null, int max = 64) =>
        World.FindBlocks(Feet, radius ?? Options.SearchRadius, match, max)
            .Where(b => !Game.IsProtected(b.Pos, b.Id)).ToList();

    public List<FoundBlock> Find(int id, double? radius = null, int max = 64) => Find((i, _) => i == id, radius, max);
}
