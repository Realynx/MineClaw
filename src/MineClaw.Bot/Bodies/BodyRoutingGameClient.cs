using System.Runtime.CompilerServices;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Bodies;

/// <summary>
/// The one game client the brain's tools are registered against (one brain, many bodies): every call acts on
/// <see cref="BodyScope.Current"/>, else on the registry's <see cref="BodyRegistry.Focus"/>. <paramref name="view"/> picks
/// which of the body's clients a registration gets (<see cref="IBody.Game"/> for the game tools, <see cref="IBody.Adapter"/>
/// for skills/gestures), so each sees exactly what it saw before routing.
/// <para>Every member forwards, interface defaults included, so a call behaves exactly as on the target. Code that
/// type-checks the client unwraps it first (<see cref="GameClients.Resolve"/>). Events subscribe on the main body: per-body
/// events are wired per body by <see cref="BodyFactory"/>.</para>
/// <para>A body tool called inside an activity with no body in scope logs a WARN (rate-limited) and acts on the focus: an
/// ActivityManager / worker / script lane that forgot to push its scope would otherwise move the wrong body silently.</para>
/// </summary>
public sealed class BodyRoutingGameClient(BodyRegistry registry, Func<IBody, IGameClient> view, ILogger? log = null) : IGameClient
{
    private long _missing;
    private long _lastWarnTicks = long.MinValue;

    public BodyRegistry Registry => registry;

    /// <summary>How often each warning is logged at most (every occurrence is counted in <see cref="MissingScopeCount"/>).</summary>
    public TimeSpan WarnEvery { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Body tool calls inside an activity that had no <see cref="BodyScope"/> (each acted on the focus body).</summary>
    public long MissingScopeCount => Interlocked.Read(ref _missing);

    /// <summary>Raised for every unscoped call inside an activity: (member, body acted on). Tests and telemetry.</summary>
    public event Action<string, IBody>? MissingScope;

    /// <summary>The body a call made now would act on.</summary>
    public IBody Body => BodyScope.Current ?? registry.Focus;

    /// <summary>The client a call made now would act on (this registration's view of <see cref="Body"/>).</summary>
    public IGameClient Target => view(Body);

    private IGameClient T([CallerMemberName] string member = "")
    {
        if (BodyScope.Current is { } scoped) return view(scoped);
        var focus = registry.Focus;
        if (ActivityManager.RunningKind is { } kind) NoteMissing(member, focus, kind);
        return view(focus);
    }

    private IGameClient Main => view(registry.Main);

    private void NoteMissing(string member, IBody focus, ActivityKind kind)
    {
        Interlocked.Increment(ref _missing);
        try { MissingScope?.Invoke(member, focus); } catch (Exception) { }
        if (log == null) return;
        long now = Environment.TickCount64, last = Interlocked.Read(ref _lastWarnTicks);
        if (last != long.MinValue && now - last < (long)WarnEvery.TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref _lastWarnTicks, now, last) != last) return;
        log.LogWarning("body tool {Member} ran inside a {Kind} activity without a BodyScope; acting on the focus body {Body} ({Count} so far)",
            member, kind, focus.Name, MissingScopeCount);
    }

    // ---- state
    public WorldState World => T().World;
    public string Username => T().Username;
    public bool IsPlaying => T().IsPlaying;
    public string? CurrentAction => T().CurrentAction;
    public double StationSearchRadius => T().StationSearchRadius;
    public Func<BlockPos, int, bool>? IsProtected { get => T().IsProtected; set => T().IsProtected = value; }
    public IReadOnlySet<int> ReservedItems { get => T().ReservedItems; set => T().ReservedItems = value; }
    public IReadOnlySet<int> EatReservedItems => T().EatReservedItems;
    public bool VitalsKnown => T().VitalsKnown;
    public bool GroundKnown => T().GroundKnown;
    public bool SafetyEngaged => T().SafetyEngaged;
    public bool Brave => T().Brave;
    public string? CurrentGesture => T().CurrentGesture;
    public IntentionalVoidStep? ActiveVoidStep => T().ActiveVoidStep;

    // ---- events (main body's; see the class remarks)
    public event Action<string>? ChatReceived { add => Main.ChatReceived += value; remove => Main.ChatReceived -= value; }
    public event Action<string>? Kicked { add => Main.Kicked += value; remove => Main.Kicked -= value; }
    public event Action<string>? Disconnected { add => Main.Disconnected += value; remove => Main.Disconnected -= value; }
    public event Action? Spawned { add => Main.Spawned += value; remove => Main.Spawned -= value; }
    public event Action? Died { add => Main.Died += value; remove => Main.Died -= value; }
    public event Action? Respawned { add => Main.Respawned += value; remove => Main.Respawned -= value; }
    public event Action<Vec3d, Vec3d>? ServerMoved { add => Main.ServerMoved += value; remove => Main.ServerMoved -= value; }
    public event Action<int, int>? HealthChanged { add => Main.HealthChanged += value; remove => Main.HealthChanged -= value; }
    public event Action<string>? PlayerJoined { add => Main.PlayerJoined += value; remove => Main.PlayerJoined -= value; }
    public event Action<string>? PlayerLeft { add => Main.PlayerLeft += value; remove => Main.PlayerLeft -= value; }
    public event Action<DangerEvent>? Danger { add => Main.Danger += value; remove => Main.Danger -= value; }
    public event Action<int, int>? EntityAnimated { add => Main.EntityAnimated += value; remove => Main.EntityAnimated -= value; }
    public event Action<int, int, ItemStack?>? ItemCollected { add => Main.ItemCollected += value; remove => Main.ItemCollected -= value; }
    public event Action<int, int, int, int>? EquipmentChanged { add => Main.EquipmentChanged += value; remove => Main.EquipmentChanged -= value; }
    public event Action<int, int>? EntityStatus { add => Main.EntityStatus += value; remove => Main.EntityStatus -= value; }
    public event Action<int, BlockPos, int>? BlockBreakProgress { add => Main.BlockBreakProgress += value; remove => Main.BlockBreakProgress -= value; }
    public event Action<BlockPos, int, int>? BlockAction { add => Main.BlockAction += value; remove => Main.BlockAction -= value; }

