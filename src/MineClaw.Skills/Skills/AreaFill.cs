using System.Text.Json;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// <c>expand_island area=x0,z0,x1,z1 y?</c>: fills every empty cell of a rectangle at one level (the foundation under a build
/// that hangs over the void, live 10-28 "expand the floor so you can build it" for design #4). It grows only from what is
/// already solid, in BFS order from the existing floor (ring by ring, every placement against a solid neighbour: the classic
/// sneak-bridge), so it never places a block in mid-air. Void-safe: the walk to each cell is the pathfinder's (no stand over
/// the void), the client sneaks every placement and leans out over an edge only with the EdgeGuard on, the hostile and
/// survival vetoes are checked before every block, and it stops and says why when no cell can be reached safely. Resumable:
/// a second run fills whatever of the rectangle is still empty. Materials come from the registry's Obtain hook (the Obtainer:
/// inventory, chests, crafting): cobblestone unless <c>block=</c>.
/// </summary>
internal static class AreaFill
{
    public const int MaxCells = 1024;

    public readonly record struct Rect(int X0, int Z0, int X1, int Z1)
    {
        public int Count => (X1 - X0 + 1) * (Z1 - Z0 + 1);
        public bool Contains(int x, int z) => x >= X0 && x <= X1 && z >= Z0 && z <= Z1;
        public override string ToString() => $"{X0}..{X1},{Z0}..{Z1}";

        public IEnumerable<BlockPos> Cells(int y)
        {
            for (int x = X0; x <= X1; x++)
            for (int z = Z0; z <= Z1; z++)
                yield return new BlockPos(x, y, z);
        }
    }

