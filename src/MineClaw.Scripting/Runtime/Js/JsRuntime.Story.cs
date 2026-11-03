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
    }
}
