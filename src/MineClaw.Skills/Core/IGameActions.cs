using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>Optional primitives a game backend may lack. Skills that need a missing one fail honestly
/// ("needs client primitive use_on_block") instead of faking it.</summary>
[Flags]
public enum Primitive
{
    None = 0,
    /// <summary>Right-click a block face holding an item (hoe, bonemeal, seeds on farmland, minecart on rail, shears on
    /// a block, flint and steel). The b1.8.1 0x0F packet with a face, for items whose onItemUse needs the clicked block.</summary>
    UseOnBlock = 1,
    /// <summary>0x07 right-click on an entity (shear sheep, milk cow, tame wolf, saddle pig, enter boat/minecart).</summary>
    UseEntity = 2,
    /// <summary>C→S 0x82 Update Sign after placing a sign.</summary>
    WriteSign = 4,
    /// <summary>0x0E status 5: release the used item (bow shot, also ends eating early).</summary>
    ReleaseUse = 8,
    /// <summary>0x0E status 4: drop the held item (one) in the facing direction.</summary>
    DropHeld = 16,
    /// <summary>0x13 entity action (leave bed = 3) / dismount via 0x07 on the vehicle again.</summary>
    LeaveBed = 32,
}

/// <summary>
/// The slice of the game client that skills compose. <see cref="ClientGameActions"/> forwards to
/// <see cref="MinecraftClient"/>; tests use an offline fake over a synthetic <see cref="WorldState"/>. Every action
/// returns a terse <see cref="ActionResult"/> and honours the cancellation token.
/// </summary>
public interface IGameActions
{
    WorldState World { get; }
    string Username { get; }
    /// <summary>Primitives this backend implements beyond the always-present core.</summary>
    Primitive Supported { get; }
    bool IsRaining { get; }
    double Reach { get; }
    IReadOnlySet<int> ReservedItems { get; }
    bool IsProtected(BlockPos pos, int id);
    /// <summary>Why a block is protected and how to unlock it (refusal text); None when unknown.</summary>
    ProtectionVerdict ExplainProtection(BlockPos pos, int id) => ProtectionVerdict.None;
    /// <summary>Sign text the server sent (0x82), by sign position.</summary>
    IReadOnlyDictionary<BlockPos, string[]> Signs { get; }

    Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options, CancellationToken ct);
    Task<ActionResult> DigAsync(BlockPos pos, CancellationToken ct);
    /// <summary>Places a block item (or a placeable item such as seeds, cane, torch, sign, door) at <paramref name="pos"/>
    /// against a solid neighbour; verified by the block appearing.</summary>
    Task<ActionResult> PlaceAsync(BlockPos pos, int itemId, CancellationToken ct);
    /// <summary>Right-clicks an interactable block (door, gate, trapdoor, lever, button, bed, chest, note block...).</summary>
    Task<ActionResult> ActivateAsync(BlockPos pos, CancellationToken ct);
    Task<ActionResult> LookAtAsync(Vec3d point, CancellationToken ct);
    /// <summary>Uses the held item, or <paramref name="itemId"/> after selecting it, in the current look direction
    /// (0x0F direction -1): eat, buckets (server-side raytrace), boats, snowballs, eggs, fishing rod, bow draw.</summary>
    Task<ActionResult> UseItemAsync(int? itemId, CancellationToken ct);
    Task<ActionResult> EatAsync(CancellationToken ct);
    Task<ActionResult> CollectDropsAsync(double radius, CancellationToken ct, bool dryOnly = false);
    Task<ActionResult> MineAsync(string blockName, int count, double radius, CancellationToken ct);
    Task<ActionResult> CraftAsync(string itemName, int count, CancellationToken ct);
    Task<ActionResult> SmeltAsync(int inputItemId, int count, int? fuelItemId, bool wait, BlockPos? furnace, CancellationToken ct);
    Task<ActionResult> CollectFurnaceAsync(BlockPos? furnace, CancellationToken ct);
    Task<ActionResult> OpenContainerAsync(BlockPos pos, CancellationToken ct);
    Task<ActionResult> CloseWindowAsync(CancellationToken ct);
    Task<ActionResult> ClickSlotAsync(int slot, bool rightClick, bool shift, CancellationToken ct);
    Task<ActionResult> DepositAllAsync(BlockPos? container, Func<ItemStack, bool>? filter, CancellationToken ct);
    Task<ActionResult> WithdrawAsync(int itemId, int count, BlockPos? container, int? damage, CancellationToken ct);
    Task<ActionResult> SendChatAsync(string text, CancellationToken ct);
    Task WaitTicksAsync(int ticks, CancellationToken ct);
    /// <summary>Waits for a world condition (block change, entity, inventory). False on timeout.</summary>
    Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct);

    // ---- optional primitives (see Supported) ----
    Task<ActionResult> UseOnBlockAsync(BlockPos pos, BlockFace face, int itemId, CancellationToken ct);
    Task<ActionResult> UseEntityAsync(int entityId, int? itemId, CancellationToken ct);
    Task<ActionResult> WriteSignAsync(BlockPos pos, string[] lines, CancellationToken ct);
    Task<ActionResult> ReleaseUseAsync(CancellationToken ct);
    Task<ActionResult> DropHeldAsync(bool wholeStack, CancellationToken ct);

    /// <summary>Replaces the local inventory model with the server's (closes any window). Used after tossing so the count
    /// reported (and the inventory ledger) is the server's, not a prediction. Default: unsupported (fakes keep their model).</summary>
    Task<ActionResult> ResyncInventoryAsync(CancellationToken ct) => Task.FromResult(ActionResult.Fail("inventory resync unsupported"));

    // ---- face-aware placement, bow, vehicles (defaults compose the members above) ----

    /// <summary>Places <paramref name="itemId"/> into <paramref name="pos"/> by clicking <paramref name="face"/> of the
    /// neighbour at pos − normal(face): Top = on the block below (sign post), horizontal = hung on that wall (wall sign,
    /// torch, ladder, button, lever, trapdoor). Default: plain <see cref="PlaceAsync(BlockPos,int,CancellationToken)"/>.</summary>
    Task<ActionResult> PlaceAsync(BlockPos pos, int itemId, BlockFace face, CancellationToken ct) => PlaceAsync(pos, itemId, ct);

    /// <summary>The face to click to hang a wall-mounted block in <paramref name="pos"/> on a solid, non-interactable
    /// horizontal neighbour (<paramref name="exclude"/> vetoes walls), or null.</summary>
    BlockFace? WallFace(BlockPos pos, Func<BlockPos, bool>? exclude = null) =>
        FaceAim.WallFace(World, pos, World.Player.EyePosition, Reach, exclude);

    /// <summary>Draw the bow <paramref name="chargeTicks"/> (20 = full), aim with gravity at <paramref name="target"/>
    /// (tracking <paramref name="entityId"/> when given) and release. Default: use + wait + look + release.</summary>
    async Task<ActionResult> ShootAsync(Vec3d target, int? entityId, int chargeTicks, CancellationToken ct)
    {
        var use = await UseItemAsync(Items.Bow, ct).ConfigureAwait(false);
        if (!use.Ok) return use;
        await WaitTicksAsync(chargeTicks, ct).ConfigureAwait(false);
        var at = entityId is { } id && World.Entities.Get(id) is { } e ? e.Position + new Vec3d(0, 0.9, 0) : target;
        double speed = MineClaw.Client.Combat.ArrowBallistics.Speed(chargeTicks);
        var aim = MineClaw.Client.Combat.ArrowBallistics.Solve(World.Player.EyePosition, at, speed);
        if (aim is null) return ActionResult.Fail("target out of bow range");
        var dir = PlayerPhysics.LookVector(aim.Value.Yaw, aim.Value.Pitch);
        await LookAtAsync(World.Player.EyePosition + dir * 10, ct).ConfigureAwait(false);
        return await ReleaseUseAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Entity id of the vehicle I'm riding, or −1.</summary>
    int RidingEntityId => -1;

    /// <summary>Get out of the boat/minecart. Default: right-click it again (b1.8.1 mounting toggles).</summary>
    Task<ActionResult> DismountAsync(CancellationToken ct) =>
        RidingEntityId < 0 ? Task.FromResult(ActionResult.Success("not riding anything")) : UseEntityAsync(RidingEntityId, null, ct);
}
