using System.Globalization;
using System.Net;
using System.Text;
using MineClaw.Bot.Blueprints;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>
/// The supervisor's plan view (<c>/view/buildplan/{id}</c>): a self-contained HTML page (inline SVG, no scripts) with an
/// oblique render of every step's cell numbered in build order, coloured by technique, scaffolding outlined dashed, water
/// columns drawn, finished steps solid and pending ones faded, a progress bar, the gate state and the step list.
/// </summary>
public static class BuildPlanView
{
    private static readonly Dictionary<Technique, string> Colours = new()
    {
        [Technique.Normal] = "#4c8bf5", [Technique.Edge] = "#f5a623", [Technique.Hidden] = "#9b59b6", [Technique.Water] = "#1abc9c",
        [Technique.Pillar] = "#e74c3c", [Technique.Stair] = "#8e6e53",
    };

    public static string Html(IReadOnlyList<BuildPlan> parts, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">")
          .Append("<title>Build plan</title><style>")
          .Append(":root{--bg:#fff;--fg:#222;--mut:#888;--bar:#e6e6e6}@media(prefers-color-scheme:dark){:root{--bg:#16181c;--fg:#ddd;--mut:#888;--bar:#333}}")
          .Append("body{background:var(--bg);color:var(--fg);font:14px system-ui,sans-serif;margin:16px}.bar{background:var(--bar);height:12px;border-radius:6px;overflow:hidden}")
          .Append(".bar>div{background:#2ecc71;height:100%}table{border-collapse:collapse;font-size:12px}td,th{padding:2px 6px;border-bottom:1px solid var(--bar);text-align:left}")
          .Append(".mut{color:var(--mut)}.key span{display:inline-block;width:10px;height:10px;margin:0 4px 0 12px;border-radius:2px}svg{max-width:100%;height:auto}</style></head><body>");
        sb.Append("<h2>").Append(Enc(title)).Append("</h2>");
        foreach (var bp in parts) Part(sb, bp, parts.Count > 1);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>One part: heading, summary, progress, gate line, key, picture, what can't be placed and the step list.</summary>
    private static void Part(StringBuilder sb, BuildPlan bp, bool several)
    {
        int done = bp.Done, total = bp.Steps.Count;
        sb.Append("<h3>").Append(several ? $"Part {bp.Part + 1}/{bp.Parts}" : "Plan").Append(" <span class=mut>").Append(Enc(bp.State)).Append("</span></h3>");
        sb.Append("<p>").Append(Enc(bp.Summary())).Append("</p>");
        sb.Append("<div class=bar><div style=\"width:").Append(Pct(total == 0 ? 1 : (double)done / total)).Append("%\"></div></div>");
        sb.Append("<p class=mut>").Append(done).Append('/').Append(total).Append(" steps done");
        GateLine(sb, bp);
        sb.Append("</p><p class=key>");
        foreach (var (t, c) in Colours) sb.Append("<span style=\"background:").Append(c).Append("\"></span>").Append(t.ToString().ToLowerInvariant());
        sb.Append("<span style=\"border:1px dashed #888\"></span>scaffold<span style=\"background:#3498db55\"></span>water column</p>");
        sb.Append(Svg(bp));
        if (bp.Infeasible.Count > 0)
        {
            sb.Append("<p><b>Can't place:</b> ");
            sb.Append(string.Join("; ", bp.Infeasible.Take(12).Select(i => Enc($"{BuildBlocks.Name(i.Id, 0)} at {i.Pos.X},{i.Pos.Y},{i.Pos.Z}: {i.Why}"))));
            sb.Append("</p>");
        }
        sb.Append("<details><summary>Steps</summary><table><tr><th>#</th><th>action</th><th>block</th><th>at</th><th>technique</th><th>stand</th><th>status</th></tr>");
        foreach (var s in bp.Steps.Take(400)) StepRow(sb, s);
        sb.Append("</table></details>");
    }

    /// <summary>" · gate: waiting — asked: “…” · turned … · note".</summary>
    private static void GateLine(StringBuilder sb, BuildPlan bp)
    {
        sb.Append(" · gate: ").Append(Enc(bp.Gate.State.ToString().ToLowerInvariant()));
        if (bp.Gate.Question != null && bp.Gate.State == GateState.Waiting) sb.Append(" — asked: “").Append(Enc(bp.Gate.Question)).Append('”');
        if (!bp.Transform.IsNone) sb.Append(" · ").Append(Enc(bp.Transform.Text));
        if (bp.Note != null) sb.Append(" · ").Append(Enc(bp.Note));
    }

    private static void StepRow(StringBuilder sb, PlanStep s) =>
        sb.Append("<tr><td>").Append(s.Index).Append("</td><td>").Append(s.Action).Append("</td><td>").Append(Enc(BuildBlocks.Name(s.Id, s.Meta)))
          .Append("</td><td>").Append(P(s.Target)).Append("</td><td>").Append(s.Technique).Append(s.HiddenFace ? " (hidden face)" : "")
          .Append("</td><td>").Append(P(s.Stand)).Append(s.Column >= 0 ? " (in water)" : "").Append("</td><td>").Append(s.Status)
          .Append(s.Note != null ? " <span class=mut>" + Enc(s.Note) + "</span>" : "").Append("</td></tr>");

