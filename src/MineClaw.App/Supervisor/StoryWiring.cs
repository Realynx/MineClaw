using System.Globalization;
using System.Text;
using MineClaw.Bot;
using MineClaw.Bot.Blueprints;
using MineClaw.Client;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Story;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Supervisor;

/// <summary>
/// Story Mode in main's process (docs/web/story-mode.md): installs the default <c>story_pawn</c> script, loads and resumes the saved
/// colonies, starts their tick, and hands the supervisor its routes. Only with the viewer on (Story Mode is the viewer's tab).
/// </summary>
internal static class StoryWiring
{
    public static SupervisorStoryRoutes? Attach(SupervisorOptions o, BotHost bot, MinecraftClient client, HiveHierarchy? hierarchy, IViewerData? viewer,
        ViewerFeed? feed, HiveBodyList? bodies, ILogger log)
    {
        if (!o.Viewer.Enabled) return null;
        var rt = bot.Runtime;
        var scripts = MineClaw.Bot.GameTools.ScriptTools.Of(rt);
        if (scripts != null)
        {
            try { log.LogInformation("story: default script story_pawn {State}", StoryColonies.Install(scripts.Library)); }
            catch (Exception ex) { log.LogWarning(ex, "story: installing story_pawn failed"); }
        }
        var host = new StoryHost(hierarchy, () => bot.Bodies, () => client.World, client.Options.Profile.Data, () => scripts, log, rt.Observations)
        {
            MainInGame = () => client.IsPlaying,
            // Founding a colony or moving its home is the owner's request: its pawns (the hive's own maid_ bodies) are brought there with
            // /tp, armed in the gate's scoped exception (managed accounts only, the blocklist applies) and sent on main's chat.
            OpCommand = async (line, why) =>
            {
                if (!rt.Commands.ArmManagedTeleport(line, why)) return "refused by the command gate";
                return await rt.Chat.SendCommandAsync(line).ConfigureAwait(false) ? null : "not sent";
            },
            // Fast / Faster on 1.12.2: a Speed effect on the colony's pawns (managed accounts only, the blocklist applies), when main is op.
            MainIsOp = () => client.IsOp ?? o.Viewer.OpAccounts.Contains(client.Options.Username, StringComparer.OrdinalIgnoreCase),
            EffectCommand = async (line, why) =>
            {
                if (!rt.Commands.ArmManagedSpeed(line, why)) return "refused by the command gate";
                return await rt.Chat.SendCommandAsync(line).ConfigureAwait(false) ? null : "not sent";
            },
        };
        var colonies = new StoryColonies(host, new StoryStore(rt.Memory.Db), log: log);
        host.AttachStorageSignGuards(client);
        // Every pawn of an active colony is the colony's, online or still waiting for a seat: the startup reconcile never takes it
        // off the whitelist (live 11-03: it raced the colony's respawn and four pawns were kicked "not white-listed" for good).
        hierarchy?.AddOwner(colonies.Owns);
        try
        {
            int n = colonies.Load();
            if (n > 0) log.LogInformation("story: {N} saved colon{Y} loaded", n, n == 1 ? "y" : "ies");
        }
        catch (Exception ex) { log.LogWarning(ex, "story: loading the saved colonies failed"); }
        colonies.Start();
        StoryStorageTool.RegisterMain(rt.Tools, colonies);
        if (hierarchy?.Bus is { } storageBus) StoryStorageTool.AttachBus(storageBus, colonies);
        ContainerKnowledge.Story = new StoryChests(colonies, () => client.World.Dimension); // the viewer's container panel reads the ledgers
        StoryDirectives? directives = viewer == null ? null : new StoryDirectives(viewer, feed)
        {
            Data = () => client.Options.Profile.Data,
            World = () => host.World,
            SaveDesign = (name, ghosts, by) => bot.Blueprints?.Service is { } svc ? SaveDesign(svc.Store, name, ghosts, by, client.World.Dimension) : null,
        };
        var timeControl = new StoryTimeControl(() => OpAccounts(o, bot, client), (account, line, why) => SendOpAsync(rt, bot, client, account, line, why),
            log: log, store: new StoryTimeRangeStore(rt.Memory.Db));
        timeControl.Start(() => client.IsPlaying ? host.World?.TimeOfDay : null);
        var routes = new SupervisorStoryRoutes(o, colonies, directives, bodies == null ? null : bodies.Rows, viewer, timeControl: timeControl);
        routes.RegisterControllerTools(rt.Tools);
        return routes;
    }

    /// <summary>The accounts this process controls (main, then its in-process bodies) and whether each is op: the server's word (1.12.2),
    /// else the config's <c>Supervisor:Viewer:OpAccounts</c>, else unknown.</summary>
    internal static IReadOnlyList<OpAccount> OpAccounts(SupervisorOptions o, BotHost bot, MinecraftClient client)
    {
        bool? Op(string name, MinecraftClient? c) => c?.IsOp ?? (o.Viewer.OpAccounts.Contains(name, StringComparer.OrdinalIgnoreCase) ? true : null);
        var list = new List<OpAccount> { new(client.Options.Username, true, client.IsPlaying ? Op(client.Options.Username, client) : null) };
        foreach (var b in bot.Bodies?.All ?? [])
            if (b is MineClaw.Bot.Bodies.Body body && !body.IsMain && body.State == MineClaw.Bot.Bodies.BodyState.Online)
                list.Add(new OpAccount(body.Name, false, Op(body.Name, body.Client)));
        return list;
    }

