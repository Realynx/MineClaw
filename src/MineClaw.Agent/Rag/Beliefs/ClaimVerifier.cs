using MineClaw.Data;

namespace MineClaw.Agent.Rag.Beliefs;

/// <summary>
/// Verification planner (C#, zero tokens). Per category:
/// <list type="bullet">
/// <item>recipe/mechanic: seed data now (mismatch = refuted); time-based ones wait for observation counters (provisional at most);</item>
/// <item>container: the last snapshot if it's fresh enough, else a queued open-and-look check;</item>
/// <item>location: world observations / loaded blocks, else re-checked when the chunk is loaded;</item>
/// <item>structure-function: a queued experiment, only when it's safe (doors, lights); risky ones are never automatic;</item>
/// <item>social: provisional, no checks; safety: informational only, never planned or verified.</item>
/// </list>
/// </summary>
internal static class ClaimVerifier
{
    private static readonly int[] ContainerBlocks = [Blocks.Chest, Blocks.Furnace, Blocks.LitFurnace, Blocks.Dispenser];
    private static readonly HashSet<string> ContainerKinds = new(StringComparer.OrdinalIgnoreCase) { "chest", "furnace", "dispenser", "container" };

    public static void Evaluate(BeliefStore s, BeliefClaim c)
    {
        if (c.Directive) return;
        switch (c.Category)
        {
            case ClaimCategory.Recipe:
                if (c.TimeBased) { s.SetPlan(c.Id, ClaimPlan.Observe, ExperimentRisk.Safe); return; }
                if (s.HasEvidence(c.Id, EvidenceKind.Seed)) return;
                if (ClaimParser.TryRecipe(c.Text.ToLowerInvariant()) is { } r && SeedOracle.Check(r) is { } v)
                    s.AddEvidence(c.Id, EvidenceKind.Seed, v.Polarity, 0.99, v.Ref, v.Reason);
                return;
            case ClaimCategory.Container: Container(s, c); return;
            case ClaimCategory.Location: Location(s, c); return;
            case ClaimCategory.Structure: Structure(s, c); return;
            default: return; // social: provisional; safety: info only; other: stays unverified
        }
    }

    private static ClaimPos? Nearby(BeliefStore s, IReadOnlyCollection<int> ids)
    {
        try { return s.World?.FindNearest(ids, 5); }
        catch (Exception) { return null; }
    }

    // ---------------- containers ----------------

    private static void Container(BeliefStore s, BeliefClaim c)
    {
        var pos = c.Pos ?? Nearby(s, ContainerBlocks); // "that chest" while standing next to it
        if (pos is not { } p) return;
        if (s.Observations is { } obs)
        {
            var row = obs.Nearest(null, p.X, p.Y, p.Z, 4, 1.6).Select(t => t.Row).FirstOrDefault(r => ContainerKinds.Contains(r.Kind));
            if (row != null)
            {
                var contents = obs.Contents(row.Id);
                int have = contents.Where(i => Matches(i, c.Object)).Sum(i => i.Count);
                var now = s.Now;
                var reference = ObservationStore.DocRef(row.Kind, row.Key);
                if (have > 0 && now - row.LastSeen <= s.Options.SnapshotMaxAge)
                {
                    s.SetPlan(c.Id, ClaimPlan.None, ExperimentRisk.Safe, pos);
                    s.AddEvidence(c.Id, EvidenceKind.Observation, +1, 0.95, reference, $"{row.Kind} had {have} {ItemWord(c.Object)}");
                    return;
                }
                // Absence is never judged from the stored row: its last_seen tracks the chest block, not when its contents
                // were looked at. A refutation needs a look after the claim (live snapshot or the Bot's check).
            }
        }
        s.SetPlan(c.Id, ClaimPlan.Container, ExperimentRisk.Safe, pos); // stale or missing: open-and-look when idle and nearby
    }

    /// <summary>A live container snapshot (the bot just looked inside, for any reason): judges open claims about it.</summary>
    public static int OnSnapshot(BeliefStore s, ClaimPos anchor, IReadOnlyList<ItemCount> contents, string kind)
    {
        int n = 0;
        foreach (var id in s.OpenContainerClaimsAt(anchor))
        {
            EvaluateContents(s, id, anchor, contents, kind);
            n++;
        }
        return n;
    }

    public static BeliefClaim? EvaluateContents(BeliefStore s, long id, ClaimPos at, IReadOnlyList<ItemCount> contents, string kind)
    {
        if (s.Get(id) is not { } c) return null;
        int have = contents.Where(i => Matches(i, c.Object)).Sum(i => i.Count);
        var reference = $"check:{kind}@{at.Format()}";
        return have > 0
            ? s.AddEvidence(id, EvidenceKind.Observation, +1, 0.95, reference, $"{kind} has {have} {ItemWord(c.Object)}")
            : s.AddEvidence(id, EvidenceKind.Observation, -1, 0.9, reference, $"looked: no {ItemWord(c.Object)} in {kind}");
    }

