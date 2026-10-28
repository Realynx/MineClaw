using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Versioning;

namespace MineClaw.Protocol.Release1122;

/// <summary>The 1.12.2 (protocol 340) adapter: a translating <see cref="Release1122Connection"/> per connect.</summary>
public sealed class Release1122ProtocolAdapter : IProtocolAdapter
{
    public static readonly Release1122ProtocolAdapter Instance = new();

    public GameVersion Version => GameVersion.Release1122;

    /// <summary>Client Settings view distance (chunks).</summary>
    public int ViewDistance { get; init; } = 8;

    /// <summary>Chat translations: the whole en_us.lang of the user's 1.12.2 client jar when one is found (read once), else
    /// the built-in keys.</summary>
    public Func<LangTable> Lang { get; init; } = () => LangTable.LoadDefault();

    public IGameConnection Connect(string host, int port, TimeSpan? connectTimeout = null)
    {
        return Release1122Connection.Connect(host, port, connectTimeout, ViewDistance, Lang());
    }

    public IGameConnection Open(Stream stream, PacketDirection inbound = PacketDirection.ClientBound, bool ownsStream = true)
    {
        if (inbound != PacketDirection.ClientBound)
            throw new NotSupportedException("the 1.12.2 connection is client-side only (the FakeServer codec is multi-version P2)");
        return new Release1122Connection(stream, ownsStream) { ViewDistance = ViewDistance, Lang = Lang() };
    }

    /// <summary>The canonical handshake carries the username; the connection turns it into Handshake + Login Start.</summary>
    public IReadOnlyList<IPacket> LoginStart(string username) => [new HandshakePacket(username)];

    public int MaxChatLength => Release1122Connection.MaxChatLength;
}
