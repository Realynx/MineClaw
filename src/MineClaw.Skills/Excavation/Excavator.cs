using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>A target air volume: cells to dig with their phase (a pattern's repeat: dug strictly in order when
/// <see cref="Ordered"/>), openings never sealed (<see cref="Skip">doorways, connections</see>) and the access cells a tunnel added.</summary>
public sealed class DigVolume(string label)
{
    public string Label { get; } = label;
    public Dictionary<BlockPos, int> Phase { get; } = new();
    /// <summary>Openings that stay open: a doorway into another excavation, the entrance at the top of a shaft.</summary>
    public HashSet<BlockPos> Skip { get; } = new();
    /// <summary>Cells added on the way (an access tunnel, ores followed from a corridor): dug like the rest, reported apart.</summary>
    public HashSet<BlockPos> Access { get; } = new();
    /// <summary>Phases strictly in order (a stair: never digging ahead); a hazard in phase k stops every later phase.</summary>
    public bool Ordered { get; init; }

    public void Add(BlockPos p, int phase = 0)
    {
        if (!Phase.TryGetValue(p, out int k) || phase < k) Phase[p] = phase;
    }

    public bool Contains(BlockPos p) => Phase.ContainsKey(p);
    public int Count => Phase.Count;
    public IEnumerable<BlockPos> Cells => Phase.Keys;

    public static (BlockPos Min, BlockPos Max) Bounds(IEnumerable<BlockPos> cells)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
        foreach (var c in cells)
        {
            x0 = Math.Min(x0, c.X); y0 = Math.Min(y0, c.Y); z0 = Math.Min(z0, c.Z);
            x1 = Math.Max(x1, c.X); y1 = Math.Max(y1, c.Y); z1 = Math.Max(z1, c.Z);
        }
        return (new BlockPos(x0, y0, z0), new BlockPos(x1, y1, z1));
    }
}

public sealed record ExcavateOptions
{
    /// <summary>Fill the openings the dig exposes into foreign air (caves, dungeons) and floor holes, with the cheapest block.</summary>
    public bool Seal { get; init; } = true;
    /// <summary>Torch the dug space so no cell stays at or below light 7 (mobs spawn).</summary>
    public bool Torches { get; init; } = true;
    /// <summary>Dig a 1×2 passage from where I can walk when no stand reaches the volume.</summary>
    public bool Tunnel { get; init; } = true;
    /// <summary>Also dig ores a dug cell exposes (strip mines), following the vein while it stays in reach.</summary>
    public bool Ores { get; init; }
    public string? Block { get; init; }
    public int MaxDigs { get; init; } = 640;
    public int MaxSeal { get; init; } = 96;
    public int MaxOres { get; init; } = 64;
    public int LightLevel { get; init; } = 8;
    public TimeSpan Budget { get; init; } = TimeSpan.FromMinutes(12);
    /// <summary>Keep the volume as must-be-air after the job (rooms, patterns, strips); a plain box is released.</summary>
    public bool KeepRegistered { get; init; }
}

