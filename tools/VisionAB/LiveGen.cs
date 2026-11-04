using System.Globalization;
using System.Text;
using MineClaw.Bot;
using MineClaw.Bot.Perception;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.Perception.Vision.Eval;
using MineClaw.Render;
using MineClaw.World;
using Microsoft.Data.Sqlite;
using JsonDocument = System.Text.Json.JsonDocument;
using JsonValueKind = System.Text.Json.JsonValueKind;
using JsonElement = System.Text.Json.JsonElement;

/// <summary>
/// Rebuilds a live spot from a db snapshot (db + its -wal) and renders the exact contact sheet the bot rendered:
///   dotnet run --project tools/VisionAB -- livegen [--db=data/alice-snap/perc.db] [--focus=4,64,6] [--self=14,64,-4] [--yaw=0]
///       [--fixture=tests/fixtures/vision-eval/dual-gen.txt] [--box=-6,56,-6,16,70,16]
/// Writes sheet PNG + legend + a block dump (y layers) to --out, and (with --fixture) the blocks as "x y z id meta".
/// </summary>
static class LiveGen
{
    public static int Run(string root, string outDir, Func<string, string?> arg)
    {
        var db = arg("db") ?? Path.Combine(root, "data", "alice-snap", "perc.db");
        var focus = P(arg("focus") ?? "4,64,6");
        var self = P(arg("self") ?? "14,64,-4");
        float yaw = float.TryParse(arg("yaw"), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ? y : 0;
        var box = (arg("box") ?? "-6,56,-6,16,70,16").Split(',').Select(int.Parse).ToArray();
        var w = Load(db);
        var dig = DigCells(db);
        Console.WriteLine($"{w.RememberedCount} columns; dig cells {string.Join(" ", dig)}");
        var layers = Layers(w, box);
        File.WriteAllText(Path.Combine(outDir, "livegen-layers.txt"), layers);
        Console.WriteLine(layers);
        if (arg("fixture") is { } fx) WriteFixture(Path.IsPathRooted(fx) ? fx : Path.Combine(root, fx), w, box, dig, db);
        var digSet = dig.ToHashSet();
        EvalSheet(outDir, w, self, focus, yaw, digSet);
        LiveSheet(root, outDir, db, w, self, focus, yaw, digSet);
        return 0;
    }

    /// <summary>The box's non-empty y layers as character maps (x digits across, z down).</summary>
    private static string Layers(WorldState w, int[] box)
    {
        var sb = new StringBuilder();
        for (int yy = box[1]; yy <= box[4]; yy++)
        {
            bool any = false;
            var layer = new StringBuilder($"y{yy}\n     ");
            for (int x = box[0]; x <= box[3]; x++) layer.Append(Mod(x));
            layer.Append('\n');
            for (int z = box[2]; z <= box[5]; z++)
            {
                layer.Append($"{z,4} ");
                for (int x = box[0]; x <= box[3]; x++)
                {
                    int id = w.PlanningView.GetBlock(x, yy, z);
                    any |= id != 0;
                    layer.Append(Glyph(id));
                }
                layer.Append('\n');
            }
            if (any) sb.Append(layer);
        }
        return sb.ToString();
    }

    private static char Glyph(int id) => Glyphs.TryGetValue(id, out var g) ? g : '#';

    private static readonly Dictionary<int, char> Glyphs = new()
    {
        [0] = '.', [4] = 'c', [1] = 's', [8] = '~', [9] = '~', [10] = '!', [11] = '!', [2] = 'd', [3] = 'd', [17] = 'L', [18] = 'l', [5] = 'p',
        [54] = 'C', [61] = 'F', [62] = 'F', [58] = 'W', [89] = 'g', [20] = 'o',
    };

    /// <summary>The box's blocks as "x y z id meta" lines (a vision-eval fixture).</summary>
    private static void WriteFixture(string path, WorldState w, int[] box, List<(int, int, int)> dig, string db)
    {
        var f = new StringBuilder($"# overworld around the dual cobble gen (dig {string.Join(" ", dig)}), from {Path.GetFileName(db)} world_chunks (x y z id meta)\n");
        for (int x = box[0]; x <= box[3]; x++)
            for (int z = box[2]; z <= box[5]; z++)
                for (int yy = 0; yy < 128; yy++)
                    if (w.PlanningView.GetBlock(x, yy, z) is var id and not 0) f.Append(CultureInfo.InvariantCulture, $"{x} {yy} {z} {id} {w.PlanningView.GetMeta(x, yy, z)}\n");
        File.WriteAllText(path, f.ToString());
        Console.WriteLine($"fixture {path}");
    }

    /// <summary>The eval harness's sheet for this spot.</summary>
    private static void EvalSheet(string outDir, WorldState w, (int X, int Y, int Z) self, (int X, int Y, int Z) focus, float yaw, HashSet<(int, int, int)> digSet)
    {
        var c = new VisionEvalCase("live", "live", w, self, focus, "q", "", _ => false, p => digSet.Contains((p.X, p.Y, p.Z)), Radius: 5, Yaw: yaw);
        var sheet = VisionEval.Sheet(c);
        File.WriteAllBytes(Path.Combine(outDir, "livegen-sheet.png"), sheet.Png);
        File.WriteAllText(Path.Combine(outDir, "livegen-sheet.txt"), sheet.Legend);
        Console.WriteLine($"{sheet.Width}x{sheet.Height} ~{sheet.Tokens} tok\n{sheet.Legend}");
    }

    /// <summary>As live: memory regions ($ soft-protected), structure boxes + object labels as marks, textures, the job target.</summary>
    private static void LiveSheet(string root, string outDir, string db, WorldState w, (int X, int Y, int Z) self, (int X, int Y, int Z) focus, float yaw,
        HashSet<(int, int, int)> digSet)
    {
        var (regions, marks) = Regions(db);
        var roles = new RoleClassifier
        {
            Blocks = w.PlanningView,
            IsGenCell = p => digSet.Contains((p.X, p.Y, p.Z)),
            IsGenGeometry = p => BlockDescriber.IsGenCell(w, p),
            Verdict = (p, _) => regions.Any(r => p.X >= r.X0 && p.X <= r.X1 && p.Y >= r.Y0 && p.Y <= r.Y1 && p.Z >= r.Z0 && p.Z <= r.Z1) && !digSet.Contains((p.X, p.Y, p.Z))
                ? new ProtectionVerdict(ProtectionLevel.Soft, "structure", "part of a remembered build") : ProtectionVerdict.None,
        };
        var atlas = TextureLocator.Load(new TextureOptions { AssetsDir = Path.Combine(root, "data", "assets"), AllowDownload = false }, _ => { });
        var voxels = new WorldVoxels(w);
        voxels.Prepare(focus.X, focus.Z, 2);
        var live = new ContactSheet().Render(new ContactSheetRequest
        {
            Blocks = w.PlanningView, Focus = focus, Radius = 5, Self = self, Marks = marks, Target = focus, Atlas = atlas, Light = voxels,
            Time = 6000, Roles = roles.Map, Axis = VisionEval.AxisFor(focus, self, yaw),
        });
        File.WriteAllBytes(Path.Combine(outDir, "livegen-live.png"), live.Png);
        File.WriteAllText(Path.Combine(outDir, "livegen-live.txt"), live.Legend);
        Console.WriteLine($"live-like: {live.Width}x{live.Height} ~{live.Tokens} tok, atlas {(atlas != null)}\n{live.Legend}");
    }

    private static char Mod(int x) => (char)('0' + ((x % 10) + 10) % 10);

    private static (int X, int Y, int Z) P(string s)
    {
        var p = s.Split(',').Select(v => int.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
        return (p[0], p[1], p[2]);
    }

    private static string Copy(string db)
    {
        var dir = Path.Combine(Path.GetTempPath(), "livegen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "snap.db");
        File.Copy(db, tmp);
        if (File.Exists(db + "-wal")) File.Copy(db + "-wal", tmp + "-wal"); // recent writes live in the WAL
        return tmp;
    }

    public static WorldState Load(string db)
    {
        var tmp = Copy(db);
        var w = new WorldState { Dimension = 0 };
        using var c = new SqliteConnection($"Data Source={tmp};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT cx, cz, data, last_seen FROM world_chunks WHERE dim = 0";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            w.Remember(new RememberedColumn(r.GetInt32(0), r.GetInt32(1), 0, DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), ChunkSnapshot.Decode((byte[])r.GetValue(2))));
        return w;
    }

    internal static (List<(int X0, int Y0, int Z0, int X1, int Y1, int Z1)>, List<VisionMark>) Regions(string db)
    {
        var tmp = Copy(db);
        var regions = new List<(int, int, int, int, int, int)>();
        var marks = new List<VisionMark>();
        using var c = new SqliteConnection($"Data Source={tmp};Pooling=False");
        c.Open();
        ReadStructures(c, regions, marks);
        ReadObjects(c, marks);
        return (regions, marks);
    }

    private static void ReadStructures(SqliteConnection c, List<(int, int, int, int, int, int)> regions, List<VisionMark> marks)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, data FROM structures WHERE dimension = 0";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            using var j = JsonDocument.Parse(r.GetString(1));
            if (!j.RootElement.TryGetProperty("min", out var mn) || mn.ValueKind != JsonValueKind.Object) continue;
            var mx = j.RootElement.TryGetProperty("max", out var m2) && m2.ValueKind == JsonValueKind.Object ? m2 : mn;
            int I(JsonElement e, string k) => e.GetProperty(k).GetInt32();
            var b = (Math.Min(I(mn, "x"), I(mx, "x")), Math.Min(I(mn, "y"), I(mx, "y")), Math.Min(I(mn, "z"), I(mx, "z")),
                Math.Max(I(mn, "x"), I(mx, "x")), Math.Max(I(mn, "y"), I(mx, "y")), Math.Max(I(mn, "z"), I(mx, "z")));
            regions.Add(b);
            marks.Add(new VisionMark(r.GetString(0), b.Item1, b.Item2, b.Item3, b.Item4, b.Item5, b.Item6, Box: true));
        }
    }

    private static void ReadObjects(SqliteConnection c, List<VisionMark> marks)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT kind, min_x, min_y, min_z, max_x, max_y, max_z FROM observations WHERE dimension = 0 AND removed IS NULL " +
                          "AND kind IN ('chest','furnace','crafting_table','cobble_generator','portal','farm') AND min_x IS NOT NULL";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string label = r.GetString(0) switch { "crafting_table" => "bench", "cobble_generator" => "gen", var k => k };
            int x0 = (int)r.GetDouble(1), y0 = (int)r.GetDouble(2), z0 = (int)r.GetDouble(3), x1 = (int)r.GetDouble(4), y1 = (int)r.GetDouble(5), z1 = (int)r.GetDouble(6);
            if (marks.Any(m => Math.Abs(m.CentreX - (x0 + x1) / 2.0) <= 2 && Math.Abs(m.CentreZ - (z0 + z1) / 2.0) <= 2 && m.Box)) continue;
            marks.Add(new VisionMark(label, x0, y0, z0, x1, y1, z1, Box: x1 > x0 || z1 > z0));
        }
    }

    internal static List<(int, int, int)> DigCells(string db)
    {
        var tmp = Copy(db);
        var list = new List<(int, int, int)>();
        using var c = new SqliteConnection($"Data Source={tmp};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT fields FROM observations WHERE kind = 'cobble_generator' AND removed IS NULL AND dimension = 0";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            using var j = JsonDocument.Parse(r.GetString(0));
            if (j.RootElement.TryGetProperty("dig", out var d))
                foreach (var e in d.EnumerateArray()) list.Add((e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32()));
        }
        return list;
    }
}