    // ---- descriptions
    public string DescribeStatus() => T().DescribeStatus();
    public string DescribeInventory(bool detail = false, int maxChars = 600) => T().DescribeInventory(detail, maxChars);
    public string DescribeSurroundings(double radius = 12, bool detail = false, int maxChars = 600) => T().DescribeSurroundings(radius, detail, maxChars);
    public string DescribeOpenWindow(int maxChars = 600) => T().DescribeOpenWindow(maxChars);

    // ---- actions
    public Task<ActionResult> SendChatAsync(string text, CancellationToken ct = default) => T().SendChatAsync(text, ct);
    public Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options = null, CancellationToken ct = default) => T().GotoAsync(goal, options, ct);
    public Task<ActionResult> FollowPlayerAsync(string name, double distance = 3, CancellationToken ct = default) => T().FollowPlayerAsync(name, distance, ct);
    public Task<ActionResult> StopAsync(CancellationToken ct = default) => T().StopAsync(ct);
    public Task<ActionResult> MineAsync(string blockName, int count, double radius = 16, CancellationToken ct = default) => T().MineAsync(blockName, count, radius, ct);
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, CancellationToken ct = default) =>
        T().HarvestGeneratorAsync(x, y, z, count, ct);
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, BlockPos? stand, CancellationToken ct = default) =>
        T().HarvestGeneratorAsync(x, y, z, count, stand, ct);
    public Task<ActionResult> HarvestGeneratorAsync(IReadOnlyList<BlockPos> cells, int count, BlockPos? stand, CancellationToken ct = default) =>
        T().HarvestGeneratorAsync(cells, count, stand, ct);
    public Task<ActionResult> CollectDropsAsync(double radius = 8, CancellationToken ct = default) => T().CollectDropsAsync(radius, ct);
    public Task<ActionResult> CraftAsync(int itemId, int damage = -1, int count = 1, CancellationToken ct = default) => T().CraftAsync(itemId, damage, count, ct);
    public Task<ActionResult> SmeltAsync(int inputItemId, int count, int? fuelItemId = null, bool wait = false, BlockPos? furnace = null, CancellationToken ct = default) =>
        T().SmeltAsync(inputItemId, count, fuelItemId, wait, furnace, ct);
    public Task<ActionResult> CollectFurnaceAsync(BlockPos? furnace = null, CancellationToken ct = default) => T().CollectFurnaceAsync(furnace, ct);
    public Task<ActionResult> OpenContainerAsync(int x, int y, int z, CancellationToken ct = default) => T().OpenContainerAsync(x, y, z, ct);
    public Task<ActionResult> ClickSlotAsync(int slot, bool rightClick = false, bool shift = false, CancellationToken ct = default) => T().ClickSlotAsync(slot, rightClick, shift, ct);
    public Task<ActionResult> CloseWindowAsync(CancellationToken ct = default) => T().CloseWindowAsync(ct);
    public Task<ActionResult> DepositAllAsync(BlockPos? container = null, Func<ItemStack, bool>? filter = null, CancellationToken ct = default) =>
        T().DepositAllAsync(container, filter, ct);
    public Task<ActionResult> WithdrawAsync(int itemId, int count, BlockPos? container = null, int? damage = null, CancellationToken ct = default) =>
        T().WithdrawAsync(itemId, count, container, damage, ct);
    public Task<ActionResult> PlaceBlockAsync(int x, int y, int z, int itemId, CancellationToken ct = default) => T().PlaceBlockAsync(x, y, z, itemId, ct);
    public Task<ActionResult> PlaceVariantAsync(int x, int y, int z, int itemId, int? damage, BlockFace? face, CancellationToken ct = default) =>
        T().PlaceVariantAsync(x, y, z, itemId, damage, face, ct);
    public Task<ActionResult> DigBlockAsync(int x, int y, int z, CancellationToken ct = default) => T().DigBlockAsync(x, y, z, ct);
    public Task<ActionResult> ActivateBlockAsync(int x, int y, int z, CancellationToken ct = default) => T().ActivateBlockAsync(x, y, z, ct);
    public Task<ActionResult> EatAsync(CancellationToken ct = default) => T().EatAsync(ct);
    public Task<ActionResult> ChopTreeAsync(BlockPos? at = null, int? wood = null, CancellationToken ct = default) => T().ChopTreeAsync(at, wood, ct);
    public Task<ActionResult> ChopThisTreeAsync(BlockPos at, CancellationToken ct = default) => T().ChopThisTreeAsync(at, ct);

    // ---- brainstem body
    public bool FaceIdle(Vec3d point) => T().FaceIdle(point);
    public bool SwingIdle() => T().SwingIdle();
    public bool PlayGesture(GesturePlan plan) => T().PlayGesture(plan);
    public void CancelGesture() => T().CancelGesture();
    public Task<ActionResult> StepIntoVoidAsync(string reason, bool keepItemsOk = false, CancellationToken ct = default) =>
        T().StepIntoVoidAsync(reason, keepItemsOk, ct);
    public Task<ActionResult> GestureAsync(string move, string? target = null, int? times = null, CancellationToken ct = default) =>
        T().GestureAsync(move, target, times, ct);
}
