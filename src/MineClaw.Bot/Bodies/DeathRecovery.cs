using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using MineClaw.Client;
using MineClaw.World;

namespace MineClaw.Bot.Bodies;

/// <summary>Zero-token death-drop recovery for a brain's bodies (also runs in each hive). Never steals a busy body.</summary>
public sealed class DeathRecovery : IDisposable
{
    private sealed class Loss(string name, BlockPos spot, int dimension, DateTimeOffset until, WorldState world)
    {
        public readonly string Name = name;
        public readonly BlockPos Spot = spot;
        public readonly int Dimension = dimension;
        public readonly DateTimeOffset Until = until;
        public readonly (bool Lava, bool Void, int WaterDepth) Hazards = HazardsAt(world, spot);
        public IBody? Runner;
        public CancellationTokenSource? Stop;
    }

    private readonly object _gate = new();
    private readonly BodyRegistry _bodies;
    private readonly EventQueue _events;
    private readonly TimeProvider _time;
    private readonly List<Loss> _losses = [];
    private readonly List<(IBody Body, Action Died)> _watches = [];
    private readonly Timer _timer;
    private bool _disposed;
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    public DeathRecovery(BodyRegistry bodies, EventQueue events, TimeProvider? time = null)
    {
        _bodies = bodies;
        _events = events;
        _time = time ?? TimeProvider.System;
        _timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private sealed class WatchLease(DeathRecovery owner, IBody body) : IDisposable
    {
        public void Dispose() => owner.Unwatch(body);
    }

    public IDisposable Watch(IBody body)
    {
        Action died = () =>
        {
            var w = body.Adapter.World;
            lock (_gate)
            {
                if (_disposed) return;
                _losses.Add(new Loss(body.Name, w.Player.Position.ToBlockPos(), w.Dimension, _time.GetUtcNow() + TimeSpan.FromMinutes(5), w));
                _timer.Change(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500));
            }
            // A lane can be inside a host wait when the queued death event arrives. Pause it directly too, so its
            // continuation cannot keep the respawned body busy ahead of recovery (or resume over the recovery job).
            body.Scripts?.Current?.RequestPause(MineClaw.Scripting.PauseKind.Danger, "recovering death drops", abort: true);
        };
        lock (_gate)
        {
            if (_disposed || _watches.Any(w => ReferenceEquals(w.Body, body))) return new WatchLease(this, body);
            _watches.Add((body, died));
            body.Adapter.Died += died;
        }
        return new WatchLease(this, body);
    }

    private void Unwatch(IBody body)
    {
        lock (_gate)
        {
            foreach (var watch in _watches.Where(w => ReferenceEquals(w.Body, body)).ToArray())
            {
                body.Adapter.Died -= watch.Died;
                _watches.Remove(watch);
            }
            foreach (var loss in _losses.Where(l => ReferenceEquals(l.Runner, body))) loss.Stop?.Cancel();
        }
    }

    private void Report(Loss loss, string text) => _events.Post(GameEvent.Notice(
        $"[recovery] {loss.Name} at {loss.Spot.X},{loss.Spot.Y},{loss.Spot.Z}: {text}", EventPriority.Normal));

