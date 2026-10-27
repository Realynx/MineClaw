using MineClaw.Agent.Rag;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>The Bot's mechanism memory: learning from a player's activation, demonstrations/answers, persistence.</summary>
public class MechanismTests
{
    private static readonly BlockPos Door = new(0, 64, 0), Button = new(-1, 65, 1);

    /// <summary>Stone floor, a wall at x=0 with an iron door at z=0 and a button beside it (outside).</summary>
    private static void Build(WorldState w)
    {
        for (int x = -8; x <= 8; x++)
        for (int z = -8; z <= 8; z++)
        {
            w.SetBlock(x, 63, z, Blocks.Stone, 0);
            if (x == 0) for (int y = 64; y <= 66; y++) w.SetBlock(x, y, z, Blocks.Stone, 0);
        }
        w.SetBlock(0, 64, 0, Blocks.IronDoor, 0);
        w.SetBlock(0, 65, 0, Blocks.IronDoor, 8);
        w.SetBlock(Button.X, Button.Y, Button.Z, Blocks.StoneButton, 2);
    }

    /// <summary>Alice presses the button: the server sends the button, then both door halves, then pops it 20 ticks later.</summary>
    private static void Press(IndexFixture f, MechanismWatcher m)
    {
        f.World.SetBlock(Button.X, Button.Y, Button.Z, Blocks.StoneButton, 2 | 8);
        f.Clock.Advance(TimeSpan.FromMilliseconds(50));
        f.World.SetBlock(0, 64, 0, Blocks.IronDoor, 4);
        f.World.SetBlock(0, 65, 0, Blocks.IronDoor, 12);
        for (int t = 0; t < 19; t++) { f.Clock.Advance(TimeSpan.FromMilliseconds(50)); m.Pump(); }
        f.World.SetBlock(Button.X, Button.Y, Button.Z, Blocks.StoneButton, 2);
        f.World.SetBlock(0, 64, 0, Blocks.IronDoor, 0);
        f.World.SetBlock(0, 65, 0, Blocks.IronDoor, 8);
        for (int t = 0; t < 10; t++) { f.Clock.Advance(TimeSpan.FromMilliseconds(50)); m.Pump(); }
    }

    [Fact]
    public void One_observed_player_activation_becomes_a_persisted_link_that_survives_a_restart()
    {
        using var f = new IndexFixture();
        Build(f.World);
        f.World.Entities.SpawnPlayer(7, "Alice", new Vec3d(-1.5, 64, 2.5), 0, 0);
        string dbPath = Path.Combine(f.Dir, "test.db");
        using (var m = new MechanismWatcher(f.World, f.Store, f.Clock, selfName: "maid_chan").Attach())
        {
            MechanismObservation? seen = null;
            m.Learned += o => seen ??= o;
            Press(f, m);
            Assert.NotNull(seen);
            Assert.True(seen!.IsNew);
            Assert.Equal("Alice", seen.By);
        }
        var link = Assert.Single(f.World.Mechanisms.All());
        Assert.Equal(TriggerKind.Button, link.Kind);
        Assert.Equal(2, link.Effects.Count);
        Assert.InRange(link.PulseTicks!.Value, 19, 21);
        var row = f.Store.Get(MechanismWatcher.Kind, "-1,65,1");
        Assert.NotNull(row);
        Assert.Contains("iron door", row!.Text);
        Assert.Contains("pulse", row.Text);

        // Restart: a new process opens the same db, a fresh world, and loads the graph back.
        using var db2 = AgentDb.Open(dbPath);
        using var rag2 = new RagIndex(db2, time: f.Clock, backgroundEmbedding: false);
        var store2 = new ObservationStore(rag2, f.Clock);
        var world2 = new WorldState();
        world2.ApplyChunkRegion(-32, 0, -32, 64, 128, 64, new byte[64 * 128 * 64], null, null, null);
        Build(world2);
        using var m2 = new MechanismWatcher(world2, store2, f.Clock);
        Assert.Equal(1, m2.Load());
        var back = Assert.Single(world2.Mechanisms.All());
        Assert.Equal(link.Trigger, back.Trigger);
        Assert.Equal(link.PulseTicks, back.PulseTicks);
        Assert.Equal(link.Effects.Select(e => (e.Pos, e.Off, e.On)), back.Effects.Select(e => (e.Pos, e.Off, e.On)));
        Assert.Equal(0, world2.Mechanisms.All().Count(l => l.Dimension != 0));

        // The pathfinder uses it right away: press the learned button, then the door within its pulse.
        var plan = Pathfinder.FindPath(world2, new Vec3d(-3.5, 64, 0.5), new GoalBlock(3, 64, 0), PathOptions.Default with
        {
            AllowOpenDoors = true, AllowPartial = false, Mechanisms = world2.Mechanisms.View(0),
        });
        Assert.True(plan.Success, plan.ToString());
        Assert.Contains(plan.Steps, s => s.Kind == MoveKind.Trigger && s.Trigger!.Pos == Button);
    }

