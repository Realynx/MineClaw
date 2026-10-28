using System.Collections.Concurrent;
using MineClaw.Bot.Perception;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>A clock tests move by hand.</summary>
public sealed class SignalClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero).UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(double seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
}

/// <summary>
/// The typed game events from fake packets (docs/design/reactive-character.md). b1.8.1 and 1.12.2 share the canonical client
/// events (animation, entity status, collect, equipment, spawns, block changes, health, time, rain); the version-specific source
/// is the punch: b1.8.1 infers it from a swing + raycast, 1.12.2 also has the Block Break Animation.
/// </summary>
public class GameSignalsTests
{
    private static (GameSignals S, FakeGameClient C, SignalClock T, ConcurrentQueue<GameSignal> Got) Make()
    {
        var c = new FakeGameClient();
        c.Floor(64, 8);
        c.World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        c.World.Player.SetHealth(20, 20, 5);
        var t = new SignalClock();
        var s = new GameSignals(c, n => n == "robinchan", t) { Intent = text => text.Contains("ty") ? "thanks" : "banter" };
        var got = new ConcurrentQueue<GameSignal>();
        s.Raised += got.Enqueue;
        return (s, c, t, got);
    }

    private static List<GameSignal> Of(ConcurrentQueue<GameSignal> q, string name) => q.Where(x => x.Name == name).ToList();

    [Fact]
    public void B181_punch_is_a_swing_plus_raycast_debounced_on_the_same_block()
    {
        var (s, c, t, got) = Make();
        c.World.Entities.SpawnPlayer(7, "PoofImaFox", new Vec3d(3.5, 65, 3.5), 0, 90, heldItem: Items.StonePickaxe); // looking straight down
        s.OnAnimation(7, 1);
        t.Advance(0.3); s.OnAnimation(7, 1);
        t.Advance(0.3); s.OnAnimation(7, 1);
        var punch = Assert.Single(Of(got, "player.punchBlock"));
        Assert.Equal("PoofImaFox", punch.Player);
        Assert.Equal("stone", punch.Data["block"]!.ToString());
        Assert.Equal("stone_pickaxe", punch.Data["held"]!.ToString());
        Assert.Equal(3, (int)punch.Data["pos"]!["x"]!);
        Assert.Equal(64, (int)punch.Data["pos"]!["y"]!);
        Assert.Equal(3, Of(got, "player.swing").Count);
        t.Advance(2); s.OnAnimation(7, 1); // a new punch once the debounce passed
        Assert.Equal(2, Of(got, "player.punchBlock").Count);
        c.World.Entities.SetHeldItem(7, Blocks.Cobblestone); // a block in hand: the swing places, it doesn't point
        t.Advance(2); s.OnAnimation(7, 1);
        Assert.Equal(2, Of(got, "player.punchBlock").Count);
        c.World.Entities.SpawnPlayer(8, "robinchan", new Vec3d(1.5, 65, 1.5), 0, 90);
        s.OnAnimation(8, 1); // other bots never trigger anything
        Assert.DoesNotContain(got, g => g.Player == "robinchan");
    }

    [Fact]
    public void R1122_block_break_animation_is_a_punch_too()
    {
        var (s, c, _, got) = Make();
        c.World.Entities.SpawnPlayer(7, "PoofImaFox", new Vec3d(3.5, 65, 3.5), 0, 0);
        s.OnBreakAnimation(7, new BlockPos(2, 64, 2), 3);
        s.OnBreakAnimation(7, new BlockPos(2, 64, 2), 4); // the same dig
        var p = Assert.Single(Of(got, "player.punchBlock"));
        Assert.Equal("empty", p.Data["held"]!.ToString());
    }

    [Fact]
    public void R1122_block_break_animation_reaches_punch_through_the_client_event()
    {
        // The wiring (multi-version P2): the client's BlockBreakProgress → Attach → player.punchBlock.
        var (s, c, _, got) = Make();
        s.Attach();
        c.World.Entities.SpawnPlayer(7, "PoofImaFox", new Vec3d(3.5, 65, 3.5), 0, 0, heldItem: Items.IronAxe);
        c.RaiseBlockBreak(7, new BlockPos(2, 64, 2), 0);
        c.RaiseBlockBreak(7, new BlockPos(2, 64, 2), -1); // stopped: not a punch
        var p = Assert.Single(Of(got, "player.punchBlock"));
        Assert.Equal("iron_axe", p.Data["held"]!.ToString());
        Assert.Equal(2, (int)p.Data["pos"]!["x"]!);
    }

