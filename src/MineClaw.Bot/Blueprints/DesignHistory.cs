using System.Globalization;
using MineClaw.Agent.Memory;
using MineClaw.World;
using MineClaw.World.Designs;
using MPos = MineClaw.Agent.Memory.BlockPos;
using BlockPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// Git-style history for saved designs (ARCHITECTURE.md "Design revisions"). Fox, 10-30: "Maybe this should be similar to Git
/// style, so that we can detect changes and do revisions and regressions." Every save or capture is a revision (r1, r2…: who,
/// why, when, the DSL and its block set; HEAD = the newest); <c>design log/diff/status/commit/revert</c> read and move it.
/// <list type="bullet">
/// <item><b>commit</b>: HEAD plus what the world shows changed (a player's deliberate edit, or the bot's requested one) becomes
/// the next revision, its DSL drawn as it stands (<see cref="StructureCapture"/>'s drawing: raw metas, <c>facing south</c>).</item>
/// <item><b>revert</b> ("rebuild r2", "put it back", "undo the last revision"): r2's block set becomes the next revision (history
/// is never rewritten) and the ordinary construction executor builds it in place: what r2 lacks is drawn as forced air (dug),
/// what it has and the world lacks is placed, through the gate-approved plan and the protection rules (its own job edits it).</item>
/// </list>
/// Linear only (no branches); a revision can carry a tag ("good"). Both versions: the snapshot holds the version's own ids/metas.
/// </summary>
public sealed class DesignHistory(BlueprintService service, DesignRevisions revisions, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public DesignRevisions Revisions => revisions;
    /// <summary>The bot's chunk view (live, or remembered: <see cref="WorldState.PlanningView"/>), or null (no world: no diff).</summary>
    public Func<IBlockAccess?> World { get; set; } = () => null;
    /// <summary>The editing context (a trusted touch opens the design for requested edits).</summary>
    public EditContext? Edits { get; set; }
    /// <summary>Memory (the linked structure's box follows a revision that grows or shrinks the design).</summary>
    public MemoryStore? Memory { get; set; }
    /// <summary>The bot's name (author of its own revisions).</summary>
    public Func<string> BotName { get; set; } = () => "bot";
    /// <summary>What the drift watcher knows about who changed which cell ("Fox 2, me 1"), for status/commit lines.</summary>
    public Func<long, DesignDiff, string?>? Causes { get; set; }
    /// <summary>Whether every drifted cell was the hive's own doing (a commit of the bot's own requested edit needs no trusted word).</summary>
    public Func<long, DesignDiff, bool>? AllOwn { get; set; }

    // ------------------------------------------------------------------ snapshots and saves

    /// <summary>The design's block set as its DSL builds it (gate corrections applied), plus the world's air around it when given.</summary>
    public DesignSnapshot? SnapshotOf(BlueprintRecord rec, IBlockAccess? world = null)
    {
        if (string.IsNullOrWhiteSpace(rec.Dsl)) return null;
        VoxelPlan? plan;
        try { plan = service.PlanOf(rec, out _); } catch (Exception) { return null; }
        if (plan == null) return null;
        var built = service.AsBuilt(rec, plan);
        var s = new DesignSnapshot();
        foreach (var b in built.Blocks.Values) s.Blocks[b.Pos] = (b.Id, b.Meta);
        foreach (var p in built.Air) if (!s.Blocks.ContainsKey(p)) s.Air.Add(p);
        if (world != null) s.AddWorldAir(world);
        return s;
    }

    /// <summary>Migration: a design saved before revisions becomes r1 (its current DSL; author its requester or the bot) the first
    /// time anything looks at its history. Null = nothing to keep (no DSL yet, or it doesn't parse).</summary>
    public DesignRevision? Ensure(BlueprintRecord rec)
    {
        if (revisions.Head(rec.Id) is { } head) return head;
        if (rec.Status == DesignStatus.Deleted || SnapshotOf(rec) is not { } snap) return null;
        bool captured = rec.Status == DesignStatus.Captured;
        return revisions.Add(rec.Id, snap, rec.Dsl, rec.Origin, rec.Dim, rec.Requester ?? (rec.Bot.Length > 0 ? rec.Bot : BotName()),
            captured ? RevisionAuthor.Player : RevisionAuthor.Bot, captured ? "captured (saved before revisions)" : "saved before revisions",
            at: rec.Updated == default ? null : rec.Updated);
    }

    /// <summary>
    /// A save: the design's current DSL as the next revision when it differs from HEAD (a design version, a capture, a copy).
    /// <paramref name="blocksOnly"/> (a finished build): only when the block set itself changed (a gate correction moved it).
    /// </summary>
    public DesignRevision? Sync(BlueprintRecord rec, string author, string kind, string message, IBlockAccess? world = null, bool blocksOnly = false)
    {
        try
        {
            if (SnapshotOf(rec, world) is not { } snap) return null;
            var head = revisions.Head(rec.Id);
            if (head != null && head.Dsl == rec.Dsl && head.Origin == rec.Origin && (blocksOnly || head.Hash == snap.Hash())) return head;
            if (head != null && blocksOnly && revisions.Snapshot(rec.Id, head.Rev) is { } hs && DesignDiff.Between(hs, snap).Empty) return head;
            return revisions.Add(rec.Id, snap, rec.Dsl, rec.Origin, rec.Dim, author, kind, message);
        }
        catch (Exception) { return null; } // history is best effort: a save never fails on it
    }

    // ------------------------------------------------------------------ reading

    /// <summary>"#12 spiral_staircase: r3 Fox 2m ago 'added a landing' [good] · r2 … · r1 …" (newest first).</summary>
    public string Log(BlueprintRecord rec, int max = 6)
    {
        if (Ensure(rec) == null) return $"#{rec.Id} \"{rec.Name}\" has no revisions (no design saved yet)";
        var now = _time.GetUtcNow();
        var list = revisions.List(rec.Id, max + 1);
        return $"#{rec.Id} {rec.Name}: " + string.Join(" · ", list.Take(max).Select(r => r.Line(now))) + (list.Count > max ? " · …" : "");
    }

    /// <summary>A revision against the world (the bot's chunk view): null when there is no history or no world.</summary>
    public (DesignRevision Rev, DesignDiff Diff)? AgainstWorld(BlueprintRecord rec, string? spec = null)
    {
        if (Ensure(rec) == null || World() is not { } w) return null;
        if (revisions.Resolve(rec.Id, spec) is not { } r || revisions.Snapshot(rec.Id, r.Rev) is not { } snap) return null;
        return (r, DesignDiff.Against(snap, w));
    }

    /// <summary>"#12 spiral_staircase at r2 (Fox 5m ago 'captured'); world: −3 stone (Fox 2, me 1)" / "…; world matches r2".</summary>
    public string Status(BlueprintRecord rec)
    {
        if (Ensure(rec) is not { } head) return $"#{rec.Id} \"{rec.Name}\" has no revisions";
        var line = $"#{rec.Id} {rec.Name} at {head.Line(_time.GetUtcNow())}";
        if (AgainstWorld(rec) is not { } aw) return line + "; world not in view";
        var d = aw.Diff;
        var unk = d.Unknown > 0 ? $" ({d.Unknown} cells not loaded)" : "";
        if (d.Empty) return line + (d.Unknown >= head.Blocks ? "; world not in view (its chunks aren't loaded)" : $"; world matches r{head.Rev}" + unk);
        var who = Causes?.Invoke(rec.Id, d) is { Length: > 0 } c ? $" ({c})" : "";
        return line + $"; world: {d.Text()}{who}{unk}; commit{{id={rec.Id}}} keeps it, revert{{id={rec.Id}}} restores r{head.Rev}";
    }

    /// <summary>
    /// <c>diff{id, what}</c>: nothing = HEAD vs the world; "r2" = r2 vs the world; "r1 r3" / "r1..r3" = between them (older →
    /// newer): "r1→r3: +12 stone, −3 planks, ~2 oak stairs facing; 154,40,276 −stone; …".
    /// </summary>
    public string Diff(BlueprintRecord rec, string? what)
    {
        if (Ensure(rec) == null) return $"#{rec.Id} \"{rec.Name}\" has no revisions";
        var parts = (what ?? "").Replace("..", " ").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.Equals("vs", StringComparison.OrdinalIgnoreCase) && !p.Equals("world", StringComparison.OrdinalIgnoreCase)).ToList();
        if (parts.Count >= 2)
        {
            if (revisions.Resolve(rec.Id, parts[0]) is not { } a || revisions.Resolve(rec.Id, parts[1]) is not { } b) return $"no such revision ({what}); design log{{id={rec.Id}}} lists them";
            if (a.Rev > b.Rev) (a, b) = (b, a);
            var d = DesignDiff.Between(revisions.Snapshot(rec.Id, a.Rev)!, revisions.Snapshot(rec.Id, b.Rev)!);
            return $"r{a.Rev}→r{b.Rev}: {d.Text()}" + (d.Empty ? "" : "; " + d.Cells(6));
        }
        var spec = parts.FirstOrDefault();
        if (revisions.Resolve(rec.Id, spec) is not { } r) return $"no such revision '{spec}'; design log{{id={rec.Id}}} lists them";
        if (AgainstWorld(rec, spec) is not { } aw) return $"r{r.Rev}: the world isn't in view (come closer)";
        var dw = aw.Diff;
        var who = !dw.Empty && Causes?.Invoke(rec.Id, dw) is { Length: > 0 } c ? $" ({c})" : "";
        return $"r{r.Rev}→world: {dw.Text()}{who}" + (dw.Empty ? "" : "; " + dw.Cells(6)) + (dw.Unknown > 0 ? $"; {dw.Unknown} not loaded" : "");
    }

    // ------------------------------------------------------------------ commit

    /// <summary>
    /// HEAD plus what the world shows changed → the next revision ("Fox added a landing": a trusted player's word, or the bot's
    /// own requested edit). A griefer's change is never committed on its own: an untrusted speaker can't commit, and the drift
    /// watcher only offers it. A message starting with '#' ("#good") tags the new revision.
    /// </summary>
    public (bool Ok, string Text) Commit(BlueprintRecord rec, string? by, string? message)
    {
        if (Ensure(rec) is not { } head || revisions.Snapshot(rec.Id, head.Rev) is not { } snap) return (false, $"#{rec.Id} has no revisions to commit onto");
        if (World() is not { } w) return (false, "the build isn't in view: come closer so I can see what changed");
        var d = DesignDiff.Against(snap, w);
        if (d.Empty) return (false, $"nothing to commit: the world matches r{head.Rev}" + (d.Unknown > 0 ? $" ({d.Unknown} cells not loaded)" : ""));
        bool trusted = Edits?.IsTrusted(by) ?? service.IsTrusted(by);
        if (!trusted && !(by == null && AllOwn?.Invoke(rec.Id, d) == true))
            return (false, $"committing #{rec.Id} needs a trusted player's word ({by ?? "nobody"} asked); the change stays uncommitted ({d.Text()})");
        var who = Causes?.Invoke(rec.Id, d);
        var next = snap.Clone();
        int skipped = 0;
        foreach (var c in d.Removed) { next.Blocks.Remove(c.Pos); if (c.ToId == 0) next.Air.Add(c.Pos); }
        foreach (var c in d.Added.Concat(d.Changed))
        {
            var (cls, id, meta, _) = StructureCapture.Classify(w, c.Pos, null);
            next.Air.Remove(c.Pos);
            if (cls == StructureCapture.CellClass.Build) next.Blocks[c.Pos] = (id, meta);
            else { next.Blocks.Remove(c.Pos); skipped++; } // a bed, a sign, terrain: not a design block (don't-care from now on)
        }
        next.AddWorldAir(w);
        var (tag, msg) = TagOf(message);
        var text = msg ?? (who is { Length: > 0 } ? $"world changes ({who})" : "world changes");
        return Save(rec, next, InteriorAir(next), by ?? BotName(), trusted ? RevisionAuthor.Player : RevisionAuthor.Bot, text, tag,
            r => $"committed #{rec.Id} {rec.Name} as r{r.Rev}: {d.Text()}" + (skipped > 0 ? $" ({skipped} not buildable, left out)" : ""));
    }

    private static (string? Tag, string? Message) TagOf(string? message)
    {
        var m = message?.Trim();
        if (string.IsNullOrEmpty(m)) return (null, null);
        if (m.StartsWith('#'))
        {
            var sp = m.IndexOf(' ');
            return sp < 0 ? (m[1..], null) : (m[1..sp], m[(sp + 1)..].Trim() is { Length: > 0 } rest ? rest : null);
        }
        return (null, m);
    }

    // ------------------------------------------------------------------ revert

    /// <summary>
    /// "rebuild r2" / "put it back the way it was" / "undo the last revision": the target's block set becomes the next revision
    /// and is built in place by the construction executor (a trusted player's word; its gate is approved by them): blocks the
    /// target lacks are drawn as forced air and dug, missing ones placed, wrong ones swapped. Default target: the one before HEAD.
    /// </summary>
    public (bool Ok, string Text) Revert(BlueprintRecord rec, string? what, string? by)
    {
        if (!(Edits?.IsTrusted(by) ?? service.IsTrusted(by)))
            return (false, $"reverting #{rec.Id} needs a trusted player's word ({by ?? "nobody"} asked)");
        if (Ensure(rec) is not { } head || revisions.Snapshot(rec.Id, head.Rev) is not { } headSnap) return (false, $"#{rec.Id} has no revisions");
        var spec = string.IsNullOrWhiteSpace(what) ? "previous" : what;
        // "put it back" with no older revision = HEAD again (restore damage); "undo the last revision" on r1 = nothing before it.
        var target = revisions.Resolve(rec.Id, spec) ?? (string.IsNullOrWhiteSpace(what) ? head : null);
        if (target == null) return (false, $"no revision '{what}' of #{rec.Id}; design log{{id={rec.Id}}} lists them");
        if (revisions.Snapshot(rec.Id, target.Rev) is not { } snap) return (false, $"r{target.Rev} has no block set");
        if (World() is not { } w) return (false, "the build isn't in view: come closer first");
        var d = DesignDiff.Against(snap, w);
        // Blocks HEAD has that the target doesn't: dug too (they stand where the target has nothing to say).
        var clear = d.Added.Select(c => c.Pos).ToHashSet();
        foreach (var p in headSnap.Blocks.Keys)
            if (!snap.Blocks.ContainsKey(p) && w.IsLoaded(p.X, p.Z) && w.GetBlock(p.X, p.Y, p.Z) is var id && id != 0 && !DesignDiff.Drifts(id)) clear.Add(p);
        var work = d.Inverse();
        foreach (var p in clear.Where(p => !d.Added.Any(a => a.Pos == p))) work.Removed.Add(new DiffCell(p, w.GetBlock(p.X, p.Y, p.Z), w.GetMeta(p.X, p.Y, p.Z), 0, 0));
        if (work.Empty) return (false, $"#{rec.Id} already matches r{target.Rev}" + (d.Unknown > 0 ? $" ({d.Unknown} cells not loaded)" : ""));
        var next = snap.Clone();
        next.Air.UnionWith(clear);
        var air = InteriorAir(next);
        air.UnionWith(clear);
        var res = Save(rec, next, air, by!, RevisionAuthor.Player, target.Rev == head.Rev ? $"restore r{target.Rev}" : $"revert to r{target.Rev}", null,
            r => $"reverting #{rec.Id} {rec.Name} to r{target.Rev} (now r{r.Rev}): {work.Text()}", accept: by);
        if (!res.Ok) return res;
        // Turned blocks (stairs facing another way): the job takes them down first, the plan puts them back facing right.
        service.Refit(rec.Id, d.Changed.Where(c => c.FromId == c.ToId).Select(c => c.Pos));
        // Build it in place now: the plan's gate is the trusted word that asked for it (no second "is this right?").
        try
        {
            service.PlanSummary(rec);
            if (service.Plans?.Get(rec.Id, 0) is { } bp)
            {
                bp.Gate.State = Building.GateState.Approved;
                bp.Gate.ApprovedBy = by;
                bp.Gate.HeldBy = null;
                service.Plans.Save(bp);
            }
        }
        catch (Exception) { /* the build plans it again */ }
        var refusal = service.StartBuild(rec.Id, null);
        return refusal == null ? (true, res.Text + "; building it now, a [task] reports the end") : (false, res.Text + $"; saved, but can't start: {refusal}");
    }

    // ------------------------------------------------------------------ drawing a revision as the design's DSL

    /// <summary>Interior air of a block set (between the lowest and highest block of each column, as a capture draws '.').</summary>
    internal static HashSet<BlockPos> InteriorAir(DesignSnapshot s)
    {
        var cols = new Dictionary<(int X, int Z), (int Lo, int Hi)>();
        foreach (var p in s.Blocks.Keys)
            cols[(p.X, p.Z)] = cols.TryGetValue((p.X, p.Z), out var r) ? (Math.Min(r.Lo, p.Y), Math.Max(r.Hi, p.Y)) : (p.Y, p.Y);
        var air = new HashSet<BlockPos>();
        foreach (var p in s.Air)
            if (cols.TryGetValue((p.X, p.Z), out var c) && p.Y > c.Lo && p.Y < c.Hi) air.Add(p);
        return air;
    }

    /// <summary>
    /// The DSL that draws <paramref name="snap"/> as it stands (origin = its min corner), <paramref name="air"/> drawn '.'; parsed
    /// back and compared cell by cell. Null + why when it can't be drawn (over 32 a side, nothing buildable).
    /// </summary>
    internal static (string? Dsl, BlockPos Min, int Mismatched, string? Why) Draw(string name, DesignSnapshot snap, IReadOnlySet<BlockPos> air, int height)
    {
        var cells = snap.Blocks.Keys.Concat(air).ToList();
        if (snap.Blocks.Count == 0) return (null, default, 0, "nothing buildable left in it");
        var min = new BlockPos(cells.Min(p => p.X), cells.Min(p => p.Y), cells.Min(p => p.Z));
        var max = new BlockPos(cells.Max(p => p.X), cells.Max(p => p.Y), cells.Max(p => p.Z));
        int wx = max.X - min.X + 1, hy = max.Y - min.Y + 1, dz = max.Z - min.Z + 1;
        if (wx > BlueprintParser.MaxSide || dz > BlueprintParser.MaxSide || hy > BlueprintParser.MaxHeight)
            return (null, min, 0, string.Create(CultureInfo.InvariantCulture, $"it would be {wx}×{hy}×{dz} (a design holds at most {BlueprintParser.MaxSide} a side)"));
        var r = new CaptureResult { Name = name, Min = min, Max = max };
        foreach (var (p, s) in snap.Blocks) r.Cells[p] = s;
        foreach (var p in air) if (!snap.Blocks.ContainsKey(p)) r.Air.Add(p);
        var dsl = StructureCapture.Dsl(r);
        var (bp, issues) = BlueprintParser.Parse(dsl);
        if (issues.FirstOrDefault(i => i.Level == IssueLevel.Error) is { } perr) return (null, min, 0, "it doesn't draw as a design: " + perr);
        var (plan, more) = BlueprintNormalizer.Normalize(bp, min, height);
        if (plan == null || more.Any(i => i.Level == IssueLevel.Error)) return (null, min, 0, "it doesn't normalize: " + string.Join("; ", more.Where(i => i.Level == IssueLevel.Error).Take(2)));
        int bad = snap.Blocks.Count(kv => !plan.Blocks.TryGetValue(kv.Key, out var b) || b.Id != kv.Value.Id || b.Meta != kv.Value.Meta);
        return (dsl, min, bad, null);
    }

    /// <summary>Draws the new HEAD, saves the design (DSL, origin, a reset gate transform: it is drawn as built), adds the revision,
    /// moves the linked structure's box and opens the design for editing.</summary>
    private (bool Ok, string Text) Save(BlueprintRecord rec, DesignSnapshot next, HashSet<BlockPos> air, string author, string kind, string message,
        string? tag, Func<DesignRevision, string> text, string? accept = null)
    {
        int height = World()?.WorldHeight ?? ChunkColumn.SizeY;
        var (dsl, min, bad, why) = Draw(rec.Name, next, air, height);
        if (dsl == null) return (false, $"can't save #{rec.Id} as a revision: {why}");
        rec.Dsl = dsl;
        rec.Origin = min;
        if (accept != null)
        {
            rec.Status = DesignStatus.Accepted;
            rec.AcceptedBy = accept;
        }
        service.Store.Update(rec);
        // The DSL is drawn as it stands: a gate correction (turn/shift) from an earlier build must not apply on top of it.
        if (service.Plans?.Get(rec.Id, 0) is { Transform.IsNone: false } bp)
        {
            bp.Transform = Building.BuildTransform.None;
            service.Plans.Save(bp);
        }
        var r = revisions.Add(rec.Id, next, dsl, min, rec.Dim, author, kind, message, tag);
        if (Memory != null && next.Box is { } box) MoveLink(Memory, rec, box.Min, box.Max, r.Rev);
        Edits?.Touch(rec.Id, $"{author} made r{r.Rev}");
        return (true, text(r) + (bad > 0 ? $" ({bad} cells drawn as stand-ins)" : ""));
    }

    /// <summary>The remembered structure that links the design follows its new box (protection guards what the design is now).</summary>
    internal static void MoveLink(MemoryStore memory, BlueprintRecord rec, BlockPos min, BlockPos max, int? rev)
    {
        var linked = memory.Read(d => d.Structures.Where(s => MineClaw.Bot.Memory.DesignLinks.Of(s.Note) == rec.Id).Select(s => s.Name).ToList());
        foreach (var name in linked)
            memory.UpsertStructure(name, st =>
            {
                st.Min = new MPos(min.X, min.Y, min.Z);
                st.Max = new MPos(max.X, max.Y, max.Z);
                st.History.Add(string.Create(CultureInfo.InvariantCulture, $"{(rev is { } n ? $"r{n}" : "built")} of design #{rec.Id}: x {min.X}..{max.X}, y {min.Y}..{max.Y}, z {min.Z}..{max.Z}"));
            });
    }
}
