using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Placing a block the way a design has it turned (docs/web/story-mode.md "Rotation"): the furnace's front, the stairs' rise and
/// half, the log's axis, the torch's wall. <see cref="BlockOrientation"/> says which clicks give the wanted meta from where the body
/// stands; the body clicks one from here, or walks to a stand that has one (a piston pointing up wants the eyes over it, one pointing
/// down wants them under it), says the yaw right before the click and sends the cursor. The world's meta is then checked: a wrong
/// one is taken back and placed once more from another stand when the block can be dug for its drop, else the action fails with
/// "placed with wrong rotation" (the block stays; the colony shows the cell).
/// </summary>
public sealed partial class MinecraftClient
{
    /// <summary>Places <paramref name="itemId"/> at <paramref name="pos"/> so the block there gets <paramref name="meta"/>'s facing,
    /// axis or half. A block whose meta the click doesn't decide is placed as by <see cref="PlaceBlockAsync"/>.</summary>
    public Task<ActionResult> PlaceTurnedAsync(BlockPos pos, int itemId, int? damage, int meta, CancellationToken ct = default, bool exact = false) =>
        RunActionAsync("place", ct, c => PlaceTurnedCoreAsync(pos, itemId, damage, meta, c, exact));

    /// <summary>A construction access session keeps its footing: place only against a visible face from here, including the
    /// requested orientation, without walking away or trying another stand after a rejected rotation.</summary>
    public Task<ActionResult> PlaceHereAsync(BlockPos pos, int itemId, int? damage = null, int? meta = null, CancellationToken ct = default, bool exact = false) =>
        RunActionAsync("place", ct, async c =>
        {
            int block = BlockOrientation.BlockOf(itemId);
            if (exact)
            {
                if (meta is not { } wanted) return ActionResult.Fail("exact placement needs block metadata");
                if (ExactPlaceRefusal(block, wanted, damage) is { } no) return ActionResult.Fail(no);
                damage ??= BlockOrientation.ExactDamage(block, wanted, Modern);
            }
            if (meta is not { } turn || !BlockOrientation.Turns(block, turn, Modern))
            {
                var plain = await PlaceCoreAsync(pos, itemId, c, allowPathing: false, damage: damage).ConfigureAwait(false);
                return exact ? await ExactPlaceVerdictAsync(plain, pos, block, meta!.Value, c).ConfigureAwait(false) : plain;
            }
            if (PlaceCellRefusal(pos, itemId, damage) is { } refused) return ActionResult.Fail(refused);
            if (TurnPlaceRefusal(pos, itemId, damage) is { } unavailable) return ActionResult.Fail(unavailable);
            if (PickTurn(pos, block, turn, Modern, World.Player.Position, visibleOnly: true) is not { } pick)
                return ActionResult.Fail($"can't place {ItemName(itemId)} at {Fmt(pos)}: no visible support face for the requested rotation from here");
            var placed = await PlaceAgainstCoreAsync(pos, pick.Face, itemId, damage, pick.Yaw, allowHidden: false, c, cursor: pick.Cursor).ConfigureAwait(false);
            if (!placed.Ok) return placed;
            if (exact) return await ExactPlaceVerdictAsync(placed, pos, block, turn, c).ConfigureAwait(false);
            bool correct = await WaitUntilAsync(() => World.GetBlock(pos) == block && BlockOrientation.Same(block, turn, World.GetMeta(pos), Modern),
                TimeSpan.FromMilliseconds(400), c).ConfigureAwait(false);
            return correct ? placed : ActionResult.Fail($"placed with wrong rotation at {Fmt(pos)}: wants {BlockOrientation.Describe(block, turn, Modern)}");
        });

    private bool Modern => Options.Profile.Data.VersionId != "b1.8.1";

