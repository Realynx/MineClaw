using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

public static partial class BlueprintNormalizer
{
    private sealed partial class Canvas
    {
        public void ReportUnusedPalette()
        {
            foreach (var c in bp.Palette.Keys.Where(k => !used.Contains(k)))
                issues.Add(new(IssueLevel.Info, "palette", $"palette '{c}' unused"));
        }

        /// <summary>The canonical state of every drawn block cell (a cell's op facing, else the palette's meta).</summary>
        public Dictionary<(int X, int Y, int Z), (int Id, int Meta)> States()
        {
            var states = new Dictionary<(int X, int Y, int Z), (int Id, int Meta)>();
            for (int y = 0; y < H; y++)
                for (int z = 0; z < D; z++)
                    for (int x = 0; x < W; x++)
                    {
                        char c = grid[x, y, z];
                        if (c is Air or Keep || !bp.Palette.TryGetValue(c, out var e)) continue;
                        int meta = facing.TryGetValue((x, y, z), out var f) && BuildBlocks.MetaFor(e.Id, f) is { } fm ? fm : e.CanonicalMeta;
                        states[(x, y, z)] = (e.Id, meta);
                    }
            return states;
        }

        private static bool SameAt(Dictionary<(int X, int Y, int Z), (int Id, int Meta)> states, int x, int y, int z, int id) =>
            states.TryGetValue((x, y, z), out var s) && s.Id == id;

        /// <summary>Door halves: a door is two blocks (three or more stacked is a mistake), the upper half derived from the lower.</summary>
        public void CheckDoors(Dictionary<(int X, int Y, int Z), (int Id, int Meta)> states)
        {
            RemoveDoorStacks(states);
            foreach (var ((x, y, z), (id, meta)) in states.OrderBy(kv => kv.Key.Y).ToList())
            {
                if (!states.ContainsKey((x, y, z)) || BuildBlocks.KindOf(id) != BuildKind.Door) continue;
                PairDoor(states, x, y, z, id, meta);
            }
        }

        /// <summary>A door is two blocks: three or more drawn on top of each other is a mistake, not two doors.</summary>
        private void RemoveDoorStacks(Dictionary<(int X, int Y, int Z), (int Id, int Meta)> states)
        {
            foreach (var ((x, y, z), (id, _)) in states.ToList())
            {
                if (BuildBlocks.KindOf(id) != BuildKind.Door || !SameAt(states, x, y + 1, z, id) || !SameAt(states, x, y + 2, z, id) || SameAt(states, x, y - 1, z, id))
                    continue;
                Err("door", $"door cells at {x},{y}..{y + 2},{z} stack three high: a door is two blocks");
                for (int k = y + 2; SameAt(states, x, k, z, id); k++) states.Remove((x, k, z));
            }
        }

        private void PairDoor(Dictionary<(int X, int Y, int Z), (int Id, int Meta)> states, int x, int y, int z, int id, int meta)
        {
            if (states.TryGetValue((x, y - 1, z), out var below) && below.Id == id && (below.Meta & 8) == 0)
            {
                states[(x, y, z)] = (id, (below.Meta & 7) | 8);
                return;
            }
            // A lower half: the cell above becomes the upper half (drawn as the same symbol, or left free).
            if (y + 1 >= H) { Err("door", $"door at {x},{y},{z} is on the top layer: its upper half needs the layer above"); return; }
            if (states.TryGetValue((x, y + 1, z), out var above) && above.Id != id)
            {
                Err("door", $"door at {x},{y},{z} needs its upper half free, but {BuildBlocks.Name(above.Id, above.Meta)} is drawn above it");
                return;
            }
            if (grid[x, y + 1, z] is Keep or Air) states[(x, y + 1, z)] = (id, (meta & 7) | 8);
            CheckDoorFloor(states, x, y, z);
        }

