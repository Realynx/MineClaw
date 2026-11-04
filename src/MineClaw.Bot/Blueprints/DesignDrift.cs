using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Designs;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// Change detection for saved designs (ARCHITECTURE.md "Design revisions", drift): the world (this body's chunk view) against
/// each standing design's HEAD (captured or built ones), when a body sees a block change inside one or loads its chunks. Every
/// changed cell is classified when it happens: <b>me</b> (this body's own dig/place, the self-edit log; the hive's own placement
/// ledger), an <b>explosion</b> (the blast log), <b>fire</b>, an <b>enderman</b> beside it, a <b>player</b> within reach (by
/// name), else <b>unknown</b> (it changed while nobody watched: found on load). Once the changes settle the brain gets one
/// cheap line per burst, aggregated per design ("[design] #12 spiral_staircase drifted from r2: −3 stone (Fox 2, me 1);
/// commit{id=12} keeps it, revert{id=12} restores"), at most every <see cref="MinGapSeconds"/> per design and never while its
/// own build/revert job runs. Nothing is committed or restored on its own: a player's edit (or a griefer's) waits for a
/// trusted word. The last world view goes to the db (<see cref="DesignRevisions.SaveWorld"/>) for the Memory Explorer.
/// </summary>
public sealed class DesignDrift : IDisposable
{
    private sealed record Tracked(long Id, string Name, int Rev, BlockPos Min, BlockPos Max, int Dim);

    private readonly BlueprintService _service;
    private readonly DesignHistory _history;
    private readonly WorldState _world;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private volatile Tracked[] _tracked = [];
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<BlockPos, string>> _causes = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _dirty = new();
    private readonly ConcurrentDictionary<long, (string Sig, DateTimeOffset At)> _reported = new();
    private DateTimeOffset _refreshed = DateTimeOffset.MinValue;
    private readonly Timer? _timer;
    private int _ticking;

    public DesignDrift(BlueprintService service, DesignHistory history, WorldState world, TimeProvider? time = null, ILogger? log = null, bool timer = true)
    {
        _service = service;
        _history = history;
        _world = world;
        _time = time ?? TimeProvider.System;
        _log = log;
        world.BlockChanged += OnChanged;
        world.ColumnLoaded += OnLoaded;
        history.Causes = Causes;
        history.AllOwn = AllOwn;
        if (timer) _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
    }

    /// <summary>The one perception line ("[design] …"): the host posts it as a Background notice (no wake).</summary>
    public Action<string>? Notice { get; set; }
    /// <summary>This bot's name (its own edits read "me").</summary>
    public Func<string> Self { get; set; } = () => "me";
    /// <summary>A hive bot's name (another body's change reads as that body, the hive's own).</summary>
    public Func<string, bool> IsBot { get; set; } = _ => false;
    /// <summary>A design whose build/revert/undo job runs now: its changes are the job's, not drift.</summary>
    public Func<long, bool> Busy { get; set; } = _ => false;
    /// <summary>The same design's drift line again only after this long (and only when the drift itself changed).</summary>
    public int MinGapSeconds { get; set; } = 120;
    /// <summary>Changes settle this long before the design is compared (a player digging a column is one burst).</summary>
    public int SettleSeconds { get; set; } = 4;

    public void Dispose()
    {
        _world.BlockChanged -= OnChanged;
        _world.ColumnLoaded -= OnLoaded;
        _timer?.Dispose();
    }

    /// <summary>Re-reads which designs stand (captured/built with a history) and their boxes (grown by 1: an added step counts).</summary>
    public void Refresh()
    {
        var list = new List<Tracked>();
        try
        {
            foreach (var id in _history.Revisions.Designs())
            {
                if (_service.Store.Get(id) is not { } rec || rec.Status is not (DesignStatus.Captured or DesignStatus.Built)) continue;
                if (_history.Revisions.Head(id) is not { } head || _history.Revisions.Snapshot(id, head.Rev)?.Box is not { } box) continue;
                list.Add(new Tracked(id, rec.Name, head.Rev, new BlockPos(box.Min.X - 1, box.Min.Y - 1, box.Min.Z - 1),
                    new BlockPos(box.Max.X + 1, box.Max.Y + 1, box.Max.Z + 1), head.Dim));
            }
        }
        catch (Exception ex) { _log?.LogDebug(ex, "design drift refresh failed"); return; }
        _tracked = list.ToArray();
        _refreshed = _time.GetUtcNow();
    }

