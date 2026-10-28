using MineClaw.Agent.Rag;
using MineClaw.Agent.Rules;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Chat;

/// <summary>
/// Foreign bots' command prefixes in the db (table <c>command_prefixes</c>: player → bot → prefix, answered-line hits, confirmed,
/// updated), so "c trees" stays robinchan's across restarts (live 10-28 15:45: a restart wiped the learned "c " and main took
/// it). Seeded and confirmed from trusted players' standing rules ("when I say c, I'm talking to robinchan"), including ones
/// that were stored as claims. Zero tokens; every failure is logged and ignored (routing then just relearns).
/// </summary>
public sealed class CommandPrefixStore
{
    private readonly AgentDb _db;
    private readonly TimeProvider _time;
    private bool _table;

    public CommandPrefixStore(AgentDb db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public sealed record Row(string Player, string Bot, string Prefix, int Hits, bool Confirmed, DateTimeOffset Updated);

    private void EnsureTable()
    {
        if (_table) return;
        _db.Exec("""
            CREATE TABLE IF NOT EXISTS command_prefixes(
                player TEXT NOT NULL, bot TEXT NOT NULL COLLATE NOCASE, prefix TEXT NOT NULL, hits INTEGER NOT NULL DEFAULT 0,
                confirmed INTEGER NOT NULL DEFAULT 0, updated INTEGER NOT NULL, PRIMARY KEY(player, bot, prefix));
            """);
        _table = true;
    }

    /// <summary>Records a count or confirmation (counts and confirmations only grow).</summary>
    public void Upsert(string? player, string bot, string prefix, int hits, bool confirmed)
    {
        EnsureTable();
        _db.Exec("""
            INSERT INTO command_prefixes(player, bot, prefix, hits, confirmed, updated) VALUES ($p, $b, $x, $h, $c, $t)
            ON CONFLICT(player, bot, prefix) DO UPDATE SET hits = max(hits, excluded.hits), confirmed = max(confirmed, excluded.confirmed),
                updated = excluded.updated
            """, ("$p", (player ?? "").Trim()), ("$b", bot.Trim()), ("$x", prefix), ("$h", Math.Max(0, hits)), ("$c", confirmed ? 1 : 0),
            ("$t", AgentDb.ToUnixMs(_time.GetUtcNow())));
    }

    public IReadOnlyList<Row> All()
    {
        EnsureTable();
        return _db.Query("SELECT player, bot, prefix, hits, confirmed, updated FROM command_prefixes ORDER BY updated",
            r => new Row(r.GetString(0), r.GetString(1), r.GetString(2), (int)r.GetInt64(3), r.GetInt64(4) != 0, AgentDb.FromUnixMs(r.GetInt64(5))));
    }

    /// <summary>
    /// Wires a router to the db: restores every known prefix, persists each new count/confirmation, and registers prefixes named
    /// by trusted players' standing rules (now and whenever one is stored). Returns the store (null when the db failed).
    /// </summary>
    public static CommandPrefixStore? Attach(ChatRouter chat, AgentDb db, DirectiveStore? rules = null, TimeProvider? time = null, ILogger? log = null)
    {
        CommandPrefixStore store;
        try
        {
            store = new CommandPrefixStore(db, time);
            foreach (var g in store.All().GroupBy(r => (Bot: r.Bot.ToLowerInvariant(), r.Prefix)))
                chat.RestorePrefix(g.First().Bot, g.Key.Prefix, g.Max(r => r.Hits), g.Any(r => r.Confirmed));
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "command prefixes: restore failed");
            return null;
        }
        chat.PrefixLearned += (player, bot, prefix, hits, confirmed) =>
        {
            try
            {
                store.Upsert(player, bot, prefix, hits, confirmed);
                if (confirmed || hits == 2)
                    log?.LogInformation("addressing: \"{Prefix}\" is {Bot}'s command prefix ({How}); its lines never wake us", prefix.Trim(), bot,
                        confirmed ? $"declared{(player != null ? " by " + player : "")}" : "answered twice");
            }
            catch (Exception ex) { log?.LogWarning(ex, "command prefixes: saving {Bot} \"{Prefix}\" failed", bot, prefix); }
        };
        if (rules != null)
        {
            try
            {
                foreach (var d in rules.Active()) chat.LearnPrefixRule(d.Player, d.Text);
                // A directive the brain stored as an unverified claim (live 10-28 14:51 → claim#10) still counts from a trusted player.
                if (db.TableExists("claims"))
                    foreach (var (who, text) in db.Query("SELECT claimant, text FROM claims ORDER BY id DESC LIMIT 500", r => (r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1))))
                        if (rules.Trusted(who)) chat.LearnPrefixRule(who, text);
            }
            catch (Exception ex) { log?.LogWarning(ex, "command prefixes: seeding from rules failed"); }
            rules.Added += d =>
            {
                try { chat.LearnPrefixRule(d.Player, d.Text); } catch (Exception) { }
            };
        }
        return store;
    }
}
