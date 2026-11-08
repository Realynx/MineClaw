using System.Text.RegularExpressions;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private static readonly Regex NotContainer = new(@"\bat (?<x>-?\d+),(?<y>-?\d+),(?<z>-?\d+) is not a container\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A removed chest is neither a source of supplies nor a destination with 27 empty slots.
    // Keep its filter and priority so replacing the chest restores the owner's storage settings.
    private bool ReconcileStorage(StorageState st, IStoryWorld w, P3? missing = null)
    {
        bool Present(P3 at) => at != missing && w.Block(at.X, at.Y, at.Z).Id is 54 or 146;
        var at = st.Chest;
        if (at != missing && !w.Loaded(at.X, at.Z)) return false;
        bool present = Present(at);
        if (!present && st.DoubleWith is { } other)
        {
            if (w.Loaded(other.X, other.Z) && Present(other)) { at = other; present = true; }
        }
        // Do not shrink a double chest merely because its other column is unloaded.
        if (present && st.DoubleWith is { } knownHalf && !w.Loaded(knownHalf.X, knownHalf.Z)) return false;
        P3? half = present ? DoubleHalf(at, w)
            : st.DoubleWith is { } unseen && !w.Loaded(unseen.X, unseen.Z) ? unseen : null;
        if (half == missing) half = null;
        if (st.Missing == !present && st.Chest == at && st.DoubleWith == half) return false;
        if (st.Chest != at) Removed("storage", st.Chest.ToString());
        st.Chest = at;
        st.DoubleWith = half;
        st.Missing = !present;
        // A changed double chest has an unknown inventory. A new look must replace its old combined contents.
        st.Contents.Clear();
        st.Enchanted.Clear();
        st.SeenAt = null;
        _seenAsked.Remove(at);
        _looked.Remove(at);
        Recount(st);
        WakeIdle();
        return true;
    }

    internal bool MissingContainer(PawnState pawn, string? note)
    {
        if (note == null || World is not { } world) return false;
        bool handled = false;
        foreach (Match match in NotContainer.Matches(note))
        {
            var at = new P3(int.Parse(match.Groups["x"].Value), int.Parse(match.Groups["y"].Value), int.Parse(match.Groups["z"].Value));
            if (StorageAt(at) is not { } st) continue;
            ReconcileStorage(st, Host.ViewOf(pawn.Name) ?? world, at);
            handled = true;
        }
        return handled;
    }
}
