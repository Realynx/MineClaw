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
        if (target.HiveView) return await HivePictureAsync(draw).ConfigureAwait(false);
        int radius = Math.Clamp(i.Int("radius") ?? _o.SheetRadius, 3, 12);
        bool aids = i.Bool("aids") ?? true;
        var reqs = ParseViews(i, out var viewError);
        if (viewError != null) return (null, viewError);
        if (WantsComposite(reqs)) return await CompositeSeeAsync(reqs!, target.Focus, target.Dim, radius, aids, s, draw, ct).ConfigureAwait(false);
        return await SheetSeeAsync(target, reqs, radius, aids, s, draw, ct).ConfigureAwait(false);
    }

    private async Task<(VisionImage? Image, string? Error)> HivePictureAsync(Func<Func<VisionImage>, Task<VisionImage>> draw)
    {
        var hive = await draw(() =>
        {
            var h = HiveOverview(image: true);
            return h.Image is { } hi ? new VisionImage(hi.Data, hi.Width, hi.Height, 0, h.Text, []) : new VisionImage([], 0, 0, 0, h.Text, []);
        }).ConfigureAwait(false);
        return hive.Png.Length > 0 ? (hive, null) : (null, "no hive picture");
    }

    private async Task<(VisionImage? Image, string? Error)> CompositeSeeAsync(List<PanelRequest> reqs, BlockPos focus, int dim, int radius, bool aids,
        ContactSheet s, Func<Func<VisionImage>, Task<VisionImage>> draw, CancellationToken ct)
    {
        var (jobs, notes, error) = await PanelsAsync(reqs, focus, dim, radius, aids, ct).ConfigureAwait(false);
        if (error != null) return (null, error);
        if (jobs.Count == 0) return (null, "no panels to render");
        return (await draw(() => Composite(jobs, focus, notes, s)).ConfigureAwait(false), null);
    }

    /// <summary>The plain contact sheet (known view kinds only), from a clone's live pose when the focus is one.</summary>
    private async Task<(VisionImage? Image, string? Error)> SheetSeeAsync(VisionTarget target, List<PanelRequest>? reqs, int radius, bool aids,
        ContactSheet s, Func<Func<VisionImage>, Task<VisionImage>> draw, CancellationToken ct)
    {
        var focus = target.Focus;
        var views = reqs?.Select(r => r.Kind).ToList() ?? [];
        var bad = views.Where(x => !ContactSheet.Known.Contains(x.Trim())).ToList();
        if (bad.Count > 0) return (null, $"unknown view '{bad[0]}'");
        var viewParams = CamParamsOf(reqs);
        int dim = target.Dim;
        var pose = target.Bot is { } cb ? await ClonePoseAsync(cb, "render see: view request failed", ct).ConfigureAwait(false) : null;
        if (pose != null) (focus, dim) = (PoseBlock(pose), pose.Dim);
        var player = target.Player;
        return (await draw(() => s.Render(SheetRequest(focus, radius, views, player, SceneFor(focus, dim, radius), pose, aids, viewParams))).ConfigureAwait(false), null);
    }
}
