using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Perception;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Perception.Vision.Eval;

/// <summary>One spatial question about a small world, with a grader for the answer text.</summary>
public sealed record VisionEvalCase(string Id, string Title, WorldState World, (int X, int Y, int Z) Self, (int X, int Y, int Z) Focus,
    string Question, string Expected, Func<string, bool> Grade, Func<BlockPos, bool>? GenCells = null, int Radius = 6, float Yaw = 180,
    Func<BlockPos, bool>? Protected = null);

/// <summary>An eval arm: "direct" = the brain model reads the sheet itself; "perception" = a perception model + verifier.</summary>
public sealed record VisionEvalArm(string Name, string Kind, string Model)
{
    public static readonly VisionEvalArm OpusDirect = new("opus-direct", "direct", "claude-opus-5-5");
    public static readonly VisionEvalArm SonnetPerception = new("sonnet-perception", "perception", "claude-sonnet-5-5");
    public static readonly VisionEvalArm HaikuPerception = new("haiku-perception", "perception", "claude-haiku-5-5");
    /// <summary>Deterministic first (<see cref="VisionRouter"/>: voxels + roles, zero tokens), Sonnet perception for the rest.</summary>
    public static readonly VisionEvalArm RouterSonnet = new("router+sonnet", "router", "claude-sonnet-5-5");
    public static readonly IReadOnlyList<VisionEvalArm> Default = [OpusDirect, SonnetPerception, HaikuPerception, RouterSonnet];
}

public sealed record VisionEvalRow(string Case, string Arm, bool Ok, string Answer, long InputTokens, long OutputTokens, int ImageTokens,
    int BrainTokens, decimal Usd, long LatencyMs, int Checked, int Mismatched, bool RawOk, string? Error = null);

/// <summary>
/// The vision v2 eval: today's live failures as cases (floor vs gen cell, place faces in a hole, edge distance, "the middle one",
/// the portal on a void-floored nether platform) × arms (Opus reading the sheet vs Sonnet/Haiku perception + voxel verification).
/// Accuracy, tokens and $ per question. Offline it runs on recorded/fake <see cref="ILlmClient"/> answers (tests); the
/// VisionAB tool runs it live (≤20 calls, hard $ cap).
/// </summary>
public static partial class VisionEval
{
    public const string DirectSystem =
        "You are the brain of a Minecraft Beta 1.8.1 skyblock bot. Axes: +x east, -x west, -z north, +z south, +y up; 1 grid cell = 1 block. " +
        "Answer the spatial question from the contact-sheet picture (several panels of one spot; the cyan box/ring is the focus, yellow ruler " +
        "numbers are its coordinates; G = gen cell (MINE HERE), P portal, navy hatch = void; LAYERS: left = blocks AT feet height (solid = a wall beside you, not floor), right = the layer you stand on, green dot = " +
        "air with a face to place against). Think briefly, then end with one line 'ANSWER: <answer>' (coords as x,y,z).";

    [GeneratedRegex(@"(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)")]
    private static partial Regex TripleRx();

