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
        var host = new StoryHost(hierarchy, () => bot.Bodies, () => client.World, client.Options.Profile.Data, () => scripts, log);
        var colonies = new StoryColonies(host, new StoryStore(rt.Memory.Db), log: log);
        try
        {
            int n = colonies.Load();
            if (n > 0) log.LogInformation("story: {N} saved colon{Y} loaded", n, n == 1 ? "y" : "ies");
        }
        catch (Exception ex) { log.LogWarning(ex, "story: loading the saved colonies failed"); }
        colonies.Start();
        StoryDirectives? directives = viewer == null ? null : new StoryDirectives(viewer, feed)
        {
            Data = () => client.Options.Profile.Data,
            World = () => host.World,
            SaveDesign = (name, ghosts, by) => bot.Blueprints?.Service is { } svc ? SaveDesign(svc.Store, name, ghosts, by, client.World.Dimension) : null,
        };
        return new SupervisorStoryRoutes(o, colonies, directives, bodies == null ? null : bodies.Rows, viewer);
    }

    /// <summary>
    /// A directive's build as a design (the blueprint DSL: size, origin, palette, one layer per y), accepted by the player who asked;
    /// null when it doesn't fit a design (over 32 a side) or has nothing to place.
    /// </summary>
    internal static long? SaveDesign(BlueprintStore store, string name, IReadOnlyList<GhostState> ghosts, string by, int dim)
    {
        var place = ghosts.Where(g => !g.Remove).ToList();
        if (place.Count == 0) return null;
        int x1 = place.Min(g => g.At.X), y1 = place.Min(g => g.At.Y), z1 = place.Min(g => g.At.Z);
        int x2 = place.Max(g => g.At.X), y2 = place.Max(g => g.At.Y), z2 = place.Max(g => g.At.Z);
        int w = x2 - x1 + 1, h = y2 - y1 + 1, d = z2 - z1 + 1;
        if (w > BlueprintParser.MaxSide || d > BlueprintParser.MaxSide || h > BlueprintParser.MaxHeight) return null;
        const string Symbols = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var palette = new Dictionary<(int, int), char>();
        var sb = new StringBuilder();
        string F(int v) => v.ToString(CultureInfo.InvariantCulture);
        sb.Append("blueprint ").Append(name.Replace('\n', ' ')).Append('\n');
        sb.Append($"size {F(w)}x{F(h)}x{F(d)}\norigin {F(x1)},{F(y1)},{F(z1)}\n");
        foreach (var g in place)
            if (!palette.ContainsKey((g.Id, g.Meta)))
            {
                if (palette.Count >= Symbols.Length) return null;
                palette[(g.Id, g.Meta)] = Symbols[palette.Count];
            }
        sb.Append("palette ").Append(string.Join(" ", palette.Select(p =>
            $"{p.Value}={MineClaw.Data.Blocks.Get(p.Key.Item1).Name}{(p.Key.Item2 > 0 ? ":" + F(p.Key.Item2) : "")}"))).Append('\n');
        var at = place.GroupBy(g => g.At).ToDictionary(g => g.Key, g => g.Last());
        for (int y = 0; y < h; y++)
        {
            sb.Append("layer ").Append(F(y)).Append('\n');
            for (int z = 0; z < d; z++)
            {
                for (int x = 0; x < w; x++)
                    sb.Append(at.TryGetValue(new P3(x1 + x, y1 + y, z1 + z), out var g) ? palette[(g.Id, g.Meta)] : '_');
                sb.Append('\n');
            }
        }
        var rec = store.Insert(new BlueprintRecord
        {
            Name = name, Description = $"from the Story Mode map for {by}", Requester = by, Dsl = sb.ToString(), Status = DesignStatus.Accepted, Dim = dim,
            Site = new MineClaw.World.BlockPos(x1 + w / 2, y1 - 1, z1 + d / 2), Anchor = "where it was drawn",
        });
        rec.Origin = new MineClaw.World.BlockPos(x1, y1, z1);
        rec.AcceptedBy = by;
        rec.Rev = 1;
        rec.Summary = $"{name} {F(w)}×{F(h)}×{F(d)}, {F(place.Count)} blocks (story map)";
        try { store.Update(rec); } catch (Exception) { /* the insert stands */ }
        return rec.Id;
    }
}
