using System.Globalization;
using MineClaw.Bot.Blueprints;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>What a plan step does.</summary>
public enum StepAction { Clear, Place, Scaffold, RemoveScaffold, PourWater, PickupWater }

/// <summary>
/// How a step is done (each a verified primitive with its preconditions and cost):
/// <see cref="Normal"/> a visible face from a walkable stand; <see cref="Edge"/> the same from an edge stand (sneaking,
/// leaning out to bridge); <see cref="Hidden"/> a face the bot can't see (the underside of the floor, an edge block's outer
/// face), clicked within reach: mc-dev NetServerHandler checks distance only; <see cref="Water"/> from inside a water
/// column poured at the edge (descend, hover at the depth, place, climb back out, take the water back).
/// </summary>
public enum Technique { Normal, Edge, Hidden, Water }

public enum StepStatus { Pending, Done, Verified, Skipped }

/// <summary>One planned step: the target cell, the block, where to stand, which face to click, and its progress.</summary>
public sealed class PlanStep
{
    public int Index { get; set; }
    public StepAction Action { get; set; }
    public Technique Technique { get; set; }
    public BlockPos Target { get; set; }
    public int Id { get; set; }
    public int Meta { get; set; }
    public int Item { get; set; }
    public int Damage { get; set; }
    /// <summary>Foot cell to stand in (in a water column: the hover cell, feet at its y).</summary>
    public BlockPos Stand { get; set; }
    /// <summary>The face clicked on the block at Target − normal(Face) (placements, pours); null for digs.</summary>
    public BlockFace? Face { get; set; }
    public bool HiddenFace { get; set; }
    /// <summary>Look yaw for facing metas (stairs, doors, chests: the server reads only the yaw).</summary>
    public float? Yaw { get; set; }
    /// <summary>The water column the stand is in (index into <see cref="BuildPlan.Columns"/>), or −1.</summary>
    public int Column { get; set; } = -1;
    /// <summary>Steps that must be done first (the one that placed the clicked support, a column's pour).</summary>
    public List<int> Deps { get; set; } = new();
    public int Part { get; set; }
    /// <summary>One of the first placements that show the build's position and direction (the confirmation gate).</summary>
    public bool Gate { get; set; }
    public StepStatus Status { get; set; }
    public string? Note { get; set; }
    /// <summary>Estimated ticks (travel + the action).</summary>
    public double Cost { get; set; }

    public BlockPos Against => Face is { } f ? Target - f.Normal() : Target;
    public bool IsPlacement => Action is StepAction.Place or StepAction.Scaffold;
    public bool Finished => Status is StepStatus.Done or StepStatus.Verified or StepStatus.Skipped;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"#{Index} {Action} {BuildBlocks.Name(Id, Meta)} {Target.X},{Target.Y},{Target.Z} [{Technique}] from {Stand.X},{Stand.Y},{Stand.Z}{(Face is { } f ? " face " + f : "")}");
}

/// <summary>A water column the plan pours: the source beside the edge floor block, the stand it's poured from and exited to.</summary>
public sealed class PlanColumn
{
    public int Index { get; set; }
    public BlockPos Source { get; set; }
    /// <summary>The clicked face of the edge floor block (Source − normal(Face)).</summary>
    public BlockFace Face { get; set; }
    /// <summary>The edge stand: poured from, stepped off into the column, climbed back out onto.</summary>
    public BlockPos Entry { get; set; }
    /// <summary>The deepest feet height the plan hovers at.</summary>
    public int Depth { get; set; }
    public int DescendTicks { get; set; }
    public int AscendTicks { get; set; }
    public bool Poured { get; set; }
    public bool Removed { get; set; }

    public SwimColumn Swim => new(Source.X, Source.Y, Source.Z);
}

/// <summary>A planned cell the planner couldn't schedule, with why (reported before building).</summary>
public sealed record Infeasible(BlockPos Pos, int Id, string Why);

