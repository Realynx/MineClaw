using MineClaw.Data;

namespace MineClaw.World.Tests;

/// <summary>The viewer's display-only facts kept by the world model: the entity event ring and the block entity store.</summary>
public class ViewerFactsTests
{
    [Fact]
    public void Entity_events_keep_five_seconds_and_map_animations_and_statuses()
    {
        var ring = new EntityEventRing();
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        ring.Add(new EntityEvent(1, "swing", now.AddSeconds(-6)));
        ring.Add(new EntityEvent(2, "hurt", now.AddSeconds(-1)));
        Assert.Equal(new[] { 2 }, ring.Recent(now).Select(e => e.EntityId));
        for (int i = 0; i < EntityEventRing.Capacity + 10; i++) ring.Add(new EntityEvent(i, "swing", now));
        Assert.Equal(EntityEventRing.Capacity, ring.Recent(now).Count); // bounded

        Assert.Equal(("swing", "swing_off", "hurt", "crit", "magic_crit"), (EntityEventRing.AnimationKind(1, 0), EntityEventRing.AnimationKind(1, 1),
            EntityEventRing.AnimationKind(2, 0), EntityEventRing.AnimationKind(6, 0), EntityEventRing.AnimationKind(7, 0)));
        Assert.Null(EntityEventRing.AnimationKind(3, 0)); // waking up
        Assert.Equal(("hurt", "death", "eat", "teleport"), (EntityEventRing.StatusKind(2), EntityEventRing.StatusKind(3), EntityEventRing.StatusKind(9), EntityEventRing.StatusKind(46)));
        Assert.Null(EntityEventRing.StatusKind(7));
    }

    [Fact]
    public void Block_entities_change_once_and_leave_with_their_column()
    {
        var w = new WorldState();
        var ids = new byte[16 * 128 * 16];
        w.ApplyChunkRegion(0, 0, 0, 16, 128, 16, ids, null, null, null);
        var changes = new List<BlockPos>();
        w.BlockEntities.Changed += changes.Add;
        var p = new BlockPos(3, 70, 4);
        w.BlockEntities.Set(p, BlockEntityInfo.Sign(["a", "b", "", ""]));
        w.BlockEntities.Set(p, BlockEntityInfo.Sign(["a", "b", "", ""])); // the same text again: no change
        w.BlockEntities.Set(new BlockPos(5, 70, 4), new BlockEntityInfo("banner") { Color = 1, Patterns = [new BannerPattern("cr", 2)] });
        w.BlockEntities.Set(new BlockPos(5, 70, 4), new BlockEntityInfo("banner") { Color = 1, Patterns = [new BannerPattern("cr", 2)] });
        Assert.Equal(2, changes.Count);
        Assert.Equal(2, w.BlockEntities.InColumn(0, 0).Count);
        w.BlockEntities.Set(p, null);
        Assert.Equal(3, changes.Count);
        w.UnloadColumn(0, 0);
        Assert.Empty(w.BlockEntities.InColumn(0, 0));
    }

    [Fact]
    public void Spawn_data_is_kept_for_objects_and_paintings()
    {
        var t = new EntityTracker();
        Assert.Equal(13 | 1 << 12, t.SpawnObject(1, 70, new Vec3d(0, 70, 0), data: 13 | 1 << 12).Data);
        Assert.Equal(2, t.SpawnPainting(2, "Kebab", new Vec3d(0.5, 65.5, 0.5), direction: 2).Data);
        t.NoteEvent(1, "hurt");
        Assert.Equal("hurt", Assert.Single(t.Events.Recent(DateTime.UtcNow)).Kind);
    }
}
