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

    private async Task<ActionResult> Held(ClaimKey key, Func<Task<ActionResult>> action, CancellationToken ct)
    {
        var r = await claims.TryClaimAsync(key, Ttl, null, ct).ConfigureAwait(false);
        if (!r.Ok) return ActionResult.Fail($"busy: {r.Holder ?? "another bot"} is using {key.Kind.ToString().ToLowerInvariant()} {key.Min.X},{key.Min.Y},{key.Min.Z}");
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
        if (!r.Ok) return ActionResult.Fail($"busy: {r.Holder ?? "another bot"} is using {key.Kind.ToString().ToLowerInvariant()} {key.Min.X},{key.Min.Y},{key.Min.Z}");
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

    private Task<ActionResult> Chest(BlockPos? p, Func<Task<ActionResult>> action, CancellationToken ct) =>
        p is { } pos ? Held(ClaimKey.Chest(pos, inner.World.Dimension), action, ct) : action();

    private Task<ActionResult> Furnace(BlockPos? p, Func<Task<ActionResult>> action, CancellationToken ct) =>
        p is { } pos ? Held(ClaimKey.Furnace(pos, inner.World.Dimension), action, ct) : action();

    // ---- claimed actions ----
    public Task<ActionResult> OpenContainerAsync(int x, int y, int z, CancellationToken ct = default) =>
        Chest(new BlockPos(x, y, z), () => inner.OpenContainerAsync(x, y, z, ct), ct);
    public Task<ActionResult> DepositAllAsync(BlockPos? container = null, Func<ItemStack, bool>? filter = null, CancellationToken ct = default) =>
        Chest(container, () => inner.DepositAllAsync(container, filter, ct), ct);
    public Task<ActionResult> WithdrawAsync(int itemId, int count, BlockPos? container = null, int? damage = null, CancellationToken ct = default) =>
        Chest(container, () => inner.WithdrawAsync(itemId, count, container, damage, ct), ct);
    public Task<ActionResult> SmeltAsync(int inputItemId, int count, int? fuelItemId = null, bool wait = false, BlockPos? furnace = null, CancellationToken ct = default) =>
        Furnace(furnace, () => inner.SmeltAsync(inputItemId, count, fuelItemId, wait, furnace, ct), ct);
    public Task<ActionResult> CollectFurnaceAsync(BlockPos? furnace = null, CancellationToken ct = default) =>
        Furnace(furnace, () => inner.CollectFurnaceAsync(furnace, ct), ct);
    public Task<ActionResult> DigBlockAsync(int x, int y, int z, CancellationToken ct = default) =>
        Held(ClaimKey.Block(x, y, z, inner.World.Dimension), () => inner.DigBlockAsync(x, y, z, ct), ct);
    public Task<ActionResult> PlaceBlockAsync(int x, int y, int z, int itemId, CancellationToken ct = default) =>
        Held(ClaimKey.Block(x, y, z, inner.World.Dimension), () => inner.PlaceBlockAsync(x, y, z, itemId, ct), ct);
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, CancellationToken ct = default) =>
        HeldRenewing(ClaimKey.Block(x, y, z, inner.World.Dimension), () => inner.HarvestGeneratorAsync(x, y, z, count, ct), ct);

    // ---- forwarded ----
    public WorldState World => inner.World;
    public string Username => inner.Username;
    public bool IsPlaying => inner.IsPlaying;
    public string? CurrentAction => inner.CurrentAction;
    public double StationSearchRadius => inner.StationSearchRadius;
    public Func<BlockPos, int, bool>? IsProtected { get => inner.IsProtected; set => inner.IsProtected = value; }
    public IReadOnlySet<int> ReservedItems { get => inner.ReservedItems; set => inner.ReservedItems = value; }

    public event Action<string>? ChatReceived { add => inner.ChatReceived += value; remove => inner.ChatReceived -= value; }
    public event Action<string>? Kicked { add => inner.Kicked += value; remove => inner.Kicked -= value; }
    public event Action<string>? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
    public event Action? Spawned { add => inner.Spawned += value; remove => inner.Spawned -= value; }
    public event Action? Died { add => inner.Died += value; remove => inner.Died -= value; }
    public event Action? Respawned { add => inner.Respawned += value; remove => inner.Respawned -= value; }
    public event Action<int, int>? HealthChanged { add => inner.HealthChanged += value; remove => inner.HealthChanged -= value; }
    public event Action<string>? PlayerJoined { add => inner.PlayerJoined += value; remove => inner.PlayerJoined -= value; }
    public event Action<string>? PlayerLeft { add => inner.PlayerLeft += value; remove => inner.PlayerLeft -= value; }
    public event Action<DangerEvent>? Danger { add => inner.Danger += value; remove => inner.Danger -= value; }
    public event Action<int, int>? EntityAnimated { add => inner.EntityAnimated += value; remove => inner.EntityAnimated -= value; }
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
    public Task<ActionResult> CollectDropsAsync(double radius = 8, CancellationToken ct = default) => inner.CollectDropsAsync(radius, ct);
    public Task<ActionResult> CraftAsync(int itemId, int damage = -1, int count = 1, CancellationToken ct = default) => inner.CraftAsync(itemId, damage, count, ct);
    public Task<ActionResult> ClickSlotAsync(int slot, bool rightClick = false, bool shift = false, CancellationToken ct = default) => inner.ClickSlotAsync(slot, rightClick, shift, ct);
    public Task<ActionResult> CloseWindowAsync(CancellationToken ct = default) => inner.CloseWindowAsync(ct);
    public Task<ActionResult> ActivateBlockAsync(int x, int y, int z, CancellationToken ct = default) => inner.ActivateBlockAsync(x, y, z, ct);
    public Task<ActionResult> EatAsync(CancellationToken ct = default) => inner.EatAsync(ct);
}
