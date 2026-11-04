using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

public static partial class BlueprintNormalizer
{
    private sealed partial class Canvas
    {
        /// <summary>roof X flat|gable|slope:dir x1,y,z1 x2,z2 [ridge]: a stairs symbol also gets each step's facing.</summary>
        public void Roof(BlueprintOp op, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi)
        {
            var styleParts = op.Arg.Split('|');
            var style = styleParts[0];
            char ridge = styleParts.Length > 1 && styleParts[1].Length == 1 ? styleParts[1][0] : op.Symbol;
            bool stairs = bp.Palette.TryGetValue(op.Symbol, out var pe) && BuildBlocks.KindOf(pe.Id) == BuildKind.Stairs;
            if (!In(lo.X, lo.Y, lo.Z) || !In(hi.X, lo.Y, hi.Z)) { Err("bounds", $"roof {lo.X},{lo.Y},{lo.Z}..{hi.X},{hi.Z} is outside the size", op.Line); return; }
            if (style == "flat") Fill(lo, (hi.X, lo.Y, hi.Z), (_, _, _) => op.Symbol, op.Line);
            else if (style == "slope") SlopeRoof(op, lo, hi, stairs);
            else GableRoof(op, lo, hi, ridge, stairs);
        }

        /// <summary>A slope rising one layer per row toward the op's facing.</summary>
        private void SlopeRoof(BlueprintOp op, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi, bool stairs)
        {
            var d = op.Facing!.Value;
            var (dx, dz) = d.Offset();
            int steps = dx != 0 ? hi.X - lo.X + 1 : hi.Z - lo.Z + 1;
            for (int k = 0; k < steps; k++)
            {
                int y = lo.Y + k;
                if (y >= H) { Err("bounds", $"slope roof needs {steps} layers from y{lo.Y}: raise the size", op.Line); return; }
                for (int z = lo.Z; z <= hi.Z; z++)
                    for (int x = lo.X; x <= hi.X; x++)
                    {
                        if (SlopeStep(dx, dz, x, z, lo, hi) != k) continue;
                        Set(x, y, z, op.Symbol, op.Line);
                        if (stairs) facing[(x, y, z)] = d;
                    }
            }
        }

        /// <summary>How many rows a cell lies from the slope's low edge.</summary>
        private static int SlopeStep(int dx, int dz, int x, int z, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi) =>
            dx > 0 ? x - lo.X : dx < 0 ? hi.X - x : dz > 0 ? z - lo.Z : hi.Z - z;

        /// <summary>A gable: ridge along the longer side; each layer up steps one row in from both long edges.</summary>
        private void GableRoof(BlueprintOp op, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi, char ridge, bool stairs)
        {
            bool alongX = hi.X - lo.X >= hi.Z - lo.Z;
            int span = alongX ? hi.Z - lo.Z + 1 : hi.X - lo.X + 1;
            var (across0, across1) = alongX ? (lo.Z, hi.Z) : (lo.X, hi.X);
            var (run0, run1) = alongX ? (lo.X, hi.X) : (lo.Z, hi.Z);
            for (int k = 0; k < (span + 1) / 2; k++)
            {
                int y = lo.Y + k;
                if (y >= H) { Err("bounds", $"gable roof over {span} rows needs {(span + 1) / 2} layers from y{lo.Y}: raise the size", op.Line); return; }
                int a = across0 + k, b = across1 - k;
                if (a == b) { GableRow(op.Line, alongX, y, a, run0, run1, ridge, null); continue; }
                GableRow(op.Line, alongX, y, a, run0, run1, op.Symbol, GableFacing(stairs, alongX, nearSide: true));
                GableRow(op.Line, alongX, y, b, run0, run1, op.Symbol, GableFacing(stairs, alongX, nearSide: false));
            }
        }

        /// <summary>A gable row's stairs face away from the ridge: the near (low-coordinate) row south/east, the far row north/west.</summary>
        private static Dir? GableFacing(bool stairs, bool alongX, bool nearSide) =>
            !stairs ? null : alongX ? (nearSide ? Dir.South : Dir.North) : (nearSide ? Dir.East : Dir.West);

        private void GableRow(int line, bool alongX, int y, int row, int run0, int run1, char sym, Dir? stepFacing)
        {
            for (int s = run0; s <= run1; s++)
            {
                var (x, z) = alongX ? (s, row) : (row, s);
                Set(x, y, z, sym, line);
                if (stepFacing is { } f) facing[(x, y, z)] = f;
            }
        }
    }
}
