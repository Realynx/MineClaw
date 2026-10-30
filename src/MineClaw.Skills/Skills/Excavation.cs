using System.Text.Json;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// The excavation ops behind <c>excavate</c> (and the brain's <c>do dig</c>, the worker's <c>skill</c>, scripts' <c>excavate()</c>):
/// dig a box or a pattern, extend a remembered pattern to a depth or re-apply it elsewhere, learn a pattern from dug cells or a
/// rule, dig a room off a stair landing, a strip mine off a room (ores it exposes, signs per resource), seal a dig's openings,
/// torch it, list/forget sites. Every op is one deterministic C# job (<see cref="Excavator"/>) and answers in one line.
/// Live 10-30 CT 114: a worker asked for exactly this ("need scripted scan+fill of shaft wall ring … no script tool available
/// to me"); now it is a plain skill any tier can run.
/// </summary>
public static class ExcavationOps
{
    public const int MaxBoxCells = 4096;

    public static Task<SkillResult> RunAsync(SkillContext ctx, string? op, JsonElement args, CancellationToken ct) =>
        (op ?? args.Str("op") ?? "dig").Trim().ToLowerInvariant() switch
        {
            "dig" or "box" or "pattern" or "extend" => DigAsync(ctx, args, ct),
            "seal" => SealAsync(ctx, args, ct),
            "room" => RoomAsync(ctx, args, ct),
            "strip" or "strip_mine" or "branch" => StripAsync(ctx, args, ct),
            "learn" or "remember" => Task.FromResult(Learn(ctx, args)),
            "light" or "torch" => LightAsync(ctx, args, ct),
            "list" => Task.FromResult(List(ctx)),
            "forget" or "delete" => Task.FromResult(Forget(ctx, args)),
            var o => Task.FromResult(SkillResult.Fail($"excavate: unknown op '{o}' (dig|seal|room|strip|learn|light|list|forget)")),
        };

    private static ExcavateOptions Options(JsonElement a) => new()
    {
        Seal = a.Flag("seal", true), Torches = a.Flag("torches", true), Tunnel = a.Flag("tunnel", true), Block = a.Str("block"),
        MaxDigs = Math.Clamp(a.Int("max") ?? 640, 1, 2048),
    };

    // ------------------------------------------------------------------ dig

