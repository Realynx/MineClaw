namespace MineClaw.Protocol;

/// <summary>
/// Protocol constants and the connection sequence for Minecraft Beta 1.8 / 1.8.1 (protocol 17), verified against the
/// b1.8 server source (NetLoginHandler / NetServerHandler).
/// <para><b>Offline-mode login</b> (TCP, default port 25565; all packets have no length prefix):</para>
/// <list type="number">
/// <item>C→S 0x02 Handshake: the username (vanilla max 16 chars).</item>
/// <item>S→C 0x02 Handshake: connection hash. "-" means offline mode (no authentication). Any other value is an
///   online-mode server hash (client would have to call the session server's joinserver; MineClaw does not).</item>
/// <item>C→S 0x01 Login Request: protocol 17, username, and zeros for every other field
///   (MapSeed 0, ServerMode 0, Dimension 0, Difficulty 0, WorldHeight 0, MaxPlayers 0) — the server ignores them.
///   Use <see cref="Packets.LoginRequestPacket.ForClient"/>.</item>
/// <item>S→C either 0xFF Kick ("Outdated server!", "Outdated client!", "The server is full!", ban message,
///   "Took too long to log in" after 600 ticks / 30 s) or 0x01 Login Request carrying the player's entity id,
///   empty name, map seed, server mode (0 survival / 1 creative), dimension, difficulty, world height (128) and
///   max players.</item>
/// <item>The server then sends, in order: 0x06 Spawn Position; 0x04 Time Update (+ 0x46 reason 1 if raining);
///   0x03 "&lt;name&gt; joined the game." to everyone; 0xC9 Player List Item for every online player;
///   0x0D Player Position &amp; Look (S→C order: X, Stance, Y, Z); 0x04 Time Update; 0x29 effects; inventory
///   0x68 / 0x67. Chunks (0x32 + 0x33) and entities arrive around these as the player manager ticks.</item>
/// <item>The client must answer the server's 0x0D by echoing it C→S (X, feet Y, Stance, Z unchanged). Until it does,
///   the server ignores all of the client's movement packets.</item>
/// </list>
/// <para><b>Steady state:</b> the server sends 0x00 Keep Alive (random int) every 20 ticks; echo the same id. The
/// server disconnects ("disconnect.timeout") after 1200 ticks (60 s) without receiving any packet, and its socket
/// read times out after 30 s; the vanilla client sends a movement packet (0x0A–0x0D) every tick, which also keeps the
/// connection alive. Chat C→S is limited to 100 characters.</para>
/// </summary>
public static class ProtocolInfo
{
    /// <summary>Protocol version sent in 0x01 (Beta 1.8 and 1.8.1).</summary>
    public const int ProtocolVersion = 17;

    public const int DefaultPort = 25565;

    /// <summary>Feet-to-eyes offset used for the 0x0B/0x0D stance field.</summary>
    public const double PlayerEyeHeight = 1.62;

    /// <summary>The 0x02 hash an offline-mode server sends.</summary>
    public const string OfflineConnectionHash = "-";
}
