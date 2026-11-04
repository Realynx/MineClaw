using System.Globalization;
using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using MineClaw.Data;
using MineClaw.Scripting;
using MineClaw.Scripting.Crew;
using MineClaw.World;

namespace MineClaw.Bot.Bodies;

/// <summary>
/// The crew board in the shared SQLite db: hive clones are separate processes on the same db file (WAL), so every lane sees
/// one board, one inbox per body and one set of leases. Tables are created on first use (idempotent). Values ≤ 4 KB per crew.
/// </summary>
public sealed class DbCrewBoard : ICrewBoard
{
    private readonly AgentDb _db;
    private readonly TimeProvider _time;
    private bool _ready;

    public DbCrewBoard(AgentDb db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    private void Ensure()
    {
        if (_ready) return;
        _db.Exec("CREATE TABLE IF NOT EXISTS crew_board(crew TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, updated INTEGER NOT NULL, PRIMARY KEY(crew, key))");
        _db.Exec("CREATE TABLE IF NOT EXISTS crew_inbox(id INTEGER PRIMARY KEY AUTOINCREMENT, crew TEXT NOT NULL, to_body TEXT NOT NULL, value TEXT NOT NULL)");
        _db.Exec("CREATE TABLE IF NOT EXISTS crew_locks(crew TEXT NOT NULL, key TEXT NOT NULL, owner TEXT NOT NULL, until INTEGER NOT NULL, PRIMARY KEY(crew, key))");
        _ready = true;
    }

    private long Now => AgentDb.ToUnixMs(_time.GetUtcNow());

    public string? Get(string crew, string key)
    {
        Ensure();
        return _db.Scalar("SELECT value FROM crew_board WHERE crew = $c AND key = $k", ("$c", crew), ("$k", key)) as string;
    }

    public bool Set(string crew, string key, string? json)
    {
        Ensure();
        return _db.Tx(() =>
        {
            if (json == null) { _db.Exec("DELETE FROM crew_board WHERE crew = $c AND key = $k", ("$c", crew), ("$k", key)); return true; }
            long size = _db.ScalarLong("SELECT COALESCE(SUM(LENGTH(key) + LENGTH(value)), 0) FROM crew_board WHERE crew = $c AND key <> $k", ("$c", crew), ("$k", key));
            if (size + key.Length + json.Length > InMemoryCrewBoard.MaxBytes) return false;
            _db.Exec("INSERT INTO crew_board(crew, key, value, updated) VALUES ($c, $k, $v, $u) ON CONFLICT(crew, key) DO UPDATE SET value = excluded.value, updated = excluded.updated",
                ("$c", crew), ("$k", key), ("$v", json), ("$u", Now));
            return true;
        });
    }

    public double Inc(string crew, string key, double by)
    {
        Ensure();
        return _db.Tx(() =>
        {
            double cur = Get(crew, key) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            cur += by;
            Set(crew, key, JsonSerializer.Serialize(cur));
            return cur;
        });
    }

    public void Send(string crew, string to, string json)
    {
        Ensure();
        _db.Exec("INSERT INTO crew_inbox(crew, to_body, value) VALUES ($c, $t, $v)", ("$c", crew), ("$t", to.ToLowerInvariant()), ("$v", json));
    }

    public string? TryReceive(string crew, string me)
    {
        Ensure();
        return _db.Tx(() =>
        {
            var row = _db.Query("SELECT id, value FROM crew_inbox WHERE crew = $c AND to_body = $t ORDER BY id LIMIT 1", r => (r.GetInt64(0), r.GetString(1)),
                ("$c", crew), ("$t", me.ToLowerInvariant())).FirstOrDefault();
            if (row.Item2 == null) return null;
            _db.Exec("DELETE FROM crew_inbox WHERE id = $i", ("$i", row.Item1));
            return row.Item2;
        });
    }

    public bool TryLock(string crew, string key, string owner, TimeSpan ttl)
    {
        Ensure();
        return _db.Tx(() =>
        {
            var cur = _db.Query("SELECT owner, until FROM crew_locks WHERE crew = $c AND key = $k", r => (r.GetString(0), r.GetInt64(1)), ("$c", crew), ("$k", key)).FirstOrDefault();
            if (cur.Item1 != null && cur.Item2 > Now && !string.Equals(cur.Item1, owner, StringComparison.OrdinalIgnoreCase)) return false;
            _db.Exec("INSERT INTO crew_locks(crew, key, owner, until) VALUES ($c, $k, $o, $u) ON CONFLICT(crew, key) DO UPDATE SET owner = excluded.owner, until = excluded.until",
                ("$c", crew), ("$k", key), ("$o", owner), ("$u", Now + (long)ttl.TotalMilliseconds));
            return true;
        });
    }

    public void Unlock(string crew, string key, string owner)
    {
        Ensure();
        _db.Exec("DELETE FROM crew_locks WHERE crew = $c AND key = $k AND owner = $o COLLATE NOCASE", ("$c", crew), ("$k", key), ("$o", owner));
    }
}

/// <summary>
/// Crew lanes on in-process bodies (Bodies:Mode=inproc): a lane is a script run on that body's own lane (its ScriptBridge, its
/// activity slot, its interrupts: only its body's dangers and chat naming it). A body that is busy (a job, a script, offline)
/// answers a failed lane at once. Direct actions (<c>body("67").dig(...)</c>) run on an idle body inside its body scope.
/// </summary>
public sealed class BodyCrewPort(BodyRegistry registry, ToolRegistry tools, ICrewBoard board) : ICrewPort
{
    public ICrewBoard Board { get; } = board;

