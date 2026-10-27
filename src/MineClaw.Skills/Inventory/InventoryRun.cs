using System.Diagnostics;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Skills.Inventory;

/// <summary>
/// Executes click plans against the live window with bookkeeping (clicks, opens, moves, elapsed) and the invariants:
/// re-plan from the resynced window after a rejected click (0x6A accepted=false), and never close a window while the
/// cursor (or the 2×2 grid) still holds items.
/// </summary>
public sealed class InventoryRun(SkillContext ctx)
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    public int Clicks { get; private set; }
    public int Opens { get; private set; }
    public int Moves { get; set; }
    public int Rejected { get; private set; }
    public HashSet<BlockPos> Containers { get; } = new();
    public List<string> Notes { get; } = new();
    public BlockPos? Open { get; private set; }

    public IGameActions Game => ctx.Game;
    public WorldState World => ctx.World;

    public WindowModel Snapshot() => WindowModel.Of(World.Inventory.ActiveWindow, World.Inventory.Cursor);

    /// <summary>Runs clicks in order; false at the first rejected/failed one (the window is then resynced by the server).</summary>
    public async Task<bool> ExecAsync(IReadOnlyList<Click> plan, CancellationToken ct)
    {
        foreach (var c in plan)
        {
            ct.ThrowIfCancellationRequested();
            var r = await Game.ClickSlotAsync(c.Slot, c.Right, c.Shift, ct);
            Clicks++;
            if (!r.Ok)
            {
                Rejected++;
                await Game.WaitTicksAsync(2, ct); // let the 0x68/0x67 resync land
                return false;
            }
        }
        return true;
    }

    /// <summary>Moves exactly <paramref name="count"/> (or what's there) of an item between slot sets of the open window,
    /// re-planning after a reject. Returns how many arrived.</summary>
    public async Task<int> TransferAsync(int id, int? damage, int count, Func<WindowModel, IReadOnlyCollection<int>> from,
        Func<WindowModel, IReadOnlyCollection<int>> to, CancellationToken ct, int? dest = null)
    {
        int moved = 0;
        for (int attempt = 0; attempt < 4 && moved < count; attempt++)
        {
            var m = Snapshot();
            var toSlots = to(m);
            int before = m.Count(id, damage, toSlots);
            var plan = ClickPlanner.Transfer(m, id, damage, count - moved, from(m), toSlots, dest);
            if (plan.Count == 0) break;
            bool ok = await ExecAsync(plan, ct);
            // Rejected mid-plan: put whatever is on the cursor back where it came from, then re-plan from the resync.
            if (!ok) await ClearCursorAsync(ct, from(Snapshot()));
            moved += Snapshot().Count(id, damage, toSlots) - before;
            if (ok) break;
        }
        return moved;
    }

    /// <summary>Puts the cursor stack down (merging first). True when the cursor is empty.</summary>
    public async Task<bool> ClearCursorAsync(CancellationToken ct, IEnumerable<int>? prefer = null)
    {
        for (int i = 0; i < 3 && !World.Inventory.Cursor.IsEmpty; i++)
        {
            var m = Snapshot();
            var plan = ClickPlanner.ClearCursor(m, prefer ?? m.SlotsOf(Region.Player));
            if (plan.Count == 0) return false;
            await ExecAsync(plan, ct);
        }
        return World.Inventory.Cursor.IsEmpty;
    }

    /// <summary>Walks to and opens a container (claiming it on the hive board first). Null = opened.</summary>
    public async Task<string?> OpenAsync(BlockPos pos, CancellationToken ct)
    {
        if (Open == pos && World.Inventory.OpenWindow is not null) return null;
        var close = await CloseAsync(ct);
        if (close is not null) return close;
        if (ctx.Protected(pos)) return ctx.ProtectedText(pos, SkillJson.Fmt(pos));
        if (!ContainerDirectory.IsContainer(ctx.Block(pos))) return $"no container at {SkillJson.Fmt(pos)}";
        var claim = await ctx.Claims.TryClaimAsync(ClaimKey.Chest(pos, ctx.World.Dimension), TimeSpan.FromMinutes(2), "inventory", ct);
        if (!claim.Ok) return $"chest {SkillJson.Fmt(pos)} {claim.Message ?? "claimed"}";
        var r = await Game.OpenContainerAsync(pos, ct);
        if (!r.Ok) { await ctx.Claims.ReleaseAsync(ClaimKey.Chest(pos, ctx.World.Dimension), ct); return r.Message; }
        Opens++;
        Containers.Add(pos);
        Open = pos;
        return null;
    }

    /// <summary>Closes the open window only with an empty cursor/grid; snapshots the container for the index.</summary>
    public async Task<string?> CloseAsync(CancellationToken ct)
    {
        var w = World.Inventory.OpenWindow;
        if (!await ClearCursorAsync(ct)) return "cursor still holds items (no free slot); window left open";
        if (w is null)
        {
            // Player window: the 2×2 grid must be empty too.
            for (int s = 1; s <= 4; s++)
                if (!World.Inventory.PlayerWindow.Get(s).IsEmpty) await ExecAsync([new Click(s, Shift: true)], ct);
            Open = null;
            return null;
        }
        if (Open is { } pos)
        {
            ctx.Directory.Snapshot(pos, Enumerable.Range(0, w.ContainerSlots).Select(w.Get).ToArray());
            await ctx.Claims.ReleaseAsync(ClaimKey.Chest(pos, ctx.World.Dimension), ct);
        }
        await Game.CloseWindowAsync(ct);
        Open = null;
        return null;
    }

    public TimeSpan Elapsed => _sw.Elapsed;

    public string Summary(string? tail = null)
    {
        var s = $"{Moves} moves, {Containers.Count} chest{(Containers.Count == 1 ? "" : "s")}, {Clicks} clicks, {Elapsed.TotalSeconds:0}s";
        if (Rejected > 0) s += $", {Rejected} rejected+replanned";
        if (tail is { Length: > 0 }) s += "; " + tail;
        if (Notes.Count > 0) s += "; " + string.Join("; ", Notes.Take(3));
        return s;
    }

    /// <summary>Contents of the open container window.</summary>
    public ItemStack[] ContainerContents()
    {
        var w = World.Inventory.OpenWindow;
        return w is null ? [] : Enumerable.Range(0, w.ContainerSlots).Select(w.Get).ToArray();
    }

    public bool Reserved(int id) => Game.ReservedItems.Contains(id);
}
