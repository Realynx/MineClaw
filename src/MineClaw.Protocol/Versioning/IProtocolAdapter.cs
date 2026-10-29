using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol.Versioning;

/// <summary>
/// Opens connections that speak one server version and hides its connection/login specifics
/// (docs/design/multi-version.md, "Seam 1"). Registered once per process in the game-version profile; the client never
/// names a wire version itself.
/// </summary>
public interface IProtocolAdapter
{
    GameVersion Version { get; }

    /// <summary>Opens a TCP connection to the server (client side).</summary>
    IGameConnection Connect(string host, int port, TimeSpan? connectTimeout = null);

    /// <summary>Wraps an already open duplex stream (tests, proxies). <paramref name="inbound"/> = the direction read.</summary>
    IGameConnection Open(Stream stream, PacketDirection inbound = PacketDirection.ClientBound, bool ownsStream = true);

    /// <summary>The canonical messages the client sends right after connecting to start the login. b1.8.1: the 0x02
    /// handshake with the username (the server's 0x02 reply and the 0x01 login follow in dispatch). 1.12.2: nothing the
    /// client has to send itself: the connection runs handshake → login start → set compression → login success and
    /// surfaces Join Game as the canonical 0x01 login.</summary>
    IReadOnlyList<IPacket> LoginStart(string username);

    /// <summary>Longest chat line the server accepts from a client (b1.8.1: 100; 1.12.2: 256).</summary>
    int MaxChatLength { get; }

    /// <summary>This adapter for one client whose connections ask the server for a view distance of <paramref name="chunks"/>
    /// (1.12.2 Client Settings). The adapter of a profile is process-wide, so this never changes it: it answers a per-client
    /// copy, or itself when the version has no such setting (b1.8.1: the server decides alone).</summary>
    IProtocolAdapter WithViewDistance(int chunks) => this;
}

/// <summary>The b1.8.1 (protocol 17) adapter: the existing <see cref="PacketConnection"/>, unchanged.</summary>
public sealed class Beta181ProtocolAdapter : IProtocolAdapter
{
    public static readonly Beta181ProtocolAdapter Instance = new();

    public GameVersion Version => GameVersion.Beta181;

    public IGameConnection Connect(string host, int port, TimeSpan? connectTimeout = null) =>
        PacketConnection.Connect(host, port, connectTimeout);

    public IGameConnection Open(Stream stream, PacketDirection inbound = PacketDirection.ClientBound, bool ownsStream = true) =>
        new PacketConnection(stream, inbound, ownsStream);

    public IReadOnlyList<IPacket> LoginStart(string username) => [new HandshakePacket(username)];

    public int MaxChatLength => 100;
}
