namespace MineClaw.Hive.Story;

/// <summary>
/// Crouch-walking in growing zones (docs/web/story-mode.md "Farming"; Fox 11-04: "The pawns need to crouch-walk through growing zones,
/// so they don't crush plants or tilled ground", "If the ground in a grow zone isn't planted or tilled, they don't have to crouch-walk
/// — better for speed"). Every pawn body gets the colony's farm zones as a filter (<see cref="IStoryHost.SetGrowZone"/>, refreshed each
/// director tick with the speed); the planner reads the live blocks inside them, so only tilled or planted cells are sneaked.
/// </summary>
public sealed partial class StoryColony
{
    private Func<int, int, int, bool>? _growFilter;
    private string _growKey = "";

    /// <summary>The farm zones' cells as an immutable predicate (foot cell x,y,z; the painted levels ± 2), rebuilt when a zone changes;
    /// null with none. Called under <c>_lock</c>.</summary>
    internal Func<int, int, int, bool>? GrowZoneFilter()
    {
        var farms = Zones.Where(z => z.Kind == "farm").ToList();
        string key = string.Join(";", farms.Select(z => $"{z.Id}:{z.Area}:{z.Cells.Count}"));
        if (key == _growKey) return _growFilter;
        _growKey = key;
        var snap = farms.Select(z => (Y1: z.Area.Y1 - 2, Y2: z.Area.Y2 + 2, Cells: z.Cells.Clone())).ToArray();
        _growFilter = snap.Length == 0 ? null : (x, y, z) =>
        {
            foreach (var f in snap)
                if (y >= f.Y1 && y <= f.Y2 && f.Cells.Contains(x, z)) return true;
            return false;
        };
        return _growFilter;
    }
}