    /// <summary>A standing design whose box (grown by <paramref name="pad"/>) holds <paramref name="p"/>, or null.</summary>
    public (long Id, string Name)? At(BlockPos p, int pad = 0)
    {
        foreach (var t in _tracked)
            if (t.Dim == _world.Dimension && p.X >= t.Min.X - pad && p.X <= t.Max.X + pad && p.Y >= t.Min.Y - pad && p.Y <= t.Max.Y + pad
                && p.Z >= t.Min.Z - pad && p.Z <= t.Max.Z + pad) return (t.Id, t.Name);
        return null;
    }

    // ------------------------------------------------------------------ what changed, and who did it

    private void OnChanged(BlockChange ch)
    {
        var tracked = _tracked;
        if (tracked.Length == 0) return;
        foreach (var t in tracked)
        {
            if (t.Dim != _world.Dimension || !Inside(ch.Pos, t.Min, t.Max)) continue;
            string cause;
            try { cause = Cause(ch); } catch (Exception) { cause = "unknown"; }
            _causes.GetOrAdd(t.Id, _ => new())[ch.Pos] = cause; // the latest change at a cell says who changed it
            _dirty[t.Id] = _time.GetUtcNow();
        }
    }

    private void OnLoaded(int cx, int cz)
    {
        foreach (var t in _tracked)
            if (t.Dim == _world.Dimension && cx * 16 + 15 >= t.Min.X && cx * 16 <= t.Max.X && cz * 16 + 15 >= t.Min.Z && cz * 16 <= t.Max.Z)
                _dirty.TryAdd(t.Id, _time.GetUtcNow()); // what changed while nobody watched (causes stay unknown)
    }

    /// <summary>Who changed a cell, decided when the change arrives (the network thread; cheap lookups only).</summary>
    internal string Cause(BlockChange ch)
    {
        var p = ch.Pos;
        if (_world.SelfEdits.IsRecent(p)) return Self();
        if (PlacedBy(ch) is { } placer) return placer;
        if (_world.Blasts.Near(p)) return "an explosion";
        if (ch.NewId == Blocks.Fire || ch.NewId == 0 && BlockFaceExtensions.All.Any(f => _world.GetBlock(p.Offset(f)) == Blocks.Fire)) return "fire";
        return NearbyCause(p);
    }

    /// <summary>The bot that placed this very block (this one when the log names nobody), or null.</summary>
    private string? PlacedBy(BlockChange ch)
    {
        if (ch.NewId == 0 || _world.Placed.Get(ch.Pos, _world.Dimension) is not { } pl || !PlacedBlockLog.SameBlock(pl.BlockId, ch.NewId)) return null;
        return pl.Bot is { Length: > 0 } by ? by : Self();
    }

    /// <summary>An enderman within 4, else the nearest other player within 6, else "unknown".</summary>
    private string NearbyCause(BlockPos p)
    {
        var at = new Vec3d(p.X + 0.5, p.Y + 0.5, p.Z + 0.5);
        if (_world.Entities.Nearby(at, 4, e => e.Kind == EntityKind.Mob && e.DisplayName.Contains("enderman", StringComparison.OrdinalIgnoreCase), 1).Count > 0)
            return "an enderman";
        var player = _world.Entities.Nearby(at, 6, e => e.Kind == EntityKind.Player && e.Name is { Length: > 0 } n
            && !n.Equals(Self(), StringComparison.OrdinalIgnoreCase), 1).FirstOrDefault();
        return player?.Name ?? "unknown";
    }

