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
    /// <summary>Items a meal must not use right now: <see cref="ReservedItems"/> plus food held for a hand-off (the client's
    /// <see cref="MinecraftClient.EatReservedItems"/>). Default: the reserved items.</summary>
    IReadOnlySet<int> EatReservedItems => ReservedItems;

    event Action<string>? ChatReceived;
    event Action<string>? Kicked;
    event Action<string>? Disconnected;
    event Action? Spawned;
    event Action? Died;
    event Action? Respawned;
    /// <summary>Server-forced move (from, to): see <see cref="MinecraftClient.ServerMoved"/>. Default: never raised.</summary>
    event Action<Vec3d, Vec3d>? ServerMoved { add { } remove { } }
    event Action<int, int>? HealthChanged;
    event Action<string>? PlayerJoined;
    event Action<string>? PlayerLeft;
    event Action<DangerEvent>? Danger;
    event Action<int, int>? EntityAnimated;
    event Action<BlockPos, int, int>? BlockAction;
    /// <summary>0x16 collect item (collector entity, item entity, stack or null). Default: never raised.</summary>
    event Action<int, int, ItemStack?>? ItemCollected { add { } remove { } }
    /// <summary>0x05 entity equipment (entity, slot, item id, damage). Default: never raised.</summary>
    event Action<int, int, int, int>? EquipmentChanged { add { } remove { } }
    /// <summary>0x26 entity status (entity, status: 2 hurt, 3 dead). Default: never raised.</summary>
    event Action<int, int>? EntityStatus { add { } remove { } }
    /// <summary>Another player's dig progress (entity, block, crack stage; −1 stopped): 1.12.2's Block Break Animation. Default:
    /// never raised (b1.8.1 has none).</summary>
    event Action<int, BlockPos, int>? BlockBreakProgress { add { } remove { } }

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
    /// <summary>Harvest from a player-taught stand spot (walks onto it first, refuses to dig from elsewhere). Default: no stand.</summary>
    Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, BlockPos? stand, CancellationToken ct = default) =>
        stand is null ? HarvestGeneratorAsync(x, y, z, count, ct) : Task.FromResult(ActionResult.Fail("this client can't use a taught stand spot"));
    /// <summary>Harvest several cells of one generator in rotation (the first positions the bot; the rest are dug while in reach).
    /// Default: the first cell only.</summary>
    Task<ActionResult> HarvestGeneratorAsync(IReadOnlyList<BlockPos> cells, int count, BlockPos? stand, CancellationToken ct = default) =>
        cells.Count == 0 ? Task.FromResult(ActionResult.Fail("no generator cell given"))
            : HarvestGeneratorAsync(cells[0].X, cells[0].Y, cells[0].Z, count, stand, ct);
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
    /// <summary>Places an item variant (slab type, wool colour: <paramref name="damage"/>) by clicking <paramref name="face"/> of a
    /// neighbour (doors: the top of the block below). Default: plain <see cref="PlaceBlockAsync(int,int,int,int,CancellationToken)"/>.</summary>
    Task<ActionResult> PlaceVariantAsync(int x, int y, int z, int itemId, int? damage, BlockFace? face, CancellationToken ct = default) =>
        PlaceBlockAsync(x, y, z, itemId, ct);
    Task<ActionResult> DigBlockAsync(int x, int y, int z, CancellationToken ct = default);
    Task<ActionResult> ActivateBlockAsync(int x, int y, int z, CancellationToken ct = default);
    Task<ActionResult> EatAsync(CancellationToken ct = default);

    /// <summary>Fell one tree (nearest / near <paramref name="at"/> / of a wood type), gather saplings, replant
    /// (<see cref="MinecraftClient.ChopTreeAsync"/>). Default: unsupported, so wrappers and fakes needn't implement it.</summary>
    Task<ActionResult> ChopTreeAsync(BlockPos? at = null, int? wood = null, CancellationToken ct = default) =>
        Task.FromResult(ActionResult.Fail("chop_tree isn't available on this client"));

    // ---- brainstem body (defaults: unsupported, so wrappers and fakes needn't implement them)

    /// <summary>Head turn toward a point on the next tick while the body is idle (no action lock). False = busy/unsupported.</summary>
    bool FaceIdle(Vec3d point) => false;

    /// <summary>One arm swing ("nod") while the body is idle. False = busy/unsupported.</summary>
    bool SwingIdle() => false;

    /// <summary>A client safety reflex owns the body (fight/defence, safety hold, heal retreat): the top of the priority stack.</summary>
    bool SafetyEngaged => false;

    /// <summary>The brain's "brave" override is on (<see cref="MinecraftClient.SetBrave"/>): fear gates don't hold the body.</summary>
    bool Brave => false;

    /// <summary>Plays a brainstem gesture tick by tick, no action lock (<see cref="MinecraftClient.PlayGesture"/>).
    /// False = refused (busy body for sneak/jump, safety) or unsupported.</summary>
    bool PlayGesture(GesturePlan plan) => false;
    /// <summary>Stops the running gesture (the real look and "stop sneaking" go out next tick).</summary>
    void CancelGesture() { }
    /// <summary>The gesture playing now, or null.</summary>
    string? CurrentGesture => null;

    /// <summary>The first 0x08 health of this session arrived (false = health/food are still defaults).</summary>
    bool VitalsKnown => true;
    /// <summary>The ground under the feet is loaded and verified (false = "airborne/over void" would be a guess).</summary>
    bool GroundKnown => true;

    /// <summary>Body language: jump | spin | look | wave | crouch | punch (<see cref="MinecraftClient.ActAsync"/>).</summary>
    /// <summary>The explicit, deliberate step off the island into the void (<see cref="MinecraftClient.StepIntoVoidAsync"/>).</summary>
    Task<ActionResult> StepIntoVoidAsync(string reason, bool keepItemsOk = false, CancellationToken ct = default) =>
        Task.FromResult(ActionResult.Fail("into_void isn't available on this client"));
    /// <summary>A deliberate void step in progress (its fall/death are the plan, not dangers), or null.</summary>
    IntentionalVoidStep? ActiveVoidStep => null;

    Task<ActionResult> GestureAsync(string move, string? target = null, int? times = null, CancellationToken ct = default) =>
        Task.FromResult(ActionResult.Fail("body language isn't available on this client"));
}

