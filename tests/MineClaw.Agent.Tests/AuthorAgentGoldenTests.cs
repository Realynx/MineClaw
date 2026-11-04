using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Authoring;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins the script author's loop (WP06 refactor of <c>AuthorAgent.RunAsync</c>): for scripted drafts, the exact request payload of
/// every call as it left (the Anthropic params, the spend cap left and the cost function), and the outcome. Hashed (line-ending
/// independent); on a mismatch the dump lands in the temp dir.
/// </summary>
public class AuthorAgentGoldenTests
{
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["revise"] = "9590E1061AF474B841CBC698AC9E25227CE0462CFF3A70AD6FFE2CD42562ED21",
        ["give_up"] = "E9DEAE98068EAB272EC1D9BFA51B569A76E678E5E1B2F35851DE8A22EDA72E38",
        ["cost_cap_throw"] = "69C9FAEF58077B1E6622AB710BF971364DEFDBDEE7E8045F2BF4ADD5C6FDC3F5",
        ["pass"] = "AC23BD5DD45727390D1F21C66F17BF571C06297E484A9EAE2D4DDBFBB7B6D28D",
        ["one_version"] = "8E8A49724CD16CE874D169A2F11CF8AB3D4A1001FA2929DA34F42807FFED58A6",
        ["interrupted"] = "4D91C7091ABB30FA2CD4FCC4DA630316FD460F8572666AEFD75ACE87FA46785E",
        ["job_cap"] = "2297CE314907FCDCB996FADE10BB04C45CD350C349C70CDEAE30FC524B5CCC44",
        ["interrupted_no_usage"] = "0B2CFAC860F6CA7A8666C94685647FAF414FF8BD89647BD909CE181D069A277D",
        ["job_cap_looser"] = "F9BCA13E920FCD445BB071E7BB0641F500B614C82293C433F536D58A85158BF5",
        ["refused"] = "EEB3A34372F1D932BA1E6AFEFC6FEFD4445947BF50B98475FC208B62DB35D48B",
        ["sonnet_brain"] = "2E1106C4FFFECA2877A034B59BF171A1388525E2821526E0757329246185FC77",
        ["ollama"] = "A59876A7D2A941C36BCB1DCDF95FBE28177B172D40279E48D6C04DBF496AE913",
        ["spent_cap"] = "5CD0A1462640129D44508BDCDB05CAAECB03C024A49EB3D099EB7BAB257FE8F2",
    };

    /// <summary>Answers each call with the next scripted reply (or throws it); dumps every request at call time.</summary>
    private sealed class Drafts(LlmOptions o, params Func<LlmRequest, LlmResponse>[] replies) : ILlmClient
    {
        private int _i;
        public StringBuilder Log { get; } = new();

        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            var client = new AnthropicLlmClient(o);
            Log.Append("== call ").Append(_i).Append(" tier=").Append(r.Tier).Append(" purpose=").Append(r.Purpose).Append(" maxTokens=")
               .Append(r.MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? "-").Append(" cacheHistory=").Append(r.CacheHistory)
               .Append(" effort=").Append(r.Effort ?? "-").Append(" maxUsd=").Append(r.MaxUsd?.ToString(CultureInfo.InvariantCulture) ?? "-")
               .Append(" cost=").Append(r.CostOf?.Invoke(new LlmUsage(1000, 2000, 0, 0)).ToString(CultureInfo.InvariantCulture) ?? "-").Append('\n');
            Log.Append(JsonSerializer.Serialize(client.BuildParams(r, r.Model ?? o.ModelFor(r.Tier)))).Append('\n');
            return Task.FromResult(replies[_i++](r));
        }
    }

    private static Func<LlmRequest, LlmResponse> Reply(string text, string stop = StopReasons.EndTurn, int output = 2000) =>
        r => new LlmResponse([new LlmText(text)], stop, new LlmUsage(500, output, 100, 50), r.Model!);

    private static AuthorJob Job(decimal? maxUsd = null) => new()
    {
        Kind = "script", System = "REFERENCE: lua cheat-sheet", Brief = "goal: mine the cobble gen\nfacts: gen at the west cell",
        Check = code => code.Contains("bad", StringComparison.Ordinal) ? AuthorCheck.Fail("lint errors: L1 bad thing\nL2 more")
            : code.Contains("boom", StringComparison.Ordinal) ? throw new InvalidOperationException("checker exploded") : AuthorCheck.Pass,
        MaxUsd = maxUsd,
    };

    private static string Outcome(AuthorOutcome r) =>
        $"== outcome ok={r.Ok} versions={r.Versions} usage={r.Usage} usd={r.Usd.ToString(CultureInfo.InvariantCulture)} cost={r.Cost} " +
        $"model={r.Model} blocker={r.Blocker ?? "-"}\nartifact={r.Artifact ?? "-"}\n";

    private static async Task<string> Run(LlmOptions o, AuthorJob job, params Func<LlmRequest, LlmResponse>[] replies)
    {
        var llm = new Drafts(o, replies);
        string tail;
        try { tail = Outcome(await new AuthorAgent(llm, o).RunAsync(job, CancellationToken.None)); }
        catch (Exception ex) { tail = "== threw " + ex.GetType().Name + ": " + ex.Message + "\n"; }
        return (llm.Log + tail).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\\r\\n", "\\n", StringComparison.Ordinal);
    }

    private static void Check(string name, string dump)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden", "author");
        if (!Expected.TryGetValue(name, out var want) || want != hash)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".txt"), dump);
        }
        Assert.True(Expected.TryGetValue(name, out want), $"no golden for {name}: [\"{name}\"] = \"{hash}\",");
        Assert.True(want == hash, $"{name} payload changed: {hash} (dump in {dir})");
    }

    private static LlmOptions Options(Action<LlmOptions>? configure = null)
    {
        var o = new LlmOptions { ApiKey = "sk-test-not-real", AuthorMaxVersions = 5 };
        configure?.Invoke(o);
        return o;
    }

    [Fact]
    public async Task Revisions_after_a_cut_off_a_missing_block_a_lint_failure_and_a_throwing_check()
    {
        Check("revise", await Run(Options(), Job(),
            Reply("thinking about it... ```lua\nunfinished", StopReasons.MaxTokens),
            Reply("here you go, no fence"),
            Reply("```lua\nbad()\n```"),
            Reply(""),
            Reply("```lua\n-- purpose: mine\nboom()\n```")));
        Check("pass", await Run(Options(), Job(), Reply("```lua\nbad()\n```"), Reply("```lua\n-- purpose: mine\ngood()\n```")));
    }

    [Fact]
    public async Task Giving_up_after_the_last_version()
    {
        Check("give_up", await Run(Options(o => o.AuthorMaxVersions = 2), Job(), Reply("```lua\nbad()\n```"), Reply("```\nbad again\n```")));
        Check("one_version", await Run(Options(o => o.AuthorMaxVersions = 0), Job(), Reply("no block")));
    }

    [Fact]
    public async Task Spend_caps_effort_and_the_job_cap()
    {
        Check("spent_cap", await Run(Options(o => o.AuthorMaxUsd = 0.05m), Job(), Reply("```lua\nbad()\n```", output: 50_000)));
        Check("job_cap", await Run(Options(o => { o.AuthorMaxUsd = 0; o.AuthorEffort = "  high "; }), Job(maxUsd: 1.25m),
            Reply("```lua\nbad()\n```"), Reply("```lua\ngood()\n```")));
        Check("job_cap_looser", await Run(Options(o => { o.AuthorMaxUsd = 0.5m; o.AuthorEffort = " "; }), Job(maxUsd: 9m),
            Reply("```lua\ngood()\n```")));
        Check("sonnet_brain", await Run(Options(o => o.MainModel = "claude-sonnet-5-5"), Job(), Reply("```lua\ngood()\n```")));
    }

    [Fact]
    public async Task Cost_cap_and_interrupted_calls_and_refusals()
    {
        Check("cost_cap_throw", await Run(Options(), Job(), Reply("```lua\nbad()\n```"),
            r => throw new LlmCostCapException(new LlmUsage(100, 40_000, 0, 0), r.Model!, 1.0m, r.MaxUsd ?? 0)));
        Check("interrupted", await Run(Options(), Job(), Reply("```lua\nbad()\n```"),
            r => throw new LlmInterruptedException(new LlmUsage(100, 700, 0, 0), r.Model!)));
        Check("interrupted_no_usage", await Run(Options(), Job(), r => throw new LlmInterruptedException(null, r.Model!)));
        var haiku = Options();
        Assert.Null(haiku.ApplyBrainProfile("haiku"));
        Check("refused", await Run(haiku, Job()));
        Check("ollama", await Run(Options(o => o.MainProvider = LlmProviders.Ollama), Job()));
    }
}
