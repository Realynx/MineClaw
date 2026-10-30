using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Bot.Brainstem;
using MineClaw.Bot.Game;
using MineClaw.Bot.Perception;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.Scripting;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The gesture path over each version's real wire (Fox 10-29: "nodding my head yes or no ... a signal for the state machine
/// scripting"): a fake player nods with look packets (b1.8.1 0x20 Entity Look; 1.12.2 0x28 Entity Look + 0x36 Entity Head Look,
/// crouches with metadata flag 0x02), the real client tracks it, <see cref="GameSignals"/> recognises it, a running script's
/// <c>on('player.gesture', {kind:'nod'})</c> handler and a machine's transition run, and the nod answers the bot's pending
/// ask ("PoofimaFox: yes (nod)").
/// </summary>
public class GestureFakeServerTests
{
    private static async Task<(MinecraftClient C, FakeServer S)> StartAsync(bool release1122)
    {
        var server = new FakeServer(release1122);
        var o = new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), HostileAlertRadius = 0,
            ConnectionFactory = release1122 ? server.CreateConnection : server.CreateClientConnection,
        };
        if (release1122) o = o with { Profile = MineClaw.Client.Versioning.GameVersionProfiles.Java1122 };
        var client = new MinecraftClient(o);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);
        await Until(() => client.World.Player.OnGround, 3000);
        return (client, server);
    }

    private static async Task<bool> Until(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(5);
        }
        return cond();
    }

    /// <summary>Spawns PoofimaFox 4 blocks east of the bot, looking at its head; returns that look.</summary>
    private static async Task<(float Yaw, float Pitch)> SpawnFox(MinecraftClient c, FakeServer s)
    {
        var me = c.World.Player.Position;
        var fox = new Vec3d(me.X + 4, me.Y, me.Z);
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(fox + new Vec3d(0, PlayerPhysics.EyeHeight, 0), me + new Vec3d(0, PlayerPhysics.EyeHeight - 0.2, 0));
        s.Send(new NamedEntitySpawnPacket
        {
            EntityId = 77, PlayerName = "PoofimaFox", Exact = (fox.X, fox.Y, fox.Z),
            X = FixedPoint.ToAbsolute(fox.X), Y = FixedPoint.ToAbsolute(fox.Y), Z = FixedPoint.ToAbsolute(fox.Z),
            Yaw = FixedPoint.ToAngle(yaw), Pitch = FixedPoint.ToAngle(pitch),
        });
        Assert.True(await Until(() => c.World.Entities.FindPlayer("PoofimaFox") != null, 3000));
        return (yaw, pitch);
    }

    /// <summary>Two down-up nods as the server's tracker sends them: a look packet every 2 ticks (100 ms), 1.12.2 plus the head look.</summary>
    private static async Task NodAsync(FakeServer s, bool release1122, float yaw, float pitch)
    {
        foreach (var off in new float[] { 0, 12, 25, 30, 20, 6, 0, 12, 26, 30, 18, 5, 0 })
        {
            s.Send(new EntityLookPacket { EntityId = 77, Yaw = FixedPoint.ToAngle(yaw), Pitch = FixedPoint.ToAngle(pitch + off) });
            if (release1122) s.Send(new EntityHeadLookMessage { EntityId = 77, Yaw = FixedPoint.ToAngle(yaw) });
            await Task.Delay(90);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fake_player_nods_the_script_handler_runs_and_it_answers_the_pending_question(bool release1122)
    {
        var (c, s) = await StartAsync(release1122);
        await using var _ = c;
        using var __ = s;
        var game = new MinecraftGameClient(c);
        using var signals = new GameSignals(game);
        signals.Attach();

        // A script listening for the nod (the reactive layer: no model in the loop).
        var host = new ScriptHost { World = c.World, Username = "maid_chan" };
        using var scripts = new ScriptController(host, new ScriptOptions());
        signals.Raised += sig => scripts.Deliver(sig.ToScriptEvent());
        var (run, err) = scripts.Start(new ScriptRequest
        {
            Name = "nod_watch", Language = ScriptLanguage.Js, Code = """
                // purpose: hear Fox's nod
                const seen = []
                on('player.gesture', { kind: 'nod', player: 'PoofimaFox' }, e => { seen.push(e.kind + ':' + e.at + ':' + e.answer) })
                for (let i = 0; i < 400 && seen.length === 0; i++) wait(0.1)
                return seen.join(',') || 'none'
                """,
        });
        Assert.True(run != null, err);

        // The brain side: the bot asked Fox something and waits on the answer.
        var q = new EventQueue();
        var chat = new ChatRouter(new ChatOptions { Username = "maid_chan", LineDelayMs = 0 }, q, (_, _) => Task.CompletedTask);
        signals.Raised += new GestureFeed(chat, q).OnSignal;
        var answer = chat.WaitForReplyAsync("PoofimaFox", TimeSpan.FromSeconds(40), default);

        var (yaw, pitch) = await SpawnFox(c, s);
        await NodAsync(s, release1122, yaw, pitch);

        await run!.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Equal("nod:me:yes", run.Result);
        Assert.Equal("PoofimaFox: yes (nod)", await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        if (release1122) Assert.False(float.IsNaN(c.World.Entities.FindPlayer("PoofimaFox")!.HeadYaw)); // the head look was tracked
        else Assert.True(float.IsNaN(c.World.Entities.FindPlayer("PoofimaFox")!.HeadYaw));     // b1.8.1 has none: the look yaw
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_machine_transitions_on_a_crouch_bob_from_metadata(bool release1122)
    {
        var (c, s) = await StartAsync(release1122);
        await using var _ = c;
        using var __ = s;
        using var signals = new GameSignals(new MinecraftGameClient(c));
        signals.Attach();
        var host = new ScriptHost { World = c.World, Username = "maid_chan" };
        using var scripts = new ScriptController(host, new ScriptOptions());
        signals.Raised += sig => scripts.Deliver(sig.ToScriptEvent());
        var (run, err) = scripts.Start(new ScriptRequest
        {
            Name = "bob_machine", Language = ScriptLanguage.Js, Code = """
                // purpose: wait until Fox crouch-bobs, then come
                return machine({
                  initial: 'idle',
                  states: {
                    idle: { tick: () => { wait(0.1) }, on: { 'player.gesture': e => e.kind === 'bob' ? 'come' : null, [after(40000)]: 'done' } },
                    come: { enter: () => 'done' },
                    done: { final: true },
                  },
                })
                """,
        });
        Assert.True(run != null, err);
        await SpawnFox(c, s);
        foreach (var on in new[] { true, false, true, false })
        {
            s.Send(new EntityMetadataPacket { EntityId = 77, Metadata = new EntityMetadata([MetadataEntry.FromByte(0, (sbyte)(on ? 2 : 0))]) });
            await Task.Delay(120);
        }
        await run!.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(run.Ok, run.Outcome);
        var took = run.Finished!.Value - run.Started;
        Assert.True(took < TimeSpan.FromSeconds(35), $"took {took} (the after(40 s) timeout, not the bob?) {run.Outcome}");
    }
}
