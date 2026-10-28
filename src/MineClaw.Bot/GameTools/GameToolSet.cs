using System.Text.Json;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.Memory;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Registers the game macro tools. Descriptions and schemas are deliberately terse: every byte here is in the cached
/// prefix of every request. Positions are one "x,y,z" string (or a remembered name) instead of three integers.
/// Chat is not a tool: the runtime types the model's plain text into chat. <c>stop</c> is the runtime's built-in
/// (it cancels the activity, whose token cancels the client action).
/// </summary>
public static class GameToolSet
{
    public static readonly string[] ToolNames =
    [
        "status", "inventory", "look", "window", "goto", "follow", "mine", "harvest_generator", "collect_drops", "craft", "smelt",
        "collect_furnace", "deposit", "withdraw", "organize_chest", "place", "dig", "use_block", "eat", TreeTools.ToolName,
    ];

    private const string At = "x,y,z or saved name";

    /// <summary>
    /// The dig cell to harvest: the asked one when free, else the nearest unclaimed cell of the same generator ("6,64,8 is
    /// maid_chan's; took 4,64,6"). Without a hive every cell is free. The fallback never invents a cell (live 10-28, 69:
    /// "5,64,7 is taken by another bot; took the free cell 5,65,7" — a block over a cell, taught by a stray punch): only
    /// <see cref="FallbackCells"/>, else "busy: every dig cell ... is taken" (the caller waits or does another job).
    /// </summary>
    internal static (BlockPos? Cell, string? Note) PickGeneratorCell(IGameClient c, BlockPos asked, Func<BlockPos, IReadOnlyList<BlockPos>>? cells)
    {
        var claims = GameClients.Claimed(c)?.Claims;
        int dim = c.World.Dimension;
        if (claims == null || claims.IsFree(MineClaw.World.Claims.ClaimKey.Block(asked, dim))) return (asked, null);
        var all = FallbackCells(c, asked, cells);
        foreach (var x in all)
            if (claims.IsFree(MineClaw.World.Claims.ClaimKey.Block(x, dim)))
                return (x, $"{Locations.Fmt(asked)} is taken by another bot; took the free cell {Locations.Fmt(x)}");
        return (null, $"busy: every dig cell of that generator is taken ({Locations.Fmt(asked)}{(all.Count > 0 ? " +" + all.Count : "")})");
    }

