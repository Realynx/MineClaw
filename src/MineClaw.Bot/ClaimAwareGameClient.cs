using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot;

/// <summary>
/// Hive: the game client the tools see. Before touching a chest/furnace (by position) or digging/placing a block it takes a
/// short lease on the <see cref="IClaimBoard"/> and refuses with "busy: X is using it" when another hive bot holds it, so
/// two clones never reorganise the same chest or mine the same block. Mining's block selection is covered separately by
/// the host's <c>IsProtected</c> predicate (claimed blocks are skipped). Everything else forwards unchanged.
/// Calls without an explicit position (e.g. "nearest chest") can't be pre-claimed: skills should pass the container.
/// </summary>
public sealed class ClaimAwareGameClient(IGameClient inner, IClaimBoard claims) : IGameClient
{
    /// <summary>Lease length for one container/block action.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromSeconds(60);

    public IGameClient Inner => inner;
    public IClaimBoard Claims => claims;

    /// <summary>A refusal without a holder is an unanswered request (IPC timeout): main may still grant it late, which would
    /// leave a lease nobody releases (live 10-28: one clone ended up holding two gen cells). Undo it; a release is idempotent.</summary>
    private async Task<ActionResult> Refused(ClaimKey key, ClaimResult r)
    {
        if (r.Holder == null)
        {
            try { await claims.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* expires anyway */ }
            return ActionResult.Fail($"busy: the claim board didn't answer for {key.Kind.ToString().ToLowerInvariant()} {key.Min.X},{key.Min.Y},{key.Min.Z} ({r.Message ?? "no answer"}); try again");
        }
        return ActionResult.Fail($"busy: {r.Holder} is using {key.Kind.ToString().ToLowerInvariant()} {key.Min.X},{key.Min.Y},{key.Min.Z}");
    }

    private async Task<ActionResult> Held(ClaimKey key, Func<Task<ActionResult>> action, CancellationToken ct)
    {
        var r = await claims.TryClaimAsync(key, Ttl, null, ct).ConfigureAwait(false);
        if (!r.Ok) return await Refused(key, r).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally
        {
            try { await claims.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* lease expires anyway */ }
        }
    }

