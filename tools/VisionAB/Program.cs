// VisionAB: does the brain answer spatial questions about the real island better from text `look`, the ASCII `look map`,
// the `see` top-down image or the textured first-person `eye` image? Loads the remembered chunks + memory from a db snapshot,
// renders each arm's observation exactly as the tools would, asks the main-tier model (real API, hard $ cap) and grades.
//   dotnet run --project tools/VisionAB -- render [--db=...] [--out=...]      write the observations to disk (no API)
//   dotnet run --project tools/VisionAB -- run [--arms=text,map,see,eye] [--only=q1,q2] [--spent=0.12]
// Config: the gitignored src/MineClaw.App/appsettings.json (Agent:Llm). Keys are never printed.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

const decimal HardCapUsd = 2.00m;
var root = FindRoot();
string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith("--" + name + "=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 3)..];
var (mode, dbPath, outDir) = Paths(args, root, Arg);
Directory.CreateDirectory(outDir);
// Vision v2 (contact sheet + perception tier): built-in cases, no db snapshot.
if (await StandaloneMode(mode, root, outDir, Arg) is { } standalone) return standalone;
decimal spent = decimal.TryParse(Arg("spent"), NumberStyles.Number, CultureInfo.InvariantCulture, out var prior) ? prior : 0m;

var (world, marks) = LoadWorld(dbPath);
Console.WriteLine($"world: {world.RememberedCount} remembered columns, {marks.Count} marks from {Path.GetFileName(dbPath)}");
var atlas = LoadAtlas(root);

// The bot stands where it hangs out (by the double chest in the house), facing west toward the doors.
var view = new View(world, marks, (X: 4, Y: 64, Z: -3), 90f, 12f, TimeArg(Arg), atlas);
var (obs, renderer, voxels, eyeR) = Observe(view);

switch (mode)
{
    case "grid": Grid(world, view.Self); return 0;
    case "probe": Probe(world, Arg("at")); return 0;
    case "render": RenderFiles(view, obs, renderer, voxels, eyeR, outDir); return 0;
}
return await LiveAb(root, outDir, obs, spent, Arg, HardCapUsd);

// ------------------------------------------------------------------ helpers

static (string Mode, string DbPath, string OutDir) Paths(string[] args, string root, Func<string, string?> arg) =>
    (args.FirstOrDefault(a => !a.StartsWith("--")) ?? "render",
     arg("db") ?? Path.Combine(root, "data", "alice-snap", "mineclaw3.db"),
     arg("out") ?? Path.Combine(root, "data", "visionab"));

static long TimeArg(Func<string, string?> arg) => arg("time") is { } ts && long.TryParse(ts, out var tt) ? tt : 6000;

// The modes that need no db snapshot; null for the others.
static async Task<int?> StandaloneMode(string mode, string root, string outDir, Func<string, string?> arg)
{
    switch (mode)
    {
        case "sheets": return PerceptionMode.Sheets(root, outDir);
        case "livegen": return LiveGen.Run(root, outDir, arg);
        case "render3d": return Render3D.Run(root, outDir, arg);
        case "perception":
            if (LoadLlm(root) is not { } pllm) { Console.Error.WriteLine("no API key"); return 2; }
            return await PerceptionMode.Live(root, outDir, arg, pllm);
        default: return null;
    }
}

// Agent:Llm from the gitignored appsettings.json, the key from ANTHROPIC_API_KEY when it has none; null without a key.
static LlmOptions? LoadLlm(string root)
{
    var cfg = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "src", "MineClaw.App", "appsettings.json"), optional: true).Build();
    var llm = new LlmOptions();
    cfg.GetSection("Agent:Llm").Bind(llm);
    if (string.IsNullOrWhiteSpace(llm.ApiKey)) llm.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    return string.IsNullOrWhiteSpace(llm.ApiKey) ? null : llm;
}

static TextureAtlas? LoadAtlas(string root)
{
    var atlas = TextureLocator.Load(new TextureOptions { AssetsDir = Path.Combine(root, "data", "assets"), AllowDownload = false }, Console.WriteLine);
    Console.WriteLine(atlas == null ? "textures: none (flat palette)" : $"textures: {atlas.Size}px atlas");
    return atlas;
}

static Observation Pictured(VisionImage img) =>
    new(img.Legend + (img.Labels.Count > 0 ? " Labels: " + string.Join("; ", img.Labels.Take(8)) : ""), img);

