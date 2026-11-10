using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MineClaw.World.Tests;

/// <summary>
/// The takeover's physics parity fixture (tests/fixtures/control-parity/&lt;version&gt;-&lt;case&gt;.json): a small block world as a list
/// of cells, the start, the input of every tick and the position and velocity after it, as <see cref="PlayerPhysics"/> (b1.8.1)
/// and <see cref="Release1122.Physics1122"/> (1.12.2) compute them. The browser's prediction is checked against the same files,
/// so this is what ties the two languages together. These tests read the files and never write them; set
/// <see cref="WriteSwitch"/>=1 and run <see cref="Fixtures_are_written_only_when_asked"/> to regenerate them, then review the
/// change, since a file freezes whatever the C# physics does that day.
/// </summary>
public class ControlParityTests
{
    public const string WriteSwitch = "MINECLAW_WRITE_CONTROL_PARITY";
    private const float East = -90f; // the yaw that walks toward +X
    private const int Stone = 1, StillWater = 9, Ladder = 65, LadderOnEastWall = 4;

    public static readonly string[] Versions = ["b1.8.1", "1.12.2"];
    public static readonly string[] CaseNames = ["walk", "sprint-jump", "walk-diagonal", "sneak-diagonal", "ledge", "ladder", "shallow-water"];

    public static IEnumerable<object[]> Cases() => from v in Versions from c in CaseNames select new object[] { v, c };

    private sealed record Step(MoveInput Input, Vec3d Position, Vec3d Velocity, bool OnGround);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Physics_reproduces_the_fixture(string version, string name)
    {
        using var file = JsonDocument.Parse(File.ReadAllText(FixturePath(version, name)));
        var root = file.RootElement;
        Assert.Equal((version, name), (root.GetProperty("version").GetString(), root.GetProperty("case").GetString()));
        bool release = version == "1.12.2";
        using var _ = GoldenWorlds.Data(release);
        var world = GoldenWorlds.Empty(release);
        foreach (var c in root.GetProperty("cells").EnumerateArray())
            world.Set(c[0].GetInt32(), c[1].GetInt32(), c[2].GetInt32(), c[3].GetInt32(), c[4].GetInt32());
        var start = root.GetProperty("start");
        var s = Start(new Vec3d(D(start, "x"), D(start, "y"), D(start, "z")), (float)D(start, "yaw"),
            new Vec3d(D(start, "vx"), D(start, "vy"), D(start, "vz")), start.GetProperty("onGround").GetBoolean());

        int tick = 0;
        foreach (var t in root.GetProperty("ticks").EnumerateArray())
        {
            tick++;
            var i = t.GetProperty("input");
            PlayerPhysics.Tick(world, s, new MoveInput((float)D(i, "forward"), (float)D(i, "strafe"), i.GetProperty("jump").GetBoolean(),
                i.GetProperty("sneak").GetBoolean(), i.GetProperty("sprint").GetBoolean(), (float)D(i, "yaw"), (float)D(i, "pitch")));
            var want = (D(t, "x"), D(t, "y"), D(t, "z"), D(t, "vx"), D(t, "vy"), D(t, "vz"));
            var got = (s.Position.X, s.Position.Y, s.Position.Z, s.Velocity.X, s.Velocity.Y, s.Velocity.Z);
            Assert.True(Close(want, got), $"{version} {name} tick {tick}: expected {want}, got {got}");
            Assert.True(t.GetProperty("onGround").GetBoolean() == s.OnGround, $"{version} {name} tick {tick}: on ground {s.OnGround}");
        }
        Assert.InRange(tick, 20, 40); // the browser's test compares scripts of up to 40 ticks
    }

