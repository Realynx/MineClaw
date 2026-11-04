using System.Text.Json;
using MineClaw.Data;
using MineClaw.Skills.Inventory;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>One endpoint of a move: the player's inventory (all/main/hotbar, optional slot) or a container.</summary>
public sealed record Endpoint(BlockPos? Container, Region Region, int? Slot, string Label)
{
    public bool IsPlayer => Container is null;

    public static Endpoint? Parse(SkillContext ctx, string? text, int? slot)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToLowerInvariant();
        if (t is "inv" or "inventory" or "me" or "player") return new(null, Region.Player, slot, "inv");
        if (t is "hotbar" or "hand") return new(null, Region.Hotbar, slot, "hotbar");
        if (t is "main") return new(null, Region.Main, slot, "main");
        return ctx.Directory.Resolve(ctx, text) is { } p ? new(p, Region.Container, slot, text.Trim()) : null;
    }

    /// <summary>Slot set in a window model (pinned slot mapped into that window).</summary>
    public IReadOnlyCollection<int> Slots(WindowModel m)
    {
        if (Slot is { } s)
            return Region switch
            {
                Region.Container => [s],
                Region.Hotbar => [m.HotbarStart + Math.Clamp(s, 0, 8)],
                Region.Main => [m.MainStart + Math.Clamp(s, 0, 26)],
                _ => [s < 9 ? m.HotbarStart + s : m.MainStart + Math.Clamp(s - 9, 0, 26)],
            };
        return m.SlotsOf(Region).ToArray();
    }
}

public sealed record MoveSpec(int Id, int? Damage, int? Count, Endpoint From, Endpoint To)
{
    public int Done { get; set; }
    /// <summary>Picked up from a container, waiting to be dropped off at another.</summary>
    public int Carried { get; set; }
    public bool Finished { get; set; }
    public string Name => Items.DisplayName(Id, Damage ?? 0).ToLowerInvariant();
}

/// <summary>
/// Declarative batch moves: plans the whole list at once (grouped per container, nearest-first tour, each container
/// opened as few times as possible), then executes minimal click plans with 0x6A-verified clicks.
/// </summary>
[Skill]
public sealed class MoveItemsSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "move_items", Category = SkillCategory.Storage, Summary = "batch item moves inv/hotbar/chests, min clicks",
        Args = "moves:[{item,count|all,from,to,slot?}]",
        Schema = SkillJson.Parse("""{"type":"object","properties":{"moves":{"type":"array","items":{"type":"object","properties":{"item":{"type":"string"},"count":{},"from":{"type":"string"},"to":{"type":"string"},"slot":{"type":"integer"}},"required":["item","from","to"]}}},"required":["moves"]}"""),
        Estimate = TimeSpan.FromSeconds(30),
        Keywords = "move items transfer put take chest inventory hotbar withdraw deposit stash fetch storage organize slots",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var moves = new List<MoveSpec>();
        if (!args.TryGetProperty("moves", out var arr) || arr.ValueKind != JsonValueKind.Array) return Fail("moves required");
        foreach (var m in arr.EnumerateArray())
        {
            var (spec, error) = ParseMove(ctx, m);
            if (spec is null) return Fail(error!);
            moves.Add(spec);
        }
        if (moves.Count == 0) return Fail("no moves");
        var run = new InventoryRun(ctx);
        try
        {
            var error = await new MovePlanner(ctx, run).RunAsync(moves, ct);
            var close = await run.CloseAsync(ct);
            return Outcome(ctx, run, moves, error ?? close);
        }
        catch
        {
            await run.CloseAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>One move of the batch, or why it can't be done (unknown item or endpoint, a reserved item leaving me).</summary>
    private static (MoveSpec? Spec, string? Error) ParseMove(SkillContext ctx, JsonElement m)
    {
        if (SkillJson.Item(m.Str("item")) is not { } it) return (null, $"unknown item '{m.Str("item")}'");
        var from = Endpoint.Parse(ctx, m.Str("from"), null);
        var to = Endpoint.Parse(ctx, m.Str("to"), m.Int("slot"));
        if (from is null) return (null, $"unknown source '{m.Str("from")}'");
        if (to is null) return (null, $"unknown destination '{m.Str("to")}'");
        if (from.IsPlayer && ctx.Game.ReservedItems.Contains(it.Id) && !to.IsPlayer) return (null, $"{Item(it.Id)} is reserved; won't move it out");
        return (new MoveSpec(it.Id, Damage(it), Count(m), from, to), null);
    }

    /// <summary>A number, or null for "all" (or no count).</summary>
    private static int? Count(JsonElement m) =>
        m.Str("count") is { } c && !c.Equals("all", StringComparison.OrdinalIgnoreCase) && int.TryParse(c, out var n) ? n : null;

    /// <summary>The damage to match: none for tools (any wear) or an unspecified one.</summary>
    private static int? Damage((int Id, int Damage) it) => it.Damage < 0 || Items.Get(it.Id).MaxDamage > 0 ? null : it.Damage;

    private static SkillResult Outcome(SkillContext ctx, InventoryRun run, List<MoveSpec> moves, string? failure)
    {
        int done = moves.Count(m => m.Finished);
        run.Moves = done;
        string tail = Tail(ctx, moves);
        return failure is not null ? Fail($"{done}/{moves.Count} moves done, {run.Summary(tail)}; {failure}") : Ok(run.Summary(tail));
    }

    private static string Tail(SkillContext ctx, List<MoveSpec> moves)
    {
        var full = moves.Where(m => m.To.Container is { } c && ctx.Directory is ContainerDirectory d && d.Snapshots.TryGetValue(c, out var s) && Fill.Full(s))
            .Select(m => m.To.Label).Distinct().ToArray();
        return full.Length > 0 ? string.Join(", ", full) + " now full" : "";
    }
}

