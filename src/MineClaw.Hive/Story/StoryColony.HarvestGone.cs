using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private DateTimeOffset _harvestPrune = DateTimeOffset.MinValue;

    /// <summary>Saved no-tree errors may be resting for ten minutes. Retire confirmed missing bases on the next
    /// normal tick instead of waiting for another futile job; the usual cancellation path releases any active worker.</summary>
    private void PruneGoneHarvestMarks()
    {
        if (Now - _harvestPrune < TimeSpan.FromSeconds(5) || World is not { } world) return;
        _harvestPrune = Now;
        var marks = Designations.Where(d => d.Kind == "chop" && d.At != null && d.Fails > 0 && NoTreeTarget(d.Reason)).ToList();
        if (marks.Count == 0) return;
        var views = Pawns.Keys.Select(name => (Name: name, Live: Host.Live(name), View: OwnView(name)))
            .Where(v => v.Live.Online && v.Live.Dim == world.Dimension && v.View?.Dimension == world.Dimension).ToList();
        var gone = new List<string>();
        foreach (var d in marks)
        {
            var at = d.At!.Value;
            var owner = _current.FirstOrDefault(kv => kv.Value.Source == "designation" && kv.Value.SourceId == d.Id).Key;
            var view = views.Where(v => v.View!.Loaded(at.X, at.Z))
                .OrderBy(v => v.Name == owner ? 0 : 1).ThenBy(v => at.Dist(v.Live.X, v.Live.Y, v.Live.Z))
                .Select(v => v.View).FirstOrDefault() ?? world;
            if (!view.Loaded(at.X, at.Z) || StoryBlocks.Logs.Contains(view.Block(at.X, at.Y, at.Z).Id)) continue;
            _chopPartials.Remove(d.Id);
            ForgetHarvestFailures(DesignationKey(d));
            gone.Add(d.Id);
        }
        if (gone.Count > 0) RemoveDesignations("chop", null, null, gone);
    }

    /// <summary>A failed harvest whose target has already gone is finished work, not a failed map marker.
    /// Only loaded observations count. Explicit no-tree results cancel the old base marker; generic failures
    /// preserve recorded branches still standing for the normal partial-chop path.</summary>
    private bool HarvestGone(PawnState pawn, ActiveJob job, string? note)
    {
        var des = job.Source == "designation" ? Designations.FirstOrDefault(d => d.Id == job.SourceId) : null;
        string kind = des?.Kind ?? job.Kind;
        if (job.Source is not ("designation" or "zone" or "crop") || kind is not ("chop" or "cut" or "harvest")
            || job.At is not { } at) return false;
        var own = OwnView(pawn.Name);
        var view = own != null && own.Dimension == World?.Dimension && own.Loaded(at.X, at.Z) ? own : World;
        IReadOnlyList<P3>? logs = des?.Blocks;
        if (kind == "chop" && logs == null && job.Json["logs"] is JsonArray a)
            logs = a.Select(n => ParseCell(n!.GetValue<string>())).Where(p => p != null).Select(p => p!.Value).ToList();
        bool noTree = kind == "chop" && NoTreeTarget(note) && view != null && view.Loaded(at.X, at.Z)
            && !StoryBlocks.Logs.Contains(view.Block(at.X, at.Y, at.Z).Id);
        if (!noTree && !HarvestAbsent(view, kind, at, logs)) return false;

        GearTakesEnded(job, done: false);
        if (job.Lease is { } key)
        {
            ForgetHarvestFailures(key);
            _cropHarvest.Remove(key); // no harvest happened, so do not advance a growing bill
        }
        if (des != null) DesignationDone(pawn, job, null);
        else
        {
            _zoneScan.Remove(job.SourceId ?? "");
            _growScan.Remove(job.SourceId ?? "");
            if (job.Lease is { } lease) _skip[lease] = Now + ViewLagRest;
        }
        return true;
    }

    private void ForgetHarvestFailures(string key)
    {
        _skip.Remove(key);
        _leaseFails.Remove(key);
        _softFails.Remove(key);
        ForgetPawnFails(key);
    }

    private static bool NoTreeTarget(string? note) => note != null
        && (note.Contains("no tree at ", StringComparison.OrdinalIgnoreCase)
            || note.Contains("no tree there", StringComparison.OrdinalIgnoreCase)
            || note.Contains("tree doesn't exist", StringComparison.OrdinalIgnoreCase));

    private static bool HarvestAbsent(IStoryWorld? view, string kind, P3 at, IReadOnlyList<P3>? logs = null)
    {
        if (view == null || !view.Loaded(at.X, at.Z)) return false;
        if (kind == "chop")
        {
            if (StoryBlocks.Logs.Contains(view.Block(at.X, at.Y, at.Z).Id)) return false;
            return logs == null || logs.All(p => view.Loaded(p.X, p.Z) && !StoryBlocks.Logs.Contains(view.Block(p.X, p.Y, p.Z).Id));
        }
        return BlockStillThere(view, kind, at, view.Block(at.X, at.Y, at.Z).Id) == null;
    }

    /// <summary>Plain chop/farm zones also consult the worker's connection, as named growing zones already do.
    /// Main may still show a harvested tree or crop while the worker has the newer block changes.</summary>
    private bool HarvestAgrees(string pawn, string kind, P3 at)
    {
        if (kind is not ("chop" or "harvest")) return true;
        var own = OwnView(pawn);
        if (own == null || own.Dimension != World?.Dimension || !own.Loaded(at.X, at.Z)) return true;
        var logs = kind == "chop" && World is { } w ? StoryBlocks.TreeFrom(w, at) : null;
        if (!HarvestAbsent(own, kind, at, logs)) return true;
        _skip["b:" + at] = Now + ViewLagRest;
        return false;
    }
}
