using System.Text;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Self;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace MineClaw.Agent.Tests;

/// <summary>The caveman-style internal register ("Agent:Terse") and the telemetry section mapping.</summary>
public class TerseTests(ITestOutputHelper output)
{
    private const string Persona = "you are maid-chan, brainrotted, 67, skibidi rizz, when given orders you act uwu and like a maid";

    private static (AgentRuntime Rt, FakeLlm Llm, List<string> Sent) Create(Action<TerseOptions>? terse = null) =>
        TestRuntime.Create(o =>
        {
            o.Persona = Persona;
            terse?.Invoke(o.Terse);
        });

    private static TerseOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new AgentOptions();
        config.GetSection("Agent").Bind(options);
        return options.Terse;
    }

    // ---------------- options ----------------

    [Fact]
    public void Defaults_AfterAB_FullWorkerReflection_LiteThinkingDelegateMemory_CompactionRaptorOff()
    {
        var t = new AgentOptions().Terse;
        foreach (var p in new[] { TersePurpose.Worker, TersePurpose.Reflection }) Assert.Equal(TerseLevel.Full, t.For(p));
        foreach (var p in new[] { TersePurpose.Thinking, TersePurpose.Delegate, TersePurpose.AskMemory }) Assert.Equal(TerseLevel.Lite, t.For(p));
        foreach (var p in new[] { TersePurpose.Compaction, TersePurpose.Raptor }) Assert.Equal(TerseLevel.Off, t.For(p));
    }

    [Fact]
    public void Binding_ScalarForm_SetsEveryPurposeButRaptor()
    {
        var t = Bind(new() { ["Agent:Terse"] = "ultra" });
        Assert.Equal(TerseLevel.Ultra, t.For(TersePurpose.Thinking));
        Assert.Equal(TerseLevel.Ultra, t.For(TersePurpose.Worker));
        Assert.Equal(TerseLevel.Off, t.For(TersePurpose.Raptor));

        var off = Bind(new() { ["Agent:Terse"] = "off" });
        Assert.All(TersePurpose.All, p => Assert.Equal(TerseLevel.Off, off.For(p)));
    }

    [Fact]
    public void Binding_ObjectForm_PerPurposeOverrides()
    {
        var t = Bind(new()
        {
            ["Agent:Terse:Level"] = "lite",
            ["Agent:Terse:Purposes:ask_memory"] = "ultra",
            ["Agent:Terse:Purposes:Worker"] = "off",
            ["Agent:Terse:Purposes:raptor"] = "full", // capped: summaries keep fidelity
            ["Agent:Terse:Purposes:thinking"] = "bogus", // unknown → falls back to Level
        });
        Assert.Equal(TerseLevel.Ultra, t.For(TersePurpose.AskMemory));
        Assert.Equal(TerseLevel.Ultra, t.For("AskMemory"));
        Assert.Equal(TerseLevel.Off, t.For(TersePurpose.Worker));
        Assert.Equal(TerseLevel.Lite, t.For(TersePurpose.Raptor));
        Assert.Equal(TerseLevel.Lite, t.For(TersePurpose.Thinking));
        Assert.Equal(TerseLevel.Lite, t.For(TersePurpose.Compaction));

        // Overrides only (no Level): the rest keep their built-in defaults.
        var o = Bind(new() { ["Agent:Terse:Purposes:compaction"] = "lite" });
        Assert.Equal(TerseLevel.Lite, o.For(TersePurpose.Compaction));
        Assert.Equal(TerseLevel.Full, o.For(TersePurpose.Worker));
        Assert.Equal(TerseLevel.Lite, o.For(TersePurpose.Delegate));
    }

    [Fact]
    public void Binding_FromJson_ObjectForm()
    {
        using var tmp = new TempDir();
        var file = tmp.File("appsettings.json");
        File.WriteAllText(file, """{ "Agent": { "Terse": { "Purposes": { "delegate": "full", "raptor": "lite" } } } }""");
        var config = new ConfigurationBuilder().AddJsonFile(file).Build();
        var options = new AgentOptions();
        config.GetSection("Agent").Bind(options);
        Assert.Equal(TerseLevel.Full, options.Terse.For(TersePurpose.Delegate));
        Assert.Equal(TerseLevel.Lite, options.Terse.For(TersePurpose.Raptor));
        Assert.Equal(TerseLevel.Full, options.Terse.For(TersePurpose.Worker));
    }

    // ---------------- prompts: cached prefix, stable bytes ----------------

    private static Dictionary<string, string> Prefixes(AgentRuntime rt, TerseOptions t) => new()
    {
        ["brain"] = rt.Agent.SystemPrompt[0].Text,
        ["worker"] = rt.Worker!.SystemPrompt[0].Text,
        ["ask_memory"] = rt.MemoryAgent!.SystemPrompt[0].Text,
        ["compaction"] = ContextManager.CompactionInstructions + Prompts.CompactionStyle(t.For(TersePurpose.Compaction)),
        ["reflection"] = ReflectionPass.InstructionsV2 + Prompts.ReflectionStyle(t.For(TersePurpose.Reflection)),
        ["raptor"] = Prompts.RaptorStyle(t.For(TersePurpose.Raptor)),
    };

    [Fact]
    public void PromptBytes_AreStablePerTier_AndTerseTextIsInTheCachedPrefix()
    {
        var (a, _, _) = Create();
        var (b, _, _) = Create();
        var pa = Prefixes(a, a.Options.Terse);
        var pb = Prefixes(b, b.Options.Terse);
        foreach (var k in pa.Keys) Assert.Equal(pa[k], pb[k]);

        Assert.True(a.Agent.SystemPrompt[0].Cache);
        Assert.True(a.Worker!.SystemPrompt[0].Cache);
        Assert.True(a.MemoryAgent!.SystemPrompt[0].Cache);
        Assert.Contains("Internal style", pa["brain"]);
        Assert.Contains("- Thinking and delegate/ask_memory inputs: " + Prompts.TerseStyle(TerseLevel.Lite), pa["brain"]);
        Assert.Contains("No narration between tool calls", pa["worker"]);
        Assert.Contains(Prompts.TerseStyle(TerseLevel.Full), pa["worker"]);
        Assert.Contains(Prompts.TerseStyle(TerseLevel.Lite), pa["ask_memory"]);
        Assert.Equal(ContextManager.CompactionInstructions, pa["compaction"]); // default off after the A/B
        Assert.Contains(Prompts.TerseKeep, pa["reflection"]);
        Assert.Equal("", pa["raptor"]); // default off: the paper's prompt only
        // Identity block (breakpoint 2) carries no style text.
        Assert.DoesNotContain("terse", a.Agent.SystemPrompt[1].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TerseText_NeverRidesInTheUncachedTail()
    {
        var (rt, llm, _) = Create();
        llm.ThenText("on it uwu");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("kiwi", "clawd chop that tree pls")]), default);
        var req = Assert.Single(llm.Requests);
        var tail = string.Concat(req.Messages.SelectMany(m => m.Content).OfType<LlmText>().Select(t => t.Text));
        Assert.DoesNotContain("caveman", tail);
        Assert.DoesNotContain("Internal style", tail);
        Assert.DoesNotContain(Prompts.TerseKeep, tail);
        Assert.Contains("Internal style", req.System[0].Text);
    }

    [Fact]
    public void Off_LeavesEveryPromptByteIdenticalToTheLegacyText()
    {
        var (rt, _, _) = Create(t => t.Level = "off");
        var legacy = Prompts.System(Persona, "", "", delegation: true);
        Assert.Equal(legacy, rt.Agent.SystemPrompt[0].Text);
        Assert.Equal(Prompts.WorkerSystem(""), rt.Worker!.SystemPrompt[0].Text);
        Assert.Equal(Prompts.MemorySystem, rt.MemoryAgent!.SystemPrompt[0].Text);
    }

    [Fact]
    public void PrefixTokenDelta_PerTier_IsAtMost80()
    {
        var (off, _, _) = Create(t => t.Level = "off");
        var baseP = Prefixes(off, off.Options.Terse);
        foreach (var level in new[] { "lite", "full", "ultra" })
        {
            var (rt, _, _) = Create(t => { t.Level = level; t.Purposes["raptor"] = "lite"; });
            var p = Prefixes(rt, rt.Options.Terse);
            foreach (var k in p.Keys)
            {
                int delta = TextUtil.EstimateTokens(p[k]) - TextUtil.EstimateTokens(baseP[k]);
                output.WriteLine($"{level,-5} {k,-10} +{delta} tokens");
                Assert.InRange(delta, 1, 80);
            }
        }
    }

    // ---------------- chat stays in persona ----------------

    private static string ChatRules(string prompt)
    {
        int a = prompt.IndexOf("Chat rules", StringComparison.Ordinal), b = prompt.IndexOf("How turns work", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a);
        return prompt[a..b];
    }

    [Fact]
    public void Chat_IsNotAffected_PersonaAndChatRulesUnchanged_StyleFarFromChatRules()
    {
        var (off, _, _) = Create(t => t.Level = "off");
        var (on, _, _) = Create(t => t.Level = "ultra");
        var p = on.Agent.SystemPrompt[0].Text;
        Assert.Contains(Persona, p);
        Assert.Equal(ChatRules(off.Agent.SystemPrompt[0].Text), ChatRules(p));
        foreach (var word in new[] { "terse", "caveman", "fragment", "articles", "Internal style" })
            Assert.DoesNotContain(word, ChatRules(p), StringComparison.OrdinalIgnoreCase);
        // The style section comes after Safety, says it is not for chat, and the persona section precedes the chat rules.
        int style = p.IndexOf("Internal style", StringComparison.Ordinal);
        Assert.True(style > p.IndexOf("Safety", StringComparison.Ordinal));
        Assert.True(p.IndexOf(Persona, StringComparison.Ordinal) < p.IndexOf("Chat rules", StringComparison.Ordinal));
        Assert.Contains("never chat; chat keeps the persona voice", p[style..]);
        // Everything before the style section is byte-identical to the off prompt.
        Assert.StartsWith(p[..(style - 1)], off.Agent.SystemPrompt[0].Text);
    }

    [Fact]
    public void BrainStyle_SplitsLines_WhenThinkingAndDelegateLevelsDiffer()
    {
        var (rt, _, _) = Create(t => { t.Purposes["thinking"] = "lite"; t.Purposes["delegate"] = "full"; });
        var p = rt.Agent.SystemPrompt[0].Text;
        Assert.Contains("- Thinking: " + Prompts.TerseStyle(TerseLevel.Lite), p);
        Assert.Contains("- delegate/ask_memory inputs: " + Prompts.TerseStyle(TerseLevel.Full), p);
        Assert.Contains("Drop no detail. Keep exact: " + Prompts.TerseKeep, p);
    }

    [Theory]
    [InlineData("ok: 12 oak_log, sapling at 9,64,3", "ok: 12 oak_log, sapling at 9,64,3")]
    [InlineData("Two chests could be Fox's. Ambiguous.  needs_input: which one, -3,64,5 or 4,64,1?", "needs_input: which one, -3,64,5 or 4,64,1?")]
    [InlineData("Done placing.\nfailed: out of cobblestone; 6 of 15 left", "failed: out of cobblestone; 6 of 15 left")]
    [InlineData("Okay, all good", "Okay, all good")]
    public void WorkerResultLine_SkipsAReasoningPreface(string text, string expected) =>
        Assert.Equal(expected, MineClaw.Agent.Worker.WorkerAgent.ResultLine(text));

    [Fact]
    public async Task Worker_PrefacedNeedsInput_IsClassifiedAsNeedsInput()
    {
        var (rt, llm, _) = Create();
        llm.Then(r => llm.Resp(r, StopReasons.EndTurn, new LlmText("Two chests say Fox. needs_input: -3,64,5 or 4,64,1 for the 3 diamonds?")));
        var o = await rt.Worker!.RunAsync("put 3 diamonds in Fox's chest", "0 diamond in inventory", 2, default);
        Assert.True(o.NeedsInput);
        Assert.False(o.Ok);
        Assert.StartsWith("needs_input: -3,64,5 or 4,64,1", o.Summary);
    }

    // ---------------- telemetry sections ----------------

    [Fact]
    public void Sections_SplitIdentity_SelfBlock_Memory_Digest_Events()
    {
        const string self = "[self] who you've grown into: hums while mining; kiwi: bestie";
        const string memory = "[memory] #12 furnace row by spawn";
        const string digest = "[since last turn] kiwi placed 4 torches";
        var text = "pos 1,64,2 | task: idle\n" + digest + "\n" + self + "\n" + memory + "\n[chat] <kiwi> hi";
        var turn = new TurnInput(DateTimeOffset.UtcNow, ModelTier.Main, "pos 1,64,2 | task: idle", digest, memory, "[chat] <kiwi> hi", text, self);
        var identity = "Your name in chat is Clawd. Your login username is Clawd_bot.";
        var req = new LlmRequest
        {
            System = [new LlmSystemBlock(new string('a', 700) + "\n# Skyblock core rules\n" + new string('k', 350)), new LlmSystemBlock(identity)],
            Messages = [LlmMessage.User(text)],
        };
        var (s, _) = PromptSections.Estimate(req, turn);
        Assert.Contains(PromptSection.Self, PromptSection.All);
        Assert.Equal(TextUtil.EstimateTokens(identity), s[PromptSection.Identity]);
        Assert.Equal(TextUtil.EstimateTokens(self), s[PromptSection.Self]);
        Assert.Equal(TextUtil.EstimateTokens(memory), s[PromptSection.Memory]);
        Assert.Equal(TextUtil.EstimateTokens(digest), s[PromptSection.Digest]);
        Assert.Equal(TextUtil.EstimateTokens(text) - s[PromptSection.Self] - s[PromptSection.Memory] - s[PromptSection.Digest] + 4,
            s[PromptSection.Events]);

        // Legacy v1 (Growth off): the self-profile follows the identity paragraph inside system block 2.
        var legacy = new LlmRequest { Messages = req.Messages, System = [req.System[0], new LlmSystemBlock(identity + "\n\nYou've grown: likes cobble, hums while mining")] };
        var (l, _) = PromptSections.Estimate(legacy);
        Assert.Equal(TextUtil.EstimateTokens(identity), l[PromptSection.Identity]);
        Assert.True(l[PromptSection.Self] > 0);
    }

    [Fact]
    public async Task BrainTurn_AttributesTheSelfBlockToSelf_NotEvents()
    {
        var options = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0 };
        options.Chat.Username = "Clawd_bot";
        options.Chat.DisplayName = "Clawd";
        options.Chat.Aliases = [];
        options.Chat.LineDelayMs = 0;
        var fake = new FakeLlm();
        var tel = new PromptTelemetry(options);
        var rt = AgentRuntime.Create(options, (_, _) => Task.CompletedTask, tel.Wrap(fake), persist: false);
        tel.Attach(rt);
        Assert.NotNull(rt.Growth);
        Assert.True(rt.Self!.Apply(new SelfChange("add", SelfSection.Trait, "hums while mining", Reason: "seen"), SelfProfile.SourceTool).Ok);
        var records = new List<PromptUsageRecord>();
        TurnInput? seen = null;
        tel.Recorded += records.Add;
        rt.Agent.TurnBuilt += t => seen = t;
        fake.UsagePerCall = new LlmUsage(300, 40, 9000, 700);
        fake.ThenText("hiii");

        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("kiwi", "clawd hi")]), default);

        Assert.NotNull(seen);
        Assert.StartsWith(ContextManager.SelfBlockTag, seen!.Self);
        var r = Assert.Single(records);
        Assert.True(r.Sections[PromptSection.Self] > 0);
        Assert.True(r.Sections[PromptSection.Identity] > 0);
        Assert.DoesNotContain(ContextManager.SelfBlockTag, rt.Agent.SystemPrompt[1].Text); // v2: not in block 2
        Assert.Equal(r.PromptTokens, r.Sections.Values.Sum());
        output.WriteLine(string.Join(" ", r.Sections.Select(kv => $"{kv.Key}={kv.Value}")));
    }
}
