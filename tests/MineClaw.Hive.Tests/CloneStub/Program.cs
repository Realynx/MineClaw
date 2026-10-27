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
