using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    /// <summary>A leased vein cell: only this ore, from here, with the usual protections and freshly checked exposure hazards.</summary>
    public Task<ActionResult> DigOreHereAsync(BlockPos at, int ore, CancellationToken ct = default) =>
        RunActionAsync("dig ore", ct, c =>
        {
            bool modern = Options.Profile.Inventory.ModernClicks;
            if (ore == 0 || OreIdentity.Canonical(ore, modern) != ore)
                return Task.FromResult(ActionResult.Fail("invalid canonical ore identity"));
            string? Verify()
            {
                if (!World.IsLoaded(at.X, at.Z)) return "ore target is not loaded";
                int actual = World.GetBlock(at);
                if (actual == 0) return null;
                if (OreIdentity.Canonical(actual, modern) != ore) return "ore target changed: leaving the replacement block";
                if (!OreIdentity.ExposureSafe(World, at)) return "ore exposure is unsafe: liquid, falling block, or unknown neighbor";
                return null;
            }
            if (Verify() is { } why) return Task.FromResult(ActionResult.Fail(why));
            return DigCoreAsync(at, c, allowPathing: false, floorGuard: true, requested: true, verify: Verify);
        });
}
