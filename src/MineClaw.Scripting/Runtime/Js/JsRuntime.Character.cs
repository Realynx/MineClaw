using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace MineClaw.Scripting.Runtime.Js;

/// <summary>Host calls of the <c>character</c> SDK module (Sdk/js/80-character.js) and the typed game-event payload.</summary>
internal sealed partial class JsRuntime
{
    private void InstallCharacter(ObjectInstance h)
    {
        H(h, "chLook", a =>
        {
            var x = JsConvert.Arg(a, 0);
            if (x.IsString()) return s.CharLook(x.AsString(), null) ? JsBoolean.True : JsBoolean.False;
            World.BlockPos? p = a.Length >= 3 && a.Take(3).All(v => v.IsNumber())
                ? new World.BlockPos((int)Math.Floor(a[0].AsNumber()), (int)Math.Floor(a[1].AsNumber()), (int)Math.Floor(a[2].AsNumber()))
                : ScriptSession.PosOf(JsConvert.ToNode(x, 1));
            if (p == null) throw ScriptSession.Bug("look(target) needs a player name or a position");
            return s.CharLook(null, p) ? JsBoolean.True : JsBoolean.False;
        });
        H(h, "chGesture", a => R(s.CharGesture(Str(a, 0) ?? "", Str(a, 1))));
        H(h, "chLine", a => S(s.CharLine(Str(a, 0) ?? "", Str(a, 1))));
        H(h, "chSaid", a => { s.CharSaid(Str(a, 0) ?? ""); return JsValue.Undefined; });
        H(h, "chMood", _ => V(s.CharMood()));
        H(h, "chRelation", a => V(s.CharRelation(Str(a, 0) ?? throw ScriptSession.Bug("relation(player) needs a name"))));
        H(h, "chRecall", a => V(s.CharRecall(Str(a, 0) ?? "", (int)(Num(a, 1) ?? 3))));
        H(h, "chRemember", a => R(s.CharRemember(Str(a, 0) ?? "")));
        H(h, "chHeard", a => V(s.CharHeard(Str(a, 0), Num(a, 1) ?? 60)));
        H(h, "chIntent", a => new JsString(s.CharIntent(Str(a, 0) ?? "")));
    }

    /// <summary>The typed payload's fields go on the event object (e.player, e.pos, e.block, e.held, ...).</summary>
    private void AddData(JsObject ev, ScriptEvent e)
    {
        if (e.Data is not { } d) return;
        foreach (var (k, v) in d)
            if (k is not ("kind" or "topic")) ev.Set(k, V(v?.DeepClone()));
        if (e.Player is { } p && !d.ContainsKey("player")) ev.Set("player", new JsString(p));
    }
}