/// <summary>What a run did, in one line: "dug 48, sealed 2, torches 3, left 0 (+40 cobblestone, +3 coal)".</summary>
public sealed class ExcavateReport
{
    public int Dug, Sealed, Torches, Tunnel, Refills;
    public Dictionary<BlockPos, string> Left { get; } = new();
    public Dictionary<string, int> Ores { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BlockPos> Unsealed { get; } = new();
    public string? Stop;
    public string Gained = "";

    public bool Ok => Stop is null && Left.Count == 0;

    public string Text(string what)
    {
        var sb = new System.Text.StringBuilder($"{what}: dug {Dug}");
        if (Tunnel > 0) sb.Append($" (incl. {Tunnel} access)");
        sb.Append($", sealed {Sealed}, torches {Torches}, left {Left.Count}");
        if (Left.Count > 0)
            sb.Append(" (").Append(string.Join("; ", Left.GroupBy(l => l.Value).OrderByDescending(g => g.Count()).Take(2)
                .Select(g => $"{g.Count()} {g.Key} e.g. {SkillJson.Fmt(g.First().Key)}"))).Append(')');
        if (Unsealed.Count > 0) sb.Append($"; {Unsealed.Count} opening{(Unsealed.Count == 1 ? "" : "s")} not sealed e.g. {SkillJson.Fmt(Unsealed[0])}");
        if (Ores.Count > 0) sb.Append("; ores: ").Append(string.Join(", ", Ores.OrderByDescending(o => o.Value).Select(o => $"{o.Key} {o.Value}")));
        if (Stop != null) sb.Append($"; stopped: {Stop}");
        if (Gained.Length > 0) sb.Append($" ({Gained})");
        return sb.ToString();
    }
}

/// <summary>
/// The deterministic excavation executor (zero tokens): digs a target air volume from where the body can actually stand,
/// tunnelling in from the dug space instead of pathing to a buried block, then seals what the dig opened and torches it.
/// Live 10-30 CT 114 (Fox's stair shaft to diamond level, 4×4 rooms off it): every room and step went through the LLM worker
/// cell by cell with invented coordinates: "dig failed 2x: can't reach stone at 147,12,275" (a block buried in rock), "goto … no
/// path to within 1 of (151,12,275) … need 1 blocks to pillar/bridge", "place failed 2x: 154,17,277 is occupied by
/// cobblestone", the pickaxe 98 → 64 on a failed job, four LLM rounds scanning the shaft walls by hand for cave holes.
/// Each step: flood the cells I can walk to (1-block drops only: a stair step, never a jump into a cave just opened), pick the
/// exposed target cell with the cheapest stand that sees a face of it (higher cells first; an ordered volume's earlier repeat
/// first), walk there, dig. A cell whose floor would open onto foreign air (a dungeon under the next stair column, 02:18 "see
/// how we're in a dungeon here? be careful about falling") is probed first: dug only when its floor can be sealed right after,
/// else left, and an ordered volume stops descending there. Openings into foreign air around dug cells are filled with the
/// cheapest block on hand, never a must-be-air cell (<see cref="KeepAirLog"/>), a doorway or the space I came from. Floors of
/// other excavations (a stair's treads) are never dug. Gravel/sand that falls in is dug again.
/// </summary>
public sealed class Excavator(SkillContext ctx, DigVolume vol, ExcavateOptions? options = null)
{
    private readonly ExcavateOptions _o = options ?? new ExcavateOptions();
    private WorldState W => ctx.World;
    private int Dim => W.Dimension;
    private readonly ExcavateReport _r = new();
    private HashSet<BlockPos> _home = new();
    private HashSet<BlockPos> _keepSolid = new();
    private readonly HashSet<BlockPos> _dug = new();
    private readonly HashSet<BlockPos> _pending = new();
    private readonly HashSet<BlockPos> _badStands = new();
    private readonly Dictionary<BlockPos, int> _fails = new();
    private readonly Dictionary<BlockPos, int> _refills = new();
    private int _oresAdded;
    private bool _hadPick;
    private readonly Dictionary<BlockPos, string> _oreAt = new();
    /// <summary>The last phase the dig reached: cells past it (a stair's later repeats crossing a cave) are not this run's to seal.</summary>
    private int _reached = int.MaxValue;

    public ExcavateReport Report => _r;

    /// <summary>Walking inside an excavation: 1-block drops (a stair step), no gap jumps, no doors.</summary>
    public static readonly PathOptions Walk = PathOptions.Default with { MaxDrop = 1, AllowParkour = false, AllowOpenDoors = false };

