using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>The structural checks of <see cref="DesignChecks.Evaluate"/>: the site, support, the void, doors and attachments.</summary>
public static partial class DesignChecks
{
    /// <summary>One evaluation's checks of a plan on its site, adding their issues to the evaluation.</summary>
    private sealed class SiteChecks(VoxelPlan plan, SiteContext site, DesignEvaluation e)
    {
        private readonly IBlockAccess w = site.World;
        private readonly List<BlockPos> never = new(), clear = new(), wet = new();
        private readonly Dictionary<string, List<BlockPos>> guarded = new();

        public void Add(IssueLevel l, string code, string text) => e.Issues.Add(new BlueprintIssue(l, code, text));

        /// <summary>A cell in the blueprint's own (local) coordinates.</summary>
        public string L(BlockPos p) => plan.Local(p);

        /// <summary>The first three cells, "+N" for the rest.</summary>
        public string List(IEnumerable<BlockPos> ps)
        {
            var l = ps.ToList();
            return string.Join(" ", l.Take(3).Select(L)) + (l.Count > 3 ? $" +{l.Count - 3}" : "");
        }

        private bool Solid(BlockPos p) => BuildBlocks.Supports(plan.Final(w, p));

        // ---- site loaded / overlap / obstruction / liquids

        public void Site()
        {
            var unloaded = plan.Blocks.Keys.Concat(plan.Air).Where(p => !w.IsLoaded(p.X, p.Z)).Select(p => (p.X, p.Z)).Distinct().Count();
            if (unloaded > 0) Add(IssueLevel.Warning, "unknown", $"{unloaded} columns of the site aren't loaded (unknown ground)");
            foreach (var p in plan.Blocks.Keys.Concat(plan.Air))
                if (ExistingAt(p) is { } id) Classify(p, id);
            if (never.Count > 0) Add(IssueLevel.Error, "never_break", $"overlaps {never.Count} never-break block(s) at {List(never)}: move or shrink it");
            foreach (var (why, list) in guarded) Add(IssueLevel.Error, "protected", $"overlaps {list.Count} protected block(s) ({why}) at {List(list)}: move it");
            if (wet.Count > 0) Add(IssueLevel.Error, "liquid", $"water/lava in {wet.Count} cell(s) at {List(wet)}: move it away from the gen/fluids");
            e.ToClear = clear.Count;
            if (clear.Count > 0) Add(IssueLevel.Warning, "obstruction", $"{clear.Count} existing block(s) to clear at {List(clear)}");
        }

        /// <summary>The loaded, non-air block standing in a cell that isn't already what the plan wants there, else null.</summary>
        private int? ExistingAt(BlockPos p)
        {
            if (!w.IsLoaded(p.X, p.Z)) return null;
            int id = w.GetBlock(p.X, p.Y, p.Z);
            if (id == 0) return null;
            if (plan.Blocks.TryGetValue(p, out var want) && SameState(want, id, w.GetMeta(p.X, p.Y, p.Z))) return null;
            return id;
        }

        private void Classify(BlockPos p, int id)
        {
            if (Data.Blocks.IsWater(id) || Data.Blocks.IsLava(id)) wet.Add(p);
            else if (!Data.Blocks.Get(id).Solid && id is not (Data.Blocks.Torch or Data.Blocks.Ladder or Data.Blocks.WoodenDoor)) clear.Add(p); // grass, flowers
            else Protect(p, id);
        }

        private void Protect(BlockPos p, int id)
        {
            var v = site.Verdict?.Invoke(p, id) ?? ProtectionVerdict.None;
            if (v.Level == ProtectionLevel.Hard) never.Add(p);
            else if (v.Protected) GuardedBy(string.IsNullOrWhiteSpace(v.Why) ? "a protected block" : v.Why).Add(p);
            else clear.Add(p);
        }

        private List<BlockPos> GuardedBy(string why)
        {
            if (!guarded.TryGetValue(why, out var list)) guarded[why] = list = new();
            return list;
        }

        // ---- support: every planned block connects through solids to existing ground

        public void Support(List<PlannedBlock> placed)
        {
            var reached = new HashSet<BlockPos>();
            var queue = new Queue<BlockPos>();
            foreach (var b in placed)
                if (TouchesGround(b.Pos) && reached.Add(b.Pos)) queue.Enqueue(b.Pos);
            while (queue.Count > 0) Spread(queue.Dequeue(), reached, queue);
            var floating = placed.Where(b => !reached.Contains(b.Pos)).Select(b => b.Pos).ToList();
            if (floating.Count > 0) Add(IssueLevel.Error, "floating", $"{floating.Count} block(s) float, touching nothing that stands: {List(floating)}");
            var falls = placed.Where(b => b.Kind == BuildKind.Gravity && !Solid(b.Pos.Down())).Select(b => b.Pos).ToList();
            if (falls.Count > 0) Add(IssueLevel.Error, "gravity", $"sand/gravel with nothing under it falls: {List(falls)}");
        }