    /// <summary>Oblique projection: x right, y up, z receding up-right; painter's order (far, low first).</summary>
    private static string Svg(BuildPlan bp)
    {
        var cells = bp.Steps.Where(s => s.Action is StepAction.Place or StepAction.Scaffold).ToList();
        if (cells.Count == 0) return "<p class=mut>no placements</p>";
        var f = Frame.Of(cells, bp.Columns);
        var sb = new StringBuilder();
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {f.W:0} {f.H:0}\" width=\"{f.W:0}\" height=\"{f.H:0}\">"));
        foreach (var c in bp.Columns)
            for (int y = c.Depth; y <= c.Source.Y; y++)
            {
                var p = new BlockPos(c.Source.X, y, c.Source.Z);
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"<rect x=\"{f.Sx(p):0.#}\" y=\"{f.Sy(p):0.#}\" width=\"{U - 2}\" height=\"{U - 2}\" fill=\"#3498db\" fill-opacity=\"{(c.Removed ? 0.12 : 0.35)}\"/>"));
            }
        foreach (var s in cells.OrderByDescending(s => s.Target.Z).ThenBy(s => s.Target.Y).ThenBy(s => s.Target.X)) Cell(sb, s, f, cells.Count <= 600);
        sb.Append("</svg>");
        return sb.ToString();
    }

    private const double U = 22, K = 0.5;

    /// <summary>The drawing's bounds (cells and water columns) and the oblique projection into it.</summary>
    private readonly record struct Frame(int MinX, int MaxX, int MinY, int MaxY, int MinZ, int MaxZ)
    {
        public static Frame Of(List<PlanStep> cells, List<PlanColumn> columns)
        {
            int minX = cells.Min(s => s.Target.X), maxX = cells.Max(s => s.Target.X), minY = cells.Min(s => s.Target.Y), maxY = cells.Max(s => s.Target.Y);
            int minZ = cells.Min(s => s.Target.Z), maxZ = cells.Max(s => s.Target.Z);
            foreach (var c in columns) { minY = Math.Min(minY, c.Depth); maxY = Math.Max(maxY, c.Source.Y); minX = Math.Min(minX, c.Source.X); maxX = Math.Max(maxX, c.Source.X); minZ = Math.Min(minZ, c.Source.Z); maxZ = Math.Max(maxZ, c.Source.Z); }
            return new Frame(minX, maxX, minY, maxY, minZ, maxZ);
        }

        public double Sx(BlockPos p) => (p.X - MinX) * U + (MaxZ - p.Z) * U * K + 10;
        public double Sy(BlockPos p) => (MaxY - p.Y) * U + (p.Z - MinZ) * U * K * 0.6 + 10;
        public double W => (MaxX - MinX + 1) * U + (MaxZ - MinZ + 1) * U * K + 30;
        public double H => (MaxY - MinY + 1) * U + (MaxZ - MinZ + 1) * U * K * 0.6 + 30;
    }

    /// <summary>One step's cell: coloured by technique (scaffold dashed), solid when finished, a thick edge on gate blocks, numbered.</summary>
    private static void Cell(StringBuilder sb, PlanStep s, Frame f, bool numbered)
    {
        var col = Colours[s.Technique];
        bool scaffold = s.Action == StepAction.Scaffold;
        double op = s.Finished ? 0.95 : 0.35;
        sb.Append(string.Create(CultureInfo.InvariantCulture,
            $"<g><title>#{s.Index} {s.Action} {Enc(BuildBlocks.Name(s.Id, s.Meta))} {P(s.Target)} {s.Technique} {s.Status}</title><rect x=\"{f.Sx(s.Target):0.#}\" y=\"{f.Sy(s.Target):0.#}\" width=\"{U - 2}\" height=\"{U - 2}\" rx=\"2\" fill=\"{(scaffold ? "none" : col)}\" fill-opacity=\"{op}\" stroke=\"{(scaffold ? "#888" : col)}\" stroke-dasharray=\"{(scaffold ? "3,2" : "0")}\" stroke-width=\"{(s.Gate ? 2.5 : 1)}\"/>"));
        if (numbered)
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"<text x=\"{f.Sx(s.Target) + (U - 2) / 2:0.#}\" y=\"{f.Sy(s.Target) + U / 2 + 3:0.#}\" font-size=\"8\" text-anchor=\"middle\" fill=\"{(scaffold ? "#888" : "#fff")}\">{s.Index}</text>"));
        sb.Append("</g>");
    }

    private static string P(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
    private static string Pct(double f) => (Math.Clamp(f, 0, 1) * 100).ToString("0.#", CultureInfo.InvariantCulture);
    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