    public static readonly GotoOptions WalkGoto = GotoOptions.Default with
    {
        MaxDrop = 1, AllowPlace = false, AllowBreak = false, Parkour = ParkourMode.Off, Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<ExcavateReport> RunAsync(CancellationToken ct)
    {
        var before = Snapshot();
        W.KeepAir.Add(vol.Label, Dim, vol.Cells);
        try
        {
            _keepSolid = KeepSolid(W, vol);
            _home = Home();
            _hadPick = HasPick();
            await DigLoopAsync(ct).ConfigureAwait(false);
            if (_o.Seal)
                foreach (var c in vol.Phase.Where(kv => kv.Value <= _reached).Select(kv => kv.Key).Where(Clear))
                    foreach (var f in BlockFaceExtensions.All)
                        if (IsOpening(c.Offset(f))) _pending.Add(c.Offset(f)); // cells that were already open (a cave the box cut)
            if (_o.Seal && _pending.Count > 0) await SealAllAsync(_pending.ToList(), ct).ConfigureAwait(false);
            if (_o.Torches && _dug.Count > 0) await LightAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _r.Stop = "cancelled";
        }
        finally
        {
            if (!_o.KeepRegistered) W.KeepAir.Remove(vol.Label);
        }
        _r.Gained = Gains(before, Snapshot());
        return _r;
    }

    /// <summary>
    /// Seals an existing excavation (the stair shaft's walls): every opening on its boundary into foreign air or liquid, and
    /// every floor cell under it that is open (a missing tread), except other excavations (its rooms, their doorways), the
    /// entrance level and above (<paramref name="entranceY"/>) and the open sky. Live 10-30 02:12-02:21: four LLM rounds scanned
    /// the shaft ring by hand ("need scripted scan+fill of shaft wall ring … no script tool available to me") and still missed holes.
    /// </summary>
    public async Task<ExcavateReport> SealOpeningsAsync(int? entranceY, CancellationToken ct)
    {
        var before = Snapshot();
        try
        {
            _keepSolid = KeepSolid(W, vol);
            var cells = vol.Cells.ToHashSet();
            var openings = new HashSet<BlockPos>();
            bool Below(BlockPos n) => entranceY is not { } ey || n.Y < ey;
            foreach (var c in cells)
                foreach (var f in BlockFaceExtensions.All)
                    if (c.Offset(f) is var n && Below(n) && IsOpening(n)) openings.Add(n);
            var treads = DigPattern.Floors(cells).Where(f => Below(f) && Open(f) && !vol.Skip.Contains(f) && !W.KeepAir.Contains(f, Dim)
                                                             && Aiming.IsReplaceable(W, W.GetBlock(f))).ToList();
            ForceSeal(treads);
            openings.UnionWith(treads);
            if (openings.Count > 0) await SealAllAsync(openings, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _r.Stop = "cancelled"; }
        _r.Gained = Gains(before, Snapshot());
        return _r;
    }

    /// <summary>Torches an existing excavation's open cells only (no digging).</summary>
    public async Task<ExcavateReport> LightOnlyAsync(CancellationToken ct)
    {
        try
        {
            _home = Home();
            await LightAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _r.Stop = "cancelled"; }
        return _r;
    }

    // ------------------------------------------------------------------ the dig loop

    private async Task DigLoopAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow + _o.Budget;
        int stopPhase = int.MaxValue, consecutive = 0, tunnels = 0, litAt = 0;
        int? lastPhase = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_r.Dug >= _o.MaxDigs) { _r.Stop = $"dig cap {_o.MaxDigs} reached (run again to go on)"; return; }
            if (DateTime.UtcNow > until) { _r.Stop = $"time budget {_o.Budget.TotalMinutes:0} min used (run again to go on)"; return; }
            var todo = new List<BlockPos>();
            foreach (var (c, k) in vol.Phase)
            {
                if (k > stopPhase || _r.Left.ContainsKey(c) || Clear(c)) continue;
                if (BlockShapes.IsLiquid(W.GetBlock(c))) { _r.Left[c] = "liquid in it"; continue; }
                if (!W.IsLoaded(c.X, c.Z)) { _r.Left[c] = "not loaded"; continue; }
                todo.Add(c);
            }
            if (todo.Count == 0) return;
            int phase = vol.Ordered ? todo.Min(c => vol.Phase[c]) : int.MaxValue;
            if (vol.Ordered && lastPhase is { } lp && phase > lp && _o.Torches && _dug.Count - litAt >= 12)
            {
                await LightAsync(ct).ConfigureAwait(false); // a finished stretch of stairs is lit before going deeper
                litAt = _dug.Count;
            }
            if (vol.Ordered && phase != lastPhase && !await FloorsAsync(phase, ct).ConfigureAwait(false)) stopPhase = phase;
            lastPhase = phase;
            _reached = vol.Ordered ? phase : int.MaxValue;
            var eligible = vol.Ordered ? todo.Where(c => vol.Phase[c] == phase).ToList() : todo;
            var me = StartCell();
            var flood = Pathfinder.Explore(W, me, Walk, 4000, ct);
            var pick = Choose(eligible, flood);
            if (pick is null)
            {
                if (_o.Tunnel && tunnels < 4 && PlanTunnel(flood, eligible) is { Count: > 0 } tunnel)
                {
                    tunnels++;
                    int at = vol.Ordered ? phase : 0;
                    foreach (var c in tunnel) { vol.Add(c, at); vol.Access.Add(c); }
                    W.KeepAir.Add(vol.Label, Dim, tunnel);
                    _r.Tunnel += tunnel.Count(c => !Clear(c));
                    continue;
                }
                foreach (var c in eligible) _r.Left[c] = "no stand reaches it";
                if (vol.Ordered) stopPhase = phase;
                continue;
            }
            var (cell, stand) = pick.Value;
            if (stand != me)
            {
                var g = await ctx.Game.GotoAsync(new GoalBlock(stand), WalkGoto, ct).ConfigureAwait(false);
                if (!g.Ok)
                {
                    _badStands.Add(stand);
                    if (++consecutive >= 8) { _r.Stop = $"can't move inside the dig: {g.Message}"; return; }
                    continue;
                }
                if (StartCell() != stand) { _badStands.Add(stand); continue; }
            }
            // Fall rule: probe the floor before opening a cell onto it.
            BlockPos? hole = null;
            var floor = cell.Down();
            if (!vol.Contains(floor) && Open(floor) && !Excluded(floor))
            {
                var hazard = Hazard(floor);
                if (!(_o.Seal && Block() is not null) && hazard != null)
                {
                    _r.Left[cell] = hazard + (_o.Seal ? ", no block to seal it" : "");
                    if (vol.Ordered) stopPhase = vol.Phase[cell];
                    continue;
                }
                hole = floor;
            }
            // The pick broke mid-dig (live 10-30: 98 → 64 on one failed job): another from chests/crafting, else stop rather
            // than hand-dig rock for nothing.
            if (_hadPick && NeedsPick(W.GetBlock(cell)) && !HasPick())
            {
                await PickaxeAsync(ctx, ct).ConfigureAwait(false);
                if (!HasPick()) { _r.Stop = "no pickaxe left (the last one broke)"; return; }
            }
            var r = await ctx.Game.DigAsync(cell, ct).ConfigureAwait(false);
            if (!r.Ok)
            {
                _fails[cell] = _fails.GetValueOrDefault(cell) + 1;
                if (Permanent(r.Message) || _fails[cell] >= 2) _r.Left[cell] = Short(r.Message);
                if (++consecutive >= 8) { _r.Stop = Short(r.Message); return; }
                continue;
            }
            consecutive = 0;
            if (!Clear(cell))
            {
                // Something fell in (gravel/sand above): dig it again next round, up to 8 times.
                _r.Refills++;
                if ((_refills[cell] = _refills.GetValueOrDefault(cell) + 1) > 8) _r.Left[cell] = "keeps refilling (gravel/sand above)";
                continue;
            }
            _r.Dug++;
            _dug.Add(cell);
            if (_oreAt.Remove(cell, out var ore)) _r.Ores[ore] = _r.Ores.GetValueOrDefault(ore) + 1;
            if (_o.Ores) FollowOres(cell);
            if (hole is { } h && Open(h))
            {
                if (!await SealAsync(h, ct).ConfigureAwait(false))
                {
                    _r.Left[cell] = $"floor hole at {SkillJson.Fmt(h)} ({Hazard(h) ?? "drop 1"}) not sealed";
                    if (vol.Ordered) stopPhase = vol.Phase[cell];
                }
            }
            if (_o.Seal)
            {
                foreach (var f in BlockFaceExtensions.All)
                    if (IsOpening(cell.Offset(f))) _pending.Add(cell.Offset(f));
                await SealNearbyAsync(ct).ConfigureAwait(false);
            }
            if (!vol.Ordered && _o.Torches && _dug.Count - litAt >= 32)
            {
                await LightAsync(ct).ConfigureAwait(false);
                litAt = _dug.Count;
            }
        }
    }

