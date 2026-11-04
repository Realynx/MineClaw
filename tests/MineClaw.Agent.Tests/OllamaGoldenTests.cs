using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins the Ollama client's model-facing mapping (WP06 refactor): <c>BuildBody</c> over system/tool/image/think/option variants,
/// the response <c>Accumulator</c> over whole and streamed chunks, and <c>Repair</c> over malformed tool arguments.
/// </summary>
public class OllamaGoldenTests
{
    private const string Expected = "E2C214FE3197F2672E03DBF86FFDF682C7DC471171868E2756C35AE43616E1DB";

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private static LlmOptions Opts(Action<LlmOptions>? more = null)
    {
        var o = new LlmOptions { MainProvider = "ollama", MainModel = "qwen3:8b", MainMaxTokens = 900 };
        o.Ollama.Endpoint = "http://ollama.test:11434";
        more?.Invoke(o);
        return o;
    }

    [Fact]
    public void Bodies_responses_and_repairs_map_the_same()
    {
        var sb = new StringBuilder();
        var img = new LlmImage([1, 2, 3, 4], 64, 64, "[image omitted]");
        var goto_ = new LlmToolDefinition("goto", "Walk somewhere.", J(new { type = "object", properties = new { to = new { type = "string" } } }));
        var bare = new LlmToolDefinition("look", "Look.", default);
        var full = new LlmRequest
        {
            Tier = ModelTier.Main,
            System = [new LlmSystemBlock("static rules", Cache: true), new LlmSystemBlock(""), new LlmSystemBlock("identity")],
            Tools = [goto_, bare],
            Messages =
            [
                LlmMessage.User("go to the gen"),
                LlmMessage.Assistant(new LlmText("on it"), new LlmToolUse("tu_9", "goto", J(new { to = "gen" })), new LlmThinking("hmm", "sig"),
                    new LlmToolUse("tu_10", "look", J(new Dictionary<string, string> { [LlmToolArgs.InvalidKey] = "{bad" })), new LlmToolUse("tu_11", "x", J(new[] { 1 }))),
                LlmMessage.User(new LlmToolResult("tu_9", "no route", IsError: true), new LlmToolResult("tu_10", "error: already", IsError: true),
                    new LlmToolResult("tu_11", "view") { Image = img }, new LlmToolResult("tu_zz", "orphan"), new LlmText("[new events] kiwi: hi"), new LlmText("")),
                LlmMessage.User(new LlmImageBlock(img)),
                LlmMessage.User(new LlmToolResult("tu_9", "ok")),
            ],
        };
        var minimal = new LlmRequest { Tier = ModelTier.Main, Messages = [LlmMessage.User("hi")], MaxTokens = 77, Thinking = "omit" };
        var infos = new OllamaModelInfo?[]
        {
            null, new("qwen3:8b", null), new("qwen3:8b", ["completion", "tools"]), new("qwen3:8b", ["completion", "vision", "thinking"]),
        };
        var configs = new (string Name, Action<LlmOptions> Set)[]
        {
            ("plain", _ => { }),
            ("ctx+keep+temp", o => { o.Ollama.DefaultNumCtx = 8192; o.Ollama.KeepAlive = " 30m "; o.Ollama.Temperature = 0.25; }),
            ("adaptive+vision", o => { o.MainThinking = "adaptive"; o.Ollama.VisionModels = ["qwen3"]; }),
            ("disabled-think", o => o.MainThinking = "disabled"),
        };
        foreach (var (name, set) in configs)
        {
            using var client = new OllamaLlmClient(Opts(set), new HttpClient(new FakeOllama()));
            foreach (var info in infos)
                foreach (var (label, req) in new[] { ("full", full), ("minimal", minimal) })
                    foreach (var stream in new[] { false, true })
                        sb.Append(name).Append(' ').Append(info?.Capabilities == null ? (info == null ? "noinfo" : "nocaps") : string.Join(",", info.Capabilities))
                          .Append(' ').Append(label).Append(' ').Append(stream).Append(' ')
                          .Append(client.BuildBody(req, "qwen3:8b", info, stream).ToJsonString()).Append('\n');
        }

        string[][] streams =
        [
            ["""{"message":{"role":"assistant","content":"hello"},"done":true,"done_reason":"stop","prompt_eval_count":12,"eval_count":3}"""],
            ["""{"message":{"content":"a","thinking":"hm"}}""", """{"message":{"content":"","thinking":""}}""", """{"message":{"content":"b"},"done":false}""",
             """{"done":true,"done_reason":"length","prompt_eval_count":5,"eval_count":9}"""],
            ["""{"message":{"tool_calls":[{"function":{"name":"goto","arguments":{"to":"gen"}}},{"id":"c7","function":{"name":" look ","arguments":"{\"dir\":\"n\""}},{"function":{"name":""}},{"name":"bare","arguments":"not json"},{"function":{"name":"nul","arguments":null}}]},"done":true}"""],
            ["""{"message":"text"}""", """{"done":"yes","eval_count":4}""", """{"message":{"content":"   "},"done":true}"""],
        ];
        foreach (var chunks in streams)
        {
            var seen = new List<string>();
            var acc = new OllamaLlmClient.Accumulator { OnToolUse = u => seen.Add(u.Name) };
            foreach (var c in chunks)
            {
                using var doc = JsonDocument.Parse(c);
                sb.Append("add ").Append(acc.Add(doc.RootElement)).Append(' ').Append(acc.Done).Append('\n');
            }
            var r = acc.ToResponse("m");
            sb.Append("resp ").Append(r.StopReason).Append(' ').Append(r.Usage.InputTokens).Append('/').Append(r.Usage.OutputTokens).Append(' ')
              .Append(r.Reasoning ?? "null").Append(" early=").Append(string.Join(",", seen)).Append('\n');
            foreach (var b in r.Content)
                sb.Append("  ").Append(b switch
                {
                    LlmText t => "text " + t.Text,
                    LlmToolUse u => $"use {(u.Id.StartsWith("call_", StringComparison.Ordinal) ? "call_*" + u.Id[^1] : u.Id)} {u.Name} {u.Input.GetRawText()}",
                    _ => b.GetType().Name,
                }).Append('\n');
        }

        string[] repairs =
        [
            "{\"a\":1}", "```json\n{\"a\":1}\n```", "{'a':'b'}", "{\"a\":[1,2,}", "{\"a\":\"unterminated", "{\"a\":{\"b\":[1,{\"c\":2",
            "noise {\"a\":1} trailing }", "{\"a\":\"x\\\"y\"", "no braces", "{\"s\":\"}{\"", "{\"a\":1}}", "[{\"a\":1}", "{\"a\":\"it's\"}",
        ];
        foreach (var raw in repairs) sb.Append("repair ").Append(raw).Append(" => ").Append(OllamaLlmClient.Repair(raw)).Append('\n');
        foreach (var raw in repairs)
            sb.Append("args ").Append(OllamaLlmClient.ParseArguments(J(raw)).GetRawText()).Append('\n');

        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ollama.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
