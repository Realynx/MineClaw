using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Blueprints.Mine;
using MineClaw.Data.Versioning;

namespace MineClaw.App.Supervisor;

/// <summary>
/// The Design Studio's bot routes (docs/web/api.md "Design Studio"), behind <c>Supervisor:Viewer:Enabled</c>,
/// <c>Supervisor:Viewer:Designer:Enabled</c> and the token header (the bridge enforces the <c>design</c>/<c>admin</c> role and
/// injects <c>player</c>/<c>who</c>, the signed-in user, into every write body):
/// <list type="bullet">
/// <item><c>GET /api/designer/models</c> → <c>{models:[{id, label, provider, model, tier, profile, cost_hint, default}], default, version, budget}</c></item>
/// <item><c>POST /api/designer/sessions</c> <c>{model?, design_id?}</c> → the session (a new canvas, or a design's newest revision)</item>
/// <item><c>GET /api/designer/sessions/{id}</c> → the session: canvas, transcript, palette, the design's revisions</item>
/// <item><c>POST /api/designer/sessions/{id}/messages</c> <c>{text, model?, canvas:{rev, edits?}, selection?}</c> → the AI's reply + the ops it applied</item>
/// <item><c>POST /api/designer/sessions/{id}/canvas</c> <c>{rev, ops, anchor?}</c> → the user's own edits (the AI is told about them)</item>
/// <item><c>POST /api/designer/sessions/{id}/save</c> <c>{name, summary?, as_new?}</c> → <c>{design_id, rev, …}</c> through the designs history</item>
/// </list>
/// Everything happens on the sandbox canvas: no route touches the world.
/// </summary>
public sealed class DesignerRoutes
{
    public const string Prefix = "/api/designer";
    /// <summary>A message with a canvas edit batch, or a canvas post (the bridge caps JSON bodies at 256 KB too).</summary>
    public const int MaxBodyBytes = 256 * 1024;
    public const int MaxText = 2000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SupervisorOptions _o;
    private readonly Func<BlueprintService?> _service;
    private readonly DesignerStore _store;
    private readonly LlmOptions _llm;
    private readonly DesignerAgent _agent;
    private readonly UsageTracker? _usage;
    private readonly Func<string> _bot;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    public DesignerRoutes(SupervisorOptions o, Func<BlueprintService?> service, DesignerStore store, ILlmClient llm, LlmOptions llmOptions,
        UsageTracker? usage, Func<string>? bot = null)
    {
        _o = o;
        _service = service;
        _store = store;
        _llm = llmOptions;
        _usage = usage;
        _agent = new DesignerAgent(llm, llmOptions, usage, o.Viewer.Designer);
        _bot = bot ?? (() => "");
    }

    public DesignerStore Store => _store;

    /// <summary>The world seed the server told the bot (b1.8.1's login carries it; 1.12.2's doesn't): null or 0 = unknown.</summary>
    public Func<long?>? ServerSeed { get; init; }

