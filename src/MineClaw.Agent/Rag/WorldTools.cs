using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Rag;

/// <summary><c>where_is</c>: structured world lookups (chest contents, nearest X, what's around) — zero LLM tokens, no embeddings.</summary>
public static class WorldTools
{
    public static void Register(ToolRegistry registry, ObservationStore world, Func<BlockPos?>? position, TimeProvider? time = null,
        MemoryStore? memory = null)
    {
        time ??= TimeProvider.System;
        registry.Register("where_is",
            "Find an item in known chests, or the nearest known chest/furnace/bed/farm/tree/portal... No args: what's around. " +
            "dim=overworld|nether|any",
            ToolSchema.Create().Str("item", required: false).Str("kind", required: false).Str("dim", required: false).Build(),
            (input, _) => Task.FromResult(WhereIs(world, input.Str("item"), input.Str("kind"), position?.Invoke(), time.GetUtcNow(), memory,
                input.Str("dim"))), cheapTier: true);
    }

    /// <param name="memory">Named waypoints/structures for landmark hints ("by cobble_gen"); null = none.</param>
    /// <param name="dim">overworld|nether|any; null = the bot's current dimension. Things elsewhere get a "(nether)" tag and no distance.</param>
    public static ToolResult WhereIs(ObservationStore world, string? item, string? kind, BlockPos? here, DateTimeOffset now, MemoryStore? memory = null,
        string? dim = null)
    {
        int cur = world.CurrentDimension;
        if (!WorldDim.TryParse(dim, cur, out var want)) return ToolResult.Error("dim must be overworld|nether|any");
        string Ago(DateTimeOffset t) => TextTokens.Ago(now - t);
        // Another dimension is only remembered (not live): say so, with its age ("remembered 2h ago").
        string Seen(ObservationRow r) => r.Dimension != cur ? $"remembered {Ago(r.LastSeen)}" : $"seen {Ago(r.LastSeen)}";
        // Contents are as old as the last OPEN, not the chest block's last sighting (live 10-27 21:06: "bread x5 (seen just now)"
        // was main's deposit a minute earlier; a player had emptied the chest since, unseen: no packets for their clicks).
        string Contents(ObservationRow r)
        {
            if (r.ContentsAt is not { } at) return "contents age unknown, may have changed";
            var age = now - at;
            string ago = age < TimeSpan.FromSeconds(60) ? $"{Math.Max(0, (int)age.TotalSeconds)}s ago" : TextTokens.Ago(age);
            string by = string.IsNullOrWhiteSpace(r.ContentsBy) ? "" : " by " + r.ContentsBy;
            return $"opened{by} {ago}" + (age >= TimeSpan.FromSeconds(30) ? ", may have changed" : "");
        }
        // "4m NE" from you, plus the nearest named landmark of the spot when one is close ("4m NE, by cobble_gen"); only in your dimension.
        string Dist(ObservationRow r)
        {
            if (r.Bounds is not { } b || r.Dimension != cur) return "";
            var (cx, cy, cz) = ((b.MinX + b.MaxX) / 2.0, (b.MinY + b.MaxY) / 2.0, (b.MinZ + b.MaxZ) / 2.0);
            var s = here != null ? $" {b.Distance(here.X, here.Y, here.Z):0}m {Landmarks.Compass(cx - here.X, cz - here.Z)}".TrimEnd() : "";
            var lm = memory != null ? Landmarks.Near(memory, null, cx, cy, cz, maxNamed: 12) : null;
            return lm != null ? s + (s.Length > 0 ? ", " : " ") + lm : s;
        }
        string Tag(ObservationRow r) => WorldDim.Tag(r.Dimension, cur);
        string Link(ObservationRow r) =>
            r.Kind == PortalLinks.Kind && world.Portals.LinkOf(r.Dimension, r.Key) is { } to ? $" -> {WorldDim.Name(to.Dim)} {to.Pos}" : "";

        if (!string.IsNullOrWhiteSpace(item))
        {
            var hits = world.FindItemInContainers(item.Trim(), 6, want);
            if (hits.Count == 0)
            {
                // Verified observations first; player claims only as a labelled fallback ("fox says: ... (unverified)").
                var claims = memory != null ? Beliefs.BeliefStore.Find(memory.Db)?.ContainerClaims(item, 2) : null;
                var elsewhere = want != WorldDim.Any && world.FindItemInContainers(item.Trim(), 6, WorldDim.Other(want)) is { Count: > 0 } o
                    ? $" | {o.Sum(h => h.Count)} in the {WorldDim.Name(WorldDim.Other(want))} (dim=any)" : "";
                return ToolResult.Success($"no {item.Trim()} in known chests" + (want != cur ? $" in the {WorldDim.Name(want)}" : "") + elsewhere +
                                          (claims is { Count: > 0 } ? " | " + string.Join("; ", claims) : ""));
            }
            var name = hits[0].Name;
            var parts = hits.Select(h => $"x{h.Count}: {h.Container.Kind}@{h.Container.Where}{Tag(h.Container)}{Dist(h.Container)} ({Contents(h.Container)})");
            return ToolResult.Success($"{name} " + string.Join("; ", parts) + (hits.Count > 1 ? $" | total {hits.Sum(h => h.Count)}" : ""));
        }

        if (here == null) return ToolResult.Error("position unknown");
        // Distances in another dimension are measured from your position scaled 1:8 (where you'd come out of a portal).
        (double X, double Y, double Z) At(int d) { var p = WorldDim.Convert(here.X, here.Y, here.Z, cur, d); return (p.X, p.Y, p.Z); }
        int[] dims = want == WorldDim.Any ? [cur, WorldDim.Other(cur)] : [want];

        if (!string.IsNullOrWhiteSpace(kind))
        {
            var k = kind.Trim().ToLowerInvariant().Replace(' ', '_');
            List<(ObservationRow Row, double Distance)> Find(string kk) => dims
                .SelectMany(d => { var a = At(d); return world.Nearest(kk, a.X, a.Y, a.Z, 3, d == cur ? 256 : 4096, d); }).Take(3).ToList();
            var near = Find(k);
            if (near.Count == 0 && k.EndsWith('s')) near = Find(k = k[..^1]);
            if (near.Count == 0)
            {
                var other = want != WorldDim.Any ? WorldDim.Other(want) : (int?)null;
                bool there = other is { } od && world.OfKind(k, od, 1).Count > 0;
                return ToolResult.Success($"no known {k} nearby" + (want != cur ? $" in the {WorldDim.Name(want)}" : "") +
                                          (there ? $" (some in the {WorldDim.Name(other!.Value)}: dim=any)" : ""));
            }
            return ToolResult.Success(string.Join("; ", near.Select(n =>
                $"{n.Row.Kind}@{n.Row.Where}{Tag(n.Row)}{Dist(n.Row)}{Link(n.Row)} ({Seen(n.Row)})" +
                (n.Row.Source != ObservationSource.Auto ? " [" + ObservationStore.Source(n.Row.Source) + "]" : ""))));
        }

        int d0 = want == WorldDim.Any ? cur : want;
        var a0 = At(d0);
        var kinds = world.ListKinds(Box.Around(a0.X, a0.Y, a0.Z, 32), d0);
        var where = d0 != cur ? $" (in the {WorldDim.Name(d0)})" : "";
        return ToolResult.Success(kinds.Count == 0 ? "nothing indexed within 32 blocks" + where
            : "within 32" + where + ": " + string.Join(", ", kinds.Select(k => $"{k.Key} x{k.Value}")));
    }
}
