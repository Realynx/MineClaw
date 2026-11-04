using MineClaw.Data;

namespace MineClaw.Protocol.Release1122;

/// <summary>
/// 1.12.2 block entity NBT → <see cref="BlockEntityInfo"/> (the viewer's looks; decompiled <c>TileEntity*.writeToNBT</c> /
/// <c>getUpdateTag</c>): beds (<c>color</c>), banners (<c>Base</c>, <c>Patterns[{Pattern, Color}]</c>), skulls (<c>SkullType</c>,
/// <c>Rot</c>, <c>Owner.Name</c>), flower pots (<c>Item</c>, <c>Data</c>), spawners (<c>SpawnData.id</c>), jukeboxes
/// (<c>RecordItem.id</c>), note blocks (<c>note</c>), and any other block entity by its id. Signs are <see cref="Codec1122.SignLines"/>.
/// </summary>
public static class BlockEntities1122
{
    /// <summary>The block entity's position (its x, y, z ints), or null.</summary>
    public static (int X, int Y, int Z)? PositionOf(NbtCompound tag) =>
        tag.GetInt("x") is { } x && tag.GetInt("y") is { } y && tag.GetInt("z") is { } z ? (x, y, z) : null;

    private static string Bare(string? id) => (id ?? "").StartsWith("minecraft:", StringComparison.Ordinal) ? id![10..] : id ?? "";

    /// <summary>Update Block Entity's action per kind (<c>NetHandlerPlayClient.handleUpdateTileEntity</c>: 1 spawner, 4 skull,
    /// 5 flower pot, 6 banner, 11 bed; anything else 0).</summary>
    public static int ActionOf(string kind) => kind switch
    {
        "mob_spawner" => 1, "skull" => 4, "flower_pot" => 5, "banner" => 6, "bed" => 11, _ => 0,
    };

    /// <summary>The inverse (the fake server's connection): the NBT the vanilla server writes for these looks.</summary>
    public static NbtCompound Tag(int x, int y, int z, BlockEntityInfo info)
    {
        var c = new NbtCompound { ["id"] = "minecraft:" + (info.Kind == "note_block" ? "noteblock" : info.Kind), ["x"] = x, ["y"] = y, ["z"] = z };
        if (TagWriters.TryGetValue(info.Kind, out var write)) write(c, info);
        return c;
    }

    // The looks each kind's tag carries (TileEntity*.writeToNBT); other kinds carry their id and position only.
    private static readonly Dictionary<string, Action<NbtCompound, BlockEntityInfo>> TagWriters = new()
    {
        ["bed"] = static (c, info) => c["color"] = info.Color ?? 14,
        ["banner"] = WriteBanner,
        ["skull"] = WriteSkull,
        ["flower_pot"] = static (c, info) =>
        {
            c["Item"] = info.Item is { } item ? "minecraft:" + item : "minecraft:air";
            c["Data"] = info.Data ?? 0;
        },
        ["mob_spawner"] = static (c, info) =>
        {
            if (info.Mob is { } mob) c["SpawnData"] = new NbtCompound { ["id"] = "minecraft:" + mob };
        },
        ["note_block"] = static (c, info) => c["note"] = (sbyte)(info.Note ?? 0),
    };

    private static void WriteBanner(NbtCompound c, BlockEntityInfo info)
    {
        c["Base"] = info.Color ?? 0;
        if (info.Patterns is { Count: > 0 } ps)
            c["Patterns"] = new NbtList(NbtType.Compound, ps.Select(p => (object)new NbtCompound { ["Pattern"] = p.Pattern, ["Color"] = p.Color }).ToList());
    }

    private static void WriteSkull(NbtCompound c, BlockEntityInfo info)
    {
        c["SkullType"] = (sbyte)(info.Skull ?? 0);
        c["Rot"] = (sbyte)(info.Rotation ?? 0);
        if (info.Owner is { } owner) c["Owner"] = new NbtCompound { ["Name"] = owner };
    }

    /// <summary>The looks of a non-sign block entity; null for a sign or a tag without an id.</summary>
    public static BlockEntityInfo? Info(NbtCompound tag)
    {
        var id = Bare(tag.GetString("id")).ToLowerInvariant();
        if (id.Length == 0 || id == "sign") return null;
        return InfoReaders.TryGetValue(id, out var read) ? read(tag) : new BlockEntityInfo(id);
    }

    // The looks read per id (any other id: just its kind).
    private static readonly Dictionary<string, Func<NbtCompound, BlockEntityInfo>> InfoReaders = new()
    {
        ["bed"] = static tag => new BlockEntityInfo("bed") { Color = tag.GetInt("color") ?? 14 },
        ["banner"] = BannerInfo,
        ["skull"] = static tag => new BlockEntityInfo("skull")
        {
            Skull = tag.GetInt("SkullType") ?? 0, Rotation = tag.GetInt("Rot") ?? 0, Owner = tag.GetCompound("Owner")?.GetString("Name"),
        },
        ["flower_pot"] = FlowerPotInfo,
        ["mob_spawner"] = static tag =>
        {
            var mob = Bare(tag.GetCompound("SpawnData")?.GetString("id"));
            return new BlockEntityInfo("mob_spawner") { Mob = mob.Length == 0 ? null : mob };
        },
        ["jukebox"] = static tag => new BlockEntityInfo("jukebox") { Record = tag.GetCompound("RecordItem")?.GetString("id") is { } rec ? Bare(rec) : null },
        ["noteblock"] = NoteBlockInfo,
        ["note_block"] = NoteBlockInfo,
        ["music"] = NoteBlockInfo,
    };

    private static BlockEntityInfo BannerInfo(NbtCompound tag)
    {
        var patterns = new List<BannerPattern>();
        if (tag.GetList("Patterns") is { } list)
            foreach (var o in list.Items)
                if (o is NbtCompound p && p.GetString("Pattern") is { } code) patterns.Add(new BannerPattern(code, p.GetInt("Color") ?? 0));
        return new BlockEntityInfo("banner") { Color = tag.GetInt("Base") ?? 0, Patterns = patterns.Count > 0 ? patterns : null };
    }

    private static BlockEntityInfo FlowerPotInfo(NbtCompound tag)
    {
        var item = Bare(tag.GetString("Item"));
        return new BlockEntityInfo("flower_pot") { Item = item.Length == 0 || item == "air" ? null : item, Data = item.Length == 0 ? null : tag.GetInt("Data") };
    }

    private static BlockEntityInfo NoteBlockInfo(NbtCompound tag) => new("note_block") { Note = tag.GetInt("note") };
}
