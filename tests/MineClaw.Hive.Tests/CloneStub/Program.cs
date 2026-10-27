// Fake hive clone process for MineClaw.Hive.Tests: the real CloneHost + IPC + shared db, with a fake game body and LLM.
//   --clone <name> --parent-pipe <pipe> --db <file> --mode normal|crash|dbwrite|spend [--stacks n] [--chest n] [--sub-cap usd]
// Prints marker lines on stdout (READY, LOGOUT ..., EXIT n) for the tests.

using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Telemetry;
using MineClaw.Hive;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Db;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Testing;
using MineClaw.World;
using MineClaw.World.Claims;

string Arg(string name, string def = "")
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

var name = Arg("--clone");
var pipe = Arg("--parent-pipe");
var dbPath = Arg("--db");
var mode = Arg("--mode", "normal");
int stacks = int.Parse(Arg("--stacks", "0"));
int chest = int.Parse(Arg("--chest", "0"));
var cfg = new HiveConfig
{
    CloneUsernames = ["maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"],
    HeartbeatSeconds = 0.5, HeartbeatTimeoutSeconds = 6, SnapshotSeconds = 0.3,
    DepositTimeoutSeconds = 15, ParentLossDepositSeconds = 5,
};
cfg.Budget.CloneDailyCapUsd = decimal.Parse(Arg("--sub-cap", "1.5"), System.Globalization.CultureInfo.InvariantCulture);
cfg.Budget.GrantUsd = 0.10m;
cfg.Budget.EstimateCheapUsd = 0.10m;

void Out(string s) { Console.Out.WriteLine(s); Console.Out.Flush(); }

AgentDb? db = string.IsNullOrEmpty(dbPath) ? null : AgentDb.Open(dbPath);
HiveStore? store = db != null ? new HiveStore(db, name) : null;
var host = new CloneHost(name, pipe, cfg, store: store);