    /// <summary>
    /// The generator's other dig cells a bot may move to when <paramref name="asked"/> is claimed, nearest first: on the asked
    /// cell's own layer (a gen digs in one layer: never the block over or under a cell), taught or regen-confirmed
    /// (<see cref="AcquisitionSources.KnownCell"/>), and a taught one only while the world still reads it as a cell (not the
    /// lava/water source a stray punch landed on). Only when the generator has no known cell at all do geometry cells (solid
    /// cobble/stone with lava flowing in and water touching) count.
    /// </summary>
    internal static List<BlockPos> FallbackCells(IGameClient c, BlockPos asked, Func<BlockPos, IReadOnlyList<BlockPos>>? cells)
    {
        var src = Acquisition.Of(c);
        var known = src?.KnownCell;
        var confirmed = src?.ConfirmedCell;
        var raw = (cells?.Invoke(asked) ?? Perception.BlockDescriber.GenCellsNear(c.World, asked, 4))
            .Where(x => x != asked && x.Y == asked.Y).Distinct().ToList();
        bool Valid(BlockPos x) => confirmed?.Invoke(x) == true || Perception.BlockDescriber.IsGenCell(c.World, x);
        List<BlockPos> list;
        if (known != null && (known(asked) || raw.Any(known)))
            list = raw.Where(x => known(x) && Valid(x)).ToList();
        else
            list = raw.Where(x => c.World.GetBlock(x) is Blocks.Cobblestone or Blocks.Stone && Perception.BlockDescriber.IsGenCell(c.World, x)).ToList();
        return list.OrderBy(x => x.DistanceSquaredTo(asked)).ToList();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<BlockPos, DateTime> EnclosedAsked = new();

    /// <summary>How long an "enclosed cell, where do I stand?" question counts as asked for that cell.</summary>
    public static TimeSpan EnclosedAskMemory { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A cell with no reachable stand spot: the first time, a needs_input naming exactly what boxes it in and asking where to
    /// stand; after that (until <see cref="EnclosedAskMemory"/>) a plain failure that says it was asked already.
    /// </summary>
    internal static ToolResult AskOnceIfEnclosed(ToolResult r, BlockPos cell)
    {
        if (r.Ok || !r.Text.Contains(" is enclosed (", StringComparison.Ordinal)) return r;
        var text = r.Text.Replace("; ask a player once where to stand", "", StringComparison.Ordinal);
        var now = DateTime.UtcNow;
        if (EnclosedAsked.TryGetValue(cell, out var at) && now - at < EnclosedAskMemory)
            return ToolResult.Error($"{text} (already asked where to stand {(int)(now - at).TotalMinutes} min ago; waiting for an answer)");
        EnclosedAsked[cell] = now;
        return ToolResult.Error($"needs_input: {text}. Where should I stand to dig {Locations.Fmt(cell)}?");
    }

    private static bool IsAuto(string at) => at.Trim().ToLowerInvariant() is "auto" or "any" or "free" or "free cell" or "any cell";

    /// <summary>
    /// <c>harvest_generator at=auto</c>: the nearest taught/regen-confirmed dig cell no other hive bot holds (else the nearest
    /// one, so the caller's claim-aware pick queues for it). Never a geometry guess when cells were taught.
    /// </summary>
    internal static (BlockPos? Cell, string? Note) AutoGeneratorCell(IGameClient c, Func<BlockPos, IReadOnlyList<BlockPos>>? cells)
    {
        var src = Acquisition.Of(c);
        var me = c.World.Player.Position.ToBlockPos();
        var known = src?.KnownCell;
        var all = (src?.GeneratorCells?.Invoke() ?? cells?.Invoke(me) ?? Perception.BlockDescriber.GenCellsNear(c.World, me, 16)).Distinct().ToList();
        var list = known != null && all.Any(known) ? all.Where(known).ToList() : all;
        list = list.Where(x => src?.ConfirmedCell?.Invoke(x) == true || Perception.BlockDescriber.IsGenCell(c.World, x)).ToList();
        var layer = list;
        list = list.Where(x => src?.ConfirmedCell?.Invoke(x) == true || Perception.DigCells.OffLayerOf(x, layer) == null)
            .OrderBy(x => x.DistanceSquaredTo(me)).ToList();
        if (list.Count == 0) return (null, "needs_input: no known generator cell for at=auto; punch the gen's cells and I'll mine only those");
        var claims = GameClients.Claimed(c)?.Claims;
        int dim = c.World.Dimension;
        var free = claims == null ? list[0] : list.FirstOrDefault(x => claims.IsFree(MineClaw.World.Claims.ClaimKey.Block(x, dim)), list[0]);
        return (free, $"auto: cell {Locations.Fmt(free)}");
    }

    /// <summary>Lava and water next to it (geometry), or one of the generator's taught / regen-confirmed cells.</summary>
    internal static bool IsGeneratorCell(IGameClient c, BlockPos p, Func<BlockPos, IReadOnlyList<BlockPos>>? cells)
    {
        if (Perception.BlockDescriber.IsGenCell(c.World, p)) return true;
        return cells?.Invoke(p).Contains(p) == true;
    }

    /// <param name="landmark">Nearest named landmark of a point, phrased relationally ("2 E of cobble_gen"); null = no hints.</param>
    /// <param name="generatorCells">All dig cells of the generator around a block (dual generators have several); null = the
    /// world geometry only.</param>
    public static void RegisterAll(ToolRegistry tools, IGameClient c, IPlaceResolver places, BotOptions? options = null,
        Func<double, double, double, string?>? landmark = null, Func<BlockPos, IReadOnlyList<BlockPos>>? generatorCells = null,
        GeneratorSupport? generator = null)
    {
        options ??= new BotOptions();
        var crafter = new Crafter(c) { Observations = generator?.Observations }; // chest ingredients count too
        var organizer = new ChestOrganizer(c);
        int max() => tools.MaxResultLength;

        // ---- observation (instant)
        tools.Register("status", "Your full status line.", ToolSchema.Empty,
            (_, _) =>
            {
                var h = Hints(c, landmark);
                return Ok(h.Length > 0 ? $"{c.DescribeStatus()} | {h}" : c.DescribeStatus());
            }, cheapTier: true);
        tools.Register("inventory", "Your inventory, grouped.",
            ToolSchema.Create().Bool("detail", "with hotbar", required: false).Build(),
            (i, _) => Ok(c.DescribeInventory(i.Bool("detail") == true, max())), cheapTier: true);
        tools.Register("look", "Surroundings: footing, edges, blocks, chests, ores, players, mobs, drops. detail=more. map=ASCII grid.",
            ToolSchema.Create().Int("radius", "default 12", required: false).Bool("detail", required: false).Bool("map", required: false).Build(),
            (i, _) =>
            {
                if (i.Bool("map") == true) return Ok(VisionTools.LookMap(c, 2 * Math.Clamp(i.Int("radius") ?? 7, 2, 12) + 1));
                bool detail = i.Bool("detail") == true;
                // detail may use the tool's own (bigger) cap; plain look stays at the small default.
                int cap = detail ? tools.CapFor("look") : max();
                var h = Hints(c, landmark);
                var body = c.DescribeSurroundings(Math.Clamp(i.Int("radius") ?? 12, 3, 32), detail, h.Length > 0 ? Math.Max(80, cap - h.Length - 3) : cap);
                return Ok(h.Length > 0 ? $"{h} | {body}" : body);
            }, cheapTier: true);
        tools.Register("window", "Contents of the open container.", ToolSchema.Empty,
            (_, _) => Ok(c.DescribeOpenWindow(tools.CapFor("window"))));

        // ---- movement
        tools.Register("goto", "Walk to x,y,z, a saved place or player (come here).",
            ToolSchema.Create().Str("to", "x,y,z, name or player", required: false).Num("dist", required: false)
                .Bool("build", "may dig/place on the way", required: false)
                .Str("allow_parkour", "void = gap jumps over the void too; only when poofimafox/kiwi asked for this jump", required: false, enumValues: ["off", "ground", "void"])
                .Str("into_void", "reason: deliberately step off the nearest edge into the void", required: false)
                .Bool("keep_items_ok", required: false).Build(),
            async (i, ct) =>
            {
                // The one explicit way off the island (a deliberate, reasoned opt-in; every other walk keeps full void safety).
                if (i.Str("into_void") is { } why)
                    return R(await c.StepIntoVoidAsync(why, i.Bool("keep_items_ok") == true, ct));
                var to = i.ReqStr("to");
                bool build = i.Bool("build") == true;
                var parkour = i.Str("allow_parkour") switch { "off" => ParkourMode.Off, "void" => ParkourMode.Void, _ => ParkourMode.Ground };
                if (build) await ClutchBlocks.EnsureAsync(c, ct); // bridging/pillaring = edge work: carry clutch blocks first
                var opts = GotoOptions.Default with { AllowBreak = build, AllowPlace = build, Parkour = parkour };
                double? dist = i.Num("dist");
                // A saved place in the other dimension: through a known linked portal, or an honest "no known portal".
                if (Locations.ParseCoords(to) == null && places is ICrossDimensionPlaces xd && xd.ResolveElsewhere(to) is { } far)
                    return await WorldIndex.PortalTravel.GotoAsync(c, xd.Portals, far.Pos, far.Dim, opts, dist ?? 1, ct);
                // A portal block (no dist): step into the sheet and go through, never "within 1" of it.
                if (dist is null && Resolve(to, places) is { } portal && c.World.GetBlock(portal) == Blocks.Portal)
                    return await WorldIndex.PortalTravel.GoThroughAsync(c, portal, opts, ct);
                Goal goal;
                // A container/door/lever named by its coordinates (no dist): stop where it can be USED, i.e. seen within
                // reach, so a goto>withdraw chain doesn't end outside the wall next to it (live 10-27, chest 14,64,-5).
                if (Resolve(to, places) is { } p)
                    goal = dist is null && Blocks.Get(c.World.GetBlock(p)).Interactable
                        ? new GoalUseBlock(c.World, p, Aiming.DefaultReach - 0.6) : Near(p, dist ?? 1);
                else if (c.World.Entities.FindPlayer(to) is { } e) goal = new GoalNearPosition(e.Position, Math.Max(1, dist ?? 2));
                else return ToolResult.Error($"unknown place or player '{to}'");
                return R(await c.GotoAsync(goal, opts, ct));
            }, longRunning: true);
        tools.Register("follow", "Follow a player until stopped.",
            ToolSchema.Create().Str("player").Num("dist", required: false).Build(),
            async (i, ct) => R(await c.FollowPlayerAsync(i.ReqStr("player"), Math.Clamp(i.Num("dist") ?? 3, 1, 16), ct)), longRunning: true);

        // ---- gathering / crafting
        tools.Register("mine", "Mine N blocks of a type nearby and pick up drops. Skips protected builds.",
            ToolSchema.Create().Str("block").Int("count", required: false).Int("radius", required: false).Build(),
            async (i, ct) =>
            {
                // Live 10-27 15:55: "mine cobble" dug the island floor next to the dual gen. Cobble/stone come from the
                // generator's dig cells only: route to the generator loop, or ask which blocks are its cells.
                // A missing pickaxe is acquired first (chest, hive, craft, bootstrap); if that fails the client's own honest
                // result follows, led by why no tool could be had.
                var tool = await AcquireTool.ToolForBlockAsync(c, i.ReqStr("block"), ct);
                if (Blocks.ByName(i.ReqStr("block")) is { Id: Blocks.Cobblestone or Blocks.Stone } gb)
                {
                    int n = Math.Clamp(i.Int("count") ?? options.DefaultMineCount, 1, 20000);
                    var gr = await MineFromGeneratorAsync(c, gb.Id, n, generatorCells, generator, ct,
                        () => c.MineAsync(gb.Name, Math.Min(n, 256), Math.Clamp(i.Int("radius") ?? options.DefaultMineRadius, 2, 48), ct));
                    return tool is { } gt && !gt.Note.StartsWith("had ", StringComparison.Ordinal) ? gr with { Text = gt.Note + "; " + gr.Text } : gr;
                }
                var r = R(await c.MineAsync(i.ReqStr("block"), Math.Clamp(i.Int("count") ?? options.DefaultMineCount, 1, 256),
                    Math.Clamp(i.Int("radius") ?? options.DefaultMineRadius, 2, 48), ct));
                return tool is { } t && !t.Note.StartsWith("had ", StringComparison.Ordinal) ? r with { Text = t.Note + "; " + r.Text } : r;
            }, longRunning: true);
        tools.Register("harvest_generator",
            "Repeatedly mine a cobble generator's output block. Big counts run unattended: deposits when full (store), re-tools, eats.",
            ToolSchema.Create().Str("at", "output block " + At + ", or auto").Int("count", "up to 20000").Str("store", "chest " + At, required: false)
                .Str("stand", "taught stand spot x,y,z (default: the demo's)", required: false).Build(),
            async (i, ct) =>
            {
                var at = i.ReqStr("at");
                string? autoNote = null;
                BlockPos? p;
                if (IsAuto(at))
                {
                    // Several bots running one script (live 10-28: hive_acff51 hardcoded 5,64,7 for all three clones): each takes
                    // a distinct free taught/confirmed cell through the claim board.
                    (p, autoNote) = AutoGeneratorCell(c, generatorCells);
                    if (p is null) return ToolResult.Error(autoNote ?? "needs_input: which blocks are the cobble generator's cells? Punch them");
                    at = Locations.Fmt(p.Value);
                }
                else p = Locations.ParseCoords(at) ?? places.Resolve(at, "mine") ?? places.Resolve(at, "output") ?? places.Resolve(at);
                if (p is not { } g) return ToolResult.Error($"unknown generator '{at}'");
                // Hive: another bot already mines that cell -> take a free dig cell of the same generator instead.
                var (cell, note) = PickGeneratorCell(c, g, generatorCells);
                if (cell is null && GameClients.Claimed(c) != null && (options?.GeneratorQueueSeconds ?? 0) > 0)
                {
                    // More bots than cells (live 21:44: main, 67 and 1337 on one gen): queue for a free cell instead of failing.
                    var until = DateTime.UtcNow.AddSeconds(options!.GeneratorQueueSeconds);
                    while (cell is null && DateTime.UtcNow < until)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(2, Math.Max(0.05, options.GeneratorQueueSeconds / 10.0))), ct);
                        (cell, note) = PickGeneratorCell(c, g, generatorCells);
                    }
                    if (cell is null)
                        return ToolResult.Success($"standing by: {note}; waited {options.GeneratorQueueSeconds}s, dug nothing (help another way: a deposit run, or try again)");
                    note = "waited for a free cell" + (note == null ? "" : "; " + note);
                }
                if (cell is not { } pick) return ToolResult.Error(note ?? $"no free dig cell at {Locations.Fmt(g)}");
                if (autoNote != null) note = note == null ? autoNote : autoNote + "; " + note;
                // Only a generator cell (lava + water next to it, or a taught/regen-confirmed one) is ever dug here.
                // (A named place is a taught key; the yield guard still stops on a wrong one.)
                if (Locations.ParseCoords(at) != null && !IsGeneratorCell(c, pick, generatorCells))
                    return ToolResult.Error($"{Locations.Fmt(pick)} isn't a generator cell (no lava and water next to it): need the right cell");
                // store:true (live 21:12 "unknown chest 'true'") = the gen's own store / nearest chest; store:false = keep it.
                var storeArg = i.Str("store")?.Trim();
                bool storeYes = storeArg is not null && storeArg.ToLowerInvariant() is "true" or "yes" or "1" or "chest" or "default";
                bool storeNo = storeArg is not null && storeArg.ToLowerInvariant() is "false" or "no" or "0" or "none";
                if (storeYes || storeNo) storeArg = null;
                BlockPos? store = storeArg is { } st ? Locations.ParseCoords(st) ?? places.Resolve(st, "chest") ?? places.Resolve(st) : null;
                if (store == null && storeArg is { } bad) return ToolResult.Error($"unknown chest '{bad}' (store takes true, x,y,z or a chest/waypoint name)");
                // Default: the generator's own store key (only if that really is a chest; Resolve falls back to corners).
                BlockPos? IfChest(BlockPos? q) => q is { } x && c.World.GetBlock(x.X, x.Y, x.Z) == MineClaw.Data.Blocks.Chest ? x : null;
                if (!storeNo) store ??= IfChest(places.Resolve(at, "store")) ?? IfChest(places.Resolve(at, "chest"));
                if (storeYes) store ??= NearestChest(c);
                // Taught stand spot (B12/B21): dig the cell only from where the player stood in the demo, staying put there.
                BlockPos? stand = null;
                if (i.Str("stand") is { Length: > 0 } sa)
                {
                    stand = Locations.ParseCoords(sa);
                    if (stand == null) return ToolResult.Error($"stand must be x,y,z (got '{sa}')");
                }
                BlockPos? Taught(BlockPos cellPos)
                {
                    // A taught stand that can't reach this cell (taught for another one) is no stand for it.
                    try { return generator?.TaughtStand?.Invoke(cellPos) is { } t && GeneratorStand.Reaches(c.World, t, cellPos) ? t : null; }
                    catch (Exception) { return null; } // memory busy: the client picks a spot
                }
                if (stand is { } given && !GeneratorStand.Reaches(c.World, given, pick))
                {
                    // Live 21:12: one stand (3,64,4) passed for every cell; 6,64,8 was out of reach from it. The cell's own taught
                    // stand, else a spot the client finds itself; never a refusal for a stand that can't work this cell.
                    var own = Taught(pick);
                    note = (note == null ? "" : note + "; ") + $"stand {Locations.Fmt(given)} can't reach {Locations.Fmt(pick)}: "
                           + (own is { } o ? $"using its taught stand {Locations.Fmt(o)}" : "finding a spot that reaches it");
                    stand = own;
                }
                else if (stand == null) stand = Taught(pick);
                // Every cell of this generator the stand reaches is dug in rotation (never hammering one cell).
                var rotation = (generatorCells?.Invoke(pick) ?? Perception.BlockDescriber.GenCellsNear(c.World, pick, 4))
                    .Where(x => x != pick && Math.Abs(x.X - pick.X) <= 4 && x.Y == pick.Y && Math.Abs(x.Z - pick.Z) <= 4)
                    .Where(x => IsGeneratorCell(c, x, generatorCells)).Distinct().ToList();
                var loop = new GeneratorLoop(c, generator);
                var r = R(await loop.RunAsync(pick, Math.Clamp(i.Int("count") ?? 16, 1, 20000), store, ct, stand, rotation));
                // Two bots picked the same free cell at once: the lease went to the other one. Take the next free cell.
                for (int retry = 0; retry < 2 && !r.Ok && r.Text.Contains("busy:", StringComparison.Ordinal); retry++)
                {
                    var (next, why) = PickGeneratorCell(c, pick, generatorCells);
                    if (next is not { } n2 || n2 == pick) break;
                    note = (note == null ? "" : note + "; ") + why;
                    pick = n2;
                    stand = Taught(pick);
                    r = R(await loop.RunAsync(pick, Math.Clamp(i.Int("count") ?? 16, 1, 20000), store, ct, stand));
                }
                r = AskOnceIfEnclosed(r, pick);
                return note == null ? r : r with { Text = note + "; " + r.Text };
            }, longRunning: true);
        tools.Register("collect_drops", "Pick up dropped items nearby.",
            ToolSchema.Create().Int("radius", "default 8", required: false).Build(),
            async (i, ct) => R(await c.CollectDropsAsync(Math.Clamp(i.Int("radius") ?? 8, 2, 24), ct)), longRunning: true);
        tools.Register("craft", "Craft items, resolving sub-recipes; smelts prerequisites and places a crafting table if needed.",
            ToolSchema.Create().Str("item").Int("count", required: false).Build(),
            async (i, ct) =>
            {
                // "20 cobble slabs" in item: the amount counts when count is left out.
                var (n, name) = Items.ParseAmount(i.ReqStr("item"));
                if (n is not null && Items.ByNameWithDamage(name) is { } part && Items.ByNameWithDamage(i.ReqStr("item")) is { } whole
                    && (part.Item.Id, part.Damage) != (whole.Item.Id, whole.Damage)) n = null; // "disc 13" is a name, not 13 discs
                return R(await crafter.CraftAsync(n is null ? i.ReqStr("item") : name, Math.Clamp(i.Int("count") ?? n ?? 1, 1, 576), ct));
            }, longRunning: true);
        tools.Register("smelt", "Load a furnace with items + fuel. wait=stay and collect.",
            ToolSchema.Create().Str("item", "input").Int("count", required: false).Str("fuel", required: false)
                .Bool("wait", required: false).Str("at", "furnace " + At, required: false).Build(),
            async (i, ct) =>
            {
                if (Items.ByName(i.ReqStr("item")) is not { } input) return ToolResult.Error($"unknown item '{i.Str("item")}'");
                int? fuel = null;
                if (i.Str("fuel") is { } f) { if (Items.ByName(f) is { } fi) fuel = fi.Id; else return ToolResult.Error($"unknown fuel '{f}'"); }
                var at = OptionalAt(i, places, out var err);
                if (err != null) return err;
                int count = Math.Clamp(i.Int("count") ?? c.World.Inventory.CountItem(input.Id), 1, 576);
                return R(await c.SmeltAsync(input.Id, count, fuel, i.Bool("wait") == true, at, ct));
            }, longRunning: true);
        tools.Register("collect_furnace", "Take finished items from a furnace (last used if omitted).",
            ToolSchema.Create().Str("at", At, required: false).Build(),
            async (i, ct) =>
            {
                var at = OptionalAt(i, places, out var err);
                return err ?? R(await c.CollectFurnaceAsync(at, ct));
            }, longRunning: true);

