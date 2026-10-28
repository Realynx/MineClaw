namespace MineClaw.World;

/// <summary>
/// "Can I walk back down to the island?" — the one question behind never stranding a body (live 10-28 16:15: main finished the
/// drop tower on its platform at y77 with the climb's scaffold gone, a restart woke it there at night, and seven zombies killed
/// it: "there was no path from up there back to the main island"). Home is a level, not a cell: any foot cell at or below the
/// home's feet level + 1 that the real moves (no parkour, drops ≤ 3, doors) reach counts, so a body on the island is home at
/// once and one up on a build is home only if it can walk (or drop safely) down.
/// </summary>
public static class WayHome
{
    /// <summary>The walk used for the check: the follower's own moves, no gap jumps, never a drop past 3 blocks.</summary>
    public static PathOptions Walk { get; } = new()
    {
        AllowOpenDoors = true, AllowParkour = false, MaxDrop = 3, AllowPartial = false, MaxNodes = 6000, Timeout = TimeSpan.FromSeconds(2),
    };

    /// <summary>The body at <paramref name="feet"/> is at home level already (no walk needed).</summary>
    public static bool AtHome(BlockPos feet, BlockPos home) => feet.Y <= home.Y + 1;

    /// <summary>
    /// The walk down from <paramref name="feet"/> to home level, or null when there is none: its end cell (the feet cell itself
    /// when already there).
    /// </summary>
    public static BlockPos? WalkDown(IBlockAccess w, BlockPos feet, BlockPos home, PathOptions? options = null)
    {
        if (AtHome(feet, home)) return feet;
        var r = Pathfinder.FindPath(w, feet, new GroundGoal(home.Y), options ?? Walk);
        if (!r.Success) return null;
        return r.Steps.Count > 0 ? new BlockPos(r.Steps[^1].X, r.Steps[^1].Y, r.Steps[^1].Z) : feet;
    }

    /// <summary>Up somewhere with no walk back down to home level (an elevated platform with its way down gone).</summary>
    public static bool Stranded(IBlockAccess w, BlockPos feet, BlockPos home) => !AtHome(feet, home) && WalkDown(w, feet, home) == null;

    /// <summary>Any foot cell at or below the home feet level + 1.</summary>
    public sealed class GroundGoal(int homeY) : Goal
    {
        public override bool IsReached(int x, int y, int z) => y <= homeY + 1;
        public override double Distance(int x, int y, int z) => Math.Max(0, y - homeY - 1) * 2;
    }
}
