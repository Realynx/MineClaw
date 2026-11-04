using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol.Release1122;

/// <summary>
/// Decodes 1.12.2 Chunk Data (0x20) into a <see cref="ChunkSectionsMessage"/>, ported from the decompiled 1.12.2 client
/// (<c>SPacketChunkData.readPacketData</c>, <c>Chunk.read</c>, <c>BlockStateContainer.read</c>, the three palettes,
/// <c>BitArray</c>, <c>NibbleArray</c>):
/// <list type="bullet">
/// <item>Packet: int x, int z, bool full, VarInt mask, VarInt size + data, VarInt count + block-entity NBT compounds.</item>
/// <item>Data, per section in the mask (bottom up): u8 bits per block; a palette (bits ≤ 4 → 4 bits and a linear palette;
/// 5..8 → a hash-map palette; both are VarInt count + VarInt states; more → the global registry palette, which still sends a
/// VarInt 0, and the client uses log2(state count) = 13 bits whatever was sent); VarInt long count + longs (entries are
/// packed from the low bit and <b>do</b> span two longs); 2048 bytes block light; 2048 bytes sky light if the dimension has
/// sky (the overworld). Then 256 biome bytes when full.</item>
/// <item>Section cell index = y &lt;&lt; 8 | z &lt;&lt; 4 | x; nibble arrays use the same index, low nibble first.</item>
/// <item>A block state is id &lt;&lt; 4 | meta (Block.BLOCK_STATE_IDS). 1.12.2's block ids end at 255, so they fit the world's
/// byte ids; a state outside that (a modded server) reads as stone, the safe guess for collision.</item>
/// </list>
/// </summary>
public static class ChunkData
{
    public const int SectionCells = 4096, SectionLightBytes = 2048, Sections = 16, Height = Sections * 16;
    /// <summary>Bits per entry of the global palette: log2DeBruijn(Block.BLOCK_STATE_IDS.size()) in vanilla 1.12.2.</summary>
    public const int GlobalBits = 13;
    /// <summary>What an out-of-range state decodes to.</summary>
    public const byte UnknownBlock = 1;

    /// <summary>Decodes a Chunk Data body (after the packet id).</summary>
    /// <param name="hasSky">The current dimension has sky light (WorldProvider.hasSkyLight: overworld yes, nether/end no).</param>
    public static ChunkSectionsMessage Decode(ReadOnlySpan<byte> body, bool hasSky)
    {
        var r = new WireReader(body);
        int cx = r.ReadInt(), cz = r.ReadInt();
        bool full = r.ReadBool();
        int mask = r.ReadVarInt();
        int size = r.ReadVarInt();
        if (size < 0 || size > Framing.MaxPayload) throw new ProtocolException($"chunk data size {size} out of range");
        var data = r.ReadBytes(size);
        int count = r.ReadVarInt();
        if (count < 0 || count > r.Remaining) throw new ProtocolException($"block entity count {count} out of range");
        var entities = new List<NbtCompound>(count);
        for (int i = 0; i < count; i++)
            if (Nbt.ReadRoot(ref r) is { } tag) entities.Add(tag);
        if (r.Remaining != 0) throw new ProtocolException($"chunk data: {r.Remaining} bytes after the block entities");
        var regions = DecodeSections(data, cx, cz, full, mask, hasSky, out var biomes);
        return new ChunkSectionsMessage
        {
            ChunkX = cx, ChunkZ = cz, FullChunk = full, SectionMask = mask, Height = Height, HasSkyLight = hasSky,
            Regions = regions, Biomes = biomes, BlockEntities = entities,
        };
    }

    /// <summary>Decodes the section data of one column into world-coordinate regions (see <see cref="ChunkSectionsMessage"/>).</summary>
    public static List<ChunkRegion> DecodeSections(ReadOnlySpan<byte> data, int cx, int cz, bool full, int mask, bool hasSky, out byte[]? biomes)
    {
        if ((mask & ~0xFFFF) != 0) throw new ProtocolException($"chunk section mask 0x{mask:X} has bits above section 15");
        var r = new WireReader(data);
        var regions = new List<ChunkRegion>();
        ChunkRegion? column = null;
        if (full)
        {
            column = new ChunkRegion(cx * 16, 0, cz * 16, 16, Height, 16);
            if (hasSky) Array.Fill(column.SkyLight, (byte)15); // absent sections: open air (Chunk.getLightFor on an empty storage)
            regions.Add(column);
        }
        var states = new int[SectionCells];
        for (int s = 0; s < Sections; s++)
            if ((mask & 1 << s) != 0) ReadSection(ref r, states, column, cx, cz, s, hasSky, regions);
        biomes = full ? r.ReadBytes(256).ToArray() : null;
        if (r.Remaining != 0) throw new ProtocolException($"chunk section data: {r.Remaining} bytes left over");
        return regions;
    }