/// <summary>Visits containers nearest-first, doing every move that can be done at each one in a single opening.</summary>
internal sealed class MovePlanner(SkillContext ctx, InventoryRun run)
{
    public async Task<string?> RunAsync(List<MoveSpec> moves, CancellationToken ct)
    {
        await InventoryMovesAsync(moves, ct);
        // 2) Container legs, nearest container with doable work first.
        var skipped = new HashSet<BlockPos>();
        for (int visit = 0; visit < 4 * moves.Count + 4; visit++)
        {
            ct.ThrowIfCancellationRequested();
            var pending = moves.Where(m => !m.Finished && !(m.From.IsPlayer && m.To.IsPlayer)).ToList();
            if (pending.Count == 0) return null;
            var work = pending.SelectMany(m => Stops(m)).Where(c => !skipped.Contains(c)).Distinct()
                .OrderBy(c => c.DistanceSquaredTo(ctx.Me)).ToList();
            if (work.Count == 0) return pending.Count > 0 ? "some moves couldn't be done (claimed/protected containers or no room)" : null;
            var chest = work[0];
            var err = await run.OpenAsync(chest, ct);
            if (err is not null) { skipped.Add(chest); run.Notes.Add(err); continue; }
            // Deposits first (frees inventory for withdrawals), then withdrawals.
            bool deposited = await DepositsAsync(chest, pending, ct);
            bool withdrew = await WithdrawalsAsync(chest, pending, ct);
            if (!deposited && !withdrew) skipped.Add(chest);
        }
        return "too many container visits";
    }

    /// <summary>1) Inventory-only moves (e.g. inv → hotbar) in the player window.</summary>
    private async Task InventoryMovesAsync(List<MoveSpec> moves, CancellationToken ct)
    {
        foreach (var m in moves.Where(m => m.From.IsPlayer && m.To.IsPlayer))
        {
            await run.CloseAsync(ct);
            int want = m.Count ?? run.Snapshot().Count(m.Id, m.Damage, m.From.Slots(run.Snapshot()));
            m.Done += await run.TransferAsync(m.Id, m.Damage, want, w => Except(m.From.Slots(w), m.To.Slots(w)), m.To.Slots, ct, Pinned(m, run.Snapshot()));
            m.Finished = m.Done >= want;
        }
    }