    private void Tick()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                foreach (var loss in _losses.ToArray())
                {
                    if (_time.GetUtcNow() >= loss.Until)
                    {
                        loss.Stop?.Cancel();
                        _losses.Remove(loss);
                        Report(loss, "expired: 5-minute drop window ended; gave up");
                        continue;
                    }
                    // Pause requests can race a host action publishing its abort token. Re-issue until the owner's lane
                    // reaches its safe point; never let a still-running lane occupy the respawned recovery body.
                    _bodies.All.FirstOrDefault(b => b.Name.Equals(loss.Name, StringComparison.OrdinalIgnoreCase))?.Scripts?.Current
                        ?.RequestPause(MineClaw.Scripting.PauseKind.Danger, "recovering death drops", abort: true);
                    if (loss.Runner is { } running)
                    {
                        if (!Safe(running, loss)) loss.Stop?.Cancel();
                        continue;
                    }
                    foreach (var body in _bodies.All.Where(b => Available(b, loss) && Safe(b, loss))
                        .OrderBy(b => b.Adapter.World.Player.Position.DistanceSquaredTo(loss.Spot.Center)))
                    {
                        loss.Runner = body;
                        loss.Stop = new CancellationTokenSource();
                        var tool = new DelegateTool("recover_death_drops", "Recover death drops", Empty,
                            (_, ct) => RecoverAsync(loss, body, ct), longRunning: true);
                        if (body.Activities.TryStartIdleJob(tool, Empty, "recover death drops")) break;
                        loss.Runner = null;
                        loss.Stop.Dispose();
                        loss.Stop = null;
                    }
                }
                if (_losses.Count == 0) _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception) { /* a timer must never bring down a hive; the next tick retries pending losses */ }
    }

    /// <summary>A dead body's unattended lane must not resume ahead of its own recovery.</summary>
    public bool IsHolding(IBody body)
    {
        lock (_gate) return _losses.Any(l => l.Name.Equals(body.Name, StringComparison.OrdinalIgnoreCase) || ReferenceEquals(l.Runner, body));
    }

    private static bool Available(IBody b, Loss loss)
    {
        var script = b.Scripts?.Current;
        return b.Adapter.IsPlaying && b.Adapter.CurrentAction == null && !b.Activities.IsBusy
            && (script == null || b.Name.Equals(loss.Name, StringComparison.OrdinalIgnoreCase) && script.Status == MineClaw.Scripting.ScriptStatus.Paused)
            && !b.Adapter.SafetyEngaged;
    }

    private static bool Safe(IBody body, Loss loss)
    {
        var c = body.Adapter;
        var w = c.World;
        var p = w.Player;
        if (!BodyFit(c, loss) || Hazardous(loss.Hazards, p.Health)) return false;
        if (HostileNear(w, p.Position) || HostileNear(w, loss.Spot.Center)) return false;
        if (loss.Spot.Y < 1 || loss.Spot.Y >= w.WorldHeight - 2) return false;
        // The corpse's chunk may be outside respawn view distance. Its captured hazards gate the trip; navigation only
        // walks loaded, safe ground, and the live destination is checked again as soon as it loads (and before pickup).
        if (!w.IsLoaded(loss.Spot.X, loss.Spot.Z)) return true;
        return !Hazardous(HazardsAt(w, loss.Spot), p.Health);
    }

    /// <summary>Playing, alive, no safety reflex running, near full health, in the loss's dimension.</summary>
    private static bool BodyFit(Game.IGameClient c, Loss loss) =>
        c.IsPlaying && !c.World.Player.IsDead && !c.SafetyEngaged && c.World.Player.Health >= 16 && c.World.Dimension == loss.Dimension;

    /// <summary>Lava, the void, or deep water without full health.</summary>
    private static bool Hazardous((bool Lava, bool Void, int WaterDepth) h, double health) =>
        h.Lava || h.Void || h.WaterDepth > 3 && health < 20;

    private static bool HostileNear(WorldState w, Vec3d at) => w.Entities.Nearest(at, 8, e => e.IsHostileMob) != null;

    private static (bool Lava, bool Void, int WaterDepth) HazardsAt(WorldState w, BlockPos spot)
    {
        bool lava = false;
        for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            lava |= BlockShapes.IsLava(w.GetBlock(spot.X + x, spot.Y + y, spot.Z + z));
        int top = spot.Y, bottom = spot.Y;
        while (top < w.WorldHeight - 1 && BlockShapes.IsWater(w.GetBlock(spot.X, top, spot.Z))) top++;
        while (bottom > 0 && BlockShapes.IsWater(w.GetBlock(spot.X, bottom - 1, spot.Z))) bottom--;
        return (lava, w.IsOverVoid(spot.X, spot.Y, spot.Z), top - bottom);
    }

    private async Task<ToolResult> RecoverAsync(Loss loss, IBody body, CancellationToken ct)
    {
        // Yield before calling a real action: TryStartIdleJob may run synchronously under the coordinator's lock.
        await Task.Yield();
        CancellationToken stop;
        lock (_gate) stop = loss.Stop!.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
        linked.CancelAfter(loss.Until - _time.GetUtcNow() > TimeSpan.Zero ? loss.Until - _time.GetUtcNow() : TimeSpan.FromMilliseconds(1));
        string result;
        try
        {
            using var scope = BodyScope.Push(body);
            if (!Safe(body, loss)) result = "stopped: unsafe death spot or body";
            else
            {
                var c = body.Game;
                // Ordinary navigation and collection retain their edge/lava/breath guards. The approach forbids building,
                // digging and parkour; pickup paths use the default goto options. Deep water requires an uninjured body.
                var go = await c.GotoAsync(new GoalNear(loss.Spot, 2), GotoOptions.Default with
                {
                    AllowPartial = true, AllowBreak = false, AllowPlace = false, Parkour = ParkourMode.Off,
                    MaxDrop = 1, MaxHeadUnderSteps = 0, Timeout = TimeSpan.FromSeconds(45),
                }, linked.Token).ConfigureAwait(false);
                if (!go.Ok) result = "couldn't reach drops safely: " + go.Message;
                else if (!body.Adapter.World.IsLoaded(loss.Spot.X, loss.Spot.Z) || !Safe(body, loss)) result = "stopped: death spot became unsafe or is still unloaded";
                else result = (await c.CollectDropsAsync(6, linked.Token).ConfigureAwait(false)).Message;
            }
        }
        catch (OperationCanceledException) { result = "stopped: cancelled, unsafe, or drop window ended"; }
        catch (Exception ex) { result = "failed: " + ex.Message; }
        finally
        {
            lock (_gate)
            {
                _losses.Remove(loss);
                loss.Stop?.Dispose();
                loss.Stop = null;
            }
        }
        Report(loss, $"{body.Short}: {result}");
        return ToolResult.Success(result); // reported once above, not a second detached-job failure wake
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            foreach (var (body, died) in _watches) body.Adapter.Died -= died;
            foreach (var loss in _losses) loss.Stop?.Cancel();
            _losses.Clear();
        }
    }
}
