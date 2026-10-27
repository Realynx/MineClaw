using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Beliefs;
using MineClaw.Bot.Game;
using MineClaw.Bot.WorldIndex;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Bot.Beliefs;

public sealed class BeliefCheckOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 20;
    /// <summary>Only claims this close get checked (the bot walks at most this far, never breaking or placing blocks).</summary>
    public double NearbyBlocks { get; set; } = 16;
    public double Reach { get; set; } = 4.0;
    public int MinHealth { get; set; } = 12;
    public int MinFood { get; set; } = 6;
    /// <summary>How long block changes are watched after pressing a button/lever.</summary>
    public int ObserveMs { get; set; } = 1500;
    public int DangerCooldownSeconds { get; set; } = 30;
    public int GotoTimeoutSeconds { get; set; } = 20;
    /// <summary>Changes farther than this from the trigger are ignored (other players building).</summary>
    public int ObserveRadius { get; set; } = 8;
}

/// <summary>
/// Bot side of the belief system: when the bot is idle, healthy, fed, out of danger and near a queued claim, it runs the
/// cheap check — open-and-look for container claims, press-and-watch for safe button/lever claims — and reports the result
/// to the <see cref="BeliefStore"/>. Zero tokens. Gating: only <see cref="ExperimentRisk.Safe"/> plans run (the store never
/// queues risky ones), movement never breaks or places blocks (protection can't be touched) and keeps the client's
/// EdgeGuard, hive leases are respected (skip when another bot holds the block/chest), levers are flipped back, and safety
/// claims are never planned at all.
/// </summary>
public sealed class BeliefChecker : IDisposable
{
    private static readonly HashSet<int> ContainerIds = [Blocks.Chest, Blocks.Furnace, Blocks.LitFurnace, Blocks.Dispenser];

    private readonly BeliefStore _store;
    private readonly IGameClient _client;
    private readonly IClaimBoard _claims;
    private readonly Func<bool> _busy;
    private readonly BeliefCheckOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private Timer? _timer;
    private int _running;
    private DateTimeOffset _lastDanger = DateTimeOffset.MinValue;
    private bool _disposed;

    public BeliefChecker(BeliefStore store, IGameClient client, IClaimBoard? claims = null, Func<bool>? busy = null,
        BeliefCheckOptions? options = null, TimeProvider? time = null, ILogger? log = null)
    {
        _store = store;
        _client = client;
        _claims = claims ?? NullClaimBoard.Instance;
        _busy = busy ?? (() => false);
        _o = options ?? new BeliefCheckOptions();
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _client.Danger += OnDanger;
    }

    public BeliefStore Store => _store;
    public BeliefCheckOptions Options => _o;

    private void OnDanger(DangerEvent _) => _lastDanger = _time.GetUtcNow();

    private WorldIndexer? _indexer;

    /// <summary>Judges open container claims from every live snapshot the indexer takes (zero tokens, no extra trips).</summary>
    public void Follow(WorldIndexer indexer)
    {
        _indexer = indexer;
        indexer.ContainerSnapshot += OnSnapshot;
    }

    private void OnSnapshot(string kind, string key, BlockPos at, IReadOnlyList<ItemCount> contents, bool first)
    {
        if (_disposed) return;
        try { _store.ContainerSnapshot(new ClaimPos(at.X, at.Y, at.Z), contents, kind); }
        catch (Exception ex) { _log.LogDebug(ex, "belief snapshot check failed"); }
    }

    public void Start()
    {
        if (!_o.Enabled || _timer != null) return;
        var every = TimeSpan.FromSeconds(Math.Max(5, _o.IntervalSeconds));
        _timer = new Timer(_ => _ = TickSafeAsync(), null, every, every);
    }