    /// <summary>Bodies this port never hands out (main's own crews skip the bodies a hive script owns). Null = none.</summary>
    public Func<string, bool>? Excluded { get; set; }

    public IReadOnlyList<string> Bodies() => registry.All.Where(b => !b.IsMain && b.State == BodyState.Online && Excluded?.Invoke(b.Name) != true)
        .Select(b => b.Short).ToList();

    private Body? Find(string name) => registry.Find(name) as Body;

    public BodyInfo? Info(string body)
    {
        if (Find(body) is not { } b) return null;
        var w = b.Adapter.World;
        var p = w.Player.Position;
        var job = b.ScriptBridge?.Controller.Current?.Name is { } sn ? "script " + sn : b.Activities.Current?.Label;
        return new BodyInfo(b.Short, w.Player.Health, w.Player.Food, new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z)),
            job != null, job, w.Inventory.EmptySlotCount());
    }

    public int Count(string body, string item)
    {
        if (Find(body) is not { } b) return -1;
        var it = Items.ByNameWithDamage(item);
        return it is { } i ? b.Adapter.World.Inventory.CountItem(i.Item.Id, i.Damage >= 0 ? i.Damage : null) : -1;
    }

    public string? HeldBy(string body) => Find(body)?.LiveCrew;

    public bool Hold(string body, string crew)
    {
        if (Find(body) is not { } b) return false;
        lock (b)
        {
            if (b.LiveCrew is { } other && !string.Equals(other, crew, StringComparison.Ordinal)) return false;
            b.Crew = crew;
            return true;
        }
    }

    public ILane Start(LaneSpec spec)
    {
        if (Find(spec.Body) is not { ScriptBridge: { } lane } b) return Lanes.Failed(spec.Body, $"no body {spec.Body} online");
        if (b.State != BodyState.Online) return Lanes.Failed(spec.Body, $"body {spec.Body} is {b.State.ToString().ToLowerInvariant()}");
        // One body, one crew: another live crew's body (held between its lanes too, e.g. in an everyBody retry pause) isn't taken.
        if (!Hold(b.Short, spec.CrewId)) return Lanes.Failed(spec.Body, $"body {spec.Body} is in crew {b.LiveCrew}");
        TakeOver(b, lane, spec.CrewId);
        var busy = lane.Controller.Current is { } cur ? "script " + cur.State() : b.Activities.Current?.Label;
        if (busy != null) return Lanes.Failed(spec.Body, $"body {spec.Body} busy: {ScriptText.Cap(busy, 80)}");
        lane.Crew ??= this; // the lane's crew.* calls see the coordinator's board
        var (s, err) = lane.StartLane(new ScriptRequest
        {
            Name = spec.Script, Code = spec.Code, Language = spec.Language, Role = spec.Role, ArgsJson = spec.ArgsJson, CrewId = spec.CrewId,
            Version = spec.Version, Author = spec.Author, Coordinator = spec.Coordinator,
        }, b);
        return s == null ? Lanes.Failed(spec.Body, err ?? "can't start") : Lanes.Of(spec.Body, s);
    }

    /// <summary>
    /// A crew takes over a body busy with something that isn't a lane of a live crew (its own leftover script run, an old
    /// standing order's job): the crew is the newer order from the same brain (live 10-30: 1337 and 126 kept running
    /// last crew's "deforest" at "felled 0" and refused the new crew's lane every 30 s, so they stood still for good).
    /// </summary>
    private static void TakeOver(Body b, GameTools.ScriptBridge lane, string crewId)
    {
        if (lane.Controller.Current is { } old && old.Request.CrewId != crewId && !LiveLane(old)) lane.Controller.Cancel("taken over by crew " + crewId);
        else if (lane.Controller.Current is null && b.Activities.Current is not null) b.Activities.Cancel("taken over by crew " + crewId);
        for (int i = 0; i < 60 && BusyElsewhere(b, lane, crewId); i++)
            Thread.Sleep(50); // the old run stops at its next safe point
    }

    /// <summary>Still running another crew's script, or (no script) an activity.</summary>
    private static bool BusyElsewhere(Body b, GameTools.ScriptBridge lane, string crewId) =>
        lane.Controller.Current is { } c0 ? c0.Request.CrewId != crewId : b.Activities.Current is not null;

    /// <summary>A lane of another crew whose coordinator still runs: not ours to take (one body, one crew).</summary>
    private static bool LiveLane(MineClaw.Scripting.ScriptSession s) => s.Request.CrewId is { Length: > 0 } id && MineClaw.Scripting.Crew.Crews.IsLive(id);

    public async Task<ActResult> ActAsync(string body, string action, JsonElement args, CancellationToken ct)
    {
        if (Find(body) is not { } b) return ActResult.Fail($"no body {body} online");
        if (b.ScriptBridge?.Controller.Current is { } s) return ActResult.Fail($"body {body} busy: script {s.Name}");
        if (b.Activities.Current is { } a) return ActResult.Fail($"body {body} busy: {a.Label}");
        using (BodyScope.Push(b))
        {
            var r = await tools.ExecuteAsync(action, args, ct).ConfigureAwait(false);
            return new ActResult(r.Ok, r.Text);
        }
    }
}

