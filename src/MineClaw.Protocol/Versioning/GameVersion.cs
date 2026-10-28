namespace MineClaw.Protocol.Versioning;

/// <summary>
/// One Minecraft version MineClaw knows by name (docs/design/multi-version.md). The id is what config and the CLI use
/// (<c>Bot:Server:Version</c>, <c>--server-version</c>); <see cref="ProtocolVersion"/> is the number its wire handshake sends.
/// <see cref="Netty"/> = the 1.7+ VarInt-framed protocol with handshake/status/login/play states.
/// </summary>
public sealed record GameVersion(string Id, int ProtocolVersion, bool Netty, int WorldHeight)
{
    /// <summary>Beta 1.8.1 (protocol 17): unframed packets with a 1-byte id, 16x128x16 columns.</summary>
    public static readonly GameVersion Beta181 = new("b1.8.1", 17, Netty: false, WorldHeight: 128);

    /// <summary>Release 1.12.2 (protocol 340): VarInt framing, zlib compression, paletted 16³ sections, 256 high.</summary>
    public static readonly GameVersion Release1122 = new("1.12.2", 340, Netty: true, WorldHeight: 256);

    /// <summary>Every version MineClaw has a name for (whether or not a profile is implemented for it yet).</summary>
    public static IReadOnlyList<GameVersion> Known { get; } = [Beta181, Release1122];

    /// <summary>The config value that means "ask the server" (the default).</summary>
    public const string Auto = "auto";

    /// <summary>Parses a config/CLI value: an id ("b1.8.1", "1.12.2"; case and a leading "v"/"beta " ignored) or a bare
    /// protocol number ("17", "340"). Null/empty/"auto" → null (auto-detect). Unknown → false.</summary>
    public static bool TryParse(string? value, out GameVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var v = value.Trim().ToLowerInvariant();
        if (v == Auto) return true;
        if (v.StartsWith("beta ")) v = "b" + v[5..].Trim();
        else if (v.StartsWith("beta")) v = "b" + v[4..];
        if (v.StartsWith('v')) v = v[1..];
        foreach (var k in Known)
            if (k.Id == v || (int.TryParse(v, out var p) && p == k.ProtocolVersion))
            {
                version = k;
                return true;
            }
        return false;
    }

    /// <summary>The known version a modern status ping's <c>version.protocol</c> names, or null.</summary>
    public static GameVersion? ForNettyProtocol(int protocol) => Known.FirstOrDefault(k => k.Netty && k.ProtocolVersion == protocol);

    public override string ToString() => $"{Id} (protocol {ProtocolVersion})";
}
