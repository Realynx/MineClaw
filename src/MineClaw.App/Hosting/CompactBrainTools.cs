using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// The brain tool set for a hive brain without a prompt cache (DeepSeek through Ollama): every call resends the tool definitions,
/// so tools that model can't use (authoring scripts and designs is the Opus brain's job, admin and self-editing are main's) or that
/// only main needs leave its brain tier. Its workers and the C# behind every remaining tool are unchanged.
/// </summary>
public static class CompactBrainTools
{
    /// <summary>Brain-tier tools a non-Anthropic hive brain doesn't get (names that don't exist are ignored).</summary>
    public static readonly IReadOnlyList<string> Dropped =
    [
        "design",                                          // designs are drafted by the Opus brain (building one still works: do/delegate)
        "reflect_self", "identity",                         // one shared self profile and main's identity: main's
        "mark_structure", "describe_structure",             // recording/teaching structures with players: main's
        "watch_demo", "ask_stand",                          // demos and "stand here" asks: main talks with the players
    ];

    /// <summary>Takes <see cref="Dropped"/> off the brain tier; returns how many definitions it saved and their ≈tokens.</summary>
    public static (int Tools, int Tokens) Apply(ToolRegistry tools, ILogger? log = null)
    {
        int n = 0, tok = 0;
        foreach (var name in Dropped)
        {
            if (!tools.TryGet(name, out var tool) || !tools.Allows(tool, ModelTier.Main)) continue;
            var def = tools.Definitions(ModelTier.Main).FirstOrDefault(d => d.Name == name);
            if (def != null) tok += Tokens(def);
            tools.SetTiers(name, tools.TiersOf(tool) & ~ToolTiers.Main); // off the brain tier only: workers keep it
            n++;
        }
        log?.LogInformation("brain without a prompt cache: compact tools ({N} dropped, ≈{Tok} tokens per call saved; {Left} left, ≈{Total} tokens)",
            n, tok, tools.Definitions(ModelTier.Main).Count, tools.Definitions(ModelTier.Main).Sum(Tokens));
        return (n, tok);
    }

    public static int Tokens(LlmToolDefinition d) => (int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6;
}
