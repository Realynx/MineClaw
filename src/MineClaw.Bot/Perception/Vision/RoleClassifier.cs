using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Perception.Vision;

/// <summary>
/// The semantic role of a block (vision v2): what it means for the bot's work. Reuses the protection policy's verdicts
/// (<see cref="Protection.Check"/>: hard never-break, soft reasons), the world index (unknown builds, floors, portals,
/// generator walls), generator dig cells (taught, regen-confirmed or by geometry) and the placed-block log. Every source is
/// an optional delegate, so tests and the eval build one from a bare world. Precedence, highest first: never-break > gen cell
/// > portal > door > station > water/lava > crop (ripe/growing, never protected) > own placed > player build > protected
/// (never farm soil) > floor/wall > void.
/// </summary>
public sealed class RoleClassifier
{
    public required IBlockAccess Blocks { get; init; }
    /// <summary>The protection verdict of a block (no telemetry): <see cref="Protection.Check"/>.</summary>
    public Func<BlockPos, int, ProtectionVerdict>? Verdict { get; init; }
    /// <summary>Generator dig cells (taught or regen-confirmed); geometry (lava + water next to it) is checked too.</summary>
    public Func<BlockPos, bool>? IsGenCell { get; init; }
    /// <summary>Generator cells by geometry (<see cref="BlockDescriber.IsGenCell"/>); null = skip.</summary>
    public Func<BlockPos, bool>? IsGenGeometry { get; init; }
    /// <summary>Blocks a hive bot placed itself (id-checked): always minable.</summary>
    public Func<BlockPos, int, bool>? IsOwnPlaced { get; init; }
    /// <summary>World index kind protecting a cell (unknown_structure, cobble_generator, portal, floor...).</summary>
    public Func<BlockPos, string?>? IndexerKind { get; init; }
    /// <summary>Air a hostile can spawn in even by day (the light model's spawn rule at World.k 0); null = no dark overlay.</summary>
    public Func<BlockPos, bool>? IsDarkSpawn { get; init; }

    public RoleMap Map => RoleOf;

    /// <summary>Builds the classifier for a live bot from the same sources the protection policy uses.</summary>
    public static RoleClassifier For(WorldState world, Protection? protection, Func<BlockPos, bool>? digCells, Func<BlockPos, string?>? indexerKind) => new()
    {
        Blocks = world.PlanningView,
        Verdict = protection == null ? null : protection.Check,
        IsGenCell = digCells,
        IsGenGeometry = p => BlockDescriber.IsGenCell(world, p),
        IsOwnPlaced = (p, id) => world.Placed.IsPlaced(p, world.Dimension, id),
        IndexerKind = indexerKind,
        // Shade a mob can spawn in by day ('!'): the persistent dark spots (night darkens every open floor, so it isn't drawn).
        IsDarkSpawn = p => SpawnRules.Check(world, p, 0) == SpawnBlock.Spawnable,
    };