    private static string ItemWord(string? obj) => (obj ?? "item").Replace('_', ' ');

    /// <summary>Lenient: id, name containment, or the same root word ("iron" ~ iron_ingot / iron_ore).</summary>
    internal static bool Matches(ItemCount i, string? obj)
    {
        if (string.IsNullOrEmpty(obj)) return false;
        var o = obj.ToLowerInvariant().Replace(' ', '_');
        var name = i.Name.ToLowerInvariant();
        if (Items.ByName(o) is { } info && info.Id == i.ItemId) return true;
        if (name.Contains(o) || o.Contains(name)) return true;
        var root = o.Split('_')[0];
        return root.Length >= 3 && name.Split('_')[0] == root;
    }

    // ---------------- locations ----------------

    private static void Location(BeliefStore s, BeliefClaim c)
    {
        if (c.Pos is not { } p || c.Subject is not { Length: > 0 } kind) return;
        if (s.Observations is { } obs && obs.Nearest(kind, p.X, p.Y, p.Z, 1, 2.5) is [var hit, ..])
        {
            s.SetPlan(c.Id, ClaimPlan.None, ExperimentRisk.Safe);
            s.AddEvidence(c.Id, EvidenceKind.Observation, +1, 0.95, ObservationStore.DocRef(hit.Row.Kind, hit.Row.Key), $"seen {kind.Replace('_', ' ')} there");
            return;
        }
        if (ClaimParser.KindBlocks(kind) is not { } ids) return; // farm, island, "robin's house": nothing to look for block-wise
        if (s.World is not { } w) { s.SetPlan(c.Id, ClaimPlan.Location, ExperimentRisk.Safe); return; }
        bool loaded = false, found = false;
        for (int dx = -1; dx <= 1 && !found; dx++)
            for (int dy = -1; dy <= 1 && !found; dy++)
                for (int dz = -1; dz <= 1 && !found; dz++)
                {
                    if (w.BlockAt(p.X + dx, p.Y + dy, p.Z + dz) is not { } b) continue;
                    loaded = true;
                    found = ids.Contains(b);
                }
        if (!loaded) { s.SetPlan(c.Id, ClaimPlan.Location, ExperimentRisk.Safe); return; }
        s.SetPlan(c.Id, ClaimPlan.None, ExperimentRisk.Safe);
        s.AddEvidence(c.Id, EvidenceKind.Observation, found ? 1 : -1, found ? 0.9 : 0.85, $"block:{p.Format()}",
            found ? $"{kind.Replace('_', ' ')} is there" : $"no {kind.Replace('_', ' ')} at {p.Format()}");
    }

    // ---------------- structures ----------------

    private static void Structure(BeliefStore s, BeliefClaim c)
    {
        if (c.Risk != ExperimentRisk.Safe) { s.SetPlan(c.Id, ClaimPlan.None, c.Risk); return; } // never automatic
        var pos = c.Pos ?? Nearby(s, ClaimParser.TriggerBlocks);
        if (pos == null) return;
        s.SetPlan(c.Id, ClaimPlan.Experiment, ExperimentRisk.Safe, pos);
    }

    public static BeliefClaim? EvaluateExperiment(BeliefStore s, long id, bool pressed, IReadOnlyList<ObservedChange> changes)
    {
        if (s.Get(id) is not { } c) return null;
        if (!pressed) return s.MarkAttempt(id, "couldn't use it");
        var trigger = c.Pos;
        var other = changes.Where(ch => trigger is not { } t || ch.Pos != t).ToList();
        bool hit;
        string what;
        if (c.Target is { } tp)
        {
            hit = other.Any(ch => ch.Pos.X == tp.X && ch.Pos.Z == tp.Z && Math.Abs(ch.Pos.Y - tp.Y) <= 1);
            what = $"block at {tp.Format()}";
        }
        else
        {
            var ids = ClaimParser.TargetBlocks(c.Object);
            hit = ids.Count > 0 ? other.Any(ch => ids.Contains(ch.OldId) || ids.Contains(ch.NewId)) : other.Count > 0;
            what = c.Object?.Replace('_', ' ') ?? "something";
        }
        var reference = $"exp:{(trigger?.Format() ?? "?")}";
        if (hit)
        {
            var at = other.First().Pos.Format();
            return s.AddEvidence(id, EvidenceKind.Experiment, +1, 0.9, reference, $"pressed it, {what} changed ({at})");
        }
        if (other.Count > 0)
        {
            s.AddEvidence(id, EvidenceKind.Experiment, 0, 0.5, reference, $"pressed it, other blocks changed, not {what}");
            return s.MarkAttempt(id);
        }
        return s.AddEvidence(id, EvidenceKind.Experiment, -1, 0.85, reference, "pressed it, nothing changed");
    }
}