    /// <summary>One sent section into the full column, or into a 16³ region of its own (a partial chunk).</summary>
    private static void ReadSection(ref WireReader r, int[] states, ChunkRegion? column, int cx, int cz, int s, bool hasSky, List<ChunkRegion> regions)
    {
        ReadStates(ref r, states);
        var light = r.ReadBytes(SectionLightBytes);
        var sky = hasSky ? r.ReadBytes(SectionLightBytes) : default;
        var region = column ?? new ChunkRegion(cx * 16, s * 16, cz * 16, 16, 16, 16);
        if (column is null) regions.Add(region);
        CopySection(region, column is null ? 0 : s * 16, states, light, hasSky, sky);
    }

    /// <summary>One section's states and light into <paramref name="region"/> from row <paramref name="y0"/> (state id &gt; 255:
    /// <see cref="UnknownBlock"/>).</summary>
    private static void CopySection(ChunkRegion region, int y0, int[] states, ReadOnlySpan<byte> light, bool hasSky, ReadOnlySpan<byte> sky)
    {
        for (int i = 0; i < SectionCells; i++)
        {
            int x = i & 15, z = i >> 4 & 15, y = i >> 8;
            int at = region.Index(x, y0 + y, z);
            int state = states[i];
            int id = state >> 4;
            if ((uint)id > 255) { region.BlockIds[at] = UnknownBlock; region.Metadata[at] = 0; }
            else { region.BlockIds[at] = (byte)id; region.Metadata[at] = (byte)(state & 15); }
            region.BlockLight[at] = Nibble(light, i);
            if (hasSky) region.SkyLight[at] = Nibble(sky, i);
        }
    }

    /// <summary>Cell <paramref name="i"/>'s nibble of a section light array (even cells in the low half).</summary>
    private static byte Nibble(ReadOnlySpan<byte> arr, int i)
    {
        int n = arr[i >> 1];
        return (byte)((i & 1) == 0 ? n & 15 : n >> 4 & 15);
    }

    /// <summary>A section's palette: ≤ 4 bits → 4, ≤ 8 → its own list; more → the global palette (null; bits become 13).</summary>
    private static int[]? ReadPalette(ref WireReader r, ref int bits)
    {
        if (bits > 8)
        {
            r.ReadVarInt(); // BlockStatePaletteRegistry.read: a VarInt (0) and nothing else
            bits = GlobalBits;
            return null;
        }
        if (bits <= 4) bits = 4;
        int n = r.ReadVarInt();
        if (n < 0 || n > 1 << bits) throw new ProtocolException($"section palette of {n} entries for {bits} bits");
        var palette = new int[n];
        for (int i = 0; i < n; i++) palette[i] = r.ReadVarInt();
        return palette;
    }

    /// <summary>BlockStateContainer.read: one section's 4096 states.</summary>
    public static void ReadStates(ref WireReader r, int[] states)
    {
        int bits = r.ReadUByte();
        var palette = ReadPalette(ref r, ref bits);
        int longs = r.ReadVarInt();
        int expect = (SectionCells * bits + 63) / 64;
        if (longs != expect) throw new ProtocolException($"section data has {longs} longs, {bits} bits per block needs {expect}");
        var arr = new long[longs];
        for (int i = 0; i < longs; i++) arr[i] = r.ReadLong();
        long maxValue = (1L << bits) - 1;
        for (int i = 0; i < SectionCells; i++)
        {
            // BitArray.getAt
            int bit = i * bits;
            int lo = bit / 64, hi = ((i + 1) * bits - 1) / 64, off = bit % 64;
            long v = lo == hi
                ? (long)((ulong)arr[lo] >> off) & maxValue
                : ((long)((ulong)arr[lo] >> off) | arr[hi] << (64 - off)) & maxValue;
            int value = (int)v;
            // BlockStatePaletteLinear/HashMap.getBlockState: an index past the palette → null → air (BlockStateContainer.get)
            states[i] = palette is null ? value : value < palette.Length ? palette[value] : 0;
        }
    }

