using MineClaw.Agent;
using MineClaw.Bot;
using MineClaw.Client;
using MineClaw.Hive;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Hierarchy.Bus;
using MineClaw.Hive.Supervisor;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// Main-process wiring of the hive hierarchy (docs/design/hive-hierarchy.md): the managed account list + whitelist manager behind
/// the command gate's scoped <c>/whitelist</c> exception, the allocator as the supervisor's account seam, the ownership tree, the
/// off-channel bus (main routes; the shared-memory write host on it) and main's chat verbosity budget. The startup reconcile runs
/// once main is in game (it needs main's connection to send the removes).
/// </summary>
internal static class HiveHierarchyWiring
{
    public static HiveHierarchy Attach(HiveConfig cfg, string main, HiveSupervisor sup, BotHost bot, MinecraftClient client, AgentOptions agent,
        IReadOnlyList<string> regulars, ILogger log)
    {
        var rt = bot.Runtime;
        var acfg = cfg.Accounts;
        var managed = new ManagedAccounts(Path.Combine(agent.DataDirectory, acfg.File));
        var whitelist = new WhitelistManager(managed, acfg, main, rt.Commands.ArmManagedWhitelist, (line, ct) => rt.Chat.SendCommandAsync(line, ct), log);
        rt.Commands.ManagedAccount = whitelist.Accepts; // the gate's exception: managed names only, never main, C# only
        // The server's replies ("Added X to the whitelist" / "Could not add X"): an add counts only once confirmed.
        client.ChatReceived += line => { try { whitelist.NoteServerLine(line); } catch (Exception) { /* chat never breaks */ } };
        if (acfg.AdoptLegacy && whitelist.Adopt(cfg.LegacyCloneUsernames) is > 0 and var adopted)
            log.LogInformation("hive: adopted {N} old fixed clone name(s) into the managed list; Hive:CloneUsernames is ignored", adopted);

        var h = new HiveHierarchy(acfg, main, managed, whitelist, cfg.MaxClones, () => regulars, () => KnownPlayers(rt, client, managed, main), log,
            cfg.MaxPlayersFallback)
        {
            Bodies = new SupervisorBodies(sup),
            ProfileCheck = agent.Llm.CheckProfile,
            Notice = e => rt.Events.Post(e),
        };
        h.Allocated += name => agent.Hive.Register(name); // its chat is a hive bot's, its self profile is main's (one memory)
        // A body allocated for a job starts clean: a standing order a reused account kept from its last crew isn't this job's
        // (live 10-30: Fox's hunting crew reused the deforest crew's accounts and "some kept falling back to the old
        // tree-chopping job", bringing back 0 food).
        h.Allocated += name =>
        {
            try { if (rt.Memory.GetStandingOrder(name) != null) rt.Memory.ClearStandingOrder(name, "allocated for a new job"); }
            catch (Exception ex) { log.LogDebug(ex, "hive: clearing {Name}'s old standing order failed", name); }
        };
        sup.Accounts = h;
        // Accounts main managed before (released ones included) are reusable slots: their saved spot and history stay known.
        foreach (var a in managed.All())
        {
            sup.AddSlot(a.Name);
            agent.Hive.Register(a.Name);
        }

        // The off-channel bus: main routes; every clone process gets an endpoint + token in its welcome. Shared memory writes
        // from other hives land in main's groomer here.
        var bus = new HiveBus(main, log) { MainHandler = h.HandleBusAsync };
        h.Bus = bus;
        sup.Bus = bus;
        h.Memory = new MemoryBusHost(rt.Rag, rt.Db, bus, log);

        // Low verbosity: main's unasked public lines are budgeted; plumbing sentences never reach chat.
        rt.Agent.SpeechGate = HiveSpeech.Wrap(rt.Agent.SpeechGate, new ChatVerbosityBudget(acfg.ChatLinesPerMinute));

        _ = Task.Run(async () =>
        {
            try
            {
                while (!client.IsPlaying) await Task.Delay(1000).ConfigureAwait(false);
                await Task.Delay(3000).ConfigureAwait(false); // past the login burst
                await h.StartAsync([], n => sup.Slot(n) is { State: not (CloneState.Idle or CloneState.Retired) }).ConfigureAwait(false);
            }
            catch (Exception ex) { log.LogWarning(ex, "hive: startup reconcile failed"); }
        });
        return h;
    }

    /// <summary>Names that belong to someone else: online non-hive players, players heard in chat, regulars and trusted players.</summary>
    private static IReadOnlyCollection<string> KnownPlayers(AgentRuntime rt, MinecraftClient client, ManagedAccounts managed, string main)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in client.World.Entities.OnlinePlayers) set.Add(p);
        foreach (var p in rt.Options.Self.TrustedPlayers.Concat(rt.Options.Self.RegularPlayers)) set.Add(p);
        try
        {
            lock (rt.Db.Gate)
                foreach (var p in rt.Db.Query("SELECT DISTINCT player FROM chat_log WHERE player IS NOT NULL LIMIT 500", r => r.GetString(0))) set.Add(p);
        }
        catch (Exception) { /* best effort */ }
        set.RemoveWhere(n => n.Equals(main, StringComparison.OrdinalIgnoreCase) || managed.IsManaged(n));
        return set;
    }
}
