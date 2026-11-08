using System.Text.Json.Nodes;
using Jint.Native;
using Jint.Native.Object;

namespace MineClaw.Scripting.Runtime.Js;

/// <summary>Host calls of the <c>story</c> SDK module (Sdk/js/75-story.js): a pawn asks its colony director for work and reports.</summary>
internal sealed partial class JsRuntime
{
    private Story.IStoryPort? StoryPort => s.Host.Story ?? Story.StoryPorts.For(s.Host.Username);

    private void InstallStory(ObjectInstance h)
    {
        // story.next(s): the next job, waiting up to s seconds (interruptible, one action) while the director has none.
        H(h, "storyNext", a =>
        {
            var port = StoryPort ?? throw ScriptSession.Bug("story.next(): this body isn't a Story Mode pawn");
            JsonObject? got = null;
            double sec = Math.Clamp(Num(a, 0) ?? 0, 0, 120);
            if ((got = port.Next(s.Host.Username)) == null && sec > 0) Poll("story.next", sec, () => (got = port.Next(s.Host.Username)) != null);
            return got != null ? V(got) : JsValue.Null;
        });
        H(h, "storyReport", a =>
        {
            var port = StoryPort ?? throw ScriptSession.Bug("story.report(): this body isn't a Story Mode pawn");
            var job = Str(a, 0) ?? throw ScriptSession.Bug("story.report(job, status) needs the job id");
            var status = (Str(a, 1) ?? "done").Trim().ToLowerInvariant();
            if (status is not ("progress" or "done" or "failed")) throw ScriptSession.Bug("story.report status is progress|done|failed");
            return port.Report(s.Host.Username, job, status, Str(a, 2), Num(a, 3)) ? JsBoolean.True : JsBoolean.False;
        });
        // story.escape's eyes (Story/PitScan.cs): is this body in a pit, a cave or water, and the next move out. Zero tokens, no action.
        H(h, "storyPit", a =>
        {
            s.SafePoint();
            var toward = Str(a, 0) is { } t ? ScriptSession.PosOf(JsonValue.Create(t)) : null;
            bool climb = Jint.Runtime.TypeConverter.ToBoolean(JsConvert.Arg(a, 1)); // no way home from here: climb toward it whatever the shape
            return V(Story.PitScan.Scan(s.Host.World, s.Host.Data, s.Host.World.Player.Position, toward, climb));
        });
        // story.dig's eyes (Story/ReachScan.cs): a block above reach, the column to nerd-pole from and what to pole with.
        H(h, "storyReach", a =>
        {
            s.SafePoint();
            var at = (Str(a, 0) is { } t ? ScriptSession.PosOf(JsonValue.Create(t)) : null) ?? throw ScriptSession.Bug("storyReach(at) needs a position");
            if (!s.Host.World.IsLoaded(at.X, at.Z)) return V(new JsonObject { ["unloaded"] = true, ["why"] = "target is not loaded" });
            var targets = (JsConvert.ToNode(JsConvert.Arg(a, 1), 2) as JsonArray)?.Select(ScriptSession.PosOf)
                .OfType<World.BlockPos>().Append(at).Distinct().Take(24).ToArray();
            return V(Story.ReachScan.Scan(s.Host.World, s.Host.Data, s.Host.World.Player.Position, at, walkable: true, targets));
        });
        // Cheap visibility check for a reserved dig batch: do not search a path for each cell while standing on a pillar.
        H(h, "storyWithin", a =>
        {
            s.SafePoint();
            var at = (Str(a, 0) is { } t ? ScriptSession.PosOf(JsonValue.Create(t)) : null) ?? throw ScriptSession.Bug("storyWithin(at) needs a position");
            return Story.ReachScan.Within(s.Host.World, s.Host.World.Player.Position, at) ? JsBoolean.True : JsBoolean.False;
        });
        // story.pour's eyes: the liquid at a cell -> "water" | "lava" (a source: what a poured bucket leaves), "flowing_water" |
        // "flowing_lava" (a flow level, or falling), "" (no liquid). Zero tokens, no action.
        H(h, "storyLiquid", a =>
        {
            s.SafePoint();
            var p = ScriptSession.PosOf(JsConvert.ToNode(JsConvert.Arg(a, 0), 1)) ?? throw ScriptSession.Bug("storyLiquid(pos) needs a position");
            int id = s.Host.World.GetBlock(p.X, p.Y, p.Z), meta = s.Host.World.GetMeta(p.X, p.Y, p.Z);
            string what = id is 8 or 9 ? "water" : id is 10 or 11 ? "lava" : "";
            return new JsString(what.Length == 0 || meta == 0 ? what : "flowing_" + what);
        });
        // story.escape found no way out by itself: the colony sends help (blocks, a rescuer) or teleports it home. → what happens.
        H(h, "storyStuck", a =>
        {
            var port = StoryPort ?? throw ScriptSession.Bug("story.escape(): this body isn't a Story Mode pawn");
            return new JsString(port.Stuck(s.Host.Username, Str(a, 0) ?? "stuck"));
        });
        // A block put down for itself (a pillar step, a scaffold) or taken back: the colony's registry of temporary blocks. No-op off-colony.
        H(h, "storyTemp", a =>
        {
            var cell = Str(a, 0) ?? throw ScriptSession.Bug("storyTemp(cell, placed, why) needs the cell");
            bool placed = Jint.Runtime.TypeConverter.ToBoolean(JsConvert.Arg(a, 1));
            StoryPort?.Temporary(s.Host.Username, cell, placed, Str(a, 2) ?? "scaffold");
            return JsValue.Undefined;
        });
        // story.during(job, fn): fn while the job is still this pawn's; cut at once when the director takes it back (an idle leg when
        // real work comes up) → {ok:false, cancelled:true}. Not a failure: the next story.next() has the new job.
        H(h, "storyDuring", a =>
        {
            var port = StoryPort ?? throw ScriptSession.Bug("story.during(): this body isn't a Story Mode pawn");
            var job = Str(a, 0) ?? throw ScriptSession.Bug("story.during(job, fn) needs the job");
            var fn = JsConvert.Arg(a, 1) as Jint.Native.Function.Function ?? throw ScriptSession.Bug("story.during(job, fn) needs a function");
            var me = s.Host.Username;
            var box = s.WhileHolds(() => port.Holds(me, job), () => new StrongBox(_e.Invoke(fn)));
            if (box != null) return box.Value;
            var cut = new JsonObject { ["ok"] = false, ["cancelled"] = true, ["msg"] = "cancelled: the colony has other work for it" };
            return V(cut);
        });
    }
}
