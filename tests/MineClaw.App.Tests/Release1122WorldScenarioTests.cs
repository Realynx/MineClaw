using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.App.Hosting;
using MineClaw.Bot;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol.Packets;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.App.Tests;

/// <summary>
/// Multi-version P6, end to end: a fresh 1.12.2 memory (<c>--reset-memory</c> with the 1.12.2 pack, no skyblock mode) in a
/// NORMAL world on the fake 1.12.2 server (grass and dirt over stone, a stone outcrop, oak and acacia trees, a cave with lava
/// beside water, a village-ish house), driven by scripted brain and worker decisions. The bot greets, chops wood, crafts a
/// table and a wooden pickaxe, mines natural stone, crafts and places a furnace, smelts its porkchops, builds a small hut
/// before dusk, fights a zombie with the 1.12.2 attack cooldown, and eats. Every step runs the real tools, skills, client and
/// 1.12.2 wire; the LLM is the only fake.
/// </summary>
[Collection("Release1122World")]
public sealed class Release1122WorldScenarioTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mineclaw-p6-" + Guid.NewGuid().ToString("N")[..8]);
    private string Data => Path.Combine(_root, "mineclaw");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    // ------------------------------------------------------------------ the scripted model

    /// <summary>Scripted Opus (brain) and Sonnet (worker) rounds; quick/memory calls answer empty at once.</summary>
    private sealed class ScriptedLlm : ILlmClient
    {
        public readonly ConcurrentQueue<Func<LlmRequest, LlmResponse>> Brain = new(), Worker = new();
        public readonly ConcurrentQueue<string> Log = new();
        private int _n;

        public ScriptedLlm B(string tool, object input) { Brain.Enqueue(r => Use(r, tool, input)); return this; }
        public ScriptedLlm B(string text) { Brain.Enqueue(r => Say(r, text)); return this; }
        public ScriptedLlm W(string tool, Func<object> input) { Worker.Enqueue(r => Use(r, tool, input())); return this; }
        public ScriptedLlm W(string text) { Worker.Enqueue(r => Say(r, text)); return this; }

        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            var q = r.Tier == ModelTier.Main && !r.Purpose.StartsWith("quick_", StringComparison.Ordinal) ? Brain
                : r.Tier == ModelTier.Worker ? Worker : null;
            if (q == null) return Task.FromResult(Say(r, ""));
            var last = r.Messages.Count > 0 ? string.Join(" ", r.Messages[^1].Content.Select(b => b switch
            {
                LlmText t => t.Text,
                LlmToolResult tr => "[result] " + tr.Content,
                _ => "",
            })) : "";
            Log.Enqueue($"{r.Tier}/{r.Purpose} <- {Trim(last)}");
            var resp = q.TryDequeue(out var h) ? h(r) : Say(r, r.Tier == ModelTier.Worker ? "failed: script ran out" : "");
            Log.Enqueue($"{r.Tier} -> {Trim(string.Join(" ", resp.Content.Select(b => b is LlmToolUse u ? $"{u.Name} {u.Input}" : b is LlmText t ? t.Text : "")))}");
            return Task.FromResult(resp);
        }

        private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;

        private LlmResponse Use(LlmRequest r, string tool, object input) =>
            new([new LlmToolUse("tu_" + Interlocked.Increment(ref _n), tool, JsonSerializer.SerializeToElement(input))], StopReasons.ToolUse,
                new LlmUsage(100, 20, 1000, 0), r.Model ?? "claude-opus-5-5");

        private static LlmResponse Say(LlmRequest r, string text) =>
            new([new LlmText(text)], StopReasons.EndTurn, new LlmUsage(100, 20, 1000, 0), r.Model ?? "claude-opus-5-5");
    }

    // ------------------------------------------------------------------ the normal-world fixture

    private const int Ground = 63; // grass top; the bot walks at y 64

    /// <summary>A small normal world in chunks −1..1 (x, z −16..31): bedrock 50, stone 51..60, dirt 61..62, grass 63; a stone
    /// outcrop; an oak, a birch and an acacia tree; a cave under the outcrop with lava beside water (no generator!); a house.</summary>
    private static void NormalWorld(FakeServer s)
    {
        s.Blocks.Clear();
        for (int x = -16; x < 32; x++)
        for (int z = -16; z < 32; z++)
        {
            s.Blocks[(x, 50, z)] = Blocks.Bedrock;
            for (int y = 51; y <= 60; y++) s.Blocks[(x, y, z)] = Blocks.Stone;
            s.Blocks[(x, 61, z)] = Blocks.Dirt;
            s.Blocks[(x, 62, z)] = Blocks.Dirt;
            s.Blocks[(x, Ground, z)] = Blocks.Grass;
        }
        // A stone outcrop (natural stone at the surface, two layers) west of spawn.
        for (int x = -6; x <= 1; x++)
        for (int z = -4; z <= 3; z++)
        {
            s.Blocks[(x, 64, z)] = Blocks.Stone;
            if (x <= -2) s.Blocks[(x, 65, z)] = Blocks.Stone;
        }
        // A cave pocket in the stone with a lava pool touching water: natural, never a cobble generator.
        for (int x = 20; x <= 26; x++)
        for (int y = 54; y <= 57; y++)
        for (int z = 2; z <= 8; z++)
            s.Blocks.Remove((x, y, z));
        s.Blocks[(22, 54, 5)] = Blocks.Lava;
        s.Blocks[(23, 54, 5)] = Blocks.Stone;
        s.Blocks[(24, 54, 5)] = Blocks.Water;
        // Trees: oak at (4, 12), birch at (6, 16), acacia (log2 162) at (12, 2).
        Tree(s, 4, 12, Blocks.Log, 0, Blocks.Leaves, 0, 4);
        Tree(s, 6, 16, Blocks.Log, 2, Blocks.Leaves, 2, 4);
        Tree(s, 12, 2, 162, 0, 161, 0, 4);
        // A village-ish house: cobblestone floor, planks walls, an oak door, glass panes, a chest with bread.
        for (int x = 20; x <= 24; x++)
        for (int z = 18; z <= 22; z++)
        {
            s.Blocks[(x, Ground, z)] = Blocks.Cobblestone;
            bool wall = x is 20 or 24 || z is 18 or 22;
            if (!wall) continue;
            for (int y = 64; y <= 66; y++) s.Blocks[(x, y, z)] = Blocks.Planks;
        }
        s.Blocks[(22, 64, 18)] = Blocks.WoodenDoor; s.Metas[(22, 64, 18)] = 1;
        s.Blocks[(22, 65, 18)] = Blocks.WoodenDoor; s.Metas[(22, 65, 18)] = 8;
        s.Blocks[(20, 65, 20)] = Blocks.GlassPane;
        s.Blocks[(24, 65, 20)] = Blocks.GlassPane;
        for (int x = 20; x <= 24; x++) for (int z = 18; z <= 22; z++) s.Blocks[(x, 67, z)] = Blocks.Planks;
        s.Blocks[(23, 64, 21)] = Blocks.Chest;
        s.Chests[(23, 64, 21)] = Enumerable.Repeat(ItemStack.Empty, 27).Select((e, i) => i == 0 ? new ItemStack((short)Items.Bread, 3, 0) : e).ToArray();

        s.SpawnAt = (8.5, 64, 8.5);
        s.Inventory[9] = new ItemStack((short)Items.Porkchop, 2, 0); // a friend's gift: raw porkchops to cook
        s.DropOnDig = true;
        s.ConsumeOnPlace = true;
        s.DigDrop = (pos, id, meta) => Release1122GameData.Instance.DropsFor(id, meta, s.Held.IsEmpty ? -1 : s.Held.Id).Where(x => x.Chance >= 1f).ToList() is { Count: > 0 } d
            ? new ItemStack((short)d[0].ItemId, (byte)Math.Max(1, d[0].Min), (short)d[0].Damage) : ItemStack.Empty;
    }

    private static void Tree(FakeServer s, int x, int z, int log, int logMeta, int leaves, int leafMeta, int height)
    {
        for (int y = 64; y < 64 + height; y++) { s.Blocks[(x, y, z)] = log; if (logMeta != 0) s.Metas[(x, y, z)] = logMeta; }
        for (int y = 64 + height - 2; y <= 64 + height; y++)
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        {
            if (dx == 0 && dz == 0 && y < 64 + height) continue;
            if (Math.Abs(dx) == 2 && Math.Abs(dz) == 2) continue;
            if (y == 64 + height && (Math.Abs(dx) > 1 || Math.Abs(dz) > 1)) continue;
            s.Blocks[(x + dx, y, z + dz)] = leaves;
            if (leafMeta != 0) s.Metas[(x + dx, y, z + dz)] = leafMeta;
        }
    }

    private static async Task<bool> Until(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(10); }
        return cond();
    }

    private static int Count(FakeServer s, int id) => s.Inventory.Where(st => !st.IsEmpty && st.Id == id).Sum(st => st.Count);

    // ------------------------------------------------------------------ the scenario

    [Fact]
    public async Task A_fresh_1122_bot_in_a_normal_world_greets_gathers_crafts_mines_smelts_builds_fights_and_eats()
    {
        // 1. --reset-memory: a fresh db with only the 1.12.2 pack + sdk-js (no skyblock mode).
        Directory.CreateDirectory(Data);
        var reset = await MemoryReset.RunAsync(new MemoryReset.Request
        {
            DataDirectory = Data, PackPath = Path.Combine(Root(), "shards", "defaults", "1.12.2-gamedata.mcshard"), Yes = true,
            Options = new AgentOptions { DataDirectory = Data, KnowledgeDirectory = Path.Combine(Root(), "docs", "knowledge") },
        }, new StringWriter(), DateTimeOffset.UtcNow);
        Assert.Equal(ExitCodes.Ok, reset.Exit);

        // The process facts this run plays under (the App sets them from the version and the db's packs).
        using var data = GameData.Override(Release1122GameData.Instance);
        using var world = WorldMode.Override(WorldMode.Normal); // before the client: its tick thread inherits both scopes
        var server = new FakeServer(release1122: true);
        NormalWorld(server);
        var clientOptions = new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateConnection, AutoReconnect = false,
            Profile = MineClaw.Client.Versioning.GameVersionProfiles.Java1122,
        };
        await using var client = new MinecraftClient(clientOptions);
        using var _s = server;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await client.ConnectAsync(cts.Token);
        Assert.True(await Until(() => client.World.Player.OnGround, 5000), "on the ground");
        Assert.Equal(256, client.World.WorldHeight);
        Assert.Equal(1, client.World.GetBiome(8, 8)); // the Chunk Data biome array (the fake server's plains) reached the world

        var llm = new ScriptedLlm();
        var o = MineClaw.Bot.Tests.TestBot.Options();
        o.DataDirectory = Data;
        o.KnowledgeDirectory = Path.Combine(Root(), "docs", "knowledge");
        o.GameVersion = "1.12.2";
        o.Rag.SeedFile = "";
        o.Self.TrustedPlayers = ["PoofimaFox"];
        using var host = BotHost.Create(new MinecraftGameClient(client), o, new BotOptions { GeneratorQueueSeconds = 0 }, llm, persist: true);
        var rt = host.Runtime;
        // The db's packs say: a normal world (no skyblock mode), the mode the App would make the process default.
        Assert.Equal(WorldMode.Normal, rt.World);
        var system = string.Join("\n", rt.Agent.SystemPrompt.Select(b => b.Text));
        Assert.Contains("Core rules (1.12.2)", system);
        foreach (var skyblockism in new[] { "skyblock", "beta 1.8.1", "the void is below", "island", "no bridges" })
            Assert.DoesNotContain(skyblockism, system, StringComparison.OrdinalIgnoreCase);

        async Task Turn(string player, string text)
        {
            output.WriteLine($"<{player}> {text}");
            await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat(player, text)]), CancellationToken.None);
        }
        async Task Idle(string what, int ms)
        {
            Assert.True(await Until(() => !rt.Activities.IsBusy, ms), $"{what}: still busy ({rt.Activities.Describe()})");
        }
        void Dump()
        {
            foreach (var l in llm.Log) output.WriteLine(l);
            llm.Log.Clear();
        }

        try
        {
            // 2. Greets.
            llm.B("hey fox! nice day out here");
            await Turn("PoofimaFox", "hi maid_chan!");
            Assert.True(server.WaitFor(p => p is ChatPacket c && c.Message.Contains("hey fox", StringComparison.OrdinalIgnoreCase), 5000), "greeted");
            Dump();

            // 3. Gathers wood: two trees (oak and the acacia, log2).
            llm.B("do", new { action = "chop_tree", then = new object[] { new { action = "chop_tree" } } }).B("on it");
            await Turn("PoofimaFox", "can you get us some wood?");
            await Until(() => Count(server, Blocks.Log) + Count(server, 162) >= 8, 90_000);
            await Idle("chopping", 60_000);
            Dump();
            int logs = Count(server, Blocks.Log) + Count(server, 162);
            output.WriteLine($"logs {logs}: {string.Join(", ", server.Inventory.Where(i => !i.IsEmpty).Select(i => $"{i.Id}:{i.Damage}x{i.Count}"))}");
            Assert.True(logs >= 4, $"logs {logs}");

            // 4. Crafts a table (placed) and a wooden pickaxe.
            llm.B("do", new { action = "craft", args = new { item = "wooden_pickaxe" } }).B("making a pickaxe");
            await Turn("PoofimaFox", "make yourself a wooden pickaxe");
            await Idle("crafting", 60_000);
            Dump();
            Assert.True(Count(server, Items.WoodenPickaxe) == 1, "a wooden pickaxe");
            Assert.NotEmpty(server.Workbenches); // the crafting table it placed

            // 5. Mines natural stone (no generator in this world).
            llm.B("do", new { action = "mine", args = new { block = "stone", count = 34 } }).B("off to the rocks");
            await Turn("PoofimaFox", "mine some stone");
            Assert.True(await Until(() => Count(server, Blocks.Cobblestone) >= 30, 120_000), $"cobblestone {Count(server, Blocks.Cobblestone)}");
            await Idle("mining", 60_000);
            Dump();
            int cobble = Count(server, Blocks.Cobblestone);
            Assert.True(cobble >= 30, $"cobblestone {cobble}");
            Assert.DoesNotContain(server.Dug, d => d.X is >= 20 and <= 24 && d.Z is >= 18 and <= 22); // the house stays whole
            Assert.DoesNotContain(server.Dug, d => d.Y < 60);                                       // no cave/gen detour

            // 6. Furnace: crafted, placed, porkchops smelted (worker, scripted).
            server.StartSmelting();
            var furnaceAt = $"10,{Ground + 1},6"; // open grass near spawn
            llm.B("delegate", new { task = "craft a furnace, place it next to you and cook the 2 porkchops", done_when = "2 cooked porkchops" })
                .B("cooking dinner");
            llm.W("craft", () => new { item = "furnace" })
                .W("place", () => new { block = "furnace", at = furnaceAt })
                .W("smelt", () => new { item = "porkchop", count = 2, fuel = "planks", wait = true })
                .W("ok: 2 cooked porkchops");
            await Turn("PoofimaFox", "cook those porkchops?");
            await Until(() => Count(server, Items.CookedPorkchop) == 2, 90_000);
            await Idle("smelting", 60_000);
            Dump();
            Assert.Single(server.Furnaces);
            Assert.True(Count(server, Items.CookedPorkchop) == 2, $"cooked porkchops {Count(server, Items.CookedPorkchop)}");

            // 7. A small hut before dusk (skills: walls with a doorway on the south, then a roof).
            server.Send(new TimeUpdatePacket { Time = 11000 });
            int hx = 14, hz = 12, hy = Ground + 1;
            llm.B("do", new { action = "goto", args = new { to = $"{hx},{hy},{hz}" } }).B("delegate", new { task = "build a small hut around you: walls 2 high, doorway south, roof", done_when = "walls and roof up" })
                .B("building us a hut before dark");
            llm.W("skill", () => new { name = "build_wall", args = new { from = $"{hx - 1},{hy},{hz - 1}", to = $"{hx + 1},{hy},{hz - 1}", height = 2, block = "cobblestone" } })
                .W("skill", () => new { name = "build_wall", args = new { from = $"{hx - 1},{hy},{hz}", to = $"{hx - 1},{hy},{hz + 1}", height = 2, block = "cobblestone" } })
                .W("skill", () => new { name = "build_wall", args = new { from = $"{hx + 1},{hy},{hz}", to = $"{hx + 1},{hy},{hz + 1}", height = 2, block = "cobblestone" } })
                .W("skill", () => new { name = "fill_floor", args = new { from = $"{hx - 1},{hy + 2},{hz - 1}", to = $"{hx + 1},{hy + 2},{hz + 1}", block = "cobblestone" } })
                .W("ok: hut built (walls 2 high, roof, doorway south)");
            await Turn("PoofimaFox", "it's getting late, build a little shelter");
            await Until(() => llm.Worker.IsEmpty, 120_000);
            await Idle("building", 120_000);
            Dump();
            int Placed(int x0, int x1, int y0, int y1, int z0, int z1) =>
                Enumerable.Range(x0, x1 - x0 + 1).Sum(x => Enumerable.Range(y0, y1 - y0 + 1).Sum(y => Enumerable.Range(z0, z1 - z0 + 1)
                    .Count(z => server.Blocks.TryGetValue((x, y, z), out int id) && id == Blocks.Cobblestone)));
            int walls = Placed(hx - 1, hx + 1, hy, hy + 1, hz - 1, hz - 1) + Placed(hx - 1, hx - 1, hy, hy + 1, hz, hz + 1) + Placed(hx + 1, hx + 1, hy, hy + 1, hz, hz + 1);
            int roof = Placed(hx - 1, hx + 1, hy + 2, hy + 2, hz - 1, hz + 1);
            output.WriteLine($"hut: walls {walls}/14, roof {roof}/9");
            Assert.True(walls >= 12 && roof >= 7, $"hut: walls {walls}/14, roof {roof}/9");

            // 8. Dusk: a zombie walks in through the doorway; the bot waits for the 1.12.2 attack charge and kills it.
            server.Send(new TimeUpdatePacket { Time = 13000 });
            server.StartMobAi();
            var me = client.World.Player.Position;
            var zombie = server.SpawnMob(MobTypes.Zombie, me.X + 0.5, me.Y, me.Z + 5, health: 20, chase: true, attackDamage: 2);
            Assert.True(await Until(() => zombie.Dead, 30_000), $"zombie at {zombie.Health} hp; hits {string.Join(" ", server.MeleeHits)}; {client.Combat.Status}");
            var hits = server.MeleeHits.Where(h => !h.Ignored).ToList();
            Assert.NotEmpty(hits);
            // The bot waits for the charge (the fake server's wall-clock charge vs the client's 5 ms ticks can disagree by a few
            // ticks under load: Release1122FightTests pins the exact timing); spam clicking would sit near 0.2.
            Assert.True(hits.Count(h => h.Strength >= 0.85f) * 10 >= hits.Count * 8, $"charges {string.Join(" ", hits.Select(h => h.Strength))}");
            Assert.All(hits, h => Assert.True(h.Strength >= 0.5f, $"hit at charge {h.Strength}"));
            output.WriteLine($"fight: {hits.Count} hits ({string.Join(" ", hits.Select(h => $"{h.Strength:0.00}{(h.Crit ? "!" : "")}"))}), hp {server.Health}");

            // 9. Hungry and hurt: the survival reflex eats the cooked porkchop.
            server.SetVitals(14, 12, 0);
            Assert.True(await Until(() => server.Eaten.Contains(Items.CookedPorkchop), 15_000), $"ate {string.Join(",", server.Eaten)}");
        }
        finally
        {
            Dump();
            output.WriteLine("server dug: " + string.Join(" ", server.Dug.Take(80).Select(d => $"{d.X},{d.Y},{d.Z}:{d.Id}")));
        }
    }
}
