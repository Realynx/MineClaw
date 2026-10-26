namespace MineClaw.Protocol;

/// <summary>Which way a packet travels. MineClaw is a client: it reads <see cref="ClientBound"/> and writes <see cref="ServerBound"/>.</summary>
public enum PacketDirection
{
    /// <summary>Server → client (S→C).</summary>
    ClientBound,

    /// <summary>Client → server (C→S).</summary>
    ServerBound,
}

public static class PacketDirectionExtensions
{
    public static PacketDirection Opposite(this PacketDirection d) =>
        d == PacketDirection.ClientBound ? PacketDirection.ServerBound : PacketDirection.ClientBound;

    public static string Arrow(this PacketDirection d) => d == PacketDirection.ClientBound ? "S->C" : "C->S";
}
