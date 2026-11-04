using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Fishing (docs/web/story-mode.md "Fishing"): hold a rod, face the water, cast (use the item), find OUR bobber (object type 90
/// whose spawn data is our entity id: b1.8.1's 0x17 thrower, 1.12.2's Spawn Object owner), wait for a bite, reel in within the
/// window (use the item again), let the catch fly home, repeat for the session. A cast that gets no bite in 45 s is reeled in and
/// cast again. Durability-aware: a rod under 5% is swapped for a spare when there is one.
/// </summary>
public sealed partial class MinecraftClient
{
    public const int FishingRodId = 346, RawFishId = 349, FishHookType = 90;

    /// <summary>
    /// Fishes at <paramref name="target"/> (a water cell) for <paramref name="session"/> or until <paramref name="maxCatches"/> fish
    /// were caught. Success says how many casts and catches.
    /// </summary>
    public Task<ActionResult> FishAsync(Vec3d target, TimeSpan session, int maxCatches = 64, CancellationToken ct = default) =>
        RunActionAsync("fish", ct, c => FishCoreAsync(target, session, Math.Max(1, maxCatches), c));

    private async Task<ActionResult> FishCoreAsync(Vec3d target, TimeSpan session, int maxCatches, CancellationToken ct)
    {
        if (await SelectRodAsync(ct).ConfigureAwait(false) is not { } rod) return ActionResult.Fail("no fishing rod");
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(World.Player.EyePosition, target);
        await LookCoreAsync(yaw, pitch, ct).ConfigureAwait(false);
        int fishBefore = FishCount();
        var end = DateTime.UtcNow + session;
        int casts = 0, bites = 0, lost = 0;
        while (DateTime.UtcNow < end && FishCount() - fishBefore < maxCatches)
        {
            ct.ThrowIfCancellationRequested();
            if (await SelectRodAsync(ct).ConfigureAwait(false) is null) break; // it broke and there's no spare
            int me = World.Player.EntityId;
            var before = Bobbers(me).Select(e => e.Id).ToHashSet();
            Send(PlayerBlockPlacementPacket.UseItem(HeldStack()));
            casts++;
            Entity? bob = null;
            await WaitUntilAsync(() => (bob = Bobbers(me).FirstOrDefault(e => !before.Contains(e.Id))) != null, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (bob is null) { if (++lost >= 3) return ActionResult.Fail($"the cast never showed a bobber ({casts} casts)"); continue; }
            var detector = new BiteDetector();
            var left = end - DateTime.UtcNow;
            var wait = left < TimeSpan.FromSeconds(45) ? left : TimeSpan.FromSeconds(45);
            bool bit = await WaitUntilAsync(() => World.Entities.Get(bob.Id) is not { } e || detector.Sample(e.Position.Y, e.Velocity.Y),
                wait < TimeSpan.Zero ? TimeSpan.Zero : wait, ct).ConfigureAwait(false) && World.Entities.Get(bob.Id) != null;
            // Reel in: on a bite the catch flies to us; otherwise this just pulls the line back for the next cast.
            Send(PlayerBlockPlacementPacket.UseItem(HeldStack()));
            if (bit)
            {
                bites++;
                int had = FishCount();
                await WaitUntilAsync(() => FishCount() > had, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            else await WaitTicksAsync(5, ct).ConfigureAwait(false);
        }
        int caught = FishCount() - fishBefore;
        return casts == 0
            ? ActionResult.Fail($"no fishing rod to cast ({ItemName(rod.Id, rod.Damage)} worn out)")
            : ActionResult.Success($"fished: {casts} cast(s), {bites} bite(s), {caught} fish caught");
    }

    private List<Entity> Bobbers(int me) =>
        World.Entities.All().Where(e => e.Kind == EntityKind.Object && e.TypeId == FishHookType && e.Data == me).ToList();

    private int FishCount() => World.Inventory.CountItem(RawFishId, null, includeAll: false);

    /// <summary>Holds a rod: the held one unless it is under 5% and a spare exists. Null = none.</summary>
    private async Task<ItemStack?> SelectRodAsync(CancellationToken ct)
    {
        var held = HeldStack();
        int max = Items.Get(FishingRodId).MaxDamage;
        bool Worn(ItemStack s) => max > 0 && max - s.Damage < max * 0.05;
        if (held.Id == FishingRodId && !(Worn(held) && Spare(held))) return held;
        // The least worn rod in the hotbar (else one moved there by the selection).
        var rods = World.Inventory.PlayerSlots().Where(s => !s.IsEmpty && s.Id == FishingRodId).OrderBy(s => s.Damage).ToList();
        if (rods.Count == 0) return null;
        if (await SelectItemAsync(FishingRodId, rods[0].Damage, ct).ConfigureAwait(false) < 0
            && await SelectItemAsync(FishingRodId, null, ct).ConfigureAwait(false) < 0) return null;
        return HeldStack();

        bool Spare(ItemStack h) => World.Inventory.PlayerSlots().Count(s => !s.IsEmpty && s.Id == FishingRodId && !Worn(s)) > 0;
    }
}

/// <summary>
/// A bite on our bobber, from what the server tells us about it: the cast flies, lands and floats (its height settles), then a
/// bite pulls it under. b1.8.1 (EntityFishHook: <c>motionY -= 0.2</c> on a bite, velocity tracked) and 1.12.2 (EntityFishHook:
/// <c>motionY = -0.4 * rand(0.6, 1)</c>) both send that as a velocity packet and a drop in height. Fed one sample per tick.
/// </summary>
public sealed class BiteDetector
{
    /// <summary>A downward velocity at least this fast (blocks/tick) after it floats is a bite; floating bobs stay within ±0.05.</summary>
    public const double BiteVelocity = -0.12;
    /// <summary>A drop this far under the floating height is a bite (a velocity update can be missed).</summary>
    public const double BiteDrop = 0.2;
    private const int SettleTicks = 10;

    private double _lastY = double.NaN, _settleVy;
    private int _still;
    private bool _vyFresh;
    public double? FloatY { get; private set; }

    /// <summary>One tick's look at the bobber: true on a bite.</summary>
    public bool Sample(double y, double vy)
    {
        if (FloatY is not { } level)
        {
            // Floating: its height hardly changes for half a second.
            _still = !double.IsNaN(_lastY) && Math.Abs(y - _lastY) < 0.08 ? _still + 1 : 0;
            _lastY = y;
            if (_still >= SettleTicks) { FloatY = y; _settleVy = vy; }
            return false;
        }
        // The velocity we hold may be the cast's, never updated since (no packet while it floats still): only a new one counts.
        if (!_vyFresh && vy != _settleVy) _vyFresh = true;
        return _vyFresh && vy <= BiteVelocity || y < level - BiteDrop;
    }
}
