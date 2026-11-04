using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>
/// Live 15:51: the bot walked into the lit portal at -40,65,6 while enter_portal said "no lit portal nearby", came out IN
/// the nether portal (x 2, y 66-68, z 0-1) on the SkyBlock nether platform (glowstone 3x3x3 at -1..1, y 63-65; void east
/// of x 2), and couldn't go back: return_portal "no portal known", goto 2,66,0 "stops at z=1". Causes: portal lookups went
/// through the protection filter (the world indexer protects every sheet), and the server drops a player INSIDE the exit
/// portal where its 10-tick cooldown refreshes every tick, so standing there never travels. The worlds are the
/// reconstructed blocks from the live db (tests/fixtures/portal-trip), the server is <see cref="PortalSim"/>.
/// </summary>
public class PortalTripTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private static readonly (double, double, double) NetherArrival = (2.5, 66.5, 1.0);      // live "server teleport to 2.5,66.5,1"
    private static readonly (double, double, double) OverworldArrival = (-39.0, 65.5, 6.5); // live "server teleport to -39,65.5,6.5"

    private async Task<(MinecraftClient C, FakeServer S, PortalSim P)> Start(int dim, (double X, double Y, double Z) at)
    {
        var server = new FakeServer();
        var sim = new PortalSim(server, dim);
        if (dim != 0) sim.JustArrived(); // came through the portal it stands in
        sim.World(0, PortalSim.Fixture("portal-trip", "overworld.txt"), OverworldArrival);
        sim.World(-1, PortalSim.Fixture("portal-trip", "nether.txt"), NetherArrival);
        server.SpawnAt = at;
        var client = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
            Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } },
            // The world indexer protects every portal sheet and its obsidian frame.
            IsProtected = (_, id) => id is Blocks.Portal or Blocks.Obsidian,
        });
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await client.ConnectAsync(cts.Token);
        sim.SendChunks();
        if (dim != 0) server.Send(new MineClaw.Protocol.Packets.RespawnPacket { Dimension = (sbyte)dim, Difficulty = 1, WorldHeight = 128, MapSeed = 1234 });
        if (dim != 0) { sim.SendChunks(); server.Send(new MineClaw.Protocol.Packets.PlayerPositionLookPacket { X = at.X, Y = at.Y, Stance = at.Y + 1.62, Z = at.Z }); }
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!(client.World.Player.OnGround && client.World.Dimension == dim && !client.IsFrozenForSafety) && DateTime.UtcNow < until) await Task.Delay(5);
        Assert.True(client.World.Player.OnGround, "landed");
        return (client, server, sim);
    }

    private static Task<SkillResult> Run(MinecraftClient c, SkillState state, string skill) =>
        SkillRegistry.CreateDefault().RunAsync(new SkillContext(new ClientGameActions(c), SkillRegistry.CreateDefault()) { State = state },
            skill, FakeGame.J(new { }), CancellationToken.None);

    [Fact]
    public void Reconstructed_nether_portal_is_standable_and_has_a_way_out()
    {
        var w = new WorldState();
        var blocks = PortalSim.Fixture("portal-trip", "nether.txt").ToList();
        w.ApplyChunkRegion(-32, 0, -32, 64, 128, 64, Region(blocks, -32, -32, 64), null, null, null);
        w.Dimension = -1;
        var arrived = new Vec3d(2.5, 66, 1.0);
        Assert.True(PortalEntry.InPortal(w, arrived), "the server drops the player inside the exit portal");
        Assert.False(PortalEntry.InPortal(w, new Vec3d(1.6, 66, 0.5)), "1.6,66,0.5 (live script stop) is out of the sheet");
        Assert.Equal([new BlockPos(2, 66, 0), new BlockPos(2, 66, 1)], PortalEntry.StandCells(w, new BlockPos(2, 67, 0), arrived).OrderBy(p => p.Z));
        var outs = PortalEntry.StepOutCells(w, arrived);
        Assert.Contains(new BlockPos(1, 66, 1), outs);
        Assert.Contains(new BlockPos(1, 66, 0), outs);
        Assert.DoesNotContain(outs, p => p.X >= 2);                // the sheet itself and the void east of it
        Assert.DoesNotContain(new BlockPos(1, 66, -1), outs);       // the chest
        Assert.True(Safety.IsStandable(w, 2, 66, 0), "a portal cell over obsidian is a foot cell (no collision, not a hazard)");
        Assert.False(Safety.IsHazard(w, Blocks.Portal));
    }

    private static byte[] Region(List<(int X, int Y, int Z, int Id, int Meta)> blocks, int x0, int z0, int size)
    {
        var ids = new byte[size * 128 * size];
        foreach (var b in blocks)
        {
            int lx = b.X - x0, lz = b.Z - z0;
            if (lx < 0 || lz < 0 || lx >= size || lz >= size) continue;
            ids[(lx * size + lz) * 128 + b.Y] = (byte)b.Id; // ApplyChunkRegion order: x, z, y
        }
        return ids;
    }

    [Fact]
    public async Task Stranded_in_the_nether_portal_return_portal_steps_out_and_back_in()
    {
        var (c, s, sim) = await Start(-1, NetherArrival);
        await using var _c = c;
        using var _s = s;
        Assert.True(PortalEntry.InPortal(c.World, c.World.Player.Position), "starts inside the exit portal, like live");
        // The live failure first: standing in the sheet never travels (the cooldown refreshes every tick). Waits on the server's
        // tick count, not the wall clock: under load (a full test run) 800 ms held far fewer than 80 movement ticks.
        var until = DateTime.UtcNow.AddSeconds(20);
        while (sim.CooldownRefreshes <= 80 && c.World.Dimension == -1 && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Equal(-1, c.World.Dimension);
        Assert.True(sim.CooldownRefreshes > 80, $"cooldown refreshed {sim.CooldownRefreshes}x");

        var state = new SkillState { Known = () => [new PortalPair(new BlockPos(-39, 65, 6), new BlockPos(2, 66, 0), DateTimeOffset.UtcNow)] };
        var r = await Run(c, state, "return_portal");
        log.WriteLine(r.Text);
        Assert.True(r.Ok, r.Text);
        Assert.Equal(0, c.World.Dimension);
        Assert.Equal(0, sim.Dimension);
        Assert.Contains("now in the overworld", r.Text);
        var p = c.World.Player.Position;
        Assert.InRange(p.X, -41, -37);
        Assert.InRange(p.Z, 5, 8);
    }

    [Fact]
    public async Task Round_trip_overworld_to_nether_and_back_with_protected_portals()
    {
        var (c, s, sim) = await Start(0, (-39.5, 64, 1.5)); // on the bridge west of spawn, 5 blocks from the portal
        await using var _c = c;
        using var _s = s;
        var state = new SkillState();

        var there = await Run(c, state, "enter_portal");
        log.WriteLine(there.Text);
        Assert.True(there.Ok, there.Text); // live: "no lit portal nearby" (the protected sheet was filtered out)
        Assert.Equal(-1, c.World.Dimension);
        Assert.Contains("now in the nether", there.Text);
        var pair = Assert.Single(state.Portals);
        Assert.InRange(pair.Overworld.X, -40, -39);
        Assert.Equal(6, pair.Overworld.Z);
        Assert.Equal(2, pair.Nether.X);

        var back = await Run(c, state, "return_portal");
        log.WriteLine(back.Text);
        Assert.True(back.Ok, back.Text);
        Assert.Equal(0, c.World.Dimension);

        // And straight through again from the overworld arrival (inside the sheet): step out, wait, step in.
        Assert.True(PortalEntry.InPortal(c.World, c.World.Player.Position));
        var again = await Run(c, state, "enter_portal");
        Assert.True(again.Ok, again.Text);
        Assert.Equal(-1, c.World.Dimension);
        Assert.Equal(3, sim.Switches);
        Assert.True(c.World.Player.Position.Y > 60, "never fell into the nether void");
    }
}
