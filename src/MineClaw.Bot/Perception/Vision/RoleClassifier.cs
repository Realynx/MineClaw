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
        // Geometry names gen cells only where stone comes from generators (skyblock); a normal world's caves have lava by water
        // everywhere, so there only taught/indexed (regen-confirmed) cells are gen cells.
        bool geometry = MineClaw.Data.Versioning.WorldMode.Current.GeneratorStone;
        bool gen = Safe(() => IsGenCell?.Invoke(p) == true) || (geometry && id is MineClaw.Data.Blocks.Cobblestone or MineClaw.Data.Blocks.Stone or 0
            && Safe(() => IsGenGeometry?.Invoke(p) == true));
        return id == 0 ? AirRole(p, gen, geometry) : BlockRoleOf(p, id, gen, geometry);
    }

    private BlockRole AirRole(BlockPos p, bool gen, bool geometry)
    {
        // Air between lava and water is a refilling gen cell only when taught/confirmed or not stacked on one: the air
        // over a dual gen's cells (lava and water at y+1 too, live 10-27) is not a place to mine.
        if (gen && !Safe(() => IsGenCell?.Invoke(p) == true)) gen = !OnGenCell(p.Down(), geometry);
        if (gen) return BlockRole.GenCell;
        if (Safe(() => IsDarkSpawn?.Invoke(p) == true)) return BlockRole.DarkSpawn;
        for (int yy = p.Y - 1; yy >= 0; yy--) if (Blocks.GetBlock(p.X, yy, p.Z) != 0) return BlockRole.None;
        return MineClaw.Data.Versioning.WorldMode.Current.HasVoidBelow ? BlockRole.Void : BlockRole.None;
    }

    /// <summary>A taught/confirmed gen cell, or (by geometry) cobble/stone with lava and water next to it.</summary>
    private bool OnGenCell(BlockPos q, bool geometry)
    {
        int id = Blocks.GetBlock(q.X, q.Y, q.Z);
        return Safe(() => IsGenCell?.Invoke(q) == true)
               || (geometry && id is MineClaw.Data.Blocks.Cobblestone or MineClaw.Data.Blocks.Stone && Safe(() => IsGenGeometry?.Invoke(q) == true));
    }

    private BlockRole BlockRoleOf(BlockPos p, int id, bool gen, bool geometry)
    {
        var v = VerdictOf(p, id);
        if (v is { Level: ProtectionLevel.Hard }) return BlockRole.NeverBreak;
        if (gen) return BlockRole.GenCell;
        string? kind = null;
        try { kind = IndexerKind?.Invoke(p); } catch (Exception) { }
        if (IsPortalPart(p, id, kind)) return BlockRole.Portal;
        if (FixedRole(id) is { } fixedRole) return fixedRole;
        // Crops are work, not property (live 10-28: a field drawn '$' read as "protected, owned by others, needs permission"):
        // ripe/growing from the block's own metadata, whatever box, index entry, claim or planter covers them.
        if (CropRole(id, Blocks.GetMeta(p.X, p.Y, p.Z)) is { } crop) return crop;
        if (Safe(() => IsOwnPlaced?.Invoke(p, id) == true)) return BlockRole.OwnPlaced;
        if (kind == WorldIndex.IndexKinds.UnknownStructure) return BlockRole.PlayerBuild;
        return GroundRole(p, id, v, kind, geometry);
    }

    private ProtectionVerdict? VerdictOf(BlockPos p, int id)
    {
        try { return Verdict?.Invoke(p, id); } catch (Exception) { return null; }
    }

    /// <summary>A portal sheet, or obsidian of its frame (indexed as a portal, or beside a sheet).</summary>
    private bool IsPortalPart(BlockPos p, int id, string? kind) =>
        id == MineClaw.Data.Blocks.Portal || (id == MineClaw.Data.Blocks.Obsidian && (kind == WorldIndex.IndexKinds.Portal || NextToPortal(p.X, p.Y, p.Z)));

    /// <summary>Doors, stations and fluids: their role whatever covers them.</summary>
    private static BlockRole? FixedRole(int id)
    {
        if (Openables.IsDoor(id) || Openables.IsTrapdoor(id)) return BlockRole.Door;
        if (WorldIndex.IndexKinds.StationKind(id) != null) return BlockRole.Station;
        if (MineClaw.Data.Blocks.IsWater(id)) return BlockRole.Water;
        if (MineClaw.Data.Blocks.IsLava(id)) return BlockRole.Lava;
        return null;
    }

    /// <summary>Soft-protected, floor or wall (or nothing for a non-solid block).</summary>
    private BlockRole GroundRole(BlockPos p, int id, ProtectionVerdict? v, string? kind, bool geometry)
    {
        bool solid = Blocks.Info(id).Solid;
        // Farm soil is floor to walk on and plant into: its "don't break the farmland" rule lives in the policy, not the overlay.
        // A hive lease is coordination (another bot works there), not protection: no '$' for it either.
        if (v is { Protected: true, Reason: not ("floor" or ProtectionText.ClaimedReason) } && kind != WorldIndex.WorldIndexer.FloorKind
            && id != MineClaw.Data.Blocks.Farmland)
            return BlockRole.SoftProtected;
        if (!solid) return BlockRole.None;
        int above = Blocks.GetBlock(p.X, p.Y + 1, p.Z);
        if (above == 0 || !Blocks.Info(above).Solid) return BlockRole.Floor;
        // The island floor under a gen cell is still floor (the cell on top is the thing to mine, not this).
        return OnGenCell(p.Up(), geometry) ? BlockRole.Floor : BlockRole.Wall;
    }

    /// <summary>The crop role of a block (wheat by its growth stage, meta 7 = ripe; stems grow, fruit/cane/cactus are ripe), else null.
    /// Saplings are not crops (tree stewardship keeps them).</summary>
    public static BlockRole? CropRole(int id, int meta) => id switch
    {
        MineClaw.Data.Blocks.Crops or Ids1122.Carrots or Ids1122.Potatoes or Ids1122.Beetroots or Ids1122.NetherWart =>
            meta >= Ids1122.RipeStage(id) ? BlockRole.CropRipe : BlockRole.CropGrowing,
        MineClaw.Data.Blocks.PumpkinStem or MineClaw.Data.Blocks.MelonStem => BlockRole.CropGrowing,
        MineClaw.Data.Blocks.Pumpkin or MineClaw.Data.Blocks.Melon or MineClaw.Data.Blocks.SugarCane or MineClaw.Data.Blocks.Cactus => BlockRole.CropRipe,
        _ => null,
    };

    /// <summary>"crop (ripe 7/7)", "crop (growing 3/7)": the words for a crop in facts and answers (null = not a crop).</summary>
    public static string? CropText(int id, int meta) => id switch
    {
        MineClaw.Data.Blocks.Crops or Ids1122.Carrots or Ids1122.Potatoes or Ids1122.Beetroots or Ids1122.NetherWart
            when Ids1122.RipeStage(id) is { } ripe =>
            meta >= ripe ? $"crop (ripe {ripe}/{ripe})" : $"crop (growing {Math.Clamp(meta, 0, ripe - 1)}/{ripe})",
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
