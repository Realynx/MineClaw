namespace MineClaw.Protocol.Versioning;

/// <summary>
/// The connection seam (docs/design/multi-version.md, "Seam 1"): a duplex stream of <b>canonical</b> game messages. The
/// canonical model is the existing <c>MineClaw.Protocol.Packets</c> classes (the b1.8.1 shapes, extended per phase with
/// version-neutral messages where b1.8.1's shape can't carry the newer data). b1.8.1's <see cref="PacketConnection"/> is
/// the identity implementation; a 1.12.2 connection translates its own wire packets to and from the same objects
/// (framing, compression, login/play states, keep-alive and teleport confirms stay inside it).
/// <para>Threading is <see cref="PacketConnection"/>'s: one reader thread calls <see cref="ReadPacket"/>; <see cref="Send"/>
/// is thread-safe.</para>
/// </summary>
public interface IGameConnection : IDisposable
{
    /// <summary>Blocks for the next canonical message; null when the peer closed cleanly at a packet boundary.</summary>
    IPacket? ReadPacket();

    /// <summary>Sends one canonical message (translated to the wire version; thread-safe).</summary>
    void Send(IPacket packet);

    /// <summary>Whether this connection can translate <paramref name="packet"/> to its wire version. False for canonical
    /// messages of features a newer version's translator doesn't cover yet (1.12.2 P1: digging, placing, windows, entity use);
    /// the client then reports "not supported on this version yet" instead of sending.</summary>
    bool CanSend(IPacket packet) => true;

    /// <summary>Raw traffic hook (the packet recorder chains onto it). For a translating connection the bytes are its own
    /// wire bytes, and recordings carry the version in their header.</summary>
    Action<PacketTraffic>? TrafficLog { get; set; }

    long PacketsReceived { get; }
    long PacketsSent { get; }
}