    [Fact]
    public void A_players_watch_this_makes_the_next_activation_a_demonstration()
    {
        using var f = new IndexFixture();
        Build(f.World);
        f.World.Entities.SpawnPlayer(7, "Alice", new Vec3d(-1.5, 64, 2.5), 0, 0);
        using var m = new MechanismWatcher(f.World, f.Store, f.Clock).Attach();
        m.NoteChat("Alice", "watch, this button opens the door");
        Press(f, m);
        var link = Assert.Single(f.World.Mechanisms.All());
        Assert.True(link.Taught);
        Assert.Equal("Alice", link.By);
        Assert.True(link.Confidence >= 0.85);
        Assert.Equal(ObservationSource.Taught, f.Store.Get(MechanismWatcher.Kind, "-1,65,1")!.Source);
    }

    [Fact]
    public void Answer_to_how_do_i_open_this_teaches_the_link()
    {
        using var f = new IndexFixture();
        Build(f.World);
        using var m = new MechanismWatcher(f.World, f.Store, f.Clock).Attach();
        Assert.Null(m.NoteChat("Bob", "the button")); // nothing asked yet
        f.World.Mechanisms.Question = (Door, 0, f.Clock.GetUtcNow());
        var link = m.NoteChat("Bob", "it's the button next to it");
        Assert.NotNull(link);
        Assert.Equal(Button, link!.Trigger);
        Assert.Contains(link.Effects, e => e.Pos == Door && e.IsOpenable && Openables.IsOpen(e.On.Meta));
        Assert.Null(f.World.Mechanisms.Question);
        Assert.NotNull(f.Store.Get(MechanismWatcher.Kind, "-1,65,1"));
    }

    [Fact]
    public void An_activation_that_does_nothing_weakens_the_link_and_a_removed_trigger_forgets_it()
    {
        using var f = new IndexFixture();
        Build(f.World);
        using var m = new MechanismWatcher(f.World, f.Store, f.Clock).Attach();
        Press(f, m);
        double c0 = f.World.Mechanisms.All()[0].Confidence;
        f.World.SetBlock(Button.X, Button.Y, Button.Z, Blocks.StoneButton, 2 | 8); // pressed, door stays shut
        for (int t = 0; t < 20; t++) { f.Clock.Advance(TimeSpan.FromMilliseconds(50)); m.Pump(); }
        var weak = f.World.Mechanisms.All()[0];
        Assert.True(weak.Confidence < c0);
        Assert.Equal(1, weak.Misses);
        f.World.SetBlock(Button.X, Button.Y, Button.Z, 0, 0);
        m.Pump();
        Assert.Empty(f.World.Mechanisms.All());
        Assert.Null(f.Store.Get(MechanismWatcher.Kind, "-1,65,1"));
    }
}
