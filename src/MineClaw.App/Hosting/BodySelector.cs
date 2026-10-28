using MineClaw.Agent.Events;
using MineClaw.Bot.Bodies;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Inventory;
using MineClaw.Hive.Supervisor;

namespace MineClaw.App.Hosting;

/// <summary>
/// One brain, many bodies: <c>body:"any"</c> picks the fittest body for a job (docs/design/one-brain-many-bodies.md section 2):
/// the hive's <see cref="CloneFitness.Score"/> over each candidate's live hp, food, tools and distance to main, plus a failure
/// memory keyed by (body, intent), which a live change of that body's hp, food or inventory clears
/// (<see cref="AssignFailureMemory"/>). A body that can't safely do it is skipped; when none fits, the reason and the fix.
/// </summary>
public sealed class BodySelector(BodyRegistry registry, TimeProvider? time = null)
{
    private readonly AssignFailureMemory _failures = new(time);

    /// <summary>Remembers a body's failed job (its [task failed] event) so the same intent isn't given to it again blindly.</summary>
    public void OnEvent(GameEvent e)
    {
        if (e.Kind != EventKind.TaskFailed || GameEvent.BodyTag(e.Body) is not { } shortName || registry.Find(shortName) is not { IsMain: false } b) return;
        try { _failures.Record(b.Name, CloneFitness.IntentOf(e.Text), e.Text, Vitals(b), b.Adapter.World.IsNight); }
        catch (Exception) { /* memory only */ }
    }

    /// <summary>A player said "try again" / brought what was missing: the failure memory is cleared for the bodies they name.</summary>
    public int OnPlayerChat(string text) => _failures.OnPlayerChat(text);

    public (string? Pick, string? Why) Pick(string job, IReadOnlyList<IBody> candidates)
    {
        var intent = CloneFitness.IntentOf(job);
        var main = registry.Main.Adapter.World.Player.Position;
        var fits = new List<CloneFit>();
        foreach (var b in candidates)
        {
            var v = Vitals(b);
            var fit = CloneFitness.Score(b.Name, v, intent, online: !b.Activities.IsBusy, main.X, main.Z);
            if (fit.Blocker == null && _failures.Blocking(b.Name, intent, v, b.Adapter.World.IsNight) is { } failed)
                fit = fit with { Blocker = failed };
            fits.Add(fit);
        }
        var best = fits.Where(f => f.Blocker == null).OrderByDescending(f => f.Score).FirstOrDefault();
        if (best != null) return (Body.ShortName(best.Name, false), null);
        return (null, string.Join("; ", fits.Select(f => $"{Body.ShortName(f.Name, false)} {f.Blocker}")));
    }

    private static CloneVitals Vitals(IBody b)
    {
        var w = b.Adapter.World;
        var p = w.Player.Position;
        return new CloneVitals(w.Player.Health, w.Player.Food, GameInventoryPort.Snapshot(w), p.X, p.Y, p.Z, w.Dimension, DateTimeOffset.UtcNow);
    }
}