    private async Task<ActionResult> PlaceTurnedCoreAsync(BlockPos pos, int itemId, int? damage, int meta, CancellationToken ct, bool exact = false)
    {
        int block = BlockOrientation.BlockOf(itemId);
        bool modern = Modern;
        if (exact)
        {
            if (ExactPlaceRefusal(block, meta, damage) is { } no) return ActionResult.Fail(no);
            damage ??= BlockOrientation.ExactDamage(block, meta, modern);
            var placed = BlockOrientation.Turns(block, meta, modern)
                ? await PlaceTurnedOnceAsync(pos, itemId, damage, block, meta, modern, avoidHere: false, ct).ConfigureAwait(false)
                : await PlaceCoreAsync(pos, itemId, ct, damage: damage).ConfigureAwait(false);
            // A later edit may have replaced it: report the mismatch without digging a cell the job no longer owns.
            return await ExactPlaceVerdictAsync(placed, pos, block, meta, ct).ConfigureAwait(false);
        }
        if (!BlockOrientation.Turns(block, meta, modern)) return await PlaceCoreAsync(pos, itemId, ct, damage: damage).ConfigureAwait(false);
        string want = BlockOrientation.Describe(block, meta, modern) ?? $"meta {meta}";
        for (int attempt = 0; ; attempt++)
        {
            var r = await PlaceTurnedOnceAsync(pos, itemId, damage, block, meta, modern, avoidHere: attempt > 0, ct).ConfigureAwait(false);
            if (!r.Ok) return r;
            // 1.12.2 chests and furnaces get their facing from onBlockPlacedBy, a second block change right after the first.
            if (await WaitUntilAsync(() => World.GetBlock(pos) == block && BlockOrientation.Same(block, meta, World.GetMeta(pos), modern),
                    TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false))
                return ActionResult.Success($"placed {ItemName(itemId, damage ?? 0)} at {Fmt(pos)} {want}");
            if (await TakeBackWrongTurnAsync(pos, itemId, damage, block, modern, want, attempt, ct).ConfigureAwait(false) is { } failed) return failed;
        }
    }

    private string? ExactPlaceRefusal(int block, int meta, int? damage)
    {
        if (BlockOrientation.ExactPlacementRefusal(block, meta, Modern) is { } no) return "unsupported exact state: " + no;
        return damage is { } variant && variant != BlockOrientation.ExactDamage(block, meta, Modern)
            ? "the requested item variant does not match the exact block metadata" : null;
    }

