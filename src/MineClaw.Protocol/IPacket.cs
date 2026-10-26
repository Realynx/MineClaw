namespace MineClaw.Protocol;

/// <summary>
/// A b1.8.1 (protocol 17) packet. Beta packets have no length prefix: the leading id byte is followed directly
/// by the body, so every field must be read with exactly the right width. <see cref="Read"/> and
/// <see cref="Write"/> handle the body only; <see cref="PacketRegistry"/> handles the id byte.
/// </summary>
public interface IPacket
{
    /// <summary>Wire id (first byte on the wire).</summary>
    byte Id { get; }

    /// <summary>Reads the packet body (everything after the id byte).</summary>
    void Read(MinecraftReader reader, PacketDirection direction);

    /// <summary>Writes the packet body (everything after the id byte).</summary>
    void Write(MinecraftWriter writer, PacketDirection direction);
}

public static class PacketExtensions
{
    /// <summary>Writes the body as a client would send it (<see cref="PacketDirection.ServerBound"/>).</summary>
    public static void Write(this IPacket packet, MinecraftWriter writer) => packet.Write(writer, PacketDirection.ServerBound);

    /// <summary>Serializes id + body to a byte array.</summary>
    public static byte[] ToBytes(this IPacket packet, PacketDirection direction = PacketDirection.ServerBound)
    {
        using var ms = new MemoryStream();
        var w = new MinecraftWriter(ms);
        PacketRegistry.Write(w, packet, direction);
        return ms.ToArray();
    }
}