    private async Task<bool> DepositsAsync(BlockPos chest, List<MoveSpec> pending, CancellationToken ct)
    {
        bool progress = false;
        foreach (var m in pending.Where(m => m.To.Container == chest))
            if (await DepositAsync(m, ct)) progress = true;
        return progress;
    }

    /// <summary>Into the open chest from my inventory: what the move still wants (or what I carry for it); true when any moved.</summary>
    private async Task<bool> DepositAsync(MoveSpec m, CancellationToken ct)
    {
        int have = run.Snapshot().Count(m.Id, m.Damage, run.Snapshot().SlotsOf(Region.Player));
        if (have == 0) return false;
        int want = DepositWant(m, have);
        if (want <= 0) return false;
        int n = await run.TransferAsync(m.Id, m.Damage, want, w => w.SlotsOf(Region.Player).ToArray(), m.To.Slots, ct, Pinned(m, run.Snapshot()));
        m.Done += n;
        if (!m.From.IsPlayer) m.Carried -= n;
        if (n < want) run.Notes.Add($"{m.To.Label} full");
        if (DepositDone(m)) m.Finished = true;
        return n > 0;
    }

    private static int DepositWant(MoveSpec m, int have) =>
        m.From.IsPlayer ? Math.Min(have, (m.Count ?? have) - m.Done) : Math.Min(have, m.Carried);

    /// <summary>From my inventory: "all" is done after one deposit, a count when reached; carried from a chest: when all of it is in.</summary>
    private static bool DepositDone(MoveSpec m) =>
        m.From.IsPlayer ? m.Count is null || m.Done >= m.Count : m.Carried == 0 && m.Count is { } cc && m.Done >= cc;

    private async Task<bool> WithdrawalsAsync(BlockPos chest, List<MoveSpec> pending, CancellationToken ct)
    {
        bool progress = false;
        foreach (var m in pending.Where(m => m.From.Container == chest && m.Carried == 0 && !m.Finished))
            if (await WithdrawAsync(m, ct)) progress = true;
        return progress;
    }

    /// <summary>Out of the open chest: into the move's player slots, or carried in my inventory to its destination chest.</summary>
    private async Task<bool> WithdrawAsync(MoveSpec m, CancellationToken ct)
    {
        var snap = run.Snapshot();
        int there = snap.Count(m.Id, m.Damage, snap.SlotsOf(Region.Container));
        int want = Math.Min(there, (m.Count ?? there) - m.Done);
        if (want <= 0) { m.Finished = true; return false; }
        var dest = m.To.IsPlayer ? m.To : new Endpoint(null, Region.Player, null, "inv");
        int n = await run.TransferAsync(m.Id, m.Damage, want, w => w.SlotsOf(Region.Container).ToArray(), dest.Slots, ct,
            m.To.IsPlayer ? Pinned(m, snap) : null);
        Withdrew(m, n, want);
        if (n < want) run.Notes.Add("inventory full");
        return n > 0;
    }

    private static void Withdrew(MoveSpec m, int n, int want)
    {
        if (m.To.IsPlayer)
        {
            m.Done += n;
            m.Finished = m.Count is null || m.Done >= m.Count || n < want;
        }
        else
        {
            m.Carried += n;
            if (n == 0) m.Finished = true;
        }
    }

    private static IEnumerable<BlockPos> Stops(MoveSpec m)
    {
        if (m.Carried > 0 && m.To.Container is { } t) { yield return t; yield break; }
        if (m.From.Container is { } f) yield return f;
        else if (m.To.Container is { } t2) yield return t2;
    }

    private static int? Pinned(MoveSpec m, WindowModel w) => m.To.Slot is null ? null : m.To.Slots(w).First();

    private static IReadOnlyCollection<int> Except(IReadOnlyCollection<int> a, IReadOnlyCollection<int> b) => a.Except(b).ToArray();
}

