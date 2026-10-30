using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// The no-path danger reflex and spawn safety (zero tokens; live 10-28 16:20: a restart woke main on the drop tower's platform at
/// night, seven zombies came, "disengaging inward" / "retreating inward to eat" had nowhere to go and it died there).
/// <list type="bullet">
/// <item>When a retreat has no way home (no retreat cell, or no walk down to the island's level: <see cref="WayHome"/>) and
///   health is low or the mobs outnumber the body, it boxes its own 1×2 column in from where it stands (<see cref="Shelter"/>:
///   cobble, planks, dirt last, never saplings; never digs its floor, never moves), eats, and waits until hp ≥
///   <see cref="SurvivalOptions.ShelterUntilHealth"/> and the mobs are gone or it's day; then opens the side toward home. One
///   line for the brain: "boxed myself in at …: 7 zombies, hp 4; waiting it out". With no blocks it fights on as before.</item>
/// <item>At login/respawn an exposed body (night or mobs near, and away from home level or with no walk home) first walks home,
///   else boxes in, before the startup wake asks the brain anything (<see cref="SpawnSafety"/>).</item>
/// </list>
/// </summary>
public sealed partial class MinecraftClient
{
    private int _sheltering;
    private (BlockPos Feet, long Tick, bool Stranded) _strandedCache = (default, long.MinValue, false);

    /// <summary>The safety check of the last login/respawn ("walked home from …" / "boxed myself in …"); null = nothing needed.
    /// The agent awaits it before its startup wake.</summary>
    public Task<string?> SpawnSafety { get; private set; } = Task.FromResult<string?>(null);

    /// <summary>The island's level for "a walk home": the world spawn (the skyblock island).</summary>
    private BlockPos? HomeLevel => World.Spawn == default ? null : World.Spawn;

    /// <summary>Up somewhere with no walk down to the island's level (cached per foot cell for 3 s; a short search on the tick thread).</summary>
    public bool StrandedNow()
    {
        if (HomeLevel is not { } home) return false;
        var feet = FeetCell();
        var c = _strandedCache;
        if (c.Feet == feet && TickCount - c.Tick < 60) return c.Stranded;
        bool stranded = !WayHome.AtHome(feet, home)
                        && WayHome.WalkDown(World, feet, home, WayHome.Walk with { MaxNodes = 2500, Timeout = TimeSpan.FromMilliseconds(40) }) == null;
        _strandedCache = (feet, TickCount, stranded);
        return stranded;
    }

    /// <summary>Blocks on hand the box may use (count of the best-ranked ones and their id).</summary>
    private (int Id, int Count) ShelterBlock()
    {
        int best = -1, bestRank = int.MaxValue;
        var slots = World.Inventory.PlayerSlots();
        for (int i = 9; i < 45 && i < slots.Length; i++)
        {
            var st = slots[i];
            if (st.IsEmpty) continue;
            int r = Shelter.BlockRank(st.Id);
            if (r < 0 || ReservedItems.Contains(st.Id)) continue;
            if (r < bestRank) { bestRank = r; best = st.Id; }
        }
        return best < 0 ? (0, 0) : (best, World.Inventory.CountItem(best));
    }

    private int ShelterBlocksTotal()
    {
        int n = 0;
        var slots = World.Inventory.PlayerSlots();
        for (int i = 9; i < 45 && i < slots.Length; i++)
            if (!slots[i].IsEmpty && Shelter.BlockRank(slots[i].Id) >= 0 && !ReservedItems.Contains(slots[i].Id)) n += slots[i].Count;
        return n;
    }

    /// <summary>The combat engine's question when a fight turns into a retreat: box in? (blocks on hand, low hp or outnumbered,
    /// and no retreat cell or no walk home).</summary>
    private bool ShouldShelter(int threats, bool retreatFound)
    {
        var o = Options.Survival;
        if (!o.Enabled || !o.ShelterEnabled || Volatile.Read(ref _sheltering) == 1 || ActiveVoidStep is not null) return false;
        if (!EnoughToBoxIn()) return false;
        bool low = World.Player.Health <= o.LowHealth;
        if (!low && threats < o.ShelterThreats) return false;
        if (Shelter.Enclosed(World, FeetCell())) return true;
        return !retreatFound || StrandedNow();
    }

    /// <summary>The combat engine's question when a 1.9+ melee fight can't be won: could the body box in now (blocks on hand)?</summary>
    private bool CanBoxIn(int threats)
    {
        var o = Options.Survival;
        return o.Enabled && o.ShelterEnabled && Volatile.Read(ref _sheltering) == 0 && ActiveVoidStep is null && EnoughToBoxIn();
    }

