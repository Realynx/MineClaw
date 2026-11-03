using MineClaw.Agent.Telemetry;
using MineClaw.Bot.Bodies;
using MineClaw.Scripting;

namespace MineClaw.App.Supervisor;

/// <summary>
/// A process's subhive bodies as <see cref="BodyBrief"/>s: what a hive clone streams to main with its snapshot (so main's
/// supervisor lists every body) and what main reads of its own in-process bodies. Never throws; a body that fails is skipped.
/// </summary>
public static class BodyBriefs
{
    /// <summary>The non-main bodies of <paramref name="bodies"/> (null = none).</summary>
    public static IReadOnlyList<BodyBrief>? Of(IEnumerable<IBody>? bodies, TimeProvider? time = null)
    {
        if (bodies == null) return null;
        var now = (time ?? TimeProvider.System).GetUtcNow();
        List<BodyBrief>? list = null;
        try
        {
            foreach (var b in bodies)
            {
                if (b.IsMain) continue;
                try { (list ??= new()).Add(Of(b, now)); }
                catch (Exception) { /* a body mid-teardown: skip it this time */ }
            }
        }
        catch (Exception) { }
        return list;
    }

    public static BodyBrief Of(IBody b, DateTimeOffset now)
    {
        var a = b.Adapter;
        var w = a.World;
        var p = w.Player;
        var s = b.Scripts?.Current;
        string? job = null;
        try { job = b.Activities.Describe() ?? (s != null ? "script " + s.Name : null) ?? a.CurrentAction; } catch (Exception) { }
        return new BodyBrief
        {
            Name = b.Name, Dimension = w.Dimension, X = Math.Round(p.Position.X, 2), Y = Math.Round(p.Position.Y, 2), Z = Math.Round(p.Position.Z, 2),
            Hp = (int)Math.Round((double)p.Health), Food = p.Food, Task = job, Playing = a.IsPlaying, At = now, Script = Script(s),
        };
    }

    public static BodyScriptBrief? Script(ScriptSession? s) =>
        s == null ? null : new BodyScriptBrief(s.Name, s.Status.ToString().ToLowerInvariant(), s.Line, s.Status == ScriptStatus.Paused, s.Request.Version);
}