    /// <summary>Like <see cref="Held"/> for long jobs (a generator run can take minutes): the lease is renewed every half TTL.</summary>
    private async Task<ActionResult> HeldRenewing(ClaimKey key, Func<Task<ActionResult>> action, CancellationToken ct)
    {
        var r = await claims.TryClaimAsync(key, Ttl, null, ct).ConfigureAwait(false);
        if (!r.Ok) return await Refused(key, r).ConfigureAwait(false);
        using var stop = new CancellationTokenSource();
        var renew = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, Ttl.Ticks / 2)), stop.Token).ConfigureAwait(false);
                    await claims.TryClaimAsync(key, Ttl, null, stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception) { /* stopped, or the board is gone: the lease just expires */ }
        });
        try { return await action().ConfigureAwait(false); }
        finally
        {
            stop.Cancel();
            try { await renew.ConfigureAwait(false); } catch (Exception) { }
            try { await claims.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* lease expires anyway */ }
        }
    }

    private async Task<ActionResult> Chest(BlockPos? p, Func<Task<ActionResult>> action, CancellationToken ct, TimeSpan wait = default)
    {
        if (p is not { } pos) return await action().ConfigureAwait(false);
        await ApproachAsync(pos, ct).ConfigureAwait(false);
        var key = ClaimKey.Chest(ChestPairs.Canonical(inner.World, pos), inner.World.Dimension);
        if (wait <= TimeSpan.Zero) return await Held(key, action, ct).ConfigureAwait(false);
        // Live 11-03 (Story builders): "busy: maid_chan_102 is using chest 218,64,400" failed a build job outright although the
        // holder's session is a few seconds. A withdraw waits its turn (bounded), then says busy with the holder and the wait.
        var start = DateTime.UtcNow;
        string? holder = null;
        for (int seen = 0; ; seen++)
        {
            ct.ThrowIfCancellationRequested();
            if (claims.IsFree(key))
            {
                var r = await claims.TryClaimAsync(key, Ttl, null, ct).ConfigureAwait(false);
                if (r.Ok)
                {
                    try { return await action().ConfigureAwait(false); }
                    finally
                    {
                        try { await claims.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* lease expires anyway */ }
                    }
                }
                if (r.Holder == null) return await Refused(key, r).ConfigureAwait(false);
                holder = r.Holder;
            }
            else holder = claims.HolderOf(key) ?? holder;
            if (DateTime.UtcNow - start >= wait)
                return ActionResult.Fail($"busy: {holder ?? "another bot"} is using chest {pos.X},{pos.Y},{pos.Z}; waited {(DateTime.UtcNow - start).TotalSeconds:0}s");
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, 150 + 50 * seen)), ct).ConfigureAwait(false);
        }
    }

    /// <summary>How long a withdraw waits for another body's session at the chest (a few seconds each) before it says busy.</summary>
    public TimeSpan WithdrawWait { get; init; } = TimeSpan.FromSeconds(12);

    private async Task<ActionResult> Furnace(BlockPos? p, Func<Task<ActionResult>> action, CancellationToken ct)
    {
        if (p is not { } pos) return await action().ConfigureAwait(false);
        await ApproachAsync(pos, ct).ConfigureAwait(false);
        return await Held(ClaimKey.Furnace(pos, inner.World.Dimension), action, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The walk to a container happens BEFORE its lease: the lease covers only the open and the clicks (a few seconds), not a
    /// minute of walking. Live 10-29: 20 bodies serialised on one log chest ("busy: maid_chan_111 is using chest") because each
    /// held it from wherever it stood. A failed walk is the action's own business (it paths again and says why).
    /// </summary>
    private async Task ApproachAsync(BlockPos pos, CancellationToken ct)
    {
        var me = inner.World.Player.Position;
        if (me.DistanceTo(pos.Center) <= 4.5) return;
        try { await inner.GotoAsync(new GoalNear(pos.X, pos.Y, pos.Z, 3), null, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* the action reports it */ }
    }

    /// <summary>How many of our bodies may deposit into one chest at once (vanilla lets several players use one chest; the
    /// server arbitrates the slots, each click is verified). More would only crowd the chest's standing spots.</summary>
    public int DepositSlots { get; init; } = 4;

    /// <summary>How long a deposit queues for a free slot (or for an exclusive user such as an organiser) before it reports busy.</summary>
    public TimeSpan DepositWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a deposit waits for an exclusive holder of the chest (not another depositor).</summary>
    public TimeSpan ExclusiveWait { get; init; } = TimeSpan.FromSeconds(3);

    private static ClaimKey DepositSlot(BlockPos p, int dim, int i) => ClaimKey.Task($"deposit:{p.X},{p.Y},{p.Z}@{dim}#{i}");

    /// <summary>
    /// A deposit takes one of <see cref="DepositSlots"/> shared slot leases instead of the chest's exclusive one, so bodies
    /// put things away side by side; it still waits for an exclusive user (withdraw/organise holds the chest key). Fair enough
    /// for a crew: first free slot wins, a short poll, then one typed "busy" line so the store can try another chest or place one.
    /// </summary>
    private async Task<ActionResult> DepositHeld(BlockPos pos, Func<Task<ActionResult>> action, CancellationToken ct)
    {
        await ApproachAsync(pos, ct).ConfigureAwait(false);
        int dim = inner.World.Dimension;
        // A double chest is one window whichever half was named: its leases and deposit slots are its west/north half's, so
        // bodies sent to the east half share the same DepositSlots (was: 4 per half, 8 crowding one window).
        var at = ChestPairs.Canonical(inner.World, pos);
        var chestKey = ClaimKey.Chest(at, dim);
        var start = DateTime.UtcNow;
        var deadline = start + DepositWait;
        string? lastHolder = null;
        int seen = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // An exclusive user (an organiser, a withdraw) gets a short wait only: those leases are seconds now, and the store
            // tries the next chest (or places one) rather than queueing behind something that isn't a deposit.
            if (!claims.IsFree(chestKey) && DateTime.UtcNow - start >= ExclusiveWait) deadline = DateTime.UtcNow;
            if (claims.IsFree(chestKey))
                for (int i = 0; i < Math.Max(1, DepositSlots); i++)
                {
                    var key = DepositSlot(at, dim, i);
                    if (!claims.IsFree(key)) { lastHolder = claims.HolderOf(key) ?? lastHolder; continue; }
                    var r = await claims.TryClaimAsync(key, Ttl, "deposit", ct).ConfigureAwait(false);
                    if (!r.Ok)
                    {
                        if (r.Holder == null) { try { await claims.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { } }
                        else lastHolder = r.Holder;
                        continue;
                    }
                    try { return await action().ConfigureAwait(false); }
                    finally
                    {
                        try { await claims.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* expires */ }
                    }
                }
            else lastHolder = claims.HolderOf(chestKey) ?? lastHolder;
            seen++;
            if (DateTime.UtcNow >= deadline)
                return ActionResult.Fail(claims.IsFree(chestKey)
                    ? $"busy: chest {pos.X},{pos.Y},{pos.Z} has {DepositSlots} bodies depositing (last {lastHolder ?? "?"}); waited {(DateTime.UtcNow - start).TotalSeconds:0}s"
                    : $"busy: {lastHolder ?? "another bot"} is using chest {pos.X},{pos.Y},{pos.Z}; waited {(DateTime.UtcNow - start).TotalSeconds:0}s");
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, 150 + 50 * seen)), ct).ConfigureAwait(false);
        }
    }

    // ---- claimed actions ----
    public Task<ActionResult> OpenContainerAsync(int x, int y, int z, CancellationToken ct = default) =>
        Chest(new BlockPos(x, y, z), () => inner.OpenContainerAsync(x, y, z, ct), ct);
    public Task<ActionResult> DepositAllAsync(BlockPos? container = null, Func<ItemStack, bool>? filter = null, CancellationToken ct = default) =>
        container is { } pos ? DepositHeld(pos, () => inner.DepositAllAsync(container, filter, ct), ct) : inner.DepositAllAsync(container, filter, ct);
    public Task<ActionResult> WithdrawAsync(int itemId, int count, BlockPos? container = null, int? damage = null, CancellationToken ct = default) =>
        Chest(container, () => inner.WithdrawAsync(itemId, count, container, damage, ct), ct, WithdrawWait);
    public Task<ActionResult> SmeltAsync(int inputItemId, int count, int? fuelItemId = null, bool wait = false, BlockPos? furnace = null, CancellationToken ct = default) =>
        Furnace(furnace, () => inner.SmeltAsync(inputItemId, count, fuelItemId, wait, furnace, ct), ct);
    public Task<ActionResult> CollectFurnaceAsync(BlockPos? furnace = null, CancellationToken ct = default) =>
        Furnace(furnace, () => inner.CollectFurnaceAsync(furnace, ct), ct);
    public Task<ActionResult> DigBlockAsync(int x, int y, int z, CancellationToken ct = default) =>
        Held(ClaimKey.Block(x, y, z, inner.World.Dimension), () => inner.DigBlockAsync(x, y, z, ct), ct);
    public Task<ActionResult> PlaceBlockAsync(int x, int y, int z, int itemId, CancellationToken ct = default) =>
        Held(ClaimKey.Block(x, y, z, inner.World.Dimension), () => inner.PlaceBlockAsync(x, y, z, itemId, ct), ct);
    public Task<ActionResult> PlaceVariantAsync(int x, int y, int z, int itemId, int? damage, BlockFace? face, CancellationToken ct = default) =>
        Held(ClaimKey.Block(x, y, z, inner.World.Dimension), () => inner.PlaceVariantAsync(x, y, z, itemId, damage, face, ct), ct);
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, CancellationToken ct = default) =>
        GenHeld(new BlockPos(x, y, z), p => inner.HarvestGeneratorAsync(p.X, p.Y, p.Z, count, ct), ct);
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, BlockPos? stand, CancellationToken ct = default) =>
        GenHeld(new BlockPos(x, y, z), p => inner.HarvestGeneratorAsync(p.X, p.Y, p.Z, count, p == new BlockPos(x, y, z) ? stand : null, ct), ct);
    /// <summary>Hive: one cell per bot (the leased one). Rotating through unleased cells would put two bots on one cell
    /// (live 21:44 "my clone partner was using the cell at 4,64,6 at the same time"); the other cells are for the other bots.</summary>
    public Task<ActionResult> HarvestGeneratorAsync(IReadOnlyList<BlockPos> cells, int count, BlockPos? stand, CancellationToken ct = default)
    {
        if (cells.Count == 0) return Task.FromResult(ActionResult.Fail("no generator cell given"));
        var first = cells[0];
        return GenHeld(first, p => inner.HarvestGeneratorAsync([p], count, p == first ? stand : null, ct), ct);
    }

    private readonly object _genLock = new();
    private BlockPos? _genCell;

    /// <summary>The generator cell this bot holds right now (its one cell), or null.</summary>
    public BlockPos? GenCell { get { lock (_genLock) return _genCell; } }

    /// <summary>
    /// At most ONE gen cell per bot (live 10-28 02:07: 1337 held 4,64,6 and 5,64,7, main's job found no free cell and 67 mined
    /// 0 in 3 min). A gen run nested inside another one (re-tooling mines cobble for a new pickaxe) digs the cell already held
    /// instead of leasing a second; a new run on another cell starts only after the previous lease is released.
    /// </summary>
    private async Task<ActionResult> GenHeld(BlockPos cell, Func<BlockPos, Task<ActionResult>> run, CancellationToken ct)
    {
        BlockPos? held;
        lock (_genLock) held = _genCell;
        if (held is { } h) return await run(h).ConfigureAwait(false); // nested: same bot, same cell, lease already renewing
        lock (_genLock) _genCell = cell;
        try { return await HeldRenewing(ClaimKey.Block(cell.X, cell.Y, cell.Z, inner.World.Dimension), () => run(cell), ct).ConfigureAwait(false); }
        finally { lock (_genLock) if (_genCell == cell) _genCell = null; }
    }

    // ---- forwarded ----
    public WorldState World => inner.World;
    public string Username => inner.Username;
    public bool IsPlaying => inner.IsPlaying;
    public bool VitalsKnown => inner.VitalsKnown;
    public bool GroundKnown => inner.GroundKnown;
    public string? CurrentAction => inner.CurrentAction;
    public double StationSearchRadius => inner.StationSearchRadius;
    public Func<BlockPos, int, bool>? IsProtected { get => inner.IsProtected; set => inner.IsProtected = value; }
    public IReadOnlySet<int> ReservedItems { get => inner.ReservedItems; set => inner.ReservedItems = value; }
    public IReadOnlySet<int> EatReservedItems => inner.EatReservedItems;

    public event Action<string>? ChatReceived { add => inner.ChatReceived += value; remove => inner.ChatReceived -= value; }
    public event Action<string>? Kicked { add => inner.Kicked += value; remove => inner.Kicked -= value; }
    public event Action<string>? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
    public event Action? Spawned { add => inner.Spawned += value; remove => inner.Spawned -= value; }
    public event Action? Died { add => inner.Died += value; remove => inner.Died -= value; }
    public event Action? Respawned { add => inner.Respawned += value; remove => inner.Respawned -= value; }
    public event Action<Vec3d, Vec3d>? ServerMoved { add => inner.ServerMoved += value; remove => inner.ServerMoved -= value; }
    public event Action<int, int>? HealthChanged { add => inner.HealthChanged += value; remove => inner.HealthChanged -= value; }
    public event Action<string>? PlayerJoined { add => inner.PlayerJoined += value; remove => inner.PlayerJoined -= value; }
    public event Action<string>? PlayerLeft { add => inner.PlayerLeft += value; remove => inner.PlayerLeft -= value; }
    public event Action<DangerEvent>? Danger { add => inner.Danger += value; remove => inner.Danger -= value; }
    public event Action<int, int>? EntityAnimated { add => inner.EntityAnimated += value; remove => inner.EntityAnimated -= value; }
    public event Action<int, int, ItemStack?>? ItemCollected { add => inner.ItemCollected += value; remove => inner.ItemCollected -= value; }
    public event Action<int, int, int, int>? EquipmentChanged { add => inner.EquipmentChanged += value; remove => inner.EquipmentChanged -= value; }
    public event Action<int, int>? EntityStatus { add => inner.EntityStatus += value; remove => inner.EntityStatus -= value; }
    public event Action<int, BlockPos, int>? BlockBreakProgress { add => inner.BlockBreakProgress += value; remove => inner.BlockBreakProgress -= value; }
    public event Action<BlockPos, int, int>? BlockAction { add => inner.BlockAction += value; remove => inner.BlockAction -= value; }

    public string DescribeStatus() => inner.DescribeStatus();
    public string DescribeInventory(bool detail = false, int maxChars = 600) => inner.DescribeInventory(detail, maxChars);
    public string DescribeSurroundings(double radius = 12, bool detail = false, int maxChars = 600) => inner.DescribeSurroundings(radius, detail, maxChars);
    public string DescribeOpenWindow(int maxChars = 600) => inner.DescribeOpenWindow(maxChars);

    public Task<ActionResult> SendChatAsync(string text, CancellationToken ct = default) => inner.SendChatAsync(text, ct);
    public Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options = null, CancellationToken ct = default) => inner.GotoAsync(goal, options, ct);
    public Task<ActionResult> FollowPlayerAsync(string name, double distance = 3, CancellationToken ct = default) => inner.FollowPlayerAsync(name, distance, ct);
    public Task<ActionResult> StopAsync(CancellationToken ct = default) => inner.StopAsync(ct);
    public Task<ActionResult> MineAsync(string blockName, int count, double radius = 16, CancellationToken ct = default) => inner.MineAsync(blockName, count, radius, ct);
    public Task<ActionResult> CollectDropsAsync(double radius = 8, CancellationToken ct = default, bool dryOnly = false) => inner.CollectDropsAsync(radius, ct, dryOnly);
    public Task<ActionResult> CraftAsync(int itemId, int damage = -1, int count = 1, CancellationToken ct = default) => inner.CraftAsync(itemId, damage, count, ct);
    public Task<ActionResult> ClickSlotAsync(int slot, bool rightClick = false, bool shift = false, CancellationToken ct = default) => inner.ClickSlotAsync(slot, rightClick, shift, ct);
    public Task<ActionResult> CloseWindowAsync(CancellationToken ct = default) => inner.CloseWindowAsync(ct);
    public Task<ActionResult> ActivateBlockAsync(int x, int y, int z, CancellationToken ct = default) => inner.ActivateBlockAsync(x, y, z, ct);
    public Task<ActionResult> UseItemOnBlockAsync(BlockPos pos, BlockFace face, int itemId, int? damage = null, CancellationToken ct = default) =>
        inner.UseItemOnBlockAsync(pos, face, itemId, damage, ct);
    public Task<ActionResult> FishAsync(Vec3d target, TimeSpan session, int maxCatches = 64, CancellationToken ct = default) =>
        inner.FishAsync(target, session, maxCatches, ct);
    public Task<ActionResult> EatAsync(CancellationToken ct = default) => inner.EatAsync(ct);
    public bool FaceIdle(Vec3d point) => inner.FaceIdle(point);
    public bool SwingIdle() => inner.SwingIdle();
    public bool SafetyEngaged => inner.SafetyEngaged;
    public bool Brave => inner.Brave;
    public bool PlayGesture(GesturePlan plan) => inner.PlayGesture(plan);
    public void CancelGesture() => inner.CancelGesture();
    public string? CurrentGesture => inner.CurrentGesture;
    public Task<ActionResult> GestureAsync(string move, string? target = null, int? times = null, CancellationToken ct = default) =>
        inner.GestureAsync(move, target, times, ct);
    public Task<ActionResult> StepIntoVoidAsync(string reason, bool keepItemsOk = false, CancellationToken ct = default) =>
        inner.StepIntoVoidAsync(reason, keepItemsOk, ct);
    public IntentionalVoidStep? ActiveVoidStep => inner.ActiveVoidStep;
}
