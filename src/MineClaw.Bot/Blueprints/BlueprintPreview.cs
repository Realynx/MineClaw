using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>The world as it would be with the plan built: planned blocks and forced air over the real blocks.</summary>
public sealed class PlannedWorld(IBlockAccess inner, VoxelPlan plan) : IBlockAccess
{
    public VoxelPlan Plan => plan;
    public int GetBlock(int x, int y, int z) => plan.Final(inner, new BlockPos(x, y, z));
    public int GetMeta(int x, int y, int z) => plan.FinalMeta(inner, new BlockPos(x, y, z));
    public bool IsLoaded(int x, int z) => inner.IsLoaded(x, z) || (x >= plan.Min.X && x <= plan.Max.X && z >= plan.Min.Z && z <= plan.Max.Z);
    public BlockInfo Info(int id) => inner.Info(id);
    public MineClaw.World.Versioning.IPhysicsRules Physics => inner.Physics;
}

/// <summary>Light/texture voxels with the plan built (light of the real world; planned cells read as their blocks).</summary>
public sealed class PlannedVoxels(IVoxelWorld? inner, IBlockAccess blocks, VoxelPlan plan) : IVoxelWorld
{
    public int Block(int x, int y, int z) => plan.Final(blocks, new BlockPos(x, y, z));
    public int Meta(int x, int y, int z) => plan.FinalMeta(blocks, new BlockPos(x, y, z));
    public int SkyLight(int x, int y, int z) =>
        plan.Blocks.ContainsKey(new BlockPos(x, y, z)) ? 0 : inner?.SkyLight(x, y, z) is { } s and >= 0 ? s : 15;
    public int BlockLight(int x, int y, int z) => inner?.BlockLight(x, y, z) is { } b and >= 0 ? b : 0;
    public ColumnState State(int x, int z) => plan.Inside(new BlockPos(x, plan.Min.Y, z)) ? ColumnState.Live : inner?.State(x, z) ?? ColumnState.Live;
    public int WorldHeight => blocks.WorldHeight;
}

/// <summary>A rendered design preview: the PNG, its size, legend and the cache key it was made under.</summary>
public sealed record DesignPicture(byte[] Png, int Width, int Height, string Legend, ulong Key, VisionImage Sheet)
{
    public int Tokens => (Width * Height + 749) / 750;
}

/// <summary>
/// Renders a blueprint IN its real surroundings with the existing contact sheet: two 3D views (ISO from the SW, ORBIT from the NE:
/// the freecam renderer) + the flat TOP map of the site, planned blocks
/// drawn with the "planned" role (magenta, '*'), roofs not cut away, the build's box outlined and named. Cached by (plan
/// hash, surroundings hash) so revisions and the supervisor viewer re-use renders.
/// </summary>
public sealed class BlueprintPreview(ContactSheet? sheets = null)
{
    public static readonly IReadOnlyList<string> Views = ["iso", "orbit3", "top"];
    private readonly ContactSheet _sheets = sheets ?? new ContactSheet(new VisionRenderer { CacheCapacity = 12 }) { EyeThreads = 1 };
    private readonly LinkedList<DesignPicture> _cache = new();
    private readonly object _lock = new();
    public int Renders { get; private set; }
    public int CacheHits { get; private set; }

    public static int RadiusFor(VoxelPlan plan) =>
        Math.Clamp((Math.Max(plan.Max.X - plan.Min.X, plan.Max.Z - plan.Min.Z) + 1) / 2 + 3, 3, 12);

    /// <summary>The look's focus: footprint centre, one above the bottom so ISO/SIDE (4 up, 5 down) cover the whole height.</summary>
    public static (int X, int Y, int Z) FocusFor(VoxelPlan plan)
    {
        int h = plan.Max.Y - plan.Min.Y + 1;
        int feet = plan.Min.Y + Math.Clamp(h - 4, 1, 5);
        return ((plan.Min.X + plan.Max.X) / 2, feet, (plan.Min.Z + plan.Max.Z) / 2);
    }

    /// <summary>The roles with the plan: planned cells are <see cref="BlockRole.Planned"/>, the rest keep their real role.</summary>
    public static RoleMap Roles(VoxelPlan plan, RoleMap? real) => (x, y, z) =>
        plan.Blocks.ContainsKey(new BlockPos(x, y, z)) ? BlockRole.Planned : plan.Air.Contains(new BlockPos(x, y, z)) ? BlockRole.None : real?.Invoke(x, y, z) ?? BlockRole.None;

    public DesignPicture Render(VoxelPlan plan, IBlockAccess world, RoleMap? realRoles = null, IVoxelWorld? light = null, TextureAtlas? atlas = null,
        long time = 6000, int pixelBudget = 300_000)
    {
        var overlay = new PlannedWorld(world, plan);
        var focus = FocusFor(plan);
        int r = RadiusFor(plan);
        ulong key = plan.Hash() * 1099511628211UL ^ SurroundingsHash(world, plan, r) ^ (ulong)(atlas != null ? 7 : 0) ^ (ulong)pixelBudget;
        lock (_lock)
            for (var n = _cache.First; n != null; n = n.Next)
                if (n.Value.Key == key)
                {
                    _cache.Remove(n);
                    _cache.AddFirst(n);
                    CacheHits++;
                    return n.Value;
                }
        var bp = plan.Blueprint;
        var img = _sheets.Render(new ContactSheetRequest
        {
            Blocks = overlay, Focus = focus, Radius = r, Views = Views, FeetY = focus.Y, Roles = Roles(plan, realRoles),
            Atlas = atlas, Light = atlas != null ? new PlannedVoxels(light, world, plan) : null, Time = time, CutAway = false, PixelBudget = pixelBudget,
            Axis = SideAxis.LookNorth,
            Marks = [new VisionMark($"plan: {bp.Name}", plan.Min.X, plan.Min.Y, plan.Min.Z, plan.Max.X, plan.Max.Y, plan.Max.Z, Box: true)],
        });
        var legend = $"DESIGN PREVIEW of \"{bp.Name}\" {bp.SizeText}: magenta * = planned blocks (not built), everything else is the real site. " + img.Legend;
        var pic = new DesignPicture(img.Png, img.Width, img.Height, legend, key, img);
        lock (_lock)
        {
            Renders++;
            _cache.AddFirst(pic);
            while (_cache.Count > 16) _cache.RemoveLast();
        }
        return pic;
    }

    /// <summary>The real blocks around the site (a change near it re-renders; the rest of the world doesn't matter).</summary>
    private static ulong SurroundingsHash(IBlockAccess w, VoxelPlan plan, int r)
    {
        ulong h = 14695981039346656037UL;
        var (cx, cy, cz) = FocusFor(plan);
        unchecked
        {
            for (int y = cy - 6; y <= cy + 5; y++)
                for (int z = cz - r; z <= cz + r; z++)
                    for (int x = cx - r; x <= cx + r; x++)
                        h = (h ^ (ulong)(w.IsLoaded(x, z) ? w.GetBlock(x, y, z) + 1 : 0)) * 1099511628211UL;
        }
        return h;
    }
}
