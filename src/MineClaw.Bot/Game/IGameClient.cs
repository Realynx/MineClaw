using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Game;

/// <summary>
/// The slice of <see cref="MinecraftClient"/> the bot layer uses. Exists so tools, the bridge and the demonstration
/// recorder can be tested offline against a fake; <see cref="MinecraftGameClient"/> forwards to the real client.
/// </summary>
public interface IGameClient
{
    WorldState World { get; }
    string Username { get; }
    bool IsPlaying { get; }
    string? CurrentAction { get; }
    double StationSearchRadius { get; }
    Func<BlockPos, int, bool>? IsProtected { get; set; }
    IReadOnlySet<int> ReservedItems { get; set; }

    event Action<string>? ChatReceived;
    event Action<string>? Kicked;
    event Action<string>? Disconnected;
    event Action? Spawned;
    event Action? Died;
    event Action? Respawned;
    event Action<int, int>? HealthChanged;
    event Action<string>? PlayerJoined;
    event Action<string>? PlayerLeft;
    event Action<DangerEvent>? Danger;
    event Action<int, int>? EntityAnimated;
    event Action<BlockPos, int, int>? BlockAction;

    string DescribeStatus();
    string DescribeInventory(bool detail = false, int maxChars = 600);
    string DescribeSurroundings(double radius = 12, bool detail = false, int maxChars = 600);
    string DescribeOpenWindow(int maxChars = 600);

    Task<ActionResult> SendChatAsync(string text, CancellationToken ct = default);
    Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options = null, CancellationToken ct = default);
    Task<ActionResult> FollowPlayerAsync(string name, double distance = 3, CancellationToken ct = default);
    Task<ActionResult> StopAsync(CancellationToken ct = default);
    Task<ActionResult> MineAsync(string blockName, int count, double radius = 16, CancellationToken ct = default);
    Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, CancellationToken ct = default);
    Task<ActionResult> CollectDropsAsync(double radius = 8, CancellationToken ct = default);
    Task<ActionResult> CraftAsync(int itemId, int damage = -1, int count = 1, CancellationToken ct = default);
    Task<ActionResult> SmeltAsync(int inputItemId, int count, int? fuelItemId = null, bool wait = false, BlockPos? furnace = null, CancellationToken ct = default);
    Task<ActionResult> CollectFurnaceAsync(BlockPos? furnace = null, CancellationToken ct = default);
    Task<ActionResult> OpenContainerAsync(int x, int y, int z, CancellationToken ct = default);
    Task<ActionResult> ClickSlotAsync(int slot, bool rightClick = false, bool shift = false, CancellationToken ct = default);
    Task<ActionResult> CloseWindowAsync(CancellationToken ct = default);
    Task<ActionResult> DepositAllAsync(BlockPos? container = null, Func<ItemStack, bool>? filter = null, CancellationToken ct = default);
    Task<ActionResult> WithdrawAsync(int itemId, int count, BlockPos? container = null, int? damage = null, CancellationToken ct = default);
    Task<ActionResult> PlaceBlockAsync(int x, int y, int z, int itemId, CancellationToken ct = default);
    Task<ActionResult> DigBlockAsync(int x, int y, int z, CancellationToken ct = default);
    Task<ActionResult> ActivateBlockAsync(int x, int y, int z, CancellationToken ct = default);
    Task<ActionResult> EatAsync(CancellationToken ct = default);
}

/// <summary>Thin forwarding adapter over the real client.</summary>
public sealed class MinecraftGameClient(MinecraftClient client) : IGameClient
{
    public MinecraftClient Inner { get; } = client;

    public WorldState World => Inner.World;
    public string Username => Inner.Options.Username;
    public bool IsPlaying => Inner.IsPlaying;
    public string? CurrentAction => Inner.CurrentAction;
    public double StationSearchRadius => Inner.StationSearchRadius;
    public Func<BlockPos, int, bool>? IsProtected { get => Inner.IsProtected; set => Inner.IsProtected = value; }
    public IReadOnlySet<int> ReservedItems { get => Inner.ReservedItems; set => Inner.ReservedItems = value; }

