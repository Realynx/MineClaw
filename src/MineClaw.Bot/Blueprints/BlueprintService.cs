using System.Globalization;
using System.Text.Json;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Blueprints;

/// <summary>A resolved build site: the ground block the build stands on, and how it was found.</summary>
public sealed record SitePick(BlockPos Ground, string How);

/// <summary>
/// Build from a description, end to end (ARCHITECTURE.md "Blueprints"): the design loop in the background, the candidate saved
/// with its preview and bill, the chat summary, change requests → another revision, acceptance by a trusted player → construction
/// split across the crew's bodies, the finished build recorded as a soft-protected structure (scaffolding stays self-placed).
/// Everything here is C#: the brain spends nothing beyond its one <c>design</c> call.
/// </summary>
public sealed class BlueprintService
{
    private readonly BlueprintOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private readonly Dictionary<long, CancellationTokenSource> _running = new();

    public BlueprintService(BlueprintOptions o, BlueprintStore store, BlueprintPreview preview, TimeProvider? time = null, ILogger? log = null)
    {
        _o = o;
        Store = store;
        Preview = preview;
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    public BlueprintOptions Options => _o;
    public BlueprintStore Store { get; }
    public BlueprintPreview Preview { get; }
    public DesignTelemetry? Telemetry { get; init; }

    // ---- seams (wiring fills them; tests fake them)
    public IBlueprintAuthor? Author { get; set; }
    public IDesignCritic? Critic { get; set; }
    public required Func<SiteContext> Site { get; init; }
    public Func<RoleMap?> Roles { get; init; } = () => null;
    public Func<IVoxelWorld?> Light { get; init; } = () => null;
    public Func<TextureAtlas?> Atlas { get; init; } = () => null;
    public Func<long> WorldTime { get; init; } = () => 6000;
    public Func<int> Dimension { get; init; } = () => 0;
    public required Func<IBuildCrew> Crew { get; init; }
    public ConstructionExecutor Executor { get; init; } = new(new BlueprintOptions());
    /// <summary>(at, requester) → site; null at = pointed / auto-pick near the requester.</summary>
    public required Func<string?, string?, (SitePick? Site, string? Error)> ResolveSite { get; init; }
    public Func<IReadOnlyList<string>> Trusted { get; init; } = () => [];
    public Func<string, Task> Say { get; init; } = _ => Task.CompletedTask;
    /// <summary>Context for the brain's next turn (background: never wakes it).</summary>
    public Action<string> Notice { get; init; } = _ => { };
    /// <summary>Why designing isn't allowed now (not the Opus brain, budget paused), or null.</summary>
    public Func<string?> AuthorRefusal { get; init; } = () => null;
    /// <summary>Runs a build as the body's job (an activity: stop cancels it). (blueprint id, part or null) → refusal or null.</summary>
    public Func<long, int?, string?> StartBuild { get; set; } = (_, _) => "no build runner";
    /// <summary>A finished build: (record, plan) → memory structure + protection bookkeeping.</summary>
    public Action<BlueprintRecord, VoxelPlan>? Built { get; init; }
    public Func<BlockPos, string?> Landmark { get; init; } = _ => null;
    /// <summary>Last finished design run (tests).</summary>
    public Task? LastRun { get; private set; }

    public bool IsTrusted(string? player) => player != null && Trusted().Contains(player, StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ design

    /// <summary>Starts a design (background). The answer is the one line the brain gets now; the summary goes to chat later.</summary>
    public string Design(string what, string? at, string? requester)
    {
        if (string.IsNullOrWhiteSpace(what)) return "error: say what to build (what=...)";
        if (AuthorRefusal() is { } no) return "error: " + no;
        if (Author == null) return "error: no blueprint author (Opus brain) configured";
        var (site, err) = ResolveSite(at, requester);
        if (site == null) return "error: " + (err ?? "no site");
        var rec = Store.Insert(new BlueprintRecord
        {
            Name = ShortName(what), Description = what.Trim(), Requester = requester, Bot = Crew().Bodies.FirstOrDefault()?.Name ?? "",
            Status = DesignStatus.Designing, Dim = Dimension(), Site = site.Ground,
        });
        var req = new DesignRequest(rec.Description, site.Ground, SiteText(site), Constraints(site.Ground));
        LastRun = Run(rec, req);
        return $"designing #{rec.Id} \"{rec.Name}\" ({site.How}): drafting, checking and previewing in the background; the summary goes to chat";
    }

    /// <summary>A change request ("make it taller") → another revision of a design.</summary>
    public string Revise(string idOrName, string change, string? requester)
    {
        if (Store.Find(idOrName) is not { } rec) return $"error: no design '{idOrName}'";
        if (AuthorRefusal() is { } no) return "error: " + no;
        if (Author == null) return "error: no blueprint author configured";
        if (rec.Status is DesignStatus.Building) return $"error: #{rec.Id} is being built (stop it first)";
        if (rec.Status is DesignStatus.Designing) return $"#{rec.Id} is still being designed: I'll apply that after";
        rec.Status = DesignStatus.Designing;
        Store.Update(rec);
        var site = new SitePick(rec.Site, "same site");
        var req = new DesignRequest(rec.Description, rec.Site, SiteText(site), Constraints(rec.Site)) { PriorDsl = rec.Dsl, Change = change };
        LastRun = Run(rec, req, change);
        return $"revising #{rec.Id} \"{rec.Name}\": {Trim(change, 60)}";
    }

    private Task Run(BlueprintRecord rec, DesignRequest req, string? change = null)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        lock (_lock) _running[rec.Id] = cts;
        return Task.Run(async () =>
        {
            try { await RunDesignAsync(rec, req, change, cts.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "design #{Id} failed", rec.Id);
                rec.Status = string.IsNullOrEmpty(rec.Dsl) ? DesignStatus.Failed : DesignStatus.Review;
                Store.Update(rec);
                await Say($"couldn't finish the design for the {rec.Name}: {Trim(ex.Message, 80)}").ConfigureAwait(false);
            }
            finally
            {
                lock (_lock) _running.Remove(rec.Id);
                cts.Dispose();
            }
        });
    }

    private async Task RunDesignAsync(BlueprintRecord rec, DesignRequest req, string? change, CancellationToken ct)
    {
        var loop = new DesignLoop(Author!, _o.Critique ? Critic : null, Preview, _o)
        {
            Site = Site, Roles = Roles, Light = Light, Atlas = Atlas, Time = WorldTime,
            Evaluated = (v, _) => _log?.LogInformation("design #{Id} v{N}: {Eval}", rec.Id, v.N, v.EvalText),
        };
        var outcome = await loop.RunAsync(req, ct).ConfigureAwait(false);
        rec.CostUsd += outcome.Usd;
        rec.Iterations += outcome.Versions.Count;
        Telemetry?.Record(rec, outcome, change != null);
        if (outcome.Best is not { Plan: { } plan } best)
        {
            rec.Status = string.IsNullOrEmpty(rec.Dsl) ? DesignStatus.Failed : DesignStatus.Review;
            rec.Eval = outcome.Versions.LastOrDefault()?.EvalText ?? outcome.Stop;
            Store.Update(rec);
            await Say($"I couldn't get a workable design for the {rec.Name} ({outcome.Stop}).").ConfigureAwait(false);
            Notice($"[design] #{rec.Id} failed: {Trim(rec.Eval, 200)}");
            return;
        }
        rec.Dsl = best.Dsl;
        rec.Name = plan.Blueprint.Name is { Length: > 0 } n && n != "build" ? n : rec.Name;
        rec.Origin = plan.Origin;
        rec.Rev++;
        rec.Eval = best.EvalText;
        rec.Critique = best.Critique is { Skipped: false } c ? c.Text : "";
        rec.Bill = best.Eval!.Bill.Select(b => new BillRow(b.Name, b.Need, b.Have, b.Stored, b.Missing, b.Source)).ToList();
        rec.Status = best.Passing ? DesignStatus.Review : DesignStatus.Draft;
        rec.Summary = Summary(rec, plan, best.Eval!);
        Store.Update(rec, best.Picture?.Png);
        var line = best.Passing
            ? $"{rec.Summary}; say 'build it' or tell me what to change"
            : $"{rec.Summary}, but it still has problems ({Trim(string.Join("; ", best.Eval!.Issues.Where(i => i.Level == IssueLevel.Error).Take(2)), 90)}); tell me what to change";
        await Say(line).ConfigureAwait(false);
        Notice($"[design] #{rec.Id} {rec.Status} v{best.N}/{outcome.Versions.Count} ${outcome.Usd:0.000}: {Trim(best.EvalText, 260)}");
    }

    /// <summary>"small cobble hut 5×5×4 east of the gen, door south, 2 windows: 86 cobblestone, 12 planks".</summary>
    public string Summary(BlueprintRecord rec, VoxelPlan plan, DesignEvaluation e)
    {
        var parts = new List<string> { $"{rec.Name} {plan.Blueprint.SizeText}" + (Landmark(rec.Site) is { Length: > 0 } lm ? " " + lm : "") };
        if (e.DoorSides.Count > 0) parts.Add("door " + string.Join("/", e.DoorSides.Distinct().Select(d => d.Name())));
        int windows = Windows(plan);
        if (windows > 0) parts.Add($"{windows} window{(windows > 1 ? "s" : "")}");
        if (plan.Blocks.Values.Any(b => b.Id == Data.Blocks.Torch)) parts.Add("lit");
        var bill = string.Join(", ", e.Bill.OrderByDescending(b => b.Need).Take(3).Select(b => $"{b.Need} {b.Name}"));
        var shortfall = e.Bill.Where(b => b.Missing > 0).ToList();
        var tail = shortfall.Count == 0 ? " (all on hand)" : $" (short {string.Join(", ", shortfall.Take(2).Select(b => $"{b.Missing} {b.Name}"))}, I'll gather them)";
        return string.Join(", ", parts) + ": " + bill + tail;
    }

    /// <summary>Window count = connected groups of glass/pane cells.</summary>
    public static int Windows(VoxelPlan plan)
    {
        var cells = plan.Blocks.Values.Where(b => b.Id is Data.Blocks.Glass or Data.Blocks.GlassPane).Select(b => b.Pos).ToHashSet();
        int groups = 0;
        while (cells.Count > 0)
        {
            groups++;
            var q = new Queue<BlockPos>([cells.First()]);
            cells.Remove(q.Peek());
            while (q.Count > 0)
            {
                var p = q.Dequeue();
                foreach (var f in BlockFaceExtensions.All)
                    if (cells.Remove(p.Offset(f))) q.Enqueue(p.Offset(f));
            }
        }
        return groups;
    }

    // ------------------------------------------------------------------ review

    /// <summary>The design a player's chat refers to: the newest one in review (or draft) within the review window.</summary>
    public BlueprintRecord? Pending(string? player)
    {
        var now = _time.GetUtcNow();
        return Store.List(8).FirstOrDefault(r => r.Status is DesignStatus.Review or DesignStatus.Draft or DesignStatus.Designing
            && now - r.Updated < TimeSpan.FromMinutes(_o.ReviewMinutes)
            && (player == null || r.Requester == null || r.Requester.Equals(player, StringComparison.OrdinalIgnoreCase) || IsTrusted(player)));
    }

    /// <summary>A trusted player's explicit "build it": the design is accepted and construction starts.</summary>
    public string Accept(BlueprintRecord rec, string by)
    {
        if (!IsTrusted(by)) return $"only {string.Join(" or ", Trusted().Take(2))} can approve a build";
        if (rec.Status == DesignStatus.Draft) return $"#{rec.Id} still fails its checks; tell me what to change first";
        if (rec.Status is not (DesignStatus.Review or DesignStatus.Accepted)) return $"#{rec.Id} is {rec.Status}";
        rec.Status = DesignStatus.Accepted;
        rec.AcceptedBy = by;
        Store.Update(rec);
        var refusal = StartBuild(rec.Id, null);
        return refusal == null ? $"building the {rec.Name}" : $"accepted the {rec.Name}, but can't start: {refusal}";
    }

    /// <summary>"Build another one like the hut" (a trusted player): a copy of a saved design at a new site, accepted by them.</summary>
    public (BlueprintRecord? Copy, string Text) Reuse(BlueprintRecord source, string? at, string by)
    {
        if (!IsTrusted(by)) return (null, $"only {string.Join(" or ", Trusted().Take(2))} can start a build");
        var (site, err) = ResolveSite(at, by);
        if (site == null) return (null, "error: " + (err ?? "no site"));
        var (bp, _) = BlueprintParser.Parse(source.Dsl);
        bp.Origin = null; // the new site decides
        var copy = Store.Insert(new BlueprintRecord
        {
            Name = source.Name, Description = source.Description, Requester = by, Bot = source.Bot, Dsl = StripOrigin(source.Dsl),
            Status = DesignStatus.Accepted, Dim = Dimension(), Site = site.Ground,
        });
        var (fx, fz) = bp.Footprint;
        var (plan, issues) = BlueprintNormalizer.Normalize(bp, new BlockPos(site.Ground.X - fx / 2, site.Ground.Y + 1, site.Ground.Z - fz / 2));
        if (plan == null) return (null, "error: the saved design doesn't parse any more");
        var eval = DesignChecks.Evaluate(plan, Site(), issues);
        copy.Origin = plan.Origin;
        copy.AcceptedBy = by;
        copy.Eval = eval.Compact(1);
        copy.Bill = eval.Bill.Select(b => new BillRow(b.Name, b.Need, b.Have, b.Stored, b.Missing, b.Source)).ToList();
        copy.Summary = Summary(copy, plan, eval);
        if (!eval.Pass)
        {
            copy.Status = DesignStatus.Draft;
            Store.Update(copy);
            return (copy, $"the {source.Name} doesn't fit there: {Trim(string.Join("; ", eval.Issues.Where(i => i.Level == IssueLevel.Error).Take(2)), 140)}");
        }
        try { Store.Update(copy, Preview.Render(plan, Site().World, Roles(), Light(), Atlas(), WorldTime()).Png); } catch (Exception) { Store.Update(copy); }
        var refusal = StartBuild(copy.Id, null);
        return (copy, refusal == null ? $"building another {source.Name} (#{copy.Id})" : $"#{copy.Id} ready but can't start: {refusal}");
    }

    private static string StripOrigin(string dsl) =>
        string.Join("\n", dsl.Replace("\r", "").Split('\n').Where(l => !l.TrimStart().StartsWith("origin", StringComparison.OrdinalIgnoreCase)));

    public string Cancel(BlueprintRecord rec)
    {
        lock (_lock) if (_running.TryGetValue(rec.Id, out var cts)) cts.Cancel();
        if (rec.Status is DesignStatus.Built) return $"#{rec.Id} is already built";
        rec.Status = DesignStatus.Cancelled;
        Store.Update(rec);
        return $"cancelled #{rec.Id} \"{rec.Name}\"";
    }

    // ------------------------------------------------------------------ construction

    /// <summary>The plan of a saved design at its origin.</summary>
    public VoxelPlan? PlanOf(BlueprintRecord rec, out string? error)
    {
        var (bp, issues) = BlueprintParser.Parse(rec.Dsl);
        var (fx, fz) = bp.Footprint;
        var origin = rec.Origin ?? new BlockPos(rec.Site.X - fx / 2, rec.Site.Y + 1, rec.Site.Z - fz / 2);
        bp.Origin ??= origin;
        var (plan, more) = BlueprintNormalizer.Normalize(bp, origin);
        issues.AddRange(more);
        error = plan == null || issues.Any(i => i.Level == IssueLevel.Error) ? string.Join("; ", issues.Where(i => i.Level == IssueLevel.Error).Take(3)) : null;
        return plan;
    }

    /// <summary>
    /// Builds an accepted design. No part = the whole thing, split across the crew's bodies (strips at the same time) when it is
    /// big enough; a part (1-based, of N) and/or a layer = just that share on the first body (a crew script calls one per body).
    /// </summary>
    public async Task<(bool Ok, string Text)> BuildAsync(string idOrName, int? part, int? of, int? layer, CancellationToken ct, bool byLayer = false)
    {
        if (Store.Find(idOrName) is not { } rec) return (false, $"no design '{idOrName}'");
        if (rec.Status is not (DesignStatus.Accepted or DesignStatus.Building or DesignStatus.Built))
            return (false, rec.Status == DesignStatus.Review ? $"#{rec.Id} waits for a trusted player's 'build it'" : $"#{rec.Id} is {rec.Status}, not accepted");
        if (PlanOf(rec, out var err) is not { } plan) return (false, $"#{rec.Id} doesn't parse: {err}");
        var bodies = Crew().Bodies;
        if (bodies.Count == 0) return (false, "no body to build with");
        rec.Status = DesignStatus.Building;
        Store.Update(rec);
        List<(BuildPart Part, IBuildBody Body)> work;
        if (part is { } p1)
        {
            int n = Math.Max(1, of ?? Math.Max(1, Store.Builds(rec.Id).Select(b => b.Parts).DefaultIfEmpty(1).Max()));
            var parts = plan.Partition(n, byLayer, rec.Dim);
            if (p1 < 1 || p1 > parts.Count) return (false, $"part {p1} of {parts.Count}?");
            work = [(parts[p1 - 1], bodies[0])];
        }
        else
        {
            int n = plan.Blocks.Count > _o.SplitAboveBlocks ? bodies.Count : 1;
            var parts = n > 1 ? plan.Partition(n, false, rec.Dim) : [BuildPart.Whole(plan, rec.Dim)];
            work = parts.Select((pp, i) => (pp, bodies[i % bodies.Count])).ToList();
        }
        var reports = await Task.WhenAll(work.Select(w => Executor.RunAsync(rec.Id, plan, w.Part, w.Body, ct, layer))).ConfigureAwait(false);
        var text = string.Join(" | ", reports.Select((r, i) => r.Text(work[i].Part.Label)));
        var all = plan.Blocks.Values.Where(b => b.Pos.Y - plan.Origin.Y == layer || layer == null).ToList();
        var world = bodies[0].World;
        bool complete = reports.All(r => r.Stopped == null) && all.All(b => DesignChecks.SameState(b, world.GetBlock(b.Pos), world.GetMeta(b.Pos)));
        if (complete && layer == null && (part == null || BuildFinished(rec, plan, world)))
        {
            rec.Status = DesignStatus.Built;
            Store.Update(rec);
            try { Built?.Invoke(rec, plan); } catch (Exception ex) { _log?.LogWarning(ex, "recording build #{Id} failed", rec.Id); }
            await Say($"finished the {rec.Name}").ConfigureAwait(false);
            return (true, $"built #{rec.Id} \"{rec.Name}\": {text}");
        }
        var stop = reports.Select(r => r.Stopped).FirstOrDefault(s => s != null);
        return (stop == null, $"#{rec.Id} \"{rec.Name}\" {(stop == null ? "progress" : "paused")}: {text}");
    }

    private static bool BuildFinished(BlueprintRecord rec, VoxelPlan plan, WorldState w) =>
        plan.Blocks.Values.All(b => DesignChecks.SameState(b, w.GetBlock(b.Pos), w.GetMeta(b.Pos)));

    /// <summary>Unfinished builds of this body (resume after a restart): starts the first through <see cref="StartBuild"/>.</summary>
    public string? ResumePending(string owner)
    {
        foreach (var b in Store.Unfinished(owner))
        {
            if (Store.Get(b.BlueprintId) is not { Status: DesignStatus.Building or DesignStatus.Accepted } rec) continue;
            var refusal = StartBuild(rec.Id, b.Parts > 1 ? b.Part + 1 : null);
            var line = $"resuming the {rec.Name} build (#{rec.Id}, {b.Placed}/{b.Total} placed before the restart)";
            Notice("[design] " + line + (refusal != null ? $": can't start ({refusal})" : ""));
            return refusal == null ? line : null;
        }
        return null;
    }

    /// <summary>"hut #3 5×5×4, 5 layers, review; bill: ...; parts 1" (the Lua blueprint(id) answer / design show).</summary>
    public string Describe(BlueprintRecord rec)
    {
        var plan = PlanOf(rec, out _);
        var size = plan?.Blueprint.SizeText ?? "?";
        var builds = Store.Builds(rec.Id);
        var progress = builds.Count == 0 ? "" : "; build: " + string.Join(", ", builds.Select(b => $"part {b.Part + 1}/{b.Parts} {b.State} {b.Placed}/{b.Total}"));
        return $"#{rec.Id} \"{rec.Name}\" {size}, {plan?.LayerCount ?? 0} layers, {plan?.Blocks.Count ?? 0} blocks, {rec.Status}"
               + (rec.Bill.Count > 0 ? "; bill: " + string.Join(", ", rec.Bill.Take(5).Select(b => b.Missing > 0 ? $"{b.Need} {b.Item} (short {b.Missing})" : $"{b.Need} {b.Item}")) : "")
               + progress + (rec.Eval.Length > 0 ? "; eval: " + Trim(rec.Eval, 200) : "");
    }

    // ------------------------------------------------------------------ site and constraints text

    public string SiteText(SitePick site)
    {
        var g = site.Ground;
        var lm = Landmark(g);
        return string.Create(CultureInfo.InvariantCulture, $"ground block {g.X},{g.Y},{g.Z} ({site.How}{(lm != null ? ", " + lm : "")}); layer 0 is y{g.Y + 1}, footprint centred there unless you set origin");
    }

    private static readonly int[] Materials =
    [
        Data.Blocks.Cobblestone, Data.Blocks.Stone, Data.Blocks.Planks, Data.Blocks.Log, Data.Blocks.Glass, Data.Blocks.GlassPane, Data.Blocks.Sand,
        Data.Blocks.Bricks, Data.Blocks.StoneBricks, Data.Blocks.Sandstone, Data.Blocks.Wool, Data.Blocks.Torch, Data.Blocks.Slab,
        Data.Blocks.CobblestoneStairs, Data.Blocks.WoodenStairs, Data.Blocks.Fence, Items.WoodenDoor, Data.Blocks.Ladder, Data.Blocks.Glowstone,
        Data.Blocks.Netherrack, Data.Blocks.Obsidian, Data.Blocks.Dirt,
    ];

    /// <summary>Materials at hand (inventory+storage), the ground's extent to the void edge, protected builds near the site.</summary>
    public string Constraints(BlockPos ground)
    {
        var s = Site();
        var mats = Materials.Select(id => (Id: id, Inv: s.Inventory?.Invoke(id, -1) ?? 0, St: s.Stored?.Invoke(id) ?? 0))
            .Where(m => m.Inv + m.St > 0).Select(m => $"{Items.Get(m.Id).Name} {m.Inv}{(m.St > 0 ? "+" + m.St + " stored" : "")}").ToList();
        var parts = new List<string>
        {
            "materials: " + (mats.Count > 0 ? string.Join(", ", mats.Take(10)) : "almost none"),
            (s.GeneratorKnown ? "cobble gen known (cobble is cheap)" : "no cobble gen known") + (s.ChoppableTrees > 0 ? $", {s.ChoppableTrees} tree(s) to fell" : ", no trees to spare"),
            "ground from the site: " + GroundExtent(s.World, ground),
        };
        if (Nearby(s, ground) is { Length: > 0 } near) parts.Add("keep clear of: " + near);
        return string.Join("; ", parts);
    }

    private static string GroundExtent(IBlockAccess w, BlockPos g)
    {
        var bits = new List<string>();
        foreach (var d in new[] { Dir.North, Dir.East, Dir.South, Dir.West })
        {
            var (dx, dz) = d.Offset();
            int n = 0;
            for (int k = 1; k <= 12; k++)
            {
                int x = g.X + dx * k, z = g.Z + dz * k;
                if (!w.IsLoaded(x, z) || !BuildBlocks.Supports(w.GetBlock(x, g.Y, z))) break;
                n = k;
            }
            bits.Add($"{d.Name()[0]}{n}{(n < 12 ? (VoidBeyond(w, g.X + dx * (n + 1), g.Y, g.Z + dz * (n + 1)) ? " (void)" : " (edge)") : "+")}");
        }
        return string.Join(" ", bits) + " blocks";
    }

    private static bool VoidBeyond(IBlockAccess w, int x, int y, int z)
    {
        if (!w.IsLoaded(x, z)) return false;
        for (int yy = y; yy >= 0; yy--) if (w.GetBlock(x, yy, z) != 0) return false;
        return true;
    }

    private static string Nearby(SiteContext s, BlockPos g)
    {
        if (s.Verdict == null) return "";
        var found = new Dictionary<string, BlockPos>();
        var w = s.World;
        for (int y = g.Y - 1; y <= g.Y + 4; y++)
            for (int z = g.Z - 10; z <= g.Z + 10; z++)
                for (int x = g.X - 10; x <= g.X + 10; x++)
                {
                    if (!w.IsLoaded(x, z)) continue;
                    int id = w.GetBlock(x, y, z);
                    if (id == 0) continue;
                    var p = new BlockPos(x, y, z);
                    ProtectionVerdict v;
                    try { v = s.Verdict(p, id); } catch (Exception) { continue; }
                    if (!v.Protected || v.Reason is "floor") continue;
                    var key = v.Level == ProtectionLevel.Hard ? "never-break " + Blocks.Get(id).Name : string.IsNullOrWhiteSpace(v.Why) ? v.Reason : v.Why;
                    if (!found.TryGetValue(key, out var old) || old.DistanceSquaredTo(g) > p.DistanceSquaredTo(g)) found[key] = p;
                }
        return string.Join("; ", found.OrderBy(kv => kv.Value.DistanceSquaredTo(g)).Take(4).Select(kv => $"{Trim(kv.Key, 40)} {Rel(g, kv.Value)}"));
    }

    private static string Rel(BlockPos from, BlockPos to)
    {
        int dx = to.X - from.X, dz = to.Z - from.Z;
        var bits = new List<string>();
        if (dz != 0) bits.Add($"{Math.Abs(dz)}{(dz < 0 ? "N" : "S")}");
        if (dx != 0) bits.Add($"{Math.Abs(dx)}{(dx < 0 ? "W" : "E")}");
        return bits.Count == 0 ? "at the site" : string.Join(" ", bits);
    }

    /// <summary>The ground under a resolved focus: the block itself when solid, else the first solid below (≤ 4).</summary>
    public static BlockPos Ground(IBlockAccess w, BlockPos p)
    {
        if (BuildBlocks.Supports(w.GetBlock(p.X, p.Y, p.Z)) && !BuildBlocks.Supports(w.GetBlock(p.X, p.Y + 1, p.Z))) return p;
        for (int y = p.Y; y >= Math.Max(1, p.Y - 4); y--)
            if (BuildBlocks.Supports(w.GetBlock(p.X, y, p.Z))) return new BlockPos(p.X, y, p.Z);
        return p.Down();
    }

    /// <summary>A flat, free spot near <paramref name="centre"/>: solid ground with 3 air above, unprotected, the most room around it.</summary>
    public static BlockPos? AutoSite(IBlockAccess w, BlockPos centre, Func<BlockPos, int, bool>? isProtected, int radius = 10)
    {
        BlockPos? best = null;
        double bestScore = double.MinValue;
        bool Open(int x, int y, int z) => w.IsLoaded(x, z) && BuildBlocks.Supports(w.GetBlock(x, y, z)) && w.GetBlock(x, y + 1, z) == 0 && w.GetBlock(x, y + 2, z) == 0
                                          && w.GetBlock(x, y + 3, z) == 0 && isProtected?.Invoke(new BlockPos(x, y + 1, z), 0) != true;
        for (int y = centre.Y - 3; y <= centre.Y + 1; y++)
            for (int z = centre.Z - radius; z <= centre.Z + radius; z++)
                for (int x = centre.X - radius; x <= centre.X + radius; x++)
                {
                    if (!Open(x, y, z) || Math.Abs(x - centre.X) + Math.Abs(z - centre.Z) < 3) continue;
                    int room = 0;
                    for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++) if (Open(x + dx, y, z + dz)) room++;
                    double score = room * 3 - Math.Sqrt((x - centre.X) * (x - centre.X) + (z - centre.Z) * (z - centre.Z)) - Math.Abs(y - (centre.Y - 1)) * 2;
                    if (score > bestScore) { bestScore = score; best = new BlockPos(x, y, z); }
                }
        return best;
    }

