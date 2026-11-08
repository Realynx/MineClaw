using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

public static partial class StoryConstruction
{
    /// <summary>A placed plan's sparse block states. Zero denotes a removal; replacement plans keep that space empty.
    /// Unlike a shape's bounding box, this format cannot regenerate cells the owner has edited out.</summary>
    private static (List<GhostState>, Material?, string?) ExactCells(IGameData data, JsonObject item, IStoryWorld? world)
    {
        if (item["cells"] is not JsonArray { Count: > 0 and <= 4096 } cells) return ([], null, "cells required (1..4096)");
        bool exact = item["exact_state"] is JsonValue ev && ev.TryGetValue<bool>(out var e) && e;
        var ghosts = new List<GhostState>();
        var seen = new HashSet<P3>();
        Material? material = null;
        foreach (var row in cells)
        {
            if (row is not JsonArray { Count: 5 } a || a.Any(n => n is not JsonValue v || !v.TryGetValue<int>(out _)))
                return ([], null, "cells must be [[x,y,z,id,meta], ...] with integers");
            var at = new P3(a[0]!.GetValue<int>(), a[1]!.GetValue<int>(), a[2]!.GetValue<int>());
            int id = a[3]!.GetValue<int>(), meta = a[4]!.GetValue<int>();
            if (Math.Abs((long)at.X) > 30_000_000 || Math.Abs((long)at.Z) > 30_000_000 || at.Y < 1 || at.Y >= (world?.Height ?? 256))
                return ([], null, "edited cells must fit within world coordinates and y 1..world height-1");
            if (!seen.Add(at)) return ([], null, $"duplicate edited cell {at}");
            if (meta is < 0 or > 15 || id == 0 && meta != 0) return ([], null, $"invalid block metadata at {at}");
            if (id != 0 && !data.BlockExists(id)) return ([], null, $"unknown block id {id} at {at}");
            if (id == 0)
            {
                var current = world?.Block(at.X, at.Y, at.Z) ?? (Id: 0, Meta: 0);
                ghosts.Add(new GhostState { At = at, Id = current.Id, Meta = current.Meta, Remove = true });
                continue;
            }
            if (exact && MineClaw.World.BlockOrientation.ExactPlacementRefusal(id, meta, data.VersionId == "1.12.2") is { } refusal)
                return ([], null, $"{at}: {refusal}");
            var (placing, why) = PlacingItem(data, id, meta);
            if (exact && placing == null) return ([], null, $"{at}: {why}");
            bool second = MineClaw.Bot.Blueprints.DesignFacing.SecondHalf(id, meta);
            ghosts.Add(new GhostState { At = at, Id = id, Meta = meta, Item = placing, Job = !second && placing != null,
                State = !second && placing == null ? "unsupported" : "todo", Reason = !second ? why : null });
            material ??= Resolve(data, new JsonArray(id, meta));
        }
        return (ghosts, material, null);
    }
}
