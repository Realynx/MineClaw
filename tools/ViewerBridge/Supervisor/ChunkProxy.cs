using System.Globalization;
using System.Security.Cryptography;
using MineClaw.ViewerBridge.Security;

namespace MineClaw.ViewerBridge.Supervisor;

/// <summary>
/// <c>GET /v1/sup/world/chunks?dim&amp;cols&amp;light</c> through <see cref="ChunkCache"/>: columns whose hash the latest index still
/// lists come from the cache, only the rest is asked of the supervisor, and the reply is re-encoded in the requested order
/// (MCWC v2 with light, v1 without). The reply carries an <c>ETag</c> (content hash) and honours <c>If-None-Match</c> (304).
/// A request the cache cannot parse is passed through unchanged.
/// </summary>
public static class ChunkProxy
{
    public static async Task HandleAsync(HttpContext ctx, SupervisorClient sup, ChunkCache cache)
    {
        var raw = BridgeAuthMiddleware.RawTarget(ctx);
        int qi = raw.IndexOf('?');
        var query = qi >= 0 ? raw[(qi + 1)..] : "";
        var q = ChunkCache.ParseQuery(query);
        var wanted = ChunkCache.ParseCols(q.GetValueOrDefault("cols"));
        int dim = 0;
        if (wanted == null || (q.TryGetValue("dim", out var ds) && ds.Length > 0 && !int.TryParse(ds, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out dim)))
        {
            await SupervisorProxy.ForwardAsync(ctx, sup, HttpMethod.Get, "world/chunks", SupervisorProxy.Upstream("world/chunks", query), null);
            return;
        }
        bool light = ChunkCache.LightOf(query);
        var (hits, missing) = cache.Lookup(light, dim, wanted);
        var fetched = new Dictionary<(int, int), McwcColumn>();
        int version = light ? 2 : 1;
        if (missing.Count > 0)
        {
            if (await FetchAsync(ctx, sup, cache, light, dim, missing, hits.Count > 0, fetched) is not { } v) return; // answered already
            version = v;
        }
        await Write(ctx, Mcwc.Encode(version, dim, Columns(wanted, fetched, hits, version)));
    }

    /// <summary>The columns the cache missed, from the supervisor (stored in the cache): the reply's MCWC version. Null = the request was
    /// answered already (the supervisor's error, an unparsable reply passed through, or a 502 when part came from the cache).</summary>
    private static async Task<int?> FetchAsync(HttpContext ctx, SupervisorClient sup, ChunkCache cache, bool light, int dim, List<(int Cx, int Cz)> missing,
        bool anyHits, Dictionary<(int, int), McwcColumn> fetched)
    {
        var rel = $"api/world/chunks?dim={dim.ToString(CultureInfo.InvariantCulture)}&cols={string.Join(';', missing.Select(m => $"{m.Cx},{m.Cz}"))}" + (light ? "&light=1" : "");
        HttpResponseMessage res;
        try { res = await sup.SendAsync(HttpMethod.Get, rel, null, SupervisorProxy.TimeoutFor("world/chunks"), stream: false, ctx.RequestAborted); }
        catch (SupervisorUnavailableException ex) { await ApiErrors.Write(ctx, 503, "upstream_unavailable", ex.Message); return null; }
        using (res)
        {
            if (!res.IsSuccessStatusCode) { await SupervisorProxy.MapErrorAsync(ctx, res, "world/chunks", false); return null; }
            var bytes = await res.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            try
            {
                var (v, _, cols) = Mcwc.Decode(bytes);
                cache.Store(light, dim, cols);
                foreach (var c in cols) fetched[(c.Cx, c.Cz)] = c;
                return v;
            }
            catch (FormatException)
            {
                if (anyHits) { await ApiErrors.Write(ctx, 502, "upstream_error", "upstream error"); return null; }
                await Write(ctx, bytes);
                return null;
            }
        }
    }

    /// <summary>The wanted columns in order, fetched or cached (light dropped for a v1 reply).</summary>
    private static List<McwcColumn> Columns(List<(int Cx, int Cz)> wanted, Dictionary<(int, int), McwcColumn> fetched, Dictionary<(int, int), McwcColumn> hits, int version)
    {
        var outCols = new List<McwcColumn>(wanted.Count);
        foreach (var w in wanted)
            if (fetched.TryGetValue(w, out var c) || hits.TryGetValue(w, out c)) outCols.Add(version == 2 || c.Light == null ? c : c with { Light = null });
        return outCols;
    }

    private static async Task Write(HttpContext ctx, byte[] body)
    {
        var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(body))[..32] + "\"";
        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers.CacheControl = "private, no-cache";
        if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
        {
            ctx.Response.StatusCode = 304;
            return;
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = body.Length;
        await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
    }
}
