using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;
using MineClaw.World.Claims;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests;

/// <summary>
/// One brain, many bodies, step 3, end to end over real clients on fake servers: "67 and 69 mine the gen" is one brain turn
/// with the bodies on different cells (the shared claim board, at=auto); one body's danger interrupts only that body's
/// script; chat naming a body reaches that body (reflex, focus) and never main; and an in-process clone body has the hive
/// clone's survival parity (step 3a): it eats when hungry, keeps off the void, and reports a fight tagged with its body.
/// </summary>
public partial class MultiBodyBrainTests(ITestOutputHelper output)
{
    private const string Main = "maid_chan";
    private static readonly BlockPos West = new(5, 64, 7), East = new(7, 64, 7);

    // ------------------------------------------------------------------ rig

    /// <summary>One claim board for the hive; a lease stays until its holder releases it.</summary>
    private sealed class SharedBoard
    {
        public readonly Dictionary<ClaimKey, string> Held = new();
        public IClaimBoard For(string owner) => new View(this, owner);

        private sealed class View(SharedBoard b, string owner) : IClaimBoard
        {
            public string Owner => owner;
            public bool IsFree(ClaimKey key) { lock (b.Held) return !b.Held.Any(h => h.Value != owner && h.Key.ConflictsWith(key)); }
            public string? HolderOf(ClaimKey key) { lock (b.Held) return b.Held.FirstOrDefault(h => h.Value != owner && h.Key.ConflictsWith(key)).Value; }
            public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
            {
                lock (b.Held)
                {
                    var other = b.Held.FirstOrDefault(h => h.Value != owner && h.Key.ConflictsWith(key));
                    if (other.Value != null) return Task.FromResult(ClaimResult.Refused(other.Value, DateTimeOffset.MaxValue));
                    b.Held[key] = owner;
                    return Task.FromResult(ClaimResult.Granted(owner, DateTimeOffset.MaxValue));
                }
            }
            public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default)
            {
                lock (b.Held) if (b.Held.TryGetValue(key, out var o) && o == owner) b.Held.Remove(key);
                return Task.CompletedTask;
            }
        }
    }

    /// <summary>The brain's model: scripted Opus rounds; the worker and quick calls answer at once.</summary>
    private sealed class BrainLlm : ILlmClient
    {
        public readonly ConcurrentQueue<Func<LlmRequest, LlmResponse>> Script = new();
        public readonly List<LlmRequest> Brain = new();

        public BrainLlm Tool(string name, object input) { Script.Enqueue(r => Resp(r, StopReasons.ToolUse, new LlmToolUse("tu_" + Script.Count, name, JsonSerializer.SerializeToElement(input)))); return this; }
        public BrainLlm Text(string text) { Script.Enqueue(r => Resp(r, StopReasons.EndTurn, new LlmText(text))); return this; }

        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            if (r.Purpose.StartsWith("quick_", StringComparison.Ordinal) || r.Tier != ModelTier.Main) return Task.FromResult(Resp(r, StopReasons.EndTurn, new LlmText("")));
            lock (Brain) Brain.Add(r);
            return Task.FromResult(Script.TryDequeue(out var h) ? h(r) : Resp(r, StopReasons.EndTurn, new LlmText("(silent)")));
        }

        private static LlmResponse Resp(LlmRequest r, string stop, params LlmBlock[] blocks) =>
            new(blocks, stop, new LlmUsage(100, 20, 1000, 0), r.Model ?? "claude-opus-5-5");
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required Dictionary<string, FakeServer> Servers;
        public required MinecraftClient MainClient;
        public required BotHost H;
        public required WorldMemoryFlusher Flusher;
        public required List<Body> Built;
        public required BodyDirectory Dir;
        public BodyRegistry Reg => H.Bodies!;
        public Agent.AgentRuntime Rt => H.Runtime;

        public async ValueTask DisposeAsync()
        {
            await Reg.StopAllAsync();
            foreach (var b in Built) { BodyFactory.DisposeClone(b, Flusher); await b.Client!.DisposeAsync(); }
            H.Dispose();
            await MainClient.DisposeAsync();
            Flusher.Dispose();
            foreach (var s in Servers.Values) s.Dispose();
        }
    }

    private sealed class ServerHost(BrainContext brain, Dictionary<string, FakeServer> servers, MinecraftClient main, WorldMemoryFlusher flusher,
        SharedBoard? board, List<Body> built) : IBodyHost
    {
        public Body Create(string name)
        {
            var body = BodyFactory.CreateClone(brain, new MinecraftClient(Opts(name, servers[name])), board?.For(name), flusher);
            lock (built) built.Add(body);
            return body;
        }
        public Task ConnectAsync(IBody body, CancellationToken ct) => ClientBodySessions.ConnectAsync(body.Client!, ct);
        public Task<BodyExit> RunAsync(IBody body, CancellationToken ct) => ClientBodySessions.RunAsync(body.Client!, ct);
        public Task DisconnectAsync(IBody body, string reason) => ClientBodySessions.DisconnectAsync(body.Client!, reason);
        public BodySlots Slots() => new(main.World.MaxPlayers, main.World.Entities.OnlinePlayers);
        public BodySlotDecision CanSpawn(int pending) => new(true, "test");
        public IReadOnlyList<string> MustYield(IReadOnlyList<BodyProgress> running) => [];
        public Task<BodyDeposit> DepositAsync(IBody body, TimeSpan timeout, CancellationToken ct) => Task.FromResult(new BodyDeposit(true, 0, "inventory empty"));
        public void Post(GameEvent e) => brain.Runtime.Events.Post(e);
    }

    private static ClientOptions Opts(string name, FakeServer s) => new()
    {
        Username = name, TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero, RespawnDelay = TimeSpan.FromMilliseconds(10),
        ConnectionFactory = s.CreateClientConnection, HostileAlertRadius = 0, AutoReconnect = false,
    };

    private static async Task<bool> Until(Func<bool> cond, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(5); }
        return cond();
    }

    private static async Task<Rig> StartAsync(Action<FakeServer> layout, Agent.Llm.ILlmClient llm, SharedBoard? board, params string[] clones)
    {
        var names = new[] { Main }.Concat(clones).ToArray();
        var servers = names.ToDictionary(n => n, _ => { var s = new FakeServer(); layout(s); return s; }, StringComparer.OrdinalIgnoreCase);
        var mainClient = new MinecraftClient(Opts(Main, servers[Main]));
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await mainClient.ConnectAsync(cts.Token);
        Assert.True(await Until(() => mainClient.World.Player.OnGround), "main on ground");
        var options = TestBot.Options();
        options.Self.TrustedPlayers = ["PoofimaFox"];
        var h = BotHost.Create(new MinecraftGameClient(mainClient), options, new BotOptions { GeneratorQueueSeconds = 0 }, llm, persist: false,
            claims: board?.For(Main), hiveBots: () => names);
        var flusher = new WorldMemoryFlusher();
        var built = new List<Body>();
        var host = new ServerHost(h.Brain!, servers, mainClient, flusher, board, built);
        h.Bodies!.Configure(host, clones, new BodiesOptions { LaunchGapSeconds = 0, YieldCheckSeconds = 0, TickReportSeconds = 0 }, timers: false);
        var dir = BodyDirectory.Enable(h.Brain!);
        var rig = new Rig { Servers = servers, MainClient = mainClient, H = h, Flusher = flusher, Built = built, Dir = dir };
        foreach (var c in clones)
        {
            var b = h.Bodies.Spawn(c).Body!;
            Assert.True(await h.Bodies.WaitOnlineAsync(b, TimeSpan.FromSeconds(10)), c + " online");
            Assert.True(await Until(() => b.Adapter.World.Player.OnGround), c + " on ground");
        }
        return rig;
    }

    private static ItemStack Stack(int id, int n) => new((short)id, (byte)n, 0);

    /// <summary>The dual gen with its floor (ProtectionPolicyTests): dig cells 5,64,7 and 7,64,7, lava and water between.</summary>
    private static void DualGen(FakeServer srv)
    {
        srv.Blocks.Clear();
        for (int x = 1; x <= 11; x++)
        for (int z = 3; z <= 11; z++)
            srv.Blocks[(x, 63, z)] = Blocks.Cobblestone;
        for (int x = 4; x <= 8; x++)
        {
            srv.Blocks[(x, 64, 6)] = Blocks.Cobblestone;
            srv.Blocks[(x, 64, 8)] = Blocks.Cobblestone;
        }
        srv.Blocks[(4, 64, 7)] = Blocks.Lava;
        srv.Blocks[(5, 64, 7)] = Blocks.Cobblestone;
        srv.Blocks[(6, 64, 7)] = Blocks.Water;
        srv.Blocks[(7, 64, 7)] = Blocks.Cobblestone;
        srv.Blocks[(8, 64, 7)] = Blocks.Lava;
        srv.Regenerate[(5, 64, 7)] = Blocks.Cobblestone;
        srv.Regenerate[(7, 64, 7)] = Blocks.Cobblestone;
        srv.SpawnAt = (6.5, 64, 4.5);
        srv.Inventory[36] = Stack(Items.StonePickaxe, 1);
        srv.DropOnDig = true;
    }

    private static HashSet<BlockPos> Dug(FakeServer s) => s.Dug.Select(d => new BlockPos(d.X, d.Y, d.Z)).ToHashSet();

    // ------------------------------------------------------------------ the goal: one turn, two bodies, two cells

    [Fact]
    public async Task Sixty_seven_and_sixty_nine_mine_the_gen_in_one_brain_turn_on_different_cells()
    {
        var llm = new BrainLlm()
            .Tool("do", new { action = "harvest_generator", args = new { at = "auto", count = 10 }, body = new[] { "67", "69" } })
            .Text("67 and 69 are on the gen");
        var board = new SharedBoard();
        await using var rig = await StartAsync(DualGen, llm, board, "maid_chan_67", "maid_chan_69");
        foreach (var cell in new[] { West, East }) rig.H.DigCells!.Register(cell, "PoofimaFox");
        var rt = rig.Rt;
        var b67 = rig.Reg.Find("67")!;
        var b69 = rig.Reg.Find("69")!;

        var o = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "67 and 69 mine the gen")]), CancellationToken.None);
        Assert.Equal(2, o.Calls); // ONE Opus turn: the do + the closing line
        Assert.Equal(2, llm.Brain.Count);
        var result = llm.Brain[1].Messages[^1].Content.OfType<LlmToolResult>().Single().Content;
        output.WriteLine("tool result: " + result);
        Assert.StartsWith("67: ", result);
        Assert.Contains("; 69: ", result);

        Assert.True(await Until(() => Dug(rig.Servers["maid_chan_67"]).Count > 0 && Dug(rig.Servers["maid_chan_69"]).Count > 0, 30000),
            $"both dig: 67 {Dug(rig.Servers["maid_chan_67"]).Count}, 69 {Dug(rig.Servers["maid_chan_69"]).Count}");
        Assert.True(await Until(() => !b67.Activities.IsBusy && !b69.Activities.IsBusy, 30000), "both finish");
        var d67 = Dug(rig.Servers["maid_chan_67"]);
        var d69 = Dug(rig.Servers["maid_chan_69"]);
        output.WriteLine($"67 dug {string.Join(" ", d67)}; 69 dug {string.Join(" ", d69)}");
        Assert.All(d67.Concat(d69), p => Assert.Contains(p, new[] { West, East }));
        Assert.Empty(d67.Intersect(d69));             // different cells (the claim board, at=auto)
        Assert.Empty(rig.Servers[Main].Dug);         // main never moved a pick
        Assert.Equal(0, rig.H.Brain!.Tools.MissingScopeCount);

        // Their results wake the brain once, as one line.
        Assert.True(await Until(() => rt.Events.Snapshot().Count(e => EventBatch.BodyTask(e)) >= 2, 5000),
            string.Join(" | ", rt.Events.Snapshot().Select(e => e.Format())));
        // The first to finish waits (non-waking) while the other still mines; finishing within the same moment, both simply
        // land in the same batch window. Either way the brain takes them in ONE batch, as one line.
        var batch = rt.Events.TryTakeBatch();
        Assert.NotNull(batch);
        var tasks = batch!.Events.Where(EventBatch.BodyTask).ToList();
        Assert.Equal(2, tasks.Count);
        var line = new EventBatch(tasks).Format();
        output.WriteLine(line);
        Assert.StartsWith("[bodies] ", line);
        Assert.Contains("67 ok", line);
        Assert.Contains("69 ok", line);
        Assert.Single(line.Split('\n'));
    }

    // ------------------------------------------------------------------ interrupts are per body

    [Fact]
    public async Task Sixty_nines_danger_pauses_only_its_own_script_and_never_cancels_67s()
    {
        var llm = new BrainLlm();
        await using var rig = await StartAsync(s => s.SpawnAt = (8.5, 64, 8.5), llm, null, "maid_chan_67", "maid_chan_69");
        var rt = rig.Rt;
        var b67 = (Body)rig.Reg.Find("67")!;
        var b69 = (Body)rig.Reg.Find("69")!;
        const string code = "for (let i = 1; i <= 600; i++) { wait(0.05); prog(i / 6) }";
        foreach (var b in new[] { b67, b69 })
        {
            var body = b;
            _ = Task.Run(async () =>
            {
                using (BodyScope.Push(body))
                using (ToolCaller.Push(ModelTier.Main, rt.Options.Llm.MainModel))
                    return await rt.Tools.ExecuteAsync("script", JsonSerializer.SerializeToElement(new { op = "run", name = "loop" + body.Short, code }), CancellationToken.None);
            });
        }
        Assert.True(await Until(() => b67.Scripts?.Current?.Status == Scripting.ScriptStatus.Running && b69.Scripts?.Current?.Status == Scripting.ScriptStatus.Running),
            "both lanes run");
        Assert.Null(rig.H.Body!.Scripts?.Current); // the script tool routed both to their own lanes, main's is free
        Assert.StartsWith("script", b67.Activities.Current?.Label ?? ""); // each holds its own slot
        Assert.StartsWith("script", b69.Activities.Current?.Label ?? "");

        rt.Events.Post(GameEvent.LowHealth(4, cause: "zombie") with { Body = "69" });
        Assert.True(await Until(() => b69.Scripts!.Current?.Status == Scripting.ScriptStatus.Paused, 5000), "69's lane paused on its danger");
        await Task.Delay(300);
        Assert.Equal(Scripting.ScriptStatus.Running, b67.Scripts!.Current!.Status); // 67 keeps mining
        Assert.StartsWith("script", b67.Activities.Current?.Label ?? "");

        // A trusted "69 stop" is 69's; "67" never hears it. A plain trusted "stop" reaches every lane.
        rt.Events.Post(GameEvent.Chat("PoofimaFox", "69 stop"));
        await Task.Delay(200);
        Assert.Equal(Scripting.ScriptStatus.Running, b67.Scripts!.Current!.Status);
        rt.Events.Post(GameEvent.Chat("PoofimaFox", "stop"));
        Assert.True(await Until(() => b67.Scripts!.Current?.Status is not Scripting.ScriptStatus.Running, 5000), "a plain stop reaches 67's lane");
        b67.Scripts!.Current?.Stop("test over");
        b69.Scripts!.Current?.Stop("test over");
    }

    // ------------------------------------------------------------------ addressing reaches the body

    /// <summary>
    /// Live 10-30: 1337 and 126 kept running the last crew's "deforest" (felled 0) and refused the new crew's lane every 30 s
    /// ("body 1337 busy: script deforest running L102"), standing still for good. A crew lane takes over a body whose run isn't
    /// a lane of a live crew.
    /// </summary>
    [Fact]
    public async Task A_crew_lane_takes_over_a_body_still_running_a_leftover_script()
    {
        await using var rig = await StartAsync(DualGen, new BrainLlm(), null, "maid_chan_67");
        var b67 = rig.H.Bodies!.Find("maid_chan_67") as Body;
        Assert.NotNull(b67);
        var (old, err) = b67!.ScriptBridge!.Controller.Start(new MineClaw.Scripting.ScriptRequest
        {
            Name = "old_deforest", Language = MineClaw.Scripting.ScriptLanguage.Js,
            Code = """
                // purpose: the last crew's leftover
                for (let i = 0; i < 600; i++) wait(0.1)
                return 'old'
                """,
        });
        Assert.True(old != null, err);
        Assert.True(await Until(() => b67.ScriptBridge.Controller.Current != null));
        var port = new BodyCrewPort(rig.H.Bodies!, rig.H.Runtime.Tools, new MineClaw.Scripting.Crew.InMemoryCrewBoard());
        var code = """
            // purpose: one role
            function cutter() { return 'cut' }
            function main() { return 'x' }
            """;
        var lane = port.Start(new MineClaw.Scripting.Crew.LaneSpec("67", "crew_job", code, MineClaw.Scripting.ScriptLanguage.Js, "cutter", null, "c-new"));
        var r = await lane.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(r.Ok, r.Msg);
        Assert.Contains("cut", r.Msg);
        Assert.True(old!.Finished != null, "the leftover run ended");
    }

    [Fact]
    public async Task Chat_to_67_runs_67s_reflex_and_focuses_67_while_main_stays_put()
    {
        var llm = new BrainLlm().Tool("status", new { }).Text("67 is here");
        await using var rig = await StartAsync(s => s.SpawnAt = (8.5, 64, 8.5), llm, null, "maid_chan_67");
        var rt = rig.Rt;
        var b67 = rig.Reg.Find("67")!;
        // A job on each body, then "67 stop" from a trusted player: only 67's slot stops (its brainstem's reflex).
        var hold = new DelegateTool("hold_still", "test", ToolSchema.Empty, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ToolResult.Success("held"); }, longRunning: true);
        _ = rig.H.Body!.Activities.RunAsync([(hold, ToolSchema.Empty)], TimeSpan.Zero, CancellationToken.None);
        _ = b67.Activities.RunAsync([(hold, ToolSchema.Empty)], TimeSpan.Zero, CancellationToken.None);
        Assert.True(await Until(() => rig.H.Body.Activities.IsBusy && b67.Activities.IsBusy));
        rt.Events.Post(GameEvent.Chat("PoofimaFox", "67 stop"));
        Assert.True(await Until(() => !b67.Activities.IsBusy, 5000), "67 stopped by its reflex");
        Assert.True(rig.H.Body.Activities.IsBusy); // main never heard it
        rig.H.Body.Activities.Cancel("test");

        // The brain's turn about 67: 67 is the focus, an unscoped status reads 67, [bodies] carries main.
        rt.Events.TryTakeBatch(); // the stop line's own turn isn't this test's
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "67, what's up")]), CancellationToken.None);
        Assert.Equal("67", rt.Agent.TurnFocus);
        var head = llm.Brain[0].Messages[^1].Content.OfType<LlmText>().First().Text;
        output.WriteLine(head);
        Assert.StartsWith("67: pos ", head);
        Assert.Contains("[bodies] main ", head);
        var status = llm.Brain[1].Messages[^1].Content.OfType<LlmToolResult>().Single();
        Assert.False(status.IsError, status.Content);
    }

    // ------------------------------------------------------------------ 3a: survival parity with a hive clone

    [Fact]
    public async Task A_clone_body_eats_when_hungry_keeps_off_the_void_and_reports_a_fight_with_its_body()
    {
        var llm = new BrainLlm();
        await using var rig = await StartAsync(s => { s.SpawnAt = (8.5, 64, 8.5); s.Inventory[36] = Stack(Items.Bread, 3); }, llm, null, "maid_chan_67");
        var rt = rig.Rt;
        var b67 = (Body)rig.Reg.Find("67")!;
        var srv = rig.Servers["maid_chan_67"];
        var posted = new ConcurrentQueue<GameEvent>();
        rt.Events.Posted += posted.Enqueue;

        // Eats when hungry (the client's survival reflex on the clone's own connection), and its ledger says so.
        srv.SetVitals(14, 8);
        Assert.True(await Until(() => srv.Eaten.Contains(Items.Bread), 5000), "67 ate");
        Assert.Empty(rig.Servers[Main].Eaten);
        Assert.NotNull(b67.Ledger);
        string story;
        using (BodyScope.Push(b67)) story = (await rig.H.Run("inventory", new { item = "bread" })).Text;
        output.WriteLine("ledger: " + story);
        Assert.Contains("Bread: have 2", story); // 67's own ledger (main carries no bread): the meal went from 3 to 2
        Assert.DoesNotContain("no gains or losses", story);

        // Defends, and the fight report reaches the one queue tagged with 67 (never as main's).
        srv.Send(new MobSpawnPacket { EntityId = 9, Type = (byte)MobTypes.Zombie, X = FixedPoint.ToAbsolute(7.0), Y = 64 * 32, Z = FixedPoint.ToAbsolute(8.5) });
        Assert.True(srv.WaitFor(p => p is UseEntityPacket { TargetEntityId: 9 }, 4000), "67 hit back");
        Assert.True(await Until(() => posted.Any(e => e.Body == "67" && e.Text.Contains("fight", StringComparison.OrdinalIgnoreCase)), 3000),
            "fight report: " + string.Join(" | ", posted.Select(e => e.Format())));
        Assert.DoesNotContain(posted, e => e.Body == null && e.Text.Contains("zombie", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(rig.Servers[Main].Got<UseEntityPacket>());
        var report = posted.First(e => e.Body == "67" && e.Text.Contains("fight", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("[67 ", report.Format());

        // Keeps off the void: a knock toward the platform's edge never leaves it over the void.
        bool over = false;
        srv.Send(new EntityVelocityPacket { EntityId = FakeServer.EntityId, VelocityX = FixedPoint.ToVelocity(0.3), VelocityY = FixedPoint.ToVelocity(0.36), VelocityZ = 0 }); // CombatTests' knock
        var end = DateTime.UtcNow.AddMilliseconds(1200);
        while (DateTime.UtcNow < end)
        {
            over |= b67.Adapter.World.IsPlayerOverVoid(b67.Adapter.World.Player.Position);
            await Task.Delay(2);
        }
        Assert.False(over, "67 went over the void");
    }
}
