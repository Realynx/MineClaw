using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Rag;

/// <summary><c>where_is</c>: structured world lookups (chest contents, nearest X, what's around) — zero LLM tokens, no embeddings.</summary>
public static class WorldTools
{
    public static void Register(ToolRegistry registry, ObservationStore world, Func<BlockPos?>? position, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        registry.Register("where_is",
            "Where is an item (from known chest contents) or the nearest known chest/furnace/crafting_table/bed/sign/farm/tree/island. " +
            "No args: what's known around you.",
            ToolSchema.Create().Str("item", required: false).Str("kind", required: false).Build(),
            (input, _) => Task.FromResult(WhereIs(world, input.Str("item"), input.Str("kind"), position?.Invoke(), time.GetUtcNow())), cheapTier: true);
    }

    public static ToolResult WhereIs(ObservationStore world, string? item, string? kind, BlockPos? here, DateTimeOffset now)
    {
        string Ago(DateTimeOffset t) => TextTokens.Ago(now - t);
        string Dist(ObservationRow r) => here != null && r.Bounds is { } b ? $" {b.Distance(here.X, here.Y, here.Z):0}m" : "";

        if (!string.IsNullOrWhiteSpace(item))
        {
            var hits = world.FindItemInContainers(item.Trim(), 6);
            if (hits.Count == 0) return ToolResult.Success($"no {item.Trim()} in known chests");
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
                $"{n.Row.Kind}@{n.Row.Where} {n.Distance:0}m (seen {Ago(n.Row.LastSeen)})" + (n.Row.Source != ObservationSource.Auto ? " [" + ObservationStore.Source(n.Row.Source) + "]" : ""))));
        }

        var kinds = world.ListKinds(Box.Around(here.X, here.Y, here.Z, 32));
        return ToolResult.Success(kinds.Count == 0 ? "nothing indexed within 32 blocks" : "within 32: " + string.Join(", ", kinds.Select(k => $"{k.Key} x{k.Value}")));
    }
}