/// <summary>Thin forwarding adapter over the real client.</summary>
public sealed class MinecraftGameClient(MinecraftClient client) : IGameClient
{
    public MinecraftClient Inner { get; } = client;

    public WorldState World => Inner.World;
    public string Username => Inner.Options.Username;
    public bool IsPlaying => Inner.IsPlaying;
    public bool VitalsKnown => Inner.VitalsKnown;
    public bool GroundKnown => Inner.GroundKnown;
    public string? CurrentAction => Inner.CurrentAction;
    public double StationSearchRadius => Inner.StationSearchRadius;
    public Func<BlockPos, int, bool>? IsProtected { get => Inner.IsProtected; set => Inner.IsProtected = value; }
    public IReadOnlySet<int> ReservedItems { get => Inner.ReservedItems; set => Inner.ReservedItems = value; }
    public IReadOnlySet<int> EatReservedItems => Inner.EatReservedItems;

    public event Action<string>? ChatReceived { add => Inner.ChatReceived += value; remove => Inner.ChatReceived -= value; }
    public event Action<string>? Kicked { add => Inner.Kicked += value; remove => Inner.Kicked -= value; }
    public event Action<string>? Disconnected { add => Inner.Disconnected += value; remove => Inner.Disconnected -= value; }
    public event Action? Spawned { add => Inner.Spawned += value; remove => Inner.Spawned -= value; }
    public event Action? Died { add => Inner.Died += value; remove => Inner.Died -= value; }
    public event Action? Respawned { add => Inner.Respawned += value; remove => Inner.Respawned -= value; }
    public event Action<Vec3d, Vec3d>? ServerMoved { add => Inner.ServerMoved += value; remove => Inner.ServerMoved -= value; }
    public event Action<int, int>? HealthChanged { add => Inner.HealthChanged += value; remove => Inner.HealthChanged -= value; }
    public event Action<string>? PlayerJoined { add => Inner.PlayerJoined += value; remove => Inner.PlayerJoined -= value; }
    public event Action<string>? PlayerLeft { add => Inner.PlayerLeft += value; remove => Inner.PlayerLeft -= value; }
    public event Action<DangerEvent>? Danger { add => Inner.Danger += value; remove => Inner.Danger -= value; }
    public event Action<int, int>? EntityAnimated { add => Inner.EntityAnimated += value; remove => Inner.EntityAnimated -= value; }
    public event Action<BlockPos, int, int>? BlockAction { add => Inner.BlockAction += value; remove => Inner.BlockAction -= value; }
    public event Action<int, int, ItemStack?>? ItemCollected { add => Inner.ItemCollected += value; remove => Inner.ItemCollected -= value; }
    public event Action<int, int, int, int>? EquipmentChanged { add => Inner.EquipmentChanged += value; remove => Inner.EquipmentChanged -= value; }
    public event Action<int, int>? EntityStatus { add => Inner.EntityStatus += value; remove => Inner.EntityStatus -= value; }
    public event Action<int, BlockPos, int>? BlockBreakProgress { add => Inner.BlockBreakProgress += value; remove => Inner.BlockBreakProgress -= value; }

