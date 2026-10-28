using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineClaw.Render;

/// <summary>One panel of a <c>see</c> call: a view name, an optional spot/body, and camera adjustments.</summary>
public sealed record SeeView(string Kind, CamParams Params = default, string? At = null)
{
    /// <summary>The view as the <c>see</c> tool's string form: "iso yaw=200 pitch=50", "orbit gen 3", "top zoom=2 dx=3".</summary>
    public string Format() => string.Join(' ', new[] { Kind, At, Params.IsEmpty ? null : Params.Format() }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public override string ToString() => Format();
}

/// <summary>
/// The <c>see</c> tool call that renders a given sheet ("Copy as see{...}" in the Explorer's compose mode, debugging notes).
/// The bot's <c>see</c> parser (VisionHub.ParseViews) reads exactly this form back.
/// </summary>
public static class SeeCall
{
    /// <summary><c>see {"views":[...],"focus":"x,y,z","radius":6}</c> (keys only when set; aids only when off).</summary>
    public static string Format(IEnumerable<SeeView> views, string? focus = null, int? radius = null, bool aids = true, string? question = null) =>
        "see " + Json(views, focus, radius, aids, question);

    /// <summary>The tool input JSON alone (what the brain's tool_use carries).</summary>
    public static string Json(IEnumerable<SeeView> views, string? focus = null, int? radius = null, bool aids = true, string? question = null)
    {
        var o = new JsonObject();
        var arr = new JsonArray();
        foreach (var v in views) arr.Add(v.Format());
        if (arr.Count > 0) o["views"] = arr;
        if (!string.IsNullOrWhiteSpace(focus)) o["focus"] = focus;
        if (radius is { } r) o["radius"] = r;
        if (!aids) o["aids"] = false;
        if (!string.IsNullOrWhiteSpace(question)) o["question"] = question;
        return o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static string Coords(int x, int y, int z) => string.Create(CultureInfo.InvariantCulture, $"{x},{y},{z}");
}
