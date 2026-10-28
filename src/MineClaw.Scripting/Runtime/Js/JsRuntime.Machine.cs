using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using MineClaw.Scripting.Sdk;

namespace MineClaw.Scripting.Runtime.Js;

internal sealed partial class JsRuntime
{
    /// <summary><c>machine(spec)</c>: the spec's shape → <see cref="MachineSpec"/>, its callbacks → a <see cref="JsMachineHost"/>; runs it.</summary>
    private string RunMachine(string id, string initial, JsValue specV, JsValue bbV)
    {
        var spec = specV as ObjectInstance ?? throw ScriptSession.Bug("machine(spec) needs an object");
        var bb = bbV as ObjectInstance ?? new JsObject(_e);
        var states = spec.Get("states") as ObjectInstance ?? throw ScriptSession.Bug("machine({states}) needs states");
        var guards = spec.Get("guards") as ObjectInstance;
        var dict = new Dictionary<string, StateSpec>(StringComparer.Ordinal);
        foreach (var key in states.GetOwnPropertyKeys(Types.String))
        {
            var name = key.ToString();
            var st = states.Get(key) as ObjectInstance;
            double? idle = st != null && st.Get("idleMs").IsNumber() ? st.Get("idleMs").AsNumber() : null;
            dict[name] = new StateSpec(name, st?.Get("enter") is Function, st?.Get("tick") is Function, st?.Get("exit") is Function,
                st != null ? Transitions(st.Get("on"), id, name) : [], st != null && TypeConverter.ToBoolean(st.Get("final")), idle);
        }
        var global = Transitions(spec.Get("on"), id, null);
        var ms = new MachineSpec(id, initial, dict, global, JsConvert.ToNode(spec.Get("thresholds"), 0, args: false) as JsonObject);
        var ctx = new JsObject(_e);
        ctx.Set("id", new JsString(id));
        ctx.Set("bb", bb);
        ctx.Set("state", new JsString(initial));
        ctx.Set("history", new JsArray(_e));
        var host = new JsMachineHost(this, states, guards, bb, ctx);
        var runner = new MachineRunner(s, ms, host);
        ctx.Set("go", new ClrFunction(_e, "go", (_, a) => throw new ScriptUnwind(runner, JsConvert.Str(JsConvert.Arg(a, 0)) ?? "", "m.go")));
        return runner.Run();
    }

    /// <summary>An <c>on</c> table: event key → 'target' | fn(ev,bb,m) → target | {to, guard|if, do}.</summary>
    private List<TransitionSpec> Transitions(JsValue onV, string machine, string? state)
    {
        var list = new List<TransitionSpec>();
        if (onV is not ObjectInstance on) return list;
        foreach (var key in on.GetOwnPropertyKeys(Types.String))
        {
            var k = key.ToString();
            var v = on.Get(key);
            if (v.IsString()) list.Add(new TransitionSpec(k, v.AsString()));
            else if (v is Function f) list.Add(new TransitionSpec(k, null, Handler: Store(f)));
            else if (v is ObjectInstance o)
            {
                var to = JsConvert.Str(o.Get("to"));
                var g = o.Get("guard");
                if (g.IsUndefined()) g = o.Get("if");
                var does = o.Get("do");
                list.Add(new TransitionSpec(k, to, Handler: does is Function df ? Store(df) : -1, Guard: g.IsString() ? g.AsString() : null,
                    GuardFn: g is Function gf ? Store(gf) : -1));
            }
            else throw ScriptSession.Bug($"machine '{machine}'{(state != null ? " state " + state : "")}: on['{k}'] must be a state name, a function or {{to, guard}}");
        }
        return list;
    }

    private int Store(JsValue fn)
    {
        _fns.Add(fn);
        return _fns.Count - 1;
    }

    /// <summary>The JS side of a running machine: callbacks get (bb, m) / handlers (ev, bb, m) / guards (bb, ev).</summary>
    private sealed class JsMachineHost(JsRuntime rt, ObjectInstance states, ObjectInstance? guards, ObjectInstance bb, ObjectInstance ctx) : IMachineHost
    {
        private Engine E => rt._e;

        private JsValue Callback(string state, string name) => (states.Get(state) as ObjectInstance)?.Get(name) ?? JsValue.Undefined;

        private static string? Target(JsValue r) => r.IsString() ? r.AsString() : null;

        public string? Enter(string state) => Callback(state, "enter") is Function f ? Target(E.Invoke(f, bb, ctx)) : null;
        public string? Tick(string state) => Callback(state, "tick") is Function f ? Target(E.Invoke(f, bb, ctx)) : null;
        public void Exit(string state) { if (Callback(state, "exit") is Function f) E.Invoke(f, bb, ctx); }

        public (bool Taken, string? Target) Handle(TransitionSpec t, ScriptEvent e)
        {
            var ev = rt.Event(e);
            if (t.Guard != null)
            {
                var g = guards?.Get(t.Guard) as Function ?? throw ScriptSession.Bug($"machine: no guard '{t.Guard}'");
                if (!TypeConverter.ToBoolean(E.Invoke(g, bb, ev))) return (false, null);
            }
            if (t.GuardFn >= 0 && !TypeConverter.ToBoolean(E.Invoke(rt._fns[t.GuardFn], bb, ev))) return (false, null);
            if (t.Handler >= 0)
            {
                var r = E.Invoke(rt._fns[t.Handler], ev, bb, ctx);
                return (true, Target(r) ?? t.Target);
            }
            return (true, t.Target);
        }

        public JsonNode? Blackboard() => JsConvert.ToNode(bb, 0, args: false);

        public void Restore(JsonNode? node)
        {
            if (node is not JsonObject o) return;
            foreach (var key in bb.GetOwnPropertyKeys(Types.String).ToList()) bb.Delete(key);
            foreach (var (k, v) in o) bb.Set(k, JsConvert.FromNode(E, v));
        }

        public void Changed(string state, IReadOnlyList<MachineStep> history)
        {
            ctx.Set("state", new JsString(state));
            ctx.Set("history", new JsArray(E, history.Select(h =>
            {
                var o = new JsObject(E);
                o.Set("from", new JsString(h.From));
                o.Set("to", new JsString(h.To));
                o.Set("why", new JsString(h.Why));
                o.Set("at", new JsNumber(h.At));
                return (JsValue)o;
            }).ToArray()));
        }
    }
}
