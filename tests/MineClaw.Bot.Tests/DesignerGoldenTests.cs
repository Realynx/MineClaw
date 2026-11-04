using System.Globalization;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Blueprints.Mine;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Designs;
using BlockPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins what the web Designer and the model read from the Design Studio and the design history: every
/// <see cref="DesignerTools.Run"/> answer (text, op kind/label/cells, error) over build and mine canvases on both versions, the
/// canvas's describe/layer texts, <see cref="DesignerSave.Save"/> / <see cref="DesignerSave.LoadDesign"/> (result texts, the
/// stored record, the revisions with their DSL and block-set hash), and <see cref="DesignHistory"/>'s log/status/diff/commit/revert
/// texts over a world. A refactor must leave <see cref="DesignerGoldenData.Expected"/> unchanged. Regenerate (only for an
/// intended change) by setting WP02_GOLDEN_OUT.
/// </summary>
public class DesignerGoldenTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Hash(IEnumerable<string> parts)
    {
        ulong h = 14695981039346656037UL;
        foreach (var s in parts)
            foreach (var ch in s + ";")
            {
                h ^= ch;
                h *= 1099511628211UL;
            }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static string Cells(IReadOnlyList<CanvasCell> cells) =>
        $"{cells.Count} cells {Hash(cells.Select(c => $"{c.X},{c.Y},{c.Z},{c.Id},{c.Meta}"))} first [{string.Join(" ", cells.Take(3).Select(c => $"{c.X},{c.Y},{c.Z}={c.Id}:{c.Meta}"))}]";

    private static void Run(StringBuilder sb, DesignerCanvas c, string name, string json)
    {
        sb.Append("run ").Append(name).Append(' ').Append(json).Append(" => ");
        try
        {
            var (text, op, err) = DesignerTools.Run(c, name, J(json));
            sb.Append(err ? "ERR " : "").Append(text);
            if (op != null) sb.Append(" | op ").Append(op.Kind).Append(" '").Append(op.Label).Append("' ").Append(op.By).Append(' ').Append(Cells(op.Cells));
        }
        catch (Exception e) { sb.Append("THROW ").Append(e.GetType().Name).Append(": ").Append(e.Message); }
        sb.Append('\n');
    }

    private static void Canvas(StringBuilder sb, DesignerCanvas c, params int[] layers)
    {
        sb.Append("describe16 ").Append(c.Describe(16)).Append('\n');
        sb.Append("describe3 ").Append(c.Describe(3)).Append('\n');
        foreach (var y in layers) sb.Append("layer ").Append(y).Append('\n').Append(c.Layer(y)).Append('\n');
        sb.Append("wire ").Append(Hash(c.Wire().Select(a => string.Join(",", a)))).Append(" dug ").Append(Hash(c.DugWire().Select(a => string.Join(",", a))))
            .Append(" enc ").Append(Hash([c.Encode()])).Append(" rev ").Append(c.Rev).Append('\n');
    }

    private static readonly string[] BuildCalls =
    [
        "describe", "{}",
        "find_blocks", """{"q":"stairs"}""",
        "find_blocks", """{"q":"red","n":3}""",
        "find_blocks", """{"q":"plank","n":99}""",
        "find_blocks", """{"q":"zzqq"}""",
        "find_blocks", """{"q":"glas"}""",
        "find_blocks", "{}",
        "place", """{"b":"planks","at":[[10,0,10],[11,0,10],[12,0,10]]}""",
        "place", """{"b":"planks"}""",
        "place", """{"b":"planks","at":[1,2,3]}""",
        "place", """{"b":"planks","at":[[1,2]]}""",
        "place", """{"b":"planks","at":"x"}""",
        "place", """{"at":[[1,1,1]]}""",
        "place", """{"b":"grass","at":[[1,1,1]]}""",
        "place", """{"b":"planks","at":[[70,0,10],[-1,0,0],[5,60,5],[5,0,5]]}""",
        "box", """{"b":"cobblestone","from":[20,0,20],"to":[26,0,26]}""",
        "box", """{"b":"planks","from":[20,1,20],"to":[26,4,26],"mode":"hollow"}""",
        "box", """{"b":"stone","from":[30,0,30],"to":[33,3,33],"mode":"walls"}""",
        "box", """{"b":"stone","from":[30,0,30],"to":[33,3,33],"mode":"weird"}""",
        "box", """{"b":"stone","from":[0,0,0],"to":[63,40,63]}""",
        "box", """{"b":"stone","from":[0,0,0]}""",
        "box", """{"b":"stone","from":[0,0,0],"to":[1,1,1],"mode":5}""",
        "line", """{"b":"fence","from":[0,0,0],"to":[6,3,2]}""",
        "line", """{"b":"cobblestone","from":[40,5,40],"to":[40,5,40]}""",
        "place", """{"b":"wooden_door:south","at":[[23,1,26]]}""",
        "place", """{"b":"bed:east","at":[[22,1,22]]}""",
        "place", """{"b":"chest:north","at":[[21,1,21]]}""",
        "place", """{"b":"chest:west","at":[[21,1,22]]}""",
        "place", """{"b":"chest:west","at":[[21,1,23]]}""",
        "place", """{"b":"torch:wall=west","at":[[21,2,24]]}""",
        "place", """{"b":"torch:wall=east","at":[[21,2,24]]}""",
        "place", """{"b":"torch:floor","at":[[24,1,24]]}""",
        "place", """{"b":"torch","at":[[24,1,23]]}""",
        "place", """{"b":"torch:north","at":[[50,0,50]]}""",
        "place", """{"b":"torch:on=up","at":[[50,0,51]]}""",
        "place", """{"b":"torch:wall=sideways","at":[[50,0,51]]}""",
        "place", """{"b":"ladder:wall=south","at":[[22,2,25]]}""",
        "place", """{"b":"ladder:north","at":[[45,0,45]]}""",
        "place", """{"b":"oak_stairs:north","at":[[24,1,21],[25,1,21]]}""",
        "place", """{"b":"cobblestone_stairs:east","at":[[24,2,21]]}""",
        "place", """{"b":"wool:red","at":[[24,3,21]]}""",
        "place", """{"b":"log:spruce","at":[[25,3,21]]}""",
        "place", """{"b":"glass","at":[[20,2,23],[20,3,23]]}""",
        "replace", """{"a":"oak_stairs","b":"cobblestone_stairs"}""",
        "replace", """{"a":"wool","b":"wool:blue","from":[20,0,20],"to":[26,4,26]}""",
        "replace", """{"a":"any","b":"stone_bricks","from":[30,0,30],"to":[31,1,31]}""",
        "replace", """{"a":"glass","b":"planks","from":[0,0,0]}""",
        "replace", """{"b":"planks"}""",
        "replace", """{"a":"nothing_here","b":"planks"}""",
        "remove", """{"from":[30,-3,30],"to":[33,-1,33]}""",
        "remove", """{"from":[31,0,31],"to":[32,3,32]}""",
        "remove", """{"from":[31,0,31]}""",
        "box", """{"b":"cobblestone","from":[30,-4,30],"to":[33,-4,33]}""",
        "place", """{"b":"cobblestone_stairs:north","at":[[31,-3,31]]}""",
        "place", """{"b":"earth","at":[[32,-1,32],[40,-1,40],[40,3,40]]}""",
        "place", """{"b":"ground","at":[[33,-1,33]]}""",
        "remove", """{"from":[23,1,26],"to":[23,1,26]}""",
        "remove", """{"from":[22,1,22],"to":[22,1,22]}""",
        "spiral_shaft", """{"at":[1,1],"bottom":5}""",
        "nope", "{}",
        "describe", """{"y":0}""",
        "describe", """{"y":-3}""",
        "describe", """{"y":"x"}""",
    ];

    private static readonly string[] MineCalls =
    [
        "describe", "{}",
        "spiral_shaft", """{"at":[30,30],"bottom":11,"size":4,"core":2,"torch_every":6,"landings":[11,20,"x"]}""",
        "spiral_shaft", """{"at":[5,5],"bottom":30,"size":9,"core":1,"dir":"ccw","top":60}""",
        "spiral_shaft", """{"at":[5,5],"bottom":30,"size":1}""",
        "spiral_shaft", """{"at":[5],"bottom":30}""",
        "spiral_shaft", """{"bottom":30}""",
        "strip_mine", """{"at":[34,11,31],"length":20,"corridor":"2x3","spacing":3,"branch_length":6}""",
        "strip_mine", """{"at":[10,14,50],"axis":"z","length":12,"sides":"left","torch_every":4}""",
        "strip_mine", """{"at":[10,20,10],"axis":"x","length":8,"sides":"none","corridor":"1x2"}""",
        "strip_mine", """{"at":[10,20,10],"sides":"right","spacing":1,"branch_length":3,"length":10}""",
        "strip_mine", """{"length":10}""",
        "mine_preset", """{"id":"nope"}""",
        "mine_preset", "{}",
        "remove", """{"from":[40,2,40],"to":[41,3,41]}""",
        "remove", """{"from":[40,40,40],"to":[41,41,41]}""",
        "place", """{"b":"torch:wall=north","at":[[34,12,32]]}""",
        "place", """{"b":"earth","at":[[34,11,31]]}""",
        "describe", """{"y":11}""",
    ];

    private static void Script(StringBuilder sb, DesignerCanvas c, string[] calls)
    {
        for (int i = 0; i < calls.Length; i += 2) Run(sb, c, calls[i], calls[i + 1]);
    }

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var v in new[] { "b1.8.1", "1.12.2" })
        {
            sb.Append("== build ").Append(v).Append('\n');
            sb.Append("system ").Append(DesignerTools.System("PoofImaFox", v)).Append('\n');
            var c = new DesignerCanvas { Version = v };
            Script(sb, c, BuildCalls);
            Canvas(sb, c, 0, 1, -1, -3, -4);
            Run(sb, c, "clear", "{}");
            Canvas(sb, c);
            sb.Append("== mine ").Append(v).Append('\n');
            var m = new DesignerCanvas { Kind = DesignerCanvas.KindMine, Seed = "4242", Version = v };
            sb.Append("system ").Append(DesignerTools.MineSystem("PoofImaFox", m)).Append('\n');
            Script(sb, m, MineCalls);
            foreach (var p in MinePresets.All)
            {
                var pc = new DesignerCanvas { Kind = DesignerCanvas.KindMine, Seed = "77", Version = v };
                Run(sb, pc, "mine_preset", $$"""{"id":"{{p.Id}}"}""");
            }
            Canvas(sb, m, 11, 12);
            Run(sb, m, "clear", "{}");
            Canvas(sb, m);
        }
        foreach (var s in new[] { "torch:wall=west", "torch:on=east", "torch:against-north", "torch:support_south", "ladder:wall=north", "ladder:east",
                     "torch:floor", "torch:ground", "ladder:floor", "torch:5", "torch:north", "stairs:north", "torch", "ladder:wall=up", "torch:x=y" })
        {
            sb.Append("attached ").Append(s).Append(" => ");
            try { sb.Append(DesignerTools.Attached(s)?.ToString() ?? "null"); }
            catch (Exception e) { sb.Append("THROW ").Append(e.Message); }
            sb.Append('\n');
        }
        DumpSaves(sb);
        DumpHistory(sb);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ saves and loads

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static BlueprintService Designs(AgentDb db, TimeProvider t)
    {
        var s = new BlueprintService(new BlueprintOptions(), new BlueprintStore(db), new BlueprintPreview())
        {
            Site = () => throw new InvalidOperationException("no site in tests"), Crew = () => throw new InvalidOperationException(),
            ResolveSite = (_, _) => (null, "n/a"), Trusted = () => ["poofimafox"],
        };
        s.History = new DesignHistory(s, new DesignRevisions(db, t), t);
        return s;
    }

    private static void Rec(StringBuilder sb, BlueprintService s, long id)
    {
        if (s.Store.Get(id) is not { } r) { sb.Append("  rec #").Append(id).Append(" none\n"); return; }
        sb.Append(CultureInfo.InvariantCulture, $"  rec #{r.Id} '{r.Name}' status {r.Status} origin {r.Origin?.ToString() ?? "-"} by {r.Requester}/{r.AcceptedBy} cost {r.CostUsd}\n");
        sb.Append("  desc ").Append(r.Description).Append("\n  anchor ").Append(r.Anchor).Append("\n  summary ").Append(r.Summary).Append("\n  eval ").Append(r.Eval).Append('\n');
        sb.Append("  bill ").Append(string.Join("; ", r.Bill.Select(b => $"{b.Item} {b.Need}/{b.Have}/{b.Stored}/{b.Missing} {b.Source}"))).Append('\n');
        sb.Append("  dsl\n").Append(r.Dsl.ReplaceLineEndings("\n")).Append("\n  /dsl\n");
        foreach (var rv in s.History!.Revisions.List(id))
        {
            var snap = s.History.Revisions.Snapshot(id, rv.Rev);
            sb.Append(CultureInfo.InvariantCulture,
                $"  r{rv.Rev} {rv.Author} {rv.AuthorKind} '{rv.Message}' tag {rv.Tag ?? "-"} origin {rv.Origin?.ToString() ?? "-"} hash {rv.Hash} blocks {rv.Blocks} dsl {Hash([rv.Dsl.ReplaceLineEndings("\n")])} snap {snap?.Count} {snap?.Air.Count} {(snap == null ? "-" : Hash([snap.Hash()]))}\n");
        }
    }

    private static void Save(StringBuilder sb, BlueprintService s, DesignerSessionRecord session, string name, string? summary, string by, bool asNew)
    {
        var r = DesignerSave.Save(s, session, name, summary, by, asNew, "maid_chan");
        sb.Append(CultureInfo.InvariantCulture, $"save '{name}' new={asNew} => ok {r.Ok} #{r.DesignId} r{r.Rev} blocks {r.Blocks} created {r.Created}: {r.Text}\n");
        sb.Append(CultureInfo.InvariantCulture, $"  session #{session.DesignId} '{session.Name}' off {session.Offset}\n");
        if (r.Ok) Rec(sb, s, r.DesignId);
    }

    private static DesignerLoad Load(StringBuilder sb, BlueprintService s, long id, int? rev = null)
    {
        var l = DesignerSave.LoadDesign(s, id, rev);
        sb.Append(CultureInfo.InvariantCulture,
            $"load #{id} r{rev?.ToString(CultureInfo.InvariantCulture) ?? "-"} => why {l.Why ?? "-"} off {l.Offset} anchor {l.Anchor?.ToString() ?? "-"} pref {l.PreferredFor ?? "-"} rev {l.Rev} design {l.Design?.Id.ToString(CultureInfo.InvariantCulture) ?? "-"}\n");
        if (l.Canvas is { } c)
        {
            sb.Append("  ").Append(c.Kind).Append(' ').Append(c.Version).Append(' ').Append(c.Seed).Append('\n');
            Canvas(sb, c, 0, -1);
        }
        return l;
    }

    private static void DumpSaves(StringBuilder sb)
    {
        foreach (var v in new[] { "b1.8.1", "1.12.2" })
        {
            sb.Append("== saves ").Append(v).Append('\n');
            using var db = AgentDb.Open(null);
            var t = new Clock();
            var s = Designs(db, t);
            var c = new DesignerCanvas { Version = v };
            var session = new DesignerSessionRecord { Canvas = c, Owner = "poofimafox", CostUsd = 0.25m };
            Save(sb, s, session, "  ", null, "poofimafox", false);
            Save(sb, s, session, "empty", null, "poofimafox", false);
            Script(sb, c, [
                "box", """{"b":"cobblestone","from":[30,0,30],"to":[34,0,34]}""",
                "box", """{"b":"planks","from":[30,1,30],"to":[34,3,34],"mode":"walls"}""",
                "place", """{"b":"wooden_door:south","at":[[32,1,34]]}""",
                "place", """{"b":"torch:wall=west","at":[[31,2,32]]}""",
                "place", """{"b":"oak_stairs:north","at":[[33,1,31]]}""",
                "place", """{"b":"glass","at":[[30,2,32],[34,2,32]]}""",
                "box", """{"b":"planks","from":[30,4,30],"to":[34,4,34]}""",
            ]);
            Save(sb, s, session, "  \"Fox's #hut\"\n ", "first", "poofimafox", false);
            var id = session.DesignId!.Value;
            t.Now = t.Now.AddMinutes(5);
            Script(sb, c, [
                "remove", """{"from":[31,-3,31],"to":[33,-1,33]}""",
                "box", """{"b":"cobblestone","from":[31,-4,31],"to":[33,-4,33]}""",
                "place", """{"b":"ladder:wall=north","at":[[32,-3,32]]}""",
            ]);
            session.Anchor = new BlockPos(32, 0, 30);
            Save(sb, s, session, "Fox's hut", "  ", "kiwi", false);
            Save(sb, s, session, "Fox's hut", "unchanged", "kiwi", false);
            var copy = new DesignerSessionRecord { Canvas = c, Owner = "kiwi", DesignId = id };
            Save(sb, s, copy, "a very long design name that goes on and on past forty-eight characters", "copy", "kiwi", true);
            var l = Load(sb, s, id);
            Load(sb, s, id, 1);
            Load(sb, s, id, 99);
            Load(sb, s, 999);
            var s2 = new DesignerSessionRecord { Canvas = l.Canvas!, Owner = "poofimafox", DesignId = id, Offset = l.Offset, Anchor = l.Anchor };
            DesignerTools.Run(s2.Canvas, "place", J("""{"b":"wool:red","at":[[32,5,32]]}"""));
            Save(sb, s, s2, "Fox's hut", "a red block on the roof", "poofimafox", false);
            var back = Load(sb, s, id, 1);
            var s3 = new DesignerSessionRecord { Canvas = back.Canvas!, Owner = "poofimafox", DesignId = id, Offset = back.Offset };
            Save(sb, s, s3, "Fox's hut", "revert to r1", "poofimafox", false);
            sb.Append("log ").Append(s.History!.Log(s.Store.Get(id)!)).Append('\n');
            foreach (var what in new[] { "r1 r2", "r1..r4", "r4 r1", "head~1 head", "r1 r9", "first, last", "r2", "", "nope" })
                sb.Append("diff [").Append(what).Append("] ").Append(s.History.Diff(s.Store.Get(id)!, what)).Append('\n');
            sb.Append("status ").Append(s.History.Status(s.Store.Get(id)!)).Append('\n');
            foreach (var spec in new[] { "", "r2", "2", "rev 2", "HEAD", "head~2", "head^", "head-", "previous", "first", "original", "good", "r77", "#r1" })
                sb.Append("resolve [").Append(spec).Append("] ").Append(s.History.Revisions.Resolve(id, spec)?.Rev.ToString(CultureInfo.InvariantCulture) ?? "null").Append('\n');
            // Too big for a design.
            var big = new DesignerCanvas { Version = v };
            DesignerTools.Run(big, "line", J("""{"b":"stone","from":[0,0,0],"to":[40,0,0]}"""));
            Save(sb, s, new DesignerSessionRecord { Canvas = big, Owner = "poofimafox" }, "long wall", null, "poofimafox", false);
            // A mine.
            var m = new DesignerCanvas { Kind = DesignerCanvas.KindMine, Seed = "4242", Version = v };
            var ms = new DesignerSessionRecord { Canvas = m, Owner = "poofimafox", PreferredFor = v };
            Save(sb, s, ms, "deep strip", null, "poofimafox", false);
            DesignerTools.Run(m, "spiral_shaft", J("""{"at":[30,30],"bottom":11,"size":4,"core":2,"torch_every":6,"landings":[11]}"""));
            DesignerTools.Run(m, "strip_mine", J("""{"at":[34,11,31],"length":20,"corridor":"2x3","spacing":3,"branch_length":6}"""));
            Save(sb, s, ms, "deep strip", "the first cut", "poofimafox", false);
            var mid = ms.DesignId!.Value;
            var ml = Load(sb, s, mid);
            var ms2 = new DesignerSessionRecord { Canvas = ml.Canvas!, Owner = "poofimafox", DesignId = mid, Anchor = ml.Anchor, PreferredFor = ml.PreferredFor };
            ms2.Canvas.Apply(ms2.Canvas.Dug.Where(p => p.X >= 34).Select(p => new CanvasCell(p.X, p.Y, p.Z, DesignerCanvas.Earth, 0)).ToList());
            Save(sb, s, ms2, "deep strip", "shaft only", "kiwi", false);
            Load(sb, s, mid, 1);
            var mn = new DesignerCanvas { Kind = DesignerCanvas.KindMine, Seed = "9", Version = v };
            DesignerTools.Run(mn, "remove", J("""{"from":[10,20,10],"to":[12,21,12]}"""));
            Save(sb, s, new DesignerSessionRecord { Canvas = mn, Owner = "poofimafox" }, "pocket", null, "poofimafox", false);
        }
    }

    // ------------------------------------------------------------------ history over a world (commit, revert)

    private static void DumpHistory(StringBuilder sb)
    {
        sb.Append("== history\n");
        using var db = AgentDb.Open(null);
        var t = new Clock();
        var w = StructureCaptureTests.Terrain();
        for (int x = 0; x <= 2; x++) for (int z = 0; z <= 2; z++) w.SetBlock(x, 64, z, Blocks.Planks, 0);
        w.SetBlock(1, 65, 1, Blocks.Cobblestone, 0);
        w.SetBlock(1, 66, 1, Blocks.Cobblestone, 0);
        w.SetBlock(0, 65, 1, Blocks.WoodenStairs, 0);
        var store = new BlueprintStore(db);
        var started = new List<long>();
        var s = new BlueprintService(new BlueprintOptions(), store, new BlueprintPreview())
        {
            Site = () => BlueprintTests.Site(w), Crew = () => throw new InvalidOperationException(),
            ResolveSite = (_, _) => (new SitePick(new BlockPos(-20, 63, 0), "over there"), null), Trusted = () => ["PoofImaFox"],
        };
        bool refuse = false;
        s.StartBuild = (id, _) => { started.Add(id); return refuse ? "busy" : null; };
        var memory = new MemoryStore(db, null);
        var e = new EditContext { Trusted = () => ["PoofImaFox"], IsBot = n => n == "maid_chan" };
        s.Edits = e;
        IBlockAccess? view = w.PlanningView;
        s.History = new DesignHistory(s, new DesignRevisions(db, t), t) { World = () => view, Edits = e, Memory = memory, BotName = () => "maid_chan" };
        var h = s.History;
        var cap = StructureCapture.Capture(new CaptureRequest { Name = "stairs", World = w.PlanningView, Corners = (new BlockPos(0, 64, 0), new BlockPos(2, 66, 2)) });
        var (rec0, text) = DesignCapture.Save(s, cap, "PoofImaFox", (r, c) => DesignCapture.Link(memory, w, r, c));
        sb.Append("capture ").Append(text).Append('\n');
        var id = rec0!.Id;
        BlueprintRecord R() => store.Get(id)!;
        Rec(sb, s, id);
        void Line(string what, (bool Ok, string Text) r) => sb.Append(what).Append(" => ").Append(r.Ok).Append(' ').Append(r.Text).Append('\n');
        sb.Append("status ").Append(h.Status(R())).Append('\n');
        Line("commit clean", h.Commit(R(), "PoofImaFox", null));
        Line("revert clean", h.Revert(R(), null, "PoofImaFox"));
        t.Now = t.Now.AddMinutes(3);
        w.SetBlock(1, 66, 1, 0, 0);
        w.SetBlock(2, 65, 2, Blocks.Cobblestone, 0);
        w.SetBlock(0, 65, 1, Blocks.WoodenStairs, 2);
        w.SetBlock(0, 64, 0, 0, 0);
        w.SetBlock(2, 65, 0, Blocks.Bed, 0);
        sb.Append("status ").Append(h.Status(R())).Append('\n');
        sb.Append("diff ").Append(h.Diff(R(), null)).Append('\n');
        sb.Append("diff r1 ").Append(h.Diff(R(), "r1 vs world")).Append('\n');
        Line("commit griefer", h.Commit(R(), "griefer", null));
        Line("commit fox", h.Commit(R(), "PoofImaFox", "#good a step on the landing"));
        Rec(sb, s, id);
        sb.Append("diff r1 r2 ").Append(h.Diff(R(), "r1 r2")).Append('\n');
        Line("revert griefer", h.Revert(R(), "r1", "griefer"));
        Line("revert r9", h.Revert(R(), "r9", "PoofImaFox"));
        view = null;
        Line("revert no world", h.Revert(R(), "r1", "PoofImaFox"));
        Line("commit no world", h.Commit(R(), "PoofImaFox", null));
        sb.Append("status no world ").Append(h.Status(R())).Append('\n');
        sb.Append("diff no world ").Append(h.Diff(R(), "r1")).Append('\n');
        view = w.PlanningView;
        Line("revert r1", h.Revert(R(), "r1", "PoofImaFox"));
        Rec(sb, s, id);
        w.SetBlock(1, 65, 1, 0, 0);
        refuse = true;
        Line("revert prev refused", h.Revert(R(), "", "PoofImaFox"));
        Line("revert head", h.Revert(R(), "head", "PoofImaFox"));
        sb.Append("started ").Append(string.Join(",", started)).Append('\n');
        sb.Append("log ").Append(h.Log(R(), 2)).Append('\n');
        var structure = memory.Read(d => d.Structures.Select(x => $"{x.Name} {x.Min} {x.Max} [{string.Join(" | ", x.History)}]").ToList());
        sb.Append("memory ").Append(string.Join("; ", structure)).Append('\n');
        // Commit by nobody over the bot's own change.
        h.AllOwn = (_, _) => true;
        h.Causes = (_, _) => "me 1";
        w.SetBlock(1, 67, 1, Blocks.Glass, 0);
        sb.Append("status own ").Append(h.Status(R())).Append('\n');
        Line("commit own", h.Commit(R(), null, "#tagonly"));
        Line("commit own2", h.Commit(R(), null, null));
        Rec(sb, s, id);
    }

    /// <summary>Every version spelling (pinned before <see cref="DesignVersions.Normalize"/> became a table).</summary>
    [Theory]
    [InlineData("b1.8.1", "b1.8.1"), InlineData(" B181 ", "b1.8.1"), InlineData("beta", "b1.8.1"), InlineData("b1.8", "b1.8.1"), InlineData("Beta 1.8.1", "b1.8.1"),
     InlineData("beta1.8.1", "b1.8.1"), InlineData("1.12.2", "1.12.2"), InlineData("1.12", "1.12.2"), InlineData("1122", "1.12.2"), InlineData("RELEASE", "1.12.2"),
     InlineData(null, null), InlineData("", null), InlineData("1.8", null), InlineData("beta 1.8", null), InlineData("b 1.8.1", null), InlineData("1.12.1", null)]
    public void Version_spellings_normalize(string? spelling, string? version) => Assert.Equal(version, DesignVersions.Normalize(spelling));

    [Fact]
    public void Designer_tools_saves_and_history_match_the_golden_dump()
    {
        var actual = Dump().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        var expected = DesignerGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n');
        actual = actual.TrimEnd('\n');
        if (expected == actual) return;
        var a = actual.Split('\n');
        var e = expected.Split('\n');
        int i = 0;
        while (i < Math.Min(a.Length, e.Length) && a[i] == e[i]) i++;
        Assert.Fail($"designer dump differs at line {i + 1}:\n expected: {(i < e.Length ? e[i] : "<end>")}\n actual:   {(i < a.Length ? a[i] : "<end>")}");
    }
}
