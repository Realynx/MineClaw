using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;
using MineClaw.Bot.WorldIndex;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 15:51: the bot walked through the portal at -40,65,6 and the brain only got "respawned at spawn" (background), so it
/// told players "I somehow ended up back at spawn" while standing in the nether. Then "goto 2,66,0" (the portal block next
/// to it) was a within-1 goal it already met. The nether blocks are the reconstructed live platform: glowstone 3x3x3 at
/// -1..1 (y 63-65), obsidian under the sheet at x 2, portal x 2 / y 66-68 / z 0-1, frame z -1 and 2, void east.
/// </summary>
public class PortalWakeTests
{
    private static void NetherPlatform(FakeGameClient c)
    {
        for (int x = -1; x <= 1; x++) for (int y = 63; y <= 65; y++) for (int z = -1; z <= 1; z++) c.World.SetBlock(x, y, z, Blocks.Glowstone, 0);
        c.World.SetBlock(1, 66, -1, Blocks.Chest, 4);
        foreach (int z in new[] { 0, 1 }) { c.World.SetBlock(2, 65, z, Blocks.Obsidian, 0); c.World.SetBlock(2, 69, z, Blocks.Obsidian, 0); }
        for (int y = 66; y <= 68; y++)
        {
            c.World.SetBlock(2, y, -1, Blocks.Obsidian, 0);
            c.World.SetBlock(2, y, 2, Blocks.Obsidian, 0);
            c.World.SetBlock(2, y, 0, Blocks.Portal, 0);
            c.World.SetBlock(2, y, 1, Blocks.Portal, 0);
        }
    }

    private static async Task<GameEvent?> WaitEvent(BotHost h, Func<GameEvent, bool> match, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (h.Runtime.Events.Snapshot().FirstOrDefault(match) is { } e) return e;
            await Task.Delay(20);
        }
        return null;
    }

    [Fact]
    public async Task Portal_trip_wakes_the_brain_with_dimension_position_and_pair_and_the_header_says_nether()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        h.Runtime.Observations.Portals.Link(new PortalEnd(0, "-41,64,6", -40, 65, 6), new PortalEnd(-1, "2,65,-1", 2, 66, 1));
        NetherPlatform(c);
        c.World.Dimension = -1;
        c.World.Player.ServerTeleport(new Vec3d(2.5, 66, 1.0), 67.62, 0, 0, true);
        c.RaiseRespawned(); // 0x09 Respawn with dimension -1, no death before it

        var e = await WaitEvent(h, x => x.Text.Contains("went through a portal"));
        Assert.NotNull(e);
        Assert.True(e!.MustWake && e.Wakes, "a trip wakes even a sleeping brain");
        Assert.True(e.Priority < h.Runtime.Options.InterruptPriority, "never cancels the job that walked in on purpose");
        Assert.Contains("now in the NETHER at 2,66,1", e.Text);
        Assert.Contains("portal pair recorded: overworld -40,65,6 <-> nether 2,66,1", e.Text);
        Assert.DoesNotContain(h.Runtime.Events.Snapshot(), x => x.Text.Contains("respawned at spawn"));
        Assert.StartsWith("IN THE NETHER pos 2,66,1", h.Runtime.Agent.StateHeader!());
        Assert.StartsWith("[here] IN THE NETHER (standing in the portal", h.Runtime.Agent.HereBlock!(new EventBatch([e])));

        // Dying there and respawning at the overworld spawn is a respawn, not a trip.
        c.RaiseDied();
        c.World.Dimension = 0;
        c.RaiseRespawned();
        Assert.NotNull(await WaitEvent(h, x => x.Text.StartsWith("respawned at spawn in the overworld: server moved me from 2,66,1 (where I died)") && !x.Wakes));
        await Task.Delay(200);
        Assert.Single(h.Runtime.Events.Snapshot(), x => x.Text.Contains("went through a portal"));
    }

    [Fact]
    public async Task Goto_onto_the_portal_block_steps_out_then_in_and_goes_through()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        NetherPlatform(c);
        c.World.Dimension = -1;
        c.World.Player.ServerTeleport(new Vec3d(2.5, 66, 1.0), 67.62, 0, 0, true);
        var cells = new List<BlockPos>();
        // The fake body walks to the goal cell; standing in the sheet (after the cooldown) is what the server switches on.
        c.Effects["goto"] = () =>
        {
            if (c.LastGoal is not GoalBlock g) return;
            cells.Add(g.Target);
            c.World.Player.ServerTeleport(new Vec3d(g.Target.X + 0.5, g.Target.Y, g.Target.Z + 0.5), g.Target.Y + 1.62, 0, 0, true);
            if (cells.Count >= 2 && PortalEntry.InPortal(c.World, c.World.Player.Position)) c.World.Dimension = 0;
        };
        var r = await h.Run("goto", new { to = "2,66,0" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("now in the OVERWORLD", r.Text);
        Assert.Equal(2, cells.Count);
        Assert.Equal(1, cells[0].X);                 // stepped out onto the glowstone first (cooldown runs out there)
        Assert.Equal(2, cells[1].X);                 // then into the sheet
        Assert.Equal(66, cells[1].Y);
    }
}