    /// <summary>The cases do what their names say, so a regenerated file cannot quietly lose its point.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_case_shows_its_movement(string version, string name)
    {
        var (_, start, steps) = Run(version, name);
        var end = steps[^1];
        switch (name)
        {
            case "walk":
            case "walk-diagonal":
            case "sneak-diagonal":
                Assert.All(steps, st => Assert.True(st.OnGround));
                Assert.True(end.Position.X > start.X + (name == "sneak-diagonal" ? 0.5 : 2));
                if (name != "walk") Assert.True(end.Position.Z < start.Z - (name == "sneak-diagonal" ? 0.5 : 2));
                break;
            case "sprint-jump":
                Assert.Contains(steps, st => st.Position.Y > start.Y + 1);
                Assert.True(end.Position.X > start.X + 6);
                break;
            case "ledge":
                Assert.Equal(60, end.Position.Y, 6);
                Assert.True(end.OnGround);
                break;
            case "ladder":
                Assert.Equal(66, end.Position.Y, 6); // on top of the wall the ladder climbs
                Assert.True(end.Position.X > 2.5);
                break;
            case "shallow-water":
                Assert.True(end.Position.X > 4.4, "walks out of the far side");
                Assert.Contains(steps, st => Math.Abs(st.Velocity.Y + 0.02) < 1e-6); // wading: water's −0.02, not the ground's −0.0784
                Assert.All(steps, st => Assert.InRange(st.Position.Y, 63.9, 64.5));
                break;
        }
    }

    [Fact]
    public void Fixtures_are_written_only_when_asked()
    {
        if (Environment.GetEnvironmentVariable(WriteSwitch) != "1") return;
        Directory.CreateDirectory(FixtureDirectory());
        foreach (var version in Versions)
        foreach (var name in CaseNames)
        {
            var (cells, start, steps) = Run(version, name);
            File.WriteAllText(FixturePath(version, name), Json(version, name, cells, start, steps));
        }
    }

    /// <summary>One case's world and inputs, run from rest.</summary>
    private static (List<int[]> Cells, (double X, double Y, double Z, float Yaw, Vec3d V, bool OnGround) Start, List<Step> Steps) Run(string version,
        string name)
    {
        bool release = version == "1.12.2";
        using var _ = GoldenWorlds.Data(release);
        var cells = new List<int[]>();
        void Fill(int x0, int y0, int z0, int x1, int y1, int z1, int id, int meta = 0)
        {
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
                cells.Add([x, y, z, id, meta]);
        }

        var inputs = new List<MoveInput>();
        void Hold(int ticks, MoveInput input) { for (int i = 0; i < ticks; i++) inputs.Add(input); }
        var walk = new MoveInput(Forward: 1, Yaw: East);
        var idle = MoveInput.Idle(East, 0);
        switch (name)
        {
            case "walk":
                Fill(-2, 63, -1, 12, 63, 1, Stone);
                Hold(16, walk);
                Hold(6, idle);
                break;
            case "sprint-jump":
                Fill(-2, 63, -1, 16, 63, 1, Stone);
                Hold(24, walk with { Sprint = true, Jump = true });
                Hold(4, idle);
                break;
            case "walk-diagonal": // W+A while facing +X: forward and to the left, toward −Z
                Fill(-2, 63, -6, 8, 63, 2, Stone);
                Hold(20, walk with { Strafe = 1 });
                break;
            case "sneak-diagonal":
                Fill(-2, 63, -6, 8, 63, 2, Stone);
                Hold(20, walk with { Strafe = 1, Sneak = true });
                break;
            case "ledge": // off a platform onto a floor four blocks lower
                Fill(-2, 63, -1, 1, 63, 1, Stone);
                Fill(2, 59, -1, 12, 59, 1, Stone);
                Hold(30, walk);
                break;
            case "ladder": // into a ladder on a wall, up two blocks and forward onto the wall's top
                Fill(-2, 63, -1, 1, 63, 1, Stone);
                Fill(2, 64, -1, 8, 65, 1, Stone);
                Fill(1, 64, 0, 1, 65, 0, Ladder, LadderOnEastWall);
                Hold(40, walk);
                break;
            case "shallow-water": // wading through one block of water on the floor
                Fill(-2, 63, -1, 14, 63, 1, Stone);
                Fill(2, 64, -1, 3, 64, 1, StillWater);
                Hold(40, walk);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }

        var world = GoldenWorlds.Empty(release);
        foreach (var c in cells) world.Set(c[0], c[1], c[2], c[3], c[4]);
        // At rest on the floor: a few idle ticks settle the vertical speed at the version's own resting value.
        var settle = Start(new Vec3d(0.5, 64, 0.5), East, Vec3d.Zero, true);
        for (int i = 0; i < 3; i++) PlayerPhysics.Tick(world, settle, idle);
        var start = (settle.Position.X, settle.Position.Y, settle.Position.Z, East, settle.Velocity, settle.OnGround);
        // The fixture's start is all a reader gets, so the run begins from exactly that.
        var s = Start(settle.Position, East, settle.Velocity, settle.OnGround);
        var steps = new List<Step>();
        foreach (var input in inputs)
        {
            PlayerPhysics.Tick(world, s, input);
            steps.Add(new Step(input, s.Position, s.Velocity, s.OnGround));
        }
        return (cells, start, steps);
    }

