using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.Skills;
using MineClaw.Skills.Inventory;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Exposes the skill catalog to the worker through two fixed meta-tools, so adding skills never grows any cached prefix:
/// <list type="bullet">
/// <item><c>find_skill {query}</c>: ≤ 5 one-line signatures (≤ 600 chars) from the catalog (lexical ranker + the RAG
/// index, where the same signatures live as <c>skill</c> docs so auto-retrieval can surface them too).</item>
/// <item><c>skill {name, args}</c>: runs one skill; the result is a terse line under the normal tool result caps.</item>
/// </list>
/// Delegated tasks also get ≤ 4 matching signatures appended to the task text (the worker's first, uncached message).
/// The brain keeps only <c>delegate</c>; skills never appear in its tool list.
/// </summary>
public sealed class SkillHub
{
    public const string SkillTool = "skill", FindTool = "find_skill";

    public required SkillRegistry Registry { get; init; }
    public required IGameActions Game { get; init; }
    public IClaimBoard Claims { get; init; } = NullClaimBoard.Instance;
    public required ContainerDirectory Directory { get; init; }
    public required ILayoutStore Layouts { get; init; }
    public SkillState State { get; } = new();
    public SkillOptions Options { get; init; } = new();
    /// <summary>Re-reads learned skills (promoted routines, taught structures) from memory.</summary>
    public Action? SyncLearned { get; set; }

    public SkillContext NewContext() => new(Game, Registry, Options)
    {
        Claims = Claims, Directory = Directory, Layouts = Layouts, State = State,
    };

    public Task<SkillResult> RunAsync(string name, JsonElement args, CancellationToken ct) => Registry.RunAsync(NewContext(), name, args, ct);
}

public static class SkillTools
{
    private const string RagPrefix = "skill:";
    private const string RoutineMark = "[skill]";

    /// <summary>The single registration call (BotHost): meta-tools, catalog index, task hints, learned skills.</summary>
    public static SkillHub Register(AgentRuntime rt, IGameClient client, IClaimBoard? claims = null, SkillRegistry? registry = null)
    {
        var game = client is MinecraftGameClient mg ? (IGameActions)new ClientGameActions(mg.Inner) : new GameClientActions(client);
        var directory = new ContainerDirectory { External = name => FindLabelled(rt, client, name) };
        var hub = new SkillHub
        {
            Registry = registry ?? SkillRegistry.CreateDefault(), Game = game, Claims = claims ?? NullClaimBoard.Instance,
            Directory = directory, Layouts = new MemoryLayoutStore(rt),
        };
        hub.Registry.Add(PromoteRoutineSkill(rt, hub));
        // Self-reliant preconditions: a missing hoe/bucket/torch/sapling is acquired (chest, hive, craft, bootstrap) first.
        hub.Registry.AcquireMissing ??= async (_, _, ids, ct) =>
        {
            var r = await Acquisition.Planner(client, rt.Observations).AcquireAnyAsync(ids, ct).ConfigureAwait(false);
            return r.Ok ? r.Text : null;
        };
        hub.SyncLearned = () => SyncLearned(rt, hub);
        hub.SyncLearned();
        IndexCatalog(rt, hub.Registry);
        hub.Registry.Added += _ => IndexCatalog(rt, hub.Registry);

        rt.Tools.Register(SkillHub.FindTool, "Search the skill catalog (doors, farming, building, liquids, storage, nether...): <=5 skill signatures.",
            ToolSchema.Create().Str("query", "what you want done").Build(),
            async (input, ct) => ToolResult.Success(await FindAsync(rt, hub, input.ReqStr("query"), ct)),
            tiers: ToolTiers.Worker, maxResultChars: SkillCatalog.MaxResultChars + 40);

        rt.Tools.Register(SkillHub.SkillTool, "Run a catalog skill by name (find_skill shows names/args). One call does the whole job.",
            ToolSchema.Create().Str("name").Raw("args", """{"type":"object"}""", required: false).Build(),
            async (input, ct) =>
            {
                var args = input.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a : SkillJson.Empty;
                var r = await hub.RunAsync(input.ReqStr("name"), args, ct);
                return r.Ok ? ToolResult.Success(r.Text) : ToolResult.Error(r.Text);
            },
            longRunning: true, tiers: ToolTiers.Worker);

        WrapDelegateWithHints(rt, hub);
        return hub;
    }