    private async Task TickSafeAsync()
    {
        try { await TickAsync(_cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { Dispose(); }
        catch (Exception ex) { _log.LogDebug(ex, "belief check failed"); }
    }

    /// <summary>Why a check can't run now (null = go).</summary>
    public string? Gate()
    {
        if (!_client.IsPlaying) return "offline";
        if (_client.CurrentAction != null || _busy()) return "busy";
        var p = _client.World.Player;
        if (p.Health < _o.MinHealth) return "low health";
        if (p.Food < _o.MinFood) return "hungry";
        if (_time.GetUtcNow() - _lastDanger < TimeSpan.FromSeconds(_o.DangerCooldownSeconds)) return "danger";
        if (_client.World.Inventory.OpenWindow != null) return "window open";
        return null;
    }

    /// <summary>One pass: re-checks location claims whose chunk is loaded, then runs the nearest queued safe check.</summary>
    /// <returns>What it did ("checked claim#3: verified"), "skip: why", or null when nothing was due.</returns>
    public async Task<string?> TickAsync(CancellationToken ct = default)
    {
        if (_disposed || Interlocked.Exchange(ref _running, 1) == 1) return null;
        try
        {
            if (Gate() is { } why) return "skip: " + why;
            var here = _client.World.Player.Position;
            var pending = _store.Pending(20);
            foreach (var c in pending.Where(c => c.Plan == ClaimPlan.Location && c.Pos is { } lp && _client.World.IsLoaded(lp.X, lp.Z)))
                _store.Reevaluate(c.Id);
            var next = pending
                .Where(c => c.Plan is ClaimPlan.Container or ClaimPlan.Experiment && c.Risk == ExperimentRisk.Safe && c.Pos != null
                            && c.Category != ClaimCategory.Safety)
                .Select(c => (Claim: c, D: c.Pos!.Value.DistanceTo(here.X, here.Y, here.Z)))
                .Where(t => t.D <= _o.NearbyBlocks)
                .OrderBy(t => t.D).FirstOrDefault();
            if (next.Claim == null) return null;
            return next.Claim.Plan == ClaimPlan.Container
                ? await CheckContainerAsync(next.Claim, next.Claim.Pos!.Value, ct).ConfigureAwait(false)
                : await ExperimentAsync(next.Claim, next.Claim.Pos!.Value, ct).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    private async Task<bool> ApproachAsync(ClaimPos p, CancellationToken ct)
    {
        var eye = _client.World.Player.EyePosition;
        if (p.DistanceTo(eye.X, eye.Y, eye.Z) <= _o.Reach) return true;
        var r = await _client.GotoAsync(new GoalWithinReach(new BlockPos(p.X, p.Y, p.Z), _o.Reach),
            new GotoOptions { AllowBreak = false, AllowPlace = false, AllowPartial = false, Timeout = TimeSpan.FromSeconds(_o.GotoTimeoutSeconds) }, ct)
            .ConfigureAwait(false);
        return r.Ok;
    }

    private string Done(BeliefClaim? c) => c == null ? "claim gone" : $"checked claim#{c.Id}: {BeliefRanks.Name(c.Status)}";

    private async Task<string> CheckContainerAsync(BeliefClaim c, ClaimPos p, CancellationToken ct)
    {
        var bp = new BlockPos(p.X, p.Y, p.Z);
        if (_client.World.IsLoaded(p.X, p.Z) && !ContainerIds.Contains(_client.World.GetBlock(bp)))
            return Done(_store.MarkAttempt(c.Id, "no container there"));
        if (!_claims.IsFree(ClaimKey.Chest(bp, _client.World.Dimension))) return "skip: claimed by another bot";
        if (!await ApproachAsync(p, ct).ConfigureAwait(false)) return Done(_store.MarkAttempt(c.Id, "couldn't reach it"));
        int id = _client.World.GetBlock(bp);
        var open = await _client.OpenContainerAsync(p.X, p.Y, p.Z, ct).ConfigureAwait(false);
        var w = _client.World.Inventory.OpenWindow;
        if (!open.Ok || w == null) return Done(_store.MarkAttempt(c.Id, "couldn't open it"));
        List<ItemCount> items;
        try
        {
            items = w.ContainerContents().Where(s => !s.Item.IsEmpty)
                .GroupBy(s => (s.Item.Id, s.Item.Damage))
                .Select(g => new ItemCount(g.Key.Id, Items.Exists(g.Key.Id) ? Items.Get(g.Key.Id).Name : g.Key.Id.ToString(), g.Sum(s => s.Item.Count), g.Key.Damage))
                .ToList();
        }
        finally { await _client.CloseWindowAsync(ct).ConfigureAwait(false); }
        var kind = id is Blocks.Furnace or Blocks.LitFurnace ? "furnace" : id == Blocks.Dispenser ? "dispenser" : "chest";
        return Done(_store.RecordContainerContents(c.Id, p, items, kind));
    }

    private async Task<string> ExperimentAsync(BeliefClaim c, ClaimPos p, CancellationToken ct)
    {
        var bp = new BlockPos(p.X, p.Y, p.Z);
        if (!_client.World.IsLoaded(p.X, p.Z)) return "skip: not loaded";
        int trigger = _client.World.GetBlock(bp);
        if (trigger is not (Blocks.StoneButton or Blocks.Lever)) return Done(_store.MarkAttempt(c.Id, "no button or lever there"));
        var key = ClaimKey.Block(bp, _client.World.Dimension);
        if (!_claims.IsFree(key)) return "skip: claimed by another bot";
        var lease = await _claims.TryClaimAsync(key, TimeSpan.FromSeconds(30), "belief experiment", ct).ConfigureAwait(false);
        if (!lease.Ok) return "skip: claimed by another bot";
        try
        {
            if (!await ApproachAsync(p, ct).ConfigureAwait(false)) return Done(_store.MarkAttempt(c.Id, "couldn't reach it"));
            var changes = new List<ObservedChange>();
            void On(BlockChange ch)
            {
                if (Math.Abs(ch.Pos.X - p.X) > _o.ObserveRadius || Math.Abs(ch.Pos.Y - p.Y) > _o.ObserveRadius || Math.Abs(ch.Pos.Z - p.Z) > _o.ObserveRadius) return;
                lock (changes) changes.Add(new ObservedChange(new ClaimPos(ch.Pos.X, ch.Pos.Y, ch.Pos.Z), ch.OldId, ch.OldMeta, ch.NewId, ch.NewMeta));
            }
            ActionResult used;
            _client.World.BlockChanged += On;
            try
            {
                used = await _client.ActivateBlockAsync(p.X, p.Y, p.Z, ct).ConfigureAwait(false);
                if (used.Ok) await Task.Delay(Math.Max(0, _o.ObserveMs), ct).ConfigureAwait(false);
            }
            finally { _client.World.BlockChanged -= On; }
            if (used.Ok && trigger == Blocks.Lever) // leave it as we found it
            {
                await _client.ActivateBlockAsync(p.X, p.Y, p.Z, ct).ConfigureAwait(false);
            }
            List<ObservedChange> seen;
            lock (changes) seen = changes.ToList();
            return Done(_store.RecordExperiment(c.Id, used.Ok, seen));
        }
        finally { await _claims.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Danger -= OnDanger;
        if (_indexer != null) _indexer.ContainerSnapshot -= OnSnapshot;
        _timer?.Dispose();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
    }
}