    /// <summary>"x0,z0,x1,z1" (also "x0 z0 x1 z1" or a JSON array), corners in any order.</summary>
    public static Rect? ParseArea(string text)
    {
        var parts = text.Split([',', ' ', ';', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return null;
        var v = new int[4];
        for (int i = 0; i < 4; i++) if (!int.TryParse(parts[i], out v[i])) return null;
        return new Rect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }

    public static async Task<SkillResult> RunAsync(SkillContext ctx, JsonElement args, Rect r, CancellationToken ct)
    {
        var w = ctx.World;
        if (r.Count > MaxCells) return SkillResult.Fail($"area {r} is {r.Count} cells; at most {MaxCells} per call");
        int y = args.Int("y") ?? FloorLevel(ctx, r) ?? ctx.Me.Y - 1;
        if (y < 1 || y >= WorldState.Height - 2) return SkillResult.Fail($"y {y} is outside the world");
        string where = $"{r} at y{y}";
        var cells = r.Cells(y).ToList();
        if (cells.Any(c => !w.IsLoaded(c.X, c.Z))) return SkillResult.Fail($"part of {where} isn't loaded; come closer first");
        string cover = Coverage(ctx, r, y);
        var todo = cells.Where(c => SkillWorld.Replaceable(w, c)).ToHashSet();
        int total = todo.Count;
        if (total == 0) return SkillResult.Success($"filled 0/0 cells of {where}: already solid" + cover);
        var prot = todo.Where(ctx.Protected).ToList();
        todo.ExceptWith(prot);
        var level = Levels(w, y, todo);
        if (level.Count == 0)
            return SkillResult.Fail($"nothing solid at y{y} in or beside {r}: no edge to grow the floor from" + cover);
        int max = Math.Clamp(args.Int("max") ?? todo.Count, 1, MaxCells);
        var (block, note) = await MaterialAsync(ctx, args.Str("block"), Math.Min(max, todo.Count), ct).ConfigureAwait(false);
        if (block is not { } id) return SkillResult.Fail(note ?? "no building blocks");
        string name = SkillWorld.Name(id);

        int placed = 0, streak = 0;
        bool restocked = false;
        string? stop = null, lastFail = null;
        BlockPos? last = null;
        var failedAt = new Dictionary<BlockPos, int>(); // cell → its support count when it failed (retried once that grows)
        int Supports(BlockPos c) => BlockFaceExtensions.All.Count(f => SkillWorld.Solid(w, c.Offset(f)));
        while (todo.Count > 0 && placed < max)
        {
            ct.ThrowIfCancellationRequested();
            todo.RemoveWhere(c => !SkillWorld.Replaceable(w, c)); // someone else filled it
            if (todo.Count == 0) break;
            if (BuildOps.HostileVeto(ctx) is { } h) { stop = h; break; }
            if (ctx.SurvivalVeto("expand_island", SafetyClass.Edge) is { } sv) { stop = sv; break; }
            var me = ctx.Feet;
            var next = todo.Where(c => level.ContainsKey(c) && Supports(c) > failedAt.GetValueOrDefault(c))
                .OrderBy(c => level[c])
                .ThenBy(c => last is { } l && Math.Abs(c.X - l.X) + Math.Abs(c.Z - l.Z) == 1 ? 0 : 1)
                .ThenBy(c => Math.Pow(c.X + 0.5 - me.X, 2) + Math.Pow(c.Z + 0.5 - me.Z, 2))
                .Cast<BlockPos?>().FirstOrDefault();
            if (next is not { } c)
            {
                if (failedAt.Count > 0) stop = "no cell can be reached safely" + (lastFail is null ? "" : $"; last: {lastFail}");
                break;
            }
            if (!ctx.Has(id))
            {
                if (!restocked && ctx.Registry.Obtain != null)
                {
                    restocked = true;
                    await MaterialAsync(ctx, name, Math.Min(max - placed, todo.Count), ct).ConfigureAwait(false);
                }
                if (!ctx.Has(id)) { stop = $"out of {name}"; break; }
            }
            // Out of reach: walk first (the pathfinder never stands over the void); the client's place still leans out at an
            // edge (sneaking, EdgeGuard on) when the only support is the outer face of the floor block beside the cell.
            if (Aiming.FindPlaceAim(w, w.Player.EyePosition, c, ctx.Game.Reach, AABB.ForPlayer(me)) is null)
                await ctx.ReachAsync(c, ct).ConfigureAwait(false);
            var res = await ctx.Game.PlaceAsync(c, id, ct).ConfigureAwait(false);
            if (res.Ok && !SkillWorld.Replaceable(w, c))
            {
                placed++;
                todo.Remove(c);
                last = c;
                streak = 0;
                continue;
            }
            failedAt[c] = Supports(c);
            lastFail = res.Message;
            if (res.Message.Contains("void", StringComparison.OrdinalIgnoreCase) || res.Message.Contains("danger", StringComparison.OrdinalIgnoreCase))
            { stop = res.Message; break; }
            if (++streak >= 8) { stop = $"no cell can be reached safely ({streak} failed in a row; last: {res.Message})"; break; }
        }

        int left = cells.Count(c => SkillWorld.Replaceable(w, c));
        int cut = todo.Count(c => !level.ContainsKey(c));
        var why = stop ?? (left == 0 ? null
            : placed >= max && todo.Count > 0 ? $"max {max} per run"
            : cut > 0 ? $"{cut} not connected to the floor"
            : prot.Count > 0 ? $"{prot.Count} protected" : null);
        var text = $"filled {placed}/{total} cells of {where}"
                   + (left == 0 ? "; the floor is complete" : $"; {left} left" + (why is null ? "" : $" ({why})"))
                   + cover + $"; {ctx.Count(id)} {name} on me";
        if (note != null) ctx.Log?.Invoke($"expand_island area materials: {note}");
        return new SkillResult(placed > 0 || left == 0, text);
    }

    /// <summary>BFS distance of each empty cell from the existing floor (cells with a solid side neighbour at y or a block under them
    /// are 0); cells not connected to any are left out.</summary>
    private static Dictionary<BlockPos, int> Levels(IBlockAccess w, int y, HashSet<BlockPos> todo)
    {
        var level = new Dictionary<BlockPos, int>();
        var q = new Queue<BlockPos>();
        foreach (var c in todo)
            if (SkillWorld.Horizontal.Any(o => SkillWorld.Solid(w, c + o)) || SkillWorld.Solid(w, c.Down()))
            {
                level[c] = 0;
                q.Enqueue(c);
            }
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            foreach (var o in SkillWorld.Horizontal)
            {
                var n = c + o;
                if (todo.Contains(n) && level.TryAdd(n, level[c] + 1)) q.Enqueue(n);
            }
        }
        return level;
    }

    /// <summary>The default level: the floor of the nearest solid edge in or beside the rectangle (a solid block next to one of
    /// its empty cells), floor-like ones (open above) first; null when there is none near my height.</summary>
    private static int? FloorLevel(SkillContext ctx, Rect r)
    {
        var w = ctx.World;
        var me = ctx.Feet;
        (int Y, double Score)? best = null;
        for (int y = Math.Max(1, ctx.Me.Y - 10); y <= Math.Min(WorldState.Height - 2, ctx.Me.Y + 4); y++)
            for (int x = r.X0 - 1; x <= r.X1 + 1; x++)
            for (int z = r.Z0 - 1; z <= r.Z1 + 1; z++)
            {
                var p = new BlockPos(x, y, z);
                if (!SkillWorld.Solid(w, p)) continue;
                if (!SkillWorld.Horizontal.Any(o => r.Contains(x + o.X, z + o.Z) && SkillWorld.Replaceable(w, p + o))) continue;
                double score = Math.Pow(x + 0.5 - me.X, 2) + Math.Pow(y + 1 - me.Y, 2) + Math.Pow(z + 0.5 - me.Z, 2)
                               + (SkillWorld.Air(w, p.Up()) ? 0 : 1000);
                if (best is not { } b || score < b.Score) best = (y, score);
            }
        return best?.Y;
    }

    /// <summary>The block to lay: <paramref name="name"/>, else cobblestone (fetched through the Obtain hook up to
    /// <paramref name="need"/>), else any building block on hand.</summary>
    private static async Task<(int? Block, string? Note)> MaterialAsync(SkillContext ctx, string? name, int need, CancellationToken ct)
    {
        int id = Blocks.Cobblestone;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (SkillJson.Item(name) is not { Id: > 0 and < 256 } it) return (null, $"'{name}' isn't a placeable block");
            id = it.Id;
        }
        string? note = null;
        int want = Math.Min(need, 9 * 64); // what the inventory comfortably holds; the rest is fetched when it runs out
        if (ctx.Count(id) < want && ctx.Registry.Obtain is { } obtain)
            try { note = await obtain(ctx, id, want, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { ctx.Log?.Invoke($"expand_island obtain threw: {ex.Message}"); }
        if (ctx.Has(id) && !ctx.Game.ReservedItems.Contains(id)) return (id, note);
        if (string.IsNullOrWhiteSpace(name) && SkillWorld.BuildingBlock(ctx) is { } other) return (other, note);
        return (null, $"no {SkillWorld.Name(id)} to lay" + (note is null ? "" : $" ({note})"));
    }

    /// <summary>"; x -33..-32 of design #4 still uncovered (its area=-33,7,-18,22)" for each known design whose footprint the
    /// rectangle overlaps only partly, at a matching level (a floor one below its bottom, give or take 3); "" otherwise.</summary>
    internal static string Coverage(SkillContext ctx, Rect r, int y)
    {
        IReadOnlyList<DesignFootprint> fps;
        try { fps = ctx.Registry.Footprints?.Invoke() ?? []; }
        catch (Exception) { return ""; }
        var parts = new List<string>();
        foreach (var f in fps)
        {
            if (f.X1 < r.X0 || f.X0 > r.X1 || f.Z1 < r.Z0 || f.Z0 > r.Z1) continue; // not under it at all
            if (Math.Abs(f.Y0 - 1 - y) > 3) continue;
            var gaps = new List<string>();
            if (f.X0 < r.X0) gaps.Add($"x {f.X0}..{r.X0 - 1}");
            if (f.X1 > r.X1) gaps.Add($"x {r.X1 + 1}..{f.X1}");
            if (f.Z0 < r.Z0) gaps.Add($"z {f.Z0}..{r.Z0 - 1}");
            if (f.Z1 > r.Z1) gaps.Add($"z {r.Z1 + 1}..{f.Z1}");
            if (gaps.Count > 0) parts.Add($"{string.Join(", ", gaps)} of {f.Label} still uncovered (its area={f.X0},{f.Z0},{f.X1},{f.Z1})");
        }
        return parts.Count == 0 ? "" : "; " + string.Join("; ", parts.Take(2));
    }
}
