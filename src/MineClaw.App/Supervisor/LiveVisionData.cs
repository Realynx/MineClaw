using System.Text.Json;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Hive.Supervisor;
using MineClaw.Render;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Supervisor;

/// <summary>
/// The vision routes' data in the main process: main's (and in-process bodies') captured sheets straight from the hub's
/// <see cref="VisionHub.History"/>, hive clones' over the <c>vision</c>/<c>vision_png</c> IPC requests (1.5 s timeout), and
/// on-demand renders of a see input with main's hub (a clone's spot from its live pose and the shared world memory) on this
/// class's own single-threaded renderer behind the supervisor's render gate. Reads only.
/// </summary>
public sealed class LiveVisionData(VisionHub hub, BotHost? bot, string main, Func<HiveSupervisor?> hive, ILogger? log = null) : IVisionSupervisorData
{
    private readonly ContactSheet _sheet = new(new VisionRenderer { CacheCapacity = 8 }) { EyeThreads = 1 };

    public TimeSpan CloneTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

    private bool IsMain(string body) => body.Equals("main", StringComparison.OrdinalIgnoreCase) || body.Equals(main, StringComparison.OrdinalIgnoreCase);

    /// <summary>The body's name in this process (main / an in-process body), else null.</summary>
    private string? Local(string body)
    {
        if (IsMain(body)) return main;
        return bot?.Bodies?.Find(body) is { } b ? b.Name : null;
    }

    private string? Clone(string body) => hive() is { } sup && sup.Resolve(body) is { } c ? c : null;

    public async Task<VisionListReply?> ListAsync(string body, long since, CancellationToken ct)
    {
        if (Local(body) is { } name)
        {
            var list = hub.History.List(name, since);
            // In-process bodies share main's brain and hub: when nothing is tagged with the body, say so instead of showing nothing.
            return new VisionListReply(name, hub.History.Capacity, list, list.Count == 0 && !IsMain(body) ? "no sheet recorded for this body yet" : null);
        }
        if (Clone(body) is { } clone && hive() is { } sup)
        {
            var slot = sup.Slots.FirstOrDefault(s => s.Name.Equals(clone, StringComparison.OrdinalIgnoreCase));
            if (slot is not { Connected: true }) return new VisionListReply(clone, 0, [], "clone offline");
            Hive.Ipc.VisionListMsg? msg = null;
            try { msg = await sup.RequestVisionListAsync(clone, since, CloneTimeout, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log?.LogDebug(ex, "supervisor vision: clone {Clone} list failed", clone); }
            return msg == null ? new VisionListReply(clone, 0, [], "the clone didn't answer (or has vision off)") : new VisionListReply(clone, msg.Capacity, msg.Sheets);
        }
        return null;
    }

    public async Task<byte[]?> PngAsync(string body, long id, CancellationToken ct)
    {
        if (Local(body) != null) return hub.History.Png(id);
        if (Clone(body) is { } clone && hive() is { } sup)
        {
            try { return (await sup.RequestVisionPngAsync(clone, id, CloneTimeout, ct).ConfigureAwait(false))?.Png; }
            catch (Exception ex) when (ex is not OperationCanceledException) { log?.LogDebug(ex, "supervisor vision: clone {Clone} png failed", clone); }
        }
        return null;
    }

    public async Task<ViewImage?> RenderAsync(string body, string seeJson, RenderGate gate, CancellationToken ct)
    {
        string? focusDefault;
        if (Local(body) is { } name) focusDefault = IsMain(body) ? null : name;
        else if (Clone(body) is { } clone) focusDefault = clone; // main renders the clone's spot from its live pose + the shared memory
        else return null;
        using var doc = JsonDocument.Parse(seeJson);
        var input = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : JsonDocument.Parse("{}").RootElement;
        if (focusDefault != null && !input.TryGetProperty("focus", out _))
        {
            var o = System.Text.Json.Nodes.JsonNode.Parse(input.GetRawText())!.AsObject();
            o["focus"] = focusDefault;
            input = JsonDocument.Parse(o.ToJsonString()).RootElement;
        }
        var el = input.Clone();
        // Resolve on the caller's thread (clone poses go over IPC), draw behind the gate (one capped render thread).
        var (img, error) = await hub.RenderSeeAsync(el, _sheet, ct, f => gate.RunAsync(f, ct)).ConfigureAwait(false);
        if (img == null) return new ViewImage([], 0, 0, 0, "", [], Note: error ?? "nothing rendered");
        return new ViewImage(img.Png, img.Width, img.Height, img.Tokens, img.Legend, [], Note: focusDefault != null && Local(body) == null ? "rendered by main from the shared world memory" : null);
    }
}
