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
            Add(a.Kind switch
            {
                DemoActionKind.BlockBroken => $"break {a.Block ?? "block"} at {P(a.X, a.Y, a.Z)}",
                DemoActionKind.BlockPlaced => $"place {a.Block ?? "block"} at {P(a.X, a.Y, a.Z)}",
                DemoActionKind.UseBlock => $"use {a.Block ?? "block"} at {P(a.X, a.Y, a.Z)}",
                DemoActionKind.OpenContainer => $"open {a.Block ?? "container"} at {P(a.X, a.Y, a.Z)}",
                DemoActionKind.ClickSlot => a.Detail ?? "click slot",
                _ => a.Detail ?? a.Kind.ToString(),
            });
        }
        // A trailing move only matters if nothing else happened.
        if (steps.Count == 0) FlushMove();

        var result = steps.Select(s => s.Count > 1 ? $"{s.Text} (x{s.Count})" : s.Text).ToList();
        if (result.Count > maxSteps)
        {
            int dropped = result.Count - (maxSteps - 1);
            result = result.Take(maxSteps - 1).Append($"...({dropped} more actions)").ToList();
        }
        return result;
    }
}
