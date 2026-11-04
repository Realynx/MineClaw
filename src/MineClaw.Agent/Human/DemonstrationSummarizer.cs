using MineClaw.Agent.Memory;

namespace MineClaw.Agent.Human;

/// <summary>
/// Compresses a raw demonstration into a handful of how-to steps (no LLM): consecutive moves collapse to the
/// final stand position, repeated identical actions merge into "xN", coordinates are made relative to the
/// structure origin when known.
/// </summary>
public static class DemonstrationSummarizer
{
    public static List<string> Summarize(IReadOnlyList<DemoAction> actions, BlockPos? origin = null, int maxSteps = 10)
    {
        var steps = new List<(string Text, int Count)>();
        DemoAction? pendingMove = null;

        string P(int x, int y, int z) => origin == null ? $"{x},{y},{z}" : $"{Rel(x - origin.X)},{Rel(y - origin.Y)},{Rel(z - origin.Z)}";
        static string Rel(int d) => d >= 0 ? "+" + d : d.ToString();

        void Add(string text)
        {
            if (steps.Count > 0 && steps[^1].Text == text) steps[^1] = (text, steps[^1].Count + 1);
            else steps.Add((text, 1));
        }

        void FlushMove()
        {
            if (pendingMove == null) return;
            Add($"stand at {P(pendingMove.X, pendingMove.Y, pendingMove.Z)}");
            pendingMove = null;
        }

        foreach (var a in actions)
        {
            if (a.Kind == DemoActionKind.Move) { pendingMove = a; continue; }
            if (a.Kind == DemoActionKind.Chat) continue;
            FlushMove();
            Add(StepText(a, P));
        }
        // A trailing move only matters if nothing else happened.
        if (steps.Count == 0) FlushMove();

        return Cap(steps.Select(s => s.Count > 1 ? $"{s.Text} (x{s.Count})" : s.Text).ToList(), maxSteps);
    }

    /// <summary>One non-move action as a step ("place cobblestone at +1,+0,-2").</summary>
    private static string StepText(DemoAction a, Func<int, int, int, string> pos)
    {
        string At(string verb, string fallback) => $"{verb} {a.Block ?? fallback} at {pos(a.X, a.Y, a.Z)}";
        return a.Kind switch
        {
            DemoActionKind.BlockBroken => At("break", "block"),
            DemoActionKind.BlockPlaced => At("place", "block"),
            DemoActionKind.UseBlock => At("use", "block"),
            DemoActionKind.OpenContainer => At("open", "container"),
            DemoActionKind.ClickSlot => a.Detail ?? "click slot",
            _ => a.Detail ?? a.Kind.ToString(),
        };
    }

    /// <summary>At most <paramref name="maxSteps"/> lines: the last one says how many more actions were left out.</summary>
    private static List<string> Cap(List<string> result, int maxSteps)
    {
        if (result.Count <= maxSteps) return result;
        int dropped = result.Count - (maxSteps - 1);
        return result.Take(maxSteps - 1).Append($"...({dropped} more actions)").ToList();
    }
}
