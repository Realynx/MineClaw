using System.Text;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>
/// Records every action a skill asks of the game, in order, with its arguments and result text (and the feet after each
/// walk), forwarding to <paramref name="inner"/>. The skill goldens pin these traces so a refactor of a skill's control flow
/// can't change what the body does.
/// </summary>
public sealed class TraceGame(IGameActions inner) : IGameActions
{
    public StringBuilder Trace { get; } = new();

    private ActionResult Note(string call, ActionResult r)
    {
        Trace.Append("  ").Append(call).Append(" -> ").Append(r.Ok ? "ok " : "FAIL ").Append(r.Message.ReplaceLineEndings(" | "));
        if (r.Count != 0) Trace.Append(" #").Append(r.Count);
        Trace.Append('\n');
        return r;
    }

    private async Task<ActionResult> Call(string call, Task<ActionResult> t) => Note(call, await t.ConfigureAwait(false));

    private static string P(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
    private static string P(BlockPos? p) => p is { } q ? P(q) : "-";

    public WorldState World => inner.World;
    public string Username => inner.Username;
    public Primitive Supported => inner.Supported;
    public bool IsRaining => inner.IsRaining;
    public double Reach => inner.Reach;
    public IReadOnlySet<int> ReservedItems => inner.ReservedItems;
    public bool IsProtected(BlockPos pos, int id) => inner.IsProtected(pos, id);
    public ProtectionVerdict ExplainProtection(BlockPos pos, int id) => inner.ExplainProtection(pos, id);
    public IReadOnlyDictionary<BlockPos, string[]> Signs => inner.Signs;
    public int RidingEntityId => inner.RidingEntityId;

    public async Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options, CancellationToken ct)
    {
        var r = await inner.GotoAsync(goal, options, ct).ConfigureAwait(false);
        var f = World.Player.Position;
        return Note($"goto {goal} drop {options?.MaxDrop?.ToString() ?? "-"} place {options?.AllowPlace} break {options?.AllowBreak} @{f.X:0.##},{f.Y:0.##},{f.Z:0.##}", r);
    }

    public Task<ActionResult> DigAsync(BlockPos pos, CancellationToken ct) => Call($"dig {P(pos)}", inner.DigAsync(pos, ct));
    public Task<ActionResult> PlaceAsync(BlockPos pos, int itemId, CancellationToken ct) => Call($"place {P(pos)} {itemId}", inner.PlaceAsync(pos, itemId, ct));
    public Task<ActionResult> PlaceAsync(BlockPos pos, int itemId, BlockFace face, CancellationToken ct) =>
        Call($"place {P(pos)} {itemId} {face}", inner.PlaceAsync(pos, itemId, face, ct));
    public Task<ActionResult> ActivateAsync(BlockPos pos, CancellationToken ct) => Call($"activate {P(pos)}", inner.ActivateAsync(pos, ct));
    public Task<ActionResult> LookAtAsync(Vec3d point, CancellationToken ct) => Call($"look {point.X:0.##},{point.Y:0.##},{point.Z:0.##}", inner.LookAtAsync(point, ct));
    public Task<ActionResult> UseItemAsync(int? itemId, CancellationToken ct) => Call($"use {itemId?.ToString() ?? "-"}", inner.UseItemAsync(itemId, ct));
    public Task<ActionResult> EatAsync(CancellationToken ct) => Call("eat", inner.EatAsync(ct));
    public Task<ActionResult> CollectDropsAsync(double radius, CancellationToken ct, bool dryOnly = false) =>
        Call($"collect {radius} dry {dryOnly}", inner.CollectDropsAsync(radius, ct, dryOnly));
    public Task<ActionResult> MineAsync(string blockName, int count, double radius, CancellationToken ct) =>
        Call($"mine {blockName} {count} r{radius}", inner.MineAsync(blockName, count, radius, ct));
    public Task<ActionResult> CraftAsync(string itemName, int count, CancellationToken ct) => Call($"craft {itemName} {count}", inner.CraftAsync(itemName, count, ct));
    public Task<ActionResult> SmeltAsync(int inputItemId, int count, int? fuelItemId, bool wait, BlockPos? furnace, CancellationToken ct) =>
        Call($"smelt {inputItemId} {count} fuel {fuelItemId?.ToString() ?? "-"} wait {wait} at {P(furnace)}", inner.SmeltAsync(inputItemId, count, fuelItemId, wait, furnace, ct));
    public Task<ActionResult> CollectFurnaceAsync(BlockPos? furnace, CancellationToken ct) => Call($"collect_furnace {P(furnace)}", inner.CollectFurnaceAsync(furnace, ct));
    public Task<ActionResult> OpenContainerAsync(BlockPos pos, CancellationToken ct) => Call($"open {P(pos)}", inner.OpenContainerAsync(pos, ct));
    public Task<ActionResult> CloseWindowAsync(CancellationToken ct) => Call("close", inner.CloseWindowAsync(ct));
    public Task<ActionResult> ClickSlotAsync(int slot, bool rightClick, bool shift, CancellationToken ct) =>
        Call($"click {slot}{(rightClick ? " right" : "")}{(shift ? " shift" : "")}", inner.ClickSlotAsync(slot, rightClick, shift, ct));
    public Task<ActionResult> DepositAllAsync(BlockPos? container, Func<ItemStack, bool>? filter, CancellationToken ct) =>
        Call($"deposit_all {P(container)} filter {filter != null}", inner.DepositAllAsync(container, filter, ct));
    public Task<ActionResult> WithdrawAsync(int itemId, int count, BlockPos? container, int? damage, CancellationToken ct) =>
        Call($"withdraw {itemId} {count} {P(container)} dmg {damage?.ToString() ?? "-"}", inner.WithdrawAsync(itemId, count, container, damage, ct));
    public Task<ActionResult> SendChatAsync(string text, CancellationToken ct) => Call($"chat {text}", inner.SendChatAsync(text, ct));

