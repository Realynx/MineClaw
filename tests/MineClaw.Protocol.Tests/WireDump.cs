using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace MineClaw.Protocol.Tests;

/// <summary>
/// Digest goldens for the complexity refactor (WP04): a test writes a text dump of everything the codecs do over a corpus, and the
/// SHA-256 of that dump must match the digest taken from the implementation before it was reshaped. On a mismatch the dump is
/// written to the temp folder (<c>mineclaw-golden-&lt;name&gt;.txt</c>) so it can be diffed against a dump of the old code.
/// </summary>
internal static class Golden
{
    public static void Check(string name, string expected, Action<StringBuilder> dump)
    {
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var sb = new StringBuilder();
            dump(sb);
            var text = sb.ToString();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            if (hash == expected) return;
            var path = Path.Combine(Path.GetTempPath(), $"mineclaw-golden-{name}.txt");
            File.WriteAllText(path, text);
            Assert.Fail($"{name}: digest {hash} != {expected} (dump: {path})");
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }
}

/// <summary>A canonical message (or anything inside one) as text by reflection: public properties and fields sorted by name,
/// doubles round-trip, byte arrays as hex (long ones as their length and SHA-256), collections element by element.</summary>
internal static class WireDump
{
    public static string Hex(ReadOnlySpan<byte> data) =>
        data.Length <= 512 ? Convert.ToHexString(data) : $"len={data.Length} sha={Convert.ToHexString(SHA256.HashData(data))[..32]}";

    public static string Of(object? o)
    {
        var sb = new StringBuilder();
        Append(sb, o, 0);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, object? o, int depth)
    {
        switch (o)
        {
            case null: sb.Append("null"); return;
            case string s: AppendString(sb, s); return;
            case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
            case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case Enum e: sb.Append(e.GetType().Name).Append('.').Append(e); return;
            case Guid g: sb.Append(g.ToString("D")); return;
            case byte[] bytes: sb.Append("0x").Append(Hex(bytes)); return;
            case IFormattable when o.GetType().IsPrimitive || o is decimal: sb.Append(((IFormattable)o).ToString(null, CultureInfo.InvariantCulture)); return;
        }
        if (depth > 8) { sb.Append("…"); return; }
        var type = o.GetType();
        if (o is IEnumerable list)
        {
            sb.Append(type.Name).Append('[');
            bool first = true;
            foreach (var item in list)
            {
                if (!first) sb.Append(", ");
                first = false;
                Append(sb, item, depth + 1);
            }
            sb.Append(']');
            return;
        }
        sb.Append(type.Name).Append('{');
        bool firstMember = true;
        foreach (var m in Members(type))
        {
            // Storage shape is an implementation detail: this golden pins wire bytes and decoded cell values.
            // Compact storage and its allocation budget have dedicated ChunkAllocationTests.
            if (o is MineClaw.Protocol.Packets.ChunkSectionsMessage && m.Name == "SectionsOnly") continue;
            if (!firstMember) sb.Append(' ');
            firstMember = false;
            sb.Append(m.Name).Append('=');
            object? v;
            try { v = m is PropertyInfo p ? p.GetValue(o) : ((FieldInfo)m).GetValue(o); }
            catch (TargetInvocationException ex) { sb.Append('!').Append(ex.InnerException?.GetType().Name); continue; }
            if (o is MineClaw.Protocol.Packets.ChunkSectionsMessage { SectionsOnly: true, FullChunk: true } chunk && m.Name == "Regions")
                v = ExpandedRegions(chunk);
            Append(sb, v, depth + 1);
        }
        sb.Append('}');
    }

    private static List<ChunkRegion> ExpandedRegions(MineClaw.Protocol.Packets.ChunkSectionsMessage chunk)
    {
        var full = new ChunkRegion(chunk.ChunkX * 16, 0, chunk.ChunkZ * 16, 16, chunk.Height, 16);
        if (chunk.HasSkyLight) Array.Fill(full.SkyLight, (byte)15);
        foreach (var region in chunk.Regions)
            for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++)
            {
                int from = region.Index(x, 0, z), to = full.Index(x, region.Y, z);
                region.BlockIds.AsSpan(from, region.SizeY).CopyTo(full.BlockIds.AsSpan(to));
                region.Metadata.AsSpan(from, region.SizeY).CopyTo(full.Metadata.AsSpan(to));
                region.BlockLight.AsSpan(from, region.SizeY).CopyTo(full.BlockLight.AsSpan(to));
                region.SkyLight.AsSpan(from, region.SizeY).CopyTo(full.SkyLight.AsSpan(to));
            }
        return [full];
    }

    /// <summary>A quoted string; surrogates (a lone one can't be UTF-8 encoded) as \uXXXX.</summary>
    private static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            if (char.IsSurrogate(c)) sb.Append("\\u").Append(((int)c).ToString("X4"));
            else if (c == '\\' || c == '"') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else sb.Append(c);
        }
        sb.Append('"');
    }

    private static IEnumerable<MemberInfo> Members(Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract" && !p.PropertyType.IsByRefLike);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        return props.Cast<MemberInfo>().Concat(fields).OrderBy(m => m.Name, StringComparer.Ordinal);
    }
}

/// <summary>One end of an in-memory duplex: reads what the other end wrote and returns 0 (a clean end) when nothing is waiting,
/// so a single thread can drive both ends and "read until null" drains everything that was sent.</summary>
internal sealed class QueueEnd(Queue<byte> inbox, Queue<byte> outbox) : Stream
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = 0;
        while (n < count && inbox.Count > 0) buffer[offset + n++] = inbox.Dequeue();
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++) outbox.Enqueue(buffer[offset + i]);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