    public event Action<string>? ChatReceived { add => Inner.ChatReceived += value; remove => Inner.ChatReceived -= value; }
    public event Action<string>? Kicked { add => Inner.Kicked += value; remove => Inner.Kicked -= value; }
    public event Action<string>? Disconnected { add => Inner.Disconnected += value; remove => Inner.Disconnected -= value; }
    public event Action? Spawned { add => Inner.Spawned += value; remove => Inner.Spawned -= value; }
    public event Action? Died { add => Inner.Died += value; remove => Inner.Died -= value; }
    public event Action? Respawned { add => Inner.Respawned += value; remove => Inner.Respawned -= value; }
    public event Action<int, int>? HealthChanged { add => Inner.HealthChanged += value; remove => Inner.HealthChanged -= value; }
    public event Action<string>? PlayerJoined { add => Inner.PlayerJoined += value; remove => Inner.PlayerJoined -= value; }
    public event Action<string>? PlayerLeft { add => Inner.PlayerLeft += value; remove => Inner.PlayerLeft -= value; }
    public event Action<DangerEvent>? Danger { add => Inner.Danger += value; remove => Inner.Danger -= value; }
    public event Action<int, int>? EntityAnimated { add => Inner.EntityAnimated += value; remove => Inner.EntityAnimated -= value; }
    public event Action<BlockPos, int, int>? BlockAction { add => Inner.BlockAction += value; remove => Inner.BlockAction -= value; }

    public string DescribeStatus() => Inner.DescribeStatus();
    public string DescribeInventory(bool detail = false, int maxChars = 600) => Inner.DescribeInventory(detail, maxChars);
    public string DescribeSurroundings(double radius = 12, bool detail = false, int maxChars = 600) => Inner.DescribeSurroundings(radius, detail, maxChars);
    public string DescribeOpenWindow(int maxChars = 600) => Inner.DescribeOpenWindow(maxChars);

    public Task<ActionResult> SendChatAsync(string text, CancellationToken ct = default) => Inner.SendChatAsync(text, ct);
    public Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options = null, CancellationToken ct = default) => Inner.GotoAsync(goal, options, ct);
    public Task<ActionResult> FollowPlayerAsync(string name, double distance = 3, CancellationToken ct = default) => Inner.FollowPlayerAsync(name, distance, ct);
    public Task<ActionResult> StopAsync(CancellationToken ct = default) => Inner.StopAsync(ct);
    public Task<ActionResult> MineAsync(string blockName, int count, double radius = 16, CancellationToken ct = default) => Inner.MineAsync(blockName, count, radius, null, ct);
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, CancellationToken ct = default) => Inner.HarvestGeneratorAsync(x, y, z, count, ct);
    public Task<ActionResult> CollectDropsAsync(double radius = 8, CancellationToken ct = default) => Inner.CollectDropsAsync(radius, ct);
    public Task<ActionResult> CraftAsync(int itemId, int damage = -1, int count = 1, CancellationToken ct = default) => Inner.CraftAsync(itemId, damage, count, ct);
    public Task<ActionResult> SmeltAsync(int inputItemId, int count, int? fuelItemId = null, bool wait = false, BlockPos? furnace = null, CancellationToken ct = default) =>
        Inner.SmeltAsync(inputItemId, count, fuelItemId, wait, furnace, ct);
    public Task<ActionResult> CollectFurnaceAsync(BlockPos? furnace = null, CancellationToken ct = default) => Inner.CollectFurnaceAsync(furnace, ct);
    public Task<ActionResult> OpenContainerAsync(int x, int y, int z, CancellationToken ct = default) => Inner.OpenContainerAsync(x, y, z, ct);
    public Task<ActionResult> ClickSlotAsync(int slot, bool rightClick = false, bool shift = false, CancellationToken ct = default) => Inner.ClickSlotAsync(slot, rightClick, shift, ct);
    public Task<ActionResult> CloseWindowAsync(CancellationToken ct = default) => Inner.CloseWindowAsync(ct);
    public Task<ActionResult> DepositAllAsync(BlockPos? container = null, Func<ItemStack, bool>? filter = null, CancellationToken ct = default) => Inner.DepositAllAsync(container, filter, ct);
    public Task<ActionResult> WithdrawAsync(int itemId, int count, BlockPos? container = null, int? damage = null, CancellationToken ct = default) => Inner.WithdrawAsync(itemId, count, container, damage, ct);
    public Task<ActionResult> PlaceBlockAsync(int x, int y, int z, int itemId, CancellationToken ct = default) => Inner.PlaceBlockAsync(x, y, z, itemId, ct);
    public Task<ActionResult> DigBlockAsync(int x, int y, int z, CancellationToken ct = default) => Inner.DigBlockAsync(x, y, z, ct);
    public Task<ActionResult> ActivateBlockAsync(int x, int y, int z, CancellationToken ct = default) => Inner.ActivateBlockAsync(x, y, z, ct);
    public Task<ActionResult> EatAsync(CancellationToken ct = default) => Inner.EatAsync(ct);
}