    public Task WaitTicksAsync(int ticks, CancellationToken ct)
    {
        Trace.Append("  wait ").Append(ticks).Append('\n');
        return inner.WaitTicksAsync(ticks, ct);
    }

    public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        bool ok = await inner.WaitUntilAsync(condition, timeout, ct).ConfigureAwait(false);
        Trace.Append("  wait_until ").Append(timeout.TotalMilliseconds).Append(" -> ").Append(ok).Append('\n');
        return ok;
    }

    public Task<ActionResult> UseOnBlockAsync(BlockPos pos, BlockFace face, int itemId, CancellationToken ct) =>
        Call($"use_on {P(pos)} {face} {itemId}", inner.UseOnBlockAsync(pos, face, itemId, ct));
    public Task<ActionResult> UseEntityAsync(int entityId, int? itemId, CancellationToken ct) =>
        Call($"use_entity {entityId} {itemId?.ToString() ?? "-"}", inner.UseEntityAsync(entityId, itemId, ct));
    public Task<ActionResult> WriteSignAsync(BlockPos pos, string[] lines, CancellationToken ct) =>
        Call($"sign {P(pos)} [{string.Join("|", lines)}]", inner.WriteSignAsync(pos, lines, ct));
    public Task<ActionResult> ReleaseUseAsync(CancellationToken ct) => Call("release", inner.ReleaseUseAsync(ct));
    public Task<ActionResult> DropHeldAsync(bool wholeStack, CancellationToken ct) => Call($"drop_held {wholeStack}", inner.DropHeldAsync(wholeStack, ct));
    public Task<ActionResult> ResyncInventoryAsync(CancellationToken ct) => Call("resync", inner.ResyncInventoryAsync(ct));
    public BlockFace? WallFace(BlockPos pos, Func<BlockPos, bool>? exclude = null) => inner.WallFace(pos, exclude);
    public Task<ActionResult> ShootAsync(Vec3d target, int? entityId, int chargeTicks, CancellationToken ct) =>
        Call($"shoot {target.X:0.##},{target.Y:0.##},{target.Z:0.##} {entityId?.ToString() ?? "-"} {chargeTicks}", inner.ShootAsync(target, entityId, chargeTicks, ct));
    public Task<ActionResult> DismountAsync(CancellationToken ct) => Call("dismount", inner.DismountAsync(ct));
}
