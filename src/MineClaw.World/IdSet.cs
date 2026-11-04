namespace MineClaw.World;

/// <summary>
/// A set of block (or item) ids as a flag table: the lookups that were long <c>id is 1 or 2 or (&gt;= 8 and &lt;= 11) …</c> patterns.
/// <see cref="Contains"/> is one bounds check and one array read (no allocation), so it is safe on the physics and pathing hot
/// paths. Ids outside the table (negative, or above the highest listed) are not in the set.
/// </summary>
internal sealed class IdSet
{
    private readonly bool[] _in;

    private IdSet(bool[] flags) => _in = flags;

    /// <summary>The set of <paramref name="ids"/> plus every id in each inclusive <paramref name="ranges"/> pair.</summary>
    public static IdSet Of(int[] ids, params (int First, int Last)[] ranges)
    {
        int max = -1;
        foreach (int id in ids) max = Math.Max(max, id);
        foreach (var (_, last) in ranges) max = Math.Max(max, last);
        var flags = new bool[max + 1];
        foreach (int id in ids) flags[id] = true;
        foreach (var (first, last) in ranges)
            for (int id = first; id <= last; id++) flags[id] = true;
        return new IdSet(flags);
    }

    public bool Contains(int id) => (uint)id < (uint)_in.Length && _in[id];
}