    private static async Task<SkillResult> DigAsync(SkillContext ctx, JsonElement a, CancellationToken ct)
    {
        if (Box(a) is { } box)
        {
            var cells = BuildOps.Box(box.Min, box.Max).ToList();
            if (cells.Count > MaxBoxCells) return SkillResult.Fail($"box is {cells.Count} cells; at most {MaxBoxCells} per call");
            var name = a.Str("name");
            var vol = new DigVolume(name ?? $"dig {SkillJson.Fmt(box.Min)}..{SkillJson.Fmt(box.Max)}");
            foreach (var c in cells) vol.Add(c);
            await PickaxeAsync(ctx, ct).ConfigureAwait(false);
            var rep = await new Excavator(ctx, vol, Options(a) with { KeepRegistered = name != null }).RunAsync(ct).ConfigureAwait(false);
            if (name != null)
                Save(ctx, new DigSite { Name = name, Kind = "box", Dimension = ctx.World.Dimension, Cells = vol.Cells.Where(c => Clear(ctx.World, c)).ToList() });
            var size = $"{box.Max.X - box.Min.X + 1}×{box.Max.Z - box.Min.Z + 1}×{box.Max.Y - box.Min.Y + 1}";
            return Result(rep, $"dug box {size} {Span(box.Min, box.Max)}");
        }
        return await PatternAsync(ctx, a, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A pattern dig: <c>rule</c> + <c>at</c> (new), or a remembered <c>site</c> extended from its last repeat (<c>to</c> = the
    /// y its lowest cells reach, or <c>repeats</c> more), or that site re-applied <c>at</c> another anchor (<c>turn</c> the whole
    /// pattern, saved as <c>name</c>). Repeats are dug strictly in order (a stair never digs ahead) and the site remembers how far
    /// it got.
    /// </summary>
    private static async Task<SkillResult> PatternAsync(SkillContext ctx, JsonElement a, CancellationToken ct)
    {
        var w = ctx.World;
        DigSite? site = null;
        DigPattern pattern;
        string name;
        int from = 0;
        if (a.Str("rule") is { Length: > 0 } rule)
        {
            var at = a.Pos("at") ?? ctx.Me;
            if (DigPattern.Parse(rule, at, out var err) is not { } p) return SkillResult.Fail($"rule: {err}");
            pattern = p;
            name = a.Str("name") ?? $"pattern at {SkillJson.Fmt(at)}";
            if (ctx.Registry.Sites.Find(name) is { Rule: { } r0 } old && old.Anchor == at && r0 == p.Rule) { site = old; from = old.Repeats; }
        }
        else if (SiteArg(a) is { Length: > 0 } sname)
        {
            site = Resolve(ctx, sname, out var why);
            if (site is null) return SkillResult.Fail(why!);
            if (site.Pattern is not { } p) return SkillResult.Fail($"{site.Name} is a {site.Kind}, not a pattern (learn one with op=learn)");
            if (a.Pos("at") is { } at)
            {
                pattern = p.At(at, a.Int("turn") ?? 0);
                name = a.Str("name") ?? $"{site.Name} at {SkillJson.Fmt(at)}";
                site = ctx.Registry.Sites.Find(name) is { } again && again.Anchor == at ? again : null;
                from = site?.Repeats ?? 0;
            }
            else { pattern = p; name = site.Name; from = site.Repeats; }
        }
        else return SkillResult.Fail("excavate needs box (x0,y0,z0,x1,y1,z1), site (a remembered dig) or rule+at");

        int n;
        if (a.Int("to") is { } y)
        {
            if (pattern.RepeatsTo(y) is not { } k) return SkillResult.Fail($"{name} doesn't go up or down; say repeats:N");
            n = k;
        }
        else n = from + Math.Clamp(a.Int("repeats") ?? 4, 1, 256);
        n = Math.Min(n, from + 256);
        if (n <= from) return SkillResult.Success($"{name} already reaches that far ({from} repeats, lowest y{LowestY(pattern, from)})");

        var vol = new DigVolume(name) { Ordered = true };
        foreach (var (c, k) in pattern.Indexed(from, n - from)) vol.Add(c, k);
        await PickaxeAsync(ctx, ct).ConfigureAwait(false);
        var ex = new Excavator(ctx, vol, Options(a) with { KeepRegistered = true });
        var rep = await ex.RunAsync(ct).ConfigureAwait(false);
        int done = from;
        while (done < n && pattern.Repeat(done).All(c => Clear(w, c))) done++;
        var saved = (site ?? new DigSite { Name = name, Kind = "pattern", Dimension = w.Dimension }) with
        {
            Rule = pattern.Rule, Anchor = pattern.Anchor, Repeats = done, Dimension = w.Dimension, At = DateTimeOffset.UtcNow,
        };
        Save(ctx, saved, extra: vol.Cells.Where(c => Clear(w, c)));
        var what = $"{name} repeats {from}→{done} of {n} (down to y{LowestY(pattern, done)})";
        return Result(rep, what);
    }

    // ------------------------------------------------------------------ room / strip

    /// <summary><c>room{site, y?, size?, side?, gap?, name?}</c>: a W×D×H room off the site's landing at y (my feet by default).</summary>
    private static async Task<SkillResult> RoomAsync(SkillContext ctx, JsonElement a, CancellationToken ct)
    {
        var w = ctx.World;
        var parent = Parent(ctx, a, out var why);
        if (parent is null) return SkillResult.Fail(why!);
        var s = parent.Air();
        s.UnionWith(w.KeepAir.Cells(DigSites.Owner(parent)));
        int y = a.Int("y") ?? ctx.Me.Y;
        var (wd, dp, ht) = Size(a.Str("size"), 4, 4, 3);
        int gap = Math.Clamp(a.Int("gap") ?? 1, 0, 3);
        var side = MinePlanner.ParseDir(a.Str("side"));
        var name = a.Str("name") ?? $"{parent.Name} room y{y}";
        var (plan, err) = MinePlanner.Room(w, s, y, wd, dp, ht, gap, side, ctx.Me, ctx.Protected, owner: name);
        if (plan is null) return SkillResult.Fail(err!);
        var vol = new DigVolume(name);
        foreach (var c in plan.Door) vol.Add(c);
        foreach (var c in plan.Room) vol.Add(c);
        await PickaxeAsync(ctx, ct).ConfigureAwait(false);
        var rep = await new Excavator(ctx, vol, Options(a) with { KeepRegistered = true }).RunAsync(ct).ConfigureAwait(false);
        Save(ctx, new DigSite
        {
            Name = name, Kind = "room", Parent = parent.Name, Dimension = w.Dimension, Cells = vol.Cells.ToList(),
            Note = $"{plan.Side} of landing {SkillJson.Fmt(plan.Landing)}, {(gap > 0 ? $"1×2 door through {gap}" : "open to the stair")}",
        });
        var door = gap > 0 ? $"door 1×2 through the wall at {SkillJson.Fmt(plan.Door[0])}" : "open onto the stair";
        return Result(rep, $"room {wd}×{dp}×{ht} {plan.Box}, {plan.Side} of the landing {SkillJson.Fmt(plan.Landing)} ({door})");
    }

    /// <summary><c>strip{site: room, dir?, length?, spacing?, branch?, height?, signs?}</c>: a main corridor and side branches off a
    /// room, ores the corridors expose dug (the vein followed), lit; <c>signs</c> = a sign per ore type on the room wall.</summary>
    private static async Task<SkillResult> StripAsync(SkillContext ctx, JsonElement a, CancellationToken ct)
    {
        var w = ctx.World;
        var room = Parent(ctx, a, out var why);
        if (room is null) return SkillResult.Fail(why!);
        var cells = room.Air();
        BlockPos? away = null;
        if (room.Parent is { } pn && ctx.Registry.Sites.Find(pn) is { } shaft && shaft.Air() is { Count: > 0 } sa && cells.Count > 0)
        {
            var (slo, shi) = DigVolume.Bounds(sa);
            var (rlo, rhi) = DigVolume.Bounds(cells);
            double dx = (rlo.X + rhi.X - slo.X - shi.X) / 2.0, dz = (rlo.Z + rhi.Z - slo.Z - shi.Z) / 2.0;
            away = Math.Abs(dx) >= Math.Abs(dz) ? new BlockPos(Math.Sign(dx) is 0 ? 1 : Math.Sign(dx), 0, 0) : new BlockPos(0, 0, Math.Sign(dz));
        }
        var dir = MinePlanner.ParseDir(a.Str("dir") ?? a.Str("side"));
        var name0 = a.Str("name");
        var (plan, err) = MinePlanner.Strip(w, cells, dir, away, Math.Clamp(a.Int("length") ?? 16, 2, 64), Math.Clamp(a.Int("spacing") ?? 3, 1, 8),
            Math.Clamp(a.Int("branch") ?? 8, 0, 32), Math.Clamp(a.Int("height") ?? 2, 2, 3), name0 ?? $"{room.Name} strip");
        if (plan is null) return SkillResult.Fail(err!);
        var name = name0 ?? $"{room.Name} strip {MinePlanner.DirName(plan.Dir)}";
        var vol = new DigVolume(name);
        foreach (var (c, k) in plan.Cells) vol.Add(c, k);
        await PickaxeAsync(ctx, ct).ConfigureAwait(false);
        var rep = await new Excavator(ctx, vol, Options(a) with { KeepRegistered = true, Ores = true }).RunAsync(ct).ConfigureAwait(false);
        int floor = cells.Count > 0 ? cells.Min(c => c.Y) : plan.Mouth.Y;
        string signs = "";
        if (a.Flag("signs") && rep.Ores.Count > 0) signs = await SignsAsync(ctx, cells, plan.Mouth, rep.Ores, floor, ct).ConfigureAwait(false);
        var ores = rep.Ores.Count == 0 ? "no ores" : string.Join(", ", rep.Ores.OrderByDescending(o => o.Value).Select(o => $"{o.Key} {o.Value}"));
        Save(ctx, new DigSite
        {
            Name = name, Kind = "strip", Parent = room.Name, Dimension = w.Dimension, Cells = vol.Cells.Where(c => Clear(w, c)).ToList(),
            Note = $"{ores} at y{floor}",
        });
        var r = Result(rep, $"strip mine {MinePlanner.DirName(plan.Dir)} off {room.Name}: main {plan.Length}, {plan.Branches} branches");
        return signs.Length > 0 ? r with { Text = r.Text + "; " + signs } : r;
    }

    /// <summary>A sign per resource type on the room's walls near the corridor mouth: "iron ore / x5 / y12". Signs through the
    /// Obtainer (crafted from planks and a stick when there are none).</summary>
    private static async Task<string> SignsAsync(SkillContext ctx, IReadOnlySet<BlockPos> room, BlockPos mouth, Dictionary<string, int> ores, int floor, CancellationToken ct)
    {
        if ((ctx.Game.Supported & Primitive.WriteSign) == 0) return "signs: this client can't write sign text";
        var kinds = ores.OrderByDescending(o => o.Value).Take(6).ToList();
        if (ctx.Count(Items.Sign) < kinds.Count && ctx.Registry.Obtain is { } obtain)
        {
            try { await obtain(ctx, Items.Sign, kinds.Count, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { ctx.Log?.Invoke($"strip signs: {e.Message}"); }
        }
        var w = ctx.World;
        var spots = room.Where(c => c.Y == floor + 1 && w.GetBlock(c) == 0)
            .Select(c => (Pos: c, Faces: PlaceRules.AttachFaces(w, c, Items.Sign).Where(f => f != BlockFace.Top).ToList()))
            .Where(s => s.Faces.Count > 0).OrderBy(s => s.Pos.DistanceSquaredTo(mouth)).ToList();
        int placed = 0;
        foreach (var (kind, count) in kinds)
        {
            if (!ctx.Has(Items.Sign) || spots.Count == 0) break;
            var (pos, faces) = spots[0];
            spots.RemoveAt(0);
            await ctx.ReachAsync(pos, ct).ConfigureAwait(false);
            var r = await ctx.Game.PlaceAsync(pos, Items.Sign, faces[0], ct).ConfigureAwait(false);
            if (!r.Ok) continue;
            var text = await ctx.Game.WriteSignAsync(pos, [kind, $"x{count}", $"y{floor}", ""], ct).ConfigureAwait(false);
            if (text.Ok) placed++;
        }
        return placed == kinds.Count ? $"signs: {placed} ({string.Join(", ", kinds.Select(k => k.Key))})"
            : $"signs: {placed} of {kinds.Count}{(ctx.Has(Items.Sign) ? " (no wall spot left)" : " (out of signs)")}";
    }

    // ------------------------------------------------------------------ seal / light / learn / list

    private static async Task<SkillResult> SealAsync(SkillContext ctx, JsonElement a, CancellationToken ct)
    {
        var (vol, label, entrance, why) = Existing(ctx, a);
        if (vol is null) return SkillResult.Fail(why!);
        if (Box(a, "skip") is { } skip) vol.Skip.UnionWith(BuildOps.Box(skip.Min, skip.Max));
        var rep = await new Excavator(ctx, vol, Options(a)).SealOpeningsAsync(a.Int("above") ?? entrance, ct).ConfigureAwait(false);
        var text = $"sealed {label}: {rep.Sealed} opening{(rep.Sealed == 1 ? "" : "s")} filled";
        if (rep.Unsealed.Count > 0) text += $", {rep.Unsealed.Count} not (e.g. {SkillJson.Fmt(rep.Unsealed[0])}: no stand or no block)";
        if (rep.Stop != null) text += $"; stopped: {rep.Stop}";
        text += $"; {CountBlocks(ctx, a)} left";
        return new SkillResult(rep.Unsealed.Count == 0 && rep.Stop is null, text);
    }

    private static async Task<SkillResult> LightAsync(SkillContext ctx, JsonElement a, CancellationToken ct)
    {
        var (vol, label, _, why) = Existing(ctx, a);
        if (vol is null) return SkillResult.Fail(why!);
        var rep = await new Excavator(ctx, vol, Options(a)).LightOnlyAsync(ct).ConfigureAwait(false);
        var cells = vol.Cells.Where(c => Clear(ctx.World, c)).ToList();
        int dark = cells.Count(c => ctx.World.CanHostileSpawn(c));
        return new SkillResult(dark == 0, $"lit {label}: {rep.Torches} torch{(rep.Torches == 1 ? "" : "es")}, {dark} spawnable cells left"
            + (rep.Stop is null ? "" : $"; {rep.Stop}") + $"; {ctx.Count(Blocks.Torch)} torches left");
    }

    /// <summary><c>learn{name, box|design|rule+at, repeats?}</c>: a pattern from the dug cells in a box (the stair Fox demonstrated)
    /// or a design's box, or from a rule; saved as a site, its air kept clear from now on.</summary>
    private static SkillResult Learn(SkillContext ctx, JsonElement a)
    {
        var w = ctx.World;
        var name = a.Str("name") ?? SiteArg(a);
        if (string.IsNullOrWhiteSpace(name)) return SkillResult.Fail("learn needs a name");
        if (a.Str("rule") is { Length: > 0 } rule)
        {
            var at = a.Pos("at") ?? ctx.Me;
            if (DigPattern.Parse(rule, at, out var err) is not { } p) return SkillResult.Fail($"rule: {err}");
            int dug = 0;
            while (dug < 256 && p.Repeat(dug).All(c => Clear(w, c))) dug++;
            dug = a.Int("repeats") ?? dug;
            var site = new DigSite { Name = name, Kind = "pattern", Rule = p.Rule, Anchor = p.Anchor, Repeats = dug, Dimension = w.Dimension };
            Save(ctx, site);
            return SkillResult.Success($"remembered '{name}': {p.Rule} from {SkillJson.Fmt(at)}, {dug} repeats already dug; extend with to:Y");
        }
        (BlockPos Min, BlockPos Max)? box = Box(a);
        int? design = null;
        if (box is null && (a.Str("design") ?? SiteArg(a)) is { } dn && ctx.Registry.Design?.Invoke(dn.Trim().TrimStart('#')) is { } d)
        {
            box = (d.Min, d.Max);
            design = DesignId(d.Label);
        }
        if (box is not { } b) return SkillResult.Fail("learn needs box (x0,y0,z0,x1,y1,z1), design or rule");
        var learned = LearnBox(w, b.Min, b.Max, out var why);
        if (learned is null) return SkillResult.Fail(why!);
        Save(ctx, new DigSite
        {
            Name = name, Kind = "pattern", Rule = learned.Pattern.Rule, Anchor = learned.Pattern.Anchor, Repeats = learned.Repeats,
            Dimension = w.Dimension, Design = design, Note = learned.Outliers.Count > 0 ? $"{learned.Outliers.Count} dug cells off the pattern" : null,
        });
        return SkillResult.Success($"learned '{name}': {learned.Summary}; extend with to:Y, or at:x,y,z for a new one");
    }

    /// <summary>The pattern behind the dug cells (air, nothing liquid) of a box; null + why.</summary>
    public static DigPattern.Learned? LearnBox(WorldState w, BlockPos min, BlockPos max, out string? why)
    {
        why = null;
        var cells = BuildOps.Box(min, max).Where(c => w.IsLoaded(c.X, c.Z) && w.GetBlock(c) == 0).ToHashSet();
        if (cells.Count < 3) { why = $"{Span(min, max)} has {cells.Count} dug cells: nothing to learn"; return null; }
        if (cells.Count > 8192) { why = "too many dug cells to learn from (8192 max): give a tighter box"; return null; }
        var l = DigPattern.Learn(cells);
        if (l is null) why = $"no repeating unit in the {cells.Count} dug cells of {Span(min, max)}; write it as a rule (column 3 step 0,-1,0 turn -90 pivot 0.5,0.5)";
        return l;
    }

    private static SkillResult List(SkillContext ctx)
    {
        var all = ctx.Registry.Sites.All();
        if (all.Count == 0) return SkillResult.Success("no remembered digs");
        return SkillResult.Success(string.Join("; ", all.OrderByDescending(s => s.At).Take(6).Select(s => s.Describe())));
    }

    private static SkillResult Forget(SkillContext ctx, JsonElement a)
    {
        var name = SiteArg(a) ?? a.Str("name");
        if (name is null || ctx.Registry.Sites.Find(name) is not { } s) return SkillResult.Fail($"no remembered dig '{name}'");
        ctx.Registry.Sites.Delete(s.Name);
        ctx.World.KeepAir.Remove(DigSites.Owner(s));
        return SkillResult.Success($"forgot {s.Name}; its cells may be filled again");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The target of seal/light: a site, else a box's open cells. Entrance = a pattern's first repeat's top (openings at
    /// or above it are the way in).</summary>
    private static (DigVolume? Vol, string Label, int? Entrance, string? Why) Existing(SkillContext ctx, JsonElement a)
    {
        if (SiteArg(a) is { Length: > 0 } n)
        {
            var site = Resolve(ctx, n, out var why);
            if (site is null) return (null, n, null, why);
            var vol = new DigVolume(site.Name);
            foreach (var c in site.Air()) vol.Add(c);
            foreach (var c in ctx.World.KeepAir.Cells(DigSites.Owner(site))) vol.Add(c);
            int? entrance = site.Pattern is { } p ? p.Repeat(0).Max(c => c.Y) + 1 : null;
            return (vol, site.Name, entrance, null);
        }
        if (Box(a) is { } b)
        {
            var vol = new DigVolume($"box {Span(b.Min, b.Max)}");
            foreach (var c in BuildOps.Box(b.Min, b.Max).Where(c => Clear(ctx.World, c))) vol.Add(c);
            return vol.Count == 0 ? (null, "", null, "nothing dug in that box") : (vol, $"box {Span(b.Min, b.Max)}", null, null);
        }
        return (null, "", null, "needs site (a remembered dig) or box");
    }

    /// <summary>A remembered site, or a design's dug cells learned into one on first use ("extend the spiral_staircase").</summary>
    public static DigSite? Resolve(SkillContext ctx, string name, out string? why)
    {
        why = null;
        if (ctx.Registry.Sites.Find(name) is { } s) return s;
        if (ctx.Registry.Design?.Invoke(name.Trim().TrimStart('#')) is { } d)
        {
            var learned = LearnBox(ctx.World, d.Min, d.Max, out why);
            if (learned is null) return null;
            var site = new DigSite
            {
                Name = name.Trim(), Kind = "pattern", Rule = learned.Pattern.Rule, Anchor = learned.Pattern.Anchor, Repeats = learned.Repeats,
                Dimension = ctx.World.Dimension, Design = DesignId(d.Label), Note = $"learned from {d.Label}",
            };
            Save(ctx, site);
            return site;
        }
        var known = ctx.Registry.Sites.All().Select(x => x.Name).Take(5).ToList();
        why = $"no remembered dig '{name}'" + (known.Count > 0 ? $" (known: {string.Join(", ", known)})" : " (learn one: op=learn)");
        return null;
    }

    /// <summary>The site a room/strip hangs off: <c>site</c>, else the dig whose cells I stand in or next to.</summary>
    private static DigSite? Parent(SkillContext ctx, JsonElement a, out string? why)
    {
        why = null;
        if (SiteArg(a) is { Length: > 0 } n) return Resolve(ctx, n, out why);
        var me = ctx.Me;
        foreach (var p in new[] { me, me.Up(), me.Down() }.Concat(SkillWorld.Horizontal.Select(o => me + o)))
            if (ctx.World.KeepAir.Owner(p, ctx.World.Dimension) is { } owner && ctx.Registry.Sites.Find(owner) is { } s) return s;
        why = "which dig? say site:<name> (op=list shows them)";
        return null;
    }

    private static string? SiteArg(JsonElement a) => a.Str("site") ?? a.Str("pattern") ?? a.Str("of") ?? a.Str("from");

    private static void Save(SkillContext ctx, DigSite site, IEnumerable<BlockPos>? extra = null)
    {
        ctx.Registry.Sites.Save(site);
        var air = site.Air();
        if (extra != null) air.UnionWith(extra);
        ctx.World.KeepAir.Set(DigSites.Owner(site), site.Dimension, air);
    }

    /// <summary>A pickaxe before digging rock: the acquisition planner (inventory, chests, crafting) when there's none.</summary>
    private static async Task PickaxeAsync(SkillContext ctx, CancellationToken ct)
    {
        var picks = Items.All.Where(i => i.Tool == ToolType.Pickaxe && Items.Exists(i.Id)).Select(i => i.Id).ToArray();
        if (picks.Length == 0 || picks.Any(id => ctx.Has(id)) || ctx.Registry.AcquireMissing is not { } acquire) return;
        try { await acquire(ctx, Spec, picks, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { ctx.Log?.Invoke($"excavate pickaxe: {e.Message}"); }
    }

    internal static readonly SkillSpec Spec = new() { Name = "excavate", Category = SkillCategory.Building, Summary = "excavate" };

    private static SkillResult Result(ExcavateReport rep, string what) =>
        new(rep.Dug > 0 && rep.Stop is null || rep.Ok, rep.Text(what));

    private static bool Clear(WorldState w, BlockPos c)
    {
        int id = w.GetBlock(c);
        return id == 0 || !BlockShapes.IsLiquid(id) && !Blocks.Get(id).Solid;
    }

    private static int LowestY(DigPattern p, int repeats) => repeats <= 0 ? p.Repeat(0).Min(c => c.Y) : p.Repeat(repeats - 1).Min(c => c.Y);

    private static string CountBlocks(SkillContext ctx, JsonElement a) =>
        SkillWorld.BuildingBlock(ctx, a.Str("block")) is { } b ? $"{ctx.Count(b)} {SkillWorld.Name(b)}" : "no building blocks";

    private static int? DesignId(string label) =>
        System.Text.RegularExpressions.Regex.Match(label, @"#(\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    public static string Span(BlockPos a, BlockPos b) => $"x{a.X}..{b.X} y{a.Y}..{b.Y} z{a.Z}..{b.Z}";

    /// <summary>"4x4" / "4x4x3" → (width, depth, height).</summary>
    public static (int W, int D, int H) Size(string? s, int w, int d, int h)
    {
        if (string.IsNullOrWhiteSpace(s)) return (w, d, h);
        var v = s.ToLowerInvariant().Split(['x', '×', '*', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out int n) ? n : -1).ToArray();
        if (v.Length >= 2 && v.All(n => n is >= 1 and <= 16)) return (v[0], v[1], v.Length >= 3 ? v[2] : h);
        return (w, d, h);
    }

    /// <summary>A box from "x0,y0,z0,x1,y1,z1", [6 numbers] or from/to positions.</summary>
    public static (BlockPos Min, BlockPos Max)? Box(JsonElement a, string key = "box")
    {
        int[]? v = null;
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var b))
        {
            if (b.ValueKind == JsonValueKind.String)
            {
                var parts = b.GetString()!.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
                var ints = parts.Select(p => int.TryParse(p, out int n) ? (int?)n : null).ToArray();
                if (ints.Length == 6 && ints.All(i => i != null)) v = ints.Select(i => i!.Value).ToArray();
            }
            else if (b.ValueKind == JsonValueKind.Array)
            {
                var ints = b.EnumerateArray().Select(n => n.TryGetInt32(out int i) ? (int?)i : null).ToArray();
                if (ints.Length == 6 && ints.All(i => i != null)) v = ints.Select(i => i!.Value).ToArray();
            }
        }
        if (v is null && key == "box" && a.Pos("from") is { } f && a.Pos("to") is { } t) v = [f.X, f.Y, f.Z, t.X, t.Y, t.Z];
        if (v is null) return null;
        return (new BlockPos(Math.Min(v[0], v[3]), Math.Min(v[1], v[4]), Math.Min(v[2], v[5])),
                new BlockPos(Math.Max(v[0], v[3]), Math.Max(v[1], v[4]), Math.Max(v[2], v[5])));
    }
}

/// <summary>
/// <c>excavate{op?, box|site|rule, …}</c>: dig underground without the LLM placing a single cell (see <see cref="ExcavationOps"/>).
/// The brain's <c>do dig{…}</c> is this skill.
/// </summary>
[Skill]
public sealed class ExcavateSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "excavate", Category = SkillCategory.Building, Summary = "dig a box/stair pattern; seal caves, torch, never fill it",
        Args = "op?:dig|seal|room|strip|learn|light|list,box?|site?|rule?,at?,to?,name?",
        Schema = SkillJson.Schema("op:str?,box:str?,site:str?,rule:str?,at:pos?,to:int?,repeats:int?,turn:int?,name:str?,size:str?,y:int?,side:str?,gap:int?,dir:str?,length:int?,spacing:int?,branch:int?,height:int?,signs:bool?,seal:bool?,torches:bool?,tunnel:bool?,block:str?,skip:str?,design:str?"),
        Safety = SafetyClass.Destructive, Estimate = TimeSpan.FromSeconds(120),
        Keywords = "dig excavate tunnel underground mine shaft stairs staircase spiral stair down level diamond room cave hole extend deeper dig down quarry",
        Aliases = ["dig", "dig_box", "dig_down", "tunnel", "dig_stairs", "mine_shaft"],
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        ExcavationOps.RunAsync(ctx, null, args, ct);
}

/// <summary><c>seal_openings{site|box, skip?, block?}</c> = excavate op=seal: fill a dig's openings into caves and dungeons.</summary>
[Skill]
public sealed class SealOpeningsSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "seal_openings", Category = SkillCategory.Building, Summary = "fill cave/dungeon holes in a dig's walls, not doorways",
        Args = "site?|box?,skip?,block?", Schema = SkillJson.Schema("site:str?,box:str?,skip:str?,block:str?,above:int?"),
        Estimate = TimeSpan.FromSeconds(60), Pre = new() { Needs = "blocks" },
        Keywords = "seal holes wall walls fill gaps openings cave dungeon stair shaft patch plug close up",
        Aliases = ["seal", "seal_walls", "fill_holes"],
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        ExcavationOps.RunAsync(ctx, "seal", args, ct);
}

/// <summary><c>dig_room{site, y?, size?, side?, gap?}</c> = excavate op=room: a room off a stair landing that connects to it.</summary>
[Skill]
public sealed class DigRoomSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "dig_room", Category = SkillCategory.Building, Summary = "dig a WxD room off a stair landing, doorway to it",
        Args = "site?,y?,size?:4x4x3,side?,gap?:0|1", Schema = SkillJson.Schema("site:str?,y:int?,size:str?,side:str?,gap:int?,name:str?,seal:bool?,torches:bool?"),
        Safety = SafetyClass.Destructive, Estimate = TimeSpan.FromSeconds(90),
        Keywords = "room chamber 4x4 dig out hollow underground off the stairs landing mine room base",
        Aliases = ["mine_room"],
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        ExcavationOps.RunAsync(ctx, "room", args, ct);
}

/// <summary><c>strip_mine{site: room, dir?, length?, spacing?, branch?, signs?}</c> = excavate op=strip.</summary>
[Skill]
public sealed class StripMineSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "strip_mine", Category = SkillCategory.Building, Summary = "branch corridors off a room, mine exposed ores",
        Args = "site?,dir?,length?,spacing?,branch?,signs?", Schema = SkillJson.Schema("site:str?,dir:str?,length:int?,spacing:int?,branch:int?,height:int?,signs:bool?,name:str?"),
        Safety = SafetyClass.Destructive, Estimate = TimeSpan.FromSeconds(180),
        Keywords = "strip mine branch mining corridors labyrinth ores iron coal diamond tunnels signs resources level",
        Aliases = ["branch_mine"],
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        ExcavationOps.RunAsync(ctx, "strip", args, ct);
}