        // ---- containers
        tools.Register("deposit", "Put items in a chest (nearest if omitted). Default: all but tools, armor, food. Lua: deposit{} or deposit{\"x,y,z\",{\"log\"}}.",
            ToolSchema.Create().Str("at", At, required: false).StrArray("items", "only these", required: false).Build(),
            async (i, ct) =>
            {
                var names = i.StrList("items");
                var asked = DepositAt(i, places, names, out var err);
                if (err != null) return err;
                Func<ItemStack, bool>? filter = null;
                if (names.Count > 0)
                {
                    var ids = new HashSet<int>();
                    foreach (var n in names)
                        if (Items.ByName(n) is { } it) ids.Add(it.Id);
                        else return ToolResult.Error($"unknown item '{n}'");
                    filter = s => ids.Contains(s.Id);
                }
                if (asked is { } chosen) return R(await c.DepositAllAsync(chosen, filter, ct));
                return await DepositAnywhereAsync(c, generator?.Observations, filter, ct);
            }, longRunning: true);
        tools.Register("withdraw", "Take items from a chest (nearest if omitted).",
            ToolSchema.Create().Str("item").Int("count", "default 64", required: false).Str("at", At, required: false).Build(),
            async (i, ct) =>
            {
                if (Items.ByNameWithDamage(i.ReqStr("item")) is not { } it) return ToolResult.Error($"unknown item '{i.Str("item")}'");
                var at = OptionalAt(i, places, out var err) ?? NearestChest(c);
                if (err != null) return err;
                if (at == null) return ToolResult.Error("no chest nearby");
                return R(await c.WithdrawAsync(it.Item.Id, Math.Clamp(i.Int("count") ?? 64, 1, 2304), at, it.Damage < 0 ? null : it.Damage, ct));
            }, longRunning: true);
        tools.Register("organize_chest", "Merge+sort a chest; with several (';'-separated, or 'all' within 8), group items by category per chest.",
            ToolSchema.Create().Str("at", At + ", default nearest", required: false).Build(),
            async (i, ct) =>
            {
                var list = new List<BlockPos>();
                var spec = i.Str("at");
                if (string.IsNullOrWhiteSpace(spec)) { if (NearestChest(c) is { } n) list.Add(n); }
                else if (spec.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
                    list.AddRange(c.World.FindBlocks(c.World.Player.Position, 8, Blocks.Chest).Select(f => f.Pos));
                else
                    foreach (var part in spec.Split(';', '|'))
                    {
                        var s = part.Trim();
                        if (s.Length == 0) continue;
                        if (Locations.ParseCoords(s) is { } p) list.Add(p);
                        else if (places.Positions(s) is { Count: > 0 } ps) list.AddRange(ps);
                        else return ToolResult.Error($"unknown chest '{s}'");
                    }
                if (list.Count == 0) return ToolResult.Error("no chest found");
                return R(await organizer.OrganizeAsync(list, ct));
            }, longRunning: true);

        // ---- interaction
        tools.Register("place", "Place a block from inventory at x,y,z.",
            ToolSchema.Create().Str("block").Str("at", "x,y,z").Build(),
            async (i, ct) =>
            {
                if (Items.ByName(i.ReqStr("block")) is not { } it) return ToolResult.Error($"unknown block '{i.Str("block")}'");
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } p) return ToolResult.Error("at must be x,y,z");
                return R(await c.PlaceBlockAsync(p.X, p.Y, p.Z, it.Id, ct));
            }, longRunning: true);
        tools.Register("dig", "Break one block at x,y,z.",
            ToolSchema.Create().Str("at", "x,y,z").Build(),
            async (i, ct) => Locations.ParseCoords(i.ReqStr("at")) is { } p ? R(await c.DigBlockAsync(p.X, p.Y, p.Z, ct)) : ToolResult.Error("at must be x,y,z"),
            longRunning: true);
        tools.Register("use_block", "Right-click a block (door, lever, button, bed...).",
            ToolSchema.Create().Str("at", At).Build(),
            async (i, ct) =>
            {
                var at = i.ReqStr("at");
                if (Resolve(at, places) is not { } p) return ToolResult.Error($"unknown place '{at}'");
                return R(await c.ActivateBlockAsync(p.X, p.Y, p.Z, ct));
            }, longRunning: true);
        tools.Register("eat", "Eat the best spare food.", ToolSchema.Empty, async (_, ct) => R(await c.EatAsync(ct)), longRunning: true);
        TreeTools.Register(tools, c, places);
        AcquireTool.Register(tools, c); // hidden: scripts' ensure/acquire (no definition in any prefix)
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// <c>mine cobblestone/stone</c> → the generator loop on the nearest known dig cell (taught first, then regen-confirmed or
    /// lava+water geometry). Never the floor around it. No known cell → ask which blocks are the generator's cells.
    /// </summary>
    private static async Task<ToolResult> MineFromGeneratorAsync(IGameClient c, int blockId, int count,
        Func<BlockPos, IReadOnlyList<BlockPos>>? generatorCells, GeneratorSupport? generator, CancellationToken ct,
        Func<Task<ActionResult>>? ownBlocksOnly = null)
    {
        var me = c.World.Player.Position.ToBlockPos();
        var cells = (Acquisition.Of(c)?.GeneratorCells?.Invoke() ?? generatorCells?.Invoke(me) ?? [])
            .Where(p => c.World.GetBlock(p) is Blocks.Cobblestone or Blocks.Stone or 0 || BlockShapes.IsLava(c.World.GetBlock(p)))
            .OrderBy(p => p.DistanceSquaredTo(me)).ToList();
        if (cells.Count == 0 && ownBlocksOnly != null)
        {
            // No generator known: the client's mine still takes only my own/permitted cobble (never floors or walls).
            var own = await ownBlocksOnly().ConfigureAwait(false);
            if (own.Ok) return R(own) with { Text = "no known generator, took only my own placed blocks; " + own.Message };
        }
        if (cells.Count == 0)
            return ToolResult.Error($"needs_input: which blocks are the cobble generator's cells? Punch them and I'll mine only those " +
                                    $"(default: none: I never dig floors or walls for {Names.Item(blockId)})");
        var (cell, note) = PickGeneratorCell(c, cells[0], generatorCells);
        if (cell is not { } pick) return ToolResult.Error(note ?? $"no free dig cell at {Locations.Fmt(cells[0])}");
        BlockPos? stand = null;
        try { stand = generator?.TaughtStand?.Invoke(pick); } catch (Exception) { /* memory busy: the client picks a spot */ }
        var r = R(await new GeneratorLoop(c, generator).RunAsync(pick, count, null, ct, stand));
        return r with { Text = $"mining at the generator cell {Locations.Fmt(pick)} (never the floor around it)" + (note == null ? "" : "; " + note) + "; " + r.Text };
    }

    internal static ToolResult R(ActionResult a) => a.Ok ? ToolResult.Success(a.Message) : ToolResult.Error(a.Message);

    /// <summary>
    /// Cheap relational hints next to the precise output, so the model can talk like a player: where the bot is relative to
    /// the nearest named landmark, and where the player who asked is relative to the bot ("you: 2 E of cobble_gen; kiwi 3 W of you").
    /// </summary>
    internal static string Hints(IGameClient c, Func<double, double, double, string?>? landmark)
    {
        if (!c.IsPlaying) return "";
        var p = c.World.Player.Position;
        var parts = new List<string>(2);
        try
        {
            if (landmark?.Invoke(p.X, p.Y, p.Z) is { Length: > 0 } lm) parts.Add("you: " + lm);
        }
        catch (Exception) { /* hints are best-effort */ }
        if (Agent.TurnContext.Current?.Speaker is { } who && !who.Equals(c.Username, StringComparison.OrdinalIgnoreCase)
            && c.World.Entities.FindPlayer(who) is { } e)
        {
            var off = Agent.Memory.Landmarks.Offset(p.X, p.Z, e.Position.X, e.Position.Z);
            parts.Add(off == "here" ? $"{who} right by you" : $"{who} {off} of you");
        }
        return string.Join("; ", parts);
    }

    private static Task<ToolResult> Ok(string text) => Task.FromResult(ToolResult.Success(text));

    internal static BlockPos? Resolve(string spec, IPlaceResolver places) => Locations.ParseCoords(spec) ?? places.Resolve(spec);

    private static Goal Near(BlockPos p, double dist) => dist <= 0 ? new GoalBlock(p.X, p.Y, p.Z) : new GoalNear(p.X, p.Y, p.Z, dist);

    private static BlockPos? OptionalAt(JsonElement i, IPlaceResolver places, out ToolResult? error)
    {
        error = null;
        var s = i.Str("at");
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = Resolve(s, places);
        if (p == null) error = ToolResult.Error($"unknown place '{s}'");
        return p;
    }

    /// <summary>
    /// deposit's chest. Live 10-27: Lua <c>deposit{"all"}</c> / <c>deposit{"stone_axe"}</c> put an item where the chest goes
    /// ("unknown place 'all'"): "all" means the default, and an item name that isn't a place moves to <paramref name="items"/>.
    /// </summary>
    internal static BlockPos? DepositAt(JsonElement i, IPlaceResolver places, List<string> items, out ToolResult? error)
    {
        error = null;
        var s = i.Str("at")?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (Resolve(s, places) is { } p) return p;
        if (s.Equals("all", StringComparison.OrdinalIgnoreCase) || s.Equals("everything", StringComparison.OrdinalIgnoreCase)) return null;
        if (Items.ByName(s) != null) { items.Insert(0, s); return null; }
        error = ToolResult.Error($"unknown place '{s}' (deposit{{at?, items?}}: at = chest x,y,z or saved name)");
        return null;
    }

    /// <summary>
    /// <c>deposit</c> with no chest named: the nearest chest, then every other known chest (shared memory's room estimate
    /// first, "full" ones re-checked by opening them), until something goes in. "storage full" only after each was tried,
    /// naming them (live 10-27 23:06: "all known chests full" while two chests upstairs were nearly empty).
    /// </summary>
    internal static async Task<ToolResult> DepositAnywhereAsync(IGameClient c, MineClaw.Agent.Rag.ObservationStore? obs, Func<ItemStack, bool>? filter, CancellationToken ct)
    {
        var storing = c.World.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty && (filter?.Invoke(s) ?? true)).Select(s => (int)s.Id).ToHashSet();
        var why = new List<string>();
        int tried = 0;
        foreach (var chest in GeneratorLoop.Candidates(c, obs, NearestChest(c), storing).Distinct())
        {
            if (++tried > 12) break;
            var r = await c.DepositAllAsync(chest, filter, ct).ConfigureAwait(false);
            if (r.Ok) return ToolResult.Success($"{r.Message} in chest {chest.X},{chest.Y},{chest.Z}");
            if (r.Message.StartsWith("nothing to deposit", StringComparison.Ordinal)) return R(r);
            why.Add($"{chest.X},{chest.Y},{chest.Z}: {(r.Message.Length > 50 ? r.Message[..50] : r.Message)}");
        }
        return tried == 0 ? ToolResult.Error("no chest nearby") : ToolResult.Error("storage full: tried " + string.Join("; ", why.Take(6)));
    }

    private static BlockPos? NearestChest(IGameClient c) =>
        c.World.FindNearestBlock(c.World.Player.Position, c.StationSearchRadius, Blocks.Chest)?.Pos;
}
