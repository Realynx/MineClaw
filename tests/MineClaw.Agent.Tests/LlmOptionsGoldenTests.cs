using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins the startup model guard and the profile checks (WP06 refactor of <c>LlmOptions.ValidateProviders</c>,
/// <c>CheckProfile</c> and <c>ApplyBrainProfile</c>): over a corpus of configs, every validation message, every profile
/// check and every brain-profile application (the resulting tiers), plus the model allowlist and its verdict on a corpus of
/// model ids (the 5.5 generation, the kept Fable/Mythos 5.1 ids, older ones). Hashed; on a mismatch the dump lands in the temp dir.
/// </summary>
public class LlmOptionsGoldenTests
{
    private const string Expected = "D56A6F8D47C4CF15D82E611EABE43C5CD23309D042AE3450EFF864BAEF693025";

    private static readonly string[] Models =
    [
        "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1", "claude-mythos-5-1", " CLAUDE-OPUS-5-5 ",
        "claude-opus-5-5-20261101", "claude-mythos-5-1-preview", "claude-opus-5", "claude-sonnet-5", "claude-sonnet-4-5", "claude-haiku-4-5",
        "claude-opus-4-1", "claude-3-5-sonnet-latest", "claude-fable-5", "qwen3:32b", "gpt-5", "",
    ];

    private static Dictionary<string, LlmProfile> CustomProfiles() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["opus"] = new() { Model = "claude-opus-5-5" },
        ["fable"] = new() { Model = "claude-fable-5-1", MaxTokens = 4000, Thinking = "adaptive", Worker = "sonnet55", Memory = "local" },
        ["sonnet55"] = new() { Model = " claude-sonnet-5-5 ", MaxTokens = 0 },
        ["local"] = new() { Provider = "ollama", Model = "qwen3:8b", MaxTokens = 2000 },
        ["localhaiku"] = new() { Provider = " Ollama ", Model = "claude-haiku-5-5", Worker = "local", Memory = "local" },
        ["oldie"] = new() { Model = "claude-3-5-sonnet-latest" },
        ["bogus"] = new() { Provider = "openai", Model = "gpt-5" },
        ["bogusempty"] = new() { Provider = "openai", Model = "" },
        ["empty"] = new() { Model = "  " },
        ["selfref"] = new() { Model = "claude-haiku-5-5", Worker = "selfref", Memory = "SELFREF" },
        ["badsub"] = new() { Model = "claude-opus-5-5", Worker = "bogus" },
        // Not in the corpus: a sub-profile naming a missing profile (Memory = "nope") recurses without end (the "no profile"
        // refusal lists the usable profiles, which checks the referrer again): a stack overflow, kept as is.
        ["mockp"] = new() { Provider = "mock", Model = "m1", MaxTokens = -5, Thinking = " " },
        ["disabled"] = new() { Model = "claude-opus-5-5" },
        ["mythos"] = new() { Provider = " Anthropic ", Model = "claude-mythos-5-1", MaxTokens = 9000, Worker = "fable" },
        ["blankprov"] = new() { Provider = "", Model = "claude-sonnet-4-5" },
    };

    private static readonly (string Name, Action<LlmOptions> Configure)[] Configs =
    [
        ("defaults", _ => { }),
        ("defaults_key", o => o.ApiKey = "sk-test-not-real"),
        ("bad_providers", o => { o.ApiKey = "sk-test-not-real"; o.MainProvider = "gpt"; o.WorkerProvider = " OLLAMA "; o.CheapProvider = "mock"; }),
        ("bad_endpoint", o => { o.Ollama.Endpoint = "not a url"; o.CheapProvider = "ollama"; o.WorkerProvider = "Ollama"; }),
        ("ollama_main", o => { o.Ollama.Endpoint = " http://localhost:11434 "; o.MainProvider = "ollama"; o.MainModel = "qwen3:32b"; }),
        ("old_models", o => { o.ApiKey = "sk-test-not-real"; o.MainModel = "claude-opus-4-1"; o.WorkerModel = "claude-sonnet-4-5"; o.CheapModel = " claude-haiku-5-5-20261001 "; }),
        ("fable_mythos", o => { o.ApiKey = "sk-test-not-real"; o.MainModel = "claude-fable-5-1"; o.WorkerModel = "claude-mythos-5-1-preview"; o.CheapModel = "claude-fable-5"; }),
        ("old_on_ollama", o => { o.Ollama.Endpoint = "http://x:1"; o.WorkerProvider = "ollama"; o.WorkerModel = "claude-sonnet-4-5"; o.CheapProvider = ""; o.CheapModel = "claude-haiku-4-5"; }),
        ("profiles_all", o => { o.ApiKey = "sk-test-not-real"; o.Ollama.Endpoint = "http://localhost:11434"; o.Profiles = CustomProfiles(); o.DisableProfile("disabled", "tool-less model"); }),
        ("profiles_nokey", o => { o.Profiles = CustomProfiles(); }),
        ("profiles_key_only", o => { o.ApiKey = "sk-test-not-real"; o.Profiles = CustomProfiles(); o.DisableProfile(" Disabled ", "missing"); }),
        ("profiles_endpoint_only", o => { o.Ollama.Endpoint = "ftp//nope"; o.Profiles = CustomProfiles(); }),
    ];

    private static LlmOptions Build(Action<LlmOptions> configure)
    {
        var o = new LlmOptions();
        configure(o);
        return o;
    }

    private static void State(StringBuilder sb, LlmOptions o) =>
        sb.Append("  main=").Append(o.MainModel).Append('|').Append(o.MainProvider).Append('|').Append(o.MainMaxTokens).Append('|')
          .Append(o.MainThinking).Append(" profile=").Append(o.MainProfile ?? "-").Append(" authors=").Append(o.BrainAuthorsScripts)
          .Append(" worker=").Append(o.WorkerModel).Append('|').Append(o.WorkerProvider).Append('|').Append(o.WorkerMaxTokens)
          .Append(" cheap=").Append(o.CheapModel).Append('|').Append(o.CheapProvider).Append('|').Append(o.CheapMaxTokens).Append('\n');

    private static string Try(Func<string?> f)
    {
        try { return f() ?? "<ok>"; }
        catch (Exception ex) { return "<threw " + ex.GetType().Name + ": " + ex.Message + ">"; }
    }

    private static string Dump()
    {
        var sb = new StringBuilder();
        sb.Append("allowlist: ").Append(string.Join(", ", ModelProfiles.CurrentGeneration)).Append('\n');
        foreach (var m in Models) sb.Append("current ").Append('[').Append(m).Append("]=").Append(ModelProfiles.IsCurrentGeneration(m)).Append('\n');
        foreach (var (name, configure) in Configs)
        {
            var o = Build(configure);
            sb.Append("== ").Append(name).Append('\n');
            foreach (var p in o.ValidateProviders()) sb.Append("  problem: ").Append(p).Append('\n');
            var asks = new List<string?> { null, "", "  ", "nope", " OPUS " };
            asks.AddRange(o.Profiles.Keys);
            foreach (var ask in asks) sb.Append("  check [").Append(ask ?? "null").Append("]: ").Append(Try(() => o.CheckProfile(ask))).Append('\n');
            sb.Append("  usable: ").Append(string.Join(", ", o.UsableProfiles())).Append('\n');
            foreach (var key in o.Profiles.Keys.Append(" Opus ").Append("nope"))
            {
                var fresh = Build(configure);
                sb.Append("  apply [").Append(key).Append("]: ").Append(Try(() => fresh.ApplyBrainProfile(key))).Append('\n');
                State(sb, fresh);
                foreach (var p in fresh.ValidateProviders()) sb.Append("    problem: ").Append(p).Append('\n');
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void Model_guard_profile_checks_and_brain_profiles_over_a_config_corpus()
    {
        var dump = Dump();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden", "llm-options");
        if (hash != Expected)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "llm_options.txt"), dump);
        }
        Assert.True(hash == Expected, $"LLM option checks changed: {hash} (dump in {dir})");
    }
}