/// <summary>Sets an exact slot layout (preset, template, or the saved one) with the fewest cursor clicks.</summary>
[Skill]
public sealed class ArrangeChestSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "arrange_chest", Category = SkillCategory.Storage, Summary = "exact chest layout: preset/template/saved", Args = "chest,layout?,save?",
        Schema = SkillJson.Schema("chest:str,layout:str?,save:bool?"), Estimate = TimeSpan.FromSeconds(20),
        Keywords = "arrange tidy chest layout slots order neat compact columns template tidy up restore",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var name = args.Str("chest") ?? "";
        if (FindChest(ctx, name) is not { } pos) return Fail($"no chest '{name}'");
        var layoutText = LayoutFor(ctx, args, pos);
        if (layoutText.Length == 0) return Fail($"no saved layout for chest {P(pos)}");
        var run = new InventoryRun(ctx);
        var r = await ArrangeAsync(ctx, run, pos, layoutText, ct);
        var close = await run.CloseAsync(ct);
        if (r.Error is null && args.Flag("save", true)) ctx.Layouts.Save(pos, r.Final);
        if ((r.Error ?? close) is { } failure) return Fail($"chest {P(pos)}: {failure}; {run.Summary()}");
        return Ok($"chest {P(pos)} arranged ({layoutText.Split(' ')[0]}): {run.Summary(Off(r.Mismatch, r.Unplaced))}");
    }

    /// <summary>The named chest, or the nearest one within 6 when no name is given.</summary>
    private static BlockPos? FindChest(SkillContext ctx, string name) =>
        ctx.Directory.Resolve(ctx, name) ?? (name.Length == 0 ? ctx.Find((id, _) => StorageOps.IsChest(id), 6, 1).Select(f => (BlockPos?)f.Pos).FirstOrDefault() : null);

    /// <summary>The layout asked for, else the chest's saved one, else by_category; "saved" with none saved is "".</summary>
    private static string LayoutFor(SkillContext ctx, JsonElement args, BlockPos pos)
    {
        var layoutText = args.Str("layout") ?? ctx.Layouts.Get(pos) ?? "by_category";
        return layoutText.Equals("saved", StringComparison.OrdinalIgnoreCase) ? ctx.Layouts.Get(pos) ?? "" : layoutText;
    }

    private static string Off(int mismatch, int unplaced) =>
        mismatch > 0 ? $"{mismatch} slots off" : unplaced > 0 ? $"{unplaced} stacks didn't fit the template" : "";

    public static async Task<(string? Error, string Final, int Mismatch, int Unplaced)> ArrangeAsync(SkillContext ctx, InventoryRun run, BlockPos pos,
        string layoutText, CancellationToken ct)
    {
        var err = await run.OpenAsync(pos, ct);
        if (err is not null) return (err, "", 0, 0);
        var m = run.Snapshot();
        int n = m.ContainerSlots;
        var all = Enumerable.Range(0, n).ToArray();
        SlotLayout? layout = null;
        bool preset = LayoutPresets.Names.Contains(layoutText.Trim().ToLowerInvariant());
        if (!preset)
        {
            try { layout = SlotLayout.Parse(layoutText, n); }
            catch (FormatException ex) { return (ex.Message, "", 0, 0); }
        }
        // 1) Merge partial stacks so the layout works with full stacks.
        if (!await run.ExecAsync(ClickPlanner.Consolidate(m, all), ct)) { await run.ClearCursorAsync(ct); }
        // 2) Target + cycle decomposition, re-planned once if a click is rejected.
        int unplaced = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var stacks = run.ContainerContents();
            int[] target;
            if (preset) target = LayoutPresets.Target(layoutText.Trim().ToLowerInvariant(), stacks);
            else (target, unplaced) = LayoutPresets.Target(layout!, stacks);
            var plan = ClickPlanner.Permute(target);
            run.Moves += target.Where((src, i) => src >= 0 && src != i).Count();
            if (plan.Count == 0 || await run.ExecAsync(plan, ct)) break;
            await run.ClearCursorAsync(ct, all);
        }
        var final = run.ContainerContents();
        int mismatch = layout is null ? 0 : Enumerable.Range(0, Math.Min(n, layout.Slots.Count))
            .Count(i => layout.Slots[i] is { } want && (final[i].IsEmpty || final[i].Id != want.Id || final[i].Damage != want.Damage)
                        && final.Any(s => !s.IsEmpty && s.Id == want.Id));
        return (null, SlotLayout.Describe(final), mismatch, unplaced);
    }
}

