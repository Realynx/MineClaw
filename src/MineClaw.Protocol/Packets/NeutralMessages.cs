using MineClaw.Data;

namespace MineClaw.Protocol.Packets;

/// <summary>
/// Another player's dig progress (1.12.2 Block Break Animation 0x08, <c>SPacketBlockBreakAnim</c>): the digger's entity id,
/// the block and the crack stage 0..9 (−1 or ≥ 10 = stopped). Version-neutral: b1.8.1 sends nothing like it (its observers
/// infer digs from arm swings).
/// </summary>
public sealed class BlockBreakAnimationMessage : IPacket
{
    public byte Id => AnimationPacket.PacketId;
    public int EntityId { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Z { get; init; }
    public int Stage { get; init; }

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("BlockBreakAnimationMessage has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("BlockBreakAnimationMessage has no b1.8.1 wire form");
    public override string ToString() => $"BlockBreakAnimation(entity {EntityId} at {X},{Y},{Z} stage {Stage})";
}

/// <summary>
/// A dropped item entity's stack changed after its spawn (1.12.2 Entity Metadata index 6 of <c>EntityItem</c>: stacks merging,
/// a partial pickup). The spawn itself is surfaced as <see cref="PickupSpawnPacket"/>. Version-neutral: b1.8.1 item entities
/// never change their stack.
/// </summary>
public sealed class ItemEntityStackMessage : IPacket
{
    public byte Id => EntityMetadataPacket.PacketId;
    public int EntityId { get; init; }
    public ItemStack Item { get; init; } = ItemStack.Empty;

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("ItemEntityStackMessage has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("ItemEntityStackMessage has no b1.8.1 wire form");
    public override string ToString() => $"ItemEntityStack(entity {EntityId}: {Item})";
}