// Every arm's observation, rendered as the tools would (top views textured and flat, the eye after a JIT warm-up).
static (Dictionary<string, Observation> Obs, VisionRenderer Renderer, WorldVoxels Voxels, EyeRenderer Eye) Observe(View v)
{
    var self = v.Self;
    var obs = new Dictionary<string, Observation>
    {
        ["text"] = TextLook(v.World, self, v.Marks),
        ["map"] = new(AsciiMap.Render(v.World.PlanningView, self.X, self.Y, self.Z, 25, self, [], v.Marks), null),
    };
    var renderer = new VisionRenderer();
    var voxels = new WorldVoxels(v.World);
    voxels.Prepare(self.X, self.Z, 3);
    var sw = Stopwatch.StartNew();
    var top = renderer.Render(v.Top(16, self.X, self.Z, voxels));
    double topMs = sw.Elapsed.TotalMilliseconds;
    obs["see"] = Pictured(top);
    obs["seeflat"] = Pictured(renderer.Render(v.Top(16, self.X, self.Z, null)));
    obs["see12"] = Pictured(renderer.Render(v.Top(12, 4, -8, voxels)));
    var eyeR = new EyeRenderer();
    var eyeScene = v.Eye(voxels, v.Time, textured: true);
    eyeR.Render(eyeScene); // warm-up (JIT)
    var eye = eyeR.Render(eyeScene);
    obs["eye"] = Pictured(eye);
    Console.WriteLine($"render: top {top.Width}x{top.Height} ~{top.Tokens}tok {topMs:0}ms ({top.Png.Length / 1024}KB); eye {eye.Width}x{eye.Height} ~{eye.Tokens}tok {eyeR.LastMs:0}ms");
    return (obs, renderer, voxels, eyeR);
}

static void Grid(WorldState world, (int X, int Y, int Z) self)
{
    for (int z = -22; z <= 12; z++)
    {
        var sb2 = new StringBuilder($"{z,4} ");
        for (int x = -16; x <= 20; x++) sb2.Append(x == self.X && z == self.Z ? '@' : AsciiMap.Cell(world.PlanningView, x, self.Y, z));
        Console.WriteLine(sb2);
    }
    Console.WriteLine("     x=-16 .. 20");
}

static void Probe(WorldState world, string? at)
{
    foreach (var spec in (at ?? "10,-8;0,-3").Split(';'))
    {
        var xz = spec.Split(',').Select(int.Parse).ToArray();
        var col = Enumerable.Range(58, 14).Select(y => (y, id: world.PlanningView.GetBlock(xz[0], y, xz[1]), m: world.PlanningView.GetMeta(xz[0], y, xz[1])))
            .Where(t => t.id != 0).Select(t => $"y{t.y}:{Blocks.Get(t.id).Name}/{t.m}");
        Console.WriteLine($"{spec}: {string.Join(" ", col)}");
    }
}

static void RenderFiles(View v, Dictionary<string, Observation> obs, VisionRenderer renderer, WorldVoxels voxels, EyeRenderer eyeR, string outDir)
{
    foreach (var (arm, o) in obs)
    {
        File.WriteAllText(Path.Combine(outDir, arm + ".txt"), o.Text);
        if (o.Image != null) File.WriteAllBytes(Path.Combine(outDir, arm + ".png"), o.Image.Png);
    }
    var self = v.Self;
    foreach (var (view, name) in new[] { (VisionView.Iso, "iso"), (VisionView.Side, "side") })
    {
        var img = renderer.Render(new VisionScene { View = view, Radius = 12, Cx = self.X, Cy = self.Y, Cz = self.Z, Blocks = v.World.PlanningView, Self = self, Marks = v.Marks, Atlas = v.Atlas, Light = voxels, Time = v.Time });
        File.WriteAllBytes(Path.Combine(outDir, name + ".png"), img.Png);
        File.WriteAllText(Path.Combine(outDir, name + ".txt"), img.Legend);
    }
    // Day vs night, and the flat-palette fallback.
    foreach (var (t, name) in new[] { (6000L, "eye-day"), (18000L, "eye-night") })
        File.WriteAllBytes(Path.Combine(outDir, name + ".png"), eyeR.Render(v.Eye(voxels, t, textured: true)).Png);
    File.WriteAllBytes(Path.Combine(outDir, "eye-flat.png"), eyeR.Render(v.Eye(voxels, v.Time, textured: false)).Png);
    File.WriteAllBytes(Path.Combine(outDir, "top-flat.png"), renderer.Render(v.Top(16, self.X, self.Z, null)).Png);
    Console.WriteLine($"wrote observations to {outDir}");
}

