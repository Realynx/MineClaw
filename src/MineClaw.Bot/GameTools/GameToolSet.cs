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

    /// <param name="landmark">Nearest named landmark of a point, phrased relationally ("2 E of cobble_gen"); null = no hints.</param>
    public static void RegisterAll(ToolRegistry tools, IGameClient c, IPlaceResolver places, BotOptions? options = null,
        Func<double, double, double, string?>? landmark = null)
    {
        options ??= new BotOptions();
        var crafter = new Crafter(c);
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
        tools.Register("look", "Surroundings: footing, edges, blocks, chests, ores, players, mobs, drops. detail=more.",
            ToolSchema.Create().Int("radius", "default 12", required: false).Bool("detail", required: false).Build(),
            (i, _) =>
            {
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
            ToolSchema.Create().Str("to", "x,y,z, name or player").Num("dist", required: false)
                .Bool("build", "may dig/place on the way", required: false)
                .Str("allow_parkour", "void = gap jumps over the void too; only when poofimafox/kiwi asked for this jump", required: false, enumValues: ["off", "ground", "void"]).Build(),
            async (i, ct) =>
            {
                var to = i.ReqStr("to");
                bool build = i.Bool("build") == true;
                var parkour = i.Str("allow_parkour") switch { "off" => ParkourMode.Off, "void" => ParkourMode.Void, _ => ParkourMode.Ground };
                var opts = GotoOptions.Default with { AllowBreak = build, AllowPlace = build, Parkour = parkour };
                double? dist = i.Num("dist");
                // A saved place in the other dimension: through a known linked portal, or an honest "no known portal".
                if (Locations.ParseCoords(to) == null && places is ICrossDimensionPlaces xd && xd.ResolveElsewhere(to) is { } far)
                    return await WorldIndex.PortalTravel.GotoAsync(c, xd.Portals, far.Pos, far.Dim, opts, dist ?? 1, ct);
                Goal goal;
                if (Resolve(to, places) is { } p) goal = Near(p, dist ?? 1);
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
            async (i, ct) => R(await c.MineAsync(i.ReqStr("block"), Math.Clamp(i.Int("count") ?? options.DefaultMineCount, 1, 256),
                Math.Clamp(i.Int("radius") ?? options.DefaultMineRadius, 2, 48), ct)), longRunning: true);
        tools.Register("harvest_generator", "Repeatedly mine a cobble generator's output block.",
            ToolSchema.Create().Str("at", "output block " + At).Int("count").Build(),
            async (i, ct) =>
            {
                var at = i.ReqStr("at");
                var p = Locations.ParseCoords(at) ?? places.Resolve(at, "mine") ?? places.Resolve(at, "output") ?? places.Resolve(at);
                if (p is not { } g) return ToolResult.Error($"unknown generator '{at}'");
                return R(await c.HarvestGeneratorAsync(g.X, g.Y, g.Z, Math.Clamp(i.Int("count") ?? 16, 1, 512), ct));
            }, longRunning: true);
        tools.Register("collect_drops", "Pick up dropped items nearby.",
            ToolSchema.Create().Int("radius", "default 8", required: false).Build(),
            async (i, ct) => R(await c.CollectDropsAsync(Math.Clamp(i.Int("radius") ?? 8, 2, 24), ct)), longRunning: true);
        tools.Register("craft", "Craft items, resolving sub-recipes; smelts prerequisites and places a crafting table if needed.",
            ToolSchema.Create().Str("item").Int("count", required: false).Build(),
            async (i, ct) => R(await crafter.CraftAsync(i.ReqStr("item"), Math.Clamp(i.Int("count") ?? 1, 1, 576), ct)), longRunning: true);
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
        tools.Register("deposit", "Put items in a chest (nearest if omitted). Default: all but tools, armor, food.",
            ToolSchema.Create().Str("at", At, required: false).StrArray("items", "only these", required: false).Build(),
            async (i, ct) =>
            {
                var at = OptionalAt(i, places, out var err) ?? NearestChest(c);
                if (err != null) return err;
                if (at == null) return ToolResult.Error("no chest nearby");
                Func<ItemStack, bool>? filter = null;
                var names = i.StrList("items");
                if (names.Count > 0)
                {
                    var ids = new HashSet<int>();
                    foreach (var n in names)
                        if (Items.ByName(n) is { } it) ids.Add(it.Id);
                        else return ToolResult.Error($"unknown item '{n}'");
                    filter = s => ids.Contains(s.Id);
                }
                return R(await c.DepositAllAsync(at, filter, ct));
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
    }

    // ------------------------------------------------------------------ helpers

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

    private static BlockPos? NearestChest(IGameClient c) =>
        c.World.FindNearestBlock(c.World.Player.Position, c.StationSearchRadius, Blocks.Chest)?.Pos;
}
