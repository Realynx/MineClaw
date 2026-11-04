using System.Text;
using MineClaw.Agent;
using MineClaw.App.Hosting;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="ConfigCheck.Validate"/> (WP07): every message it can produce, alone and together, in order, from a valid main
/// config with one or several things wrong.
/// </summary>
public class ConfigCheckGoldenTests
{
    private static (MinecraftSettings Mc, AgentOptions Agent) Valid()
    {
        var mc = new MinecraftSettings { Host = "10.1.2.69", Username = "maid_chan" };
        var a = new AgentOptions();
        a.Llm.ApiKey = "sk-ant-test";
        a.Chat.Username = "maid_chan";
        return (mc, a);
    }

    private static IEnumerable<(string Label, Action<MinecraftSettings, AgentOptions> Change)> Cases()
    {
        yield return ("valid", (_, _) => { });
        yield return ("empty host", (mc, _) => mc.Host = " ");
        yield return ("port 0", (mc, _) => mc.Port = 0);
        yield return ("port 65536", (mc, _) => mc.Port = 65536);
        yield return ("port 65535", (mc, _) => mc.Port = 65535);
        yield return ("empty username", (mc, a) => { mc.Username = ""; a.Chat.Username = ""; });
        yield return ("long username", (mc, a) => { mc.Username = new string('a', 17); a.Chat.Username = mc.Username; });
        yield return ("16-char username", (mc, a) => { mc.Username = new string('a', 16); a.Chat.Username = mc.Username; });
        yield return ("ollama tier without endpoint", (_, a) => { a.Llm.MainProvider = "ollama"; a.Llm.MainModel = "qwen3:8b"; });
        yield return ("unknown provider", (_, a) => a.Llm.CheapProvider = "nope");
        yield return ("mock tier", (_, a) => a.Llm.MainProvider = "mock");
        yield return ("mock tier, missing script", (_, a) => { a.Llm.MainProvider = "mock"; a.Llm.MockScriptPath = "C:/no-such-dir-wp07/mock.json"; });
        yield return ("old perception model", (_, a) => a.Llm.Perception.Model = "claude-3-5-sonnet");
        yield return ("old perception model, padded provider", (_, a) => { a.Llm.Perception.Provider = " Anthropic "; a.Llm.Perception.Model = "claude-3-5-sonnet"; });
        yield return ("old perception model off anthropic", (_, a) => { a.Llm.Perception.Provider = "ollama"; a.Llm.Perception.Model = "claude-3-5-sonnet"; });
        yield return ("no api key", (_, a) => a.Llm.ApiKey = null);
        yield return ("blank api key", (_, a) => a.Llm.ApiKey = " ");
        yield return ("not an anthropic key", (_, a) => a.Llm.ApiKey = "sk-proj-123");
        yield return ("no key, all tiers on ollama", (_, a) =>
        {
            a.Llm.ApiKey = null;
            a.Llm.MainProvider = a.Llm.WorkerProvider = a.Llm.CheapProvider = "ollama";
            a.Llm.Ollama.Endpoint = "http://localhost:11434";
        });
        yield return ("no cheap model", (_, a) => a.Llm.CheapModel = "");
        yield return ("no main model", (_, a) => a.Llm.MainModel = " ");
        yield return ("zero daily cap", (_, a) => a.Budget.DailyCapUsd = 0);
        yield return ("unpriced cheap model", (_, a) => a.Llm.CheapModel = "claude-haiku-5-5-x");
        yield return ("unpriced main model", (_, a) => a.Llm.MainModel = "claude-opus-5-5-x");
        yield return ("unpriced main model on ollama", (_, a) =>
        {
            a.Llm.MainProvider = "ollama";
            a.Llm.MainModel = "claude-opus-5-5-x";
            a.Llm.Ollama.Endpoint = "http://localhost:11434";
        });
        yield return ("ollama embeddings, nothing set", (_, a) => { a.Embeddings.Provider = "Ollama"; a.Embeddings.Model = ""; a.Embeddings.Dimensions = 0; a.Embeddings.Endpoint = ""; });
        yield return ("ollama embeddings, rag off", (_, a) => { a.Embeddings.Provider = "ollama"; a.Embeddings.Model = ""; a.Rag.Enabled = false; });
        yield return ("ollama embeddings via the llm endpoint", (_, a) =>
        {
            a.Embeddings.Provider = "ollama";
            a.Embeddings.Model = "nomic";
            a.Embeddings.Endpoint = "";
            a.Llm.Ollama.Endpoint = "http://localhost:11434";
        });
        yield return ("ollama embeddings with endpoint", (_, a) => { a.Embeddings.Provider = "ollama"; a.Embeddings.Model = "nomic"; a.Embeddings.Endpoint = "http://e"; a.Embeddings.Dimensions = -1; });
        yield return ("chat username differs", (_, a) => a.Chat.Username = "someone");
        yield return ("chat username case", (_, a) => a.Chat.Username = "MAID_CHAN");
        yield return ("everything wrong", (mc, a) =>
        {
            mc.Host = "";
            mc.Port = -1;
            mc.Username = "";
            a.Llm.ApiKey = "bad";
            a.Llm.CheapModel = "";
            a.Llm.MainModel = "claude-opus-5-5-x";
            a.Budget.DailyCapUsd = -1;
            a.Llm.Perception.Model = "claude-3-opus";
            a.Embeddings.Provider = "ollama";
            a.Embeddings.Model = "";
            a.Embeddings.Dimensions = 0;
            a.Embeddings.Endpoint = "";
            a.Chat.Username = "x";
            a.Llm.WorkerProvider = "ollama";
        });
    }

    /// <summary>The missing mock script's IO message names an OS path: kept as a marker so the dump reads the same everywhere.</summary>
    private static string Portable(string problem) =>
        problem.StartsWith("Agent:Llm:MockScriptPath: ", StringComparison.Ordinal) && problem.Contains("no-such-dir-wp07", StringComparison.Ordinal)
            ? "Agent:Llm:MockScriptPath: <the IO message for the missing path>"
            : problem;

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (label, change) in Cases())
        {
            var (mc, a) = Valid();
            change(mc, a);
            sb.Append("== ").Append(label).Append('\n');
            foreach (var p in ConfigCheck.Validate(mc, a)) sb.Append(Portable(p)).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Validate_matches_the_golden_dump() => RouteGolden.Check("config-check", ConfigCheckGoldenData.Expected, Dump());
}