// ------------------------------------------------------------------ live A/B

static async Task<int> LiveAb(string root, string outDir, Dictionary<string, Observation> obs, decimal spent, Func<string, string?> arg, decimal hardCapUsd)
{
    if (LoadLlm(root) is not { } llmOptions) { Console.Error.WriteLine("no API key"); return 2; }
    var llm = new AnthropicLlmClient(llmOptions);
    string model = llmOptions.ModelFor(ModelTier.Main);
    Console.WriteLine($"model {model}; key set; hard cap ${hardCapUsd}, already spent ${spent}");

    var arms = (arg("arms") ?? "text,map,see,eye").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var only = arg("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
    var questions = Questions().Where(q => only == null || only.Contains(q.Id)).ToList();
    var results = new List<Graded>();
    var logPath = Path.Combine(outDir, $"run-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
    foreach (var q in questions)
        foreach (var arm in arms)
        {
            if (spent >= hardCapUsd - 0.05m) { Console.WriteLine($"budget cap reached (${spent:0.000}); stopping"); goto done; }
            if (await AskAsync(llm, q, arm, obs[arm], logPath) is not { } g) continue;
            spent += g.Usd;
            results.Add(g);
        }
    done:
    PrintSummary(arms, results);
    Console.WriteLine($"total ${spent:0.000} (cap ${hardCapUsd}); log {logPath}");
    return 0;
}

static string ArmInput(string arm) => arm switch
{
    "text" => """{"radius":16}""",
    "map" => """{"map":true,"radius":12}""",
    "see12" => """{"view":"top","radius":12,"focus":"4,64,-8"}""",
    "see" or "seeflat" => """{"view":"top","radius":16}""",
    _ => """{"view":"eye"}""",
};

static List<LlmToolDefinition> Tools() =>
[
    new("look", "Surroundings: footing, edges, blocks, chests, ores, players, mobs, drops. map=ASCII top-down grid.", JsonDocument.Parse("""{"type":"object","properties":{"map":{"type":"boolean"},"radius":{"type":"integer"}}}""").RootElement),
    new("see", "Picture of the world (PNG). view top=map with 1-block grid+coords; eye=first-person screenshot.", JsonDocument.Parse("""{"type":"object","properties":{"view":{"type":"string"},"radius":{"type":"integer"}}}""").RootElement),
];

// One question on one arm: the observation as the tool result, the answer graded, printed and logged; null when the call failed.
static async Task<Graded?> AskAsync(AnthropicLlmClient llm, Question q, string arm, Observation o, string logPath)
{
    const string System = "You are MineClaw, a bot on a Minecraft Beta 1.8.1 skyblock island. Axes: -Z north, +Z south, +X east, -X west; y up. " +
        "Answer the spatial question from the observation you got. You cannot call more tools now: answer anyway (best estimate if unsure). " +
        "Think briefly, then end with one line 'ANSWER: <answer>' in the format the question asks.";
    var use = new LlmToolUse("tu_1", arm is "text" or "map" ? "look" : "see", JsonDocument.Parse(ArmInput(arm)).RootElement);
    var result = new LlmToolResult("tu_1", o.Text) { Image = o.Image is { } im ? new LlmImage(im.Png, im.Width, im.Height, "[picture]") : null };
    var req = new LlmRequest
    {
        Tier = ModelTier.Main, MaxTokens = 2000, Purpose = "visionab", CacheHistory = false,
        System = [new LlmSystemBlock(System, Cache: true)],
        Tools = Tools(),
        Messages = [LlmMessage.User($"[chat] PoofimaFox: {q.Text}"), LlmMessage.Assistant(use), LlmMessage.User(result)],
    };
    LlmResponse resp;
    try { resp = await llm.CompleteAsync(req, CancellationToken.None); }
    catch (Exception ex) { Console.WriteLine($"{q.Id}/{arm}: call failed: {ex.GetType().Name} {ex.Message}"); return null; }
    decimal usd = (resp.Usage.InputTokens * 4m + resp.Usage.CacheWriteTokens * 5m + resp.Usage.CacheReadTokens * 0.4m + resp.Usage.OutputTokens * 20m) / 1_000_000m;
    var text = resp.Text;
    if (resp.ToolUses.FirstOrDefault() is { } more) text += $" (wants {more.Name} {more.Input.GetRawText()})";
    var ans = Answer(text);
    bool ok = q.Grade(ans);
    long tokensIn = resp.Usage.InputTokens + resp.Usage.CacheReadTokens + resp.Usage.CacheWriteTokens;
    Console.WriteLine($"{q.Id,-4} {arm,-5} {(ok ? "OK " : "BAD")} in {tokensIn,5} out {resp.Usage.OutputTokens,4} ${usd:0.0000}  {Trunc(ans, 70)}");
    File.AppendAllText(logPath, JsonSerializer.Serialize(new { q = q.Id, arm, ok, ans, expected = q.Expected, text, usage = resp.Usage.ToString(), usd }) + "\n");
    return new Graded(q.Id, arm, ok, ans, tokensIn, resp.Usage.OutputTokens, usd);
}

// The last "ANSWER:" line, else the whole reply.
static string Answer(string text) =>
    text.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith("ANSWER:", StringComparison.OrdinalIgnoreCase))?.Trim()[7..].Trim() ?? text.Trim();

static void PrintSummary(string[] arms, List<Graded> results)
{
    Console.WriteLine();
    Console.WriteLine("arm    acc     in/q   out/q  $/q");
    foreach (var arm in arms)
    {
        var r = results.Where(x => x.Arm == arm).ToList();
        if (r.Count == 0) continue;
        Console.WriteLine($"{arm,-5} {r.Count(x => x.Ok)}/{r.Count,-3} {r.Average(x => x.In),6:0} {r.Average(x => x.Out),6:0}  {r.Average(x => x.Usd):0.0000}");
    }
}

static string Trunc(string s, int n) => (s.Length <= n ? s : s[..n]).Replace('\n', ' ');

static string FindRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
    return d?.FullName ?? Directory.GetCurrentDirectory();
}

