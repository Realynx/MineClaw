using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

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
}

/// <summary>The bodies a build can be split across. Today: the bot's own body; the multi-body host plugs in more.</summary>
public interface IBuildCrew
{
    IReadOnlyList<IBuildBody> Bodies { get; }
}

public sealed class SingleBodyCrew(IBuildBody body) : IBuildCrew
{
    public IReadOnlyList<IBuildBody> Bodies { get; } = [body];
}

/// <summary>A body over the bot layer's <see cref="IGameClient"/>, resolved on every call (never captured).</summary>
public sealed class GameBuildBody(Func<IGameClient> client) : IBuildBody
{
    private IGameClient C => client();
    public string Name => C.Username;
    public WorldState World => C.World;

    public Task<ActionResult> GotoAsync(BlockPos stand, CancellationToken ct) =>
        C.GotoAsync(new GoalBlock(stand), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) }, ct);

    public Task<ActionResult> PlaceAsync(BuildStep step, int itemId, int damage, CancellationToken ct) =>
        BuildBlocks.HasVariants(step.Id) || step.Face != null
            ? C.PlaceVariantAsync(step.Pos.X, step.Pos.Y, step.Pos.Z, itemId, BuildBlocks.HasVariants(step.Id) ? damage : null, step.Face, ct)
            : C.PlaceBlockAsync(step.Pos.X, step.Pos.Y, step.Pos.Z, itemId, ct);

    public Task<ActionResult> DigAsync(BlockPos pos, CancellationToken ct) => C.DigBlockAsync(pos.X, pos.Y, pos.Z, ct);

    public int Count(int itemId, int damage) => C.World.Inventory.CountItem(itemId, BuildBlocks.HasVariants(itemId) && damage >= 0 ? damage : null);

    public async Task<string?> EnsureAsync(int itemId, int damage, int count, CancellationToken ct)
    {
        if (Count(itemId, damage) >= count) return null;
        var c = C;
        if (Acquisition.Of(c) == null) return $"missing {ItemName(itemId, damage)} (need {count}, have {Count(itemId, damage)}): no acquisition sources";
        var name = BuildBlocks.HasVariants(itemId) ? Items.VariantName(itemId, damage) : Items.Get(itemId).Name;
        var r = await Acquisition.Planner(c).AcquireAsync(new Need(name, count), ct).ConfigureAwait(false);
        return Count(itemId, damage) >= count ? null : $"missing {ItemName(itemId, damage)} (need {count}, have {Count(itemId, damage)}): {r.Text}";
    }

    public Task<string?> ClutchAsync(CancellationToken ct) => ClutchBlocks.EnsureAsync(C, ct);

    public bool IsProtected(BlockPos p, int id) => C.IsProtected?.Invoke(p, id) == true;

    internal static string ItemName(int item, int damage) =>
        (BuildBlocks.HasVariants(item) ? Items.VariantName(item, damage) : Items.Get(item).Name).Replace('_', ' ');
}