    private async Task<ActionResult> ExactPlaceVerdictAsync(ActionResult placed, BlockPos at, int block, int meta, CancellationToken ct)
    {
        if (!placed.Ok) return placed;
        bool correct = await WaitUntilAsync(() => World.GetBlock(at) == block && World.GetMeta(at) == meta,
            TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
        return correct ? placed : ActionResult.Fail($"placed with wrong exact state at {Fmt(at)}: wants {block}:{meta}, got {World.GetBlock(at)}:{World.GetMeta(at)}");
    }

    /// <summary>A placement that came out turned wrong: the first time (when its drop can be had) it is dug and collected for another
    /// try (null); otherwise the failure.</summary>
    private async Task<ActionResult?> TakeBackWrongTurnAsync(BlockPos pos, int itemId, int? damage, int block, bool modern, string want, int attempt,
        CancellationToken ct)
    {
        int got = World.GetMeta(pos), id = World.GetBlock(pos);
        string wrong = $"placed with wrong rotation at {Fmt(pos)}: {BlockOrientation.Describe(block, got, modern) ?? $"meta {got}"}, wants {want}";
        if (id != block) return ActionResult.Fail($"{ItemName(itemId)} at {Fmt(pos)} became {BlockName(id, got)}");
        if (attempt > 0 || !CanTakeBack(block)) return ActionResult.Fail(wrong);
        Log($"place: {wrong}; taking it back to place it again");
        var d = await DigCoreAsync(pos, ct, requested: true).ConfigureAwait(false);
        if (!d.Ok || World.GetBlock(pos) == block) return ActionResult.Fail($"{wrong} (couldn't take it back: {d.Message})");
        if (World.Inventory.CountItem(itemId, damage) == 0)
            await CollectDropsCoreAsync(4, 8, ct, around: new Vec3d(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5)).ConfigureAwait(false);
        return World.Inventory.CountItem(itemId, damage) == 0 ? ActionResult.Fail($"{wrong} (took it back, but its drop is gone)") : null;
    }

    /// <summary>Whether a wrong one may be dug and placed again: it drops itself with something carried (a furnace wants a pickaxe).</summary>
    private bool CanTakeBack(int block)
    {
        if (HarvestNeed(block) is null) return true;
        foreach (var s in World.Inventory.PlayerSlots())
            if (!s.IsEmpty && Tools.CanHarvest(block, s.Id)) return true;
        return false;
    }

    /// <summary>One placement with the click <see cref="BlockOrientation"/> asks for, from here or from a stand that has one
    /// (<paramref name="avoidHere"/>: a retry goes elsewhere first).</summary>
    private async Task<ActionResult> PlaceTurnedOnceAsync(BlockPos pos, int itemId, int? damage, int block, int meta, bool modern, bool avoidHere,
        CancellationToken ct)
    {
        if (TurnPlaceRefusal(pos, itemId, damage) is { } refusal) return ActionResult.Fail(refusal);
        string want = BlockOrientation.Describe(block, meta, modern) ?? $"meta {meta}";
        var here = World.Player.Position;
        var hereCell = new BlockPos((int)Math.Floor(here.X), (int)Math.Floor(here.Y + 1e-6), (int)Math.Floor(here.Z));
        var pick = avoidHere ? null : PickTurn(pos, block, meta, modern, here);
        if (pick is null)
        {
            var (walked, noSpot) = await WalkToTurnStandAsync(pos, itemId, block, meta, modern, hereCell, avoidHere, want, ct).ConfigureAwait(false);
            if (noSpot is not null) return ActionResult.Fail(noSpot);
            pick = walked;
        }
        var t = pick!.Value;
        return await PlaceAgainstCoreAsync(pos, t.Face, itemId, damage, t.Yaw, allowHidden: true, ct, cursor: t.Cursor).ConfigureAwait(false);
    }

    /// <summary>Why nothing can be placed at <paramref name="pos"/> (none held, unloaded, occupied), or null.</summary>
    private string? TurnPlaceRefusal(BlockPos pos, int itemId, int? damage)
    {
        if (World.Inventory.CountItem(itemId, damage) == 0) return $"no {ItemName(itemId, damage ?? 0)} in inventory";
        if (!World.IsLoaded(pos.X, pos.Z)) return $"{Fmt(pos)} is not loaded";
        int existing = World.GetBlock(pos);
        return Aiming.IsReplaceable(World, existing) ? null : $"{Fmt(pos)} is occupied by {BlockName(existing, World.GetMeta(pos))}";
    }

    /// <summary>Walks to the first stand that has the click: the click, or why there is none.</summary>
    private async Task<(Turn? Pick, string? Fail)> WalkToTurnStandAsync(BlockPos pos, int itemId, int block, int meta, bool modern, BlockPos hereCell,
        bool avoidHere, string want, CancellationToken ct)
    {
        var stands = TurnStands(pos, block, meta, modern, hereCell).ToList();
        if (stands.Count == 0)
            return (null, avoidHere ? $"placed with wrong rotation at {Fmt(pos)}: no other spot to place it {want} from"
                : $"can't place {ItemName(itemId)} at {Fmt(pos)} {want}: no spot within reach to click it from");
        foreach (var s in stands)
        {
            var g = await GotoCoreAsync(new GoalBlock(s), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(20), AllowPartial = false }, ct)
                .ConfigureAwait(false);
            if (!g.Ok) continue;
            if (PickTurn(pos, block, meta, modern, World.Player.Position) is { } pick) return (pick, null);
        }
        return (null, $"can't place {ItemName(itemId)} at {Fmt(pos)} {want}: couldn't get to a spot to click it from");
    }