    public string DescribeStatus() => Inner.DescribeStatus();
    public string DescribeInventory(bool detail = false, int maxChars = 600) => Inner.DescribeInventory(detail, maxChars);
    public string DescribeSurroundings(double radius = 12, bool detail = false, int maxChars = 600) => Inner.DescribeSurroundings(radius, detail, maxChars);
    public string DescribeOpenWindow(int maxChars = 600) => Inner.DescribeOpenWindow(maxChars);

    /// <summary>
    /// Outgoing chat choke point for everything that chats through this adapter (skills, tools, console): false = the line
    /// is dropped. BotHost installs the agent's command gate here, so no code path can send an unauthorised /command.
    /// </summary>
    public Func<string, bool>? OutgoingFilter { get; set; }

    public Task<ActionResult> SendChatAsync(string text, CancellationToken ct = default) =>
        OutgoingFilter is { } f && !f(text) ? Task.FromResult(ActionResult.Fail("blocked: commands need a trusted player's exact authorisation"))
            : Inner.SendChatAsync(text, ct);
    public Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options = null, CancellationToken ct = default) => Inner.GotoAsync(goal, options, ct);
    public Task<ActionResult> FollowPlayerAsync(string name, double distance = 3, CancellationToken ct = default) => Inner.FollowPlayerAsync(name, distance, ct);
    public Task<ActionResult> StopAsync(CancellationToken ct = default) => Inner.StopAsync(ct);
    public Task<ActionResult> MineAsync(string blockName, int count, double radius = 16, CancellationToken ct = default) => Inner.MineAsync(blockName, count, radius, null, ct);
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, CancellationToken ct = default) => Inner.HarvestGeneratorAsync(x, y, z, count, ct);
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, BlockPos? stand, CancellationToken ct = default) =>
        Inner.HarvestGeneratorAsync(x, y, z, count, stand, ct);
    public Task<ActionResult> HarvestGeneratorAsync(IReadOnlyList<BlockPos> cells, int count, BlockPos? stand, CancellationToken ct = default) =>
        Inner.HarvestGeneratorAsync(cells, count, stand, ct);
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
    public Task<ActionResult> PlaceVariantAsync(int x, int y, int z, int itemId, int? damage, BlockFace? face, CancellationToken ct = default) =>
        Inner.PlaceVariantAsync(x, y, z, itemId, damage, face, ct);
    public Task<ActionResult> DigBlockAsync(int x, int y, int z, CancellationToken ct = default) => Inner.DigBlockAsync(x, y, z, ct);
    public Task<ActionResult> ActivateBlockAsync(int x, int y, int z, CancellationToken ct = default) => Inner.ActivateBlockAsync(x, y, z, ct);
    public Task<ActionResult> EatAsync(CancellationToken ct = default) => Inner.EatAsync(ct);
    public Task<ActionResult> ChopTreeAsync(BlockPos? at = null, int? wood = null, CancellationToken ct = default) => Inner.ChopTreeAsync(at, wood, null, ct);
    public bool FaceIdle(Vec3d point) => Inner.FaceIdle(point);
    public bool SwingIdle() => Inner.SwingIdle();
    public bool SafetyEngaged => Inner.SafetyEngaged;
    public bool Brave => Inner.Brave;
    public bool PlayGesture(GesturePlan plan) => Inner.PlayGesture(plan);
    public void CancelGesture() => Inner.CancelGesture();
    public string? CurrentGesture => Inner.CurrentGesture;
    public Task<ActionResult> GestureAsync(string move, string? target = null, int? times = null, CancellationToken ct = default) =>
        Inner.ActAsync(move, target, times, ct);
    public Task<ActionResult> StepIntoVoidAsync(string reason, bool keepItemsOk = false, CancellationToken ct = default) =>
        Inner.StepIntoVoidAsync(reason, keepItemsOk, ct);
    public IntentionalVoidStep? ActiveVoidStep => Inner.ActiveVoidStep;
}