    public static string ShortName(string what)
    {
        var w = what.Trim().TrimEnd('.', '!', '?');
        foreach (var lead in new[] { "build me ", "build a ", "build an ", "make me ", "make a ", "make an ", "a ", "an ", "please " })
            if (w.StartsWith(lead, StringComparison.OrdinalIgnoreCase)) w = w[lead.Length..];
        return Trim(w, 40);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    public object BillJson(BlueprintRecord r) => new
    {
        id = r.Id, name = r.Name, description = r.Description, status = r.Status, requester = r.Requester, rev = r.Rev, summary = r.Summary,
        eval = r.Eval, critique = r.Critique, cost_usd = Math.Round(r.CostUsd, 4), iterations = r.Iterations, accepted_by = r.AcceptedBy,
        site = new[] { r.Site.X, r.Site.Y, r.Site.Z }, origin = r.Origin is { } o ? new[] { o.X, o.Y, o.Z } : null,
        bill = r.Bill, dsl = r.Dsl, builds = Store.Builds(r.Id).Select(b => new { part = b.Part + 1, parts = b.Parts, owner = b.Owner, state = b.State, placed = b.Placed, total = b.Total, note = b.Note }),
        updated = r.Updated,
    };

    internal static string Json(object o) => JsonSerializer.Serialize(o);
}
