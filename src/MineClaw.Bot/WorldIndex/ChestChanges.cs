using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.WorldIndex;

/// <summary>
/// Shared chest memory meets reality (live 10-27 21:06: main deposited 5 bread, Fox took them back out unseen, clone 67 was
/// told "bread x5 (seen just now)" and retried the take twice). When an open shows that items memory listed are gone, the
/// brain hears it once as context ("chest at 14,64,-5: bread x5 gone since maid_chan opened it 55s ago; PoofimaFox may have
/// taken it (holding bread)") and a failed <c>withdraw</c> says the same in its result. Suspects only from what the bot
/// can know: a nearby player holding a missing item, a player the activity log saw open/take from that chest since, or a
/// player standing near it now. Zero tokens.
/// </summary>
public static class ChestChanges
{
    /// <param name="current">One brain, many bodies: the indexer of the body a withdraw runs on (a clone's contributor
    /// indexer saw that chest open), else <paramref name="indexer"/> (main's).</param>
    public static void Attach(AgentRuntime rt, IGameClient client, WorldIndexer indexer, Func<string, bool> isBot, TimeProvider? time, ILogger? log,
        Func<WorldIndexer?>? current = null)
    {
        time ??= TimeProvider.System;
        AttachVanished(rt, client, indexer, isBot, time, log);
        if (!rt.Tools.TryGet("withdraw", out var inner)) return;
        rt.Tools.Replace(new Ledger.InventoryLedgerTools.WrappedTool(inner, null, async (input, ct) =>
        {
            var r = await inner.ExecuteAsync(input, ct).ConfigureAwait(false);
            if (r.Ok) return r;
            try
            {
                var idx = current?.Invoke() ?? indexer;
                idx.Pump(); // the take just closed the window: write the real contents for every hive bot now
                if (idx.RecentVanished(TimeSpan.FromSeconds(20)) is { } v)
                    return r with { Text = r.Text + " | " + Describe(v, client.World, rt.Db, isBot, time.GetUtcNow()) };
            }
            catch (Exception ex) { log?.LogDebug(ex, "withdraw chest note failed"); }
            return r;
        }));
    }

    /// <summary>The "gone since memory" context note when an open by this indexer's body shows missing items (a clone body
    /// passes its short name: the note carries it).</summary>
    public static void AttachVanished(AgentRuntime rt, IGameClient client, WorldIndexer indexer, Func<string, bool> isBot, TimeProvider? time, ILogger? log,
        string? body = null)
    {
        var clock = time ?? TimeProvider.System;
        indexer.ContentsVanished += v =>
        {
            try
            {
                var text = Describe(v, client.World, rt.Db, isBot, clock.GetUtcNow());
                log?.LogInformation("chest memory: {Text}", text);
                rt.Events.Post(GameEvent.Notice(text, EventPriority.Background) with { Body = body });
            }
            catch (Exception ex) { log?.LogDebug(ex, "chest change note failed"); }
        };
    }

    /// <summary>"chest at 14,64,-5: bread x5 gone since maid_chan opened it 1m ago; PoofimaFox may have taken it (holding bread)".</summary>
    public static string Describe(ContainerVanished v, WorldState world, AgentDb? db, Func<string, bool> isBot, DateTimeOffset now)
    {
        var gone = string.Join(", ", v.Gone.Take(4).Select(g => $"{g.Name.Replace('_', ' ')} x{g.Count}"));
        string since = v.Before.At is { } at
            ? $" since {(string.IsNullOrWhiteSpace(v.Before.By) ? "the last look" : v.Before.By + " opened it")} {Ago(now - at)}"
            : " since memory's last look";
        var who = Suspects(v, world, db, isBot);
        return $"{v.Kind} at {v.Pos.X},{v.Pos.Y},{v.Pos.Z}: {gone} gone{since}" +
               (who is { } w ? $"; {w}" : "; someone took them (not seen)");
    }

    private static string? Suspects(ContainerVanished v, WorldState world, AgentDb? db, Func<string, bool> isBot)
    {
        var ids = v.Gone.Select(g => g.ItemId).ToHashSet();
        var center = v.Pos.Center;
        var near = world.Entities.All()
            .Where(e => e.Kind == EntityKind.Player && e.Name is { Length: > 0 } n && !isBot(n) && e.Position.DistanceTo(center) <= 10)
            .ToList();
        // 1. Holding what went missing, close by: the strongest sign we can get (no packets for other players' clicks).
        if (near.FirstOrDefault(e => ids.Contains(e.HeldItem)) is { } holder)
            return $"{holder.Name} may have taken it (holding {MineClaw.Data.Items.DisplayName(holder.HeldItem, 0)})";
        // 2. The activity log (any hive bot's tracker, shared db) saw them open or take from it since memory's look.
        if (db is not null && v.Before.At is { } since && Openers(db, v.Pos, since) is { Count: > 0 } openers)
            return $"{string.Join(" or ", openers.Take(2))} may have taken it (opened it since)";
        // 3. Standing next to it now.
        var close = near.Where(e => e.Position.DistanceTo(center) <= 6).Select(e => e.Name!).ToList();
        return close.Count is 1 or 2 ? $"{string.Join(" or ", close)} was nearby" : null;
    }

    /// <summary>Players the activity log saw open/take from the chest at <paramref name="pos"/> (or its double half) since then.</summary>
    public static List<string> Openers(AgentDb db, BlockPos pos, DateTimeOffset since)
    {
        try
        {
            return db.Query("""
                SELECT player FROM activity_log WHERE at >= $s AND kind IN ('opened', 'took') AND x IS NOT NULL
                  AND abs(x - $x) <= 1 AND abs(y - $y) <= 1 AND abs(z - $z) <= 1
                GROUP BY player ORDER BY max(at) DESC LIMIT 3
                """, r => r.GetString(0), ("$s", AgentDb.ToUnixMs(since)), ("$x", pos.X), ("$y", pos.Y), ("$z", pos.Z));
        }
        catch (Exception) { return []; } // no activity table (activity tracking off)
    }

    private static string Ago(TimeSpan t) => t < TimeSpan.FromSeconds(60) ? $"{Math.Max(0, (int)t.TotalSeconds)}s ago" : TextTokens.Ago(t);
}
