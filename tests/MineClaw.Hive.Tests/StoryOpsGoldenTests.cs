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
            W.Set(chest.X, chest.Y, chest.Z, 54);
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

    /// <summary>The branches <see cref="Orders_and_designations"/> misses: re-marking failed targets, retry, removal by blocks and by
    /// entity, allow by area, every mine filter, the target caps; orders by area, position and player, hold, stop, a full queue.</summary>
    [Fact]
    public void Orders_and_designations_more()
    {
        var s = Make(3);
        var ps = s.Ps;
        s.W.Tree(4, 4); s.W.Tree(8, 2, 6);
        for (int x = 0; x < 3; x++) s.W.Set(x, 64, -6, 31, 1); // tall grass
        s.W.Set(5, 64, -6, 18); // a leaf block
        s.W.Set(0, 63, 3, 60); s.W.Set(1, 63, 3, 60); s.W.Set(0, 64, 3, 59, 7); s.W.Set(1, 64, 3, 59, 3); // ripe and unripe wheat
        s.W.Set(-2, 64, 0, 64); s.W.Set(-2, 65, 0, 64, 8); // a wooden door
        s.W.Set(2, 63, 2, 16); s.W.Set(3, 62, 2, 3); s.W.Set(3, 61, 2, 13); // coal ore, dirt, gravel
        s.W.Set(6, 64, 6, 4); s.W.Set(6, 65, 6, 5); // placed cobblestone and planks
        s.W.Ents.Add(new StoryEntity(70, "mob", "pig", 2, 64, 2));
        s.W.Ents.Add(new StoryEntity(71, "item", "stick", 1.5, 64, 1.5, ItemId: 280, Count: 2));
        s.W.Ents.Add(new StoryEntity(72, "item", "cobblestone", 3.5, 65, 1.5, ItemId: 4, Count: 1));
        s.W.Ents.Add(new StoryEntity(73, "mob", "creeper", 4, 64, 3, Hostile: true));
        s.W.Ents.Add(new StoryEntity(74, "player", "Fox", 3, 64, -2));
        void D(string op, string? kind, Area? area, IReadOnlyList<P3>? blocks, string? filter, IReadOnlyList<string>? ids = null) =>
            s.Rec($"designate {op} {kind} {area?.Json().ToJsonString()} {(blocks == null ? "-" : string.Join(";", blocks))} {filter} {(ids == null ? "-" : string.Join(",", ids))}",
                s.C.Designate(op, kind, area, blocks, filter, ids));
        D("add", "chop", null, [new P3(4, 67, 4), new P3(4, 68, 4), new P3(9, 9, 9)], null);
        D("add", "chop", Area.Of(0, 60, 0, 10, 80, 10), null, null);
        D("add", "cut", Area.Of(0, 64, -6, 5, 64, -6), null, null);
        D("add", "harvest", Area.Of(0, 64, 3, 1, 64, 3), null, null);
        foreach (var f in new[] { "soil", "stone", "ores", "all" }) D("add", "mine", Area.Of(2, 61, 2, 3, 63, 2), null, f);
        D("add", "deconstruct", Area.Of(6, 64, 6, 6, 65, 6), null, null);
        D("add", "haul", null, [new P3(1, 64, 1)], null);
        D("add", "haul", Area.Of(3, 66, 1, 3, 66, 1), null, null);
        D("add", "hunt", null, [new P3(2, 64, 2)], null);
        D("add", "hunt", Area.Of(0, 60, 0, 10, 70, 10), null, null);
        D("add", "forbid", Area.Of(-3, 63, -1, 2, 66, 2), null, null);
        D("add", "allow", Area.Of(-2, 64, 0, -2, 64, 0), null, null);
        D("add", "allow", null, [new P3(1, 64, 1)], null);
        D("add", "mine", Area.Of(0, 0, 0, 100, 30, 100), null, null);
        D("add", "mine", Area.Of(0, 40, 0, 63, 59, 63), null, null);
        D("add", "cut", null, [], null);
        s.State("designated");
        // A failed cut is marked again: tried again now (revived), the others stay as they are.
        foreach (var d in s.C.Designations.Where(d => d.Kind == "cut").Take(2)) { d.Status = "failed"; d.Fails = 3; }
        D("add", "cut", Area.Of(0, 64, -6, 5, 64, -6), null, null);
        foreach (var d in s.C.Designations.Where(d => d.Kind == "harvest")) d.Status = "failed";
        D("retry", null, null, null, null);
        D("retry", null, null, null, null, []);
        D("retry", null, null, null, null, [s.C.Designations.First(d => d.Kind == "harvest").Id!, "nope"]);
        D("remove", "mine", null, [new P3(2, 63, 2)], null);
        D("remove", "hunt", Area.Of(1, 63, 1, 3, 64, 3), null, null);
        D("remove", "haul", Area.Of(1, 63, 1, 3, 65, 3), null, null);
        D("cancel", "chop", null, null, null, [s.C.Designations.First(d => d.Kind == "cut").Id!]);
        D("remove", null, null, null, null);
        s.State("after removals");
        void O(int[] who, JsonObject order, bool queue) =>
            s.Rec($"order {string.Join(",", who)} {order.ToJsonString()} {queue}", s.C.GiveOrder(who.Select(i => ps[i]).ToList(), order, queue));
        O([0], new JsonObject { ["kind"] = " MOVE ", ["target"] = new JsonObject { ["area"] = A(10, 64, 10, 14, 64, 14) } }, false);
        O([0], new JsonObject { ["kind"] = "move", ["target"] = new JsonObject { ["area"] = A(0, 0, 0, 40, 40, 40) } }, true);
        O([0], new JsonObject { ["kind"] = "move", ["target"] = new JsonObject { ["area"] = "x" } }, true);
        O([0], new JsonObject { ["kind"] = "move", ["target"] = new JsonObject { ["player"] = 5 } }, true);
        O([1], new JsonObject { ["kind"] = "attack", ["target"] = new JsonObject { ["x"] = 4, ["y"] = 64, ["z"] = 3 } }, false);
        O([1], new JsonObject { ["kind"] = "attack", ["target"] = new JsonObject { ["player"] = "Fox" } }, true);
        O([1], new JsonObject { ["kind"] = "attack" }, true);
        O([1, 2], new JsonObject { ["kind"] = "haul", ["target"] = new JsonObject { ["area"] = A(0, 63, 0, 4, 66, 4) } }, true);
        O([2], new JsonObject { ["kind"] = "chop", ["target"] = new JsonObject { ["area"] = A(30, 60, 30, 34, 70, 34) } }, false);
        O([2], new JsonObject { ["kind"] = "chop", ["target"] = new JsonObject { ["x"] = 8, ["y"] = 64, ["z"] = 2 } }, true);
        O([2], new JsonObject { ["kind"] = "mine" }, true);
        O([0], new JsonObject { ["kind"] = "hold", ["target"] = new JsonObject { ["x"] = 1, ["y"] = 64, ["z"] = 1 } }, true);
        O([0], new JsonObject { ["kind"] = "hold" }, true);
        O([0], new JsonObject { ["kind"] = "build", ["target"] = new JsonObject { ["x"] = 1, ["y"] = 64, ["z"] = 1 } }, true);
        O([0], new JsonObject { ["kind"] = "build", ["design"] = "7" }, true);
        s.State("after orders");
        s.Run(4, "orders");
        O([0, 1], new JsonObject { ["kind"] = "stop" }, true);
        for (int k = 0; k < 34; k++) O([2], new JsonObject { ["kind"] = "move", ["target"] = new JsonObject { ["x"] = k, ["y"] = 64, ["z"] = 0 } }, true);
        s.Run(4, "work");
        s.State("end", null);
        Golden("ops-orders2", s.Sb.ToString());
    }

    // ================================================================ roles

    [Fact]
    public void Roles()
    {
        var s = Make(3);
        var ps = s.Ps;
        JsonObject Op(string op, JsonNode? role = null, string? id = null)
        {
            var o = new JsonObject { ["op"] = op };
            if (role != null) o["role"] = role;
            if (id != null) o["id"] = id;
            return o;
        }
        JsonObject P(params (string T, JsonNode? V)[] v) => new(v.Select(x => new KeyValuePair<string, JsonNode?>(x.T, x.V)));
        s.Line("set role farmer", s.C.SetRole([ps[0], ps[1]], "farmer"));
        s.Line("set role nope", s.C.SetRole([ps[2]], "nope"));
        foreach (var body in new[]
                 {
                     Op("add", new JsonObject { ["name"] = "Night miner", ["icon"] = "pickaxe-2", ["priorities"] = P(("mine", 1), ("guard", 2)) }),
                     Op("add", new JsonObject { ["name"] = "night MINER" }), Op("add", new JsonObject { ["name"] = "" }),
                     Op("add", new JsonObject { ["name"] = "x".PadRight(41, 'x') }), Op("add", new JsonObject { ["name"] = 5 }),
                     Op("add", new JsonObject { ["name"] = "Icon", ["icon"] = "Big Icon" }), Op("add", new JsonObject { ["name"] = "Icon", ["icon"] = new JsonObject() }),
                     Op("add", new JsonObject { ["name"] = "Icon", ["icon"] = "x".PadRight(25, 'x') }),
                     Op("add", new JsonObject { ["name"] = "No icon", ["icon"] = null }),
                     Op("add", new JsonObject { ["name"] = "Bad", ["priorities"] = P(("dance", 1)) }),
                     Op("add", new JsonObject { ["name"] = "Bad", ["priorities"] = P(("mine", 5)) }),
                     Op("add", new JsonObject { ["name"] = "Bad", ["priorities"] = P(("mine", "x")) }),
                     Op("add", new JsonObject { ["name"] = "Bad", ["priorities"] = new JsonArray(1) }),
                     Op("add", new JsonObject { ["name"] = "!!!" }), Op("add", new JsonObject { ["name"] = "Farmer!" }),
                     Op("add", new JsonObject { ["name"] = "A very long role name that goes on" }),
                     Op("add"), Op("add", "x"), Op("explode"), new JsonObject(),
                     Op("update", new JsonObject { ["name"] = "Z" }), Op("update", new JsonObject { ["id"] = "nope" }),
                     Op("update", new JsonObject { ["id"] = "farmer", ["priorities"] = P(("farm", 2), ("chop", 1)) }),
                     Op("update", new JsonObject { ["id"] = "farmer", ["priorities"] = P(("farm", 2), ("chop", 1)) }),
                     Op("update", new JsonObject { ["name"] = "Deep miner", ["icon"] = "drill" }, id: "night-miner"),
                     Op("update", new JsonObject { ["id"] = "miner", ["name"] = "Night Miner" }),
                     Op("update", new JsonObject { ["id"] = "farmer", ["name"] = "Hauler" }),
                     Op("update", new JsonObject { ["id"] = "night-miner", ["name"] = "Cook" }),
                     Op("remove"), Op("remove", id: "nope"), Op("remove", id: "farmer"), Op("remove", id: "guard"),
                     Op("add", new JsonObject { ["name"] = "Guard / Hunter" }),
                     Op("reset_defaults"),
                     Op("remove", new JsonObject { ["id"] = "night-miner" }),
                 })
        {
            s.Rec("roles " + body.ToJsonString(), s.C.RolesOp(body));
            s.Sb.Append("  pawns ").Append(string.Join(" ", ps.Select(p => (p.Role ?? "-") + ":" + string.Join(",", WorkTypes.All.Select(t => p.Priorities.GetValueOrDefault(t)))))).Append('\n');
        }
        s.Line("set role custom", s.C.SetRole([ps[0]], null));
        s.State("end", null);
        Golden("ops-roles", s.Sb.ToString());
    }

    // ================================================================ growing cells

    /// <summary>Every growing-zone cell class: each plant × soil × what is on it × what is beside it × the cell's place in the zone grid.</summary>
    [Fact]
    public void Grow_cells()
    {
        var (c, _, _) = StoryRig.Make(1, Release1122GameData.Instance);
        var z = new ZoneState { Id = "z1", Kind = "farm", Area = Area.Of(0, 63, 0, 10, 63, 10) };
        var sb = new StringBuilder();
        foreach (var plant in StoryPlants.For(Release1122GameData.Instance))
            foreach (var soil in new[] { 60, 2, 3, 88, 12, 1 })
                foreach (var (above, meta) in new[] { (0, 0), (31, 1), (9, 0), (1, 0), (17, 0), (6, 0), (plant.Block, 0), (plant.Block, plant.Ripe), (plant.Fruit, 0) })
                    for (int env = 0; env < 16; env++)
                        for (int xo = 0; xo < 3; xo++)
                            for (int zo = 0; zo < 2; zo++)
                            {
                                var w = new FakeStoryWorld(Release1122GameData.Instance);
                                var at = new P3(xo, 64, zo);
                                w.Set(at.X, 63, at.Z, soil);
                                w.Set(at.X, 64, at.Z, above, meta);
                                if ((env & 1) != 0) w.Set(at.X, 63, at.Z + 1, 9); // water beside the soil
                                if ((env & 2) != 0) w.Set(at.X + 4, 64, at.Z, 9); // water near, one up
                                if ((env & 4) != 0) w.Set(at.X - 1, 64, at.Z, plant.Fruit != 0 ? plant.Fruit : 1); // its fruit (or a solid block) beside
                                if ((env & 8) != 0) w.Set(at.X, 65, at.Z, plant.Block); // the plant again on top
                                var cell = c.Classify(w, plant, z, new P3(at.X, 63, at.Z));
                                sb.Append(plant.Id).Append(" s").Append(soil).Append(" a").Append(above).Append(':').Append(meta).Append(" e").Append(env)
                                    .Append(" x").Append(xo).Append(" z").Append(zo).Append(" => ").Append(cell.State).Append(' ').Append(cell.At)
                                    .Append(' ').Append(cell.Soil).Append(' ').Append(cell.Clear).Append(' ').Append(cell.Plant).Append('\n');
                            }
        GoldenHash("grow-cells", sb.ToString());
    }

    // ================================================================ the tick and the idle audit

    [Fact]
    public void Tick_and_idle_audit()
    {
        var s = Make(5);
        var ps = s.Ps;
        s.W.Tree(6, 6); s.W.Tree(-6, 6); s.W.Tree(6, -6);
        foreach (var p in ps.Take(3)) foreach (var t in WorkTypes.All) p.Priorities[t] = 0;
        ps[0].Priorities[WorkTypes.Chop] = 1;
        ps[1].Priorities[WorkTypes.Mine] = 2; // no mine work: "priority 0"
        ps[2].Priorities[WorkTypes.Build] = 1; // a build short of gold: "waiting: …"
        ps[4].Schedule = Enumerable.Repeat(ScheduleBlocks.Sleep, 24).ToArray();
        for (int i = 0; i < ps.Count; i++) s.H.Lives[ps[i].Name] = FakeStoryHost.Live(i * 2, 64, 0);
        s.Rec("designate chop", s.C.Designate("add", "chop", Area.Of(-8, 60, -8, 8, 80, 8), null, null));
        s.Rec("build gold", s.C.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "block", ["area"] = A(20, 64, 20, 21, 64, 20), ["material"] = "gold_block" }), null));
        var held = new Dictionary<string, string>();
        void Audit(string tag)
        {
            s.Sb.Append(tag).Append(" idle ").Append(string.Join(" ", ps.Select(p => p.Name + "=" + (s.C.IdleReasonOf(p.Name) ?? "-")))).Append('\n');
            s.Sb.Append("  current ").Append(string.Join(" ", ps.Select(p => s.C.CurrentOf(p.Name) is { } j ? $"{j.Source}/{j.Kind}/{j.Label}" : "-"))).Append('\n');
            s.Sb.Append("  leases ").Append(string.Join(" ", s.C.Leases().OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value))).Append('\n');
            s.Sb.Append("  started ").Append(string.Join(",", s.H.Started)).Append(" interrupted ").Append(string.Join(",", s.H.Interrupted))
                .Append(" teleported ").Append(string.Join(",", s.H.Teleported.Select(t => t.Account + ">" + t.To))).Append('\n');
            s.H.Started.Clear(); s.H.Interrupted.Clear(); s.H.Teleported.Clear();
        }
        for (int r = 0; r < 14; r++)
        {
            for (int i = 0; i < ps.Count; i++)
            {
                var name = ps[i].Name;
                if (!s.H.Live(name).Online) continue;
                if (held.TryGetValue(name, out var keep) && s.C.CurrentOf(name)?.Id == keep) continue; // still at it: no report
                var job = s.C.Next(name);
                s.Line($"r{r} {name} job", job);
                if (job?["id"]?.GetValue<string>() is not { } id) continue;
                if (i == 3 && r is 1 or 2 || i == 0 && r is 10 or 11 or 12) { held[name] = id; continue; } // this one keeps its lease (no report)
                if (job["type"]?.GetValue<string>() is "idle" or "schedule") continue; // it stays on its idle leg: the audit looks at it
                bool fail = (r + i) % 4 == 3;
                s.Sb.Append("  report ").Append(s.C.Report(name, id, fail ? "failed" : "done", fail ? "can't reach the log" : null, null)).Append('\n');
            }
            if (r == 3) s.H.Lives[ps[3].Name] = PawnLive.Offline;
            if (r == 6) s.H.Lives[ps[3].Name] = FakeStoryHost.Live(40, 64, 40);
            if (r == 5) s.H.Lives[ps[0].Name] = s.H.Lives[ps[0].Name] with { ScriptRunning = false };
            if (r == 7) s.H.Lives[ps[0].Name] = s.H.Lives[ps[0].Name] with { ScriptRunning = true };
            if (r == 8) s.C.Draft([ps[1]], true);
            if (r == 9) { s.C.Draft([ps[1]], false); s.T.Now += TimeSpan.FromSeconds(100); } // leases run out
            if (r == 10) s.T.Now += TimeSpan.FromSeconds(50); // a held lease is renewed
            if (r == 11) s.C.Designate("add", "chop", Area.Of(-8, 60, -8, 8, 80, 8), null, null);
            if (r == 12) { s.H.Lives[ps[0].Name] = s.H.Lives[ps[0].Name] with { ScriptRunning = false }; s.T.Now += TimeSpan.FromSeconds(100); } // its lease runs out
            if (r == 13) s.H.Lives[ps[0].Name] = s.H.Lives[ps[0].Name] with { ScriptRunning = true };
            s.C.Tick();
            Audit($"r{r}");
            s.T.Now += TimeSpan.FromSeconds(r == 4 ? 12 : 1);
        }
        s.State("end", null);
        Golden("tick-audit", s.Sb.ToString());
    }

    // ================================================================ growing and fishing zone bills, storage ops

    [Fact]
    public void Grow_bills_and_storage()
    {
        var s = Make(2, Release1122GameData.Instance);
        for (int x = -10; x < -4; x++) for (int z = -10; z < -4; z++) s.W.Set(x, 63, z, 2); // grass to farm
        for (int x = 20; x < 24; x++) for (int z = 0; z < 4; z++) s.W.Set(x, 63, z, 9); // a pond
        s.W.Set(0, 64, 9, 54); s.W.Set(1, 64, 9, 54); s.W.Set(4, 64, 9, 54); s.W.Set(6, 64, 12, 146); // chests
        s.Rec("farm", s.C.ZoneOp("add", "farm", Area.Of(-10, 63, -10, -5, 64, -5), null, "Wheat", new JsonObject { ["plant"] = "wheat" }));
        s.Rec("fish", s.C.ZoneOp("add", "fish", Area.Of(19, 63, -1, 24, 63, 4), null, null, null));
        s.Rec("home", s.C.ZoneOp("add", "home", Area.Of(-3, 64, -3, 3, 66, 3), null, null, null));
        var zs = s.C.Zones.Select(z => z.Id).ToList();
        string farm = zs[0], fish = zs[1], home = zs[2];
        JsonObject B(params (string K, JsonNode? V)[] kv) => new(kv.Select(x => new KeyValuePair<string, JsonNode?>(x.K, x.V)));
        foreach (var (op, id, body) in new (string, string?, JsonObject?)[]
                 {
                     ("bill_add", farm, B(("plant", "carrots"), ("mode", "until"), ("target", 20))),
                     ("bill_add", farm, B(("plant", "pumpkin"), ("mode", "count"), ("target", 5), ("paused", true))),
                     ("bill_add", farm, B(("plant", "oak"))),
                     ("bill_add", farm, B(("plant", "cactus"))),
                     ("bill_add", farm, B(("plant", "kelp"))),
                     ("bill_add", farm, B(("plant", "wheat"), ("mode", "sometimes"))),
                     ("bill_add", farm, B(("plant", "wheat"), ("target", 0))),
                     ("bill_add", farm, B(("plant", "wheat"), ("target", "x"))),
                     ("bill_add", farm, B(("plant", 5))),
                     ("bill_add", farm, null),
                     ("bill_add", home, B(("plant", "wheat"))),
                     ("bill_add", "nope", B(("plant", "wheat"))),
                     ("bill_add", fish, B(("plant", "wheat"))),
                     ("bill_add", fish, B(("mode", "count"), ("target", 7))),
                     ("bill_add", fish, B(("plant", "fish"), ("mode", "until"), ("target", 12))),
                     ("bill_update", farm, B(("bill", "gb5"), ("target", 40))),
                     ("bill_update", farm, B(("bill", "gb5"), ("mode", "forever"))),
                     ("bill_update", farm, B(("bill", "gb2"), ("plant", "melon"), ("paused", false))),
                     ("bill_update", farm, B(("bill", "gb2"), ("plant", "kelp"))),
                     ("bill_update", farm, B(("bill", "nope"), ("target", 3))),
                     ("bill_update", farm, B(("target", 3))),
                     ("bill_pause", farm, B(("bill", "gb5"))),
                     ("bill_resume", farm, B(("bill", "gb5"))), ("bill_pause", fish, B(("bill", "gb16"))),
                     ("bill_pause", farm, B(("bill", 99))),
                     ("bill_reorder", farm, B(("ids", new JsonArray("gb7", "gb5")))),
                     ("bill_reorder", farm, B(("ids", new JsonArray("gb3", "nope")))),
                     ("bill_reorder", farm, B(("ids", "gb3"))),
                     ("bill_reorder", farm, B(("ids", new JsonArray("gb2", "gb2")))),
                     ("bill_remove", farm, B(("bill", "gb6"))),
                     ("bill_remove", fish, B(("bill", "nope"))),
                     ("bill_explode", farm, B()),
                 })
        {
            s.Rec($"zone {op} {id} {body?.ToJsonString()}", s.C.ZoneOp(op, null, null, id, null, body));
            s.Sb.Append("  bills ").Append(string.Join(" | ", s.C.Zones.Select(z => z.Id + ":" + z.Plant + ":"
                + string.Join(",", (z.Bills ?? []).Select(b => $"{b.Id}/{b.Plant}/{b.Mode}/{b.Target}/{b.Done}/{b.Paused}"))))).Append('\n');
        }
        for (int i = 0; i < 3; i++) s.Rec("farm bill_add more", s.C.ZoneOp("bill_add", null, null, farm, null, B(("plant", "potatoes"))));
        for (int i = 0; i < 14; i++) s.C.ZoneOp("bill_add", null, null, fish, null, B(("plant", "fish")));
        s.Rec("fish bill_add full", s.C.ZoneOp("bill_add", null, null, fish, null, B(("plant", "fish"))));
        var f1 = new StorageFilter { Allow = ["food"], Deny = ["297"] };
        foreach (var (op, chests, filter, prio, label, from, area) in new (string, IReadOnlyList<P3>?, StorageFilter?, string?, string?, P3?, Area?)[]
                 {
                     ("set", [new P3(0, 64, 9)], f1, "preferred", "Pantry", null, null),
                     ("set", [new P3(4, 64, 9)], null, null, null, null, null),
                     ("set", [], null, null, null, null, null),
                     ("set", null, null, "urgent", null, null, null),
                     ("set", [new P3(4, 64, 9)], new StorageFilter { Allow = ["nonsense:thing:x"] }, null, null, null, null),
                     ("copy", [new P3(4, 64, 9)], null, null, null, new P3(0, 64, 9), null),
                     ("copy", [new P3(4, 64, 9)], null, null, null, new P3(9, 9, 9), null),
                     ("copy", [], null, null, null, new P3(0, 64, 9), null),
                     ("copy", [new P3(4, 64, 9)], null, null, null, null, null),
                     ("assign_area", null, null, "low", "Area", null, Area.Of(0, 60, 0, 10, 70, 20)),
                     ("assign_area", null, null, null, null, null, Area.Of(0, 0, 0, 100, 100, 100)),
                     ("assign_area", null, null, null, null, null, null),
                     ("remove", [new P3(4, 64, 9), new P3(9, 9, 9)], null, null, null, null, null),
                     ("remove", null, null, null, null, null, null),
                     ("explode", [new P3(4, 64, 9)], null, null, null, null, null),
                 })
            s.Rec($"storage {op} {(chests == null ? "-" : string.Join(";", chests))} {prio} {label} {from} {area?.Json().ToJsonString()}",
                s.C.StorageOp(op, chests, filter, prio, label, from, area));
        s.State("after ops");
        s.Run(4, "farm");
        s.State("end", null);
        Golden("ops-growbills", s.Sb.ToString());
    }

    // ================================================================ small lookups: every input

    [Fact]
    public void Tool_lookups()
    {
        var sb = new StringBuilder();
        for (int id = -1; id <= 4096; id++)
            if (StoryRecipeFamilies.Source(id) is { } src) sb.Append("source ").Append(id).Append(' ').Append(src).Append('\n');
        foreach (var d in new IGameData[] { Beta181GameData.Instance, Release1122GameData.Instance })
            foreach (var pref in new[] { "best", "axe", "sword", "bow", "none", "x" })
                for (int id = -1; id <= 2300; id++)
                    if (StoryTools.WeaponScore(d, id, pref) is var w and not 0) sb.Append(d.VersionId).Append(" weapon ").Append(pref).Append(' ').Append(id).Append(' ').Append(w).Append('\n');
        GoldenHash("tool-lookups", sb.ToString());
    }
}
