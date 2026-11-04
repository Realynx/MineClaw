using System.Buffers.Binary;
using System.Text;
using MineClaw.ViewerBridge.Supervisor;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// Pins <see cref="Mcwc.Decode"/> (WP07) against the pre-refactor implementation, kept here verbatim as the reference: v1 and v2
/// payloads, every truncation, single-byte corruptions and oversized blobs give the same columns or the same FormatException text.
/// </summary>
public class McwcDecodeEquivalenceTests
{
    private static (int Version, int Dim, List<McwcColumn> Columns) Reference(ReadOnlySpan<byte> b)
    {
        int p = 0;
        ReadOnlySpan<byte> Take(ref int at, int n, ReadOnlySpan<byte> src)
        {
            if (n < 0 || at + n > src.Length) throw new FormatException("MCWC truncated");
            var s = src.Slice(at, n);
            at += n;
            return s;
        }
        if (!Take(ref p, 4, b).SequenceEqual("MCWC"u8)) throw new FormatException("not MCWC");
        int version = BinaryPrimitives.ReadUInt16LittleEndian(Take(ref p, 2, b));
        if (version is not (1 or 2)) throw new FormatException("MCWC version");
        _ = BinaryPrimitives.ReadUInt16LittleEndian(Take(ref p, 2, b));
        int dim = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
        int count = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
        if (count is < 0 or > Mcwc.MaxColumns) throw new FormatException("MCWC count");
        var cols = new List<McwcColumn>(count);
        for (int i = 0; i < count; i++)
        {
            int cx = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
            int cz = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
            long hash = BinaryPrimitives.ReadInt64LittleEndian(Take(ref p, 8, b));
            long seen = BinaryPrimitives.ReadInt64LittleEndian(Take(ref p, 8, b));
            bool live = Take(ref p, 1, b)[0] != 0;
            int byLen = Take(ref p, 1, b)[0];
            string by = Encoding.UTF8.GetString(Take(ref p, byLen, b));
            int dataLen = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
            if (dataLen > Mcwc.MaxBlob) throw new FormatException("MCWC blob too large");
            var data = Take(ref p, dataLen, b).ToArray();
            byte[]? light = null;
            if (version == 2)
            {
                int lightLen = BinaryPrimitives.ReadInt32LittleEndian(Take(ref p, 4, b));
                if (lightLen > Mcwc.MaxBlob) throw new FormatException("MCWC blob too large");
                light = Take(ref p, lightLen, b).ToArray();
            }
            cols.Add(new McwcColumn(cx, cz, hash, seen, live, by, data, light));
        }
        if (p != b.Length) throw new FormatException("MCWC trailing bytes");
        return (version, dim, cols);
    }

    private static string Outcome(Func<(int Version, int Dim, List<McwcColumn> Columns)> decode)
    {
        try
        {
            var (v, dim, cols) = decode();
            return $"v{v} d{dim} " + string.Join(";", cols.Select(c =>
                $"{c.Cx},{c.Cz},{c.Hash},{c.SeenMs},{c.Live},{c.By},{Convert.ToHexString(c.Data)},{(c.Light == null ? "-" : Convert.ToHexString(c.Light))}"));
        }
        catch (FormatException ex) { return "FormatException: " + ex.Message; }
    }

    private static void Same(byte[] bytes) => Assert.Equal(Outcome(() => Reference(bytes)), Outcome(() => Mcwc.Decode(bytes)));

    [Fact]
    public void Decode_matches_the_reference()
    {
        var cols = new List<McwcColumn>
        {
            new(3, -4, -5, 6, true, "b", [9, 9], [7]), new(0, 0, 0, 0, false, "", [], []), new(-1, 1, long.MaxValue, 99, true, "maid_chan_é", [1, 2, 3, 4], [5]),
        };
        foreach (var version in new[] { 1, 2 })
        {
            var bytes = Mcwc.Encode(version, version == 1 ? 0 : -1, version == 1 ? cols.Select(c => c with { Light = null }).ToList() : cols);
            Same(bytes);
            for (int n = 0; n <= bytes.Length + 1; n++) Same(n <= bytes.Length ? bytes[..n] : [.. bytes, 0]);
            var rng = new Random(version);
            for (int t = 0; t < 2000; t++)
            {
                var copy = (byte[])bytes.Clone();
                copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
                if (t % 3 == 0) copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
                Same(copy);
            }
        }
        // Header edge cases: wrong magic, versions 0/3, counts -1 and over the cap, a blob over the cap, an empty payload.
        Same([]);
        Same("MCWX"u8.ToArray());
        foreach (var (v, count) in new[] { (0, 0), (3, 0), (1, -1), (1, Mcwc.MaxColumns + 1), (2, 0) })
        {
            var h = new byte[16];
            "MCWC"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), (ushort)v);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), count);
            Same(h);
        }
        var big = Mcwc.Encode(2, 0, [new(0, 0, 0, 0, true, "x", [], [])]);
        BinaryPrimitives.WriteInt32LittleEndian(big.AsSpan(16 + 4 + 4 + 8 + 8 + 1 + 1 + 1), Mcwc.MaxBlob + 1);
        Same(big);
        var bigLight = Mcwc.Encode(2, 0, [new(0, 0, 0, 0, true, "x", [], [])]);
        BinaryPrimitives.WriteInt32LittleEndian(bigLight.AsSpan(16 + 4 + 4 + 8 + 8 + 1 + 1 + 1 + 4), Mcwc.MaxBlob + 1);
        Same(bigLight);
    }
}