/// <summary>Rotation (clockwise quarter turns about <see cref="Anchor"/>), mirror (across the main axis) and shift of a plan.</summary>
public sealed record BuildTransform(int Quarters = 0, bool Mirror = false, int Dx = 0, int Dy = 0, int Dz = 0, BlockPos Anchor = default, char Axis = 'x')
{
    public static BuildTransform None { get; } = new();
    public bool IsNone => Quarters % 4 == 0 && !Mirror && Dx == 0 && Dy == 0 && Dz == 0;

    /// <summary>This transform followed by <paramref name="next"/> (turns and mirrors keep the first anchor; shifts add).</summary>
    public BuildTransform Then(BuildTransform next) => this with
    {
        Quarters = ((Quarters + next.Quarters) % 4 + 4) % 4, Mirror = Mirror ^ next.Mirror,
        Dx = Dx + next.Dx, Dy = Dy + next.Dy, Dz = Dz + next.Dz,
        Anchor = IsNone ? next.Anchor : Anchor, Axis = IsNone ? next.Axis : Axis,
    };

    public string Text => IsNone ? "as designed" : string.Join(", ", new[]
    {
        Quarters % 4 == 2 ? "turned around" : Quarters % 4 == 1 ? "turned right" : Quarters % 4 == 3 ? "turned left" : null,
        Mirror ? "mirrored" : null,
        Dx != 0 || Dy != 0 || Dz != 0 ? $"moved {Dx},{Dy},{Dz}" : null,
    }.Where(s => s != null));
}

/// <summary>Confirmation gate: after the first few placements, pause and ask once; continue only on a trusted yes.</summary>
public enum GateState { Pending, Waiting, Approved, Off }

public sealed class GateInfo
{
    public GateState State { get; set; } = GateState.Pending;
    /// <summary>Placements before asking.</summary>
    public int After { get; set; } = 5;
    public string? Question { get; set; }
    public DateTimeOffset? AskedAt { get; set; }
    public string? ApprovedBy { get; set; }
    /// <summary>The direction/site came from inference (facing, pointing, "this way").</summary>
    public bool Inferred { get; set; }
    public int Rounds { get; set; }
    /// <summary>Blocks the current gate round placed (taken down on a correction).</summary>
    public List<BlockPos> Blocks { get; set; } = new();
    /// <summary>A correction answered while no build was running (restart): applied when the build starts again.</summary>
    public BuildTransform? Correction { get; set; }
}

/// <summary>
/// The slicer's output for one part of a blueprint (persisted: resume after an interruption): the step list with techniques,
/// stands, faces and per-step status; the water columns; what couldn't be planned and why; the confirmation gate; every block
/// the build placed (undo) and the scaffolding up right now.
/// </summary>
public sealed class BuildPlan
{
    public long BlueprintId { get; set; }
    public int Part { get; set; }
    public int Parts { get; set; } = 1;
    /// <summary>Content hash of the voxel plan it was sliced from (a changed design = a new plan).</summary>
    public ulong Version { get; set; }
    public BlockPos Origin { get; set; }
    public Dir Facing { get; set; }
    public int Dim { get; set; }
    public BuildTransform Transform { get; set; } = BuildTransform.None;
    public List<PlanStep> Steps { get; set; } = new();
    public List<PlanColumn> Columns { get; set; } = new();
    public List<Infeasible> Infeasible { get; set; } = new();
    public GateInfo Gate { get; set; } = new();
    public List<BlockPos> Placed { get; set; } = new();
    public List<BlockPos> ScaffoldUp { get; set; } = new();
    public string State { get; set; } = "planned";
    public string? Note { get; set; }
    public DateTimeOffset Updated { get; set; }
    /// <summary>Re-plans so far (resume, deviations).</summary>
    public int Replans { get; set; }