    private static HashSet<(int, int, int)> Triples(string s) =>
        TripleRx().Matches(s).Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))).ToHashSet();

    /// <summary>The answer part of a graded text: the perception text before "seen:", else all of it.</summary>
    public static string AnswerPart(string a)
    {
        var t = a.StartsWith("[vision]", StringComparison.Ordinal) ? a[8..] : a;
        int i = t.IndexOf("seen:", StringComparison.Ordinal);
        return (i > 0 ? t[..i] : t).Trim();
    }

    /// <summary>The first coordinate the answer names (its answer part first, then anywhere).</summary>
    public static (int, int, int)? FirstTriple(string a)
    {
        foreach (var part in new[] { AnswerPart(a), a })
            if (TripleRx().Match(part) is { Success: true } m) return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        return null;
    }

    /// <summary>The first standalone number of the answer part (not inside a coordinate).</summary>
    public static int? FirstNumber(string a)
    {
        var t = TripleRx().Replace(AnswerPart(a), " ");
        // Not a coordinate component ("x=4", "y62", "-3").
        var m = Regex.Match(t, @"(?<![\w.=-])(\d+)(?!\w|\.\d)");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        foreach (var (w, n) in new[] { ("one", 1), ("two", 2), ("three", 3), ("four", 4), ("five", 5), ("six", 6) })
            if (Regex.IsMatch(t, $@"\b{w}\b", RegexOptions.IgnoreCase)) return n;
        return null;
    }

    private static bool HasNumber(string s, int n) => Regex.IsMatch(s, $@"(?<![\d,-]){n}(?![\d,])") || Regex.IsMatch(s, $@"\b{NumberWord(n)}\b", RegexOptions.IgnoreCase);

    private static string NumberWord(int n) => n switch { 1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five", _ => n.ToString(CultureInfo.InvariantCulture) };

    // ------------------------------------------------------------------ cases

    private static WorldState Empty(int dim = 0)
    {
        var w = new WorldState { Dimension = dim };
        w.ApplyChunkRegion(-32, 0, -32, 64, 128, 64, new byte[64 * 128 * 64], null, null, null);
        return w;
    }

    private static void Fill(WorldState w, int x0, int x1, int y, int z0, int z1, int id)
    {
        for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) w.SetBlock(x, y, z, id, 0);
    }

    /// <summary>Live 15:55: "mine cobble" dug the island floor next to the gen instead of the gen cell.</summary>
    public static VisionEvalCase FloorVsGen()
    {
        var w = Empty();
        Fill(w, -2, 9, 62, -2, 11, Blocks.Cobblestone);            // island floor
        Fill(w, 3, 7, 63, 6, 6, Blocks.Cobblestone);               // gen back wall
        w.SetBlock(3, 63, 7, Blocks.Cobblestone, 0); w.SetBlock(7, 63, 7, Blocks.Cobblestone, 0);
        w.SetBlock(4, 63, 7, Blocks.Water, 0); w.SetBlock(6, 63, 7, Blocks.Lava, 0);
        w.SetBlock(5, 63, 7, Blocks.Cobblestone, 0);               // THE gen cell
        w.SetBlock(4, 63, 8, Blocks.Cobblestone, 0); w.SetBlock(6, 63, 8, Blocks.Cobblestone, 0);
        return new("floor_vs_gen", "floor vs gen cell", w, (5, 63, 9), (5, 63, 7),
            "I need cobblestone from the generator. Which exact block do I mine (x,y,z)? Not the island floor.", "5,63,7",
            a => FirstTriple(a) == (5, 63, 7));
    }

    /// <summary>Live: standing in its own hole at 7,62,9 the bot found no face to place a block against.</summary>
    public static VisionEvalCase HoleFaces()
    {
        var w = Empty();
        Fill(w, 2, 12, 61, 4, 14, Blocks.Cobblestone);
        Fill(w, 2, 12, 62, 4, 14, Blocks.Cobblestone);
        w.SetBlock(7, 62, 9, 0, 0);                                  // the hole the bot stands in
        w.SetBlock(6, 62, 9, 0, 0);                                  // and the cell it dug west of it
        var good = new HashSet<(int, int, int)> { (8, 62, 9), (7, 62, 8), (7, 62, 10), (7, 61, 9) };
        return new("hole_faces", "place faces in a hole", w, (7, 62, 9), (7, 62, 9),
            "I'm standing in my own hole at 7,62,9 and want to place a block to climb out. Which adjacent solid blocks can I place against? coords.",
            "8,62,9 7,62,8 7,62,10 (7,61,9 below); not 6,62,9 (air)",
            a => good.Count(g => ClaimsSolid(a, g)) >= 2 && !ClaimsSolid(a, (6, 62, 9)));
    }

    /// <summary>Edge work: how far to the void.</summary>
    public static VisionEvalCase EdgeDistance()
    {
        var w = Empty();
        Fill(w, -4, 4, 62, -2, 2, Blocks.Cobblestone);
        return new("edge_distance", "edge distance", w, (1, 63, 0), (1, 63, 0),
            "How many more floor blocks are east of me before the void edge? answer a number", "3",
            a => FirstNumber(a) == 3, Yaw: 270);
    }

    /// <summary>"Don't mine the middle one": three gen cells in a row.</summary>
    public static VisionEvalCase MiddleOne()
    {
        var w = Empty();
        Fill(w, -1, 9, 62, 2, 12, Blocks.Cobblestone);
        Fill(w, 3, 5, 63, 6, 6, Blocks.Lava);
        Fill(w, 3, 5, 63, 8, 8, Blocks.Water);
        Fill(w, 3, 5, 63, 7, 7, Blocks.Cobblestone);                // three cells x 3..5, z 7
        w.SetBlock(2, 63, 6, Blocks.Cobblestone, 0); w.SetBlock(6, 63, 6, Blocks.Cobblestone, 0);
        w.SetBlock(2, 63, 7, Blocks.Cobblestone, 0); w.SetBlock(6, 63, 7, Blocks.Cobblestone, 0);
        w.SetBlock(2, 63, 8, Blocks.Cobblestone, 0); w.SetBlock(6, 63, 8, Blocks.Cobblestone, 0);
        Fill(w, 2, 6, 63, 5, 5, Blocks.Cobblestone); Fill(w, 2, 6, 63, 9, 9, Blocks.Cobblestone);
        return new("middle_one", "the middle one", w, (4, 63, 11), (4, 63, 7),
            "Which one is the middle one of the three generator cells? coords x,y,z", "4,63,7",
            a => FirstTriple(a) == (4, 63, 7));
    }

    /// <summary>Live 15:51: the nether portal on the SkyBlock nether platform, void all around (tests/fixtures/portal-trip).</summary>
    public static VisionEvalCase? PortalVoidNether(string? netherFixture)
    {
        if (string.IsNullOrWhiteSpace(netherFixture)) return null;
        var w = new WorldState { Dimension = -1 };
        const int size = 64, x0 = -32, z0 = -32;
        var ids = new byte[size * 128 * size];
        foreach (var (x, y, z, id) in ParseFixture(netherFixture))
        {
            int lx = x - x0, lz = z - z0;
            if (lx < 0 || lz < 0 || lx >= size || lz >= size || (uint)y >= 128) continue;
            ids[(lx * size + lz) * 128 + y] = (byte)id; // ApplyChunkRegion order: x, z, y
        }
        w.ApplyChunkRegion(x0, 0, z0, size, 128, size, ids, null, null, null);
        return new("portal_void_nether", "portal on a void nether platform", w, (1, 66, 1), (2, 66, 0),
            "Where exactly is the nether portal (x range, y range, z range), and if I walk east out of it, what is there?",
            "portal x=2 y66-68 z0-1; east of it = void",
            a => Regex.IsMatch(a, @"\bvoid\b", RegexOptions.IgnoreCase) && (Regex.IsMatch(a, @"x\s*=?\s*2\b") || Triples(a).Any(t => t.Item1 == 2 && t.Item2 is >= 66 and <= 68 && t.Item3 is 0 or 1))
                 && !EastIsGround(a),
            Radius: 5, Yaw: 270);
    }

    /// <summary>A clause about "east" that calls it ground/floor (not "no floor") or "not void": the deadly misread.</summary>
    private static bool EastIsGround(string a)
    {
        foreach (var clause in a.Split(['.', ';', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Regex.IsMatch(clause, @"\beast\b", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(clause, @"\b(not|no) void\b", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(clause, @"(?<!\bno )\b(glowstone|ground|floor|solid|safe)\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(clause, @"\bvoid\b", RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// The text offers x,y,z as a solid face: some clause (split at ; . |) names it without calling it air/open/a hole and
    /// without denying it ("no walls ... @8,62,9" is a denial, not an offer).
    /// </summary>
    private static bool ClaimsSolid(string a, (int X, int Y, int Z) c)
    {
        foreach (var clause in Regex.Split(a, @";|\.(?:\s|$)|\|"))
        {
            if (!Triples(clause).Contains(c)) continue;
            if (!Regex.IsMatch(clause, @"\b(air|open|hole|empty|dug|missing|gap|no|not|none)\b", RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    public static IEnumerable<(int X, int Y, int Z, int Id)> ParseFixture(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var p = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) continue;
            yield return (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture), int.Parse(p[2], CultureInfo.InvariantCulture),
                int.Parse(p[3], CultureInfo.InvariantCulture));
        }
    }

    public static List<VisionEvalCase> Cases(string? netherFixture, string? dualGenFixture = null)
    {
        var list = new List<VisionEvalCase> { FloorVsGen(), HoleFaces(), EdgeDistance(), MiddleOne() };
        if (PortalVoidNether(netherFixture) is { } p) list.Add(p);
        if (DualGenLive(dualGenFixture) is { } g) list.Add(g);
        return list;
    }

    /// <summary>The auto-vision gen question (AutoVision.Classify "gen").</summary>
    public const string GenQuestion = "Which blocks here are generator cells (G, MINE HERE) and which are island floor or gen walls? Coords of the gen cells.";

    /// <summary>
    /// Live 10-27 21:12: the roofed dual gen on the main island (tests/fixtures/vision-eval/dual-gen.txt from the db snapshot):
    /// dig cells 4,64,6 5,64,7 6,64,8 on a diagonal, lava/water between them, a cobble roof at y66 over x3..6 z6..9.
    /// Sonnet perception: 6 claims checked, 5 wrong (the roof hid every gen cell in TOP and ISO).
    /// </summary>
    public static VisionEvalCase? DualGenLive(string? fixture)
    {
        if (string.IsNullOrWhiteSpace(fixture)) return null;
        var w = Load(fixture, 0);
        var truth = new HashSet<(int, int, int)> { (4, 64, 6), (5, 64, 7), (6, 64, 8) };
        return new("dual_gen_live", "roofed dual gen (live 21:12)", w, (3, 64, 4), (4, 64, 6), GenQuestion, "4,64,6 5,64,7 6,64,8",
            a => GenCellsExact(a, truth), GenCells: p => truth.Contains((p.X, p.Y, p.Z)), Radius: 5, // taught + regen-confirmed, as live
            // The remembered "gen housing" build (0,62,4..7,67,9): soft-protected ($) as live.
            Protected: p => p.X is >= 0 and <= 7 && p.Y is >= 62 and <= 67 && p.Z is >= 4 and <= 9);
    }

    /// <summary>A world from an "x y z id [meta]" fixture (64x64 columns around the origin).</summary>
    public static WorldState Load(string fixture, int dim)
    {
        var w = new WorldState { Dimension = dim };
        const int size = 64, x0 = -32, z0 = -32;
        var ids = new byte[size * 128 * size];
        var metas = new List<(int X, int Y, int Z, int Meta)>();
        foreach (var line in fixture.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var p = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) continue;
            int x = int.Parse(p[0], CultureInfo.InvariantCulture), y = int.Parse(p[1], CultureInfo.InvariantCulture), z = int.Parse(p[2], CultureInfo.InvariantCulture);
            int lx = x - x0, lz = z - z0;
            if (lx < 0 || lz < 0 || lx >= size || lz >= size || (uint)y >= 128) continue;
            ids[(lx * size + lz) * 128 + y] = (byte)int.Parse(p[3], CultureInfo.InvariantCulture);
            if (p.Length > 4 && int.Parse(p[4], CultureInfo.InvariantCulture) is var m and not 0) metas.Add((x, y, z, m));
        }
        w.ApplyChunkRegion(x0, 0, z0, size, 128, size, ids, null, null, null);
        foreach (var (x, y, z, m) in metas) w.SetBlock(x, y, z, w.GetBlock(x, y, z), m);
        return w;
    }

    /// <summary>
    /// Every true gen cell is named, and no clause that calls something a gen cell / the thing to mine (without negating it)
    /// names any other coordinate.
    /// </summary>
    public static bool GenCellsExact(string a, IReadOnlySet<(int, int, int)> truth)
    {
        var all = Triples(a);
        if (!truth.All(all.Contains)) return false;
        foreach (var clause in Regex.Split(a, @";|\.(?:\s|$)|\|"))
        {
            if (!Regex.IsMatch(clause, @"\b(gen|generator|G|mine)\b", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(clause, @"\b(not|no|isn't|aren't|never)\b", RegexOptions.IgnoreCase)) continue;
            if (Triples(clause).Any(t => !truth.Contains(t))) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ rendering

    public static RoleClassifier Roles(VisionEvalCase c) => new()
    {
        Blocks = c.World.PlanningView,
        IsGenCell = c.GenCells,
        IsGenGeometry = p => BlockDescriber.IsGenCell(c.World, p),
        Verdict = c.Protected is { } prot
            ? (p, _) => prot(p) ? new ProtectionVerdict(ProtectionLevel.Soft, "structure", "part of the gen housing build") : ProtectionVerdict.None
            : null,
    };

    /// <summary>The side cut the bot picks (<c>VisionHub.AxisFor</c>): the bot→focus line, else the way it faces.</summary>
    public static SideAxis AxisFor((int X, int Y, int Z) focus, (int X, int Y, int Z) self, float yaw)
    {
        int dx = focus.X - self.X, dz = focus.Z - self.Z;
        return Math.Abs(dx) + Math.Abs(dz) >= 2
            ? (Math.Abs(dx) >= Math.Abs(dz) ? SideAxis.LookNorth : SideAxis.LookEast)
            : (Math.Abs(Math.Cos(yaw * Math.PI / 180)) >= Math.Abs(Math.Sin(yaw * Math.PI / 180)) ? SideAxis.LookEast : SideAxis.LookNorth);
    }

    /// <summary>The same contact sheet the bot's <c>see</c> renders (no textures: deterministic).</summary>
    public static VisionImage Sheet(VisionEvalCase c, ContactSheet? sheets = null, bool cutAway = true)
    {
        var axis = AxisFor(c.Focus, c.Self, c.Yaw);
        return (sheets ?? new ContactSheet()).Render(new ContactSheetRequest
        {
            Blocks = c.World.PlanningView, Focus = c.Focus, Radius = c.Radius, Self = c.Self, Nether = c.World.Dimension == -1,
            Roles = Roles(c).Map, Axis = axis, Light = new WorldVoxels(c.World), CutAway = cutAway,
        });
    }

    // ------------------------------------------------------------------ running

    /// <summary>One case on one arm. Direct: the arm's model reads the sheet. Perception: model + verifier, graded on the verified text.</summary>
    public static async Task<VisionEvalRow> RunAsync(VisionEvalCase c, VisionEvalArm arm, ILlmClient llm, PriceTable prices, CancellationToken ct,
        VisionImage? sheet = null, decimal brainInputPerMTok = 4.00m)
    {
        sheet ??= Sheet(c);
        var sw = Stopwatch.StartNew();
        if (arm.Kind == "direct")
        {
            var req = new LlmRequest
            {
                Tier = ModelTier.Main, Model = arm.Model, MaxTokens = 1500, Purpose = "visioneval", CacheHistory = false, // the brain tier's own thinking/effort settings
                System = [new LlmSystemBlock(DirectSystem, Cache: true)],
                Messages = [LlmMessage.User(new LlmImageBlock(new LlmImage(sheet.Png, sheet.Width, sheet.Height, "[sheet]")),
                    new LlmText("legend: " + sheet.Legend + "\nquestion: " + c.Question))],
            };
            try
            {
                var resp = await llm.CompleteAsync(req, ct).ConfigureAwait(false);
                var text = resp.Text;
                var ans = text.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith("ANSWER:", StringComparison.OrdinalIgnoreCase))?.Trim()[7..].Trim() ?? text.Trim();
                bool ok = c.Grade(ans);
                long inTok = resp.Usage.InputTokens + resp.Usage.CacheReadTokens + resp.Usage.CacheWriteTokens;
                return new VisionEvalRow(c.Id, arm.Name, ok, ans, inTok, resp.Usage.OutputTokens, sheet.Tokens, (int)inTok, prices.Cost(resp.Model, resp.Usage),
                    sw.ElapsedMilliseconds, 0, 0, ok);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return new VisionEvalRow(c.Id, arm.Name, false, "", 0, 0, sheet.Tokens, 0, 0, sw.ElapsedMilliseconds, 0, 0, false, ex.GetType().Name + ": " + ex.Message);
            }
        }
        if (arm.Kind == "router" && VisionRouter.Answer(c.Question, c.World.PlanningView, Roles(c).Map, c.Focus, c.Radius) is { } exact)
        {
            int bt = (int)Math.Ceiling(exact.Length / 3.5);
            return new VisionEvalRow(c.Id, arm.Name, c.Grade(exact), exact, 0, 0, 0, bt, bt * brainInputPerMTok / 1_000_000m, sw.ElapsedMilliseconds, 0, 0, c.Grade(exact));
        }
        var model = new AnthropicPerceptionModel(llm, new PerceptionOptions { Model = arm.Model });
        var r = await model.AskAsync(new PerceptionQuery(sheet.Png, sheet.Width, sheet.Height, c.Question, sheet.Legend), ct).ConfigureAwait(false);
        var roles = Roles(c).Map;
        var v = PerceptionVerifier.Verify(c.Question, r, c.World.PlanningView, roles, c.Focus, c.Radius);
        var raw = (r.Answer ?? "") + " " + string.Join("; ", r.Facts.Select(f => f.Claim + " " + string.Join(" ", f.Coords.Select(x => $"{x.X},{x.Y},{x.Z}"))));
        int brainTokens = (int)Math.Ceiling(v.ModelText.Length / 3.5);
        decimal usd = prices.Cost(r.Model, r.Usage) + brainTokens * brainInputPerMTok / 1_000_000m;
        return new VisionEvalRow(c.Id, arm.Name, c.Grade(v.ModelText), v.ModelText, r.Usage.InputTokens + r.Usage.CacheReadTokens + r.Usage.CacheWriteTokens,
            r.Usage.OutputTokens, sheet.Tokens, brainTokens, usd, r.LatencyMs, v.Checked, v.Mismatched, c.Grade(raw), r.Error);
    }

    /// <summary>"arm  acc  in/q  out/q  brain/q  $/q  halluc" per arm.</summary>
    public static string Report(IReadOnlyList<VisionEvalRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("arm                acc   raw   in/q  out/q brain/q   $/q      halluc  ms/q");
        foreach (var g in rows.GroupBy(r => r.Arm))
        {
            var l = g.ToList();
            int chk = l.Sum(x => x.Checked), bad = l.Sum(x => x.Mismatched);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{g.Key,-18} {l.Count(x => x.Ok)}/{l.Count,-3} {l.Count(x => x.RawOk)}/{l.Count,-3} {l.Average(x => x.InputTokens),5:0} {l.Average(x => x.OutputTokens),5:0} {l.Average(x => x.BrainTokens),6:0}  {l.Average(x => x.Usd):0.00000}  {(chk == 0 ? "-" : $"{bad}/{chk}"),-7} {l.Average(x => x.LatencyMs),5:0}"));
        }
        return sb.ToString().TrimEnd();
    }
}
