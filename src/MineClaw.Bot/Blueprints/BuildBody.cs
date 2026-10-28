using MineClaw.Bot.Bodies;
using MineClaw.Bot.Building;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// One body that can build (one Minecraft connection). The executor runs per body and per part; it never touches a client
/// directly, so the coming one-brain-many-bodies host can hand it any body (BodyScope / IBody).
/// </summary>
public interface IBuildBody
{
    string Name { get; }
    WorldState World { get; }
    Task<ActionResult> GotoAsync(BlockPos stand, CancellationToken ct);
    /// <summary>Places a step's block (its item variant; the face to click when the step names one).</summary>
    Task<ActionResult> PlaceAsync(BuildStep step, int itemId, int damage, CancellationToken ct);
    Task<ActionResult> DigAsync(BlockPos pos, CancellationToken ct);
    int Count(int itemId, int damage);
    /// <summary>Gets <paramref name="count"/> of an item on hand (acquisition planner: chests, crafting, gen, trees); null = done, else why not.</summary>
    Task<string?> EnsureAsync(int itemId, int damage, int count, CancellationToken ct);
    /// <summary>A few clutch blocks before working at height (best effort; a short note or null).</summary>
    Task<string?> ClutchAsync(CancellationToken ct);
    bool IsProtected(BlockPos p, int id);

    // ---- build-planner primitives (defaults: unsupported, so fakes needn't implement them; the planner only uses water
    // columns when a water bucket is on hand)

    /// <summary>Digs from where the body is (a planned stand or a water column), no pathing.</summary>
    Task<ActionResult> DigHereAsync(BlockPos pos, CancellationToken ct) => DigAsync(pos, ct);
    /// <summary>Pours the water bucket into <paramref name="source"/> against <paramref name="face"/> of the block behind it.</summary>
    Task<ActionResult> PourWaterAsync(BlockPos source, BlockFace face, CancellationToken ct) => Task.FromResult(ActionResult.Fail("this body can't pour water"));
    /// <summary>Steps from <paramref name="entry"/> into the column (or moves within it) and holds the feet at <paramref name="feetY"/>.</summary>
    Task<ActionResult> SwimToDepthAsync(SwimColumn column, BlockPos entry, double feetY, CancellationToken ct) =>
        Task.FromResult(ActionResult.Fail("this body can't ride a water column"));
    /// <summary>Swims up and climbs out onto <paramref name="exit"/>.</summary>
    Task<ActionResult> SwimOutAsync(SwimColumn column, BlockPos exit, CancellationToken ct) => Task.FromResult(ActionResult.Success("not in a column"));
    /// <summary>Takes the water source back with the empty bucket.</summary>
    Task<ActionResult> PickupWaterAsync(BlockPos source, CancellationToken ct) => Task.FromResult(ActionResult.Fail("this body can't take water back"));
    /// <summary>Standing in <paramref name="feet"/>: jumps and places <paramref name="itemId"/> into that cell under itself (a
    /// pillar step), ending on top of it.</summary>
    Task<ActionResult> PillarAsync(BlockPos feet, int itemId, CancellationToken ct) => Task.FromResult(ActionResult.Fail("this body can't pillar"));

    // ---- crew builds (defaults: one always-there body without a claim board)

    /// <summary>In the game and alive: a crew lane stops (and is re-balanced) when its body isn't.</summary>
    bool Available => true;
    /// <summary>While not <see cref="Available"/>: seconds until it's expected back (a death's respawn), null = gone (left, kicked).</summary>
    double? BackInSeconds => null;
    /// <summary>This body's view of the hive claim board (stand cells are leased through it); null = none.</summary>
    IClaimBoard? Claims => null;
    /// <summary>Puts this body in scope for the calling flow (routed tools and acquisition act on it); null = nothing to do.</summary>
    IDisposable? Enter() => null;
}

/// <summary>
/// The bodies a build can be split across (the slicer makes one lane per body). One body: <see cref="SingleBodyCrew"/>;
/// in-process bodies: <see cref="RegistryCrew"/> (the online, idle ones); hive clones join through <see cref="IBuildJobs"/>.
/// </summary>
public interface IBuildCrew
{
    IReadOnlyList<IBuildBody> Bodies { get; }
}

