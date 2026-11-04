using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Scripting.Story;

/// <summary>
/// Where to nerd-pole from to reach a block above (the <c>story.dig</c> SDK primitive's eyes; docs/web/story-mode.md "Reaching high
/// blocks"). Fox 11-04: "With the blocks-out-of-reach prompt for mining, cutting or harvesting, it should nerd-pole up to reach those
/// blocks on its own and destroy the nerd pole on the way back down." A column right under the target (a ceiling) or beside it, with
/// ground under it (never a liquid), free all the way up (no lava beside it, nothing falling into a column under the target), and the
/// fewest blocks to stack until the target's centre is within reach of the eyes. <c>within</c>: no pillar needed;
/// <c>stand</c>/<c>steps</c>: walk there and pillar that many; <c>block</c>/<c>blocks</c>: what it pillars with and how many it
/// carries (dirt, cobblestone, netherrack, planks, stone, sandstone: never anything worth keeping); <c>gather</c>: dirt around the
/// stand to dig for blocks; <c>why</c>: no column will do.
/// </summary>
public static class ReachScan
{
    /// <summary>The highest pillar a pawn builds.</summary>
    public const int MaxPillar = 24;
    /// <summary>Reach for planning (survival 4.5, less a margin for the face's offset and the aim).</summary>
    public const double Reach = 4.0;

    private static readonly int[] PillarIds = [3, 4, 87, 5, 1, 24];
    private static readonly HashSet<int> Falling = [12, 13, 122, 145, 252];

    public static JsonObject Scan(WorldState w, IGameData data, Vec3d pos, BlockPos target)
    {
        int H = w.WorldHeight;
        bool Liquid(int x, int y, int z) => w.GetBlock(x, y, z) is >= 8 and <= 11;
        bool Lava(int x, int y, int z) => w.GetBlock(x, y, z) is 10 or 11;
        bool Solid(int x, int y, int z) => y >= 0 && y < H && BlockShapes.HasCollision(w, x, y, z);
        bool Free(int x, int y, int z) => y >= 0 && y < H && !Solid(x, y, z) && !Liquid(x, y, z) && w.GetBlock(x, y, z) is not (51 or 81 or 30);
        var center = new Vec3d(target.X + 0.5, target.Y + 0.5, target.Z + 0.5);
        double Eye(double x, double feetY, double z) => new Vec3d(x, feetY + 1.62, z).DistanceTo(center);
        var o = new JsonObject { ["within"] = Eye(pos.X, pos.Y, pos.Z) <= Reach };
        int blocks = 0;
        string? block = null;
        foreach (int id in PillarIds)
        {
            if (!data.ItemExists(id)) continue;
            int n = w.Inventory.CountItem(id);
            if (n <= 0) continue;
            blocks += n;
            block ??= data.Item(id).Name;
        }
        o["block"] = block;
        o["blocks"] = blocks;
        if (o["within"]!.GetValue<bool>()) return o;

        (BlockPos Stand, int Steps, double Score)? best = null;
        string why = "no ground to stand on below or beside it within " + MaxPillar + " blocks";
        for (int r = 0; r <= 2; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    int cx = target.X + dx, cz = target.Z + dz;
                    if (!w.IsLoaded(cx, cz)) continue;
                    bool under = r == 0;
                    if (under && Falling.Contains(w.GetBlock(target.X, target.Y + 1, target.Z))) { why = "sand or gravel over it would fall on me"; continue; }
                    // Ground: the first standable cell going down from just below the target's height.
                    int gy = -1;
                    for (int y = target.Y - 1; y >= Math.Max(1, target.Y - MaxPillar - 4); y--)
                    {
                        if (!Free(cx, y, cz)) { if (Liquid(cx, y, cz)) { why = "only liquid to stand on under it"; } break; }
                        if (Solid(cx, y - 1, cz)) { if (Free(cx, y + 1, cz)) gy = y; break; }
                        if (Liquid(cx, y - 1, cz)) { why = "only liquid to stand on under it"; break; }
                    }
                    if (gy < 0) continue;
                    int feet = gy;
                    while (feet - gy <= MaxPillar && Eye(cx + 0.5, feet, cz + 0.5) > Reach) feet++;
                    if (feet - gy > MaxPillar) { why = "more than " + MaxPillar + " blocks up"; continue; }
                    if (feet + 1 >= H - 1) { why = "above the build height"; continue; }
                    bool clear = true;
                    for (int y = gy; y <= feet + 1 && clear; y++)
                    {
                        if (!Free(cx, y, cz)) clear = false;
                        foreach (var (ax, az) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                            if (Lava(cx + ax, y, cz + az)) clear = false;
                    }
                    if (!clear) { why = "no free column to pillar up beside it"; continue; }
                    double score = (feet - gy) + 0.3 * r;
                    if (best is not { } b || score < b.Score) best = (new BlockPos(cx, gy, cz), feet - gy, score);
                }
        if (best is not { } pick)
        {
            o["why"] = why;
            return o;
        }
        o["stand"] = Fmt(pick.Stand);
        o["steps"] = pick.Steps;
        // Dirt around the stand to dig for blocks when it carries too few (the surface it walks on; never the stand's own floor).
        var gather = new JsonArray();
        var floor = new BlockPos((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y + 1e-6) - 1, (int)Math.Floor(pos.Z));
        for (int dx = -3; dx <= 3 && gather.Count < 32; dx++)
            for (int dz = -3; dz <= 3 && gather.Count < 32; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                int x = pick.Stand.X + dx, z = pick.Stand.Z + dz, y = pick.Stand.Y - 1;
                if (new BlockPos(x, y, z) == floor) continue; // never the ground under its own feet
                if (w.GetBlock(x, y, z) is not (2 or 3) || Liquid(x, y + 1, z) || Liquid(x + 1, y, z) || Liquid(x - 1, y, z) || Liquid(x, y, z + 1) || Liquid(x, y, z - 1)) continue;
                gather.Add(Fmt(new BlockPos(x, y, z)));
            }
        o["gather"] = gather;
        return o;
    }

    private static string Fmt(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
}