    /// <summary>find_skill: lexical catalog ranking, topped up with RAG hits on <c>skill</c> docs.</summary>
    public static async Task<string> FindAsync(AgentRuntime rt, SkillHub hub, string query, CancellationToken ct)
    {
        hub.SyncLearned?.Invoke();
        var specs = hub.Registry.Specs;
        var hits = SkillCatalog.Search(specs, query).ToList();
        if (hits.Count < SkillCatalog.MaxResults && rt.Options.Rag.Enabled)
        {
            try
            {
                var rag = await rt.Rag.SearchAsync(new RagQuery { Text = query, Kinds = ["skill"], Limit = SkillCatalog.MaxResults }, ct);
                foreach (var h in rag)
                    if (hub.Registry.TryGet(h.Ref[RagPrefix.Length..], out var s) && !hits.Contains(s.Spec)) hits.Add(s.Spec);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* lexical result stands */ }
        }
        var text = SkillCatalog.Format(hits);
        return text.Length > 0 ? text : "no matching skill; use the game tools directly";
    }

    /// <summary>The skill catalog as RAG docs (signature = body), refreshed when skills are added; zero prompt tokens.</summary>
    public static void IndexCatalog(AgentRuntime rt, SkillRegistry registry)
    {
        if (!rt.Options.Rag.Enabled) return;
        var docs = registry.Specs.Select(s => new RagDoc(RagPrefix + s.Name, "skill", $"{s.Name.Replace('_', ' ')} {s.Category} {s.Keywords}", s.Signature)
        {
            Weight = 0.7, Source = s.Source,
        }).ToList();
        rt.Rag.UpsertMany(docs);
        rt.Rag.DeleteByPrefix(RagPrefix, docs.Select(d => d.Ref).ToHashSet());
    }

