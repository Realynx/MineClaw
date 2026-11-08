using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private sealed record StorageSelection(List<P3>? Chests, Area? Area, string? BuildId, int? MinY, int? MaxY)
    {
        public bool Explicit => Chests != null || Area != null || BuildId != null;
        public bool Contains(P3 at) => (Area?.Contains(at) ?? true) && (MinY == null || at.Y >= MinY) && (MaxY == null || at.Y <= MaxY);
    }

    /// <summary>Structured storage management for main and hive brains. Floors are explicit Y bounds derived from discovery,
    /// never guessed from a natural-language ordinal. All mutations use the same sign-aware path as the viewer.</summary>
    public (JsonNode? Result, string? Error) StorageManage(JsonObject request)
    {
        lock (_lock)
        {
            var op = StorageText(request["op"]);
            if (op is not ("list" or "catalog" or "set" or "add" or "remove")) return (null, "op must be list|catalog|set|add|remove");
            if (op == "catalog") return StorageCatalog();
            if (op == "list") ReconcileStorageSigns(force: true);
            var (selection, selectionError) = StorageSelectionOf(request);
            if (selectionError != null) return (null, selectionError);
            if (selection == null) return (null, "invalid selection");
            if (StoragePage(request, out var offset, out var limit) is { } pageError) return (null, pageError);
            if (op != "list" && !selection.Explicit) return (null, "choose chests, area, or build_id before editing storage");
            if (StorageTargets(selection, out var targets, out var unloaded) is { } targetError) return (null, targetError);
            if (op == "list") return (StorageListing(selection, targets, unloaded, offset, limit), null);
            if (unloaded > 0) return (null, $"{unloaded} selected column(s) are not loaded; load the area before editing all of its chests");
            if (targets.Count == 0) return (null, "no chests in the selected area");
            var (filter, filterError) = ManagedFilter(request, op);
            if (filterError != null) return (null, filterError);
            string? priority = StorageText(request["priority"]), label = StorageText(request["label"]);
            if (request["priority"] != null && priority == null || request["label"] != null && label == null)
                return (null, "priority and label must be strings");
            if (filter == null && priority == null && label == null) return (null, "allow, deny, priority, or label required");
            var (changed, error) = StorageOp(op == "remove" ? "remove_entries" : op, targets, filter, priority, label);
            if (error != null) return (null, error);
            Save(force: true);
            return (new JsonObject
            {
                ["colony"] = Id, ["operation"] = op, ["result"] = changed,
                ["total_chests"] = targets.Count, ["storages"] = ManagedStorageRows(targets.Take(limit)),
            }, null);
        }
    }

    private (JsonNode? Result, string? Error) StorageCatalog()
    {
        if (World is not { } world) return (null, "no world view yet");
        var categories = new JsonArray();
        void Add(Category c, string? parent)
        {
            categories.Add(new JsonObject { ["id"] = c.Id, ["label"] = c.Label, ["parent"] = parent });
            foreach (var child in c.Children) Add(child, c.Id);
        }
        foreach (var root in StoryCategories.For(world.Data).Roots) Add(root, null);
        return (new JsonObject { ["colony"] = Id, ["categories"] = categories,
            ["entries"] = "Use category ids, item names, * (all items), numeric item ids, or id:meta for an exact variant." }, null);
    }

    private static string? StorageText(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
    private static int? StorageInteger(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var value) ? value : null;

    private (StorageSelection? Selection, string? Error) StorageSelectionOf(JsonObject request)
    {
        var buildId = StorageText(request["build_id"]);
        if (request["build_id"] != null && buildId == null) return (null, "build_id must be a string");
        int selectors = (request["chests"] != null ? 1 : 0) + (request["area"] != null ? 1 : 0) + (buildId != null ? 1 : 0);
        if (selectors > 1) return (null, "choose one selector: chests, area, or build_id");
        int? min = StorageInteger(request["y_min"]), max = StorageInteger(request["y_max"]);
        if (request["y_min"] != null && min == null || request["y_max"] != null && max == null || min > max)
            return (null, "y_min/y_max must be ordered whole-number block heights");
        List<P3>? chests = null;
        if (request["chests"] is { } chestNode)
        {
            if (chestNode is not JsonArray { Count: > 0 and <= 512 } positions) return (null, "chests must contain 1..512 [x,y,z] positions");
            chests = new();
            foreach (var node in positions)
            {
                if (node is not JsonArray { Count: 3 } a || a.Any(n => StorageInteger(n) == null) || P3.Parse(a) is not { } at)
                    return (null, "chests must be [[x,y,z], ...] with whole-number coordinates");
                chests.Add(at);
            }
        }
        Area? area = null;
        if (request["area"] is { } box)
        {
            if (box is not JsonArray { Count: 6 } a || a.Any(n => StorageInteger(n) == null) || Area.Parse(a) is not { } parsed)
                return (null, "area must be [x1,y1,z1,x2,y2,z2] with whole-number coordinates");
            area = parsed;
        }
        if (buildId != null)
        {
            if (Builds.FirstOrDefault(b => b.Id == buildId) is not { Ghosts.Count: > 0 } build)
                return (null, "build_id must name a build with known block bounds (use list)");
            area = StorageBuildBounds(build);
        }
        if (area is { } bounds)
        {
            bounds = bounds with { Y1 = Math.Max(bounds.Y1, min ?? bounds.Y1), Y2 = Math.Min(bounds.Y2, max ?? bounds.Y2) };
            if (bounds.Y1 > bounds.Y2) return (null, "the requested Y range does not intersect the selected area/build");
            long width = (long)bounds.X2 - bounds.X1 + 1, height = (long)bounds.Y2 - bounds.Y1 + 1, depth = (long)bounds.Z2 - bounds.Z1 + 1;
            if (width > 200_000 || height > 200_000 || depth > 200_000 || width * height * depth > 200_000)
                return (null, "area too large (max 200000 blocks); select a smaller floor or area");
            area = bounds;
        }
        return (new StorageSelection(chests, area, buildId, min, max), null);
    }

    private static string? StoragePage(JsonObject request, out int offset, out int limit)
    {
        offset = StorageInteger(request["offset"]) ?? 0;
        limit = StorageInteger(request["limit"]) ?? 24;
        return request["offset"] != null && StorageInteger(request["offset"]) == null || request["limit"] != null && StorageInteger(request["limit"]) == null
            || offset < 0 || limit is < 1 or > 64 ? "offset must be nonnegative and limit must be 1..64" : null;
    }

    private string? StorageTargets(StorageSelection selection, out List<P3> targets, out int unloaded)
    {
        var found = new HashSet<P3>();
        var missing = new HashSet<(int X, int Z)>();
        targets = new(); unloaded = 0;
        void Include(P3 at)
        {
            var primary = StorageAt(at)?.Chest;
            if (primary == null && DoubleHalf(at) is { } half)
                primary = at.X < half.X || at.X == half.X && at.Z < half.Z ? at : half;
            found.Add(primary ?? at);
        }
        if (selection.Chests != null)
        {
            foreach (var at in selection.Chests.Where(selection.Contains))
            {
                if (World is not { } w || !w.Loaded(at.X, at.Z)) { missing.Add((at.X, at.Z)); if (StorageAt(at) != null) Include(at); continue; }
                if (w.Block(at.X, at.Y, at.Z).Id is not (54 or 146)) return $"no chest at {at}";
                Include(at);
            }
        }
        else
        {
            foreach (var storage in Storages.Where(s => !s.Missing))
                if (selection.Contains(storage.Chest) || storage.DoubleWith is { } half && selection.Contains(half)) Include(storage.Chest);
            if (selection.Area is { } area)
                for (long xx = area.X1; xx <= area.X2; xx++)
                    for (long zz = area.Z1; zz <= area.Z2; zz++)
                    {
                        int x = (int)xx, z = (int)zz;
                        if (World is not { } w || !w.Loaded(x, z)) { missing.Add((x, z)); continue; }
                        for (long yy = area.Y1; yy <= area.Y2; yy++)
                        {
                            int y = (int)yy;
                            if (w.Block(x, y, z).Id is 54 or 146) Include(new P3(x, y, z));
                        }
                    }
        }
        targets = found.OrderBy(at => at.Y).ThenBy(at => at.X).ThenBy(at => at.Z).ToList();
        unloaded = missing.Count;
        return null;
    }

    private IEnumerable<BuildState> StorageBuildMembers(BuildState build) => Group(build.Spec) is { } group
        ? Builds.Where(b => Group(b.Spec) == group) : [build];

    private Area StorageBuildBounds(BuildState build)
    {
        var cells = StorageBuildMembers(build).SelectMany(b => b.Ghosts).Select(g => g.At).ToList();
        return Area.Of(cells.Min(p => p.X), cells.Min(p => p.Y), cells.Min(p => p.Z), cells.Max(p => p.X), cells.Max(p => p.Y), cells.Max(p => p.Z));
    }

    private JsonObject StorageListing(StorageSelection selection, List<P3> targets, int unloaded, int offset, int limit)
    {
        var builds = Builds.Where(b => b.Kind != "preserve" && b.Ghosts.Count > 0 && (selection.BuildId == null || b.Id == selection.BuildId))
            .OrderBy(b => b.Order).GroupBy(b => Group(b.Spec) is { } group ? "group:" + group : "build:" + b.Id).Select(g => g.First()).ToList();
        return new JsonObject
        {
            ["colony"] = Id, ["area"] = selection.Area?.Json(), ["complete"] = unloaded == 0, ["unloaded_columns"] = unloaded,
            ["offset"] = offset, ["limit"] = limit, ["total_chests"] = targets.Count, ["total_builds"] = builds.Count,
            ["storages"] = ManagedStorageRows(targets.Skip(offset).Take(limit)),
            ["builds"] = new JsonArray(builds.Skip(offset).Take(limit).Select(b =>
            {
                var bounds = StorageBuildBounds(b);
                var ys = targets.Concat(Storages.Where(s => !s.Missing).Select(s => s.Chest)).Where(bounds.Contains).Select(p => p.Y).Distinct().Order().ToList();
                var members = StorageBuildMembers(b).ToList();
                return (JsonNode)new JsonObject { ["id"] = b.Id, ["label"] = b.Label,
                    ["name"] = members.Select(m => StorageText(m.Spec?["name"])).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? b.Label,
                    ["group"] = Group(b.Spec), ["build_ids"] = new JsonArray(members.Select(m => (JsonNode)m.Id).ToArray()), ["kind"] = b.Kind, ["status"] = b.Status,
                    ["bounds"] = bounds.Json(), ["chest_y_levels"] = new JsonArray(ys.Select(y => (JsonNode)y).ToArray()) };
            }).ToArray()),
        };
    }

    private JsonArray ManagedStorageRows(IEnumerable<P3> targets) => new(targets.Select(at =>
    {
        var storage = StorageAt(at);
        var row = storage == null ? new JsonObject { ["chest"] = at.Json(), ["double_with"] = DoubleHalf(at)?.Json() } : StorageJson(storage);
        row["registered"] = storage != null;
        row.Remove("contents"); row.Remove("enchanted"); // location/filter discovery; the viewer's inventory remains unchanged
        return (JsonNode)row;
    }).ToArray());

    private (StorageFilter? Filter, string? Error) ManagedFilter(JsonObject request, string op)
    {
        if (request["allow"] == null && request["deny"] == null)
            return op is "add" or "remove" ? (null, "allow or deny entries required") : (null, null);
        if (World is not { } world) return (null, "no world view yet");
        var filter = new StorageFilter { Allow = op == "set" && request["allow"] == null ? ["*"] : [], Deny = [] };
        foreach (var (key, entries) in new[] { ("allow", filter.Allow), ("deny", filter.Deny) })
        {
            if (request[key] == null) continue;
            if (request[key] is not JsonArray values) return (null, key + " must be an array of category/item names");
            foreach (var value in values)
            {
                if (StorageText(value) is not { } text || StoryStorageKeywords.Normalize(text, world.Data) is not { } entry)
                    return (null, $"unknown {key} entry: {value}; use catalog for category ids");
                if (!entries.Contains(entry, StringComparer.OrdinalIgnoreCase)) entries.Add(entry);
            }
        }
        if (op != "set" && filter.Allow.Count + filter.Deny.Count == 0) return (null, "allow or deny entries required");
        return (filter, null);
    }
}