    // ------------------------------------------------------------------ encoding (fixtures and the fake server)

    /// <summary>BlockStateContainer.write + BitArray.setAt for one section: <paramref name="bits"/> ≤ 4 → 4 with a palette,
    /// 5..8 → palette, else the 13-bit global palette (palette length VarInt 0).</summary>
    public static void WriteStates(WireWriter w, ReadOnlySpan<int> states, int bits)
    {
        if (bits <= 4) bits = 4;
        var palette = PaletteOf(states, ref bits);
        w.WriteUByte((byte)bits);
        if (palette is null) w.WriteVarInt(0);
        else
        {
            w.WriteVarInt(palette.Count);
            foreach (var p in palette) w.WriteVarInt(p);
        }
        var arr = PackStates(states, palette, bits);
        w.WriteVarInt(arr.Length);
        foreach (var l in arr) w.WriteLong(l);
    }

    /// <summary>The states in first-seen order (≤ 8 bits), or null for the global palette (bits become 13).</summary>
    private static List<int>? PaletteOf(ReadOnlySpan<int> states, ref int bits)
    {
        if (bits > 8)
        {
            bits = GlobalBits;
            return null;
        }
        List<int> palette = [];
        foreach (var s in states) if (!palette.Contains(s)) palette.Add(s);
        if (palette.Count > 1 << bits) throw new ArgumentException($"{palette.Count} states don't fit {bits} bits");
        return palette;
    }

    /// <summary>BitArray.setAt for every cell: palette indices (or raw states) packed <paramref name="bits"/> wide.</summary>
    private static long[] PackStates(ReadOnlySpan<int> states, List<int>? palette, int bits)
    {
        var arr = new long[(SectionCells * bits + 63) / 64];
        long max = (1L << bits) - 1;
        for (int i = 0; i < SectionCells; i++)
        {
            long value = palette is null ? states[i] : palette.IndexOf(states[i]);
            int bit = i * bits, lo = bit / 64, hi = ((i + 1) * bits - 1) / 64, off = bit % 64;
            arr[lo] = arr[lo] & ~(max << off) | (value & max) << off;
            if (lo != hi)
            {
                int k = 64 - off, rest = bits - k;
                arr[hi] = (long)((ulong)arr[hi] >> rest << rest) | (value & max) >> k;
            }
        }
        return arr;
    }

    /// <summary>A full Chunk Data body for fixtures: <paramref name="sections"/>[s] = 4096 states (index y·256 + z·16 + x) or
    /// null for an empty section; uniform light; biomes when full.</summary>
    public static byte[] EncodeBody(int cx, int cz, bool full, int?[]? bitsPerSection, int[]?[] sections, bool hasSky,
        byte blockLight = 0, byte skyLight = 15, byte biome = 1, IReadOnlyList<NbtCompound>? blockEntities = null)
    {
        var data = new WireWriter(8192);
        int mask = 0;
        for (int s = 0; s < sections.Length; s++)
        {
            if (sections[s] is not { } st) continue;
            mask |= 1 << s;
            WriteStates(data, st, bitsPerSection?[s] ?? 4);
            var bl = new byte[SectionLightBytes]; Array.Fill(bl, (byte)(blockLight | blockLight << 4));
            data.WriteBytes(bl);
            if (hasSky) { var sl = new byte[SectionLightBytes]; Array.Fill(sl, (byte)(skyLight | skyLight << 4)); data.WriteBytes(sl); }
        }
        if (full) { var b = new byte[256]; Array.Fill(b, biome); data.WriteBytes(b); }
        var w = new WireWriter(data.Length + 32);
        w.WriteInt(cx).WriteInt(cz).WriteBool(full).WriteVarInt(mask).WriteVarInt(data.Length).WriteBytes(data.Span);
        w.WriteVarInt(blockEntities?.Count ?? 0);
        if (blockEntities is not null) foreach (var e in blockEntities) Nbt.WriteRoot(w, e);
        return w.ToArray();
    }
}
