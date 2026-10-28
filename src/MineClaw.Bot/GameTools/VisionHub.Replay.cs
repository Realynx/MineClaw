using System.Text.Json;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Render;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Renders what a <c>see</c> tool input would show, without asking any model, spending the image budget or touching the
/// history: the supervisor's compose route ("render exact" in the Explorer) and the tests. Same focus resolution, view parsing
/// (camera adjustments included), classic vs composite choice, scenes and reading aids as <see cref="SeeAsync"/>.
/// </summary>
public sealed partial class VisionHub
{
    /// <summary>The picture of a see input (<paramref name="sheets"/> = the renderer to use, e.g. a CPU-capped one); an error text otherwise.</summary>
    /// <param name="draw">Runs the CPU part (the supervisor's render gate); null = inline. Focus, poses and remote panels resolve before it.</param>
    public async Task<(VisionImage? Image, string? Error)> RenderSeeAsync(JsonElement i, ContactSheet? sheets, CancellationToken ct,
        Func<Func<VisionImage>, Task<VisionImage>>? draw = null)
    {
        var s = sheets ?? Sheets;
        draw ??= f => Task.FromResult(f());
        var target = ResolveTarget(i.Str("focus"));
        if (target.Error != null) return (null, target.Error);
        if (target.HiveView)
        {
            var hive = await draw(() =>
            {
                var h = HiveOverview(image: true);
                return h.Image is { } hi ? new VisionImage(hi.Data, hi.Width, hi.Height, 0, h.Text, []) : new VisionImage([], 0, 0, 0, h.Text, []);
            }).ConfigureAwait(false);
            return hive.Png.Length > 0 ? (hive, null) : (null, "no hive picture");
        }
        int radius = Math.Clamp(i.Int("radius") ?? _o.SheetRadius, 3, 12);
        bool aids = i.Bool("aids") ?? true;
        var reqs = ParseViews(i, out var viewError);
        if (viewError != null) return (null, viewError);
        var focus = target.Focus;
        if (reqs != null && (reqs.Any(r => r.Composite || r.Kind == "eye" && r.At != null || r.Kind == "orbit" && r.N > 1)
            || reqs.Any(r => !r.Cam.IsEmpty) && reqs.GroupBy(r => r.Kind).Any(g => g.Count() > 1)))
        {
            var (jobs, notes, error) = await PanelsAsync(reqs, focus, target.Dim, radius, aids, ct).ConfigureAwait(false);
            if (error != null) return (null, error);
            if (jobs.Count == 0) return (null, "no panels to render");
            return (await draw(() => Composite(jobs, focus, notes, s)).ConfigureAwait(false), null);
        }
        var views = reqs?.Select(r => r.Kind).ToList() ?? [];
        var bad = views.Where(x => !ContactSheet.Known.Contains(x.Trim())).ToList();
        if (bad.Count > 0) return (null, $"unknown view '{bad[0]}'");
        IReadOnlyDictionary<string, CamParams>? viewParams = reqs?.Where(r => !r.Cam.IsEmpty).GroupBy(r => r.Kind)
            .ToDictionary(g => g.Key, g => g.First().Cam, StringComparer.OrdinalIgnoreCase);
        if (viewParams is { Count: 0 }) viewParams = null;
        int dim = target.Dim;
        HiveEyePose? pose = null;
        if (target.Bot is { } cb)
        {
            try { pose = Hive == null ? null : await Hive.ViewAsync(cb.Name, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log?.LogDebug(ex, "render see: view request failed"); }
            if (pose != null)
            {
                focus = new BlockPos((int)Math.Floor(pose.X), (int)Math.Floor(pose.Y), (int)Math.Floor(pose.Z));
                dim = pose.Dim;
            }
        }
        var player = target.Player;
        return (await draw(() => s.Render(SheetRequest(focus, radius, views, player, SceneFor(focus, dim, radius), pose, aids, viewParams))).ConfigureAwait(false), null);
    }
}