        /// <summary>A face of the cell touches an existing block (not part of the plan) that holds things up.</summary>
        private bool TouchesGround(BlockPos pos) =>
            BlockFaceExtensions.All.Select(f => pos.Offset(f)).Any(n => !plan.Blocks.ContainsKey(n) && !plan.Air.Contains(n) && BuildBlocks.Supports(w.GetBlock(n.X, n.Y, n.Z)));

        private void Spread(BlockPos p, HashSet<BlockPos> reached, Queue<BlockPos> queue)
        {
            if (!BuildBlocks.Supports(plan.Blocks[p].Id) && plan.Blocks[p].Kind != BuildKind.Door) return; // torches/panes hold nothing up
            foreach (var f in BlockFaceExtensions.All)
            {
                var n = p.Offset(f);
                if (plan.Blocks.ContainsKey(n) && reached.Add(n)) queue.Enqueue(n);
            }
        }

        // ---- void: planned blocks with nothing at all below them

        public void Void(List<PlannedBlock> placed)
        {
            var overVoid = new List<BlockPos>();
            foreach (var col in placed.GroupBy(b => (b.Pos.X, b.Pos.Z)))
            {
                var low = col.OrderBy(b => b.Pos.Y).First().Pos;
                bool any = false;
                for (int y = low.Y - 1; y >= 0 && !any; y--) any = plan.Final(w, new BlockPos(low.X, y, low.Z)) != 0;
                if (!any && w.IsLoaded(low.X, low.Z)) overVoid.Add(low);
            }
            if (overVoid.Count > 0) Add(IssueLevel.Warning, "void", $"{overVoid.Count} column(s) hang over the void ({List(overVoid)}): needs scaffold from the edge");
        }

        // ---- doors, attachments

        public void Doors(List<PlannedBlock> placed)
        {
            foreach (var d in placed.Where(b => b.Kind == BuildKind.Door && !b.UpperDoor))
            {
                e.Doors++;
                if (!plan.Blocks.TryGetValue(d.Pos.Up(), out var top) || top.Id != d.Id || (top.Meta & 8) == 0)
                    Add(IssueLevel.Error, "door", $"door at {L(d.Pos)} has no upper half");
                if (!BuildBlocks.NormalCube(plan.Final(w, d.Pos.Down())))
                    Add(IssueLevel.Error, "door", $"door at {L(d.Pos)} needs a full block under it (it is {Data.Blocks.Get(plan.Final(w, d.Pos.Down())).Name})");
                var side = DoorOutside(plan, w, d) ?? BuildBlocks.DoorPlacerFacing(d.Meta).Opposite();
                e.DoorSides.Add(side);
                e.DrawnDoorSides.Add(side.Turn(-plan.Blueprint.Quarters)); // the drawing → world turn undone (facing north: south ↔ north)
            }
        }

        public void UpperDoorHalves(List<PlannedBlock> placed)
        {
            foreach (var u in placed.Where(b => b.UpperDoor))
                if (!plan.Blocks.TryGetValue(u.Pos.Down(), out var lo) || lo.Id != u.Id || (lo.Meta & 8) != 0)
                    Add(IssueLevel.Error, "door", $"door top half at {L(u.Pos)} has no lower half");
        }

        public void Attachments(List<PlannedBlock> placed)
        {
            foreach (var t in placed.Where(b => b.Id == Data.Blocks.Torch))
                if (!new[] { t.Pos.Down(), t.Pos.Offset(1, 0, 0), t.Pos.Offset(-1, 0, 0), t.Pos.Offset(0, 0, 1), t.Pos.Offset(0, 0, -1) }.Any(n => BuildBlocks.NormalCube(plan.Final(w, n))))
                    Add(IssueLevel.Error, "torch", $"torch at {L(t.Pos)} has no wall or floor to stand on");
            foreach (var l in placed.Where(b => b.Id == Data.Blocks.Ladder))
                if (!BuildBlocks.NormalCube(plan.Final(w, LadderWall(l))))
                    Add(IssueLevel.Error, "ladder", $"ladder at {L(l.Pos)} has no wall behind it");
        }

        private static BlockPos LadderWall(PlannedBlock l) =>
            l.Meta switch { 2 => l.Pos.Offset(0, 0, 1), 3 => l.Pos.Offset(0, 0, -1), 4 => l.Pos.Offset(1, 0, 0), _ => l.Pos.Offset(-1, 0, 0) };
    }
}