    [Fact]
    public void Hurt_died_pickup_equip_toss_drop_sneak()
    {
        var (s, c, t, got) = Make();
        c.World.Entities.SpawnPlayer(7, "PoofImaFox", new Vec3d(3.5, 65, 3.5), 0, 0);
        c.World.Entities.SpawnPlayer(9, "kiwi", new Vec3d(4.5, 65, 3.5), 90, 0);
        s.OnAnimation(9, 1);                // kiwi swings...
        s.OnStatus(7, 2);                   // ...and Fox gets hurt
        var hurt = Assert.Single(Of(got, "player.hurt"));
        Assert.Equal("kiwi", hurt.Data["by"]!.ToString());
        Assert.Equal("melee", hurt.Data["cause"]!.ToString());
        s.OnStatus(7, 3);
        Assert.Single(Of(got, "player.died"));

        c.World.Entities.SpawnItem(50, new ItemStack((short)Items.Diamond, 2, 0), new Vec3d(3.5, 64.2, 3.5));
        s.OnCollect(7, 50, null);
        var pick = Assert.Single(Of(got, "player.pickup"));
        Assert.Equal("diamond", pick.Data["item"]!.ToString());
        Assert.Equal(2, (int)pick.Data["count"]!);

        s.OnEquip(7, 0, Items.IronPickaxe);
        Assert.Equal("iron_pickaxe", Assert.Single(Of(got, "player.equip")).Data["item"]!.ToString());

        var toss = c.World.Entities.SpawnItem(51, new ItemStack((short)Items.Bread, 3, 0), new Vec3d(3.5, 66.3, 3.6));
        s.OnSpawn(toss);
        Assert.Single(Of(got, "item.dropped"));
        Assert.Equal("PoofImaFox", Assert.Single(Of(got, "player.toss")).Player);

        s.OnAnimation(7, 104); s.OnAnimation(7, 104); s.OnAnimation(7, 105);
        Assert.Single(Of(got, "player.sneak"));
        Assert.Single(Of(got, "player.unsneak"));
    }

    [Fact]
    public void Polled_near_leave_mobs_gaze_time_weather_and_my_health()
    {
        var (s, c, t, got) = Make();
        c.World.Time = 12000;
        c.World.Entities.SpawnPlayer(7, "PoofImaFox", new Vec3d(3.5, 65, 0.5), 90, 0); // yaw 90 = facing -X: at me
        s.Tick();
        Assert.Single(Of(got, "player.near"));
        t.Advance(1.2); s.Tick();
        var look = Assert.Single(Of(got, "player.lookAt"));
        Assert.Equal("me", look.Data["target"]!.ToString());

        c.World.Entities.SpawnMob(20, MineClaw.World.MobTypes.Zombie, new Vec3d(2.5, 65, 2.5), 0, 0);
        c.World.Time = 12600;
        c.World.Rain = 1;
        s.Tick();
        Assert.Equal("zombie", Assert.Single(Of(got, "mob.near")).Data["mob"]!.ToString());
        Assert.Single(Of(got, "time.dusk"));
        Assert.Single(Of(got, "weather"));

        c.World.Entities.Teleport(7, new Vec3d(30.5, 65, 0.5), 90, 0);
        s.Tick();
        Assert.Single(Of(got, "player.leave"));

        s.OnHealth(20); s.OnHealth(17); s.OnHealth(7); s.OnHealth(6);
        Assert.Equal(3, Of(got, "me.hurt").Count);
        Assert.Equal("zombie", Of(got, "me.hurt")[0].Data["by"]!.ToString());
        Assert.Single(Of(got, "me.lowHp"));
    }

    [Fact]
    public void Chat_has_its_intent_and_block_changes_know_who()
    {
        var (s, c, _, got) = Make();
        s.OnChat("PoofImaFox", "ty maid");
        Assert.Equal("thanks", Assert.Single(Of(got, "player.say")).Data["intent"]!.ToString());
        c.World.Entities.SpawnPlayer(7, "PoofImaFox", new Vec3d(3.5, 65, 3.5), 0, 90, heldItem: Items.StonePickaxe);
        s.OnAnimation(7, 1);
        s.OnBlockChanged(new BlockPos(3, 64, 3), Blocks.Stone, 0);
        var bc = Assert.Single(Of(got, "block.changed"));
        Assert.Equal("PoofImaFox", bc.Data["by"]!.ToString());
        Assert.Equal("air", bc.Data["to"]!.ToString());
    }

    [Fact]
    public void Time_crossing_handles_the_wrap_and_ignores_jumps_back()
    {
        Assert.True(GameSignals.Crossed(12400, 12600, 12500));
        Assert.True(GameSignals.Crossed(23400, 100, 23500));
        Assert.False(GameSignals.Crossed(13000, 1000, 12500)); // /time set back
        Assert.False(GameSignals.Crossed(12600, 12700, 12500));
    }
}
