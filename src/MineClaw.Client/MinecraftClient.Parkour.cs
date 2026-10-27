using System.Collections.Concurrent;
using MineClaw.Client.Survival;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Gap-jump (parkour) plumbing for goto: sprint/food gating, the void-parkour authorization (config, or a trusted player's
/// chat within <see cref="ClientOptions.VoidParkourRequestWindow"/> plus a clutch block in the hotbar), the shared
/// validated-jump cache, and the result-line summary. The jump itself is planned and validated in
/// <see cref="Pathfinder"/>/<see cref="Parkour"/>, executed by <see cref="PathFollower"/> and guarded by
/// <see cref="EdgeGuard"/> (planned-parkour exemption) on the tick thread.
/// </summary>
public sealed partial class MinecraftClient
{
    private readonly ParkourCache _parkourCache = new();
    /// <summary>Tick thread: the committed gap jump to keep flying after its takeoff, even if the action ends mid-air.</summary>
    private ParkourCommit? _flight;
    private readonly ConcurrentDictionary<string, DateTime> _trustedChat = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Validated gap jumps shared by every plan of this client (entries re-validate when nearby blocks change).</summary>
    public ParkourCache ParkourCache => _parkourCache;

    /// <summary>b1.8.1 (EntityPlayerSP): sprinting needs food &gt; 6.</summary>
    private bool CanSprintNow() => World.Player.Food >= HungerRules.SprintFood;

    /// <summary>
    /// Tick thread. Once a gap jump has taken off, releasing the keys mid-air would drop the bot into the gap: when the
    /// action ends (stop, timeout, danger abort, a new action) before the landing, keep feeding the plan's input until
    /// touchdown. The edge guard still re-simulates it every tick (and takes over if it no longer lands).
    /// </summary>
    private ParkourCommit? ContinueFlight(PhysicsState phys, ref MoveInput input, ParkourCommit? fromDriver)
    {
        if (fromDriver is { } pc)
        {
            _flight = pc.Tick >= pc.Plan.Schedule.JumpTick ? pc with { Tick = pc.Tick + 1 } : null;
            return pc;
        }
        if (_flight is not { } fl) return null;
        if (phys.OnGround || phys.InWater || phys.OnLadder || fl.Tick - fl.Plan.Schedule.JumpTick > Parkour.MaxFlightTicks)
        {
            _flight = null;
            return null;
        }
        input = fl.Plan.Schedule.InputAt(fl.Tick, phys);
        _flight = fl with { Tick = fl.Tick + 1 };
        return fl;
    }

    private void NoteChatForParkour(string player)
    {
        foreach (var t in Options.ParkourTrustedPlayers)
            if (t.Equals(player, StringComparison.OrdinalIgnoreCase)) _trustedChat[t] = DateTime.UtcNow;
    }

    /// <summary>Void gap jumps for this goto: the config switch, or <see cref="ParkourMode.Void"/> while a trusted player
    /// has spoken within the request window (the brain only sets it when asked). The clutch-block and strict-margin
    /// conditions are applied by the planner.</summary>
    private bool VoidParkourAuthorized(GotoOptions o)
    {
        if (o.Parkour == ParkourMode.Off) return false;
        if (CurrentAction == "follow") return false; // follow re-plans in 2 s slices: never over the void
        if (Options.AllowVoidParkour) return true;
        if (o.Parkour != ParkourMode.Void) return false;
        var since = DateTime.UtcNow - Options.VoidParkourRequestWindow;
        foreach (var t in _trustedChat.Values)
            if (t >= since) return true;
        Log("goto: void parkour asked for, but no trusted player spoke recently; ground jumps only");
        return false;
    }

    private bool HotbarHasClutchBlock()
    {
        var inv = World.Inventory;
        for (int i = 0; i < 9; i++)
        {
            var st = inv.GetPlayerSlot(36 + i);
            if (!st.IsEmpty && IsClutchBlock(st.Id)) return true;
        }
        return false;
    }

    /// <summary>" (1 sprint-jump 3.2 blocks)", " (2 sprint-jumps 3.2, 2.8 blocks, 1 gap-jump 1.9 blocks)" or "".</summary>
    internal static string JumpSummary(IReadOnlyList<ParkourPlan> jumps)
    {
        if (jumps.Count == 0) return "";
        var parts = new List<string>(2);
        foreach (var g in jumps.GroupBy(j => j.Schedule.Sprint).OrderByDescending(g => g.Key))
        {
            int n = g.Count();
            string kind = g.Key ? "sprint-jump" : "gap-jump";
            parts.Add($"{n} {kind}{(n > 1 ? "s" : "")} {string.Join(", ", g.Select(j => j.Distance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))} blocks"
                      + (g.Any(j => j.OverVoid) ? " over the void" : ""));
        }
        return $" ({string.Join(", ", parts)})";
    }
}
