namespace MineClaw.World;

/// <summary>
/// One chop_tree run by a hive bot: the tree's trunk base, soil and wood type, the log cells it felled and the ones it left
/// standing when it stopped (interrupted, cancelled, out of reach).
/// </summary>
public sealed record ChopRecord(BlockPos Base, BlockPos Soil, int Dimension, int Wood, IReadOnlySet<BlockPos> Felled,
    IReadOnlySet<BlockPos> Left, string Bot, DateTimeOffset At)
{
    public bool Unfinished => Left.Count > 0;
}

/// <summary>
/// Recent tree chops by this bot and the hive bodies that share its world (in-process bodies get main's log). A log column
/// touching cells a recent chop felled, or left standing by a chop that stopped, is that bot's own unfinished tree: it may be
/// finished even with no leaves or dirt under it (live 10-28 01:52, task #55: a birch stopped at 3/5 logs left "an unverified
/// log column at 9,68,0" that the worker refused to touch and asked Fox to punch).
/// </summary>
public sealed class ChopLog
{
    /// <summary>How long a chop's cells mark its remnant as ours.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(2);
    public const int Capacity = 256;

    private readonly object _lock = new();
    private readonly List<ChopRecord> _records = new();

    public int Count { get { lock (_lock) return _records.Count; } }

    public void Note(ChopRecord r)
    {
        if (r.Felled.Count == 0 && r.Left.Count == 0) return;
        lock (_lock)
        {
            _records.Add(r);
            if (_records.Count > Capacity) _records.RemoveRange(0, _records.Count - Capacity);
        }
    }

    /// <summary>
    /// The recent chop <paramref name="log"/> belongs to: a log it left standing, or one touching (26-neighbourhood) or standing
    /// up to 3 above a cell it felled. Newest first; null = not ours.
    /// </summary>
    public ChopRecord? Owner(BlockPos log, int dimension, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            for (int i = _records.Count - 1; i >= 0; i--)
            {
                var r = _records[i];
                if (r.Dimension != dimension || t - r.At > Window) continue;
                if (r.Left.Contains(log)) return r;
                foreach (var f in r.Felled)
                    if (Touches(f, log)) return r;
            }
        }
        return null;
    }

    /// <summary>The log is in the 26-neighbourhood of the felled cell, or up to 3 straight above it.</summary>
    private static bool Touches(BlockPos felled, BlockPos log)
    {
        int dx = Math.Abs(felled.X - log.X), dz = Math.Abs(felled.Z - log.Z), dy = log.Y - felled.Y;
        if (dx <= 1 && dz <= 1 && dy is >= -1 and <= 1) return true;
        return dx == 0 && dz == 0 && dy is > 1 and <= 3;
    }
}