    public BlockRole RoleOf(int x, int y, int z)
    {
        if ((uint)y >= Blocks.WorldHeight || !Blocks.IsLoaded(x, z)) return BlockRole.None;
        var p = new BlockPos(x, y, z);
        int id = Blocks.GetBlock(x, y, z);
        bool gen = Safe(() => IsGenCell?.Invoke(p) == true) || (id is MineClaw.Data.Blocks.Cobblestone or MineClaw.Data.Blocks.Stone or 0
            && Safe(() => IsGenGeometry?.Invoke(p) == true));
        if (id == 0)
        {
            // Air between lava and water is a refilling gen cell only when taught/confirmed or not stacked on one: the air
            // over a dual gen's cells (lava and water at y+1 too, live 10-27) is not a place to mine.
            if (gen && !Safe(() => IsGenCell?.Invoke(p) == true))
            {
                var down = new BlockPos(x, y - 1, z);
                int below = Blocks.GetBlock(x, y - 1, z);
                if (Safe(() => IsGenCell?.Invoke(down) == true)
                    || (below is MineClaw.Data.Blocks.Cobblestone or MineClaw.Data.Blocks.Stone && Safe(() => IsGenGeometry?.Invoke(down) == true)))
                    gen = false;
            }
            if (gen) return BlockRole.GenCell;
            if (Safe(() => IsDarkSpawn?.Invoke(p) == true)) return BlockRole.DarkSpawn;
            for (int yy = y - 1; yy >= 0; yy--) if (Blocks.GetBlock(x, yy, z) != 0) return BlockRole.None;
            return BlockRole.Void;
        }
        ProtectionVerdict? v = null;
        try { v = Verdict?.Invoke(p, id); } catch (Exception) { }
        if (v is { Level: ProtectionLevel.Hard }) return BlockRole.NeverBreak;
        if (gen) return BlockRole.GenCell;
        string? kind = null;
        try { kind = IndexerKind?.Invoke(p); } catch (Exception) { }
        if (id == MineClaw.Data.Blocks.Portal || (id == MineClaw.Data.Blocks.Obsidian && (kind == WorldIndex.IndexKinds.Portal || NextToPortal(x, y, z))))
            return BlockRole.Portal;
        if (id is MineClaw.Data.Blocks.WoodenDoor or MineClaw.Data.Blocks.IronDoor or MineClaw.Data.Blocks.Trapdoor) return BlockRole.Door;
        if (WorldIndex.IndexKinds.StationKind(id) != null) return BlockRole.Station;
        if (MineClaw.Data.Blocks.IsWater(id)) return BlockRole.Water;
        if (MineClaw.Data.Blocks.IsLava(id)) return BlockRole.Lava;
        // Crops are work, not property (live 10-28: a field drawn '$' read as "protected, owned by others, needs permission"):
        // ripe/growing from the block's own metadata, whatever box, index entry, claim or planter covers them.
        if (CropRole(id, Blocks.GetMeta(x, y, z)) is { } crop) return crop;
        if (Safe(() => IsOwnPlaced?.Invoke(p, id) == true)) return BlockRole.OwnPlaced;
        if (kind == WorldIndex.IndexKinds.UnknownStructure) return BlockRole.PlayerBuild;
        bool solid = Blocks.Info(id).Solid;
        // Farm soil is floor to walk on and plant into: its "don't break the farmland" rule lives in the policy, not the overlay.
        // A hive lease is coordination (another bot works there), not protection: no '$' for it either.
        if (v is { Protected: true, Reason: not ("floor" or ProtectionText.ClaimedReason) } && kind != WorldIndex.WorldIndexer.FloorKind
            && id != MineClaw.Data.Blocks.Farmland)
            return BlockRole.SoftProtected;
        if (!solid) return BlockRole.None;
        int above = Blocks.GetBlock(x, y + 1, z);
        if (above == 0 || !Blocks.Info(above).Solid) return BlockRole.Floor;
        // The island floor under a gen cell is still floor (the cell on top is the thing to mine, not this).
        var up = new BlockPos(x, y + 1, z);
        if (Safe(() => IsGenCell?.Invoke(up) == true) || (above is MineClaw.Data.Blocks.Cobblestone or MineClaw.Data.Blocks.Stone && Safe(() => IsGenGeometry?.Invoke(up) == true)))
            return BlockRole.Floor;
        return BlockRole.Wall;
    }

    /// <summary>The crop role of a block (wheat by its growth stage, meta 7 = ripe; stems grow, fruit/cane/cactus are ripe), else null.
    /// Saplings are not crops (tree stewardship keeps them).</summary>
    public static BlockRole? CropRole(int id, int meta) => id switch
    {
        MineClaw.Data.Blocks.Crops => meta >= 7 ? BlockRole.CropRipe : BlockRole.CropGrowing,
        MineClaw.Data.Blocks.PumpkinStem or MineClaw.Data.Blocks.MelonStem => BlockRole.CropGrowing,
        MineClaw.Data.Blocks.Pumpkin or MineClaw.Data.Blocks.Melon or MineClaw.Data.Blocks.SugarCane or MineClaw.Data.Blocks.Cactus => BlockRole.CropRipe,
        _ => null,
    };

    /// <summary>"crop (ripe 7/7)", "crop (growing 3/7)": the words for a crop in facts and answers (null = not a crop).</summary>
    public static string? CropText(int id, int meta) => id switch
    {
        MineClaw.Data.Blocks.Crops => meta >= 7 ? "crop (ripe 7/7)" : $"crop (growing {Math.Clamp(meta, 0, 6)}/7)",
        MineClaw.Data.Blocks.PumpkinStem or MineClaw.Data.Blocks.MelonStem => "crop stem (leave it; harvest the fruit next to it)",
        _ when CropRole(id, meta) == BlockRole.CropRipe => "crop (ripe)",
        _ => null,
    };

    private bool NextToPortal(int x, int y, int z)
    {
        foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
            if (Blocks.GetBlock(x + dx, y + dy, z + dz) == MineClaw.Data.Blocks.Portal) return true;
        return false;
    }

    private static bool Safe(Func<bool> f)
    {
        try { return f(); } catch (Exception) { return false; }
    }
}