static (WorldState, List<VisionMark>) LoadWorld(string db)
{
    var tmp = Path.Combine(Path.GetTempPath(), "visionab-" + Guid.NewGuid().ToString("N")[..8] + ".db");
    File.Copy(db, tmp);
    var w = new WorldState { Dimension = 0 };
    var marks = new List<VisionMark>();
    using (var c = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Pooling=False"))
    {
        c.Open();
        LoadChunks(c, w);
        LoadStructures(c, marks);
        LoadWaypoints(c, marks);
        LoadObservations(c, marks);
    }
    try { File.Delete(tmp); } catch (IOException) { }
    return (w, marks);
}

static void LoadChunks(SqliteConnection c, WorldState w)
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT cx, cz, data, last_seen FROM world_chunks WHERE dim = 0";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        var data = (byte[])r.GetValue(2);
        w.Remember(new RememberedColumn(r.GetInt32(0), r.GetInt32(1), 0, DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), ChunkSnapshot.Decode(data)));
    }
}

static void LoadStructures(SqliteConnection c, List<VisionMark> marks)
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
        marks.Add(new VisionMark(r.GetString(0), Math.Min(I(mn, "x"), I(mx, "x")), Math.Min(I(mn, "y"), I(mx, "y")), Math.Min(I(mn, "z"), I(mx, "z")),
            Math.Max(I(mn, "x"), I(mx, "x")), Math.Max(I(mn, "y"), I(mx, "y")), Math.Max(I(mn, "z"), I(mx, "z")), Box: true));
    }
}

static void LoadWaypoints(SqliteConnection c, List<VisionMark> marks)
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT name, x, y, z FROM waypoints WHERE dimension = 0";
    using var r = cmd.ExecuteReader();
    while (r.Read()) marks.Add(VisionMark.At(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
}

static void LoadObservations(SqliteConnection c, List<VisionMark> marks)
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

// The text arm: what `look` + `where_is` give today (underfoot, void per direction, census, nearest things, named places).
static Observation TextLook(WorldState w, (int X, int Y, int Z) me, List<VisionMark> marks)
{
    var v = w.PlanningView;
    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture, $"at {me.X},{me.Y},{me.Z} on {Blocks.Get(v.GetBlock(me.X, me.Y - 1, me.Z)).Name}");
    var edges = VoidEdges(v, me);
    sb.Append(edges.Count > 0 ? $"; void {string.Join(" ", edges)}" : "; no void within 8");
    var (counts, nearest) = Census(v, me);
    sb.Append("; blocks: ").Append(string.Join(", ", counts.OrderByDescending(kv => kv.Value).Take(7).Select(kv => $"{Blocks.Get(kv.Key).Name} {kv.Value}")));
    sb.Append("; near: ").Append(string.Join(" ", nearest.Select(kv => $"{Blocks.Get(kv.Key).Name}@{kv.Value.X},{kv.Value.Y},{kv.Value.Z}{(kv.Value.N > 1 ? $"(x{kv.Value.N})" : "")}")));
    sb.Append(" | known places: ").Append(string.Join("; ", marks.Take(14).Select(m => $"{m.Text} {m.MinX},{m.MinY},{m.MinZ}{(m.Box ? $"..{m.MaxX},{m.MaxY},{m.MaxZ}" : "")}")));
    return new Observation(sb.ToString(), null);
}