/// <summary>
/// The in-process crew (<c>Bodies:Mode=inproc</c>): every online body of the registry that has no job of its own (main
/// always), each acting through its own claim-aware client and claim board view, resolved per call.
/// </summary>
public sealed class RegistryCrew(BodyRegistry registry) : IBuildCrew
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GameBuildBody> _bodies = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IBuildBody> Bodies =>
        registry.All.Where(b => b.IsMain || b.State == BodyState.Online && !b.Activities.IsBusy)
            .Select(b => (IBuildBody)_bodies.GetOrAdd(b.Name, _ => new GameBuildBody(() => b.Game, b))).ToList();

    /// <summary>Every online body, busy or not (a build that names its bodies).</summary>
    public IReadOnlyList<IBuildBody> Online =>
        registry.All.Where(b => b.IsMain || b.State == BodyState.Online)
            .Select(b => (IBuildBody)_bodies.GetOrAdd(b.Name, _ => new GameBuildBody(() => b.Game, b))).ToList();
}

/// <summary>
/// Hive mode (clones are separate processes): the crew's other lanes are hive jobs. Main slices the build for itself plus the
/// clones, then each clone runs its lane through its own <c>build{bp, lane}</c> (shared db: the plan and its per-lane progress
/// rows; the claim board: stand cells), and main takes the scaffold down once every lane is done.
/// </summary>
public interface IBuildJobs
{
    /// <summary>Clones that could build now: online and idle (or, when <paramref name="names"/> are given, those clones).</summary>
    IReadOnlyList<CrewMember> Candidates(IReadOnlyList<string>? names = null);
    /// <summary>Starts lane <paramref name="lane"/> of design <paramref name="blueprintId"/> on clone <paramref name="body"/> (a hive job
    /// running <c>build{bp, lane}</c>). Null = started, else why not.</summary>
    string? StartLane(string body, long blueprintId, int lane);
}

public sealed class SingleBodyCrew(IBuildBody body) : IBuildCrew
{
    public IReadOnlyList<IBuildBody> Bodies { get; } = [body];
}

/// <summary>The scripts behind build jobs (C#'s fixed enabling primitives; no model authors them).</summary>
public static class BuildJobs
{
    /// <summary>A hive clone's lane job (JavaScript over the <c>build</c> host binding = <see cref="BlueprintService.BuildLaneAsync"/>).</summary>
    public const string LaneScript =
        "// purpose: build one lane of a crew build (the slicer's lane job; main coordinates and finishes)\n" +
        "const r = expect(build({ bp: args.bp, lane: args.lane }))\n" +
        "return r.msg\n";
}

/// <summary>A body over the bot layer's <see cref="IGameClient"/>, resolved on every call (never captured).</summary>
public sealed class GameBuildBody(Func<IGameClient> client, IBody? owner = null) : IBuildBody
{
    private IGameClient C => client();
    public string Name => owner?.Name ?? C.Username;
    public WorldState World => C.World;

    public bool Available => (owner == null || owner.State == BodyState.Online) && C.IsPlaying;
    public double? BackInSeconds => C.World.Player.IsDead ? 5 : owner is { State: BodyState.Connecting } ? 10 : null;
    public IClaimBoard? Claims => GameClients.Claimed(C)?.Claims is { } b && b is not NullClaimBoard ? b : null;
    public IDisposable? Enter() => owner != null ? BodyScope.Push(owner) : null;

