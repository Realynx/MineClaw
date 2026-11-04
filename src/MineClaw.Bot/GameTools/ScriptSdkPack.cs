using System.Text;
using MineClaw.Agent;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Admin.Shards.Packs;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Versioning;
using MineClaw.Scripting;
using MineClaw.Scripting.Sdk;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// The <c>sdk-js</c> knowledge pack: the JavaScript scripting SDK as retrievable memory, so a brain that plans or writes a script finds
/// the exact helper ("how do I wait for day in a script" → <c>waitFor</c>). Generated from the code, so it never drifts:
/// <list type="bullet">
/// <item>every current SDK module (core, world, flow, machine, policies, jobs, crew, character; never the deprecated compat one) and
/// each documented <c>def</c> with its doc comment (<see cref="SdkReference.Entries"/>, the same comments the author's cheat-sheet is
/// built from);</item>
/// <item>every game function a script calls as a global (aliases, action tools, catalog skills; snake_case + camelCase) with its
/// signature line and the tool's own description, listed from a real (offline, never connected) bot per game version and tagged
/// with the versions that have it;</item>
/// <item>the worked examples of <see cref="ScriptPrompts.Examples"/>.</item>
/// </list>
/// One chunk per module, function, game function and example (refs <c>kb:sdk-js.md:{part}</c>, stable names, not positions).
/// A mode pack valid in every version (<see cref="KnowledgePack.AnyVersion"/>), retrieval only (no cached-prompt lines).
/// </summary>
public static class ScriptSdkPack
{
    public const string Name = "sdk-js", Mode = "sdk-js", FileName = "sdk-js.md";

    /// <summary>The versions whose game functions the pack lists.</summary>
    public static IReadOnlyList<GameVersionProfile> Versions => [GameVersionProfiles.Beta181, GameVersionProfiles.Java1122];

    /// <param name="modules">SDK modules (null = the embedded ones; tools pass <see cref="SdkModules.FromDirectory"/> of the source tree).</param>
    /// <param name="versions">Game versions to list functions for (null = <see cref="Versions"/>).</param>
    public static GeneratedPack Build(IReadOnlyList<SdkModule>? modules = null, IReadOnlyList<GameVersionProfile>? versions = null)
    {
        // The deprecated compat module (old strategy helpers) still runs but is never taught: a fresh memory learns the primitives.
        modules = (modules ?? SdkModules.All).Where(m => !m.Deprecated).ToList();
        var docs = new List<RagDoc>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Add(string part, string title, string body)
        {
            var key = part;
            for (int n = 2; !used.Add(key); n++) key = $"{part}~{n}";
            docs.Add(new RagDoc($"kb:{FileName}:{key}", KnowledgeIndexer.Kind, title, body) { Source = FileName, Weight = 1.0, At = SeedCorpus.At });
        }

        var entries = SdkReference.Entries(modules);
        var functions = Functions(versions ?? Versions);
        Add("overview", "JS scripting SDK: overview",
            "MineClaw scripts are JavaScript, run by the script tool (author one with a goal, run saved ones by name). A script calls the " +
            "SDK helpers (modules: " + string.Join("; ", modules.Select(m => $"{m.Name} = {m.Summary}")) + ") and the game functions " +
            "(goto, mine, craft, chopTree...: each returns {ok, msg, n}; args positional in the listed order or one {name: value} object; " +
            "snake_case and camelCase both work). SDK names win over game functions with the same name.");
        AddModuleDocs(Add, modules, entries);
        AddGameFunctionDocs(Add, functions);
        int i = 0;
        foreach (var (title, code) in ScriptPrompts.Examples)
            Add($"example:{++i}", $"JS script example: {title}", $"Worked JS script example ({title}):\n```js\n{Unindent(code)}\n```");

        var sdkNames = string.Join(",", modules.Select(m => m.Name));
        return new GeneratedPack(Name, Mode, FileName,
            "sdk-js mode pack (every version): the JavaScript scripting SDK as memory: each module and helper with its doc comment, the " +
            "game functions a script calls (per game version) and worked examples. Generated from MineClaw.Scripting/Sdk/js and the bot's tools.",
            $"MineClaw.Scripting SDK modules ({sdkNames}) + game functions ({string.Join(", ", (versions ?? Versions).Select(v => v.Version.Id))}) + ScriptPrompts examples",
            docs);
    }