        private void CheckDoorFloor(Dictionary<(int X, int Y, int Z), (int Id, int Meta)> states, int x, int y, int z)
        {
            if (y >= 1 && grid[x, y - 1, z] == Air) Err("door", $"door at {x},{y},{z} has air drawn under it: a door stands on a full block");
            else if (y >= 1 && states.TryGetValue((x, y - 1, z), out var floor) && !BuildBlocks.NormalCube(floor.Id))
                Err("door", $"door at {x},{y},{z} stands on {BuildBlocks.Name(floor.Id, floor.Meta)}: it needs a full opaque block under it");
        }

        /// <summary>Slab on a same-type slab merges into a double slab in the lower cell (mc-dev BlockStep.onPlace).
        /// (b1.8.1 only: a 1.12.2 slab stays in its own cell.)</summary>
        public void CheckSlabs(Dictionary<(int X, int Y, int Z), (int Id, int Meta)> states)
        {
            if (bp.Version == DesignVersions.Release) return;
            foreach (var ((x, y, z), (id, meta)) in states)
                if (id == Blocks.Slab && states.TryGetValue((x, y - 1, z), out var b2) && b2.Id == Blocks.Slab && (b2.Meta & 7) == (meta & 7) && (b2.Meta & 8) == 0)
                    Err("slab", $"slab at {x},{y},{z} sits on the same slab type: they would merge into a double slab (use a full block)");
        }

        /// <summary>A versioned design holds any state of its version's design palette; a legacy one only what the builder builds.</summary>
        public void CheckMetas(Dictionary<(int X, int Y, int Z), (int Id, int Meta)> states)
        {
            var palette = bp.Version != null ? DesignPalette.Current : null;
            foreach (var (k, (id, meta)) in states)
                if ((palette != null ? palette.Refusal(id, meta) : BuildBlocks.MetaProblem(id, meta)) is { } mp) Err("meta", $"{k.X},{k.Y},{k.Z}: {mp}");
        }

        /// <summary>Nothing to place (a mine may be nothing but cells to dig: its forced air), or too much.</summary>
        public void CheckBlockCount(Dictionary<(int X, int Y, int Z), (int Id, int Meta)> states)
        {
            if (states.Count == 0 && !(bp.Mine && HasForcedAir())) Err("empty", "the blueprint places no blocks");
            if (states.Count > MaxBlocks) Err("size", $"{states.Count} blocks: at most {MaxBlocks} per blueprint");
        }

        private bool HasForcedAir()
        {
            foreach (var c in grid)
                if (c == Air) return true;
            return false;
        }

        /// <summary>World placement: rotate from the drawn front to the facing (Blueprint.Quarters), then offset by the origin.</summary>
        public VoxelPlan Place(Dictionary<(int X, int Y, int Z), (int Id, int Meta)> states, BlockPos origin, int worldHeight)
        {
            var plan = new VoxelPlan { Blueprint = bp, Origin = origin };
            int q = bp.Quarters;
            foreach (var ((x, y, z), (id, meta)) in states)
            {
                var p = ToWorld(origin, q, x, y, z);
                plan.Blocks[p] = new PlannedBlock(p, id, BuildBlocks.Rotate(id, meta, q));
            }
            for (int y = 0; y < H; y++)
                for (int z = 0; z < D; z++)
                    for (int x = 0; x < W; x++)
                        if (grid[x, y, z] == Air && !states.ContainsKey((x, y, z))) plan.Air.Add(ToWorld(origin, q, x, y, z));
            if (origin.Y < 1 || origin.Y + H > worldHeight) Err("bounds", $"build y {origin.Y}..{origin.Y + H - 1} leaves the world (1..{worldHeight - 1})");
            return plan;
        }

        private BlockPos ToWorld(BlockPos origin, int q, int x, int y, int z)
        {
            var (wx, wz) = q switch
            {
                1 => (D - 1 - z, x),
                2 => (W - 1 - x, D - 1 - z),
                3 => (z, W - 1 - x),
                _ => (x, z),
            };
            return new BlockPos(origin.X + wx, origin.Y + y, origin.Z + wz);
        }
    }
}
