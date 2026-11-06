namespace MineClaw.Hive.Slots;

/// <summary>
/// The two real limits on how many bodies can be in game, as Story Mode shows them (there is no count limit of its own): the server's
/// seats (<paramref name="Free"/> = max players − online − logging in; <paramref name="Kept"/> = one per offline regular, named in
/// <paramref name="KeptFor"/>; <paramref name="Slots"/> = the server's max players) and the memory ceiling's room
/// (<paramref name="MemoryRoom"/> more bodies fit; null = no ceiling; <paramref name="MemoryWhy"/> when none fits).
/// </summary>
public sealed record SeatFacts(int Free, int Kept, int? Slots = null, IReadOnlyList<string>? KeptFor = null, int? MemoryRoom = null,
    string? MemoryWhy = null)
{
    /// <summary>Seats a new body may take now: never the ones kept for offline regulars (<see cref="SlotPolicy.CanSpawn"/>).</summary>
    public int SeatRoom => Math.Max(0, Free - Kept);

    /// <summary>How many more bodies come in now: the smaller of the seats and the memory room.</summary>
    public int Room => Math.Min(SeatRoom, MemoryRoom is { } m ? Math.Max(0, m) : int.MaxValue);

    /// <summary>
    /// Why <paramref name="waiting"/> bodies can't all come now, naming the limit that binds: "server full (20 slots, 2 kept for
    /// poofimafox and kiwi)", "memory: room for 3 more" (or the ceiling's own words when none fits). Null when they all fit (they
    /// are on their way).
    /// </summary>
    public string? WhyWaiting(int waiting)
    {
        if (waiting <= 0 || Room >= waiting) return null;
        return MemoryRoom is { } m && m < SeatRoom ? MemoryText(m) : ServerText();
    }

    /// <summary>"memory: room for 9 more" (null without a ceiling).</summary>
    public string? MemoryLine() => MemoryRoom is { } m ? MemoryText(m) : null;

    private string MemoryText(int m) => m <= 0 && MemoryWhy is { Length: > 0 } why ? why : $"memory: room for {Math.Max(0, m)} more";

    /// <summary>"server full (20 slots, 2 kept for poofimafox and kiwi)": once the ones that fit are in, the rest find it full.</summary>
    private string ServerText()
    {
        string kept = Kept <= 0 ? "" : $", {Kept} kept for {Names()}";
        return Slots is { } s ? $"server full ({s} slots{kept})" : $"server full (player cap: {Math.Max(0, Free)} free{kept})";
    }

    /// <summary>"poofimafox and kiwi", "a, b and c"; "regulars" when the names aren't known.</summary>
    private string Names()
    {
        var n = KeptFor is { Count: > 0 } k ? k : null;
        if (n == null || n.Count != Kept) return "regulars";
        return n.Count == 1 ? n[0] : string.Join(", ", n.Take(n.Count - 1)) + " and " + n[^1];
    }
}
