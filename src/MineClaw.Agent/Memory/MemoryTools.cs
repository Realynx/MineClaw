using System.Globalization;
using System.Text.RegularExpressions;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Memory;

/// <summary>Model-facing memory tools over <see cref="MemoryStore"/>. All results are short.</summary>
public static class MemoryTools
{
    /// <param name="position">Current block position (used when coordinates are omitted); provided by the client layer.</param>
    public static void RegisterAll(ToolRegistry registry, MemoryStore memory, Func<BlockPos?>? position = null)
    {
        registry.Register("remember_waypoint", "Save a named location (defaults to where you stand).",
            ToolSchema.Create().Str("name").Int("x", required: false).Int("y", required: false).Int("z", required: false)
                .Str("note", required: false).Build(),
            (input, _) =>
            {
                var name = input.ReqStr("name");
                var (x, y, z) = Coords(input, "x", "y", "z", position);
                memory.SetWaypoint(name, x, y, z, input.Str("note"));
                return Task.FromResult(ToolResult.Success($"waypoint {name} = {x},{y},{z}"));
            }, cheapTier: true);

        registry.Register("mark_structure",
            "Record a structure: kind (cobble_generator|chest_storage|furnace_bank|farm|tree_farm|unknown|...), corner(s), key blocks.",
            ToolSchema.Create().Str("name").Str("kind")
                .Int("x1", required: false).Int("y1", required: false).Int("z1", required: false)
                .Int("x2", required: false).Int("y2", required: false).Int("z2", required: false)
                .StrArray("keys", "e.g. \"mine_here@4,65,-2\"", required: false)
                .Str("note", required: false).Str("taught_by", required: false).Num("confidence", "0..1", required: false).Build(),
            (input, _) =>
            {
                var name = input.ReqStr("name");
                var hasMin = input.Int("x1") != null;
                BlockPos? min = null, max = null;
                if (hasMin)
                {
                    var (x, y, z) = Coords(input, "x1", "y1", "z1", position);
                    min = new BlockPos(x, y, z);
                }
                else if (position?.Invoke() is { } here) min = here;
                if (input.Int("x2") is { } x2 && min != null)
                {
                    var p2 = new BlockPos(x2, input.Int("y2") ?? min.Y, input.Int("z2") ?? min.Z);
                    max = new BlockPos(Math.Max(min.X, p2.X), Math.Max(min.Y, p2.Y), Math.Max(min.Z, p2.Z));
                    min = new BlockPos(Math.Min(min.X, p2.X), Math.Min(min.Y, p2.Y), Math.Min(min.Z, p2.Z));
                }
                var keys = input.StrList("keys").Select(ParseKey).Where(k => k != null).Cast<KeyBlock>().ToList();
                var s = memory.UpsertStructure(name, s =>
                {
                    s.Kind = input.Str("kind") ?? s.Kind;
                    if (min != null) { s.Min = min; s.Max = max ?? s.Max; }
                    foreach (var k in keys)
                    {
                        s.KeyBlocks.RemoveAll(o => o.Label.Equals(k.Label, StringComparison.OrdinalIgnoreCase));
                        s.KeyBlocks.Add(k);
                    }
                    if (input.Str("note") is { } n) s.Note = n;
                    if (input.Str("taught_by") is { } t) s.TaughtBy = t;
                    if (input.Num("confidence") is { } c) s.Confidence = Math.Clamp(c, 0, 1);
                });
                return Task.FromResult(ToolResult.Success("saved " + MemoryStore.FormatStructure(s)));
            });

        registry.Register("describe_structure", "Add how-to-use steps to a known structure (short imperative steps).",
            ToolSchema.Create().Str("name").StrArray("steps").Str("taught_by", required: false)
                .Bool("replace", "replace existing steps", required: false).Num("confidence", "0..1", required: false).Build(),
            (input, _) =>
            {
                var name = input.ReqStr("name");
                var steps = input.StrList("steps");
                if (steps.Count == 0) return Task.FromResult(ToolResult.Error("no steps"));
                var s = memory.UpsertStructure(name, s =>
                {
                    if (input.Bool("replace") == true) s.Steps.Clear();
                    s.Steps.AddRange(steps.Select(x => TextUtil.Truncate(TextUtil.OneLine(x), 120)));
                    if (s.Steps.Count > 15) s.Steps.RemoveRange(0, s.Steps.Count - 15);
                    if (input.Str("taught_by") is { } t) s.TaughtBy = t;
                    if (input.Num("confidence") is { } c) s.Confidence = Math.Clamp(c, 0, 1);
                });
                return Task.FromResult(ToolResult.Success($"{s.Name}: {s.Steps.Count} steps"));
            });

        registry.Register("remember_note", "Save a fact about a player, the base or the world.",
            ToolSchema.Create().Str("subject", "player name, base, world...").Str("text").Build(),
            (input, _) =>
            {
                var n = memory.AddNote(input.ReqStr("subject"), TextUtil.Truncate(TextUtil.OneLine(input.ReqStr("text")), 200));
                return Task.FromResult(ToolResult.Success($"note#{n.Id} saved"));
            }, cheapTier: true);

        registry.Register("recall", "Search memory (waypoints, structures, notes, routines) by keywords.",
            ToolSchema.Create().Str("query").Build(),
            (input, _) => Task.FromResult(ToolResult.Success(memory.Recall(input.Str("query") ?? ""))), cheapTier: true);

        registry.Register("forget", "Delete a memory by name (note: id like #3).",
            ToolSchema.Create().Str("name").Str("kind", required: false, enumValues: ["waypoint", "structure", "note", "routine"]).Build(),
            (input, _) =>
            {
                int n = memory.Forget(input.Str("kind"), input.ReqStr("name"));
                return Task.FromResult(n > 0 ? ToolResult.Success($"forgot {n}") : ToolResult.Error("nothing matched"));
            });

        registry.Register("list_memory", "List saved names (optionally one kind).",
            ToolSchema.Create().Str("kind", required: false, enumValues: ["waypoint", "structure", "note", "routine"]).Build(),
            (input, _) => Task.FromResult(ToolResult.Success(memory.List(input.Str("kind")))), cheapTier: true);
    }

    private static (int, int, int) Coords(System.Text.Json.JsonElement input, string xs, string ys, string zs, Func<BlockPos?>? position)
    {
        var x = input.Int(xs);
        var y = input.Int(ys);
        var z = input.Int(zs);
        if (x != null && y != null && z != null) return (x.Value, y.Value, z.Value);
        var here = position?.Invoke() ?? throw new ToolInputException("coordinates required");
        return (x ?? here.X, y ?? here.Y, z ?? here.Z);
    }

    private static KeyBlock? ParseKey(string s)
    {
        var m = Regex.Match(s, @"^\s*(?<label>[^@]+?)\s*@\s*(?<x>-?\d+)\s*[, ]\s*(?<y>-?\d+)\s*[, ]\s*(?<z>-?\d+)\s*$");
        if (!m.Success) return null;
        return new KeyBlock
        {
            Label = m.Groups["label"].Value,
            X = int.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture),
            Y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
            Z = int.Parse(m.Groups["z"].Value, CultureInfo.InvariantCulture),
        };
    }
}
