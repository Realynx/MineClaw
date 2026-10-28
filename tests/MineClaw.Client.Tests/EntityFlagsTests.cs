using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Entity flags (watcher index 0, mc-dev Entity.java: 0x01 on fire, 0x02 sneaking, 0x04 riding, 0x08 sprinting, 0x10 eating)
/// reach every tracked entity from the spawn metadata (0x18) and from 0x28 updates, so the views can draw fire and sneaks.
/// Dropped items (0x15) stay tracked until 0x1D destroys them; 0x16 collect alone doesn't remove one (the server destroys it next).
/// </summary>
public class EntityFlagsTests
{
    private static byte[] Hex(string hex)
    {
        var clean = new string(hex.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return Enumerable.Range(0, clean.Length / 2).Select(i => Convert.ToByte(clean.Substring(2 * i, 2), 16)).ToArray();
    }

    private static IPacket Read(string hex)
    {
        var ms = new MemoryStream(Hex(hex));
        return PacketRegistry.Read(new MinecraftReader(ms), PacketDirection.ClientBound);
    }

    [Fact]
    public async Task Burning_and_sneaking_flags_parse_from_a_recorded_metadata_payload()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        s.Send(new MobSpawnPacket { EntityId = 500, Type = (byte)MobTypes.Zombie, X = FixedPoint.ToAbsolute(9.5), Y = FixedPoint.ToAbsolute(64), Z = FixedPoint.ToAbsolute(8.5), Metadata = new EntityMetadata() });
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(500) is not null));
        Assert.Equal(0, c.World.Entities.Get(500)!.Flags);
        // 0x28, entity 500, index 0 (type byte) = 0x01 on fire, terminator: what a zombie in daylight gets.
        s.Send(Read("28 00 00 01 F4 00 01 7F"));
        Assert.True(await Harness.WaitAsync(() => (c.World.Entities.Get(500)?.Flags & 1) == 1));
        Assert.True(c.World.Entities.Get(500)!.IsBurning);
        // Fire out, now sneaking (0x02).
        s.Send(Read("28 00 00 01 F4 00 02 7F"));
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(500)?.IsCrouched == true));
        Assert.False(c.World.Entities.Get(500)!.IsBurning);
    }

    [Fact]
    public async Task Dropped_items_are_tracked_until_destroyed()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        s.Send(new PickupSpawnPacket { EntityId = 77, ItemId = 6, Count = 2, Damage = 0, X = FixedPoint.ToAbsolute(4.5), Y = FixedPoint.ToAbsolute(64.1), Z = FixedPoint.ToAbsolute(9.5) });
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(77) is { Kind: EntityKind.Item }));
        var e = c.World.Entities.Get(77)!;
        Assert.Equal((6, 2), ((int)e.Item.Id, (int)e.Item.Count));
        s.Send(new EntityRelativeMovePacket { EntityId = 77, DX = 32, DY = 0, DZ = 0 });
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(77)?.Position.X > 5));
        s.Send(new CollectItemPacket { CollectedEntityId = 77, CollectorEntityId = 12345 });
        await Task.Delay(50);
        Assert.NotNull(c.World.Entities.Get(77));
        s.Send(new DestroyEntityPacket { EntityId = 77 });
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Get(77) is null));
    }
}
