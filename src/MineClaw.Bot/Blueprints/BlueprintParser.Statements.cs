using System.Globalization;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>The header and layer statements of the DSL (<see cref="BlueprintParser.Parse"/>): one handler per statement head.</summary>
public static partial class BlueprintParser
{
    private delegate void StatementHandler(ParseRun run, string rest, int line);

    private static readonly Dictionary<string, StatementHandler> StatementHandlers = new()
    {
        ["blueprint"] = (r, rest, _) => r.Name(rest),
        ["name"] = (r, rest, _) => r.Name(rest),
        ["size"] = (r, rest, ln) => r.Size(rest, ln),
        ["origin"] = (r, rest, ln) => r.Origin(rest, ln),
        ["ground"] = (r, rest, ln) => r.Ground(rest, ln),
        ["facing"] = (r, rest, ln) => r.Facing(rest, ln),
        ["front"] = (r, rest, ln) => r.Facing(rest, ln),
        ["light"] = (r, _, _) => r.Light(),
        ["lit"] = (r, _, _) => r.Light(),
        ["version"] = (r, rest, ln) => r.Version(rest, ln),
        ["kind"] = (r, rest, ln) => r.Kind(rest, ln),
        ["anchor"] = (r, rest, ln) => r.Anchor(rest, ln),
        ["entry"] = (r, rest, ln) => r.Entry(rest, ln),
        ["seed"] = (r, rest, _) => r.Seed(rest),
        ["preferred_for"] = (r, rest, ln) => r.PreferredFor(rest, ln),
        ["preferred"] = (r, rest, ln) => r.PreferredFor(rest, ln),
        ["palette"] = (r, rest, ln) => r.Palette(rest, ln),
        ["layer"] = (r, rest, ln) => r.Layer(rest, ln),
        ["layers"] = (r, rest, ln) => r.Layer(rest, ln),
    };

    private static bool IsSizeLine(string raw) => SizeHeadRx().IsMatch(raw) || SizeRx().IsMatch(raw.Replace(" ", ""));

    private static string SizeOutOfRange(Blueprint bp, int heightLimit) =>
        $"size {bp.W}x{bp.H}x{bp.D} out of range (1..{MaxSide} wide/deep, 1..{heightLimit} high)";

    /// <summary>One parse: the line cursor, the blueprint being filled and its issues.</summary>
    private sealed class ParseRun(Blueprint bp, string[] lines)
    {
        public List<BlueprintIssue> Issues { get; } = new();
        private int next;
        private bool sized, sizeTried;
        private int sizeLine;

        private void Err(int line, string code, string msg) => Issues.Add(new BlueprintIssue(IssueLevel.Error, code, msg, line));

        public void ReadAll()
        {
            while (next < lines.Length) ReadStatement();
        }

        private void ReadStatement()
        {
            int ln = next + 1;
            var raw = StripComment(lines[next]).Trim();
            next++;
            if (raw.Length == 0 || raw.StartsWith("```", StringComparison.Ordinal)) return;
            if (!sized && IsSizeLine(raw)) { FirstSize(raw, ln); return; }
            var sp = raw.IndexOf(' ');
            var head = (sp < 0 ? raw : raw[..sp]).ToLowerInvariant().TrimEnd(':');
            var rest = sp < 0 ? "" : raw[(sp + 1)..].Trim();
            if (StatementHandlers.TryGetValue(head, out var handle)) handle(this, rest, ln);
            else AddOp(head, rest, ln);
        }

        /// <summary>The first size line, in any of its spellings (<see cref="SizeOf"/>).</summary>
        private void FirstSize(string raw, int ln)
        {
            if (SizeOf(raw) is not { } sz) { Err(ln, "size", "size must be WxHxD with numbers, e.g. size 5x4x5"); sizeTried = true; return; }
            SetSize(sz, ln);
        }

        /// <summary>A later "size" line: the strict WxHxD form only.</summary>
        public void Size(string rest, int ln)
        {
            var m = SizeRx().Match(rest.Replace(" ", ""));
            if (!m.Success) { Err(ln, "size", "size must be WxHxD, e.g. 5x4x5"); return; }
            SetSize((I(m.Groups[1].Value), I(m.Groups[2].Value), I(m.Groups[3].Value)), ln);
        }

        private void SetSize((int W, int H, int D) sz, int ln)
        {
            (bp.W, bp.H, bp.D) = sz;
            if (bp.W is < 1 or > MaxSide || bp.D is < 1 or > MaxSide || bp.H is < 1 or > MaxMineHeight)
                Err(ln, "size", SizeOutOfRange(bp, MaxHeight));
            sized = true;
            sizeLine = ln;
        }

        public void Name(string rest)
        {
            var name = rest.Trim('"', '\'', ' ');
            if (name.Length > 0) bp.Name = Truncate(name, 48);
        }

        public void Origin(string rest, int ln)
        {
            if (P3(rest) is { } o) bp.Origin = new BlockPos(o.X, o.Y, o.Z);
            else Err(ln, "origin", "origin must be x,y,z");
        }

        public void Ground(string rest, int ln)
        {
            if (int.TryParse(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) && g >= 0 && g < MaxHeight)
                bp.Ground = g;
            else Err(ln, "ground", $"ground must be the number of layers below the surface (0..{MaxHeight - 1})");
        }

        public void Facing(string rest, int ln)
        {
            if (Dirs.Parse(rest) is { } f) bp.Facing = f;
            else Err(ln, "facing", "facing must be north, east, south or west");
        }

        public void Light() => bp.Light = true;

        public void Version(string rest, int ln)
        {
            if (DesignVersions.Normalize(rest) == null) Err(ln, "version", $"version must be {string.Join(" or ", DesignVersions.All)}");
        }

