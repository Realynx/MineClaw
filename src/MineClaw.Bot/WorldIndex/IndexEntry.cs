using MineClaw.Agent.Rag;
using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

/// <summary>One thing the indexer tracks (mirrors an observation row). Owned by the indexer's worker thread.</summary>
internal sealed class IndexEntry(string kind, string key)
{
    public string Kind { get; } = kind;
    public string Key { get; } = key;
    public string Id => Kind + "|" + Key;

    public BlockPos Anchor;
    public BlockPos Min, Max;
    /// <summary>Block cells belonging to the thing (station halves, cluster cells).</summary>
    public HashSet<BlockPos> Cells = new();
    /// <summary>Blocks the bot must never break because of this thing.</summary>
    public HashSet<BlockPos> Protect = new();

    /// <summary>Farm sub-type, island rank etc.; consumed by the renderer.</summary>
    public string? SubType;
    public string? Label;
    public BlockPos? LabelSign;

    /// <summary>Last container snapshot (null = never opened).</summary>
    public List<ItemCount>? Contents;
    public bool ContentsDirty;
    /// <summary>When <see cref="Contents"/> were read from an open window.</summary>
    public DateTimeOffset? ContentsAt;
    /// <summary>Who read <see cref="Contents"/> when they came from another row (a merged half's db memory); null = this bot.</summary>
    public string? ContentsBy;
    /// <summary>A double chest's contents per half (window slots 0-26 = the west/north cell, 27-53 the other), from this
    /// session's last 54-slot look: when one half is broken the survivor keeps its own items, the broken half's spill.</summary>
    public Dictionary<BlockPos, List<ItemCount>>? HalfContents;
    public bool FurnaceLit;
    public (ItemCount? In, ItemCount? Fuel, ItemCount? Out) FurnaceSlots;

    /// <summary>Kind-specific values rendered into fields JSON (generator dig/stand cells, player names...).</summary>
    public Dictionary<string, object?> Extra = new(StringComparer.Ordinal);

    public bool Dirty = true;
    public bool Seen = true;
    public bool Removed;
    public bool Force;
    public TimeSpan MinWriteInterval;

    public string? WrittenText, WrittenFields;
    public (BlockPos, BlockPos)? WrittenBounds;
    public DateTimeOffset WrittenAt;

    public Box Bounds => Box.Of(Min.X, Min.Y, Min.Z, Max.X, Max.Y, Max.Z);

    public void SetBounds(IEnumerable<BlockPos> cells)
    {
        bool first = true;
        foreach (var c in cells)
        {
            if (first) { Min = Max = c; first = false; continue; }
            Min = new BlockPos(Math.Min(Min.X, c.X), Math.Min(Min.Y, c.Y), Math.Min(Min.Z, c.Z));
            Max = new BlockPos(Math.Max(Max.X, c.X), Math.Max(Max.Y, c.Y), Math.Max(Max.Z, c.Z));
        }
    }

    public double DistanceTo(BlockPos p)
    {
        double dx = Math.Max(Math.Max(Min.X - p.X, 0), p.X - Max.X);
        double dy = Math.Max(Math.Max(Min.Y - p.Y, 0), p.Y - Max.Y);
        double dz = Math.Max(Math.Max(Min.Z - p.Z, 0), p.Z - Max.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public bool Contains(BlockPos p, int pad = 0) =>
        p.X >= Min.X - pad && p.X <= Max.X + pad && p.Y >= Min.Y - pad && p.Y <= Max.Y + pad && p.Z >= Min.Z - pad && p.Z <= Max.Z + pad;
}

/// <summary>An unknown structure the brain may want to ask players about.</summary>
public sealed record UnknownStructure(string Key, BlockPos Min, BlockPos Max, string Description)
{
    /// <summary>Most common blocks (name → count), the content signature the scenery ledger matches on.</summary>
    public IReadOnlyDictionary<string, int>? Blocks { get; init; }
}

/// <summary>Counters for the host/log ("indexer: 312 columns, 0.21 ms/column").</summary>
public sealed class IndexerStats
{
    public long Passes, ColumnsScanned, BlockChanges, Upserts, Removes, Transactions, DimensionSwitches, PortalLinks, ClusterErrors;
    public double ScanMs;
    public double MsPerColumn => ColumnsScanned == 0 ? 0 : ScanMs / ColumnsScanned;
    public override string ToString() =>
        $"passes {Passes} columns {ColumnsScanned} ({MsPerColumn:0.###} ms/col) changes {BlockChanges} upserts {Upserts} removes {Removes} tx {Transactions}";
}
