using System.Text;
using static MineClaw.World.Tests.Golden;

namespace MineClaw.World.Tests;

/// <summary>
/// Physics and probe goldens (complexity refactor WP04, checkpoint 3), taken before the ticks, the guard, the speed boost and the
/// probes were reshaped. Over the seeded <see cref="GoldenWorlds"/> of both versions:
/// <list type="bullet">
/// <item><b>walkers</b>: random held inputs (forward, strafe, jump, sneak, sprint, item use, hunger, look) from foot and water
/// cells, potion amplifiers on some bodies; plain ticks, ticks through <see cref="EdgeGuard.Filter"/> (with
/// <see cref="EdgeGuard.PlanClutch"/> and <see cref="EdgeGuard.FindInterior"/>), and ticks followed by a
/// <see cref="SpeedBoost.Plan"/> / <see cref="SpeedBoost.Apply"/>; every tick's state bit for bit.</item>
/// <item><b>probes</b>: columns (<see cref="Safety"/>), rays, dig / place / pour aims, soil and attach rules, water columns,
/// obstructions, the nearest breathable cell and swim descents, over random cells.</item>
/// </list>
/// </summary>
public class PhysicsGoldenTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var v in new[] { false, true })
        foreach (var k in GoldenWorlds.Kinds)
            yield return [k, v];
    }

    private static readonly Dictionary<string, string> Expected = new()
    {
        ["physics-cave-1122"] = "AF8E52E8A1D28545C68015F4C64788F5FEED48CFE97B4E8ED6444EBB69647AF9",
        ["physics-cave-b181"] = "0A3E4A8C25E5DE906E4052EB8B8A3FEB86834F0842EB9BFFCB502AEB5980854A",
        ["physics-ditches-1122"] = "1AF87C3B80B42873632F768086A14F91ACB2918B063D5975CBE41BD1B42DF432",
        ["physics-ditches-b181"] = "3651C2C57D08E5BD223E5C26BCE2DADDB55797574E8B46F6D7F1E6BE9DC36372",
        ["physics-doors-1122"] = "98EA4E0DEC9622F6B8E17279ACBB2A9CCCDF81114AB89983F71BDFDEAF0B1B34",
        ["physics-doors-b181"] = "CD0BEA9C343E543CF9EFD8E2186566B897660E7E2C2D45B1FFD64EA3FE808D6E",
        ["physics-field-1122"] = "FF3F3FF365AA588ED1427537F5F96B476282CA8D9437FFE7EED7A56DFEF40E6F",
        ["physics-field-b181"] = "75109564E36523C603A8BCA94A3A585F73C2654D8AE66BF39EE9CBBBC0FFB1D6",
        ["physics-hills-1122"] = "F7261A5CF81A5312DF4E7360EA6989D94C8598DBF7FD742E4CBCC107F505DE7F",
        ["physics-hills-b181"] = "7260DB300027752600107457DDDB26F5A8A9E0A8369BAFEA2911E6BA189570D8",
        ["physics-islands-1122"] = "CFBFC93225DEBA14F6B305172759257592225CD294E97BBF3244FBF50065382E",
        ["physics-islands-b181"] = "452063390BA949DFD400E49373628C0AE2FB606B54568C2D4EE422B648A652C5",
        ["physics-quarry-1122"] = "9BF68A4FA168B89738482AD354DE353CB2583B3336042A5801FEE841A2BD9A16",
        ["physics-quarry-b181"] = "E99B607B9ADFA3E88829313AA023E5FDFFB0CF90E2E44BB7D2B026CCD6B8210C",
        ["physics-water-1122"] = "A36EDAE23BC6935272927DE620C63382EC618F3AA29CCB4F33BA4882368CFEEA",
        ["physics-water-b181"] = "B80C08E77627729437DF0E2C1B43198C3A7D118B6396ACBE596A59AA818FA178",
        ["probes-cave-1122"] = "8007A258DCBF7886959E36F2069FC098687BFD365A213FD01523F6C1BB8DE19E",
        ["probes-cave-b181"] = "C5644DAC90446895C568B660DBFCBCE8D3066C78EEB9D9F85A8340BE24392D42",
        ["probes-ditches-1122"] = "0BC94461AF2368BB35BE41EA829F47379E8EA0897258B6AC1A9E254F5FCA2048",
        ["probes-ditches-b181"] = "5ACD77EEA68B66D96DFD1ABE9ACA2B2EE0AC3CB21B0E619A9181391204AA0DCE",
        ["probes-doors-1122"] = "9CC83F8E357477DFA6C51E6C687F1D4A806F35427812CAFF341518F7F13F0025",
        ["probes-doors-b181"] = "190C283F879E26F6C07F5E85561C0CB9B00257A68992F128F378582A4E36E49D",
        ["probes-field-1122"] = "95B117F2BBACA3A47CFDD456DDBC2EBC73C5AEFB2C766093C108E6EE3F7BDED1",
        ["probes-field-b181"] = "9BAF1E682AE3D51AEC8E94DBC20DF55DB0A2577874514259C717D17DDA71B1F7",
        ["probes-hills-1122"] = "FE81E819E722CE4A3168C659B75E5F09CF18561D7607891AEAEF129179DC1F07",
        ["probes-hills-b181"] = "D29D39DA83C205985198AD0F0F7C8713506D234EEE633EB884D52FDEF2B3A250",
        ["probes-islands-1122"] = "F8F1DC781EFFCB08F880CBD5306B03AF51318FF3830F5ABD5455FB971B558307",
        ["probes-islands-b181"] = "E304A615A561A8A7065B314C15BABE9D4439194508B551583FB18ECD23B01CEB",
        ["probes-quarry-1122"] = "5A1D7449B7926840AE525001FEFC74B0EFFAF651300F76A351B78F4DD841C797",
        ["probes-quarry-b181"] = "5AD88694064BE06DEB5A4D01DC67CFD01A9F0DA5438C51AEC90AB16D79841B88",
        ["probes-water-1122"] = "D2EAEF0FD0D1CAB43B137B5665BBE74AD83A2CD8A09FFCB18A1A8835E4D31B43",
        ["probes-water-b181"] = "9E3029000E2E2A5DA5AA821CD74484BE6779195C1964BA0936319DA9A1F12B55",
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Walkers_match_the_golden(string kind, bool release1122)
    {
        string name = $"physics-{kind}-{(release1122 ? "1122" : "b181")}";
        Check(name, Expected.GetValueOrDefault(name, ""), sb =>
        {
            using var _ = GoldenWorlds.Data(release1122);
            var w = GoldenWorlds.Build(kind, 1, release1122);
            var rng = new Random(kind.Length * 977 + (release1122 ? 5 : 0));
            var starts = StartCells(w, rng);
            for (int k = 0; k < starts.Count; k++) Walk(sb, w, starts[k], k, release1122, rng);
            Climb(sb, w);
        });
    }

    /// <summary>Climbers: up each ladder column's bottom (two per world), facing its wall, with a speed boost; one sneaks
    /// halfway (holds on), one lets go.</summary>
    private static void Climb(StringBuilder sb, WorldState w)
    {
        int n = 0;
        for (int x = GoldenWorlds.Min; x <= GoldenWorlds.Max && n < 2; x++)
        for (int z = GoldenWorlds.Min; z <= GoldenWorlds.Max && n < 2; z++)
        for (int y = 1; y < 100; y++)
        {
            if (w.GetBlock(x, y, z) != GoldenWorlds.Ladder || w.GetBlock(x, y - 1, z) == GoldenWorlds.Ladder) continue;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                if (!BlockShapes.HasCollision(w, x + dx, y, z + dz)) continue;
                for (int variant = 0; variant < 2; variant++)
                {
                    var s = TestWorld.Standing(x + 0.5, y, z + 0.5);
                    float yaw = PlayerPhysics.YawPitchTo(s.Position, new Vec3d(x + dx + 0.5, y, z + dz + 0.5)).Yaw;
                    sb.Append("climber ").Append(x).Append(',').Append(y).Append(',').Append(z).Append(' ').Append(variant).Append('\n');
                    for (int t = 0; t < 60; t++)
                    {
                        bool hold = t is >= 20 and < 30;
                        var input = variant == 0
                            ? new MoveInput(hold ? 0 : 1, 0, Jump: t % 9 == 0, Sneak: hold, Yaw: yaw)
                            : new MoveInput(t < 25 ? 1 : 0, t >= 25 ? 1 : 0, Yaw: yaw, Sprint: true);
                        var before = s.Position;
                        bool ground = s.OnGround;
                        PlayerPhysics.Tick(w, s, input);
                        var plan = SpeedBoost.Plan(w, s, before, ground, 2.0 + variant, double.PositiveInfinity);
                        if (plan is { } p) SpeedBoost.Apply(w, s, p);
                        sb.Append(" t").Append(t).Append(" p=").Append(R(s.Position)).Append(" v=").Append(R(s.Velocity)).Append(s.OnLadder ? " D" : " -")
                            .Append(s.OnGround ? 'G' : 'A').Append(" fd=").Append(R(s.FallDistance))
                            .Append(" boost=").Append(plan is { } q ? $"{R(q.Ey)}/{q.Mode}/{R(q.Factor)}" : "-").Append('\n');
                    }
                }
                n++;
                break;
            }
            break;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Probes_match_the_golden(string kind, bool release1122)
    {
        string name = $"probes-{kind}-{(release1122 ? "1122" : "b181")}";
        Check(name, Expected.GetValueOrDefault(name, ""), sb =>
        {
            using var _ = GoldenWorlds.Data(release1122);
            var w = GoldenWorlds.Build(kind, 2, release1122);
            var rng = new Random(kind.Length * 131 + (release1122 ? 3 : 0));
            for (int i = 0; i < 70; i++) Probe(sb, w, rng);
            Water(sb, w);
        });
    }

    private static List<BlockPos> StartCells(WorldState w, Random rng)
    {
        var feet = GoldenWorlds.FootCells(w);
        var list = new List<BlockPos>();
        for (int i = 0; i < 9 && feet.Count > 0; i++) list.Add(feet[rng.Next(feet.Count)]);
        var water = WaterCells(w);
        for (int i = 0; i < 3 && water.Count > 0; i++) list.Add(water[rng.Next(water.Count)]);
        return list;
    }

    private static List<BlockPos> WaterCells(WorldState w)
    {
        var r = new List<BlockPos>();
        for (int x = GoldenWorlds.Min; x <= GoldenWorlds.Max; x++)
        for (int z = GoldenWorlds.Min; z <= GoldenWorlds.Max; z++)
        for (int y = 40; y < 80; y++)
            if (BlockShapes.IsLiquid(w.GetBlock(x, y, z)) && w.GetBlock(x, y + 1, z) == 0) r.Add(new BlockPos(x, y, z));
        return r;
    }

    private static MoveInput RandomInput(Random rng, BlockPos home, PhysicsState s)
    {
        float f = new[] { 1f, 1f, 0.5f, 0f, -1f }[rng.Next(5)];
        float st = new[] { 0f, 0f, 1f, -1f }[rng.Next(4)];
        float yaw = rng.Next(3) == 0
            ? PlayerPhysics.YawPitchTo(s.Position, new Vec3d(home.X + 0.5, s.Position.Y, home.Z + 0.5)).Yaw
            : (float)(rng.NextDouble() * 360 - 180);
        return new MoveInput(f, st, Jump: rng.Next(10) < 3, Sneak: rng.Next(7) == 0, Sprint: rng.Next(2) == 0, Yaw: yaw,
            Pitch: (float)(rng.NextDouble() * 120 - 60), UsingItem: rng.Next(10) == 0, Hungry: rng.Next(10) == 0);
    }

    private static void Walk(StringBuilder sb, WorldState w, BlockPos start, int k, bool release1122, Random rng)
    {
        var s = TestWorld.Standing(start.X + 0.5, start.Y, start.Z + 0.5);
        s.Velocity = new Vec3d(0, -0.0784, 0);
        if (release1122 && k % 4 == 1) { s.SpeedAmplifier = 1; s.JumpBoostAmplifier = 0; }
        if (release1122 && k % 4 == 2) s.SlownessAmplifier = 0;
        int mode = k % 3;
        var guard = new EdgeGuard();
        sb.Append("walker ").Append(k).Append(' ').Append(start).Append(" mode ").Append(mode).Append('\n');
        var input = RandomInput(rng, start, s);
        for (int t = 0; t < 110; t++)
        {
            if (t % 7 == 0) input = RandomInput(rng, start, s);
            var use = input;
            if (mode == 1)
            {
                var d = guard.Filter(w, s, input, allowDrop: t % 5 == 0);
                use = d.Input;
                sb.Append(" g=").Append(d.Actions).Append(d.NearEdge ? 'E' : '-');
                if (d.Unrecoverable || (!s.OnGround && t % 4 == 0))
                {
                    var c = guard.PlanClutch(w, s, 4.5);
                    sb.Append(" clutch=").Append(c is { } cp ? $"{cp.Target}/{cp.Saves}/{R(cp.PredictedFall)}/{cp.Aim.Against}/{cp.Aim.Face}/{R(cp.Aim.Point)}" : "-");
                }
                if (t % 10 == 0) sb.Append(" interior=").Append(guard.FindInterior(w, s)?.ToString() ?? "-");
            }
            var before = s.Position;
            bool ground = s.OnGround;
            PlayerPhysics.Tick(w, s, use);
            if (mode == 2)
            {
                double factor = new[] { 1.5, 2.0, 3.0 }[t % 3];
                double room = t % 7 == 0 ? 0.3 : t % 11 == 0 ? 0 : double.PositiveInfinity;
                var plan = SpeedBoost.Plan(w, s, before, ground, factor, room);
                if (plan is { } p)
                {
                    int n = SpeedBoost.Apply(w, s, p);
                    sb.Append(" boost=").Append(R(p.Ex)).Append('/').Append(R(p.Ey)).Append('/').Append(R(p.Ez)).Append('/').Append(p.Segments)
                        .Append('/').Append(p.Mode).Append('/').Append(R(p.Factor)).Append('/').Append(n);
                }
                else sb.Append(" boost=-");
            }
            sb.Append(" t").Append(t).Append(" p=").Append(R(s.Position)).Append(" v=").Append(R(s.Velocity))
                .Append(s.OnGround ? " G" : " A").Append(s.InWater ? "W" : "").Append(s.InLava ? "L" : "").Append(s.InWeb ? "B" : "")
                .Append(s.OnLadder ? "D" : "").Append(s.Sprinting ? "R" : "").Append(s.Sneaking ? "S" : "").Append(s.Frozen ? "F" : "")
                .Append(s.CollidedHorizontally ? "H" : "").Append(s.CollidedVertically ? "V" : "")
                .Append(" fd=").Append(R(s.FallDistance)).Append(" dmg=").Append(s.FallDamage).Append(" ys=").Append(R(s.YSize))
                .Append(" h=").Append(R(s.Height)).Append(" eye=").Append(R(s.EyeHeight)).Append(" jt=").Append(s.JumpTicks)
                .Append(" ls=").Append(R(s.LandSpeed)).Append(" as=").Append(R(s.AirSpeed)).Append('\n');
            if (s.Position.Y < 0) break;
        }
    }

    private static readonly int[] SoilItems = [6, 295, 338, 81, 39, 40, 37, 38, 1, 372, 351, 361, 362, 391, 392];
    private static readonly int[] AttachItems = [50, 69, 77, 65, 323, 76, 96, 106, 1];

    private static void Probe(StringBuilder sb, WorldState w, Random rng)
    {
        int x = rng.Next(GoldenWorlds.Min, GoldenWorlds.Max + 1), z = rng.Next(GoldenWorlds.Min, GoldenWorlds.Max + 1);
        int y = (GoldenWorlds.Surface(w, x, z)?.Y ?? 64) + rng.Next(-3, 3);
        var c = new BlockPos(x, y, z);
        sb.Append("cell ").Append(c).Append(" deadly=").Append(Safety.IsDeadlyColumn(w, x, y, z, 3)).Append('/').Append(Safety.IsDeadlyColumn(w, x, y, z, 10))
            .Append(" fatal=").Append(Safety.IsFatalColumn(w, x, y, z)).Append(" edge=").Append(Safety.EdgeDistance(w, x, y, z))
            .Append(" stand=").Append(Safety.IsStandable(w, x, y, z)).Append(" support=").Append(Safety.SupportBelow(w, x, y, z))
            .Append(" voidn=").Append(Safety.VoidNeighbourCount(w, x, y, z)).Append('\n');
        var eye = new Vec3d(x + 0.5, y + PlayerPhysics.EyeHeight, z + 0.5);
        for (int i = 0; i < 3; i++)
        {
            float yaw = (float)(rng.NextDouble() * 360 - 180), pitch = (float)(rng.NextDouble() * 180 - 90);
            var hit = Raycast.Cast(w, eye, yaw, pitch, 6);
            sb.Append(" ray ").Append(R(yaw)).Append('/').Append(R(pitch)).Append(" = ")
                .Append(hit is { } h ? $"{h.Pos}/{h.Face}/{R(h.Point)}/{R(h.Distance)}/{h.BlockId}" : "-").Append('\n');
        }
        var me = AABB.ForPlayer(new Vec3d(x + 0.5, y, z + 0.5));
        for (int i = 0; i < 4; i++)
        {
            var t = new BlockPos(x + rng.Next(-3, 4), y + rng.Next(-3, 3), z + rng.Next(-3, 4));
            var dig = Aiming.FindDigAim(w, eye, t);
            var place = Aiming.FindPlaceAim(w, eye, t, 4.5, me);
            var pour = Aiming.FindPourAim(w, eye, t);
            sb.Append(" aim ").Append(t).Append(" dig=").Append(dig is { } d ? $"{d.Block}/{d.Face}/{R(d.Point)}/{R(d.Yaw)}/{R(d.Pitch)}/{R(d.Distance)}" : "-")
                .Append(" place=").Append(Fmt(place)).Append(" pour=").Append(Fmt(pour)).Append('\n');
            int soil = SoilItems[rng.Next(SoilItems.Length)], att = AttachItems[rng.Next(AttachItems.Length)];
            var snap = PlaceRules.SnapAttachable(w, t, att, eye);
            sb.Append(" soil ").Append(soil).Append('=').Append(PlaceRules.SoilProblem(w, t, soil) ?? "-")
                .Append(" attach ").Append(att).Append('=').Append(snap is { } sp ? $"{sp.Pos}/{sp.Face}/{sp.Moved}" : "-").Append('\n');
        }
        var target = new Vec3d(x + 0.5 + rng.Next(-4, 5), y, z + 0.5 + rng.Next(-4, 5));
        sb.Append(" obstruct=");
        foreach (var o in StuckProbe.Obstructions(w, new Vec3d(x + 0.5, y, z + 0.5), target)) sb.Append(o.Pos).Append(o.Head ? 'H' : 'F').Append(' ');
        sb.Append(" free=");
        foreach (var (dx, dz) in StuckProbe.FreeDirections(w, new Vec3d(x + 0.5, y, z + 0.5), target)) sb.Append(dx).Append(',').Append(dz).Append(' ');
        sb.Append('\n');
    }

    private static string Fmt(PlaceAim? a) =>
        a is { } p ? $"{p.Target}/{p.Against}/{p.Face}/{R(p.Point)}/{R(p.Yaw)}/{R(p.Pitch)}/{R(p.Distance)}" : "-";

    private static void Water(StringBuilder sb, WorldState w)
    {
        var water = WaterCells(w);
        for (int i = 0; i < water.Count; i += Math.Max(1, water.Count / 12))
        {
            var c = water[i];
            var (bottom, landed) = WaterColumn.Extent(w, c);
            sb.Append("water ").Append(c).Append(" extent=").Append(bottom).Append('/').Append(landed).Append(" cells=");
            foreach (var (p, m) in WaterColumn.Cells(w, c, 64)) sb.Append(p).Append(':').Append(m).Append(' ');
            var under = new BlockPos(c.X, c.Y - 1, c.Z);
            var b = Breath.NearestBreathable(w, under, 2000, 16);
            sb.Append(" breath=").Append(b is { } br ? $"{br.Cell}/{br.Steps}/{string.Join(";", br.Path)}" : "-");
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var stand = new BlockPos(c.X + dx, c.Y + 1, c.Z + dz);
                if (!Safety.IsStandable(w, stand.X, stand.Y, stand.Z)) continue;
                var r = SwimSim.SimulateDescent(w, stand, new SwimColumn(c.X, c.Y, c.Z), c.Y - 2, 300);
                sb.Append(" descent=").Append(r.Ok).Append('/').Append(r.Ticks).Append('/').Append(R(r.MinFeetY)).Append('/').Append(R(r.End.Position)).Append('/').Append(r.Why ?? "-");
                break;
            }
            sb.Append('\n');
        }
    }
}
