using MineClaw.Agent.Human;
using MineClaw.World;

namespace MineClaw.Bot.Demo;

/// <summary>
/// Turns recorded <see cref="DemoAction"/>s into a short how-to, e.g.
/// "stood at 3,65,-2 facing lava; right-clicked water bucket on lava at 4,64,-2; cobblestone appeared at 5,64,-2".
/// Moves collapse to the stand position before the next action, identical consecutive steps merge into "(xN)",
/// and coordinates are relative ("+1,+0,-2") when a structure origin is known.
/// </summary>
public static class DemoSummary
{
    public static List<string> Summarize(IReadOnlyList<DemoAction> actions, BlockPos? origin = null, int maxSteps = 12)
    {
        var steps = new Steps(origin);
        foreach (var a in actions)
        {
            if (a.Kind == DemoActionKind.Move) { steps.PendingMove = a; continue; }
            if (a.Kind == DemoActionKind.Chat) continue;
            steps.FlushMove();
            steps.Add(StepText(a, steps.P(a.X, a.Y, a.Z), a.Block ?? "block"));
        }
        if (steps.Count == 0) steps.FlushMove();
        return Cap(steps.Texts(), maxSteps);
    }

    private static string StepText(DemoAction a, string at, string block) => a.Kind switch
    {
        DemoActionKind.UseBlock => UseText(a.Detail, at, block),
        DemoActionKind.OpenContainer => $"opened {block} at {at}",
        DemoActionKind.BlockPlaced => PlacedText(a.Detail, at, block),
        DemoActionKind.BlockBroken => BrokenText(a.Detail, at, block),
        DemoActionKind.ClickSlot => a.Detail ?? "clicked a slot",
        _ => a.Detail ?? a.Kind.ToString().ToLowerInvariant(),
    };

    private static string UseText(string? detail, string at, string block) =>
        detail == Perception.Gestures.Punched ? $"punched {block} at {at}"
        : detail != null ? $"right-clicked {detail} on {block} at {at}" : $"used {block} at {at}";

    private static string PlacedText(string? detail, string at, string block) => detail switch
    {
        "appeared" => $"{block} appeared at {at}",
        "poured" => $"poured {block} at {at}",
        _ => $"placed {block} at {at}",
    };

    private static string BrokenText(string? detail, string at, string block) => detail switch
    {
        "vanished" => $"{block} vanished at {at}",
        "scooped" => $"scooped {block} at {at}",
        _ => $"broke {block} at {at}",
    };

    private static List<string> Cap(List<string> result, int maxSteps)
    {
        if (result.Count <= maxSteps) return result;
        int dropped = result.Count - (maxSteps - 1);
        return result.Take(maxSteps - 1).Append($"...({dropped} more)").ToList();
    }

    /// <summary>The steps so far: repeats merged, the pending move collapsed to one "stood at" before the next action.</summary>
    private sealed class Steps(BlockPos? origin)
    {
        private readonly List<(string Text, int Count)> _steps = [];
        private string? _lastStand;

        public DemoAction? PendingMove;

        public int Count => _steps.Count;

        public string P(int x, int y, int z) => origin is not { } o ? $"{x},{y},{z}" : $"{Rel(x - o.X)},{Rel(y - o.Y)},{Rel(z - o.Z)}";

        private static string Rel(int d) => d >= 0 ? "+" + d : d.ToString();

        public void Add(string text)
        {
            if (_steps.Count > 0 && _steps[^1].Text == text) _steps[^1] = (text, _steps[^1].Count + 1);
            else _steps.Add((text, 1));
        }

        public void FlushMove()
        {
            if (PendingMove is not { } m) return;
            PendingMove = null;
            var text = $"stood at {P(m.X, m.Y, m.Z)}" + (m.Detail != null ? " " + m.Detail : "");
            if (text == _lastStand) return;
            _lastStand = text;
            Add(text);
        }

        public List<string> Texts() => _steps.Select(s => s.Count > 1 ? $"{s.Text} (x{s.Count})" : s.Text).ToList();
    }
}
