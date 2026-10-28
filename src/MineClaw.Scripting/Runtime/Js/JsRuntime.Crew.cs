using System.Text.Json;
using Jint;
using System.Text.Json.Nodes;
using Jint.Native;
using Jint.Native.Object;
using MineClaw.Scripting.Crew;

namespace MineClaw.Scripting.Runtime.Js;

internal sealed partial class JsRuntime
{
    private string CrewId => s.Request.CrewId ?? $"{s.Name}#{s.Id}";
    private string Me => s.Host.Body ?? "main";

    private ICrewPort Port() => s.Host.Crew ?? throw ScriptSession.Bug("no crew here: this bot has one body (bodies() is empty)");

    /// <summary>The crew API: body handles, lanes (spawn / parallel), the shared board, leases.</summary>
    private void InstallCrew(ObjectInstance h)
    {
        H(h, "bodies", _ => V(new JsonArray((s.Host.Crew?.Bodies() ?? []).Select(b => (JsonNode?)JsonValue.Create(b)).ToArray())));
        H(h, "bodyInfo", a =>
        {
            var name = Str(a, 0) ?? "";
            var i = Port().Info(name) ?? throw ScriptSession.Bug($"no body '{name}' online");
            return V(new JsonObject
            {
                ["name"] = i.Name, ["hp"] = i.Hp, ["food"] = i.Food, ["pos"] = ScriptSession.PosNode(i.Pos.X, i.Pos.Y, i.Pos.Z),
                ["busy"] = i.Busy, ["job"] = i.Job, ["free"] = i.Free,
            });
        });
        H(h, "bodyInv", a => new JsNumber(Port().Count(Str(a, 0) ?? "", Str(a, 1) ?? throw ScriptSession.Bug("inv(item) needs an item name"))));
        H(h, "bodyAct", a =>
        {
            var body = Str(a, 0) ?? "";
            var action = Str(a, 1) ?? "";
            var c = s.Callables(_ => true).FirstOrDefault(x => x.Global == action || Camel(x.Global) == action)
                ?? throw ScriptSession.Bug($"{body}.{action}(): no such action");
            var args = JsConvert.Arg(a, 2) is JsArray arr ? arr.ToArray() : [];
            var json = MapArgs(c.Params, args);
            bool atomic = c.Action?.Atomic ?? s.Host.SkillIsAtomic(c.Target);
            return R(s.RunAction($"{body}.{c.Global}", atomic, ct => Port().ActAsync(body, c.Target, json, ct)));
        });

        H(h, "lane", a =>
        {
            var body = Str(a, 0) ?? throw ScriptSession.Bug("spawn(body, role, args) needs a body");
            var role = Str(a, 1) ?? throw ScriptSession.Bug("spawn(body, role, args) needs a role function name");
            var argsJson = JsConvert.ToNode(JsConvert.Arg(a, 2), 0, args: false)?.ToJsonString();
            var lane = Port().Start(new LaneSpec(body, s.Name, s.Code, s.Language, role, argsJson, CrewId, s.Request.Version, s.Request.Author));
            int id = ++_nextLane;
            _lanes[id] = lane;
            s.Log($"lane {body}: {role} started");
            return new JsNumber(id);
        });
        H(h, "laneWait", a =>
        {
            var lane = Lane(a);
            double? sec = Num(a, 1);
            return R(s.RunAction("lane " + lane.Body, false, async ct =>
            {
                try
                {
                    return sec is { } t ? await lane.Completion.WaitAsync(TimeSpan.FromSeconds(t), ct).ConfigureAwait(false)
                        : await lane.Completion.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (TimeoutException) { return ActResult.Fail($"lane {lane.Body} still running: {lane.Status}"); }
            }));
        });
        H(h, "laneJoin", a => Join(JsConvert.Arg(a, 0) is JsArray ids ? ids.Select(x => (int)x.AsNumber()).ToList() : [], JsConvert.Arg(a, 1).IsBoolean() && a[1].AsBoolean()));
        H(h, "laneCancel", a => { Lane(a).Cancel(Str(a, 1) ?? "cancelled by coordinator"); return JsValue.Undefined; });
        H(h, "laneStatus", a => new JsString(Lane(a).Status));

        H(h, "crewId", _ => new JsString(CrewId));
        H(h, "crewGet", a => Board().Get(CrewId, Str(a, 0) ?? "") is { } json ? JsConvert.FromJson(_e, json) : JsValue.Null);
        H(h, "crewSet", a =>
        {
            var json = JsConvert.ToNode(JsConvert.Arg(a, 1), 0, args: false)?.ToJsonString();
            if (!Board().Set(CrewId, Str(a, 0) ?? "", json)) throw ScriptSession.Bug($"crew board full ({InMemoryCrewBoard.MaxBytes} bytes)");
            return JsValue.Undefined;
        });
        H(h, "crewInc", a => new JsNumber(Board().Inc(CrewId, Str(a, 0) ?? "", Num(a, 1) ?? 1)));
        H(h, "crewSend", a =>
        {
            Board().Send(CrewId, Str(a, 0) ?? throw ScriptSession.Bug("crew.send(body, msg) needs a body"),
                JsConvert.ToNode(JsConvert.Arg(a, 1), 0, args: false)?.ToJsonString() ?? "null");
            return JsValue.Undefined;
        });
        H(h, "crewRecv", a =>
        {
            string? got = null;
            Poll("crew.recv", Num(a, 0) ?? 60, () => (got = Board().TryReceive(CrewId, Me)) != null);
            return got != null ? JsConvert.FromJson(_e, got) : JsValue.Null;
        });
        H(h, "crewBarrier", a =>
        {
            var name = "barrier:" + (Str(a, 0) ?? "b");
            int n = (int)(Num(a, 1) ?? 2);
            Board().Inc(CrewId, name, 1);
            bool met = Poll("crew.barrier", Num(a, 2) ?? 300, () => Count(name) >= n);
            return met ? JsBoolean.True : JsBoolean.False;
        });
        H(h, "crewLock", a => Board().TryLock(CrewId, Str(a, 0) ?? "", Me, TimeSpan.FromSeconds(Num(a, 1) ?? 120)) ? JsBoolean.True : JsBoolean.False);
        H(h, "crewUnlock", a => { Board().Unlock(CrewId, Str(a, 0) ?? "", Me); return JsValue.Undefined; });
    }

    private ICrewBoard Board() => s.Host.Crew?.Board ?? (_localBoard ??= new InMemoryCrewBoard(s.Time));
    private InMemoryCrewBoard? _localBoard;

    private double Count(string key) =>
        Board().Get(CrewId, key) is { } v && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    private ILane Lane(JsValue[] a) =>
        Num(a, 0) is { } id && _lanes.TryGetValue((int)id, out var l) ? l : throw ScriptSession.Bug("unknown lane");

    /// <summary>Waits (an interruptible action, ≤ s seconds) until <paramref name="cond"/> holds; false on timeout.</summary>
    private bool Poll(string label, double seconds, Func<bool> cond)
    {
        if (cond()) return true;
        DateTimeOffset? deadline = null;
        var r = s.RunAction(label, false, async ct =>
        {
            var end = deadline ??= s.Time.GetUtcNow() + TimeSpan.FromSeconds(Math.Clamp(seconds, 0.1, s.Options.MaxWaitSeconds));
            while (true)
            {
                if (cond()) return ActResult.Success("ok");
                if (s.Time.GetUtcNow() >= end) return ActResult.Fail("timeout");
                await Task.Delay(TimeSpan.FromMilliseconds(200), s.Time, ct).ConfigureAwait(false);
            }
        });
        return r.Ok;
    }

    /// <summary>
    /// <c>parallel()</c>: waits for every lane (an interruptible action: a danger on this body cuts the wait, the lanes keep
    /// going). The first failure cancels the rest and fails this script, unless keepGoing (then its result reads "failed: …").
    /// </summary>
    private JsValue Join(List<int> ids, bool keepGoing)
    {
        var lanes = ids.Select(i => _lanes.TryGetValue(i, out var l) ? l : throw ScriptSession.Bug("unknown lane")).ToList();
        string? failure = null;
        s.RunAction("parallel", false, async ct =>
        {
            var pending = lanes.ToList();
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending.Select(l => l.Completion)).WaitAsync(ct).ConfigureAwait(false);
                var lane = pending.First(l => l.Completion == done);
                pending.Remove(lane);
                var r = await done.ConfigureAwait(false);
                if (!r.Ok && !keepGoing)
                {
                    failure = $"lane {lane.Body} failed: {r.Msg}";
                    foreach (var other in pending) other.Cancel("sibling lane failed");
                    return ActResult.Fail(failure);
                }
            }
            return ActResult.Success($"{lanes.Count} lanes done");
        });
        if (failure != null) throw s.Fail(failure);
        var results = lanes.Select(l => l.Completion.IsCompletedSuccessfully
            ? (l.Completion.Result.Ok ? l.Completion.Result.Msg : "failed: " + l.Completion.Result.Msg) : "running").ToList();
        return V(new JsonArray(results.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()));
    }
}