    /// <summary>
    /// Before an ordered phase (a stair repeat) is walked into: cells of it that are already open (a cave crossed the column)
    /// need their floor too. Seals each open floor; false when one can't be and the drop under it is a real fall (lava, water,
    /// more than 1): the dig stops descending there.
    /// </summary>
    private async Task<bool> FloorsAsync(int phase, CancellationToken ct)
    {
        foreach (var (c, k) in vol.Phase)
        {
            if (k != phase || !Clear(c)) continue;
            var floor = c.Down();
            if (vol.Contains(floor) || !Open(floor) || Excluded(floor)) continue;
            if (_o.Seal && await SealAsync(floor, ct).ConfigureAwait(false)) continue;
            if (Hazard(floor) is { } h) { _r.Left[c] = $"{h}{(_o.Seal ? ", no block to seal it" : "")}"; return false; }
        }
        return true;
    }

    /// <summary>The exposed target with the cheapest stand that sees a face of it: higher cells first (top-down), path cost
    /// next. A stand never on, in or under the cell, never a stand that failed this run.</summary>
    private (BlockPos Cell, BlockPos Stand)? Choose(List<BlockPos> eligible, Dictionary<BlockPos, double> flood)
    {
        if (eligible.Count == 0 || flood.Count == 0) return null;
        double reach = ctx.Game.Reach - 0.25;
        int top = eligible.Max(c => c.Y);
        (BlockPos, BlockPos)? best = null;
        double bestScore = double.MaxValue;
        foreach (var t in eligible)
        {
            if (!Exposed(t)) continue;
            double tier = 3 * (top - t.Y);
            if (tier >= bestScore) continue;
            for (int dx = -4; dx <= 4; dx++)
            for (int dy = -5; dy <= 3; dy++)
            for (int dz = -4; dz <= 4; dz++)
            {
                var s = t.Offset(dx, dy, dz);
                if (!flood.TryGetValue(s, out double g) || _badStands.Contains(s)) continue;
                if (s == t || s.Up() == t || s.Down() == t) continue;
                double score = g + tier;
                if (score >= bestScore) continue;
                var eye = new Vec3d(s.X + 0.5, s.Y + PlayerPhysics.EyeHeight, s.Z + 0.5);
                if (eye.DistanceTo(t.Center) > reach + 0.9) continue;
                if (Aiming.FindDigAim(W, eye, t, reach) is null) continue;
                best = (t, s);
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>
    /// A 1×2 passage (flat, a 3-high step down, or a step up) from any cell I can walk to, to the nearest target cell: the
    /// fewest blocks dug, never a floor of another excavation, a protected, unbreakable or liquid cell, or one beside lava.
    /// </summary>
    private List<BlockPos>? PlanTunnel(Dictionary<BlockPos, double> flood, List<BlockPos> targets)
    {
        var near = new HashSet<BlockPos>();
        foreach (var t in targets) foreach (var f in BlockFaceExtensions.All) near.Add(t.Offset(f));
        var (bmin, bmax) = DigVolume.Bounds(targets.Append(ctx.Me));
        bool Inside(BlockPos p) => p.X >= bmin.X - 12 && p.X <= bmax.X + 12 && p.Y >= bmin.Y - 6 && p.Y <= bmax.Y + 6
                                   && p.Z >= bmin.Z - 12 && p.Z <= bmax.Z + 12 && p.Y > 0 && p.Y < W.WorldHeight - 2;
        var tset = targets.ToHashSet();
        // Reach a remaining target (not just any dug cell of the volume: that dug holes under finished corridors).
        bool Goal(BlockPos f) => tset.Contains(f) || tset.Contains(f.Up()) || near.Contains(f) || near.Contains(f.Up());
        var dist = new Dictionary<BlockPos, int>();
        var prev = new Dictionary<BlockPos, (BlockPos From, BlockPos[] Dig)>();
        var pq = new PriorityQueue<BlockPos, int>();
        foreach (var s in flood.Keys)
            if (Inside(s)) { dist[s] = 0; pq.Enqueue(s, 0); }
        int nodes = 0;
        while (pq.TryDequeue(out var f, out int d))
        {
            if (d > dist[f] || ++nodes > 20000) { if (nodes > 20000) break; continue; }
            if (d > 0 && Goal(f))
            {
                var path = new List<BlockPos>();
                for (var c = f; prev.TryGetValue(c, out var p); c = p.From) path.InsertRange(0, p.Dig);
                return path.Distinct().Where(c => !Clear(c)).ToList();
            }
            foreach (var o in SkillWorld.Horizontal)
            foreach (int dy in new[] { 0, -1, 1 })
            {
                var g = new BlockPos(f.X + o.X, f.Y + dy, f.Z + o.Z);
                if (!Inside(g) || dist.ContainsKey(g) && dist[g] <= d) continue;
                BlockPos[] cells = dy switch
                {
                    0 => [g, g.Up()],
                    -1 => [g.Up(2), g.Up(), g],          // step down: the next column is open 3 high
                    _ => [f.Up(2), g, g.Up()],           // step up: head room over me, then the next column
                };
                if (!BlockShapes.HasCollision(W, g.X, g.Y - 1, g.Z) || cells.Contains(g.Down())) continue;
                int cost = 0;
                bool ok = true;
                foreach (var c in cells)
                {
                    if (Clear(c)) continue;
                    if (!Diggable(c)) { ok = false; break; }
                    cost++;
                }
                if (!ok) continue;
                int nd = d + cost * 2 + 1;
                if (dist.TryGetValue(g, out int old) && old <= nd) continue;
                dist[g] = nd;
                prev[g] = (f, cells);
                pq.Enqueue(g, nd);
            }
        }
        return null;
    }

    private bool Diggable(BlockPos c)
    {
        if (!W.IsLoaded(c.X, c.Z) || c.Y <= 0 || _keepSolid.Contains(c) || W.KeepAir.Owner(c, Dim) is { } o && o != vol.Label) return false;
        int id = W.GetBlock(c);
        if (BlockShapes.IsLiquid(id) || Blocks.Get(id).Hardness < 0 || Blocks.Get(id).Interactable) return false;
        if (ctx.Protected(c)) return false;
        foreach (var f in BlockFaceExtensions.All) if (BlockShapes.IsLava(W.GetBlock(c.Offset(f)))) return false;
        return true;
    }

    /// <summary>A dug cell exposes ores: add them (and so the rest of the vein, as each is dug) while under the cap.</summary>
    private void FollowOres(BlockPos cell)
    {
        foreach (var f in BlockFaceExtensions.All)
        {
            var n = cell.Offset(f);
            if (vol.Contains(n) || _oresAdded >= _o.MaxOres || !IsOre(W.GetBlock(n)) || _keepSolid.Contains(n) || ctx.Protected(n)) continue;
            vol.Add(n, vol.Phase.GetValueOrDefault(cell));
            vol.Access.Add(n);
            _oresAdded++;
            _oreAt[n] = OreName(W.GetBlock(n));
        }
    }

    /// <summary>An ore by this version's block names ("iron_ore", "lit_redstone_ore", "emerald_ore" on 1.12.2).</summary>
    public static bool IsOre(int id) => id != 0 && Blocks.Get(id).Name.EndsWith("_ore", StringComparison.Ordinal);

    /// <summary>"iron ore", "redstone ore" (lit or not).</summary>
    public static string OreName(int id) => Blocks.Get(id).Name.Replace("lit_", "").Replace('_', ' ');

    // ------------------------------------------------------------------ sealing

    /// <summary>
    /// Air (or liquid) next to the excavation that isn't the excavation, its doorways, another dug design, the space I set out
    /// from, or the open sky: a cave/dungeon opening to fill.
    /// </summary>
    private bool IsOpening(BlockPos n)
    {
        if (vol.Contains(n) || vol.Skip.Contains(n) || _home.Contains(n) || W.KeepAir.Contains(n, Dim)) return false;
        if (!W.IsLoaded(n.X, n.Z) || n.Y <= 0 || n.Y >= W.WorldHeight - 1) return false;
        if (!Aiming.IsReplaceable(W, W.GetBlock(n))) return false;
        return !SkyOpen(W, n);
    }

    private bool Excluded(BlockPos n) => vol.Skip.Contains(n) || _home.Contains(n) || W.KeepAir.Contains(n, Dim);

    /// <summary>Seals the pending openings I can place from right here (no walking).</summary>
    private async Task SealNearbyAsync(CancellationToken ct)
    {
        foreach (var n in _pending.ToList())
        {
            if (_r.Sealed >= _o.MaxSeal) return;
            if (!IsOpening(n)) { _pending.Remove(n); continue; }
            if (PlaceAim(EyeNow(), n) is null) continue;
            if (await PlaceSealAsync(n, ct).ConfigureAwait(false)) _pending.Remove(n);
        }
    }

    /// <summary>Seals each opening from a stand that can place it (walking inside the dug space); what can't be is reported.</summary>
    public async Task<int> SealAllAsync(IReadOnlyCollection<BlockPos> openings, CancellationToken ct)
    {
        int done = 0;
        var left = new List<BlockPos>(openings);
        while (left.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (_r.Sealed >= _o.MaxSeal) { _r.Unsealed.AddRange(left.Where(IsOpening)); _r.Stop ??= $"seal cap {_o.MaxSeal} reached"; break; }
            var me = ctx.Feet;
            var n = left.OrderBy(c => c.Center.DistanceTo(me)).First();
            left.Remove(n);
            if (!IsOpening(n) && !_forceSeal.Contains(n)) continue;
            if (await SealAsync(n, ct).ConfigureAwait(false)) done++;
            else _r.Unsealed.Add(n);
        }
        _pending.Clear();
        return done;
    }

    private readonly HashSet<BlockPos> _forceSeal = new();

    /// <summary>Marks cells to seal even though they would otherwise not count as openings (a stair's missing treads).</summary>
    public void ForceSeal(IEnumerable<BlockPos> cells) => _forceSeal.UnionWith(cells);

    private async Task<bool> SealAsync(BlockPos n, CancellationToken ct)
    {
        if (!Aiming.IsReplaceable(W, W.GetBlock(n))) return true;
        if (Block() is null) return false;
        if (PlaceAim(EyeNow(), n) is null)
        {
            var flood = Pathfinder.Explore(W, StartCell(), Walk, 4000, ct);
            BlockPos? stand = null;
            double best = double.MaxValue;
            foreach (var (s, g) in flood)
            {
                if (g >= best || s == n || s.Up() == n || s.Down() == n || s.ManhattanTo(n) > 7) continue;
                if (PlaceAim(new Vec3d(s.X + 0.5, s.Y + PlayerPhysics.EyeHeight, s.Z + 0.5), n, s) is null) continue;
                stand = s;
                best = g;
            }
            if (stand is not { } st) return false;
            var g2 = await ctx.Game.GotoAsync(new GoalBlock(st), WalkGoto, ct).ConfigureAwait(false);
            if (!g2.Ok) return false;
        }
        return await PlaceSealAsync(n, ct).ConfigureAwait(false);
    }

    private async Task<bool> PlaceSealAsync(BlockPos n, CancellationToken ct)
    {
        if (Block() is not { } block) return false;
        if (W.KeepAir.Refusal(n, Dim, block) != null) return false;
        var r = await ctx.Game.PlaceAsync(n, block, ct).ConfigureAwait(false);
        if (!r.Ok) return false;
        _r.Sealed++;
        return true;
    }

    private PlaceAim? PlaceAim(Vec3d eye, BlockPos n, BlockPos? feet = null)
    {
        double reach = ctx.Game.Reach - 0.25;
        var box = AABB.ForPlayer(feet is { } f ? f.BottomCenter : ctx.Feet);
        return Aiming.FindPlaceAim(W, eye, n, reach, box) ?? (box.Intersects(AABB.Block(n.X, n.Y, n.Z)) ? null : PlaceRules.BlindAim(W, eye, n, reach));
    }

    private int? Block() => SkillWorld.BuildingBlock(ctx, _o.Block);

    // ------------------------------------------------------------------ light

    /// <summary>Torches the dug cells to at least <see cref="ExcavateOptions.LightLevel"/> (the light model's set cover, torches
    /// only inside the excavation), reaching each spot from a stand in the dug space.</summary>
    public async Task LightAsync(CancellationToken ct)
    {
        var cells = _dug.Count > 0 ? _dug : vol.Cells.Where(Clear).ToHashSet();
        if (cells.Count == 0) return;
        var (lo, hi) = DigVolume.Bounds(cells);
        var region = new LightPlan.Region(lo.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z, vol.Label);
        var dark = LightPlan.Dark(W, region, null, _o.LightLevel, out _).Where(cells.Contains).ToList();
        if (dark.Count == 0) return;
        bool Allowed(BlockPos c) => (vol.Contains(c) || _home.Contains(c)) && W.GetBlock(c) == 0;
        var plan = LightPlan.Cover(W, region, dark, null, _o.LightLevel, Allowed, ctx.Feet, 64);
        if (plan.Torches.Count == 0) return;
        if (ctx.Count(Blocks.Torch) < plan.Torches.Count && ctx.Registry.Obtain is { } obtain)
        {
            try { await obtain(ctx, Blocks.Torch, plan.Torches.Count, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { ctx.Log?.Invoke($"excavate torches: {e.Message}"); }
        }
        foreach (var spot in plan.Torches)
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Has(Blocks.Torch)) { _r.Stop ??= "out of torches"; return; }
            var c = spot.Pos;
            if (!SkillWorld.Replaceable(W, c)) continue;
            var faces = PlaceRules.AttachFaces(W, c, Blocks.Torch);
            if (faces.Count == 0) continue;
            if (EyeNow().DistanceTo(c.Center) > ctx.Game.Reach - 0.5)
            {
                var flood = Pathfinder.Explore(W, StartCell(), Walk, 4000, ct);
                var stand = flood.Where(kv => kv.Key != c && kv.Key.Up() != c
                        && new Vec3d(kv.Key.X + 0.5, kv.Key.Y + PlayerPhysics.EyeHeight, kv.Key.Z + 0.5).DistanceTo(c.Center) <= ctx.Game.Reach - 0.5)
                    .OrderBy(kv => kv.Value).Select(kv => (BlockPos?)kv.Key).FirstOrDefault();
                if (stand is not { } st || !(await ctx.Game.GotoAsync(new GoalBlock(st), WalkGoto, ct).ConfigureAwait(false)).Ok) continue;
            }
            var r = await ctx.Game.PlaceAsync(c, Blocks.Torch, faces[0], ct).ConfigureAwait(false);
            if (r.Ok && W.GetBlock(c) == Blocks.Torch) _r.Torches++;
        }
    }

    // ------------------------------------------------------------------ tools

    private static readonly Lazy<int[]> Picks = new(() => Items.All.Where(i => i.Tool == ToolType.Pickaxe).Select(i => i.Id).ToArray());

    private bool HasPick() => Picks.Value.Any(id => Items.Exists(id) && ctx.Has(id));

    private static bool NeedsPick(int id) => id != 0 && Blocks.Get(id) is { BestTool: ToolType.Pickaxe, MinHarvestLevel: >= 0 };

    /// <summary>A pickaxe through the acquisition planner (inventory, chests, crafting, bootstrap) when there's none.</summary>
    public static async Task PickaxeAsync(SkillContext ctx, CancellationToken ct)
    {
        var picks = Picks.Value.Where(Items.Exists).ToArray();
        if (picks.Length == 0 || picks.Any(id => ctx.Has(id)) || ctx.Registry.AcquireMissing is not { } acquire) return;
        try { await acquire(ctx, ExcavationOps.Spec, picks, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { ctx.Log?.Invoke($"excavate pickaxe: {e.Message}"); }
    }

    // ------------------------------------------------------------------ world helpers

    private bool Open(BlockPos p) => W.IsLoaded(p.X, p.Z) && !BlockShapes.HasCollision(W, p.X, p.Y, p.Z);

    /// <summary>Dug: air, or something without a collision box that isn't a liquid (a torch I placed).</summary>
    private bool Clear(BlockPos p)
    {
        int id = W.GetBlock(p);
        return id == 0 || !BlockShapes.IsLiquid(id) && !BlockShapes.HasCollision(W, p.X, p.Y, p.Z) && !Blocks.Get(id).Solid;
    }

    private bool Exposed(BlockPos t)
    {
        foreach (var f in BlockFaceExtensions.All)
            if (Open(t.Offset(f))) return true;
        return false;
    }

    /// <summary>What's under an open floor cell: "cave under it (drop 6)", "lava under it", "water under it"; null for a 1-block dip.</summary>
    private string? Hazard(BlockPos floor)
    {
        int drop = 0;
        for (var p = floor; p.Y > 0 && drop <= 24; p = p.Down())
        {
            int id = W.GetBlock(p);
            if (BlockShapes.IsLava(id)) return "lava under it";
            if (BlockShapes.IsWater(id)) return "water under it";
            if (BlockShapes.HasCollision(W, p.X, p.Y, p.Z)) break;
            drop++;
        }
        return drop <= 1 ? null : drop > 24 ? "a drop of 24+ under it" : $"cave under it (drop {drop})";
    }

    private BlockPos StartCell() => Pathfinder.StartCellFor(W, ctx.Feet) ?? ctx.Me;
    private Vec3d EyeNow() => W.Player.EyePosition;

    /// <summary>The open space I set out from (feet and head, 6-connected, ≤ 3000 cells within 24 of the volume): its walls are
    /// the way I came, never an opening to seal.</summary>
    private HashSet<BlockPos> Home()
    {
        var (lo, hi) = DigVolume.Bounds(vol.Cells.Append(ctx.Me));
        var seen = new HashSet<BlockPos>();
        var q = new Queue<BlockPos>();
        foreach (var s in new[] { ctx.Me, ctx.Me.Up() }) if (Open(s) && !vol.Contains(s) && seen.Add(s)) q.Enqueue(s);
        while (q.Count > 0 && seen.Count < 3000)
        {
            var c = q.Dequeue();
            foreach (var f in BlockFaceExtensions.All)
            {
                var n = c.Offset(f);
                if (n.X < lo.X - 24 || n.X > hi.X + 24 || n.Z < lo.Z - 24 || n.Z > hi.Z + 24 || n.Y < lo.Y - 24 || n.Y > hi.Y + 24) continue;
                if (vol.Contains(n) || !Open(n) || !seen.Add(n)) continue;
                q.Enqueue(n);
            }
        }
        return seen;
    }

    /// <summary>Floors of every other excavation near this one (a stair's treads): never dug, never planned through.</summary>
    public static HashSet<BlockPos> KeepSolid(WorldState w, DigVolume vol)
    {
        var keep = new HashSet<BlockPos>();
        foreach (var owner in w.KeepAir.Owners)
        {
            if (string.Equals(owner, vol.Label, StringComparison.OrdinalIgnoreCase)) continue;
            var cells = w.KeepAir.Cells(owner);
            foreach (var f in DigPattern.Floors(cells as IReadOnlySet<BlockPos> ?? cells.ToHashSet()))
                if (!vol.Contains(f)) keep.Add(f);
        }
        return keep;
    }

    /// <summary>Nothing with a collision box above <paramref name="p"/> up to the world top: open sky (the shaft's entrance).</summary>
    public static bool SkyOpen(IBlockAccess w, BlockPos p)
    {
        for (int y = p.Y + 1; y < w.WorldHeight; y++)
            if (BlockShapes.HasCollision(w, p.X, y, p.Z)) return false;
        return true;
    }

    private static bool Permanent(string m) =>
        m.Contains("protected", StringComparison.OrdinalIgnoreCase) || m.Contains("lava", StringComparison.OrdinalIgnoreCase)
        || m.Contains("unbreakable", StringComparison.OrdinalIgnoreCase) || m.Contains("can't be dug", StringComparison.OrdinalIgnoreCase)
        || m.Contains("someone is standing", StringComparison.OrdinalIgnoreCase) || m.Contains("void", StringComparison.OrdinalIgnoreCase);

    private static string Short(string m)
    {
        if (m.Contains("protected", StringComparison.OrdinalIgnoreCase)) return "protected";
        if (m.Contains("lava", StringComparison.OrdinalIgnoreCase)) return "lava behind it";
        var s = m.Replace('\n', ' ');
        return s.Length <= 60 ? s : s[..57] + "...";
    }

    private Dictionary<int, int> Snapshot()
    {
        var d = new Dictionary<int, int>();
        foreach (var s in W.Inventory.PlayerSlots())
            if (!s.IsEmpty) d[s.Id] = d.GetValueOrDefault(s.Id) + s.Count;
        return d;
    }

    private static string Gains(Dictionary<int, int> a, Dictionary<int, int> b) =>
        string.Join(", ", b.Select(kv => (kv.Key, N: kv.Value - a.GetValueOrDefault(kv.Key))).Where(x => x.N > 0)
            .OrderByDescending(x => x.N).Take(4).Select(x => $"+{x.N} {Items.DisplayName(x.Key, 0).ToLowerInvariant()}"));
}
