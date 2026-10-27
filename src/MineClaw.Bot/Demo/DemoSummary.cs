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
        var steps = new List<(string Text, int Count)>();
        DemoAction? pendingMove = null;
        string? lastStand = null;

        string P(int x, int y, int z) => origin is not { } o ? $"{x},{y},{z}" : $"{Rel(x - o.X)},{Rel(y - o.Y)},{Rel(z - o.Z)}";
        static string Rel(int d) => d >= 0 ? "+" + d : d.ToString();

        void Add(string text)
        {
            if (steps.Count > 0 && steps[^1].Text == text) steps[^1] = (text, steps[^1].Count + 1);
            else steps.Add((text, 1));
        }

        void FlushMove()
        {
            if (pendingMove is not { } m) return;
            pendingMove = null;
            var text = $"stood at {P(m.X, m.Y, m.Z)}" + (m.Detail != null ? " " + m.Detail : "");
            if (text == lastStand) return;
            lastStand = text;
            Add(text);
        }

        foreach (var a in actions)
        {
            if (a.Kind == DemoActionKind.Move) { pendingMove = a; continue; }
            if (a.Kind == DemoActionKind.Chat) continue;
            FlushMove();
            string at = P(a.X, a.Y, a.Z), block = a.Block ?? "block";
            Add(a.Kind switch
            {
                DemoActionKind.UseBlock => a.Detail != null ? $"right-clicked {a.Detail} on {block} at {at}" : $"used {block} at {at}",
                DemoActionKind.OpenContainer => $"opened {block} at {at}",
                DemoActionKind.BlockPlaced => a.Detail switch
                {
                    "appeared" => $"{block} appeared at {at}",
                    "poured" => $"poured {block} at {at}",
                    _ => $"placed {block} at {at}",
                },
                DemoActionKind.BlockBroken => a.Detail switch
                {
                    "vanished" => $"{block} vanished at {at}",
                    "scooped" => $"scooped {block} at {at}",
                    _ => $"broke {block} at {at}",
                },
                DemoActionKind.ClickSlot => a.Detail ?? "clicked a slot",
                _ => a.Detail ?? a.Kind.ToString().ToLowerInvariant(),
            });
        }
        if (steps.Count == 0) FlushMove();

        var result = steps.Select(s => s.Count > 1 ? $"{s.Text} (x{s.Count})" : s.Text).ToList();
        if (result.Count > maxSteps)
        {
            int dropped = result.Count - (maxSteps - 1);
            result = result.Take(maxSteps - 1).Append($"...({dropped} more)").ToList();
        }
        return result;
    }
}