/// <summary>
/// One crew's view of its process's bodies: a hive script's (<c>hive spawn kind=script bodies=N</c>: exactly the accounts allocated to
/// it), a brain's crew over a body list or "all" (<c>script{name, body:[…]}</c>), or the bodies a hive spawned straight onto a script.
/// Only the members online now, so <c>bodies()</c> grows as late logins arrive and shrinks as bodies leave; with <paramref name="crew"/>
/// a body another live crew holds is left out (one body, one crew). Lanes start only on its own bodies.
/// </summary>
/// <param name="pending">Bodies still to come (null = its members not online yet).</param>
public sealed class SubsetCrewPort(ICrewPort inner, Func<IReadOnlyCollection<string>> members, string? crew = null, Func<int>? pending = null) : ICrewPort
{
    public ICrewBoard Board => inner.Board;

    private bool Member(string body) => members().Any(m => string.Equals(m, body, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Body.ShortName(m, false), body, StringComparison.OrdinalIgnoreCase));

    private bool Mine(string body) => Member(body) && (crew == null || inner.HeldBy(body) is not { } h || string.Equals(h, crew, StringComparison.Ordinal));

    public IReadOnlyList<string> Bodies() => inner.Bodies().Where(Mine).ToList();

    /// <summary>Its accounts not online yet (allocated, still logging in).</summary>
    public int Pending()
    {
        if (pending != null) return pending();
        var on = inner.Bodies();
        return members().Count(m => !on.Contains(Body.ShortName(m, false), StringComparer.OrdinalIgnoreCase) && !on.Contains(m, StringComparer.OrdinalIgnoreCase));
    }
    public BodyInfo? Info(string body) => Mine(body) ? inner.Info(body) : null;
    public int Count(string body, string item) => Mine(body) ? inner.Count(body, item) : -1;
    public ILane Start(LaneSpec spec) => Mine(spec.Body) ? inner.Start(spec) : Lanes.Failed(spec.Body, $"{spec.Body} isn't one of this crew's bodies");
    public Task<ActResult> ActAsync(string body, string action, JsonElement args, CancellationToken ct) =>
        Mine(body) ? inner.ActAsync(body, action, args, ct) : Task.FromResult(ActResult.Fail($"{body} isn't one of this crew's bodies"));
    public string? HeldBy(string body) => inner.HeldBy(body);
    public bool Hold(string body, string crewId) => Member(body) && inner.Hold(body, crewId);
}

/// <summary>The lane handles the ports hand out.</summary>
public static class Lanes
{
    public static ILane Failed(string body, string why) => new Lane(body, null, Task.FromResult(ActResult.Fail(why)), () => why);

    public static ILane Of(string body, ScriptSession s) =>
        new Lane(body, r => s.Stop(r), s.Completion.ContinueWith(_ => new ActResult(s.Ok, s.LaneResult), TaskScheduler.Default), s.State);

    public static ILane Of(string body, Action<string>? cancel, Task<ActResult> done, Func<string> status) => new Lane(body, cancel, done, status);

    private sealed class Lane(string body, Action<string>? cancel, Task<ActResult> done, Func<string> status) : ILane
    {
        public string Body => body;
        public string Status => done.IsCompleted ? (done.Result.Ok ? "done" : "failed: " + done.Result.Msg) : status();
        public Task<ActResult> Completion => done;
        public void Cancel(string reason) => cancel?.Invoke(reason);
    }
}

/// <summary>
/// A clone process's side of a crew: no bodies of its own to start (its lane is the one main started), but the same db board.
/// </summary>
public sealed class LaneOnlyCrewPort(ICrewBoard board) : ICrewPort
{
    public ICrewBoard Board { get; } = board;
    public IReadOnlyList<string> Bodies() => [];
    public BodyInfo? Info(string body) => null;
    public int Count(string body, string item) => -1;
    public ILane Start(LaneSpec spec) => Lanes.Failed(spec.Body, "lanes start other lanes only from main (the coordinator)");
    public Task<ActResult> ActAsync(string body, string action, JsonElement args, CancellationToken ct) =>
        Task.FromResult(ActResult.Fail("a hive lane acts only on its own body (main coordinates the crew)"));
}
