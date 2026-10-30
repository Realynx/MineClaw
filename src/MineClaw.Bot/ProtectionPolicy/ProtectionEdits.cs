using MineClaw.Bot.Memory;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot;

public sealed partial class Protection
{
    /// <summary>The editing context (<see cref="EditContext"/>): which designs are open and whether the running work was asked for.
    /// Null = no editing context (saved designs protect like any build).</summary>
    public EditContext? Edits { get; set; }

    /// <summary>
    /// The editing-context rule for an <b>explicit</b> dig (the client asks only for a named cell: the dig tool, a script's dig, a
    /// build/revert step; never for a mine pick, a material, a path dig or a chop): true when every soft source at the block is a
    /// saved design open for editing or one of the bot's own marks, and the work was requested (<see cref="EditContext.Requested"/>).
    /// Never for a never-break mark, a container, a station/config block, a farm, a hive lease, a generator wall or any other
    /// indexed thing; an indexer "floor"/"unknown build" tag yields only inside such a box (the stair shaft's stone was both).
    /// Live 10-30: main captured Fox's spiral staircase at his request, then refused each next column he asked it to dig.
    /// </summary>
    public bool EditYields(BlockPos p, int id) => EditCheck(p, id) == null;

    /// <summary>
    /// The design checks' view while a design is revised or rebuilt (its designer/run under
    /// <see cref="EditContext.JobFor"/>): its own cells are its to change, every other build keeps its verdict. Without it
    /// "make the tower taller" over the built tower fails its own check: "overlaps N protected block(s) (part of the tower build)".
    /// </summary>
    public ProtectionVerdict CheckFor(BlockPos p, int id) =>
        EditContext.Job is { } job && EditCheck(p, id, only: job) == null ? ProtectionVerdict.None : Check(p, id);

    /// <summary>Why an explicit dig at <paramref name="p"/> does not count as editing (null = it does; see <see cref="EditYields"/>).
    /// <paramref name="only"/>: only that design's own boxes (and the bot's own marks) may yield.</summary>
    public string? EditCheck(BlockPos p, int id, long? only = null)
    {
        if (Edits is not { } e) return "no editing context";
        int dim = Dim;
        if (Marks?.NeverAt(p, dim) != null) return "a never-break mark";
        if (IsContainer(id)) return "a container (exact permits only)";
        if (_blockIds.Contains(id) || IsFarmPlant(id) || id == Blocks.Farmland) return "not part of a design";
        foreach (var r in _configRegions) if (Inside(p, r.Min, r.Max)) return "a protected region from my config";
        if (Claims != null && !Claims.IsFree(ClaimKey.Block(p, dim))) return "another hive bot's part";
        if (IndexerKind?.Invoke(p) is { } kind && kind is not (IndexKinds.UnknownStructure or WorldIndexer.FloorKind)) return $"a {kind.Replace('_', ' ')}";
        var boxes = MemoryRegions(p, id).ToList();
        if (boxes.Count == 0) return "not a saved design or my own mark";
        if (e.Requested() == null) return "nobody asked (my own choice)";
        foreach (var b in boxes)
        {
            if (b.Design is { } d)
            {
                if (only is { } o && d != o) return $"design #{d} isn't the one being worked on (#{o})";
                if (e.Open(d) == null) return $"design #{d} isn't open for editing";
            }
            else if (!(b.Structure && e.BotMade(b.Owner))) return $"the {b.Label ?? "remembered"} build is {b.Owner ?? "someone"}'s";
        }
        return null;
    }

    /// <summary>
    /// The "structure" refusal: "part of the spiral_staircase build (design #12; editing it needs a trusted player's ask)", "part of
    /// the stair_shaft_seal_y16 build (my own mark; a trusted player's ask digs through it)", else "part of the main house build". One line
    /// that says how to proceed without the unlock phrase too.
    /// </summary>
    private string StructureWhy(ProtectedBox b)
    {
        var label = string.IsNullOrWhiteSpace(b.Label) ? null : b.Label;
        if (b.Design is { } d)
            return $"part of the {label ?? "saved"} build (design #{d}; editing it needs a trusted player's ask)";
        if (b.Structure && Edits?.BotMade(b.Owner) == true)
            return $"part of the {label ?? "remembered"} build (my own mark; a trusted player's ask digs through it)";
        return label == null ? "part of a remembered build" : $"part of the {label} build";
    }
}