    /// <summary>Same definition, same position (cached prefix untouched); the task text gains skill hints.</summary>
    private static void WrapDelegateWithHints(AgentRuntime rt, SkillHub hub)
    {
        if (!rt.Tools.TryGet(Agent.Worker.WorkerAgent.ToolName, out var inner)) return;
        rt.Tools.Replace(new HintedTool(inner, input =>
        {
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("task", out var t) || t.ValueKind != JsonValueKind.String) return input;
            var task = t.GetString() ?? "";
            var hinted = HintedTask(hub.Registry, task);
            if (ReferenceEquals(hinted, task)) return input;
            var o = JsonNode.Parse(input.GetRawText())!.AsObject();
            o["task"] = hinted;
            return JsonSerializer.SerializeToElement(o);
        }));
    }

    /// <summary>The task text the worker sees: the brain's task plus ≤ 4 matching skill signatures (≈ ≤ 200 tokens).</summary>
    public static string HintedTask(SkillRegistry registry, string task)
    {
        var hints = SkillCatalog.Hints(registry.Specs, task);
        return hints.Length == 0 ? task : task + "\n" + hints;
    }

    private sealed class HintedTool(IAgentTool inner, Func<JsonElement, JsonElement> rewrite) : IAgentTool
    {
        public string Name => inner.Name;
        public string Description => inner.Description;
        public JsonElement InputSchema => inner.InputSchema;
        public bool IsLongRunning => inner.IsLongRunning;
        public bool AvailableToCheapTier => inner.AvailableToCheapTier;
        public ToolTiers Tiers => inner.Tiers;
        public int? MaxResultChars => inner.MaxResultChars;
        public Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct) => inner.ExecuteAsync(rewrite(input), ct);
    }

    // ------------------------------------------------------------------ learned skills

    /// <summary>promote_routine: turns a working saved routine into a named catalog skill (persisted via the routine's note).</summary>
    private static ISkill PromoteRoutineSkill(AgentRuntime rt, SkillHub hub) => new PromotedSkill(new SkillSpec
    {
        Name = "promote_routine", Category = SkillCategory.Misc, Summary = "make a saved routine a named catalog skill", Args = "routine,summary",
        Schema = SkillJson.Schema("routine:str,summary:str"), Estimate = TimeSpan.FromSeconds(1),
        Keywords = "promote routine skill save learn remember macro",
    }, (_, args, _) =>
    {
        var name = SkillJson.Str(args, "routine") ?? "";
        var r = rt.Memory.GetRoutine(name);
        if (r is null) return Task.FromResult(SkillResult.Fail($"no routine '{name}'"));
        var summary = (SkillJson.Str(args, "summary") ?? r.Note ?? $"replay {r.Name}").Replace(RoutineMark, "").Trim();
        r.Note = $"{RoutineMark} {summary}";
        rt.Memory.SaveRoutine(r);
        hub.SyncLearned?.Invoke();
        return Task.FromResult(SkillResult.Success($"routine {r.Name} is now skill {PromotedSkill.SkillName(r.Name)}"));
    });

    private static void SyncLearned(AgentRuntime rt, SkillHub hub)
    {
        var (routines, structures) = rt.Memory.Read(d => (
            d.Routines.Where(r => r.Note?.StartsWith(RoutineMark, StringComparison.Ordinal) == true).Select(r => (r.Name, r.Note!)).ToList(),
            d.Structures.Where(s => s.Steps.Count > 0).Select(s => (s.Name, s.Kind, Steps: s.Steps.ToList(), s.Min)).ToList()));
        foreach (var (name, note) in routines)
        {
            if (hub.Registry.TryGet(PromotedSkill.SkillName(name), out var existing) && existing.Spec.Source == "routine") continue;
            hub.Registry.Add(PromotedSkill.FromRoutine(name, note[RoutineMark.Length..].Trim(), async (repeat, ct) =>
            {
                var r = rt.Memory.GetRoutine(name);
                if (r is null) return (false, $"routine {name} was forgotten");
                var res = await rt.Routines.RunAsync(r, repeat, ct);
                return (res.Ok, res.Text);
            }));
        }
        foreach (var s in structures)
        {
            var key = "use_" + PromotedSkill.SkillName(s.Name);
            if (hub.Registry.TryGet(key, out var existing) && existing.Spec.Source == "structure") continue;
            hub.Registry.Add(PromotedSkill.FromStructure(s.Name, s.Kind, s.Steps, s.Min is { } m ? new BlockPos(m.X, m.Y, m.Z) : null));
        }
    }

    /// <summary>Chest by WorldIndex label / waypoint name ("Iron storage").</summary>
    private static BlockPos? FindLabelled(AgentRuntime rt, IGameClient client, string name)
    {
        // Waypoints are per dimension: an overworld chest name never resolves while in the nether.
        if (rt.Memory.GetWaypoint(name) is { } w && w.Dimension == client.World.Dimension) return new BlockPos(w.X, w.Y, w.Z);
        var p = client.World.Player.Position;
        foreach (var (row, _) in rt.Observations.Nearest("chest", p.X, p.Y, p.Z, limit: 32, maxDistance: 64))
            if (row.Text.Contains(name, StringComparison.OrdinalIgnoreCase) && row.Bounds is { } b)
                return new BlockPos((int)b.MinX, (int)b.MinY, (int)b.MinZ);
        return null;
    }

    /// <summary>Chest layouts remembered as notes ("layout@x,y,z").</summary>
    private sealed class MemoryLayoutStore(AgentRuntime rt) : ILayoutStore
    {
        private static string Key(BlockPos p) => $"layout@{p.X},{p.Y},{p.Z}";

        public string? Get(BlockPos chest) => rt.Memory.Read(d => d.Notes.LastOrDefault(n => n.Subject == Key(chest))?.Text);

        public void Save(BlockPos chest, string template)
        {
            if (Get(chest) == template) return;
            rt.Memory.Update(d => d.Notes.RemoveAll(n => n.Subject == Key(chest)));
            rt.Memory.AddNote(Key(chest), template);
        }
    }
}

/// <summary><see cref="IGameActions"/> over the bot's <see cref="IGameClient"/> (fakes, wrappers). Look/use primitives
/// the interface lacks report themselves as unavailable.</summary>
internal sealed class GameClientActions(IGameClient c) : IGameActions
{
    public WorldState World => c.World;
    public string Username => c.Username;
    public Primitive Supported => Primitive.None;
    public bool IsRaining => false;
    public double Reach => 4.25;
    public IReadOnlySet<int> ReservedItems => c.ReservedItems;
    public bool IsProtected(BlockPos pos, int id) => c.IsProtected?.Invoke(pos, id) ?? false;
    public IReadOnlyDictionary<BlockPos, string[]> Signs { get; } = new Dictionary<BlockPos, string[]>();

