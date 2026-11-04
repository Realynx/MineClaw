using System.Text;
using MineClaw.ViewerBridge.Security;

namespace MineClaw.ViewerBridge.Supervisor;

/// <summary>
/// <c>/v1/sup/designer/*</c> → the bot's Design Studio routes (<c>/api/designer/*</c>). Reads pass with any signed role; writes
/// (every POST) need <c>design</c> or <c>admin</c> in <c>X-MCX-Roles</c>, and the bridge writes who is designing into the body
/// (<c>player</c> = the linked Minecraft player, <c>who</c> = "&lt;user&gt; (web)"), refusing a body that tries to set them. A
/// message runs a model tool loop, so it gets a long timeout.
/// </summary>
public static class DesignerProxy
{
    public const string Prefix = "designer/";

    public static bool CanWrite(BridgeIdentity id) => id.IsAdmin || id.Has("design");

    public static TimeSpan TimeoutFor(string path) =>
        path.EndsWith("/messages", StringComparison.Ordinal) ? TimeSpan.FromSeconds(240) : TimeSpan.FromSeconds(20);

    public static async Task HandleAsync(HttpContext ctx, SupervisorClient sup)
    {
        var id = ctx.Identity();
        var split = SupervisorProxy.Split(BridgeAuthMiddleware.RawTarget(ctx));
        if (split is not { } s || !s.Path.StartsWith(Prefix, StringComparison.Ordinal)) { await ApiErrors.Write(ctx, 404, "not_found", "not found"); return; }
        var method = ctx.Request.Method;
        bool post = HttpMethods.IsPost(method);
        if (!post && !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)) { await ApiErrors.Write(ctx, 404, "not_found", "not found"); return; }
        HttpContent? content = null;
        if (post)
        {
            var (body, bad) = WriteBody(ctx, id);
            if (bad is { } b) { await ApiErrors.Write(ctx, b.Status, b.Code, b.Message); return; }
            content = body;
        }
        HttpResponseMessage res;
        try
        {
            res = await sup.SendAsync(SupervisorProxy.MethodOf(method),
                SupervisorProxy.Upstream(s.Path, s.Query), content, TimeoutFor(s.Path), false, ctx.RequestAborted);
        }
        catch (SupervisorUnavailableException ex) { await ApiErrors.Write(ctx, 503, "upstream_unavailable", ex.Message); return; }
        using (res) await ReplyAsync(ctx, res, s.Path, post);
    }

    /// <summary>A designer write (the design role): the JSON body with the signed player and user; the refusal otherwise.</summary>
    private static (HttpContent? Body, (int Status, string Code, string Message)? Error) WriteBody(HttpContext ctx, BridgeIdentity id)
    {
        if (!CanWrite(id)) return (null, (403, "forbidden", "design role required"));
        var obj = SupervisorProxy.ParseObject(ctx.BodyBytes());
        if (obj == null) return (null, (400, "bad_request", "body must be a JSON object"));
        if (obj.ContainsKey("player") || obj.ContainsKey("who")) return (null, (400, "bad_request", "player and who are set by the bridge"));
        obj["player"] = id.Player;
        obj["who"] = id.Who;
        return (new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json"), null);
    }

    /// <summary>The supervisor's reply, buffered; 409 (the session is busy) passes as a 429 so the client retries; other errors map as usual.</summary>
    private static async Task ReplyAsync(HttpContext ctx, HttpResponseMessage res, string path, bool post)
    {
        if (!res.IsSuccessStatusCode)
        {
            if ((int)res.StatusCode == 409) { await ApiErrors.Write(ctx, 429, "rate_limited", "the session is busy (a message is still running)", 1500); return; }
            await SupervisorProxy.MapErrorAsync(ctx, res, path, post);
            return;
        }
        ctx.Response.StatusCode = (int)res.StatusCode;
        SupervisorProxy.CopyHeaders(res, ctx.Response);
        var body = await res.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength = body.Length;
        await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
    }
}
