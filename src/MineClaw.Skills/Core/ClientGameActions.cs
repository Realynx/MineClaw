using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// <see cref="IGameActions"/> over the real <see cref="MinecraftClient"/>, using only its public API. Primitives the
/// client doesn't expose yet (see <see cref="Primitive"/>) return "needs client primitive ..." so skills report the gap
/// honestly; <c>CATALOG.md</c> lists the exact Client additions that light them up.
/// </summary>
public sealed class ClientGameActions(MinecraftClient client) : IGameActions
{
    public MinecraftClient Inner { get; } = client;

    public WorldState World => Inner.World;
    public string Username => Inner.Options.Username;
    public Primitive Supported => Primitive.None;
    public bool IsRaining => Inner.IsRaining;
    public double Reach => Inner.Options.Reach;
    public IReadOnlySet<int> ReservedItems => Inner.ReservedItems;
    public bool IsProtected(BlockPos pos, int id) => Inner.IsProtected?.Invoke(pos, id) ?? false;
    public IReadOnlyDictionary<BlockPos, string[]> Signs => Inner.Signs;

    public Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options, CancellationToken ct) => Inner.GotoAsync(goal, options, ct);
    public Task<ActionResult> DigAsync(BlockPos p, CancellationToken ct) => Inner.DigBlockAsync(p.X, p.Y, p.Z, ct);
    public Task<ActionResult> PlaceAsync(BlockPos p, int itemId, CancellationToken ct) => Inner.PlaceBlockAsync(p.X, p.Y, p.Z, itemId, ct);
    public Task<ActionResult> ActivateAsync(BlockPos p, CancellationToken ct) => Inner.ActivateBlockAsync(p.X, p.Y, p.Z, ct);
    public Task<ActionResult> LookAtAsync(Vec3d point, CancellationToken ct) => Inner.LookAtAsync(point, ct);
    public Task<ActionResult> UseItemAsync(int? itemId, CancellationToken ct) => Inner.UseItemAsync(itemId, ct);
    public Task<ActionResult> EatAsync(CancellationToken ct) => Inner.EatAsync(ct);
    public Task<ActionResult> CollectDropsAsync(double radius, CancellationToken ct) => Inner.CollectDropsAsync(radius, ct);
    public Task<ActionResult> MineAsync(string blockName, int count, double radius, CancellationToken ct) =>
        Inner.MineAsync(blockName, count, radius, null, ct);
    public Task<ActionResult> CraftAsync(string itemName, int count, CancellationToken ct) => Inner.CraftAsync(itemName, count, ct);
    public Task<ActionResult> SmeltAsync(int input, int count, int? fuel, bool wait, BlockPos? furnace, CancellationToken ct) =>
        Inner.SmeltAsync(input, count, fuel, wait, furnace, ct);
    public Task<ActionResult> CollectFurnaceAsync(BlockPos? furnace, CancellationToken ct) => Inner.CollectFurnaceAsync(furnace, ct);
    public Task<ActionResult> OpenContainerAsync(BlockPos p, CancellationToken ct) => Inner.OpenContainerAsync(p.X, p.Y, p.Z, ct);
    public Task<ActionResult> CloseWindowAsync(CancellationToken ct) => Inner.CloseWindowAsync(ct);
    public Task<ActionResult> ClickSlotAsync(int slot, bool rightClick, bool shift, CancellationToken ct) => Inner.ClickSlotAsync(slot, rightClick, shift, ct);
    public Task<ActionResult> DepositAllAsync(BlockPos? container, Func<ItemStack, bool>? filter, CancellationToken ct) =>
        Inner.DepositAllAsync(container, filter, ct);
    public Task<ActionResult> WithdrawAsync(int itemId, int count, BlockPos? container, int? damage, CancellationToken ct) =>
        Inner.WithdrawAsync(itemId, count, container, damage, ct);
    public Task<ActionResult> SendChatAsync(string text, CancellationToken ct) => Inner.SendChatAsync(text, ct);

    public async Task WaitTicksAsync(int ticks, CancellationToken ct)
    {
        long until = Inner.TickCount + Math.Max(1, ticks);
        var step = Inner.Options.TickInterval;
        while (Inner.TickCount < until) await Task.Delay(step, ct).ConfigureAwait(false);
    }

    public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        while (true)
        {
            if (condition()) return true;
            if (DateTime.UtcNow >= end) return false;
            await Task.Delay(Inner.Options.TickInterval, ct).ConfigureAwait(false);
        }
    }

    public Task<ActionResult> UseOnBlockAsync(BlockPos pos, BlockFace face, int itemId, CancellationToken ct) => Missing("use_on_block");
    public Task<ActionResult> UseEntityAsync(int entityId, int? itemId, CancellationToken ct) => Missing("use_entity");
    public Task<ActionResult> WriteSignAsync(BlockPos pos, string[] lines, CancellationToken ct) => Missing("write_sign");
    public Task<ActionResult> ReleaseUseAsync(CancellationToken ct) => Missing("release_use");
    public Task<ActionResult> DropHeldAsync(bool wholeStack, CancellationToken ct) => Missing("drop_held");

    private static Task<ActionResult> Missing(string name) => Task.FromResult(ActionResult.Fail($"needs client primitive {name}"));
}