    /// <summary>"Fox 2, me 1, unknown 3" for the drifted cells of a diff (cells nobody saw change: unknown).</summary>
    public string? Causes(long id, DesignDiff d)
    {
        if (d.Empty) return null;
        _causes.TryGetValue(id, out var map);
        var self = Self();
        var g = d.All.Select(c => map != null && map.TryGetValue(c.Pos, out var who) ? who : "unknown")
            .Select(w => w.Equals(self, StringComparison.OrdinalIgnoreCase) ? "me" : w)
            .GroupBy(w => w).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} {x.Count()}");
        return string.Join(", ", g);
    }

    /// <summary>Every drifted cell was this bot's or a hive body's own doing (a commit of its requested edit needs no trusted word).</summary>
    public bool AllOwn(long id, DesignDiff d)
    {
        if (d.Empty || !_causes.TryGetValue(id, out var map)) return false;
        var self = Self();
        return d.All.All(c => map.TryGetValue(c.Pos, out var who) && (who.Equals(self, StringComparison.OrdinalIgnoreCase) || IsBot(who)));
    }

    // ------------------------------------------------------------------ the line

    /// <summary>One pass: settled designs compared with HEAD; drifted ones reported in one aggregated line. Returns the line (tests).</summary>
    public string? Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return null;
        try { return TickOnce(); }
        catch (Exception ex) { _log?.LogDebug(ex, "design drift tick failed"); return null; }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    private string? TickOnce()
    {
        var now = _time.GetUtcNow();
        if (now - _refreshed > TimeSpan.FromSeconds(60)) Refresh();
        var lines = new List<string>();
        foreach (var (id, at) in _dirty.ToArray())
            if (DriftLine(id, at, now) is { } l) lines.Add(l);
        if (lines.Count == 0) return null;
        var line = "[design] " + string.Join(" | ", lines);
        Notice?.Invoke(line);
        _log?.LogInformation("design drift: {Line}", line);
        return line;
    }

    /// <summary>A settled, idle design compared with HEAD: its drift line (and the world view saved), or null (no drift, or the
    /// same drift already said / said too recently).</summary>
    private string? DriftLine(long id, DateTimeOffset at, DateTimeOffset now)
    {
        if (now - at < TimeSpan.FromSeconds(SettleSeconds) || Busy(id)) return null;
        _dirty.TryRemove(id, out _);
        if (_service.Store.Get(id) is not { } rec || _history.AgainstWorld(rec) is not { } aw) return null;
        var d = aw.Diff;
        if (d.Empty)
        {
            _causes.TryRemove(id, out _);
            _reported.TryRemove(id, out _);
            return null;
        }
        var sig = DesignSnapshot.EncodeCells(d.All.Select(c => c.Pos)) + d.Text(9);
        if (SaidAlready(id, at, now, sig)) return null;
        _reported[id] = (sig, now);
        var who = Causes(id, d);
        var line = $"#{id} {rec.Name} drifted from r{aw.Rev.Rev}: {d.Text()} ({who})" + (IsDamage(who)
            ? $"; revert{{id={id}}} restores, commit{{id={id}}} keeps"
            : $"; commit{{id={id}}} keeps, revert{{id={id}}} restores");
        try { SaveWorld(rec, aw.Rev.Rev, d, who ?? ""); } catch (Exception ex) { _log?.LogDebug(ex, "design world view not saved"); }
        return line;
    }

    /// <summary>This drift was said already, or another one too recently (then it is said again once the gap is over).</summary>
    private bool SaidAlready(long id, DateTimeOffset at, DateTimeOffset now, string sig)
    {
        if (!_reported.TryGetValue(id, out var last) || last.Sig != sig && now - last.At >= TimeSpan.FromSeconds(MinGapSeconds)) return false;
        if (last.Sig != sig) _dirty.TryAdd(id, at); // again once the gap is over
        return true;
    }

    /// <summary>Damage (an explosion, fire, an enderman) suggests revert first; anything else, commit first.</summary>
    private static bool IsDamage(string? who) =>
        who != null && (who.Contains("explosion", StringComparison.Ordinal) || who.Contains("fire", StringComparison.Ordinal) || who.Contains("enderman", StringComparison.Ordinal));

    /// <summary>The world as this body sees the design's cells now (its HEAD's block and air cells), for the Explorer.</summary>
    private void SaveWorld(BlueprintRecord rec, int rev, DesignDiff d, string causes)
    {
        if (_history.Revisions.Snapshot(rec.Id, rev) is not { } head) return;
        var w = _world.PlanningView;
        var seen = new DesignSnapshot();
        foreach (var p in head.Blocks.Keys.Concat(head.Air))
        {
            if (!w.IsLoaded(p.X, p.Z)) continue;
            int id = w.GetBlock(p.X, p.Y, p.Z);
            if (id == 0) seen.Air.Add(p);
            else seen.Blocks[p] = (id, w.GetMeta(p.X, p.Y, p.Z));
        }
        _history.Revisions.SaveWorld(rec.Id, rev, seen, Self(), d.Text(), causes);
    }

    private static bool Inside(BlockPos p, BlockPos min, BlockPos max) =>
        p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;
}
