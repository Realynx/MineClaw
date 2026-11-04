using MineClaw.Data;
using static MineClaw.World.Tests.Golden;

namespace MineClaw.World.Tests;

/// <summary>
/// <see cref="BlockOrientation"/> golden (complexity refactor WP04), taken before its rule lookups were reshaped: for every block id
/// and meta of both versions, the mask, whether it turns, the words, the item damage and the agreement of every meta pair, and the
/// predicted meta of every click (each face, yaw quarter and a few off-quarter yaws, cursor heights, sneaking, stands near and far,
/// above and below), plus the item → block map and the solver's clicks.
/// </summary>
public class OrientationGoldenTests
{
    private static readonly float[] Yaws = [0f, 44f, 46f, 90f, 135f, 180f, 225f, 269f, 270f, 315f, -90f, 400f];
    private static readonly float[] CursorYs = [0f, 0.5f, 0.5001f, 1f];
    private static readonly Vec3d[] Feet = [new(0.5, 64, 3.5), new(1.5, 65, 0.5), new(0.5, 62, 1.5), new(0.2, 66.5, 0.9), new(3.5, 64, 0.5), new(-1.6, 61, -1.2)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Orientation_matches_the_golden(bool modern) => Check($"orientation-{(modern ? "1122" : "b181")}",
        modern ? "458089520C6FECF1E5907BED13FACFFFF5EE339497263795EA0B8E06C2F6E169" : "58955170574FA56BB2D6D3FB261306D591F5E95F2B5D375963484AA119C9FA16", sb =>
    {
        using var _ = GoldenWorlds.Data(modern);
        var at = new BlockPos(0, 64, 0);
        for (int id = 0; id < 256; id++)
        {
            sb.Append(id).Append(" mask=").Append(BlockOrientation.Mask(id, modern)).Append('\n');
            for (int meta = 0; meta < 16; meta++)
            {
                sb.Append(' ').Append(meta).Append(" turns=").Append(BlockOrientation.Turns(id, meta, modern)).Append(" dmg=")
                    .Append(BlockOrientation.Damage(id, meta)).Append(" words=").Append(BlockOrientation.Describe(id, meta, modern) ?? "-").Append(" same=");
                for (int got = 0; got < 16; got++) sb.Append(BlockOrientation.Same(id, meta, got, modern) ? '1' : '0');
                sb.Append('\n');
            }
            sb.Append(" predict ");
            foreach (BlockFace face in Enum.GetValues<BlockFace>())
            foreach (float yaw in Yaws)
            foreach (float cy in CursorYs)
            foreach (var feet in Feet)
            foreach (bool sneak in new[] { false, true })
            foreach (int damage in new[] { 0, 3, 13 })
            {
                var m = BlockOrientation.Predict(id, damage, at, new PlaceInput(face, yaw, feet, 0.5f, cy, 0.5f, sneak), modern);
                sb.Append(m?.ToString() ?? "-").Append(',');
            }
            sb.Append('\n');
            if (BlockOrientation.Mask(id, modern) == 0) continue;
            for (int meta = 0; meta < 16; meta++)
            {
                sb.Append(" solve ").Append(meta).Append(':');
                foreach (var t in BlockOrientation.Solve(id, meta, at, Feet[meta % Feet.Length], modern, f => f != BlockFace.Bottom || meta % 2 == 0, meta % 3 == 0))
                    sb.Append(t.Face).Append('/').Append(t.Yaw is { } y ? R(y) : "-").Append('/').Append(R(t.CursorX)).Append('/').Append(R(t.CursorY))
                        .Append('/').Append(R(t.CursorZ)).Append(' ');
                sb.Append('\n');
            }
        }
        for (int item = 0; item < 2300; item++) sb.Append(BlockOrientation.BlockOf(item)).Append(',');
        sb.Append('\n');
        for (int q = -2; q < 6; q++) sb.Append(BlockOrientation.LeftHinge(q)).Append(R(BlockOrientation.YawOf(q))).Append(' ');
    });
}