    /// <summary>
    /// One op command from <paramref name="account"/>: main's through the command gate's scoped exception (armed, audited, blocklist
    /// applied) and its gated chat; another body's straight on its own connection after the same blocklist check, audited the same way.
    /// </summary>
    private static async Task<string?> SendOpAsync(MineClaw.Agent.AgentRuntime rt, BotHost bot, MinecraftClient client, string account, string line,
        string why)
    {
        if (string.Equals(account, client.Options.Username, StringComparison.OrdinalIgnoreCase))
        {
            if (!rt.Commands.ArmStoryTime(line, why)) return "refused by the command gate";
            return await rt.Chat.SendCommandAsync(line).ConfigureAwait(false) ? null : "not sent";
        }
        if (rt.Commands.IsBlocked(line)) return "refused: blocked command";
        if (bot.Bodies?.Find(account) is not MineClaw.Bot.Bodies.Body { Client: { } c }) return "no such body";
        var r = await c.SendChatAsync(line, CancellationToken.None).ConfigureAwait(false);
        if (!r.Ok) return r.Message;
        rt.Commands.Audit(line, why + " via " + account);
        return null;
    }

    /// <summary>
    /// A directive's build as a design (the blueprint DSL: size, origin, palette, one layer per y), accepted by the player who asked;
    /// null when it doesn't fit a design (over 32 a side) or has nothing to place.
    /// </summary>
    internal static long? SaveDesign(BlueprintStore store, string name, IReadOnlyList<GhostState> ghosts, string by, int dim)
    {
        var place = ghosts.Where(g => !g.Remove).ToList();
        if (place.Count == 0) return null;
        var box = DesignBox.Of(place);
        if (box.W > BlueprintParser.MaxSide || box.D > BlueprintParser.MaxSide || box.H > BlueprintParser.MaxHeight) return null;
        if (DesignPalette(place) is not { } palette) return null;
        return InsertDesign(store, name, DesignDsl(name, place, box, palette), box, place.Count, by, dim);
    }

    /// <summary>The bounding box of a design's blocks: its low corner and size.</summary>
    private readonly record struct DesignBox(int X1, int Y1, int Z1, int W, int H, int D)
    {
        public static DesignBox Of(List<GhostState> place)
        {
            int x1 = place.Min(g => g.At.X), y1 = place.Min(g => g.At.Y), z1 = place.Min(g => g.At.Z);
            int x2 = place.Max(g => g.At.X), y2 = place.Max(g => g.At.Y), z2 = place.Max(g => g.At.Z);
            return new DesignBox(x1, y1, z1, x2 - x1 + 1, y2 - y1 + 1, z2 - z1 + 1);
        }
    }

    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>One symbol per (block, meta) in order of first use; null when there are more kinds than symbols.</summary>
    private static Dictionary<(int, int), char>? DesignPalette(List<GhostState> place)
    {
        const string Symbols = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var palette = new Dictionary<(int, int), char>();
        foreach (var g in place)
            if (!palette.ContainsKey((g.Id, g.Meta)))
            {
                if (palette.Count >= Symbols.Length) return null;
                palette[(g.Id, g.Meta)] = Symbols[palette.Count];
            }
        return palette;
    }

    /// <summary>The blueprint DSL: name, size, origin, palette, then one layer per y (rows by z, a symbol or '_' per x).</summary>
    private static string DesignDsl(string name, List<GhostState> place, DesignBox b, Dictionary<(int, int), char> palette)
    {
        var sb = new StringBuilder();
        sb.Append("blueprint ").Append(name.Replace('\n', ' ')).Append('\n');
        sb.Append($"size {Inv(b.W)}x{Inv(b.H)}x{Inv(b.D)}\norigin {Inv(b.X1)},{Inv(b.Y1)},{Inv(b.Z1)}\n");
        sb.Append("palette ").Append(string.Join(" ", palette.Select(p =>
            $"{p.Value}={MineClaw.Data.Blocks.Get(p.Key.Item1).Name}{(p.Key.Item2 > 0 ? ":" + Inv(p.Key.Item2) : "")}"))).Append('\n');
        var at = place.GroupBy(g => g.At).ToDictionary(g => g.Key, g => g.Last());
        for (int y = 0; y < b.H; y++)
        {
            sb.Append("layer ").Append(Inv(y)).Append('\n');
            for (int z = 0; z < b.D; z++)
            {
                for (int x = 0; x < b.W; x++)
                    sb.Append(at.TryGetValue(new P3(b.X1 + x, b.Y1 + y, b.Z1 + z), out var g) ? palette[(g.Id, g.Meta)] : '_');
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>Saves the design as accepted by <paramref name="by"/> (revision 1, sited under its middle); its id.</summary>
    private static long InsertDesign(BlueprintStore store, string name, string dsl, DesignBox b, int blocks, string by, int dim)
    {
        var rec = store.Insert(new BlueprintRecord
        {
            Name = name, Description = $"from the Story Mode map for {by}", Requester = by, Dsl = dsl, Status = DesignStatus.Accepted, Dim = dim,
            Site = new MineClaw.World.BlockPos(b.X1 + b.W / 2, b.Y1 - 1, b.Z1 + b.D / 2), Anchor = "where it was drawn",
        });
        rec.Origin = new MineClaw.World.BlockPos(b.X1, b.Y1, b.Z1);
        rec.AcceptedBy = by;
        rec.Rev = 1;
        rec.Summary = $"{name} {Inv(b.W)}×{Inv(b.H)}×{Inv(b.D)}, {Inv(blocks)} blocks (story map)";
        try { store.Update(rec); } catch (Exception) { /* the insert stands */ }
        return rec.Id;
    }
}