    /// <summary>The click from feet at <paramref name="feet"/> that gives the meta, on a face in reach (a face in view first, then the
    /// nearest); null = none from here.</summary>
    private Turn? PickTurn(BlockPos pos, int block, int meta, bool modern, Vec3d feet, bool visibleOnly = false)
    {
        var eye = new Vec3d(feet.X, feet.Y + PlayerPhysics.EyeHeight, feet.Z);
        if (AABB.ForPlayer(feet).Intersects(AABB.Block(pos.X, pos.Y, pos.Z))) return null;
        Turn? best = null;
        double bestScore = double.MaxValue;
        foreach (var t in BlockOrientation.Solve(block, meta, pos, feet, modern, f => Clickable(pos, f), sneaking: true))
        {
            var against = pos - t.Face.Normal();
            var point = FaceAim.FaceCenter(World, against, t.Face);
            double d = eye.DistanceTo(point);
            // The client's own reach to the face; the server's: feet within 8 of the clicked block's centre.
            if (d > Options.Reach || feet.DistanceTo(against.Center) >= 8) continue;
            bool visible = FaceAim.Find(World, eye, against, t.Face, Options.Reach) is not null;
            if (visibleOnly && !visible) continue;
            double score = d + (visible ? 0 : 10);
            if (score < bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    /// <summary>A face that takes a placement click, including 1.12.2 containers when sneaking.</summary>
    private bool Clickable(BlockPos pos, BlockFace face)
    {
        var a = pos - face.Normal();
        if (!World.IsLoaded(a.X, a.Z)) return false;
        return PlaceRules.IsSupport(World, a, allowSneakInteraction: true);
    }

    /// <summary>Stand cells near <paramref name="pos"/> (feet and head open, ground under, not in the cell) from which a click gives the
    /// meta: in view first, then the nearest to <paramref name="from"/>; never <paramref name="from"/> itself.</summary>
    /// <summary>Loaded, feet and head open and dry, on dry ground.</summary>
    private bool IsOpenStand(BlockPos s)
    {
        if (!World.IsLoaded(s.X, s.Z)) return false;
        if (BlockShapes.HasCollision(World, s.X, s.Y, s.Z) || BlockShapes.HasCollision(World, s.X, s.Y + 1, s.Z)) return false;
        if (BlockShapes.IsLiquid(World.GetBlock(s)) || BlockShapes.IsLiquid(World.GetBlock(s.X, s.Y + 1, s.Z))) return false;
        return BlockShapes.HasCollision(World, s.X, s.Y - 1, s.Z) && !BlockShapes.IsLiquid(World.GetBlock(s.X, s.Y - 1, s.Z));
    }

    private IEnumerable<BlockPos> TurnStands(BlockPos pos, int block, int meta, bool modern, BlockPos from)
    {
        var list = new List<(BlockPos S, double Score)>();
        for (int dy = -3; dy <= 2; dy++)
        for (int dx = -4; dx <= 4; dx++)
        for (int dz = -4; dz <= 4; dz++)
        {
            var s = new BlockPos(pos.X + dx, pos.Y + dy, pos.Z + dz);
            if (s == from || !IsOpenStand(s)) continue;
            var feet = new Vec3d(s.X + 0.5, s.Y, s.Z + 0.5);
            if (PickTurn(pos, block, meta, modern, feet) is not { } t) continue;
            var eye = new Vec3d(feet.X, feet.Y + PlayerPhysics.EyeHeight, feet.Z);
            bool seen = FaceAim.Find(World, eye, pos - t.Face.Normal(), t.Face, Options.Reach) is not null;
            list.Add((s, Math.Sqrt(s.DistanceSquaredTo(from)) + (seen ? 0 : 6)));
        }
        return list.OrderBy(x => x.Score).Take(4).Select(x => x.S);
    }
}