switch (mode)
{
    case "crash":
        {
            // Connects, says hello, then dies like a crashed process.
            var w = await host.ConnectParentAsync(TimeSpan.FromSeconds(10), default);
            Out(w == null ? "NOPARENT" : "READY");
            Environment.Exit(w == null ? CloneExitCodes.NoParent : 3);
            return 3;
        }
    case "crashonce":
        {
            // First run: takes a claim, then crashes. Restarted run (marker file exists): reports what the welcome resumed.
            var marker = Arg("--marker");
            var w = await host.ConnectParentAsync(TimeSpan.FromSeconds(10), default);
            if (w == null) { Out("NOPARENT"); return CloneExitCodes.NoParent; }
            if (!File.Exists(marker))
            {
                var c = await host.Claims.TryClaimAsync(ClaimKey.Block(9, 64, 9));
                File.WriteAllText(marker, "crashed");
                Out("READY claim=" + c.Ok);
                await Task.Delay(300);
                Environment.Exit(3);
                return 3;
            }
            var t = w.Task;
            // ReclaimFor ran before the welcome was built: the mirror already shows the lease as this clone's again.
            bool regranted = w.Claims.Leases.Any(l => l.Owner == name && l.Key == ClaimKey.Block(9, 64, 9).Id);
            var body = new FakeBody(name, new FakePort());
            host.Body = body;
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await host.ReportAsync("progress", $"WELCOME restarted={t?.Restarted} task={t?.Text} loadout={string.Join(",", t?.Loadout ?? [])} " +
                    $"claims={string.Join(",", t?.Claims ?? [])} regranted={regranted}");
            });
            Out("READY");
            int code = await host.RunAsync(default, w);
            Out("EXIT " + code);
            return code;
        }
    case "dbwrite":
        {
            // Holds a claim, saves an inventory snapshot, then hammers the shared db until it is killed.
            var w = await host.ConnectParentAsync(TimeSpan.FromSeconds(10), default);
            if (w == null) { Out("NOPARENT"); return CloneExitCodes.NoParent; }
            var claim = await host.Claims.TryClaimAsync(ClaimKey.Block(5, 64, 5));
            var items = new List<ItemMsg> { new(4, 0, 32, "cobblestone"), new(17, 0, 5, "log") };
            store!.SaveInventory(name, items);
            await host.Link!.SendAsync(Msg.Inventory, new InventoryMsg(items));
            _ = Task.Run(async () => { while (true) { await host.Link!.SendAsync(Msg.Heartbeat); await Task.Delay(300); } });
            Out("READY claim=" + claim.Ok);
            long n = 0;
            var big = new string('x', 2000);
            while (true)
            {
                db!.Tx(() =>
                {
                    for (int i = 0; i < 50; i++)
                        db.Exec("INSERT INTO chat_log(at, kind, player, text) VALUES ($a, 'test', $p, $t)", ("$a", n), ("$p", name), ("$t", big + n++));
                });
                if (n % 500 == 0) Out("WROTE " + n);
            }
        }
    case "chat":
        {
            // Real clone chat intake: ChatRouter + EventQueue + command gate wired by ClonePolicy (as CloneMode does), fed by
            // the lines main forwards; every brain wake-up and its speech verdict is reported to main over IPC as progress.
            var body = new FakeBody(name, new FakePort()) { TaskText = "idle" };
            host.Body = body;
            var chatOptions = new MineClaw.Agent.Chat.ChatOptions
            {
                Username = name, DisplayName = "claudechan " + ChatArbiterNumber(name), Aliases = ["claudechan " + ChatArbiterNumber(name)], LineDelayMs = 0,
            };
            var events = new MineClaw.Agent.Events.EventQueue(new MineClaw.Agent.Events.EventQueueOptions { CoalesceWindowMs = 300, MaxBatchDelayMs = 600 });
            var chat = new MineClaw.Agent.Chat.ChatRouter(chatOptions, events, (_, _) => Task.CompletedTask);
            var gate = new MineClaw.Agent.Chat.CommandGate(new MineClaw.Agent.Chat.CommandOptions(), () => ["kiwi", "poofimafox"]);
            chat.Commands = gate;
            ClonePolicy.Apply(chat, events, host.Arbiter);
            chat.Accepted += m => { if (m.Player != null) host.ChatAccepted(m.Player); };
            body.OnInject = raw =>
            {
                chat.OnServerChat(raw);
                if (raw.Contains("/time set day")) _ = host.ReportAsync("progress", "CMD " + (gate.Authorize("/time set day").Ok ? "ran" : "refused"));
            };
            body.OnTask = t =>
            {
                events.Post(MineClaw.Agent.Events.GameEvent.Notice($"{ClonePolicy.TaskTag} {t.Text} — take what you need from storage first", MineClaw.Agent.Events.EventPriority.Normal));
                return Task.CompletedTask;
            };
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var batch = await events.WaitBatchAsync(default);
                    var speech = ClonePolicy.Speech(batch, "on it robin uwu", alreadySpoke: false) != null ? "SAY" : "MUTE";
                    var woke = string.Join(" / ", batch.Waking.Select(e => e.Format()));
                    await host.ReportAsync("progress", $"TURN {speech}: {(woke.Length > 120 ? woke[..120] : woke)}");
                }
            });
            Out("READY");
            int code = await host.RunAsync(default);
            Out("EXIT " + code);
            return code;
        }
    default:
        {
            var port = new FakePort(stacks, chest);
            var body = new FakeBody(name, port);
            host.Body = body;
            host.RecordExperience = (k, t) => store?.RecordOnce("death", k, name, t) ?? false;
            ILlmClient llm = new GatedLlmClient(new FakeLlm(), new PriceTable(), cfg.Budget, () => host.Link);
            ((GatedLlmClient)llm).Denied += why => { Out("DENIED " + why); _ = host.LogoutAsync("budget", planned: true); };
            body.OnTask = async t =>
            {
                body.RaiseUsage(new PromptUsageRecord { Ts = DateTimeOffset.UtcNow, Bot = name, Tier = "memory", Model = "claude-haiku-5-5", Purpose = "chat_turn", CostUsd = 0.001m });
                if (t.Text.Contains("claim:"))
                {
                    var r = await host.Claims.TryClaimAsync(ClaimKey.Block(9, 64, 9));
                    await host.ReportAsync("progress", r.Ok ? "claim granted" : "claim refused by " + r.Holder, 0.3);
                }
                if (t.Text.Contains("spend"))
                {
                    try { while (true) { await llm.CompleteAsync(new LlmRequest { Tier = ModelTier.Cheap }, default); Out("CALL"); } }
                    catch (HiveBudgetDeniedException) { }
                }
                if (t.Text.Contains("die")) body.Die("fell into the void");
                if (t.Text.Contains("done")) await host.ReportAsync("done", "finished the fake task");
            };
            Out("READY");
            int code = await host.RunAsync(default);
            Out($"LOGOUT reason={host.LogoutReason} empty={host.LastDeposit?.Empty} left={port.Items().Count} disconnect={body.ItemsAtDisconnect}");
            Out("EXIT " + code);
            return code;
        }
}

static string ChatArbiterNumber(string clone) => MineClaw.Hive.Chat.ChatArbiter.NumberOf(clone) ?? clone;