    /// <summary>
    /// Blocks on hand for every open cell of the box here: a half-built box is no cover and only litters the ground (live 10-30:
    /// bodies with 2-3 planks started boxes they couldn't close, fought on beside them and left the stubs behind).
    /// </summary>
    private bool EnoughToBoxIn()
    {
        int have = ShelterBlocksTotal();
        return have > 0 && have >= BlocksToBoxIn(FeetCell());
    }

    /// <summary>Blocks the box here takes: its plan's cells still to fill (open sides, roof and any block they need to stand on).</summary>
    private int BlocksToBoxIn(BlockPos feet) =>
        Shelter.Enclosed(World, feet) ? 0 : Shelter.Plan(World, feet).Count(p => Aiming.IsReplaceable(World, World.GetBlock(p.Target)));

    /// <summary>From the tick thread: start the box (once at a time).</summary>
    private void StartShelter(string why)
    {
        if (Interlocked.CompareExchange(ref _sheltering, 1, 0) != 0) return;
        Combat.Sheltering = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await ShelterAsync(why, CancellationToken.None, alreadyMarked: true).ConfigureAwait(false);
                Log($"survival: shelter {r}");
            }
            catch (Exception ex) { Log("survival: shelter failed: " + ex.Message); }
        });
    }

    /// <summary>
    /// Boxes the body in where it stands, eats, waits it out, then opens the side toward home. Fails (and fights on) with no
    /// blocks. <paramref name="why"/> names the trigger for the log.
    /// </summary>
    public async Task<ActionResult> ShelterAsync(string why, CancellationToken ct = default, bool alreadyMarked = false, Action<string>? boxed = null)
    {
        if (!alreadyMarked && Interlocked.CompareExchange(ref _sheltering, 1, 0) != 0) return ActionResult.Fail("already boxed in");
        Combat.Sheltering = true;
        try
        {
            return await RunActionAsync("shelter", ct, async c =>
            {
                var feet = FeetCell();
                var placed = new List<BlockPos>();
                int need = BlocksToBoxIn(feet), have = ShelterBlocksTotal();
                if (have < need || have == 0 && need > 0)
                    return ActionResult.Fail(have == 0 ? "no blocks to box myself in with" : $"only {have} block(s) for a box that takes {need}: not starting one");
                for (int pass = 0; pass < 3 && !Shelter.Enclosed(World, feet); pass++)
                {
                    foreach (var p in Shelter.Plan(World, feet))
                    {
                        var (id, n) = ShelterBlock();
                        if (n == 0) break;
                        if (!Aiming.IsReplaceable(World, World.GetBlock(p.Target))) continue;
                        var r = await PlaceAgainstCoreAsync(p.Target, p.Face, id, null, null, allowHidden: true, c).ConfigureAwait(false);
                        if (r.Ok) placed.Add(p.Target);
                        else Log($"survival: shelter block at {Fmt(p.Target)}: {r.Message}");
                    }
                }
                var mobs = World.Entities.Nearby(World.Player.Position, 12, CombatRules.IsThreat);
                bool closed = Shelter.Enclosed(World, feet);
                if (placed.Count == 0 && !closed)
                    return ActionResult.Fail(ShelterBlocksTotal() == 0 ? "no blocks to box myself in with" : $"couldn't box myself in at {Fmt(feet)}");
                if (!closed && ShelterBlocksTotal() == 0)
                {
                    // Ran out part way (a placement failed, a block went to something else): no cover, so take the stub back down.
                    var back = await ClearShelterAsync(feet, placed, c).ConfigureAwait(false);
                    return ActionResult.Fail($"ran out of blocks boxing in at {Fmt(feet)} ({Shelter.Open(World, feet).Count} side(s) open){back}");
                }
                var line = $"boxed myself in at {Fmt(feet)}: {MobCount(mobs)}, hp {World.Player.Health}; waiting it out"
                           + (closed ? "" : $" ({Shelter.Open(World, feet).Count} side(s) still open)");
                Log("survival: " + line + $" ({why})");
                Raise(() => CombatEventRaised?.Invoke(new CombatEvent(CombatEventKind.Critical, line)));
                try { boxed?.Invoke(line); } catch (Exception) { }

                var o = Options.Survival;
                var until = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Max(0.05, o.ShelterMaxMinutes));
                while (DateTime.UtcNow < until)
                {
                    var p = World.Player;
                    if (SurvivalReflex.EatNow(World.Inventory.PlayerSlots(), EatReserved(p.Health), p.Health, p.Food, o) is { } meal)
                    {
                        var eat = await UseCoreAsync(meal.ItemId, c).ConfigureAwait(false);
                        if (eat.Ok) Interlocked.Increment(ref _autoMeals);
                    }
                    bool quiet = NearestThreat(p.Position, 16) is null || !World.IsNight;
                    if (p.Health >= o.ShelterUntilHealth && quiet) break;
                    await Task.Delay(500, c).ConfigureAwait(false);
                }
                // Safe now: the whole box comes down. Timed out with mobs still about: only the way out opens (taking it all down would
                // only box in again), and the rest is cleared once the body is idle by it and nothing can get at it.
                bool safe = NearestThreat(World.Player.Position, 16) is null;
                var open = safe ? await ClearShelterAsync(feet, placed, c).ConfigureAwait(false) : await OpenShelterAsync(feet, placed, c).ConfigureAwait(false);
                return ActionResult.Success($"waited it out boxed in at {Fmt(feet)} (hp {World.Player.Health}){open}");
            }).ConfigureAwait(false);
        }
        finally
        {
            Combat.Sheltering = false;
            Volatile.Write(ref _sheltering, 0);
        }
    }

    private readonly List<(BlockPos Feet, List<BlockPos> Blocks, long Tick)> _shelterLeft = new();
    private int _cleaning;

    /// <summary>A box left standing within 5 of <paramref name="me"/> (forgotten after 30 min: someone else may use it by then).</summary>
    private bool ShelterLeftovers(Vec3d me)
    {
        lock (_shelterLeft)
        {
            _shelterLeft.RemoveAll(b => TickCount - b.Tick > 20 * 60 * 30 || b.Blocks.Count == 0);
            return _shelterLeft.Any(b => b.Feet.Center.DistanceTo(me) <= 5);
        }
    }

    private void StartShelterCleanup()
    {
        if (Interlocked.CompareExchange(ref _cleaning, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                (BlockPos Feet, List<BlockPos> Blocks, long Tick) box;
                var me = World.Player.Position;
                lock (_shelterLeft)
                {
                    var i = _shelterLeft.FindIndex(b => b.Feet.Center.DistanceTo(me) <= 5);
                    if (i < 0) return;
                    box = _shelterLeft[i];
                    _shelterLeft.RemoveAt(i);
                }
                var r = await RunActionAsync("clear shelter", CancellationToken.None, async c =>
                    ActionResult.Success("cleared the old box" + await ClearShelterAsync(box.Feet, box.Blocks, c).ConfigureAwait(false))).ConfigureAwait(false);
                Log($"survival: {r.Message}");
            }
            catch (Exception ex) { Log("survival: clearing an old box failed: " + ex.Message); }
            finally { Volatile.Write(ref _cleaning, 0); }
        });
    }

    /// <summary>
    /// Takes the box down: the side toward home first (the way out), then every other block it placed (top first), picking them
    /// back up. Only its own blocks; one it can't dig is reported and left (live 10-30: Fox asked for the boxes to be cleaned up).
    /// </summary>
    private async Task<string> ClearShelterAsync(BlockPos feet, List<BlockPos> placed, CancellationToken c)
    {
        if (placed.Count == 0) return "";
        var home = HomeLevel ?? feet;
        var side = new[] { BlockFace.North, BlockFace.East, BlockFace.South, BlockFace.West }
            .Select(f => f.Normal())
            .Where(d => placed.Contains(feet + d) || placed.Contains(feet.Up() + d))
            .OrderBy(d => (feet + d).DistanceSquaredTo(home)).Cast<BlockPos?>().FirstOrDefault();
        var order = placed.Distinct()
            .OrderBy(p => side is { } s && (p == feet.Up() + s || p == feet + s) ? 0 : 1)
            .ThenByDescending(p => p.Y).ToList();
        int dug = 0;
        var left = new List<BlockPos>();
        foreach (var p in order)
        {
            if (!BlockShapes.HasCollision(World, p.X, p.Y, p.Z)) continue;
            var r = await DigCoreAsync(p, c, allowPathing: false).ConfigureAwait(false);
            if (r.Ok) dug++;
            else left.Add(p);
        }
        return $"; took the box down ({dug} block(s) back)" + (left.Count > 0 ? $", {left.Count} left at {string.Join(" ", left.Take(3).Select(Fmt))}" : "");
    }

    /// <summary>Digs the side of the box toward home (only blocks it placed), so the next walk can leave; the rest is remembered for
    /// <see cref="StartShelterCleanup"/>.</summary>
    private async Task<string> OpenShelterAsync(BlockPos feet, List<BlockPos> placed, CancellationToken c)
    {
        var home = HomeLevel ?? feet;
        var sides = new[] { BlockFace.North, BlockFace.East, BlockFace.South, BlockFace.West }
            .Select(f => f.Normal())
            .Where(d => placed.Contains(feet + d) || placed.Contains(feet.Up() + d))
            .OrderBy(d => (feet + d).DistanceSquaredTo(home)).ToList();
        string said = "";
        if (sides.Count > 0)
        {
            var side = sides[0];
            said = $"; opened the side at {Fmt(feet + side)}";
            foreach (var p in new[] { feet.Up() + side, feet + side })
            {
                if (!placed.Contains(p) || !BlockShapes.HasCollision(World, p.X, p.Y, p.Z)) continue;
                var r = await DigCoreAsync(p, c, allowPathing: false).ConfigureAwait(false);
                if (!r.Ok) { said = $"; couldn't open the box: {r.Message}"; break; }
            }
        }
        var rest = placed.Where(p => BlockShapes.HasCollision(World, p.X, p.Y, p.Z)).ToList();
        if (rest.Count > 0) lock (_shelterLeft) _shelterLeft.Add((feet, rest, TickCount));
        return said + (rest.Count > 0 ? $" ({rest.Count} block(s) to clear once it's safe)" : "");
    }

    private static string MobCount(IReadOnlyList<Entity> mobs)
    {
        if (mobs.Count == 0) return "no mobs in sight";
        return string.Join(", ", mobs.GroupBy(m => (m.DisplayName ?? "mob").ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .Select(g => g.Count() == 1 ? $"1 {g.Key}" : $"{g.Count()} {g.Key}{(g.Key.EndsWith('s') ? "" : "s")}"));
    }

    // ------------------------------------------------------------------ spawn safety

    /// <summary>
    /// After a login/respawn: exposed (night or a hostile within 16) and away from home level or with no walk home → walk home,
    /// else box in. Runs once the ground under the body is loaded; the agent's startup wake awaits it.
    /// </summary>
    private void StartSpawnSafety()
    {
        if (!Options.Survival.Enabled || !Options.Survival.SecureOnSpawn) return;
        SpawnSafety = Task.Run(async () =>
        {
            try { return await SecureAfterSpawnAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log("survival: spawn safety failed: " + ex.Message); return null; }
        });
    }

    /// <summary>The spawn check itself (public for tests): null = safe as it is, else what it did.</summary>
    public async Task<string?> SecureAfterSpawnAsync(CancellationToken ct)
    {
        // The chunks under the body arrive just after the login.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < until && !(IsPlaying && Pathfinder.StartCellFor(World, World.Player.Position) is not null))
            await Task.Delay(100, ct).ConfigureAwait(false);
        if (!IsPlaying || World.Dimension != 0 || HomeLevel is not { } home) return null;
        var feet = FeetCell();
        bool exposed = World.IsNight || NearestThreat(16) is not null;
        if (!exposed || WayHome.AtHome(feet, home)) return null;
        if (WayHome.WalkDown(World, feet, home) is { } down)
        {
            var g = await GotoAsync(new GoalBlock(down), null, ct).ConfigureAwait(false);
            var line = g.Ok ? $"woke up at {Fmt(feet)} ({(World.IsNight ? "night" : "mobs near")}): walked down to {Fmt(down)} first"
                : $"woke up at {Fmt(feet)} ({(World.IsNight ? "night" : "mobs near")}): couldn't walk down ({g.Message})";
            Log("survival: " + line);
            if (g.Ok) return line;
        }
        if (ShelterBlocksTotal() == 0) return $"woke up stranded at {Fmt(feet)} at {(World.IsNight ? "night" : "with mobs near")}: no way down and no blocks to box in with";
        // The answer is due once the box is up (the wait inside it goes on holding the body).
        var up = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ShelterAsync("spawned exposed with no way home", ct, boxed: l => up.TrySetResult(l));
        var first = await Task.WhenAny(up.Task, run).ConfigureAwait(false);
        var what = first == up.Task ? await up.Task.ConfigureAwait(false) : (await run.ConfigureAwait(false)).Message;
        return $"woke up stranded at {Fmt(feet)} ({(World.IsNight ? "night" : "mobs near")}, no way down): {what}";
    }
}
