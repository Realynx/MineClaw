using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    // OpenWindow initially contains empty slots. Only a full server snapshot for that exact window proves it empty.
    private volatile Window? _confirmedContainerWindow;

    /// <summary>An explicit deconstruct designation may remove this chest only after a fresh server inventory proves both
    /// halves empty. Keep the window open during the dig so a late deposit aborts it. No permit survives this action.</summary>
    public Task<ActionResult> DeconstructChestAsync(BlockPos pos, CancellationToken ct = default) =>
        RunActionAsync("deconstruct chest", ct, c => DeconstructChestCoreAsync(pos, c));

    private async Task<ActionResult> DeconstructChestCoreAsync(BlockPos pos, CancellationToken ct)
    {
        if (!World.IsLoaded(pos.X, pos.Z)) return ActionResult.Fail($"{Fmt(pos)} is not loaded");
        int id = World.GetBlock(pos);
        if (id == 0) return ActionResult.Success($"{Fmt(pos)} already clear");
        if (!ChestPairs.IsPairable(id)) return ActionResult.Fail($"deconstruct chest expected a chest at {Fmt(pos)}");
        if (NeverBreak?.Invoke(pos) == true) return ActionResult.Fail(ProtectedRefusal(pos, id));
        // Equip before opening: swapping a tool from the main inventory may close a window.
        int held = await SelectToolForAsync(id, ct).ConfigureAwait(false);
        var opened = await OpenCoreAsync(pos, ct).ConfigureAwait(false);
        if (!opened.Ok) return opened;
        try
        {
            var window = World.Inventory.OpenWindow;
            var partner = ChestPairs.Partner(World, pos);
            string? Verify()
            {
                if (World.GetBlock(pos) != id || ChestPairs.Partner(World, pos) != partner)
                    return "chest layout changed; inspect again";
                if (window == null || window.Type != WindowType.Chest || !ReferenceEquals(window, World.Inventory.OpenWindow)
                    || !ReferenceEquals(window, _confirmedContainerWindow)) return "fresh chest contents unavailable";
                if (window.ContainerSlots != (partner == null ? ChestPairs.HalfSlots : ChestPairs.DoubleSlots))
                    return "chest halves are not fully visible";
                if (window.ContainerContents().Any(s => !s.Item.IsEmpty)) return "evacuate contents before removing chest";
                if (NeverBreak?.Invoke(pos) == true) return "chest is marked never-break";
                return null;
            }
            ActionResult Waiting(string why) => ActionResult.Fail("chest deconstruction waiting: " + why);
            if (Verify() is { } wait) return Waiting(wait);
            var job = new DigJob(pos, id, BlockName(id, World.GetMeta(pos)), false, true, false, true, true, true);
            if (DigRefusal(job) is { } no) return ActionResult.Fail(no);
            var (aim, unreachable) = await DigAimAsync(job, ct).ConfigureAwait(false);
            if (unreachable != null) return unreachable;
            if (DigSafetyVeto(pos, job.Name, generator: false, positioned: true) is { } unsafeDig) return ActionResult.Fail(unsafeDig);
            int ticks = DigTicksHere(pos, id);
            if (ticks == int.MaxValue) return ActionResult.Fail($"{job.Name} can't be dug");
            if (await DigForTicksAsync(pos, id, aim!.Value, ticks, ct, Verify).ConfigureAwait(false) is { } changed) return Waiting(changed);
            return await DigVerdictAsync(job, ticks, held, ct).ConfigureAwait(false);
        }
        finally { await EnsureNoWindowAsync(ct).ConfigureAwait(false); }
    }
}