    public Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options, CancellationToken ct) => c.GotoAsync(goal, options, ct);
    public Task<ActionResult> DigAsync(BlockPos p, CancellationToken ct) => c.DigBlockAsync(p.X, p.Y, p.Z, ct);
    public Task<ActionResult> PlaceAsync(BlockPos p, int itemId, CancellationToken ct) => c.PlaceBlockAsync(p.X, p.Y, p.Z, itemId, ct);
    public Task<ActionResult> ActivateAsync(BlockPos p, CancellationToken ct) => c.ActivateBlockAsync(p.X, p.Y, p.Z, ct);
    public Task<ActionResult> LookAtAsync(Vec3d point, CancellationToken ct) => No("look");
    public Task<ActionResult> UseItemAsync(int? itemId, CancellationToken ct) => No("use_item");
    public Task<ActionResult> EatAsync(CancellationToken ct) => c.EatAsync(ct);
    public Task<ActionResult> CollectDropsAsync(double radius, CancellationToken ct) => c.CollectDropsAsync(radius, ct);
    public Task<ActionResult> MineAsync(string blockName, int count, double radius, CancellationToken ct) => c.MineAsync(blockName, count, radius, ct);
    public Task<ActionResult> CraftAsync(string itemName, int count, CancellationToken ct) =>
        Items.ByNameWithDamage(itemName) is { } i ? c.CraftAsync(i.Item.Id, i.Damage, count, ct) : Task.FromResult(ActionResult.Fail($"unknown item '{itemName}'"));
    public Task<ActionResult> SmeltAsync(int input, int count, int? fuel, bool wait, BlockPos? furnace, CancellationToken ct) => c.SmeltAsync(input, count, fuel, wait, furnace, ct);
    public Task<ActionResult> CollectFurnaceAsync(BlockPos? furnace, CancellationToken ct) => c.CollectFurnaceAsync(furnace, ct);
    public Task<ActionResult> OpenContainerAsync(BlockPos p, CancellationToken ct) => c.OpenContainerAsync(p.X, p.Y, p.Z, ct);
    public Task<ActionResult> CloseWindowAsync(CancellationToken ct) => c.CloseWindowAsync(ct);
    public Task<ActionResult> ClickSlotAsync(int slot, bool rightClick, bool shift, CancellationToken ct) => c.ClickSlotAsync(slot, rightClick, shift, ct);
    public Task<ActionResult> DepositAllAsync(BlockPos? container, Func<ItemStack, bool>? filter, CancellationToken ct) => c.DepositAllAsync(container, filter, ct);
    public Task<ActionResult> WithdrawAsync(int itemId, int count, BlockPos? container, int? damage, CancellationToken ct) => c.WithdrawAsync(itemId, count, container, damage, ct);
    public Task<ActionResult> SendChatAsync(string text, CancellationToken ct) => c.SendChatAsync(text, ct);
    public Task WaitTicksAsync(int ticks, CancellationToken ct) => Task.Delay(TimeSpan.FromMilliseconds(50 * Math.Max(1, ticks)), ct);

    public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= end) return false;
            await Task.Delay(50, ct);
        }
        return true;
    }

    public Task<ActionResult> UseOnBlockAsync(BlockPos pos, BlockFace face, int itemId, CancellationToken ct) => No("use_on_block");
    public Task<ActionResult> UseEntityAsync(int entityId, int? itemId, CancellationToken ct) => No("use_entity");
    public Task<ActionResult> WriteSignAsync(BlockPos pos, string[] lines, CancellationToken ct) => No("write_sign");
    public Task<ActionResult> ReleaseUseAsync(CancellationToken ct) => No("release_use");
    public Task<ActionResult> DropHeldAsync(bool wholeStack, CancellationToken ct) => No("drop_held");

    private static Task<ActionResult> No(string what) => Task.FromResult(ActionResult.Fail($"needs client primitive {what}"));
}
