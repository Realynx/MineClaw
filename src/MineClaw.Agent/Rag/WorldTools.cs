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
            "Where is an item (from known chest contents) or the nearest known chest/furnace/crafting_table/bed/sign/farm/tree/island. " +
            "No args: what's known around you.",
            ToolSchema.Create().Str("item", required: false).Str("kind", required: false).Build(),
            (input, _) => Task.FromResult(WhereIs(world, input.Str("item"), input.Str("kind"), position?.Invoke(), time.GetUtcNow(), memory)), cheapTier: true);
    }

    /// <param name="memory">Named waypoints/structures for landmark hints ("by cobble_gen"); null = none.</param>
    public static ToolResult WhereIs(ObservationStore world, string? item, string? kind, BlockPos? here, DateTimeOffset now, MemoryStore? memory = null)
    {
        string Ago(DateTimeOffset t) => TextTokens.Ago(now - t);
        // "4m NE" from you, plus the nearest named landmark of the spot when one is close ("4m NE, by cobble_gen").
        string Dist(ObservationRow r)
        {
            if (r.Bounds is not { } b) return "";
            var (cx, cy, cz) = ((b.MinX + b.MaxX) / 2.0, (b.MinY + b.MaxY) / 2.0, (b.MinZ + b.MaxZ) / 2.0);
            var s = here != null ? $" {b.Distance(here.X, here.Y, here.Z):0}m {Landmarks.Compass(cx - here.X, cz - here.Z)}".TrimEnd() : "";
            var lm = memory != null ? Landmarks.Near(memory, null, cx, cy, cz, maxNamed: 12) : null;
            return lm != null ? s + (s.Length > 0 ? ", " : " ") + lm : s;
        }

        if (!string.IsNullOrWhiteSpace(item))
        {
            var hits = world.FindItemInContainers(item.Trim(), 6);
            if (hits.Count == 0)
            {
                // Verified observations first; player claims only as a labelled fallback ("fox says: ... (unverified)").
                var claims = memory != null ? Beliefs.BeliefStore.Find(memory.Db)?.ContainerClaims(item, 2) : null;
                return ToolResult.Success($"no {item.Trim()} in known chests" + (claims is { Count: > 0 } ? " | " + string.Join("; ", claims) : ""));
            }
            var name = hits[0].Name;
            var parts = hits.Select((h, i) => $"{(i == 0 ? "" : "")}x{h.Count}: {h.Container.Kind}@{h.Container.Where}{Dist(h.Container)} (seen {Ago(h.Container.LastSeen)})");
            return ToolResult.Success($"{name} " + string.Join("; ", parts) + (hits.Count > 1 ? $" | total {hits.Sum(h => h.Count)}" : ""));
        }

        if (here == null) return ToolResult.Error("position unknown");
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var k = kind.Trim().ToLowerInvariant().Replace(' ', '_');
            var near = world.Nearest(k, here.X, here.Y, here.Z, 3);
            if (near.Count == 0 && k.EndsWith('s')) near = world.Nearest(k[..^1], here.X, here.Y, here.Z, 3);
            if (near.Count == 0) return ToolResult.Success($"no known {k} nearby");
            return ToolResult.Success(string.Join("; ", near.Select(n =>
                $"{n.Row.Kind}@{n.Row.Where}{Dist(n.Row)} (seen {Ago(n.Row.LastSeen)})" + (n.Row.Source != ObservationSource.Auto ? " [" + ObservationStore.Source(n.Row.Source) + "]" : ""))));
        }

        var kinds = world.ListKinds(Box.Around(here.X, here.Y, here.Z, 32));
        return ToolResult.Success(kinds.Count == 0 ? "nothing indexed within 32 blocks" : "within 32: " + string.Join(", ", kinds.Select(k => $"{k.Key} x{k.Value}")));
    }
}
