using System.Runtime.CompilerServices;
using MineClaw.Bot.Building;
using MineClaw.Data.Versioning;
using MineClaw.Scripting.Story;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.Blueprints;

/// <summary>Incremental, resumable ore work belonging to an accepted mine, executed by its current builder.</summary>
internal sealed class MineOreWork(VoxelPlan design, BuildPlan plan, IBuildBody body, Func<string?> hold,
    Action save, IClaimBoard? claims = null, Func<BlockPos, bool>? foreign = null)
{
    private static readonly ConditionalWeakTable<BuildPlan, SemaphoreSlim> Gates = new();
    private WorldState W => body.World;
    private bool Modern => W.Inventory.Model.ModernClicks;
    private IClaimBoard? Board => body.Claims ?? claims;
    private bool Allowed(BlockPos at) => !design.Air.Contains(at) && !design.Air.Contains(at.Up())
        && !design.Blocks.ContainsKey(at) && !design.Unsupported.ContainsKey(at)
        && foreign?.Invoke(at) != true && !body.IsProtected(at, W.GetBlock(at))
        && !plan.Placed.Contains(at) && !plan.ScaffoldUp.Contains(at) && !plan.Exit.Contains(at);

    public void Expose(BlockPos dug, int family)
    {
        if (!design.Blueprint.Mine) return;
        lock (plan)
            foreach (var (dx, dy, dz) in OreIdentity.Neighbors)
            {
                var at = new BlockPos(dug.X + dx, dug.Y + dy, dug.Z + dz);
                if (at.Y < 0 || at.Y >= W.WorldHeight || !Allowed(at)) continue;
                bool unknown = !W.IsLoaded(at.X, at.Z);
                int ore = OreIdentity.Canonical(W.GetBlock(at), Modern);
                if (!unknown && (ore == 0 || family != 0 && ore != family)) continue;
                var frontier = plan.OreFrontier ??= [];
                if (frontier.All(c => c.Pos != at)) frontier.Add(new OreFrontierCell(at, unknown ? family : ore, unknown));
            }
    }

    public void Resume(IEnumerable<BlockPos> cells)
    {
        if (!design.Blueprint.Mine) return;
        foreach (var at in cells)
            if (design.Air.Contains(at) && W.IsLoaded(at.X, at.Z) && W.GetBlock(at) == 0) Expose(at, 0);
    }

    public async Task<int> DrainAsync(CancellationToken ct, bool allowAccess = true)
    {
        if (!design.Blueprint.Mine) return 0;
        var gate = Gates.GetValue(plan, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int count = 0;
            var tried = new HashSet<BlockPos>();
            while (body.Available && hold() == null && W.Inventory.EmptySlotCount() > 0)
            {
                ct.ThrowIfCancellationRequested();
                var pending = Pending(tried);
                if (pending.Count == 0) break;
                var visible = pending.FirstOrDefault(c => ReachScan.Within(W, W.Player.Position, c.Pos));
                if (visible != null)
                {
                    tried.Add(visible.Pos);
                    if (await MineAsync(visible, ct).ConfigureAwait(false)) count++;
                    continue;
                }
                var target = pending[0];
                if (!allowAccess) break; // Crew stands belong to its lane; the finale handles new access.
                var data = Modern ? (IGameData)Release1122GameData.Instance : Beta181GameData.Instance;
                var reach = ReachScan.Scan(W, data, W.Player.Position, target.Pos, walkable: true, targets: pending.Select(c => c.Pos).ToArray());
                var standText = reach["stand"]?.GetValue<string>();
                if (!TryPos(standText, out var stand)) { tried.Add(target.Pos); continue; }
                int steps = reach["steps"]?.GetValue<int>() ?? 0;
                // Reserve all placement material for the mine's own planned supports.
                int item = new[] { 3, 4, 87, 1, 24 }.FirstOrDefault(id => body.Count(id, 0)
                    - plan.Steps.Count(s => s.IsPlacement && !s.Finished && s.Item == id) >= steps);
                if (steps > 0 && (item == 0 || Enumerable.Range(0, steps).Any(y => !AccessAllowed(new BlockPos(stand.X, stand.Y + y, stand.Z)))))
                { tried.Add(target.Pos); continue; }
                var access = ClaimKey.Area(stand, stand with { Y = stand.Y + steps + 1 }, W.Dimension);
                if (Board is { } b && !(await b.TryClaimAsync(access, TimeSpan.FromMinutes(2), "mine ore access", ct).ConfigureAwait(false)).Ok)
                { tried.Add(target.Pos); continue; }
                var pole = new List<BlockPos>();
                try
                {
                    if (!(await body.GotoOreStandAsync(stand, ct).ConfigureAwait(false)).Ok) { tried.Add(target.Pos); continue; }
                    for (int y = 0; y < steps && hold() == null; y++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var feet = new BlockPos(stand.X, stand.Y + y, stand.Z);
                        if (!AccessAllowed(feet)) break;
                        bool placed;
                        try { placed = (await body.PillarAsync(feet, item, ct).ConfigureAwait(false)).Ok; }
                        finally
                        {
                            // The server may accept the block just before cancellation or a landing failure is reported.
                            if (W.GetBlock(feet) == item)
                            {
                                pole.Add(feet);
                                lock (plan) if (!plan.ScaffoldUp.Contains(feet)) plan.ScaffoldUp.Add(feet);
                                save();
                            }
                        }
                        if (!placed) break;
                    }
                    // Stay on this access while each removal exposes more of the same vein.
                    while (body.Available && hold() == null && W.Inventory.EmptySlotCount() > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        var next = Pending(tried).FirstOrDefault(c => ReachScan.Within(W, W.Player.Position, c.Pos));
                        if (next == null) break;
                        tried.Add(next.Pos);
                        if (await MineAsync(next, ct).ConfigureAwait(false)) count++;
                    }
                    tried.Add(target.Pos);
                }
                finally
                {
                    await RecoverAsync(pole).ConfigureAwait(false);
                    if (Board is { } b2) await b2.ReleaseAsync(access, CancellationToken.None).ConfigureAwait(false);
                }
                if (pole.Any(p => W.GetBlock(p) != 0)) break;
            }
            save();
            return count;
        }
        finally { gate.Release(); }
    }

    private bool AccessAllowed(BlockPos at) => W.IsLoaded(at.X, at.Z) && W.GetBlock(at) == 0
        && !design.Blocks.ContainsKey(at) && !design.Unsupported.ContainsKey(at) && foreign?.Invoke(at) != true
        && !body.IsProtected(at, 0) && !plan.Placed.Contains(at);

    private List<OreFrontierCell> Pending(HashSet<BlockPos> tried)
    {
        lock (plan)
        {
            if (plan.OreFrontier is { } candidates)
                for (int i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    if (!c.Unknown || !W.IsLoaded(c.Pos.X, c.Pos.Z)) continue;
                    int ore = OreIdentity.Canonical(W.GetBlock(c.Pos), Modern);
                    if (ore != 0 && (c.Ore == 0 || c.Ore == ore)) candidates[i] = c with { Ore = ore, Unknown = false };
                }
            // Unknown columns retain their saved ownership until observed again.
            plan.OreFrontier?.RemoveAll(c => W.IsLoaded(c.Pos.X, c.Pos.Z)
                && (c.Unknown || OreIdentity.Canonical(W.GetBlock(c.Pos), Modern) != c.Ore || !Allowed(c.Pos)));
            return (plan.OreFrontier ?? []).Where(c => !c.Unknown && !tried.Contains(c.Pos) && OreIdentity.ExposureSafe(W, c.Pos))
                .OrderBy(c => W.Player.Position.DistanceSquaredTo(new Vec3d(c.Pos.X + .5, c.Pos.Y + .5, c.Pos.Z + .5))).ToList();
        }
    }

    private async Task<bool> MineAsync(OreFrontierCell cell, CancellationToken ct)
    {
        var key = ClaimKey.Block(cell.Pos, W.Dimension);
        if (Board is { } b && !(await b.TryClaimAsync(key, TimeSpan.FromMinutes(2), "mine exposed ore", ct).ConfigureAwait(false)).Ok) return false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (hold() != null || !Allowed(cell.Pos)) return false;
            var r = await body.DigOreHereAsync(cell.Pos, cell.Ore, ct).ConfigureAwait(false);
            if (!r.Ok || W.GetBlock(cell.Pos) != 0) return false;
            lock (plan) plan.OreFrontier?.RemoveAll(c => c.Pos == cell.Pos);
            Expose(cell.Pos, cell.Ore);
            save();
            return true;
        }
        finally { if (Board is { } b2) await b2.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); }
    }

    private async Task RecoverAsync(List<BlockPos> pole)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            foreach (var at in pole.AsEnumerable().Reverse())
            {
                if (!body.Available) break;
                var feet = W.Player.Position;
                if (W.GetBlock(at) != 0 && ((int)Math.Floor(feet.X) != at.X || (int)Math.Floor(feet.Z) != at.Z || Math.Abs(feet.Y - at.Y - 1) > .1)) break;
                if (W.GetBlock(at) != 0 && !(await body.RecoverOreScaffoldAsync(at, cleanup.Token).ConfigureAwait(false)).Ok) break;
                if (W.GetBlock(at) != 0) break;
                lock (plan) plan.ScaffoldUp.Remove(at);
                save();
            }
        }
        catch (OperationCanceledException) when (cleanup.IsCancellationRequested) { }
    }

    private static bool TryPos(string? text, out BlockPos at)
    {
        at = default;
        var p = text?.Split(',');
        if (p?.Length != 3 || !int.TryParse(p[0], out int x) || !int.TryParse(p[1], out int y) || !int.TryParse(p[2], out int z)) return false;
        at = new BlockPos(x, y, z);
        return true;
    }
}