    public int Placements => Steps.Count(s => s.Action == StepAction.Place);
    public int Scaffolds => Steps.Count(s => s.Action == StepAction.Scaffold);
    public int Done => Steps.Count(s => s.Finished);
    public double Progress => Steps.Count == 0 ? 1 : (double)Done / Steps.Count;
    public double EstSeconds => Steps.Where(s => !s.Finished).Sum(s => s.Cost) / 20.0;

    /// <summary>Items the remaining steps use (placements by item; scaffold as clutch blocks; a bucket per column).</summary>
    public Dictionary<string, int> Needs()
    {
        var d = new Dictionary<string, int>();
        foreach (var s in Steps.Where(s => !s.Finished))
        {
            if (s.Action == StepAction.Place) Add(Name(s.Item, s.Damage), 1);
            else if (s.Action == StepAction.Scaffold) Add("scaffold block", 1);
        }
        if (Columns.Any(c => !c.Removed)) Add("water bucket", 1);
        return d;
        void Add(string k, int n) => d[k] = d.GetValueOrDefault(k) + n;
    }

    private static string Name(int item, int damage) =>
        (BuildBlocks.HasVariants(item) ? Items.VariantName(item, damage) : Items.Get(item).Name).Replace('_', ' ');

    /// <summary>"47 placements, 6 scaffold, 2 water descents, needs: 86 cobblestone, 1 water bucket; est. 6 min".</summary>
    public string Summary()
    {
        var parts = new List<string> { $"{Placements} placements" };
        if (Scaffolds > 0) parts.Add($"{Scaffolds} scaffold");
        int hidden = Steps.Count(s => s.Technique == Technique.Hidden);
        if (Columns.Count > 0) parts.Add($"{Columns.Count} water descent{(Columns.Count > 1 ? "s" : "")}");
        if (hidden > 0) parts.Add($"{hidden} underside/hidden-face");
        var needs = Needs().OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}").ToList();
        var mins = EstSeconds / 60;
        var text = string.Join(", ", parts) + (needs.Count > 0 ? ", needs: " + string.Join(", ", needs.Take(4)) : "")
                   + string.Create(CultureInfo.InvariantCulture, $"; est. {(mins < 1 ? "<1" : Math.Round(mins).ToString(CultureInfo.InvariantCulture))} min");
        if (Infeasible.Count > 0)
            text += $"; can't place {Infeasible.Count}: " + string.Join("; ", Infeasible.Take(2).Select(i => $"{BuildBlocks.Name(i.Id, 0)} at {i.Pos.X},{i.Pos.Y},{i.Pos.Z} ({i.Why})"));
        return text;
    }

    /// <summary>
    /// Disjoint step sets for <paramref name="n"/> bodies (strips along the longer side, <see cref="ConstructionPlanner.Split"/>):
    /// each step goes to the strip holding its target; a dependency on another part's step becomes a wait
    /// (<see cref="PlanPartition.Waits"/>: the runner holds that step until the other part has done it).
    /// </summary>
    public List<PlanPartition> Partition(VoxelPlan plan, int n)
    {
        var strips = ConstructionPlanner.Split(plan, n, Dim);
        int PartOf(BlockPos p)
        {
            for (int i = 0; i < strips.Count; i++) if (strips[i].Allows(p)) return i;
            return 0;
        }
        var owner = Steps.ToDictionary(s => s.Index, s => PartOf(s.Target));
        var result = strips.Select((s, i) => new PlanPartition(i, strips.Count, s, new List<PlanStep>(), new Dictionary<int, List<int>>())).ToList();
        foreach (var s in Steps)
        {
            int k = owner[s.Index];
            s.Part = k;
            result[k].Steps.Add(s);
            var cross = s.Deps.Where(d => owner.TryGetValue(d, out var o) && o != k).ToList();
            if (cross.Count > 0) result[k].Waits[s.Index] = cross;
        }
        return result;
    }
}

/// <summary>One body's share of a plan: its steps and, per step, the other parts' steps it waits for.</summary>
public sealed record PlanPartition(int Index, int Count, BuildPart Strip, List<PlanStep> Steps, Dictionary<int, List<int>> Waits);