    public static bool IsDesignerPath(string path)
    {
        var p = path.TrimEnd('/');
        return p.Equals(Prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The body of a POST to a designer route (null otherwise), only with the token, up to <see cref="MaxBodyBytes"/>.</summary>
    public static async Task<string?> ReadBodyAsync(HttpListenerRequest req, SupervisorOptions o, CancellationToken ct)
    {
        if (!req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) || !IsDesignerPath(req.Url?.AbsolutePath ?? "/") || !req.HasEntityBody) return null;
        if (!o.Viewer.Enabled || !TokenOk(req.Headers["X-Supervisor-Token"], o.Token)) return null;
        return await SupervisorAdminRoutes.ReadCappedAsync(req, MaxBodyBytes, 16384, ct).ConfigureAwait(false);
    }

    private static bool TokenOk(string? given, string? token)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(given)) return false;
        var a = Encoding.UTF8.GetBytes(given);
        var b = Encoding.UTF8.GetBytes(token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ------------------------------------------------------------------ routing

    /// <summary>Null = not a designer route.</summary>
    public async Task<SupervisorReply?> TryHandleAsync(SupervisorRequest r, CancellationToken ct = default)
    {
        var path = r.Path.TrimEnd('/');
        if (!IsDesignerPath(path)) return null;
        if (Gate(r) is { } denied) return denied;
        bool post = r.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        bool get = r.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || r.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        var rest = path[Prefix.Length..];
        try
        {
            if (rest.Equals("/models", StringComparison.OrdinalIgnoreCase)) return get ? Models() : NotAllowed("GET, HEAD");
            if (rest.Equals("/sessions", StringComparison.OrdinalIgnoreCase)) return post ? Create(r) : NotAllowed("POST");
            return await SessionRouteAsync(r, rest, get, post, ct).ConfigureAwait(false);
        }
        catch (JsonException) { return Reply(400, new { error = "body must be JSON (see docs/web/api.md, Design Studio)" }); }
        catch (OperationCanceledException) { return Reply(503, new { error = "cancelled" }); }
    }

    /// <summary>The gate, in order: the viewer and the designer on, a configured token, the token header, the body size; null = through.</summary>
    private SupervisorReply? Gate(SupervisorRequest r)
    {
        if (!_o.Viewer.Enabled || !_o.Viewer.Designer.Enabled) return Reply(404, new { error = "not found" });
        if (string.IsNullOrEmpty(_o.Token)) return Reply(403, new { error = "the designer needs Supervisor:Token" });
        if (!TokenOk(r.TokenHeader, _o.Token)) return Reply(403, new { error = "X-Supervisor-Token header required" });
        if (r.Body == SupervisorAdminRoutes.TooLarge) return Reply(413, new { error = $"body over {MaxBodyBytes} bytes" });
        return null;
    }

    /// <summary><c>sessions/{id}[/{action}]</c> → the id and the lower-cased action (null for the session itself); null = not that shape.</summary>
    private static (long Id, string? Action)? SessionPath(string rest)
    {
        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (2 or 3) || !parts[0].Equals("sessions", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return null;
        return (id, parts.Length == 3 ? parts[2].ToLowerInvariant() : null);
    }

    /// <summary><c>sessions/{id}</c> (GET) and <c>sessions/{id}/messages|canvas|save|version</c> (POST, one at a time per session).</summary>
    private async Task<SupervisorReply> SessionRouteAsync(SupervisorRequest r, string rest, bool get, bool post, CancellationToken ct)
    {
        if (SessionPath(rest) is not (var id, var action)) return Reply(404, new { error = "not found" });
        if (action == null) return get ? Session(id) : NotAllowed("GET, HEAD");
        if (!post) return NotAllowed("POST");
        return action switch
        {
            "messages" => await Locked(id, () => MessageAsync(id, r, ct), ct).ConfigureAwait(false),
            "canvas" => await Locked(id, () => Task.FromResult(Canvas(id, r)), ct).ConfigureAwait(false),
            "save" => await Locked(id, () => Task.FromResult(Save(id, r)), ct).ConfigureAwait(false),
            "version" => await Locked(id, () => Task.FromResult(Version(id, r)), ct).ConfigureAwait(false),
            _ => Reply(404, new { error = "not found" }),
        };
    }

    private async Task<SupervisorReply> Locked(long id, Func<Task<SupervisorReply>> body, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false))
            return Reply(409, new { error = "the session is busy (a message is still running)" });
        try { return await body().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    // ------------------------------------------------------------------ models

    private SupervisorReply Models()
    {
        var list = DesignerModels.List(_llm);
        var def = list.FirstOrDefault();
        object? budget = null;
        if (_usage != null)
        {
            var (today, _) = _usage.SnapshotTotals();
            budget = new { mode = _usage.Mode.ToString().ToLowerInvariant(), today_usd = (double)today.TotalUsd, daily_cap_usd = (double)_usage.Options.DailyCapUsd };
        }
        return Reply(200, new
        {
            models = list.Select(m => new
            {
                id = m.Id, label = m.Label, provider = m.Provider, model = m.Model, tier = m.Tier.ToString().ToLowerInvariant(), profile = m.Profile,
                cost_hint = m.CostHint, @default = m == def,
            }),
            @default = def?.Id,
            version = GameData.Current.VersionId,
            versions = DesignVersions.All,
            budget,
            // A mine canvas may use it (our own terrain from that seed: it won't match the real world's).
            server_seed = KnownSeed(),
        });
    }

    private string? KnownSeed()
    {
        try { return ServerSeed?.Invoke() is { } n && n != 0 ? n.ToString(CultureInfo.InvariantCulture) : null; }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ sessions

    private sealed record CreateBody(string? Model, long? DesignId, string? Version, string? ConvertTo, string? Kind, string? Seed, int? Rev, string? Player,
        string? Who);

    /// <summary>"build" / "mine" from the body (null = not given; anything else is a 400).</summary>
    private static (string? Kind, string? Error) KindOf(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        null or "" => (null, null),
        DesignerCanvas.KindBuild => (DesignerCanvas.KindBuild, null),
        DesignerCanvas.KindMine => (DesignerCanvas.KindMine, null),
        _ => (null, $"kind '{kind}': use build or mine"),
    };

    private static string Requester(string? player, string? who) =>
        !string.IsNullOrWhiteSpace(player) ? player.Trim() : !string.IsNullOrWhiteSpace(who) ? who.Trim() : "a player";

    private SupervisorReply Create(SupervisorRequest r)
    {
        var b = Parse<CreateBody>(r.Body) ?? new CreateBody(null, null, null, null, null, null, null, null, null);
        var (kind, kindErr) = KindOf(b.Kind);
        if (kindErr != null) return Reply(400, new { error = kindErr });
        if (b.Seed is { Length: > 32 }) return Reply(400, new { error = "seed over 32 chars" });
        var model = DesignerModels.Resolve(_llm, b.Model);
        if (model == null) return Reply(400, new { error = b.Model is { Length: > 0 } ? $"model '{b.Model}' isn't configured" : "no model is configured" });
        string? version = null, convertTo = null;
        if (b.Version is { Length: > 0 } && (version = DesignVersions.Normalize(b.Version)) == null) return Reply(400, new { error = BadVersion(b.Version) });
        if (b.ConvertTo is { Length: > 0 } && (convertTo = DesignVersions.Normalize(b.ConvertTo)) == null) return Reply(400, new { error = BadVersion(b.ConvertTo) });
        var s = new DesignerSessionRecord { Owner = Requester(b.Player, b.Who), Model = model.Id, Name = "new design" };
        s.Canvas.Version = version ?? DesignVersions.Server;
        if (kind == DesignerCanvas.KindMine)
        {
            s.Canvas.Kind = DesignerCanvas.KindMine;
            s.Canvas.Seed = MineTerrain.NormalizeSeed(b.Seed is { Length: > 0 } seed ? seed : MineTerrain.RandomSeed());
            s.Name = "new mine";
        }
        string? note = null;
        ConvertReport? compat = null;
        if (b.Rev is < 1) return Reply(400, new { error = "rev must be 1 or more" });
        if (b.DesignId is { } did)
        {
            if (_service() is not { } svc) return Reply(503, new { error = "designs are unavailable (Bot:Blueprints off)" });
            // A revision (rev) opens that one: saving it again is a revert.
            var l = DesignerSave.LoadDesign(svc, did, b.Rev);
            if (l.Canvas is not { } canvas) return Reply(404, new { error = l.Why ?? "no such design" });
            var (off, rec, why) = (l.Offset, l.Design, l.Why);
            s.Canvas = canvas;
            s.Offset = off;
            s.DesignId = did;
            s.Name = rec?.Name ?? s.Name;
            s.Anchor = l.Anchor;
            s.PreferredFor = l.PreferredFor;
            note = why ?? (b.Rev is { } rv && l.Rev != 0 ? $"Opened r{rv} of #{did}: saving it makes it the newest revision again (a revert)." : null);
            // "Use it in a world of another version" (Story): the design's cells converted, with the report (missing ones left out).
            if (convertTo != null && convertTo != canvas.Version) compat = Convert(s, convertTo);
            using (DesignVersions.Use(s.Version)) Settle(s);
        }
        _store.Insert(s);
        if (note != null) _store.AddMessage(s.Id, "assistant", note);
        return Reply(200, SessionJson(s, compat));
    }

    private static string BadVersion(string v) => $"version '{v}': use {string.Join(" or ", DesignVersions.All)}";

    /// <summary>The session's canvas converted to <paramref name="to"/> (cells that don't exist there left out), its version set.</summary>
    private static ConvertReport Convert(DesignerSessionRecord s, string to)
    {
        var from = s.Version;
        var (cells, report) = DesignConvert.Convert(s.Canvas.Blocks, from, to);
        // A mine stays a mine on its seed (the terrain is regenerated for the new version).
        var next = new DesignerCanvas { Version = to, Rev = s.Canvas.Rev + 1, Kind = s.Canvas.Kind, Seed = s.Canvas.Seed };
        // Straight onto the new canvas (pairs and attachments are carried over as they stand, already whole).
        next.Load(cells, s.Canvas.Dug);
        s.Canvas = next;
        return report;
    }

    private sealed record VersionBody(string? Version, bool? Apply, string? Kind, string? Seed, string? PreferredFor, string? Player, string? Who);

    /// <summary>
    /// <c>POST sessions/{id}/version {version, kind?, seed?, preferred_for?, apply?}</c>: the canvas's target version and mode.
    /// <list type="bullet">
    /// <item><c>kind</c> (build | mine): an empty canvas switches at once; a full one answers <c>needs_clear</c> unless <c>apply</c>,
    /// which clears it and switches (a mine canvas is generated terrain at absolute y: nothing carries over).</item>
    /// <item><c>seed</c> (a mine): its terrain; <c>preferred_for</c> (a version, or "" / "none" to drop it): the design's preference.</item>
    /// <item><c>version</c>: an empty canvas just switches. Otherwise <c>apply</c> false (the default) is the compatibility check:
    /// <c>{version, target, report}</c> (what stays the same, what is substituted, what doesn't exist there); <c>apply</c> true
    /// converts (the missing blocks are left out), tells the model, and returns the session.</item>
    /// </list>
    /// </summary>
    private SupervisorReply Version(long id, SupervisorRequest r)
    {
        if (_store.Get(id) is not { } s) return Reply(404, new { error = "no such session" });
        var b = Parse<VersionBody>(r.Body);
        if (DesignVersions.Normalize(b?.Version) is not { } target) return Reply(400, new { error = BadVersion(b?.Version ?? "") });
        var (kind, kindErr) = KindOf(b!.Kind);
        if (VersionBodyError(b, kindErr) is { } bad) return Reply(400, new { error = bad });
        bool modeChanged = false;
        if (kind != null && kind != s.Canvas.Kind)
        {
            if (!IsBlank(s) && b.Apply != true)
                return Reply(200, new { version = s.Version, target, changed = false, report = (object?)null, needs_clear = true, session = (object?)null });
            SwitchKind(s, kind, target, b.Seed);
            modeChanged = true;
        }
        modeChanged |= ChangeSeed(s, b.Seed);
        modeChanged |= ChangePreferredFor(s, b.PreferredFor);
        if (modeChanged) _store.Update(s);
        return ChangeVersion(s, target, b.Apply == true, modeChanged);
    }

    private static string? VersionBodyError(VersionBody b, string? kindErr)
    {
        if (kindErr != null) return kindErr;
        if (b.Seed is { Length: > 32 }) return "seed over 32 chars";
        if (b.PreferredFor is { Length: > 0 } pfx && pfx != "none" && DesignVersions.Normalize(pfx) == null) return BadVersion(pfx);
        return null;
    }

    private static bool IsBlank(DesignerSessionRecord s) => s.Canvas.Count == 0 && s.Canvas.Dug.Count == 0;

    /// <summary>A fresh canvas of the other kind (a mine canvas is generated terrain at absolute y: nothing carries over); the
    /// model hears it.</summary>
    private void SwitchKind(DesignerSessionRecord s, string kind, string target, string? seedText)
    {
        bool mine = kind == DesignerCanvas.KindMine;
        s.Canvas = new DesignerCanvas
        {
            Version = target, Rev = s.Canvas.Rev + 1, Kind = kind,
            Seed = mine ? MineTerrain.NormalizeSeed(seedText is { Length: > 0 } seed ? seed : MineTerrain.RandomSeed()) : "0",
        };
        s.Anchor = null;
        s.Offset = default;
        if (!mine) s.PreferredFor = null;
        var switched = mine ? $"the canvas is now a mine over generated {target} terrain (seed {s.Canvas.Seed}, absolute y)" : "the canvas is a grassland build plot again";
        s.PendingEdits = s.PendingEdits.Length == 0 ? switched : Clip(s.PendingEdits + "; " + switched, 600);
        _store.AddMessage(s.Id, "assistant", mine ? $"Switched to a mine design over generated terrain (seed {s.Canvas.Seed})." : "Switched back to a build design.");
    }

    /// <summary>A mine's new terrain seed; true when it changed.</summary>
    private static bool ChangeSeed(DesignerSessionRecord s, string? newSeed)
    {
        if (newSeed is not { Length: > 0 } || !s.Canvas.Mine || MineTerrain.NormalizeSeed(newSeed) == s.Canvas.Seed) return false;
        s.Canvas.Seed = MineTerrain.NormalizeSeed(newSeed);
        s.PendingEdits = Clip((s.PendingEdits.Length == 0 ? "" : s.PendingEdits + "; ") + $"the terrain seed is now {s.Canvas.Seed}", 600);
        return true;
    }

    /// <summary>A mine's preferred version ("" / "none" drops it); true when it changed.</summary>
    private static bool ChangePreferredFor(DesignerSessionRecord s, string? preferredFor)
    {
        if (preferredFor == null || !s.Canvas.Mine) return false;
        var pf = preferredFor is "" or "none" ? null : DesignVersions.Normalize(preferredFor);
        if (pf == s.PreferredFor) return false;
        s.PreferredFor = pf;
        return true;
    }

    /// <summary>The target version: the same (nothing to do), an empty canvas (switches), the compatibility check (no apply), or
    /// the conversion (the missing blocks left out; the model hears it).</summary>
    private SupervisorReply ChangeVersion(DesignerSessionRecord s, string target, bool apply, bool modeChanged)
    {
        if (target == s.Version) return Reply(200, new { version = s.Version, target, changed = modeChanged, report = (object?)null, needs_clear = false, session = SessionJson(s) });
        if (IsBlank(s))
        {
            s.Canvas.Version = target;
            _store.Update(s);
            return Reply(200, new { version = s.Version, target, changed = true, report = (object?)null, session = SessionJson(s) });
        }
        if (!apply)
        {
            var (_, check) = DesignConvert.Convert(s.Canvas.Blocks, s.Version, target);
            return Reply(200, new { version = s.Version, target, changed = false, report = ReportJson(check), session = (object?)null });
        }
        var from = s.Version;
        var report = Convert(s, target);
        var line = $"the canvas was converted {report.Text()}" + (report.Missing.Count > 0 ? " (left out)" : "");
        s.PendingEdits = s.PendingEdits.Length == 0 ? line : Clip(s.PendingEdits + "; " + line, 600);
        using (DesignVersions.Use(s.Version)) Settle(s);
        _store.Update(s);
        _store.AddMessage(s.Id, "assistant", $"Converted the design from {from} to {target}: {report.Text()}.");
        return Reply(200, new { version = s.Version, target, changed = true, report = ReportJson(report), session = SessionJson(s) });
    }

    private static object ReportJson(ConvertReport r) => new
    {
        from = r.From, to = r.To, same = r.Same, compatible = r.Compatible, exact = r.Exact, text = r.Text(),
        substituted = r.Substituted.Select(kv => new { name = kv.Key, count = kv.Value }),
        missing = r.Missing.Select(kv => new { name = kv.Key, count = kv.Value }),
    };

    private SupervisorReply Session(long id)
    {
        if (_store.Get(id) is not { } s) return Reply(404, new { error = "no such session" });
        using var scope = DesignVersions.Use(s.Version);
        if (Settle(s)) _store.Update(s);
        return Reply(200, SessionJson(s));
    }

    /// <summary>
    /// A canvas opened (a session again, or a design loaded): wall torches and ladders hanging on air are turned to their solid
    /// side (<see cref="DesignerCanvas.FixAllAttachments"/>), and the model hears about it with the next message. The browser
    /// opens the corrected canvas as its starting point (not an unsaved change); the correction reaches the design on the next
    /// save. True = the canvas changed.
    /// </summary>
    private static bool Settle(DesignerSessionRecord s)
    {
        var fixedCells = s.Canvas.FixAllAttachments();
        if (fixedCells.Count == 0) return false;
        var line = "the canvas opened with " + DesignerCanvas.FixText(fixedCells);
        s.PendingEdits = s.PendingEdits.Length == 0 ? line : Clip(s.PendingEdits + "; " + line, 600);
        return true;
    }

    private object SessionJson(DesignerSessionRecord s, ConvertReport? compat = null)
    {
        var revs = s.DesignId is { } did && _service()?.History?.Revisions is { } h
            ? h.List(did, 50).Select(v => (object)new
            {
                rev = v.Rev, author = v.Author, author_kind = v.AuthorKind, message = v.Message, tag = v.Tag, created = v.At.ToUnixTimeMilliseconds(), n = v.Blocks,
                version = DesignVersions.OfDsl(v.Dsl), kind = MineDesign.IsMine(v.Dsl) ? DesignerCanvas.KindMine : DesignerCanvas.KindBuild,
            }).ToList()
            : [];
        var server = DesignVersions.Server;
        var pal = DesignPalette.For(s.Version);
        // Built on this server: as it is, converted (substitutions) or not at all (blocks it doesn't have).
        object? serverCompat = null;
        if (s.Version != server && s.Canvas.Count > 0) serverCompat = ReportJson(DesignConvert.Convert(s.Canvas.Blocks, s.Version, server).Report);
        return new
        {
            id = s.Id, owner = s.Owner, model = s.Model, design_id = s.DesignId, name = s.Name, rev = s.Canvas.Rev, blocks = s.Canvas.Wire(), dug = s.Canvas.DugWire(),
            kind = s.Canvas.Kind, seed = s.Canvas.Mine ? s.Canvas.Seed : null, preferred_for = s.PreferredFor,
            anchor = s.Anchor is { } a ? new[] { a.X, a.Y, a.Z } : null,
            messages = _store.Messages(s.Id).Select(MessageJson),
            cost_usd = (double)s.CostUsd, calls = s.Calls, version = s.Version, server_version = server, versions = DesignVersions.All,
            palette = pal.Entries.Select(p => new
            {
                id = p.Id, meta = p.Meta, name = p.Name, facing = p.Facing, spec = p.Spec, cat = p.Category, family = p.Family.ToString().ToLowerInvariant(),
                icon = p.Icon,
            }),
            categories = DesignPalette.Categories,
            compat = compat == null ? null : ReportJson(compat),
            server_compat = serverCompat,
            // A mine: absolute y 0..127 ([-depth, height)).
            plot = s.Canvas.Mine ? new { size = DesignerCanvas.Size, height = DesignerCanvas.MineHeight, depth = 0 }
                : new { size = DesignerCanvas.Size, height = DesignerCanvas.Height, depth = DesignerCanvas.Depth },
            revisions = revs,
            created = s.Created.ToUnixTimeMilliseconds(), updated = s.Updated.ToUnixTimeMilliseconds(),
        };
    }

    private static object MessageJson(DesignerMessageRecord m) => new
    {
        seq = m.Seq, role = m.Role, text = m.Text, at = m.At.ToUnixTimeMilliseconds(), model = m.Model, cost_usd = (double)m.CostUsd,
        ops = m.Ops.Select(o => new { kind = o.Kind, label = o.Label, cells = o.Cells }),
    };

    private static object OpJson(DesignerOp o) => new { kind = o.Kind, label = o.Label, by = o.By, cells = o.Cells.Select(c => c.Wire) };

    // ------------------------------------------------------------------ edits

    private sealed record OpBody(string? Kind, string? Label, int[][]? Cells);
    private sealed record CanvasRef(int Rev, OpBody[]? Edits);
    private sealed record Selection(int[]? Min, int[]? Max);
    private sealed record MessageBody(string? Text, string? Model, CanvasRef? Canvas, Selection? Selection, string? Player, string? Who);
    private sealed record CanvasBody(int Rev, OpBody[]? Ops, int[]? Anchor, bool? ClearAnchor, string? Player, string? Who);
    private sealed record SaveBody(string? Name, string? Summary, bool? AsNew, string? Player, string? Who);

    /// <summary>The user's edits applied in order; null = nothing malformed. Each changed cell is summarised for the AI's next turn.</summary>
    private static string? ApplyEdits(DesignerSessionRecord s, OpBody[]? ops, out int applied) => ApplyEdits(s, ops, out applied, out _);

    /// <summary>The same, with the attachment rule's corrections of the user's cells (<paramref name="fixedCells"/>: the browser
    /// applies them to stay in step).</summary>
    private static string? ApplyEdits(DesignerSessionRecord s, OpBody[]? ops, out int applied, out List<CanvasCell> fixedCells)
    {
        applied = 0;
        fixedCells = [];
        if (ops == null || ops.Length == 0) return null;
        if (ops.Length > 256) return "at most 256 ops per request";
        var all = new List<CanvasCell>();
        foreach (var op in ops)
        {
            if (op.Cells == null) continue;
            if (op.Cells.Any(c => c == null || c.Length != 5)) return "a cell is [x, y, z, id, meta]";
            all.AddRange(op.Cells.Select(c => new CanvasCell(c[0], c[1], c[2], c[3], c[4])));
        }
        if (all.Count > DesignerCanvas.MaxCellsPerOp) return $"at most {DesignerCanvas.MaxCellsPerOp} cells per request";
        var changed = s.Canvas.Apply(all);
        applied = changed.Count;
        fixedCells = s.Canvas.LastFixed;
        if (changed.Count > 0)
        {
            var line = DesignerCanvas.Summarize(changed, on: s.Canvas) + (DesignerCanvas.FixText(fixedCells) is { Length: > 0 } fix ? $" ({fix})" : "");
            s.PendingEdits = s.PendingEdits.Length == 0 ? line : Clip(s.PendingEdits + "; " + line, 600);
        }
        return null;
    }

    private SupervisorReply Canvas(long id, SupervisorRequest r)
    {
        if (_store.Get(id) is not { } s) return Reply(404, new { error = "no such session" });
        using var scope = DesignVersions.Use(s.Version);
        var b = Parse<CanvasBody>(r.Body);
        if (b == null) return Reply(400, new { error = "body must be JSON {rev, ops:[{kind, label, cells:[[x,y,z,id,meta]]}], anchor?}" });
        if (ApplyEdits(s, b.Ops, out var applied, out var fixedCells) is { } err) return Reply(400, new { error = err });
        if (b.Anchor is { Length: 3 } a && s.Canvas.Contains(a[0], a[1], a[2])) s.Anchor = new MineClaw.World.BlockPos(a[0], a[1], a[2]);
        else if (b.ClearAnchor == true) s.Anchor = null;
        _store.Update(s);
        return Reply(200, new { rev = s.Canvas.Rev, applied, stale = b.Rev != 0 && b.Rev > s.Canvas.Rev, @fixed = fixedCells.Select(c => c.Wire) });
    }

    private async Task<SupervisorReply> MessageAsync(long id, SupervisorRequest r, CancellationToken ct)
    {
        if (_store.Get(id) is not { } s) return Reply(404, new { error = "no such session" });
        using var scope = DesignVersions.Use(s.Version);
        var b = Parse<MessageBody>(r.Body);
        var text = b?.Text?.Trim() ?? "";
        if (text.Length == 0) return Reply(400, new { error = "text is empty" });
        if (text.Length > MaxText) return Reply(400, new { error = $"text over {MaxText} chars" });
        if (b!.Model is { Length: > 0 } wanted)
        {
            if (DesignerModels.Resolve(_llm, wanted) is not { } picked) return Reply(400, new { error = $"model '{wanted}' isn't configured" });
            s.Model = picked.Id;
        }
        var model = DesignerModels.Resolve(_llm, s.Model);
        if (model == null) return Reply(400, new { error = "no model is configured" });
        if (ApplyEdits(s, b.Canvas?.Edits, out _, out var editFixes) is { } err) return Reply(400, new { error = err });
        if (_agent.BudgetRefusal(model) is { } refusal)
        {
            _store.Update(s);
            return Reply(429, new { error = refusal, retry_after_ms = 60_000 });
        }
        var history = _store.Messages(s.Id, 40);
        var player = Requester(b.Player, b.Who);
        var userLine = _store.AddMessage(s.Id, "user", text);
        (int[] Min, int[] Max)? sel = b.Selection is { Min: { Length: 3 } mn, Max: { Length: 3 } mx } ? (mn, mx) : null;
        DesignerTurnResult turn;
        try { turn = await _agent.RunAsync(s, history, model, text, sel, player, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _store.Update(s);
            var line = _store.AddMessage(s.Id, "assistant", "(the model call failed: " + Clip(ex.Message, 160) + ")", model.Model);
            return Reply(502, new { error = "the model call failed", message = MessageJson(line) });
        }
        s.PendingEdits = "";
        s.CostUsd += turn.CostUsd;
        s.Calls += turn.Calls;
        _store.Update(s);
        var summaries = turn.Ops.Select(o => new DesignerOpSummary(o.Kind, o.Label, o.Cells.Count)).ToList();
        var reply = turn.Refusal ?? turn.Reply;
        var msg = _store.AddMessage(s.Id, "assistant", reply, turn.Model, turn.CostUsd, summaries);
        return Reply(200, new
        {
            reply,
            // The user's own cells the attachment rule corrected come first (the browser applies them like the AI's).
            ops = (editFixes.Count > 0 ? [new DesignerOp("fix", DesignerCanvas.FixText(editFixes), editFixes)] : Array.Empty<DesignerOp>()).Concat(turn.Ops).Select(OpJson),
            rev = s.Canvas.Rev, cost_usd = (double)turn.CostUsd, session_cost_usd = (double)s.CostUsd,
            model = turn.Model, calls = turn.Calls,
            usage = new { input = turn.Usage.InputTokens, output = turn.Usage.OutputTokens, cache_read = turn.Usage.CacheReadTokens, cache_write = turn.Usage.CacheWriteTokens },
            message = MessageJson(msg), user_message = MessageJson(userLine), refusal = turn.Refusal,
        });
    }

    private SupervisorReply Save(long id, SupervisorRequest r)
    {
        if (_store.Get(id) is not { } s) return Reply(404, new { error = "no such session" });
        if (_service() is not { } svc) return Reply(503, new { error = "designs are unavailable (Bot:Blueprints off)" });
        var b = Parse<SaveBody>(r.Body);
        if (b == null || string.IsNullOrWhiteSpace(b.Name)) return Reply(400, new { error = "body must be JSON {name, summary?, as_new?}" });
        if (b.Summary is { Length: > 120 }) return Reply(400, new { error = "summary over 120 chars" });
        var res = DesignerSave.Save(svc, s, b.Name, b.Summary, Requester(b.Player, b.Who), b.AsNew == true, _bot());
        if (!res.Ok) return Reply(400, new { error = res.Text });
        _store.Update(s);
        _store.AddMessage(s.Id, "assistant", res.Text);
        return Reply(200, new { design_id = res.DesignId, rev = res.Rev, name = s.Name, blocks = res.Blocks, created = res.Created, text = res.Text });
    }

    // ------------------------------------------------------------------ helpers

    private static T? Parse<T>(string? body) where T : class => string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<T>(body, Json);

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static SupervisorReply NotAllowed(string allow) =>
        Reply(405, new { error = $"{allow} only" }, new Dictionary<string, string> { ["Allow"] = allow });

    private static SupervisorReply Reply(int status, object value, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Json), headers);
}
