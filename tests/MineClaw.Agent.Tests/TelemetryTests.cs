using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;
using Xunit.Abstractions;

namespace MineClaw.Agent.Tests;

public class TelemetryTests(ITestOutputHelper output)
{
    private static (AgentRuntime Rt, FakeLlm Llm, PromptTelemetry Tel) Runtime(string? dir = null, TimeProvider? time = null)
    {
        var options = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0 };
        options.Chat.Username = "Clawd_bot";
        options.Chat.DisplayName = "Clawd";
        options.Chat.Aliases = [];
        options.Chat.LineDelayMs = 0;
        options.Events.CoalesceWindowMs = 10;
        options.Events.MaxBatchDelayMs = 50;
        options.ChatDebounce.QuietMs = 10;
        options.ChatDebounce.MaxMs = 50;
        options.ChatDebounce.ReplyQuietMs = 0;
        // Per-call accounting: the two-phase reply calls (quick ack, cheap-only triage) have their own tests (QuickReplyTests).
        options.Brainstem.Reply.QuickAck = false;
        options.Brainstem.Reply.CheapOnly = false;
        var fake = new FakeLlm();
        var tel = new PromptTelemetry(options, new TelemetryOptions(), dir, time);
        var rt = AgentRuntime.Create(options, (_, _) => Task.CompletedTask, tel.Wrap(fake), persist: false, time: time);
        tel.Attach(rt);
        return (rt, fake, tel);
    }

    [Fact]
    public void Calibrate_SumsExactlyToActual_AndKeepsProportions()
    {
        var est = new Dictionary<string, int> { ["tools"] = 1000, ["system"] = 3000, ["history"] = 5999, ["events"] = 1 };
        var cal = PromptSections.Calibrate(est, 12_345);
        Assert.Equal(12_345, cal.Values.Sum());
        Assert.Equal(est.Keys.OrderBy(k => k), cal.Keys.OrderBy(k => k));
        Assert.InRange(cal["history"] / (double)cal["system"], 1.99, 2.01);
        Assert.Equal(1, cal["events"]); // 1/10000 of 12345 = 1.23 → 1

        var zero = PromptSections.Calibrate(PromptSection.All.ToDictionary(k => k, _ => 0), 500);
        Assert.Equal(500, zero[PromptSection.Events]);
        Assert.Equal(0, PromptSections.Calibrate(est, 0).Values.Sum());
    }

    [Fact]
    public void Estimate_SplitsKnowledgeOutOfTheStaticBlock_AndIdentity()
    {
        var req = new LlmRequest
        {
            System = [new LlmSystemBlock(new string('a', 700) + "\n# World core rules\n" + new string('k', 350)), new LlmSystemBlock(new string('i', 70))],
            Messages = [LlmMessage.User("task: mine 3 cobble")],
        };
        var (s, round) = PromptSections.Estimate(req);
        Assert.Equal(200, s[PromptSection.System]);
        Assert.InRange(s[PromptSection.Knowledge], 106, 108);
        Assert.Equal(20, s[PromptSection.Identity]);
        Assert.True(s[PromptSection.Events] > 0);
        Assert.Equal(0, round);
    }

    [Fact]
    public async Task BrainTurn_RecordsCalibratedSections_JsonlRollupAndThoughts()
    {
        using var tmp = new TempDir();
        var (rt, llm, tel) = Runtime(tmp.Path);
        var records = new List<PromptUsageRecord>();
        tel.Recorded += records.Add;
        rt.Agent.StateHeader = () => "pos 1,64,2 hp 20/20 food 20 held air";
        rt.Agent.TurnDigest = _ => "kiwi placed 4 torches by the bridge";
        rt.Tools.Register("count_blocks", "Count blocks.", ToolSchema.Create().Str("block").Build(),
            (input, _) => Task.FromResult(ToolResult.Success($"12 {input.Str("block")}")));
        llm.UsagePerCall = new LlmUsage(300, 40, 9000, 700);
        llm.ThenTool("count_blocks", new { block = "cobblestone" }, text: "lemme check", id: "tu_a").ThenText("you've got 12 cobble!");

        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", "clawd can you mine some cobble?")]), default);

        Assert.Equal(2, records.Count);
        foreach (var r in records)
        {
            Assert.Equal("Clawd_bot", r.Bot);
            Assert.Equal(PromptPurpose.ChatTurn, r.Purpose);
            Assert.Equal(10_000, r.PromptTokens);
            Assert.Equal(10_000, r.Sections.Values.Sum());
            Assert.Equal(PromptSection.All.OrderBy(k => k), r.Sections.Keys.OrderBy(k => k));
            Assert.Equal(0.9, r.CacheHit, 3);
            Assert.True(r.CostUsd > 0);
            Assert.Equal(100_000, r.ContextLimit);
            Assert.True(r.Sections[PromptSection.Tools] > 0 && r.Sections[PromptSection.System] > 0 && r.Sections[PromptSection.Identity] > 0);
            Assert.True(r.Sections[PromptSection.Events] > 0 && r.Sections[PromptSection.Digest] > 0);
        }
        Assert.Equal(0, records[0].Round);
        Assert.Equal(0, records[0].Sections[PromptSection.TurnTools]);
        Assert.Equal(["count_blocks"], records[0].ToolCalls);
        Assert.Equal(StopReasons.ToolUse, records[0].StopReason);
        Assert.Equal(1, records[1].Round);
        Assert.True(records[1].Sections[PromptSection.TurnTools] > 0);
        output.WriteLine("sample breakdown (turn 1, round 1): " + string.Join(" ", records[1].Sections.Select(kv => $"{kv.Key}={kv.Value}")) +
                         $" | est {records[1].EstimatedTokens} → actual {records[1].PromptTokens}");

        // JSONL: one line per call, readable back (also while the writer holds the file open).
        var file = Directory.GetFiles(tmp.Path, "usage-*.jsonl").Single();
        string[] lines;
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs))
            lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using (var doc = JsonDocument.Parse(lines[1]))
        {
            Assert.Equal("chat_turn", doc.RootElement.GetProperty("purpose").GetString());
            Assert.True(doc.RootElement.GetProperty("sections").GetProperty("turn_tools").GetInt64() > 0);
            Assert.Equal(9000, doc.RootElement.GetProperty("cache_read_tokens").GetInt64());
        }
        Assert.Equal(records[1].Sections, PromptUsageRecord.FromJsonLine(lines[1])!.Sections);

        // Daily rollup in the usage store.
        var today = rt.Usage.SnapshotTotals().Today;
        Assert.Equal(2, today.Purposes![PromptPurpose.ChatTurn].Calls);
        Assert.Equal(20_000, today.Sections!.Values.Sum());

        // Thought context: turn input, tool call with result, final output.
        var t = tel.Thoughts.Snapshot();
        Assert.Contains("mine some cobble", t.Events);
        Assert.Contains("torches", t.Digest);
        Assert.Equal("you've got 12 cobble!", t.Output);
        var call = Assert.Single(t.Tools);
        Assert.Equal("count_blocks", call.Name);
        Assert.Equal("12 cobblestone", call.Result);
        Assert.Equal(2, tel.CallsPerHour(1)[0]);

        // Second turn: the first episode is now history; the new turn's sections reset.
        llm.ThenText("np!");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", "clawd thanks")]), default);
        var r2 = records[2];
        Assert.True(r2.Sections[PromptSection.History] > 0);
        Assert.Equal(0, r2.Sections[PromptSection.TurnTools]);
        Assert.Equal(0, r2.Round);
        Assert.Equal("np!", tel.Thoughts.Snapshot().Output);
        Assert.Empty(tel.Thoughts.Snapshot().Tools);
        output.WriteLine("sample breakdown (turn 2, round 0): " + string.Join(" ", r2.Sections.Select(kv => $"{kv.Key}={kv.Value}")) +
                         $" | est {r2.EstimatedTokens} → actual {r2.PromptTokens}");

        // Seeding a fresh hub from the JSONL restores the history.
        var again = new PromptTelemetry(rt.Options, new TelemetryOptions(), tmp.Path);
        Assert.Equal(3, again.SeedFromLogs());
        Assert.Equal(r2.Ts, again.Last!.Ts);
        again.Dispose();
        tel.Dispose();
        rt.Dispose();
    }

    [Fact]
    public async Task OtherPurposes_AreNormalised_AndErrorsRecordedThenRethrown()
    {
        var (rt, llm, tel) = Runtime();
        var records = new List<PromptUsageRecord>();
        tel.Recorded += records.Add;
        var client = tel.Wrap(llm);
        llm.ThenText("journal");
        await client.CompleteAsync(new LlmRequest { Tier = ModelTier.Cheap, Purpose = "compact", Messages = [LlmMessage.User("fold this")] }, default);
        Assert.Equal(PromptPurpose.Compaction, records[0].Purpose);
        Assert.Equal("memory", records[0].Tier);
        Assert.Equal(records[0].PromptTokens, records[0].Sections[PromptSection.Events]);

        llm.Then(_ => throw new HttpRequestException("boom"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CompleteAsync(new LlmRequest { Tier = ModelTier.Worker, Purpose = "worker", Messages = [LlmMessage.User("task: x")] }, default));
        Assert.Equal(PromptPurpose.DelegateRound, records[1].Purpose);
        Assert.Contains("boom", records[1].Error);
        Assert.Equal(0, records[1].PromptTokens);
        rt.Dispose();
    }

    [Fact]
    public void ThoughtTracker_CapsEverything()
    {
        var tr = new ThoughtTracker(new ThoughtCaps { Events = 50, Output = 40, MaxTools = 2, ToolResult = 20 });
        tr.OnTurn(new TurnInput(DateTimeOffset.UtcNow, ModelTier.Main, "h", "", "", new string('e', 500), "x"));
        var uses = Enumerable.Range(0, 5).Select(i => (LlmBlock)new LlmToolUse("t" + i, "look", JsonSerializer.SerializeToElement(new { detail = true }))).ToList();
        tr.OnBrainResponse(new LlmResponse([new LlmText(new string('o', 500)), .. uses], StopReasons.ToolUse, LlmUsage.Zero, "m"));
        tr.OnBrainRequest(new LlmRequest { Messages = [LlmMessage.User(new LlmToolResult("t4", new string('r', 300)))] });
        var s = tr.Snapshot();
        Assert.Equal(50, s.Events.Length);
        Assert.Equal(40, s.Output.Length);
        Assert.Equal(2, s.Tools.Count);
        Assert.Equal(20, s.Tools[^1].Result!.Length);
        Assert.Null(s.Tools[0].Result);
    }

    [Fact]
    public void JsonlWriter_RollsDaily_AndDropsOldFiles()
    {
        using var tmp = new TempDir();
        var time = new ManualTime();
        File.WriteAllText(Path.Combine(tmp.Path, "usage-2026-01-01.jsonl"), "{}\n");
        using var w = new UsageJsonlWriter(tmp.Path, keepDays: 30, time);
        w.Write(new PromptUsageRecord { Bot = "a", Purpose = "chat_turn" });
        time.Advance(TimeSpan.FromDays(1));
        w.Write(new PromptUsageRecord { Bot = "a", Purpose = "ask_memory" });
        var names = Directory.GetFiles(tmp.Path).Select(f => Path.GetFileName(f)).OrderBy(n => n).ToArray();
        Assert.Equal(["usage-2026-10-26.jsonl", "usage-2026-10-27.jsonl"], names);
        Assert.Equal("ask_memory", Assert.Single(w.Read(time.GetLocalNow())).Purpose);
    }

    [Fact]
    public void BotSnapshot_RoundTripsAsJson()
    {
        var snap = new BotSnapshot
        {
            Name = "maid_chan_420", Status = BotStatus.Online, Pos = new BotPos(12.5, 64, -3.2), Hp = 18, Food = 17, Task = "mine #3 4s",
            Thought = new ThoughtSnapshot { Output = "hi", Tools = [new ThoughtToolCall(DateTimeOffset.UnixEpoch, "look", "", "ok", true)] },
            Usage = new UsageSnapshot { TodayUsd = 1.5m, Today = [new TierUsage("opus", 1.5m, 3, 0.8, 1000, 50)], CallsPerHour = [0, 2] },
            Viewport = new ViewportGrid { Radius = 1, Top = ["...", ".#.", "..."], Markers = [new ViewMarker("player", "kiwi", 1, 0, -1)] },
            Chat = ["12:00:00 <kiwi> hi"],
        };
        var json = snap.ToJson();
        Assert.Contains("\"status\":\"Online\"", json);
        var back = BotSnapshot.FromJson(json)!;
        Assert.Equal(snap.Pos, back.Pos);
        Assert.Equal("look", back.Thought!.Tools[0].Name);
        Assert.Equal(".#.", back.Viewport!.Top[1]);
        Assert.Equal("kiwi", back.Viewport.Markers[0].Name);
        Assert.Equal(1.5m, back.Usage!.Today[0].Usd);
    }
}