    public Task<ActionResult> GotoAsync(BlockPos stand, CancellationToken ct) =>
        C.GotoAsync(new GoalBlock(stand), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) }, ct);

    private MinecraftClient? Real => GameClients.Minecraft(C)?.Inner;

    /// <summary>A planned step (face chosen by the planner, body already on its stand) clicks exactly that face from here;
    /// otherwise the client finds its own aim and paths into reach.</summary>
    public Task<ActionResult> PlaceAsync(BuildStep step, int itemId, int damage, CancellationToken ct) =>
        step.Face is { } face && Real is { } real
            ? real.PlaceAgainstAsync(step.Pos, face, itemId, BuildBlocks.HasVariants(step.Id) ? damage : null, step.Yaw, step.Hidden, ct)
            : BuildBlocks.HasVariants(step.Id) || step.Face != null
            ? C.PlaceVariantAsync(step.Pos.X, step.Pos.Y, step.Pos.Z, itemId, BuildBlocks.HasVariants(step.Id) ? damage : null, step.Face, ct)
            : C.PlaceBlockAsync(step.Pos.X, step.Pos.Y, step.Pos.Z, itemId, ct);

    public Task<ActionResult> DigAsync(BlockPos pos, CancellationToken ct) => C.DigBlockAsync(pos.X, pos.Y, pos.Z, ct);

    public Task<ActionResult> DigHereAsync(BlockPos pos, CancellationToken ct) =>
        Real is { } r ? r.DigHereAsync(pos, ct) : C.DigBlockAsync(pos.X, pos.Y, pos.Z, ct);

    public Task<ActionResult> PourWaterAsync(BlockPos source, BlockFace face, CancellationToken ct) =>
        Real is { } r ? r.PourWaterAsync(source, face, ct) : Task.FromResult(ActionResult.Fail("pouring needs the real client"));

    public Task<ActionResult> SwimToDepthAsync(SwimColumn column, BlockPos entry, double feetY, CancellationToken ct) =>
        Real is { } r ? r.SwimToDepthAsync(column, entry, feetY, ct) : Task.FromResult(ActionResult.Fail("swimming needs the real client"));

    public Task<ActionResult> SwimOutAsync(SwimColumn column, BlockPos exit, CancellationToken ct) =>
        Real is { } r ? r.SwimOutAsync(column, exit, ct) : Task.FromResult(ActionResult.Success("not in a column"));

    public Task<ActionResult> PickupWaterAsync(BlockPos source, CancellationToken ct) =>
        Real is { } r ? r.PickupWaterAsync(source, ct) : Task.FromResult(ActionResult.Fail("taking water back needs the real client"));

    public Task<ActionResult> PillarAsync(BlockPos feet, int itemId, CancellationToken ct) =>
        Real is { } r ? r.PillarPlaceAsync(feet, itemId, ct) : Task.FromResult(ActionResult.Fail("pillaring needs the real client"));

    public int Count(int itemId, int damage) => C.World.Inventory.CountItem(itemId, BuildBlocks.HasVariants(itemId) && damage >= 0 ? damage : null);

    public async Task<string?> EnsureAsync(int itemId, int damage, int count, CancellationToken ct)
    {
        if (Count(itemId, damage) >= count) return null;
        var c = C;
        if (Acquisition.Of(c) == null) return $"missing {ItemName(itemId, damage)} (need {count}, have {Count(itemId, damage)}): no acquisition sources";
        // The exact variant ("cobblestone_slab" = 44:3), in a batch the inventory can hold (the rest comes when it's placed).
        var name = BuildBlocks.HasVariants(itemId) ? Items.VariantName(itemId, damage) : Items.Get(itemId).Name;
        count = Math.Max(1, Math.Min(count, Count(itemId, damage) + Room(c.World, itemId, BuildBlocks.HasVariants(itemId) ? damage : null)));
        if (Count(itemId, damage) >= count) return null;
        var r = await Acquisition.Planner(c).AcquireAsync(new Need(name, count), ct).ConfigureAwait(false);
        return Count(itemId, damage) >= count ? null : $"missing {ItemName(itemId, damage)} (need {count}, have {Count(itemId, damage)}): {r.Text}";
    }

    public Task<string?> ClutchAsync(CancellationToken ct) => ClutchBlocks.EnsureAsync(C, ct);

    public bool IsProtected(BlockPos p, int id) => C.IsProtected?.Invoke(p, id) == true;

    /// <summary>How many more of an item the inventory holds (free slots, room in partial stacks).</summary>
    internal static int Room(WorldState w, int id, int? damage)
    {
        int max = Math.Max(1, Items.Get(id).MaxStack), room = 0;
        var slots = w.Inventory.PlayerSlots();
        for (int i = 9; i < 45 && i < slots.Length; i++)
        {
            var st = slots[i];
            if (st.IsEmpty) room += max;
            else if (st.Id == id && (damage is null || st.Damage == damage)) room += Math.Max(0, max - st.Count);
        }
        return room;
    }

    internal static string ItemName(int item, int damage) =>
        (BuildBlocks.HasVariants(item) ? Items.VariantName(item, damage) : Items.Get(item).Name).Replace('_', ' ');
}