/// <summary>Assigns categories to chests, moves stray stacks into their category's chest, consolidates and sorts.</summary>
[Skill]
public sealed class OrganizeStorageSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "organize_storage", Category = SkillCategory.Storage, Summary = "sort nearby chests by category, consolidate", Args = "chests?,scheme?,label?",
        Schema = SkillJson.Schema("chests:str?,scheme:str?,label:bool?"), Estimate = TimeSpan.FromMinutes(2),
        Keywords = "organize sort storage chests categories clean store ores wood food tools sorting system",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var scope = args.Str("chests") ?? "all";
        var chests = Pick(ctx, scope);
        if (chests.Count == 0) return Fail($"no chests for '{scope}'");
        var run = new InventoryRun(ctx);
        var contents = await SurveyAsync(ctx, run, chests, ct);
        if (contents.Count == 0) return Fail(run.Summary("no chest could be opened"));
        var cat = Categorize(ctx, contents);
        var moves = StrayMoves(contents, cat);
        string? error = moves.Count > 0 ? await new MovePlanner(ctx, run).RunAsync(moves, ct) : null;
        run.Moves = moves.Count(m => m.Finished);
        await SortAsync(ctx, run, cat, args.Str("scheme") ?? "by_category", ct);
        var close = await run.CloseAsync(ct);
        string summary = Summary(ctx, args, run, cat);
        return (error ?? close) is { } failure ? Fail($"{summary}; {failure}") : Ok(summary);
    }

    /// <summary>1) Survey: one opening per chest, nearest first (also consolidates partial stacks in it).</summary>
    private static async Task<Dictionary<BlockPos, ItemStack[]>> SurveyAsync(SkillContext ctx, InventoryRun run, List<BlockPos> chests, CancellationToken ct)
    {
        var contents = new Dictionary<BlockPos, ItemStack[]>();
        foreach (var c in chests.OrderBy(c => c.DistanceSquaredTo(ctx.Me)))
        {
            var err = await run.OpenAsync(c, ct);
            if (err is not null) { run.Notes.Add(err); continue; }
            await run.ExecAsync(ClickPlanner.Consolidate(run.Snapshot(), Enumerable.Range(0, run.Snapshot().ContainerSlots).ToArray()), ct);
            contents[c] = run.ContainerContents();
        }
        await run.CloseAsync(ct);
        return contents;
    }

    /// <summary>2) Categories: label/assignment first, then the majority of contents, then what's left in order (assigned in the
    /// directory).</summary>
    private static Dictionary<BlockPos, string> Categorize(SkillContext ctx, Dictionary<BlockPos, ItemStack[]> contents)
    {
        var cat = new Dictionary<BlockPos, string>();
        foreach (var (c, s) in contents)
            if (ctx.Directory.CategoryOf(ctx, c) is { } known) cat[c] = known;
        foreach (var (c, s) in contents.Where(kv => !cat.ContainsKey(kv.Key)))
            if (Majority(s) is { } major && !cat.ContainsValue(major)) cat[c] = major;
        var remaining = StorageCategory.Order.Where(o => !cat.ContainsValue(o)).ToQueue();
        foreach (var c in contents.Keys.Where(c => !cat.ContainsKey(c))) cat[c] = remaining.Count > 0 ? remaining.Dequeue() : "misc";
        foreach (var (c, k) in cat) ctx.Directory.Assign(c, k);
        return cat;
    }

    private static string? Majority(ItemStack[] s) =>
        s.Where(x => !x.IsEmpty).GroupBy(x => StorageCategory.Of(x.Id, x.Damage)).OrderByDescending(g => g.Sum(x => x.Count)).FirstOrDefault()?.Key;

    /// <summary>3) Stray stacks → their category chest (or "misc" when that category has no chest).</summary>
    private static List<MoveSpec> StrayMoves(Dictionary<BlockPos, ItemStack[]> contents, Dictionary<BlockPos, string> cat)
    {
        var moves = new List<MoveSpec>();
        foreach (var (c, s) in contents)
        foreach (var g in s.Where(x => !x.IsEmpty).GroupBy(x => (x.Id, x.Damage)))
        {
            var k = StorageCategory.Of(g.Key.Id, g.Key.Damage);
            if (cat[c] == k || Home(cat, k) is not { } h || h == c) continue;
            moves.Add(new MoveSpec(g.Key.Id, Items.Get(g.Key.Id).MaxDamage > 0 ? null : g.Key.Damage, g.Sum(x => x.Count),
                new Endpoint(c, Region.Container, null, $"chest@{P(c)}"), new Endpoint(h, Region.Container, null, $"storage:{k}")));
        }
        return moves;
    }

    private static BlockPos? Home(Dictionary<BlockPos, string> cat, string k) =>
        cat.Where(kv => kv.Value == k).Select(kv => (BlockPos?)kv.Key).FirstOrDefault()
        ?? cat.Where(kv => kv.Value == "misc").Select(kv => (BlockPos?)kv.Key).FirstOrDefault();

    /// <summary>4) Sort each chest by category/id (its saved layout when it has one), nearest first.</summary>
    private static async Task SortAsync(SkillContext ctx, InventoryRun run, Dictionary<BlockPos, string> cat, string scheme, CancellationToken ct)
    {
        foreach (var c in cat.Keys.OrderBy(c => c.DistanceSquaredTo(ctx.Me)))
        {
            var r = await ArrangeChestSkill.ArrangeAsync(ctx, run, c, ctx.Layouts.Get(c) ?? scheme, ct);
            if (r.Error is not null) run.Notes.Add(r.Error);
        }
    }

    private static string Summary(SkillContext ctx, JsonElement args, InventoryRun run, Dictionary<BlockPos, string> cat)
    {
        string labels = args.Flag("label") && !ctx.Game.Supported.HasFlag(Primitive.WriteSign) ? "labels skipped (needs write_sign)" : "";
        return run.Summary(string.Join(", ", cat.Select(kv => $"{kv.Value}@{P(kv.Key)}")) + (labels.Length > 0 ? "; " + labels : ""));
    }

    private static List<BlockPos> Pick(SkillContext ctx, string scope)
    {
        // Large chests are two blocks: one per pair, its west/north half (was: any touching chest dropped, a trapped chest
        // beside a normal one included).
        var all = MineClaw.World.ChestPairs.Distinct(ctx.World, ctx.Find((id, _) => StorageOps.IsChest(id), ctx.Options.SearchRadius, 64).Select(f => f.Pos))
            .Where(p => ctx.Claims.IsFree(MineClaw.World.Claims.ClaimKey.Chest(p, ctx.World.Dimension))).ToList();
        if (scope is "all" or "") return all;
        if (scope.Contains("..")) // area "x,y,z..x,y,z"
        {
            var parts = scope.Split("..");
            var a = SkillJson.Parse($"{{\"p\":\"{parts[0]}\"}}").Pos("p");
            var b = SkillJson.Parse($"{{\"p\":\"{parts[1]}\"}}").Pos("p");
            if (a is { } pa && b is { } pb)
            {
                var key = MineClaw.World.Claims.ClaimKey.Area(pa, pb, ctx.World.Dimension);
                return all.Where(key.Contains).ToList();
            }
        }
        return scope.Split('|', ',').Select(s => ctx.Directory.Resolve(ctx, s)).Where(p => p is not null).Select(p => p!.Value).Distinct().ToList();
    }
}

internal static class QueueExt
{
    public static Queue<T> ToQueue<T>(this IEnumerable<T> e) => new(e);
}
