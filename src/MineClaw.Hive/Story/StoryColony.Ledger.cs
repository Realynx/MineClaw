using System.Text.RegularExpressions;

namespace MineClaw.Hive.Story;

/// <summary>
/// The storage ledger corrected from what a pawn saw (docs/web/story-mode.md "Tools are colony stock"). Live 11-04: "no wooden pickaxe
/// in the chest at 218,64,400" and "no stone hoe in the chest at 223,64,400 (it had: dirt x333, egg x24, …)" over and over: the
/// colony's record said the item was there, the pawn opened the chest and it wasn't (someone took it), and the next pick planned the
/// same fetch again. A fetch that finds its item missing now fixes the record at once (the item is gone from that chest; an empty
/// chest is empty; a full listing replaces the record), the job isn't counted as failed, and the next pick plans from real stock: another
/// chest, a pawn with one to spare, or a wait with why and an auto bill. The same fetch is never planned again until the record shows
/// the item there again (a deposit).
/// </summary>
public sealed partial class StoryColony
{
    // The client's take refusals (MinecraftClient.Windows EmptyTakeReason, MinecraftClient.Gear WithdrawMatchingAsync):
    // "no {item} in the chest at x,y,z: it is empty" | "no {item} in the chest at x,y,z (it had: a x3, b x2[, +N more])" | "no {item} in the chest at x,y,z".
    private static readonly Regex MissingTake = new(
        @"no (?<item>[^()]+?)(?: \([^)]*\))? in the (?:chest|trapped_chest|container) at (?<x>-?\d+),(?<y>-?\d+),(?<z>-?\d+)(?<empty>: it is empty)?(?: \(it had: (?<had>[^)]*)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A failed job whose note says a fetched item wasn't in its chest: the chest's record is corrected from what the pawn saw. True =
    /// it was that (callers then don't count it as a failure: the next pick plans from real stock).
    /// </summary>
    internal bool StaleFetch(PawnState p, ActiveJob j, string? note)
    {
        if (note == null || World is not { } w || MissingTake.Match(note) is not { Success: true } m) return false;
        var at = new P3(int.Parse(m.Groups["x"].Value), int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["z"].Value));
        if (StorageAt(at) is not { } st) return false;
        var name = m.Groups["item"].Value.Trim();
        var item = w.Data.ItemByNameWithDamage(name);
        bool changed = false;
        if (m.Groups["empty"].Success) { changed = st.Contents.Count > 0; st.Contents.Clear(); st.Enchanted.Clear(); }
        else if (item is { } it)
        {
            changed = st.Contents.RemoveAll(c => c.Id == it.Item.Id) > 0;
            st.Enchanted.RemoveAll(e => e.Id == it.Item.Id);
            // A full listing (no "+N more") is what the chest holds: the record becomes it.
            if (m.Groups["had"].Success && !m.Groups["had"].Value.Contains("more", StringComparison.Ordinal) && Listing(w, m.Groups["had"].Value) is { } seen)
            {
                changed |= !seen.SequenceEqual(st.Contents);
                st.Contents = seen;
            }
        }
        else return false;
        if (changed) Recount(st); else st.Rev = Touch();
        _log_StaleFetch(p, name, at);
        return true;
    }

    /// <summary>"dirt x333, egg x24" as stacks (null when a name doesn't resolve: the record then keeps the rest as it was).</summary>
    private static List<Stack>? Listing(IStoryWorld w, string had)
    {
        var list = new List<Stack>();
        foreach (var part in had.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int x = part.LastIndexOf(" x", StringComparison.Ordinal);
            if (x < 0 || !int.TryParse(part[(x + 2)..], out var n)) return null;
            if (w.Data.ItemByNameWithDamage(part[..x].Trim()) is not { } it) return null;
            list.Add(new Stack(it.Item.Id, Math.Max(0, it.Damage), n));
        }
        return list;
    }

    private readonly Dictionary<string, DateTimeOffset> _staleSaid = new(StringComparer.Ordinal);

    private void _log_StaleFetch(PawnState p, string item, P3 at)
    {
        var key = item + "@" + at;
        if (_staleSaid.TryGetValue(key, out var last) && Now - last < TimeSpan.FromMinutes(10)) return;
        _staleSaid[key] = Now;
        Alert("info", $"{p.Display} found no {item} in the chest at {at}: the colony's record is corrected", p.Name);
    }
}
