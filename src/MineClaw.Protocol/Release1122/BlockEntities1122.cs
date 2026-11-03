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
        switch (info.Kind)
        {
            case "bed": c["color"] = info.Color ?? 14; break;
            case "banner":
                c["Base"] = info.Color ?? 0;
                if (info.Patterns is { Count: > 0 } ps)
                    c["Patterns"] = new NbtList(NbtType.Compound, ps.Select(p => (object)new NbtCompound { ["Pattern"] = p.Pattern, ["Color"] = p.Color }).ToList());
                break;
            case "skull":
                c["SkullType"] = (sbyte)(info.Skull ?? 0);
                c["Rot"] = (sbyte)(info.Rotation ?? 0);
                if (info.Owner is { } owner) c["Owner"] = new NbtCompound { ["Name"] = owner };
                break;
            case "flower_pot":
                c["Item"] = info.Item is { } item ? "minecraft:" + item : "minecraft:air";
                c["Data"] = info.Data ?? 0;
                break;
            case "mob_spawner":
                if (info.Mob is { } mob) c["SpawnData"] = new NbtCompound { ["id"] = "minecraft:" + mob };
                break;
            case "note_block": c["note"] = (sbyte)(info.Note ?? 0); break;
        }
        return c;
    }

    /// <summary>The looks of a non-sign block entity; null for a sign or a tag without an id.</summary>
    public static BlockEntityInfo? Info(NbtCompound tag)
    {
        var id = Bare(tag.GetString("id")).ToLowerInvariant();
        if (id.Length == 0 || id == "sign") return null;
        switch (id)
        {
            case "bed":
                return new BlockEntityInfo("bed") { Color = tag.GetInt("color") ?? 14 };
            case "banner":
            {
                var patterns = new List<BannerPattern>();
                if (tag.GetList("Patterns") is { } list)
                    foreach (var o in list.Items)
                        if (o is NbtCompound p && p.GetString("Pattern") is { } code) patterns.Add(new BannerPattern(code, p.GetInt("Color") ?? 0));
                return new BlockEntityInfo("banner") { Color = tag.GetInt("Base") ?? 0, Patterns = patterns.Count > 0 ? patterns : null };
            }
            case "skull":
                return new BlockEntityInfo("skull")
                {
                    Skull = tag.GetInt("SkullType") ?? 0, Rotation = tag.GetInt("Rot") ?? 0, Owner = tag.GetCompound("Owner")?.GetString("Name"),
                };
            case "flower_pot":
            {
                var item = Bare(tag.GetString("Item"));
                return new BlockEntityInfo("flower_pot") { Item = item.Length == 0 || item == "air" ? null : item, Data = item.Length == 0 ? null : tag.GetInt("Data") };
            }
            case "mob_spawner":
            {
                var mob = Bare(tag.GetCompound("SpawnData")?.GetString("id"));
                return new BlockEntityInfo("mob_spawner") { Mob = mob.Length == 0 ? null : mob };
            }
            case "jukebox":
                return new BlockEntityInfo("jukebox") { Record = tag.GetCompound("RecordItem")?.GetString("id") is { } rec ? Bare(rec) : null };
            case "noteblock" or "note_block" or "music":
                return new BlockEntityInfo("note_block") { Note = tag.GetInt("note") };
            default:
                return new BlockEntityInfo(id);
        }
    }
}