    /// <summary>One doc per module (its helpers listed), then one per SDK entry.</summary>
    private static void AddModuleDocs(Action<string, string, string> add, IReadOnlyList<SdkModule> modules, IReadOnlyList<SdkReference.Entry> entries)
    {
        foreach (var m in modules)
        {
            var names = entries.Where(e => e.Module == m.Name).SelectMany(e => e.Names).Distinct(StringComparer.Ordinal).ToList();
            add($"mod:{m.Name}", $"JS SDK module {m.Name}", $"JS script SDK module {m.Name}: {m.Summary}." +
                (names.Count > 0 ? " Helpers: " + string.Join(", ", names) + "." : ""));
        }
        foreach (var e in entries)
            add($"fn:{e.Module}.{(e.Names.Count > 0 ? e.Names[0] : "doc")}", $"JS SDK {e.Module}: {string.Join(", ", e.Names.Take(6))}",
                $"JS script SDK, {e.Module} module ({e.ModuleSummary}): {e.Text}");
    }

    private static void AddGameFunctionDocs(Action<string, string, string> add, IReadOnlyList<VersionedGlobal> functions)
    {
        foreach (var f in functions)
            add($"game:{f.Global.Camel}", $"JS game function {f.Global.Camel}",
                $"JS script game function {f.Global.Signature} ({f.Global.Kind}{(f.Global.Kind == "alias" ? " of " + f.Global.Target : "")}; " +
                $"{string.Join(", ", f.Versions)}): {f.Description}. Returns {{ok, msg, n}}.");
    }

    /// <summary>A game function with the versions that have it (same name and params) and its tool/skill description.</summary>
    public sealed record VersionedGlobal(ScriptGlobal Global, IReadOnlyList<string> Versions, string Description);

    /// <summary>The script globals of each version's offline bot, merged: one entry per (name, params) with the versions that have it.</summary>
    public static IReadOnlyList<VersionedGlobal> Functions(IReadOnlyList<GameVersionProfile> versions)
    {
        var merged = new List<(ScriptGlobal G, List<string> V, string D)>();
        foreach (var profile in versions)
            foreach (var (g, desc) in Globals(profile))
            {
                var hit = merged.FindIndex(x => x.G.Name == g.Name && x.G.Params.SequenceEqual(g.Params) && x.D == desc);
                if (hit >= 0) merged[hit].V.Add(profile.Version.Id);
                else merged.Add((g, [profile.Version.Id], desc));
            }
        return merged.Select(x => new VersionedGlobal(x.G, x.V, x.D)).ToList();
    }

    /// <summary>
    /// The globals a JS script gets on a bot of this version, with each target's one-line description: a real bot graph built offline
    /// (an unconnected client, in-memory db, no LLM calls), so the list is exactly what <c>JsRuntime</c> binds.
    /// </summary>
    public static IReadOnlyList<(ScriptGlobal Global, string Description)> Globals(GameVersionProfile profile)
    {
        var client = new MinecraftClient(new ClientOptions { Host = "offline.invalid", Port = 1, Username = "sdk_pack", Profile = profile, AutoReconnect = false });
        var options = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0, DataDirectory = Path.GetTempPath(), GameVersion = profile.Version.Id };
        options.Self.Enabled = false;
        options.Rag.SeedFile = "";
        using var host = BotHost.Create(new MinecraftGameClient(client), options, null, new OfflineLlm(), persist: false);
        var bridge = host.Body?.ScriptBridge ?? throw new InvalidOperationException("the offline bot has no script lane");
        var skills = host.Brain?.Skills.Registry;
        var list = new List<(ScriptGlobal, string)>();
        foreach (var g in ScriptGlobals.List(bridge.Controller.Host))
        {
            string desc = "";
            if (g.Kind != "skill" && host.Runtime.Tools.TryGet(g.Target, out var tool)) desc = tool.Description;
            else if (skills != null && skills.TryGet(g.Target, out var sk)) desc = sk.Spec.Summary;
            list.Add((g, OneLine(desc)));
        }
        return list;
    }

    private static string OneLine(string s)
    {
        var t = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        return t.Length > 220 ? t[..217].TrimEnd() + "..." : t.Length > 0 ? t : "no description";
    }

    private static string Unindent(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n');
        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())).Trim();
    }

    /// <summary>Never called: building the offline graph needs a client object, not a model.</summary>
    private sealed class OfflineLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("the sdk-js pack builder never calls a model");
    }
}
