using System.Text;
using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using static MineClaw.Hive.Tests.StoryGoldenTests;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Behaviour pins for the Story director's op handlers and job picker (docs/dev/complexity.md WP01): scripted sessions on a fixed
/// clock whose every answer, the jobs pawns get and the state the web reads (<c>State()</c>, full and delta) are compared with goldens
/// written by the code before it was reshaped (see <see cref="StoryGoldenTests"/>).
/// </summary>
public sealed class StoryOpsGoldenTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly P3 Chest = new(0, 64, 9), Chest2 = new(4, 64, 9), Table = new(3, 64, -3), Furnace = new(5, 64, -3);

    private sealed class Session
    {
        public required StoryColony C;
        public required FakeStoryHost H;
        public required FakeStoryWorld W;
        public required Clock T;
        public readonly StringBuilder Sb = new();
        public List<PawnState> Ps => C.Pawns.Values.ToList();

        public void Rec(string label, (JsonNode? Result, string? Error) r) => Sb.Append(label).Append(" => ").Append(R(r)).Append('\n');
        public void Line(string label, JsonNode? n) => Sb.Append(label).Append(" => ").Append(J(n)).Append('\n');

        public void Store(P3 chest, params Stack[] stock)
        {
            if (C.StorageAt(chest) == null) Rec("storage set " + chest, C.StorageOp("set", [chest], null, null, null));
            foreach (var s in stock) C.AddContents(C.StorageAt(chest)!, s);
        }

        /// <summary>Rounds of picks: each pawn's next job is recorded, then reported done (or failed every third), a second passes.</summary>
        public void Run(int rounds, string tag)
        {
            for (int r = 0; r < rounds; r++)
            {
                for (int i = 0; i < C.Pawns.Count; i++)
                {
                    var name = C.P(i);
                    var job = C.Next(name);
                    Line($"{tag} r{r} {name} job", job);
                    if (job?["id"]?.GetValue<string>() is { } id)
                        Sb.Append("  report ").Append(C.Report(name, id, (r + i) % 3 == 2 ? "failed" : "done", (r + i) % 3 == 2 ? "blocked" : null, null)).Append('\n');
                }
                T.Now += TimeSpan.FromSeconds(1);
            }
        }

        public void State(string tag, long? since = 0)
        {
            var s = C.State(since);
            s.Remove("categories");
            s.Remove("plants");
            Line(tag + " state", s);
        }
    }

    private static Session Make(int pawns, IGameData? data = null)
    {
        var t = new Clock(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var (c, h, w) = StoryRig.Make(pawns, data, t);
        c.Rng = new Random(1);
        return new Session { C = c, H = h, W = w, T = t };
    }

    private static JsonArray A(params int[] v) => new(v.Select(x => (JsonNode)x).ToArray());

    // ================================================================ gear: bulk ops, policies, material rules

    [Fact]
    public void Gear_policies_and_rules()
    {
        var s = Make(3);
        var ps = s.Ps;
        foreach (var (p, lvl) in ps.Zip(new[] { 2, 10, 5 }))
        {
            p.Skills[WorkTypes.Guard] = new SkillState { Level = lvl };
            p.Skills[WorkTypes.Hunt] = new SkillState { Level = 0 };
            p.Skills[WorkTypes.Mine] = new SkillState { Level = 7 - lvl / 2 };
        }
        ps[2].Priorities[WorkTypes.Mine] = 1;
        ps[1].Priorities[WorkTypes.Chop] = 0;
        s.H.Lives[ps[0].Name] = FakeStoryHost.Bare(0, 64, 0, new Stack(297, 0, 2)) with { Gear = [new GearSlot(5, 298, 0, 1), new GearSlot(36, 268, 0, 1), new GearSlot(37, 270, 50, 1)] };
        s.H.Lives[ps[1].Name] = FakeStoryHost.Bare(2, 64, 0) with { Gear = [new GearSlot(38, 306, 0, 1)] };
        s.H.Lives[ps[2].Name] = FakeStoryHost.Bare(4, 64, 0);
        s.Store(Chest, new Stack(272, 0, 1), new Stack(267, 0, 1), new Stack(276, 0, 1), new Stack(310, 0, 1), new Stack(306, 0, 1),
            new Stack(311, 0, 1), new Stack(307, 0, 1), new Stack(301, 0, 1), new Stack(297, 0, 4), new Stack(274, 0, 2), new Stack(275, 0, 1),
            new Stack(273, 0, 1), new Stack(257, 200, 1), new Stack(359, 0, 1), new Stack(261, 0, 1));
        s.Store(Chest2, new Stack(320, 0, 6), new Stack(256, 0, 1), new Stack(291, 0, 1));

        // Policies and rules first (they steer the gear ops).
        JsonObject Pol(JsonObject spec) => new() { ["op"] = "set", ["policy"] = spec };
        foreach (var body in new[]
                 {
                     Pol(new JsonObject { ["name"] = "Fighter", ["weapon"] = "sword", ["armor"] = "always", ["food"] = 4, ["tools"] = new JsonArray("pickaxe", "axe", "pickaxe") }),
                     Pol(new JsonObject { ["name"] = "Archer", ["weapon"] = "bow", ["armor"] = "night", ["spare"] = true, ["tools_by_work"] = false }),
                     Pol(new JsonObject { ["name"] = "X", ["weapon"] = "laser" }), Pol(new JsonObject { ["name"] = "" }),
                     Pol(new JsonObject { ["name"] = "Y", ["armor"] = "maybe" }), Pol(new JsonObject { ["name"] = "Z", ["food"] = 20 }),
                     Pol(new JsonObject { ["name"] = "Z", ["food"] = "x" }), Pol(new JsonObject { ["name"] = "Z", ["spare"] = "yes" }),
                     Pol(new JsonObject { ["name"] = "Z", ["tools_by_work"] = 1 }), Pol(new JsonObject { ["name"] = "Z", ["tools"] = new JsonArray("spoon") }),
                     Pol(new JsonObject { ["name"] = "Z", ["tools"] = "axe" }), Pol(new JsonObject { ["id"] = "nope", ["name"] = "Z" }),
                     Pol(new JsonObject { ["id"] = "pol1", ["armor"] = "none", ["food"] = 0 }),
                     new JsonObject { ["op"] = "set" }, new JsonObject { ["op"] = "explode" },
                     new JsonObject { ["op"] = "assign", ["id"] = "pol1", ["pawns"] = new JsonArray(ps[0].Name) },
                     new JsonObject { ["op"] = "assign", ["id"] = "pol2", ["pawns"] = new JsonArray(ps[1].Name, "nobody") },
                     new JsonObject { ["op"] = "assign", ["id"] = "pol2", ["pawns"] = new JsonArray(ps[1].Name) },
                     new JsonObject { ["op"] = "assign", ["id"] = "nope", ["pawns"] = new JsonArray(ps[2].Name) },
                     new JsonObject { ["op"] = "remove", ["id"] = StoryColony.DefaultPolicy }, new JsonObject { ["op"] = "remove", ["id"] = "nope" },
                 })
            s.Rec("policies " + body.ToJsonString(), s.C.PoliciesOp(body));
        JsonObject Rule(string m, string? tool = null, string? tier = null, string? enchant = null, string? fallback = null)
        {
            var r = new JsonObject { ["material"] = m };
            if (tool != null) r["tool"] = tool;
            if (tier != null) r["tier"] = tier;
            if (enchant != null) r["enchant"] = enchant;
            if (fallback != null) r["fallback"] = fallback;
            return new JsonObject { ["op"] = "set", ["rule"] = r };
        }
        foreach (var body in new[]
                 {
                     Rule("group:ores", tier: "stone"), Rule("iron_ore", tier: "iron"), Rule("iron_ore", tier: "wood"), Rule("iron_ore", tool: "shovel"),
                     Rule("obsidian", tier: "iron"), Rule("unobtainium"), Rule("iron_ore", enchant: "fortune"), Rule("stone", tool: "spoon"),
                     Rule("stone", tier: "mithril"), Rule("dirt", tool: "shovel", fallback: "wait"), Rule("dirt", fallback: "never"), Rule("log", tool: "axe"),
                     new JsonObject { ["op"] = "set" }, new JsonObject { ["op"] = "remove", ["material"] = "log" }, new JsonObject { ["op"] = "remove", ["material"] = "x" },
                     new JsonObject { ["op"] = "nope" },
                 })
            s.Rec("rules " + body.ToJsonString(), s.C.MaterialRulesOp(body));
        s.Line("gear options", s.C.SetGearOptions(true, new JsonObject { ["count"] = 1, ["kinds"] = new JsonArray("pickaxe") }));
        s.Line("gear options bad", s.C.SetGearOptions(null, new JsonObject { ["count"] = 99 }));

        foreach (var op in new[] { "arm", "armor", "return", "tools", "dance" })
        {
            s.Rec("gear " + op, s.C.GearOp(op, ps, new JsonObject()));
            s.Rec("gear " + op + " one", s.C.GearOp(op, [ps[2]], new JsonObject()));
            s.Rec("gear " + op + " none", s.C.GearOp(op, [], new JsonObject()));
        }
        foreach (var body in new[]
                 {
                     new JsonObject { ["item"] = A(297, 0), ["count"] = 3 }, new JsonObject { ["item"] = "bread" }, new JsonObject { ["item"] = "nonsense" },
                     new JsonObject { ["item"] = A(297) }, new JsonObject { ["item"] = A(297, 0), ["count"] = 99 }, new JsonObject { ["item"] = A(297, 0), ["count"] = "x" },
                     new JsonObject { ["item"] = A(310, 0) }, new JsonObject { ["item"] = A(267, 0), ["count"] = 2 }, new JsonObject { ["item"] = A(320, -1), ["count"] = 5 },
                     new JsonObject { ["item"] = A(9999, 0) }, new JsonObject(), new JsonObject { ["item"] = "iron_shovel" },
                 })
            s.Rec("gear pickup " + body.ToJsonString(), s.C.GearOp("pickup", ps, body));
        s.State("after gear");
        s.Run(6, "gear");
        s.C.Draft([ps[0]], true);
        s.Run(2, "drafted");
        s.C.Draft([ps[0]], false);
        s.T.Now += TimeSpan.FromHours(9); // night: armour by policy
        s.Run(3, "night");
        s.State("end", null);
        Golden("ops-gear", s.Sb.ToString());
    }

    // ================================================================ zones

    [Fact]
    public void Zones()
    {
        var s = Make(2);
        for (int x = 20; x < 24; x++) for (int z = 0; z < 4; z++) s.W.Set(x, 63, z, 9); // a pond
        for (int x = -10; x < -4; x++) for (int z = -10; z < -4; z++) s.W.Set(x, 63, z, 2); // grass to farm
        (string, string?, Area?, string?, string?, JsonObject?)[] calls =
        [
            ("add", "home", Area.Of(-3, 64, -3, 3, 66, 3), null, "House", null),
            ("add", "home", Area.Of(4, 64, -3, 6, 66, 3), null, null, null),
            ("add", "dump", Area.Of(-8, 64, -8, -6, 64, -6), null, null, null),
            ("add", "farm", Area.Of(-10, 63, -10, -5, 64, -5), null, "Wheat", new JsonObject { ["plant"] = "wheat" }),
            ("add", null, Area.Of(-10, 63, -4, -5, 64, -4), null, null, new JsonObject { ["plant"] = "wheat" }),
            ("add", "grow", Area.Of(10, 63, 0, 10, 63, 0), null, null, new JsonObject { ["plant"] = "wheat" }),
            ("add", "farm", Area.Of(0, 64, 60, 9, 64, 65), null, null, new JsonObject { ["plant"] = "sugar_cane" }),
            ("add", "chop", Area.Of(30, 63, 0, 31, 63, 1), null, null, new JsonObject { ["plant"] = "wheat" }),
            ("add", "farm", Area.Of(40, 63, 0, 41, 63, 1), null, null, new JsonObject { ["plant"] = "cactus", ["sow"] = true }),
            ("add", "fish", Area.Of(19, 63, -1, 24, 63, 4), null, null, null),
            ("add", "fish", Area.Of(50, 63, 50, 52, 63, 52), null, null, null),
            ("add", "mine", Area.Of(0, 50, 30, 5, 60, 35), null, "Pit", null),
            ("add", "mine", Area.Of(0, 50, 40, 0, 60, 45), null, null, null),
            ("add", "mine", Area.Of(10, 50, 40, 14, 60, 45), null, null, new JsonObject { ["mode"] = "ores" }),
            ("add", "mine", Area.Of(20, 50, 40, 24, 60, 45), null, null, new JsonObject { ["mode"] = "gold" }),
            ("add", "stockpile", Area.Of(0, 64, 0, 1, 64, 1), null, null, null),
            ("add", "lake", Area.Of(0, 64, 0, 1, 64, 1), null, null, null),
            ("add", null, Area.Of(0, 64, 0, 1, 64, 1), null, null, null),
            ("add", "home", Area.Of(0, 64, 0, 300, 64, 300), null, null, null),
            ("add", "home", null, null, null, null),
            ("add", "home", Area.Of(7, 64, -3, 8, 66, 3), null, "x".PadRight(41, 'x'), null),
            ("add", "home", Area.Of(7, 64, -3, 8, 66, 3), null, null, new JsonObject { ["into"] = "z1" }),
            ("add", "dump", Area.Of(7, 64, 5, 8, 66, 6), null, null, new JsonObject { ["into"] = "z1" }),
            ("add", null, Area.Of(7, 64, 7, 8, 66, 8), null, null, new JsonObject { ["into"] = "nope" }),
            ("add", null, Area.Of(7, 64, 7, 8, 66, 8), null, null, new JsonObject { ["into"] = 3 }),
            ("rename", null, null, "z1", "  Big house ", null),
            ("rename", null, null, "z1", "", null),
            ("rename", null, null, "nope", "x", null),
            ("update", null, null, "z12", null, new JsonObject { ["mode"] = "ores" }),
            ("update", null, null, "z1", null, new JsonObject { ["mode"] = "ores" }),
            ("update", null, null, "nope", null, new JsonObject { ["mode"] = "ores" }),
            ("update", null, null, "z4", null, new JsonObject { ["harvest"] = false }),
            ("seek_seeds", null, null, "z4", null, null),
            ("irrigation", null, null, "z4", null, null),
            ("bill_add", null, null, "z4", null, new JsonObject { ["plant"] = "wheat", ["target"] = 10 }),
            ("remove", "home", Area.Of(-1, 64, -3, 1, 66, 3), null, null, null),
            ("remove", "any", Area.Of(4, 64, 0, 6, 66, 0), null, null, null),
            ("remove", "lake", Area.Of(4, 64, 0, 6, 66, 0), null, null, null),
            ("remove", null, null, "z3", null, null),
            ("remove", null, null, "nope", null, null),
            ("paint", "home", Area.Of(0, 64, 0, 1, 64, 1), null, null, null),
        ];
        foreach (var (op, kind, area, id, name, grow) in calls)
            s.Rec($"zone {op} {kind} {area?.Json().ToJsonString()} {id} {name} {grow?.ToJsonString()}", s.C.ZoneOp(op, kind, area, id, name, grow));
        s.State("zones");
        s.Run(4, "zones");
        s.State("end", null);
        Golden("ops-zones", s.Sb.ToString());
    }

    // ================================================================ builds, materials, bills

    private static Session Workshop(int pawns)
    {
        var s = Make(pawns);
        s.W.Set(Table.X, Table.Y, Table.Z, 58);
        s.W.Set(Furnace.X, Furnace.Y, Furnace.Z, 61);
        s.Store(Chest, new Stack(17, 0, 6), new Stack(4, 0, 40), new Stack(12, 0, 8), new Stack(263, 0, 3), new Stack(15, 0, 4), new Stack(296, 0, 6));
        s.Store(Chest2, new Stack(5, 0, 20), new Stack(280, 0, 4), new Stack(5, 1, 8));
        return s;
    }

    [Fact]
    public void Builds_and_materials()
    {
        var s = Workshop(3);
        s.Rec("bench table", s.C.WorkbenchOp("designate", Table));
        s.Line("options", s.C.SetOptions(true, null));
        JsonObject It(string kind, JsonNode area, string? mat = null, JsonObject? extra = null)
        {
            var o = new JsonObject { ["kind"] = kind, ["area"] = area };
            if (mat != null) o["material"] = mat;
            foreach (var (k, v) in extra ?? []) o[k] = v?.DeepClone();
            return o;
        }
        var adds = new JsonArray[]
        {
            [It("wall", A(10, 64, 10, 14, 64, 14), "oak planks", new JsonObject { ["height"] = 2, ["roof"] = new JsonObject { ["style"] = "gable" } })],
            [It("door", A(12, 64, 10, 12, 64, 10)), It("window", A(10, 65, 12, 10, 65, 12), "glass")],
            [It("fence", A(20, 64, 0, 24, 64, 0)), It("stairs", A(0, 64, 20, 3, 66, 20), "cobblestone")],
            [It("floor", A(-5, 63, 20, -3, 63, 22), "stone_bricks")],
            [It("block", A(30, 64, 30, 31, 64, 31), "bookshelf")],
            [It("crafting_table", A(-2, 64, 5, -2, 64, 5)), It("furnace", A(-3, 64, 5, -3, 64, 5), null, new JsonObject { ["facing"] = "s" })],
            [It("remove", A(1, 63, 1, 2, 63, 1))],
            [It("block", A(40, 64, 40, 40, 64, 40), null, new JsonObject { ["material"] = A(59, 7) })],
            [It("tower", A(0, 64, 0, 0, 64, 0))],
            [It("wall", A(0, 64, 0, 200, 64, 200), "dirt")],
            [],
        };
        foreach (var items in adds) s.Rec("builds add " + items.ToJsonString(), s.C.BuildsOp("add", items, null));
        s.Rec("builds add null", s.C.BuildsOp("add", null, null));
        s.Rec("builds add str", s.C.BuildsOp("add", new JsonArray("wall"), null));
        s.State("after adds");
        s.Line("architect", s.C.Architect());
        s.Rec("builds pause b2", s.C.BuildsOp("pause", null, ["b2"]));
        s.Rec("builds reorder", s.C.BuildsOp("reorder", null, ["b4", "b1"]));
        s.Rec("builds reorder bad", s.C.BuildsOp("reorder", null, ["zz"]));
        s.Rec("builds remove none", s.C.BuildsOp("remove", null, []));
        s.Rec("builds remove nope", s.C.BuildsOp("remove", null, ["nope"]));
        s.Rec("builds bad op", s.C.BuildsOp("explode", null, null));
        s.State("after ops");
        s.Run(8, "build");
        s.Rec("builds resume b2", s.C.BuildsOp("resume", null, ["b2"]));
        s.Rec("builds remove b5", s.C.BuildsOp("remove", null, ["b5"]));
        s.Run(6, "build2");
        s.State("end", null);
        Golden("ops-builds", s.Sb.ToString());
    }

    [Fact]
    public void Bills()
    {
        var s = Workshop(2);
        s.Rec("bench table", s.C.WorkbenchOp("designate", Table));
        s.Rec("bench nothing", s.C.WorkbenchOp("designate", new P3(9, 64, 9)));
        JsonObject Add(P3 bench, JsonObject bill) => new() { ["bench"] = bench.Json(), ["bill"] = bill };
        foreach (var body in new[]
                 {
                     Add(Table, new JsonObject { ["recipe"] = "craft:stick", ["mode"] = "count", ["target"] = 4 }),
                     Add(Table, new JsonObject { ["recipe"] = "craft:stick", ["mode"] = "until", ["target"] = 10, ["radius"] = 999, ["pawn"] = "any" }),
                     Add(Table, new JsonObject { ["recipe"] = "craft:bread", ["mode"] = "forever", ["min_skill"] = 3, ["store"] = "best" }),
                     Add(Table, new JsonObject { ["recipe"] = "craft:bread", ["store"] = new JsonObject { ["chest"] = Chest.Json() }, ["paused"] = true }),
                     Add(Table, new JsonObject { ["recipe"] = "craft:bread", ["store"] = 3 }),
                     Add(Table, new JsonObject { ["recipe"] = "craft:bread", ["ingredients"] = new JsonObject { ["allow"] = new JsonArray("food") } }),
                     Add(Table, new JsonObject { ["recipe"] = "craft:bread", ["ingredients"] = new JsonObject { ["deny"] = new JsonArray("nonsense") } }),
                     Add(Table, new JsonObject { ["recipe"] = "craft:bread", ["fuel"] = "coal" }),
                     Add(Table, new JsonObject { ["recipe"] = "craft:bread", ["pawn"] = "nobody" }),
                     Add(Table, new JsonObject { ["recipe"] = "craft:bread", ["mode"] = "always" }),
                     Add(Table, new JsonObject { ["recipe"] = "smelt:iron_ore" }),
                     Add(Table, new JsonObject { ["mode"] = "count" }),
                     Add(Furnace, new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 5, ["fuel"] = "planks" }),
                     Add(Furnace, new JsonObject { ["recipe"] = "smelt:sand", ["target"] = 3, ["fuel"] = "lava" }),
                     Add(Furnace, new JsonObject { ["recipe"] = "smelt:sand", ["target"] = 3, ["fuel"] = "inherit" }),
                     new JsonObject { ["bench"] = Table.Json() }, new JsonObject { ["bill"] = new JsonObject() },
                     Add(new P3(9, 64, 9), new JsonObject { ["recipe"] = "craft:stick" }),
                 })
            s.Rec("bills add " + body.ToJsonString(), s.C.BillsOp("add", body));
        foreach (var (op, body) in new (string, JsonObject)[]
                 {
                     ("update", new JsonObject { ["id"] = "bill1", ["target"] = 8, ["pawn"] = s.C.P(1) }),
                     ("update", new JsonObject { ["id"] = "bill1", ["recipe"] = "craft:nothing" }),
                     ("update", new JsonObject { ["id"] = "nope" }),
                     ("pause", new JsonObject { ["id"] = "bill2" }), ("resume", new JsonObject { ["id"] = "bill2" }),
                     ("reorder", new JsonObject { ["bench"] = Table.Json(), ["ids"] = new JsonArray("bill3", "bill1") }),
                     ("reorder", new JsonObject { ["bench"] = Table.Json(), ["ids"] = new JsonArray("nope") }),
                     ("reorder", new JsonObject { ["bench"] = new P3(9, 9, 9).Json(), ["ids"] = new JsonArray("bill1") }),
                     ("release_claim", new JsonObject { ["build"] = "b1", ["item"] = "stick" }),
                     ("enchant_tools", new JsonObject { ["prefer"] = "efficiency" }), ("enchant_tools", new JsonObject()),
                     ("remove", new JsonObject { ["id"] = "bill4" }), ("remove", new JsonObject { ["id"] = "nope" }),
                     ("explode", new JsonObject()),
                 })
            s.Rec($"bills {op} {body.ToJsonString()}", s.C.BillsOp(op, body));
        s.State("after bills");
        s.Run(10, "bills");
        s.State("end", null);
        Golden("ops-bills", s.Sb.ToString());
    }

    // ================================================================ orders and designations

    [Fact]
    public void Orders_and_designations()
    {
        var s = Make(3);
        s.W.Tree(4, 4); s.W.Tree(8, 2, 6); s.W.Tree(-6, 3);
        for (int x = 0; x < 3; x++) s.W.Set(x, 64, -6, 31, 1); // tall grass
        s.W.Set(2, 63, 2, 16); s.W.Set(3, 63, 2, 15);
        s.W.Ents.Add(new StoryEntity(60, "mob", "zombie", 6, 64, 6, Hostile: true));
        s.W.Ents.Add(new StoryEntity(61, "player", "Fox", 3, 64, -2));
        s.W.Ents.Add(new StoryEntity(62, "item", "stick", 1.5, 64, 1.5, ItemId: 280, Count: 2));
        var ps = s.Ps;
        foreach (var (op, kind, area, blocks, filter) in new (string, string?, Area?, IReadOnlyList<P3>?, string?)[]
                 {
                     ("add", "chop", Area.Of(0, 60, -2, 10, 80, 10), null, null),
                     ("add", "cut", Area.Of(0, 64, -6, 3, 64, -6), null, null),
                     ("add", "mine", Area.Of(0, 63, 0, 4, 63, 4), null, "ores"),
                     ("add", "mine", Area.Of(0, 62, 0, 1, 62, 1), null, null),
                     ("add", "mine", Area.Of(0, 62, 0, 1, 62, 1), null, "gems"),
                     ("add", "haul", Area.Of(0, 64, 0, 2, 64, 2), null, null),
                     ("add", "hunt", Area.Of(0, 60, 0, 10, 70, 10), null, null),
                     ("add", "deconstruct", null, [new P3(4, 64, 4)], null),
                     ("add", "forbid", null, [new P3(1, 64, 1)], null),
                     ("add", "allow", null, [new P3(1, 64, 1)], null),
                     ("add", "harvest", Area.Of(0, 60, 0, 10, 70, 10), null, null),
                     ("add", "dance", Area.Of(0, 60, 0, 1, 61, 1), null, null),
                     ("add", "chop", null, null, null),
                     ("add", "chop", Area.Of(-500, 0, -500, 500, 120, 500), null, null),
                     ("remove", null, Area.Of(0, 63, 0, 1, 63, 1), null, null),
                     ("cancel", "chop", null, null, null),
                     ("explode", "chop", Area.Of(0, 63, 0, 1, 63, 1), null, null),
                 })
            s.Rec($"designate {op} {kind} {area?.Json().ToJsonString()} {blocks?.Count} {filter}", s.C.Designate(op, kind, area, blocks, filter));
        s.Rec("designate remove ids", s.C.Designate("remove", null, null, null, null, ["d1", "nope"]));
        foreach (var (who, order, queue) in new (int[], JsonObject, bool)[]
                 {
                     ([0], new JsonObject { ["kind"] = "move", ["target"] = new JsonObject { ["x"] = 10, ["y"] = 64, ["z"] = 10 } }, false),
                     ([0], new JsonObject { ["kind"] = "move", ["target"] = new JsonObject { ["x"] = 11, ["y"] = 64, ["z"] = 10 } }, true),
                     ([1], new JsonObject { ["kind"] = "attack", ["target"] = new JsonObject { ["entity"] = 60 } }, false),
                     ([1], new JsonObject { ["kind"] = "attack", ["target"] = new JsonObject { ["entity"] = 61 } }, false),
                     ([1], new JsonObject { ["kind"] = "attack", ["target"] = new JsonObject { ["player"] = "fox" }, ["confirm"] = true }, true),
                     ([1], new JsonObject { ["kind"] = "attack", ["target"] = new JsonObject { ["player"] = "ghost" } }, false),
                     ([2], new JsonObject { ["kind"] = "chop", ["target"] = new JsonObject { ["x"] = 4, ["y"] = 68, ["z"] = 4 } }, false),
                     ([2], new JsonObject { ["kind"] = "chop", ["target"] = new JsonObject { ["area"] = A(0, 60, 0, 10, 75, 10) } }, true),
                     ([2], new JsonObject { ["kind"] = "mine", ["target"] = new JsonObject { ["area"] = A(0, 62, 0, 3, 63, 3) } }, true),
                     ([2], new JsonObject { ["kind"] = "mine", ["target"] = new JsonObject { ["area"] = A(0, 0, 0, 100, 100, 100) } }, true),
                     ([0, 1], new JsonObject { ["kind"] = "haul", ["target"] = new JsonObject { ["entity"] = 62 } }, true),
                     ([0], new JsonObject { ["kind"] = "haul" }, true),
                     ([0], new JsonObject { ["kind"] = "build" }, true),
                     ([0], new JsonObject { ["kind"] = "build", ["design"] = 4 }, true),
                     ([0], new JsonObject { ["kind"] = "move" }, true),
                     ([0], new JsonObject { ["kind"] = "move", ["target"] = new JsonObject { ["q"] = 1 } }, true),
                     ([0], new JsonObject { ["kind"] = "dance" }, true),
                     ([0], new JsonObject(), true),
                 })
            s.Rec($"order {string.Join(",", who)} {order.ToJsonString()} {queue}", s.C.GiveOrder(who.Select(i => ps[i]).ToList(), order, queue));
        s.State("after orders");
        s.Run(5, "orders");
        s.Rec("order stop", s.C.GiveOrder([ps[0]], new JsonObject { ["kind"] = "stop" }, false));
        s.Run(5, "work");
        s.State("end", null);
        Golden("ops-orders", s.Sb.ToString());
    }
}
