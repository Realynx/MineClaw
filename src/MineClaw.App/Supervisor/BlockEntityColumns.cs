using System.Text.Json;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>One block entity for the viewer (<c>/api/world/blockentities</c>): position, kind and its looks (null facts left out).
/// A 1.12.2 sign whose text has styles carries <paramref name="Rich"/> too: four lines of <see cref="SignRun"/>s (the plain
/// <paramref name="Lines"/> stay for older readers).</summary>
public sealed record WorldBlockEntity(int X, int Y, int Z, string Kind, IReadOnlyList<string>? Lines = null, int? Color = null,
    IReadOnlyList<BannerPattern>? Patterns = null, int? Skull = null, int? Rotation = null, string? Owner = null, string? Item = null, int? Data = null,
    string? Mob = null, string? Record = null, int? Note = null, IReadOnlyList<IReadOnlyList<SignRun>>? Rich = null);

/// <summary>A column's block entities and their hash (changes with any of them: the viewer refetches only changed columns).</summary>
public sealed record WorldBlockEntityColumn(int Cx, int Cz, long Hash, IReadOnlyList<WorldBlockEntity> Entities);

/// <summary>
/// Builds a column's block entities from every live world holding it: what the connections described (<see cref="WorldState.BlockEntities"/>:
/// sign text on both versions, 1.12.2's beds, banners, skulls, pots, spawners) over the block-entity blocks actually in the column
/// (b1.8.1 sends no data for chests, spawners, jukeboxes, note blocks, beds: their kind comes from the block). A described entry
/// whose block is gone is left out; a chest's kind follows its block (1.12.2 names trapped chests "chest"). Pure.
/// </summary>
public static class BlockEntityColumns
{
    /// <summary>Block id → the block entity kind it carries.</summary>
    public static string? KindOf(int blockId) => blockId switch
    {
        63 or 68 => "sign", 26 => "bed", 176 or 177 => "banner", 144 => "skull", 140 => "flower_pot", 54 => "chest", 146 => "trapped_chest",
        130 => "ender_chest", 52 => "mob_spawner", 84 => "jukebox", 25 => "note_block", _ => null,
    };

    public static WorldBlockEntityColumn Build(int cx, int cz, IEnumerable<WorldState> worlds)
    {
        var described = new Dictionary<BlockPos, BlockEntityInfo>();
        ChunkColumn? live = null;
        foreach (var w in worlds)
        {
            live ??= w.GetColumn(cx, cz) is { HasData: true } c ? c : null;
            foreach (var (pos, info) in w.BlockEntities.InColumn(cx, cz)) described.TryAdd(pos, info);
        }
        var list = new List<WorldBlockEntity>();
        if (live != null)
        {
            for (int lx = 0; lx < 16; lx++)
            for (int lz = 0; lz < 16; lz++)
            for (int y = 0; y < live.Height; y++)
            {
                int id = live.GetBlock(lx, y, lz);
                if (KindOf(id) is not { } kind) continue;
                var pos = new BlockPos(cx * 16 + lx, y, cz * 16 + lz);
                var info = described.TryGetValue(pos, out var d) && SameFamily(d.Kind, kind) ? d with { Kind = kind } : new BlockEntityInfo(kind);
                if (kind == "jukebox" && info.Record is null && live.GetMeta(lx, y, lz) != 0) info = info with { Data = 1 }; // has a record
                list.Add(Wire(pos, info));
            }
        }
        else list.AddRange(described.OrderBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.Z).Select(kv => Wire(kv.Key, kv.Value)));
        return new WorldBlockEntityColumn(cx, cz, Hash(list), list);
    }

    private static bool SameFamily(string described, string block) =>
        described == block || (described is "chest" && block is "trapped_chest") || (described is "note_block" or "noteblock" && block == "note_block");

    private static WorldBlockEntity Wire(BlockPos p, BlockEntityInfo i) =>
        new(p.X, p.Y, p.Z, i.Kind, i.Lines, i.Color, i.Patterns, i.Skull, i.Rotation, i.Owner, i.Item, i.Data, i.Mob, i.Record, i.Note,
            i.Kind == "sign" ? SignText.Rich(i.Formatted) : null);

    /// <summary>FNV-1a over the entries' JSON: any change of a kind, a line, a colour… changes it.</summary>
    public static long Hash(IReadOnlyList<WorldBlockEntity> list)
    {
        if (list.Count == 0) return 0;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(list, SupervisorJson.Options);
        ulong h = 14695981039346656037UL;
        foreach (var b in bytes) { h ^= b; h *= 1099511628211UL; }
        return unchecked((long)h);
    }
}
