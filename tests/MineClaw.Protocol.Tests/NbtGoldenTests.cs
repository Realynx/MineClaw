using System.Text;
using MineClaw.Protocol.Release1122;

namespace MineClaw.Protocol.Tests;

/// <summary>NBT golden (complexity refactor WP04): every tag type written, read back and dumped, plus the malformed inputs' errors.</summary>
public class NbtGoldenTests
{
    [Fact]
    public void Nbt_matches_the_golden() => Golden.Check("nbt", "C2BC73BAE91B998594AC73A878B28C866AB95FA243B24603CB9376B2912F4165", Dump);

    /// <summary>b1.8.1 map chunk packing (the inflated payload, so the runtime's zlib doesn't matter) over odd origins, partial
    /// heights and regions spanning columns, plus its argument checks.</summary>
    [Fact]
    public void Chunk_encode_matches_the_golden() => Golden.Check("chunk-encode", "A1B5E3B18756B5AE56F315C9286CE1471610D3D65812526DE86E721EF70A3F8F", sb =>
    {
        (int X, int Y, int Z, int SX, int SY, int SZ)[] boxes =
        [
            (0, 0, 0, 16, 128, 16), (16, 0, -32, 16, 128, 16), (3, 5, 7, 4, 9, 3), (-5, 1, -20, 20, 2, 30), (30, 127, 30, 1, 1, 1),
            (0, 64, 0, 16, 64, 16), (-1, 0, -1, 2, 128, 2), (0, -1, 0, 1, 2, 1), (0, 120, 0, 1, 9, 1), (0, 0, 0, 0, 1, 1), (0, 0, 0, 257, 1, 1),
        ];
        foreach (var (x, y, z, sx, sy, sz) in boxes)
        {
            sb.Append($"{x},{y},{z} {sx}x{sy}x{sz}: ");
            try
            {
                var reg = new ChunkRegion(x, y, z, sx, sy, sz);
                for (int i = 0; i < reg.Volume; i++)
                {
                    reg.BlockIds[i] = (byte)(i * 7);
                    reg.Metadata[i] = (byte)(i * 3);
                    reg.BlockLight[i] = (byte)(i * 5 + 1);
                    reg.SkyLight[i] = (byte)(i * 11 + 2);
                }
                var p = ChunkDataDecoder.Encode(reg);
                var raw = ChunkDataDecoder.Inflate(p.CompressedData, sx * sy * sz * 5 / 2);
                sb.Append($"{p.X},{p.Y},{p.Z} {p.SizeX}x{p.SizeY}x{p.SizeZ} ").Append(WireDump.Hex(raw));
                var back = ChunkDataDecoder.Decode(p);
                sb.Append(" back ").Append(WireDump.Hex(back.BlockIds)).Append(' ').Append(WireDump.Hex(back.Metadata)).Append(' ')
                    .Append(WireDump.Hex(back.BlockLight)).Append(' ').Append(WireDump.Hex(back.SkyLight));
            }
            catch (Exception ex) { sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message); }
            sb.Append('\n');
        }
    });

    private static NbtCompound Everything() => new()
    {
        ["b"] = (sbyte)-5, ["s"] = (short)-300, ["i"] = 123456, ["l"] = long.MinValue, ["f"] = 1.5f, ["d"] = -0.1,
        ["ba"] = new byte[] { 0, 1, 255 }, ["str"] = "héllo\0𝄞", ["empty"] = "",
        ["list"] = new NbtList(NbtType.Int, [1, 2, 3]),
        ["none"] = new NbtList(NbtType.End, []),
        ["lists"] = new NbtList(NbtType.List, [new NbtList(NbtType.String, ["a"]), new NbtList(NbtType.Compound, [new NbtCompound { ["x"] = 1 }])]),
        ["c"] = new NbtCompound { ["inner"] = new NbtCompound { ["deep"] = (short)7 } },
        ["ia"] = new[] { int.MinValue, 0, int.MaxValue }, ["la"] = new[] { long.MaxValue, -1L },
    };

    private static void Dump(StringBuilder sb)
    {
        var all = Everything();
        var bytes = Nbt.ToBytes(all);
        sb.Append(Convert.ToHexString(bytes)).Append('\n');
        var back = Nbt.ReadRoot(bytes, out int used);
        sb.Append(used).Append(' ').Append(WireDump.Of(back)).Append('\n');
        foreach (var v in back!.Values) sb.Append(v.GetType().Name).Append(v is NbtList nl ? "<" + nl.ElementType + ">" : "").Append(' ');
        sb.Append('\n');
        sb.Append(Convert.ToHexString(Nbt.ToBytes(null))).Append('\n');
        foreach (var v in all.Values) sb.Append(Nbt.TypeOf(v)).Append('\n');
        foreach (object bad in new object[] { 1u, (byte)1, 'c', 1m, new object() }) sb.Append(Try(() => Nbt.TypeOf(bad).ToString())).Append('\n');
        string[] malformed =
        [
            "",                       // nothing
            "00",                     // TAG_End root
            "0300000000002A",         // int root
            "0A0000" + "0D000178" + "00", // unknown tag type 13
            "0A0000" + "09000178" + "03FFFFFFFF" + "00", // negative list length
            "0A0000" + "09000178" + "0000000001" + "00", // list of TAG_End with elements
            "0A0000" + "07000178" + "7FFFFFFF" + "00",   // byte array longer than the packet
            "0A0000" + "0B000178" + "00000002" + "00000001" + "00", // int array short
            "0A0000" + "0C000178" + "00000001" + "0000000000000001" + "00", // long array
            "0A0000" + "08000478C080" + "00",            // string key with bad length
            "0A0000" + "080001780002C080" + "00",        // modified UTF-8 NUL
            "0A0000" + "080001780003EDA0BD" + "00",      // a lone surrogate's bytes
            "0A0000" + "08000178000180" + "00",          // bad UTF-8 lead
            "0A0000" + "0A000178",                       // unterminated
            Deep(513), Deep(512),
        ];
        NbtCompound[] tags =
        [
            new(), new() { ["ench"] = new NbtList(NbtType.End, []) },
            new() { ["ench"] = new NbtList(NbtType.Compound, [new NbtCompound { ["id"] = (short)1 }, new NbtCompound { ["lvl"] = 2 }, "x"]) },
            new() { ["StoredEnchantments"] = new NbtList(NbtType.Compound, []) },
            new() { ["StoredEnchantments"] = new NbtList(NbtType.Compound, [new NbtCompound { ["id"] = 9, ["lvl"] = (short)4 }]) },
            new() { ["display"] = new NbtCompound { ["Name"] = "N" } }, new() { ["display"] = new NbtCompound { ["color"] = -1 } },
            new() { ["display"] = "not a compound" },
            new() { ["Potion"] = "minecraft:water" }, new() { ["Potion"] = "empty" }, new() { ["Potion"] = "mundane" }, new() { ["Potion"] = "thick" },
            new() { ["Potion"] = "awkward" }, new() { ["Potion"] = "swiftness" }, new() { ["Potion"] = "minecraft:awkward", ["CustomPotionEffects"] = new NbtList(NbtType.Compound, []) },
            new() { ["CustomPotionEffects"] = new NbtList(NbtType.Compound, [new NbtCompound()]) }, new() { ["RepairCost"] = (sbyte)3 },
        ];
        foreach (var tag in tags) sb.Append(WireDump.Of(Codec1122.TagOf(tag))).Append('\n');
        var meta = Wire1122Script.AllSerializers(new WireWriter()).ToArray();
        var item = new WireWriter().WriteItemMetadata(new Data.ItemStack(5, 2, 1)).ToArray();
        foreach (var (blob, want) in new[] { (meta, 17), (meta, 6), (meta, 99), (item, 6), (item, 7), (meta[..^3], 6), (new byte[] { 4, 14 }, 6) })
            sb.Append(want).Append(" → ").Append(Try(() =>
            {
                int pos = 0;
                var s = Codec1122.ReadMetadataSlot(blob, ref pos, want);
                return pos + " " + WireDump.Of(s);
            })).Append('\n');
        foreach (var hex in malformed)
            sb.Append(hex.Length > 40 ? hex[..40] + "…" : hex).Append(" → ").Append(Try(() =>
            {
                var c = Nbt.ReadRoot(Convert.FromHexString(hex), out int n);
                return n + " " + WireDump.Of(c);
            })).Append('\n');
    }

    private static string Deep(int depth)
    {
        var sb = new StringBuilder("0A0000");
        for (int i = 0; i < depth; i++) sb.Append("0A000178");
        for (int i = 0; i <= depth; i++) sb.Append("00");
        return sb.ToString();
    }

    private static string Try(Func<string> f)
    {
        try { return f(); }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }
}
