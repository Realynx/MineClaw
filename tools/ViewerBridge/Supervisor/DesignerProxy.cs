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
            if (!CanWrite(id)) { await ApiErrors.Write(ctx, 403, "forbidden", "design role required"); return; }
            var obj = SupervisorProxy.ParseObject(ctx.BodyBytes());
            if (obj == null) { await ApiErrors.Write(ctx, 400, "bad_request", "body must be a JSON object"); return; }
            if (obj.ContainsKey("player") || obj.ContainsKey("who")) { await ApiErrors.Write(ctx, 400, "bad_request", "player and who are set by the bridge"); return; }
            obj["player"] = id.Player;
            obj["who"] = id.Who;
            content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
        }
        HttpResponseMessage res;
        try
        {
            res = await sup.SendAsync(post ? HttpMethod.Post : HttpMethods.IsHead(method) ? HttpMethod.Head : HttpMethod.Get,
                SupervisorProxy.Upstream(s.Path, s.Query), content, TimeoutFor(s.Path), false, ctx.RequestAborted);
        }
        catch (SupervisorUnavailableException ex) { await ApiErrors.Write(ctx, 503, "upstream_unavailable", ex.Message); return; }
        using (res)
        {
            if (!res.IsSuccessStatusCode)
            {
                // 409 (the session is busy) passes as a 429 so the client retries; everything else maps as usual.
                if ((int)res.StatusCode == 409) { await ApiErrors.Write(ctx, 429, "rate_limited", "the session is busy (a message is still running)", 1500); return; }
                await SupervisorProxy.MapErrorAsync(ctx, res, s.Path, post);
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
}