// The first void column per direction within 8 ("N3"), stopping at a wall.
static List<string> VoidEdges(IBlockAccess v, (int X, int Y, int Z) me)
{
    var edges = new List<string>();
    foreach (var (name, dx, dz) in new[] { ("N", 0, -1), ("E", 1, 0), ("S", 0, 1), ("W", -1, 0) })
        for (int d = 1; d <= 8; d++)
        {
            int x = me.X + dx * d, z = me.Z + dz * d;
            if (v.GetBlock(x, me.Y, z) != 0 && v.GetBlock(x, me.Y + 1, z) != 0) break;
            if (VoidColumn(v, x, me.Y - 1, z)) { edges.Add($"{name}{d}"); break; }
        }
    return edges;
}

static bool VoidColumn(IBlockAccess v, int x, int top, int z)
{
    bool voidCol = true;
    for (int y = top; y >= 0 && voidCol; y--) voidCol = v.GetBlock(x, y, z) == 0;
    return voidCol;
}

// Block counts within 16 (±6 layers), and the nearest of each interesting block with how many there are.
static (Dictionary<int, int> Counts, Dictionary<int, (int X, int Y, int Z, int D, int N)> Nearest) Census(IBlockAccess v, (int X, int Y, int Z) me)
{
    var counts = new Dictionary<int, int>();
    var nearest = new Dictionary<int, (int X, int Y, int Z, int D, int N)>();
    int[] interesting = [Blocks.Chest, Blocks.Furnace, Blocks.Workbench, Blocks.WoodenDoor, Blocks.Water, Blocks.Lava, Blocks.Sapling, Blocks.Log, Blocks.Torch];
    for (int x = me.X - 16; x <= me.X + 16; x++)
        for (int z = me.Z - 16; z <= me.Z + 16; z++)
            for (int y = me.Y - 6; y <= me.Y + 6; y++)
            {
                int id = v.GetBlock(x, y, z);
                if (id == 0) continue;
                counts[id] = counts.GetValueOrDefault(id) + 1;
                if (!interesting.Contains(id)) continue;
                int d = (x - me.X) * (x - me.X) + (y - me.Y) * (y - me.Y) + (z - me.Z) * (z - me.Z);
                if (!nearest.TryGetValue(id, out var cur) || d < cur.D) nearest[id] = (x, y, z, d, cur.N + 1);
                else nearest[id] = cur with { N = cur.N + 1 };
            }
    return (counts, nearest);
}

static IEnumerable<Question> Questions() => QuestionSet.All;

sealed record Observation(string Text, VisionImage? Image);

sealed record Graded(string Q, string Arm, bool Ok, string Answer, long In, long Out, decimal Usd);

// The scene every arm is rendered from: the remembered world, its marks, where the bot stands and looks, the time and textures.
sealed record View(WorldState World, List<VisionMark> Marks, (int X, int Y, int Z) Self, float Yaw, float Pitch, long Time, TextureAtlas? Atlas)
{
    /// <summary>The top-down view around (cx, cz); textured and lit when <paramref name="light"/> is given, else the flat palette.</summary>
    public VisionScene Top(int radius, int cx, int cz, WorldVoxels? light) => light is null
        ? new VisionScene { View = VisionView.Top, Radius = radius, Cx = cx, Cy = Self.Y, Cz = cz, Blocks = World.PlanningView, Self = Self, Marks = Marks }
        : new VisionScene { View = VisionView.Top, Radius = radius, Cx = cx, Cy = Self.Y, Cz = cz, Blocks = World.PlanningView, Self = Self, Marks = Marks, Atlas = Atlas, Light = light, Time = Time };

    public EyeScene Eye(WorldVoxels voxels, long time, bool textured) => new()
    {
        World = voxels, Atlas = textured ? Atlas : null, EyeX = Self.X + 0.5, EyeY = Self.Y + 1.62, EyeZ = Self.Z + 0.5, Yaw = Yaw, Pitch = Pitch, Time = time, Marks = Marks,
    };
}