    private static PhysicsState Start(Vec3d feet, float yaw, Vec3d velocity, bool onGround)
    {
        var s = new PhysicsState();
        s.Teleport(feet, yaw, 0);
        s.Velocity = velocity;
        s.OnGround = onGround;
        return s;
    }

    private static bool Close((double, double, double, double, double, double) a, (double, double, double, double, double, double) b) =>
        Math.Abs(a.Item1 - b.Item1) < 1e-9 && Math.Abs(a.Item2 - b.Item2) < 1e-9 && Math.Abs(a.Item3 - b.Item3) < 1e-9
        && Math.Abs(a.Item4 - b.Item4) < 1e-9 && Math.Abs(a.Item5 - b.Item5) < 1e-9 && Math.Abs(a.Item6 - b.Item6) < 1e-9;

    private static double D(JsonElement o, string name) => o.GetProperty(name).GetDouble();

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string B(bool v) => v ? "true" : "false";

    /// <summary>One tick per line, so a regenerated file diffs by tick.</summary>
    private static string Json(string version, string name, List<int[]> cells, (double X, double Y, double Z, float Yaw, Vec3d V, bool OnGround) start,
        List<Step> steps)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"version\": \"{version}\",\n  \"case\": \"{name}\",\n");
        sb.Append("  \"units\": \"blocks; velocity in blocks per tick after the tick (the wire's vx, vy, vz); yaw -90 faces +x; cells are [x, y, z, id, meta], everything else is air\",\n");
        sb.Append("  \"cells\": [").Append(string.Join(", ", cells.Select(c => $"[{string.Join(", ", c)}]"))).Append("],\n");
        sb.Append($"  \"start\": {{ \"x\": {N(start.X)}, \"y\": {N(start.Y)}, \"z\": {N(start.Z)}, \"yaw\": {N(start.Yaw)}, ")
            .Append($"\"vx\": {N(start.V.X)}, \"vy\": {N(start.V.Y)}, \"vz\": {N(start.V.Z)}, \"onGround\": {B(start.OnGround)} }},\n");
        sb.Append("  \"ticks\": [\n");
        for (int i = 0; i < steps.Count; i++)
        {
            var (input, p, v, onGround) = (steps[i].Input, steps[i].Position, steps[i].Velocity, steps[i].OnGround);
            sb.Append($"    {{ \"input\": {{ \"forward\": {N(input.Forward)}, \"strafe\": {N(input.Strafe)}, \"jump\": {B(input.Jump)}, ")
                .Append($"\"sneak\": {B(input.Sneak)}, \"sprint\": {B(input.Sprint)}, \"yaw\": {N(input.Yaw)}, \"pitch\": {N(input.Pitch)} }}, ")
                .Append($"\"x\": {N(p.X)}, \"y\": {N(p.Y)}, \"z\": {N(p.Z)}, \"vx\": {N(v.X)}, \"vy\": {N(v.Y)}, \"vz\": {N(v.Z)}, ")
                .Append($"\"onGround\": {B(onGround)} }}").Append(i < steps.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static string FixtureDirectory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "MineClaw.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root.FullName, "tests", "fixtures", "control-parity");
    }

    private static string FixturePath(string version, string name) => Path.Combine(FixtureDirectory(), $"{version}-{name}.json");
}
