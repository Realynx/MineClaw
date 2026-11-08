using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    // A deep quarry's route starts by walking away from the target to the stair entrance. Small, explicit legs keep the
    // ordinary pathfinder from exhausting its search at the opposite rim or choosing a cave under a storage chest.
    private ActiveJob QuarryTravel(ActiveJob job, PawnLive live)
    {
        if (!live.Online || job.Kind == "attack" || World is not { } w) return job;
        var inside = Zones.FirstOrDefault(z => IsQuarry(z) && z.Contains(live.Block.X, live.Block.Z));
        var quarry = job.Source == "zone" ? Zones.FirstOrDefault(z => z.Id == job.SourceId && IsQuarry(z)) : null;
        bool fetch = job.Json["fetch"] is JsonArray { Count: > 0 } || job.Json["store"] is JsonArray { Count: > 0 }
            || job.Json["pillar"] != null && live.Inventory.Where(s => PillarAny.Contains(s.Id)).Sum(s => s.Count) < 4;
        bool exits = inside != null && live.Y < QuarryTopOf(inside) - 1
            && (fetch || job.At is { } target && quarry != inside
                && (!inside.Contains(target.X, target.Z) || target.Y > live.Y + 2));
        if (exits) job.Json["quarry_exit"] = QuarryWalk(w, inside!, (int)Math.Floor(live.Y), QuarryTopOf(inside!) + 1);
        if (quarry == null || job.At is not { } at) return job;
        int? planned = job.Json["quarry_access_y"]?.GetValue<int>()
            ?? (job.Kind == "dig" ? Quarry(quarry)?.WorkFeet : null) ?? QuarryWorkHeight(w, quarry, at);
        if (planned is not { } feet) return job;
        int from = inside == quarry && !fetch ? (int)Math.Floor(live.Y) : QuarryTopOf(quarry) + 1;
        if (Math.Abs(from - feet) > 2) job.Json["quarry_approach"] = QuarryWalk(w, quarry, from, feet);
        return job;
    }

    private static int? QuarryWorkHeight(IStoryWorld w, ZoneState z, P3 target)
    {
        return QuarryAccessColumns(z, [target]).Select(c => QuarryColumnFeet(w, c.X, c.Z, target.Y)).OfType<int>().Cast<int?>().Min();
    }

    private JsonArray? QuarryWalk(IStoryWorld w, ZoneState z, int fromY, int toY)
    {
        if (StoryQuarry.Ring(z.Cells) is not { } ring) return null;
        int top = QuarryTopOf(z), from = Math.Clamp(top + 1 - fromY, 0, top - 1), to = Math.Clamp(top + 1 - toY, 0, top - 1);
        int direction = from <= to ? 1 : -1;
        var route = new JsonArray();
        for (int k = from; ; k += direction)
        {
            var step = StoryQuarry.Step(ring, top, k).Add(0, 1, 0);
            if (!StoryWander.Standable(w, step.X, step.Y, step.Z)
                || !StoryWander.Passable(w.Block(step.X, step.Y + 2, step.Z).Id)) return null;
            if (k == from || k == to || Math.Abs(k - from) % 3 == 0) route.Add(step.ToString());
            if (k == to) return route;
        }
    }
}
