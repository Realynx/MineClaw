using System.Text;
using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Telemetry;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;
using MineClaw.Hive.Testing;
using MineClaw.Hive.Claims;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Pins <see cref="HiveSupervisor.List"/> (WP07): the <c>list_clones</c> line over every slot state (out, starting, logging out,
/// restarting, retired, idle) with no task, a plain task (profile, pending, result, progress, position), a script task (brain asleep
/// or awake, default profile, not out, result), and deaths.
/// </summary>
public class HiveListGoldenTests
{
    private static readonly string[] Names = [.. Enumerable.Range(60, 18).Select(i => "maid_chan_" + i)];

    private static HiveSupervisor Supervisor()
    {
        var cfg = new HiveConfig { CloneUsernames = [.. Names], MaxClones = 0 };
        return new HiveSupervisor(cfg, "maid_chan", Stub.Launcher(), new ClaimBoard(TimeSpan.FromSeconds(60)),
            new BudgetLedger(new BudgetOptions(), cfg.Budget, () => (0m, 0m)), new ChatArbiter("maid_chan", "maid_chan", cfg.Clones()),
            null, () => new SlotFacts(30, ["maid_chan"]), []);
    }

    [Fact]
    public void List_matches_the_golden_line()
    {
        var sup = Supervisor();
        var slots = sup.Slots.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        var states = new[] { CloneState.Running, CloneState.Starting, CloneState.Stopping, CloneState.Backoff, CloneState.Retired, CloneState.Idle };
        var plain = new TaskMsg("t1", "mine 64 cobblestone at the generator by the big oak tree please", Profile: "haiku");
        var script = new TaskMsg("t2", "run", Script: "cobble_loop");
        var scriptProfile = new TaskMsg("t3", "run", Script: "chop", Profile: "deepseek");
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            s.State = states[i % states.Length];
            s.Connected = i % 4 != 1;
            switch (i / states.Length)
            {
                case 0:
                    s.Task = i % 2 == 0 ? plain : null;
                    s.ResultIn = i == 2;
                    s.ResultText = i == 2 ? new string('r', 80) : null;
                    s.Progress = i == 0 ? 0.42 : 0;
                    s.Snapshot = i == 0 ? new BotSnapshot { Pos = new BotPos(1.5, 64, -3.2) } : null;
                    break;
                case 1:
                    s.Task = i % 2 == 0 ? script : scriptProfile;
                    s.Progress = 0.5;
                    s.Snapshot = new BotSnapshot { Brain = i % 3 == 0 ? "awake" : i % 3 == 1 ? "asleep (script x)" : null };
                    s.ResultIn = i == 7;
                    s.ResultText = i == 7 ? "done" : null;
                    break;
                default:
                    s.Task = new TaskMsg("t4", "short");
                    s.ResultIn = i == 13;
                    s.ResultText = null;
                    s.Deaths = i - 12;
                    if (i == 14) s.Crashes.AddRange([DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch]);
                    break;
            }
        }
        var sb = new StringBuilder(sup.List().Replace(" | ", "\n"));
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "hive-list.txt"), sb.ToString());
        Assert.Equal(HiveListGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), sb.ToString().ReplaceLineEndings("\n").TrimEnd('\n'));
    }
}