        public void Kind(string rest, int ln)
        {
            if (rest.Trim().ToLowerInvariant() is var kind && kind is "mine" or "build") bp.Kind = kind;
            else Err(ln, "kind", "kind must be build or mine");
        }

        /// <summary>anchor absolute X,Y,Z (or anchor absolute Y): a mine's layer 0 at world y Y (X,Z: its place on the studio plot).</summary>
        public void Anchor(string rest, int ln)
        {
            if (!TryAnchor(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                Err(ln, "anchor", "anchor must be 'absolute X,Y,Z' (a mine at world y Y) or 'surface'");
            if (bp.AbsoluteY is < 1) Err(ln, "anchor", $"anchor absolute y {bp.AbsoluteY} must be 1 or more (y 0 is bedrock)");
        }

        private bool TryAnchor(string[] t)
        {
            bool abs = t.Length == 2 && t[0].Equals("absolute", StringComparison.OrdinalIgnoreCase);
            if (abs && P3(t[1]) is { } at)
            {
                bp.AbsoluteY = at.Y;
                bp.PlotAt = (at.X, at.Z);
                return true;
            }
            if (abs && int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ay)) { bp.AbsoluteY = ay; return true; }
            if (t.Length >= 1 && t[0].ToLowerInvariant() is "surface" or "ground") { bp.AbsoluteY = null; return true; }
            return false;
        }

        public void Entry(string rest, int ln)
        {
            if (P3(rest) is { } en) bp.Entry = en;
            else Err(ln, "entry", "entry must be x,y,z (the mine's top, in the design's own coordinates)");
        }

        public void Seed(string rest) => bp.Seed = rest.Trim().Length is > 0 and <= 32 ? rest.Trim() : null;

        public void PreferredFor(string rest, int ln)
        {
            if (DesignVersions.Normalize(rest) is { } pv) bp.PreferredFor = pv;
            else Err(ln, "preferred_for", $"preferred_for must be {string.Join(" or ", DesignVersions.All)}");
        }

        public void Palette(string rest, int ln)
        {
            foreach (var tok in rest.Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries)) ParsePalette(bp, tok, ln, Issues);
        }

        /// <summary>"layer 0" or "layer 1-3", then its rows up to the next statement (or the blank line after them).</summary>
        public void Layer(string rest, int ln)
        {
            var m = LayerRx().Match(rest);
            if (!m.Success) { Err(ln, "layer", "layer needs a number (layer 0) or a range (layer 1-3)"); return; }
            int a = I(m.Groups[1].Value);
            int b = m.Groups[2].Success ? I(m.Groups[2].Value) : a;
            var rows = ReadRows();
            for (int y = Math.Min(a, b); y <= Math.Max(a, b); y++)
            {
                if (bp.Layers.ContainsKey(y)) Err(ln, "layer", $"layer {y} written twice");
                bp.Layers[y] = (rows, ln);
                bp.Order.Add(("layer", y));
            }
        }

        private List<string> ReadRows()
        {
            var rows = new List<string>();
            while (next < lines.Length)
            {
                var r = StripComment(lines[next]).Trim();
                if (r.Length == 0) { next++; if (rows.Count > 0) break; continue; }
                if (IsStatement(r)) break;
                rows.Add(r);
                next++;
            }
            return rows;
        }

        private void AddOp(string head, string rest, int ln)
        {
            if (ParseOp(head, rest, ln, Issues) is not { } op) return;
            bp.Ops.Add(op);
            bp.Order.Add(("op", bp.Ops.Count - 1));
        }

        /// <summary>The whole-design checks after the last line, then the drawn front (<see cref="BlueprintNormalizer"/>).</summary>
        public void Finish()
        {
            if (!sized && !sizeTried) InferSize();
            CheckHeights();
            // Settle the drawn front now (doors drawn on the facing side = not turned) so the footprint is right before normalizing.
            if (bp.Facing != Dir.South && Normalizable()) BlueprintNormalizer.Normalize(bp);
        }

        /// <summary>No size line: infer it from the drawn layers (widest row, row count, highest layer) when there are any.</summary>
        private void InferSize()
        {
            if (bp.Layers.Count == 0 || !bp.Layers.Values.Any(l => l.Rows.Count > 0)) { Err(0, "size", "missing 'size WxHxD'"); return; }
            bp.W = bp.Layers.Values.SelectMany(l => l.Rows).Max(r => r.Length);
            bp.D = bp.Layers.Values.Max(l => l.Rows.Count);
            bp.H = bp.Layers.Keys.Max() + 1;
            Issues.Add(new BlueprintIssue(IssueLevel.Warning, "size-inferred", $"no size line: inferred {bp.W}x{bp.H}x{bp.D} from the layers"));
            if (bp.W > MaxSide || bp.D > MaxSide || bp.H > HeightLimit(bp)) Err(0, "size", SizeOutOfRange(bp, HeightLimit(bp)));
        }

        private void CheckHeights()
        {
            if (bp.Ground > 0 && bp.H > 0 && bp.Ground > bp.H) Err(0, "ground", $"ground {bp.Ground} is deeper than the design (size is {bp.H} high)");
            // Only a mine reaches past 32 high (the kind line may come after the size line).
            if (sized && !bp.Mine && bp.H > MaxHeight && bp.W <= MaxSide && bp.D <= MaxSide) Err(sizeLine, "size", SizeOutOfRange(bp, MaxHeight));
        }

        private bool Normalizable() => bp.W > 0 && bp.H > 0 && bp.D > 0 && bp.W <= MaxSide && bp.D <= MaxSide && bp.H <= HeightLimit(bp);
    }
}
