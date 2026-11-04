using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Beliefs;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins model-facing text and parsing (WP06 refactor): <see cref="WorldTools.WhereIs"/> over items, kinds, the area listing, both
/// dimensions, portals, landmarks, contents ages and claims; <see cref="ClaimParser"/> over a claim corpus;
/// <see cref="SafetyWarnings.Threat"/> and <see cref="WorldDim.TryParse"/> over their words.
/// </summary>
public class WorldToolsGoldenTests
{
    private const string ExpectedWhereIs = "3277DF925639C4D1180462710D0E9FEDDC0F1B78D8E93D7C1A82827A1170DBAD";
    private const string ExpectedParsers = "D907CE7E524054C232BFA38EE21B0C85F4E8D3A67D5938001047CD9DFAD19834";

    private static string Hash(string dump, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), dump);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
    }

    [Fact]
    public void Where_is_answers_the_same()
    {
        var time = new ManualTime();
        using var db = AgentDb.Open(null);
        var idx = new RagIndex(db, new RagOptions(), new FakeEmbedder(), time: time, backgroundEmbedding: false);
        var world = new ObservationStore(idx, time);
        var mem = new MemoryStore(db, idx, time);
        mem.SetWaypoint("cobble_gen", 12, 64, 2, null, 0);
        mem.SetWaypoint("nether_hub", 3, 70, 1, null, -1);
        world.Upsert(new Observation("chest", "10,64,-3", "chest") { Bounds = Box.Point(10, 64, -3), Contents = [new ItemCount(265, "iron_ingot", 12), new ItemCount(4, "cobblestone", 64)], ContentsBy = "maid_chan" });
        world.Upsert(new Observation("chest", "14,64,-5", "double chest") { Bounds = new Box(14, 64, -5, 15, 64, -5), Contents = [new ItemCount(265, "iron_ingot", 3)] });
        world.Upsert(new Observation("chest", "2,70,2", "nether chest") { Bounds = Box.Point(2, 70, 2), Contents = [new ItemCount(297, "bread", 5), new ItemCount(265, "iron_ingot", 1)], Dimension = -1 });
        world.Upsert(new Observation("furnace", "11,64,0", "furnace") { Bounds = Box.Point(11, 64, 0) });
        world.Upsert(new Observation("furnace", "1,70,1", "nether furnace") { Bounds = Box.Point(1, 70, 1), Dimension = -1 });
        world.Upsert(new Observation("tree", "40,64,40", "oak") { Bounds = Box.Point(40, 64, 40), Source = ObservationSource.Taught });
        world.Upsert(new Observation("portal", "5,64,5", "portal") { Bounds = Box.Point(5, 64, 5) });
        world.Upsert(new Observation("portal", "1,65,1", "portal") { Bounds = Box.Point(1, 65, 1), Dimension = -1 });
        world.Portals.Link(new PortalEnd(0, "5,64,5", 5, 64, 5), new PortalEnd(-1, "1,65,1", 1, 65, 1));
        var beliefs = BeliefStore.For(mem);
        beliefs.Ingest("PoofImaFox", "there are diamonds in the chest at 3,64,7");
        time.Advance(TimeSpan.FromSeconds(20));

        var sb = new StringBuilder();
        var heres = new BlockPos?[] { new(0, 64, 0), null };
        string?[] items = ["iron_ingot", "iron ingots", " bread ", "diamond", "melon", null];
        string?[] kinds = ["furnace", "furnaces", "tree", "portal", "lava", null];
        string?[] dims = [null, "nether", "any", "bogus"];
        foreach (var cur in new[] { 0, -1 })
        {
            idx.CurrentDimension = () => cur;
            foreach (var here in heres)
                foreach (var dim in dims)
                    foreach (var m in new MemoryStore?[] { mem, null })
                    {
                        foreach (var item in items)
                            foreach (var kind in kinds)
                            {
                                if (item == null && kind != null && kind != "furnace" && here == null) continue;
                                var r = WorldTools.WhereIs(world, item, kind, here, time.GetUtcNow().AddSeconds(item == "iron_ingot" ? 50 : 0), m, dim);
                                sb.Append(cur).Append(' ').Append(here?.ToString() ?? "-").Append(' ').Append(dim ?? "-").Append(' ').Append(m != null).Append(' ')
                                  .Append(item ?? "-").Append(' ').Append(kind ?? "-").Append(" => ").Append(r.Ok).Append(' ').Append(r.Text).Append('\n');
                            }
                    }
        }
        Assert.Equal(ExpectedWhereIs, Hash(sb.ToString(), "where-is.txt"));
    }

    [Fact]
    public void Claims_threats_and_dims_parse_the_same()
    {
        var sb = new StringBuilder();
        string[] claims =
        [
            "there are diamonds in the chest at 3,64,7", "the chest at 1,64,1 has 12 iron ingots", "storage at 5 64 5 holds bread",
            "you can make a furnace from 8 cobblestone", "a crafting table is made from 4 planks", "smelt iron ore to get iron ingots",
            "you make paper with 3 sugar cane", "saplings take a few minutes to grow", "cobblestone regenerates after a second at 4,64,4",
            "the lever at 2,64,9 opens the iron door", "pressing the button at 1,65,1 drains the lava", "the button lights the lamps",
            "the iron ore is under the island", "iron is at 10,40,10", "diamonds are at -5,12,30", "Fox is nice", "kiwi likes wool",
            "never go near the void edge", "always say hi to Robin", "don't fall off the edge", "lava at 3,60,3 is dangerous",
            "you need 9 gold ingots for a gold block", "two sticks and a coal make torches", "", "    ", "hello there",
            "the pressure plate at 0,64,0 opens the trapdoor", "the lever floods the farm", "the tnt at 9,9,9 explodes when you push the button",
            "you can smelt sand into glass in a furnace", "cooked fish come from raw fish in the furnace", "at 1,2,3 and 4,5,6 there is a bridge",
        ];
        foreach (var c in claims)
            sb.Append("claim ").Append(c).Append(" => ").Append(JsonSerializer.Serialize(ClaimParser.Parse(c))).Append('\n');
        foreach (var p in new[] { "8 gold blocks", "an iron ingot", "three sticks", "some cobble", "2x torch", "sugar cane", "a stack of wool", "nonsense item", "the wooden planks items", "1 2 3 4" })
            sb.Append("item ").Append(p).Append(" => ").Append(JsonSerializer.Serialize(ClaimParser.ResolveItem(p))).Append('\n');
        foreach (var (verb, noun, raw) in new (string, string?, string)[] { ("open", "door", "the door"), ("drain", null, "x"), ("push", "piston", "pistons"), ("open", "tnt", "tnt"), ("fire", "light", "lamp"), ("open", null, "the mobs") })
            sb.Append("risk ").Append(verb).Append(' ').Append(noun ?? "null").Append(' ').Append(raw).Append(" => ").Append(ClaimParser.RiskOf(verb, noun, raw)).Append('\n');

        string?[] lines =
        [
            null, "", "zombie", "zombie!", "a zombie is attacking you", "watch out, skeleton", "arrows!", "creeper behind you", "hiss!",
            "it exploded!", "spider!", "lava!", "fire! run", "you're burning!", "void!", "don't fall!", "the edge!", "you'll die!", "dying!",
            "he's dead!", "she died!", "zombies!!", "careful, wolves!", "run", "Robin, a zombie is attacking you", "slimes!", "ghast!",
        ];
        foreach (var l in lines) sb.Append("threat ").Append(l ?? "null").Append(" => ").Append(SafetyWarnings.Threat(l) ?? "null").Append('\n');

        string?[] dims = [null, "", " here ", "current", "this", "overworld", "OW", "world", "surface", "0", "nether", "Hell", "-1", "any", "all", "both", "*", "end", "1"];
        foreach (var cur in new[] { 0, -1 })
            foreach (var d in dims)
                sb.Append("dim ").Append(cur).Append(' ').Append(d ?? "null").Append(" => ").Append(WorldDim.TryParse(d, cur, out var v)).Append(' ').Append(v).Append('\n');
        Assert.Equal(ExpectedParsers, Hash(sb.ToString(), "parsers.txt"));
    }
}
