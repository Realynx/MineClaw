using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Hive;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Hierarchy.Bus;
using MineClaw.Hive.Ipc;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// A clone process's side of the hive hierarchy: shared-memory writes through main (one groomer, provenance), instant
/// invalidation on main's <c>mem_changed</c>, the bus inbox, a hive-kind clone's own <c>hive</c> tool, and its chat budget.
/// </summary>
internal static class CloneHierarchyWiring
{
    public static MemoryBusClient? Attach(CloneHost host, WelcomeMsg welcome, AgentRuntime rt, HiveConfig hive, string clone, ILogger log)
    {
        // Low verbosity: unasked lines are budgeted per hive; muted text goes to main as progress (BotAgent.Muted).
        rt.Agent.SpeechGate = HiveSpeech.Wrap(rt.Agent.SpeechGate, new ChatVerbosityBudget(hive.Accounts.ChatLinesPerMinute));
        if (host.BusToken == null) return null; // an older main without a bus: local writes to the shared db, as before
        MineClaw.Hive.Story.StoryStorageTool.RegisterRemote(rt.Tools, host);

        var brain = welcome.Task?.Kind == "script" ? null : string.IsNullOrWhiteSpace(welcome.Task?.Profile) ? "opus" : welcome.Task!.Profile;
        var mem = new MemoryBusClient((env, timeout) => host.SendBusAsync(env, timeout), clone, host.BusToken, rt.Db, new MemProvenance(clone, clone, brain), log)
        {
            Invalidate = () => { rt.Memory.Reload(); rt.Rag.ReloadVectors(); },
        };
        rt.Rag.WriteThrough = mem.Write; // one memory: main grooms and stores; reads stay direct (WAL)
        host.BusInbox = (env, _) =>
        {
            switch (env.Type)
            {
                case BusTypes.MemChanged:
                    mem.OnChanged();
                    break;
                case BusTypes.Observation when env.As<ObservationMsg>() is { } ob:
                    rt.Events.Post(GameEvent.Notice($"[hive bus] {HiveNode.ShortOf(env.From)} saw: {ob.Text}", EventPriority.Background));
                    break;
                case BusTypes.NeedHelp when env.As<NeedHelpMsg>() is { } nh:
                    rt.Events.Post(GameEvent.Notice($"[hive bus] {HiveNode.ShortOf(env.From)} needs help: {nh.Text}", EventPriority.Low));
                    break;
            }
            return Task.FromResult(new BusReply(true));
        };
        // A hive-kind clone owns clones of its own (main allocates and whitelists them over the bus).
        if (welcome.Task is { Kind: "hive", Script: null } && !rt.Tools.Contains(HiveTools.MainTool)) CloneHiveTool.Register(rt.Tools, host);
        log.LogInformation("clone {Clone}: on the hive bus (memory writes via main{Tool})", clone,
            rt.Tools.Contains(HiveTools.MainTool) ? "; own hive tool" : "");
        return mem;
    }
}
