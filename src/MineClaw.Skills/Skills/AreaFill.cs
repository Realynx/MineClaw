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

    /// <summary>What the fill starts from: the level, the empty cells still to fill and their BFS level from the floor.</summary>
    private sealed record Setup(Rect R, int Y, string Where, List<BlockPos> Cells, string Cover, HashSet<BlockPos> Todo, int Total,
        List<BlockPos> Prot, Dictionary<BlockPos, int> Level);

    public static async Task<SkillResult> RunAsync(SkillContext ctx, JsonElement args, Rect r, CancellationToken ct)
    {
        var (s, done) = Prepare(ctx, args, r);
        if (s is null) return done!;
        int max = Math.Clamp(args.Int("max") ?? s.Todo.Count, 1, MaxCells);
        var (block, note) = await MaterialAsync(ctx, args.Str("block"), Math.Min(max, s.Todo.Count), ct).ConfigureAwait(false);
        if (block is not { } id) return SkillResult.Fail(note ?? "no building blocks");
        var fill = new Filler(ctx, s, max, id);
        await fill.RunAsync(ct).ConfigureAwait(false);
        return fill.Result(note);
    }

    /// <summary>The level and cells to fill, or the answer without placing anything (too big, outside the world, not loaded,
    /// already solid, nothing to grow from).</summary>
    private static (Setup? S, SkillResult? Done) Prepare(SkillContext ctx, JsonElement args, Rect r)
    {
        var w = ctx.World;
        if (r.Count > MaxCells) return (null, SkillResult.Fail($"area {r} is {r.Count} cells; at most {MaxCells} per call"));
        int y = args.Int("y") ?? FloorLevel(ctx, r) ?? ctx.Me.Y - 1;
        if (y < 1 || y >= w.WorldHeight - 2) return (null, SkillResult.Fail($"y {y} is outside the world"));
        string where = $"{r} at y{y}";
        var cells = r.Cells(y).ToList();
        if (cells.Any(c => !w.IsLoaded(c.X, c.Z))) return (null, SkillResult.Fail($"part of {where} isn't loaded; come closer first"));
        string cover = Coverage(ctx, r, y);
        var todo = cells.Where(c => SkillWorld.Replaceable(w, c)).ToHashSet();
        int total = todo.Count;
        if (total == 0) return (null, SkillResult.Success($"filled 0/0 cells of {where}: already solid" + cover));
        var prot = todo.Where(ctx.Protected).ToList();
        todo.ExceptWith(prot);
        var level = Levels(w, y, todo);
        if (level.Count == 0)
            return (null, SkillResult.Fail($"nothing solid at y{y} in or beside {r}: no edge to grow the floor from" + cover));
        return (new Setup(r, y, where, cells, cover, todo, total, prot, level), null);
    }

    /// <summary>The placing loop: the lowest-level reachable cell next (beside the last one, then nearest), restocked once,
    /// stopped by a veto, a void/danger refusal, 8 failures in a row or no reachable cell.</summary>
    private sealed class Filler(SkillContext ctx, Setup s, int max, int id)
    {
        private readonly WorldState _w = ctx.World;
        private readonly string _name = SkillWorld.Name(id);
        private readonly Dictionary<BlockPos, int> _failedAt = new(); // cell → its support count when it failed (retried once that grows)
        private int _placed, _streak;
        private bool _restocked;
        private string? _stop, _lastFail;
        private BlockPos? _last;

        public async Task RunAsync(CancellationToken ct)
        {
            while (s.Todo.Count > 0 && _placed < max)
            {
                ct.ThrowIfCancellationRequested();
                s.Todo.RemoveWhere(c => !SkillWorld.Replaceable(_w, c)); // someone else filled it
                if (s.Todo.Count == 0 || Vetoed()) break;
                var me = ctx.Feet;
                if (Next(me) is not { } c)
                {
                    NothingReachable();
                    break;
                }
                if (!await StockedAsync(ct).ConfigureAwait(false) || !await PlaceAsync(c, me, ct).ConfigureAwait(false)) break;
            }
        }

        private bool Vetoed()
        {
            _stop = BuildOps.HostileVeto(ctx) ?? ctx.SurvivalVeto("expand_island", SafetyClass.Edge);
            return _stop != null;
        }

        private int Supports(BlockPos c) => BlockFaceExtensions.All.Count(f => SkillWorld.Solid(_w, c.Offset(f)));

        private BlockPos? Next(Vec3d me) =>
            s.Todo.Where(c => s.Level.ContainsKey(c) && Supports(c) > _failedAt.GetValueOrDefault(c))
                .OrderBy(c => s.Level[c])
                .ThenBy(c => _last is { } l && Math.Abs(c.X - l.X) + Math.Abs(c.Z - l.Z) == 1 ? 0 : 1)
                .ThenBy(c => Math.Pow(c.X + 0.5 - me.X, 2) + Math.Pow(c.Z + 0.5 - me.Z, 2))
                .Cast<BlockPos?>().FirstOrDefault();

        private void NothingReachable()
        {
            if (_failedAt.Count > 0) _stop = "no cell can be reached safely" + (_lastFail is null ? "" : $"; last: {_lastFail}");
        }

        /// <summary>Blocks on hand, fetched once more through the Obtain hook when they run out.</summary>
        private async Task<bool> StockedAsync(CancellationToken ct)
        {
            if (ctx.Has(id)) return true;
            if (!_restocked && ctx.Registry.Obtain != null)
            {
                _restocked = true;
                await MaterialAsync(ctx, _name, Math.Min(max - _placed, s.Todo.Count), ct).ConfigureAwait(false);
            }
            if (ctx.Has(id)) return true;
            _stop = $"out of {_name}";
            return false;
        }

        /// <summary>Out of reach: walk first (the pathfinder never stands over the void); the client's place still leans out at an
        /// edge (sneaking, EdgeGuard on) when the only support is the outer face of the floor block beside the cell.</summary>
        private async Task<bool> PlaceAsync(BlockPos c, Vec3d me, CancellationToken ct)
        {
            if (Aiming.FindPlaceAim(_w, _w.Player.EyePosition, c, ctx.Game.Reach, AABB.ForPlayer(me)) is null)
                await ctx.ReachAsync(c, ct).ConfigureAwait(false);
            var res = await ctx.Game.PlaceAsync(c, id, ct).ConfigureAwait(false);
            if (!res.Ok || SkillWorld.Replaceable(_w, c)) return Failed(c, res.Message);
            _placed++;
            s.Todo.Remove(c);
            _last = c;
            _streak = 0;
            return true;
        }

        private bool Failed(BlockPos c, string message)
        {
            _failedAt[c] = Supports(c);
            _lastFail = message;
            if (message.Contains("void", StringComparison.OrdinalIgnoreCase) || message.Contains("danger", StringComparison.OrdinalIgnoreCase))
                _stop = message;
            else if (++_streak >= 8) _stop = $"no cell can be reached safely ({_streak} failed in a row; last: {message})";
            return _stop is null;
        }

        public SkillResult Result(string? note)
        {
            int left = s.Cells.Count(c => SkillWorld.Replaceable(_w, c));
            var why = _stop ?? Why(left);
            var text = $"filled {_placed}/{s.Total} cells of {s.Where}"
                       + (left == 0 ? "; the floor is complete" : $"; {left} left" + (why is null ? "" : $" ({why})"))
                       + s.Cover + $"; {ctx.Count(id)} {_name} on me";
            if (note != null) ctx.Log?.Invoke($"expand_island area materials: {note}");
            return new SkillResult(_placed > 0 || left == 0, text);
        }

        /// <summary>Why cells are left when nothing stopped the run: the per-run cap, cells cut off from the floor, protected ones.</summary>
        private string? Why(int left)
        {
            if (left == 0) return null;
            int cut = s.Todo.Count(c => !s.Level.ContainsKey(c));
            if (_placed >= max && s.Todo.Count > 0) return $"max {max} per run";
            if (cut > 0) return $"{cut} not connected to the floor";
            return s.Prot.Count > 0 ? $"{s.Prot.Count} protected" : null;
        }
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
        for (int y = Math.Max(1, ctx.Me.Y - 10), y1 = Math.Min(w.WorldHeight - 2, ctx.Me.Y + 4); y <= y1; y++)
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
        if (Named(name) is not { } id) return (null, $"'{name}' isn't a placeable block");
        int want = Math.Min(need, 9 * 64); // what the inventory comfortably holds; the rest is fetched when it runs out
        string? note = await ObtainAsync(ctx, id, want, ct).ConfigureAwait(false);
        if (ctx.Has(id) && !ctx.Game.ReservedItems.Contains(id)) return (id, note);
        if (string.IsNullOrWhiteSpace(name) && SkillWorld.BuildingBlock(ctx) is { } other) return (other, note);
        return (null, $"no {SkillWorld.Name(id)} to lay" + (note is null ? "" : $" ({note})"));
    }

    /// <summary>The block named (a placeable block id), cobblestone when none is named; null when the name isn't a block.</summary>
    private static int? Named(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Blocks.Cobblestone;
        return SkillJson.Item(name) is { Id: > 0 and < 256 } it ? it.Id : null;
    }

    /// <summary>Tops <paramref name="id"/> up to <paramref name="want"/> through the Obtain hook; its note, or null.</summary>
    private static async Task<string?> ObtainAsync(SkillContext ctx, int id, int want, CancellationToken ct)
    {
        if (ctx.Count(id) >= want || ctx.Registry.Obtain is not { } obtain) return null;
        try { return await obtain(ctx, id, want, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { ctx.Log?.Invoke($"expand_island obtain threw: {ex.Message}"); }
        return null;
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
            if (Apart(f, r) || Math.Abs(f.Y0 - 1 - y) > 3) continue; // not under it at all, or another level
            var gaps = Gaps(f, r);
            if (gaps.Count > 0) parts.Add($"{string.Join(", ", gaps)} of {f.Label} still uncovered (its area={f.X0},{f.Z0},{f.X1},{f.Z1})");
        }
        return parts.Count == 0 ? "" : "; " + string.Join("; ", parts.Take(2));
    }

    private static bool Apart(DesignFootprint f, Rect r) => f.X1 < r.X0 || f.X0 > r.X1 || f.Z1 < r.Z0 || f.Z0 > r.Z1;

    /// <summary>The strips of the footprint beyond each side of the rectangle.</summary>
    private static List<string> Gaps(DesignFootprint f, Rect r)
    {
        var gaps = new List<string>();
        if (f.X0 < r.X0) gaps.Add($"x {f.X0}..{r.X0 - 1}");
        if (f.X1 > r.X1) gaps.Add($"x {r.X1 + 1}..{f.X1}");
        if (f.Z0 < r.Z0) gaps.Add($"z {f.Z0}..{r.Z0 - 1}");
        if (f.Z1 > r.Z1) gaps.Add($"z {r.Z1 + 1}..{f.Z1}");
        return gaps;
    }
}
