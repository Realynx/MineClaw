using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Data;
using MineClaw.Scripting;
using MineClaw.World;

namespace MineClaw.Scripting.Tests;

/// <summary>Offline script host: a real <see cref="WorldState"/>, every action recorded as "name {json}"; per-action overrides.</summary>
public sealed class Rig : IDisposable
{
    public WorldState World { get; } = new();
    public ConcurrentQueue<string> Calls { get; } = new();
    public List<string> CallList => Calls.ToList();
    public ConcurrentDictionary<string, Func<JsonElement, CancellationToken, Task<ActResult>>> On { get; } = new();
    public ConcurrentQueue<string> Said { get; } = new();
    public ConcurrentQueue<string> Emoted { get; } = new();
    public ConcurrentQueue<string> Logs { get; } = new();
    public ConcurrentQueue<string> Wakes { get; } = new();
    public string? AskReply { get; set; } = "yes";
    public string? Veto { get; set; }
    /// <summary>What where(item) answers (the real tool's format: "name x12: chest@3,64,2 (...) | total 17"); null = nowhere known.</summary>
    public Func<string, string?> Where { get; set; } = item => item == "iron" ? "iron: chest 3,64,2 (x12)" : null;
    public ScriptController C { get; }
    public ScriptHost Host { get; }

    /// <summary>The language <see cref="Start"/> runs (the legacy engine tests pass Lua code; JS tests use <see cref="Js"/>).</summary>
    public ScriptLanguage Lang { get; init; } = ScriptLanguage.Lua;

    /// <summary>A rig whose scripts are JavaScript.</summary>
    public static Rig Js(ScriptOptions? options = null, Crew.ICrewPort? crew = null, IScriptCheckpointStore? checkpoints = null,
        Func<string, string?>? storedParams = null, string? body = null, MineClaw.Data.Versioning.IGameData? data = null,
        IReadOnlyList<ScriptAction>? more = null) =>
        new(options, crew, checkpoints, storedParams, body, data, more) { Lang = ScriptLanguage.Js };

    /// <param name="more">Script actions besides the usual ones (a hidden tool a test needs, e.g. clear_temporary).</param>
    public Rig(ScriptOptions? options = null, Crew.ICrewPort? crew = null, IScriptCheckpointStore? checkpoints = null,
        Func<string, string?>? storedParams = null, string? body = null, MineClaw.Data.Versioning.IGameData? data = null,
        IReadOnlyList<ScriptAction>? more = null)
    {
        World.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -6; x <= 6; x++)
        for (int z = -6; z <= 6; z++)
            World.SetBlock(x, 64, z, Blocks.Stone, 0);
        World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        World.Player.SetHealth(20, 20, 5);
        var host = new ScriptHost
        {
            World = World,
            Username = "maid_chan",
            Data = data ?? MineClaw.Data.Versioning.Beta181GameData.Instance,
            Actions =
            [
                new ScriptAction("goto", ["to", "dist"]),
                new ScriptAction("mine", ["block", "count", "radius"]),
                new ScriptAction("chop_tree", ["at"]),
                new ScriptAction("deposit", ["at", "items"], Atomic: true),
                new ScriptAction("dig", ["at"], Atomic: true),
                new ScriptAction("escape_dig", ["at"], Atomic: true),
                new ScriptAction("clear_dig", ["at"], Atomic: true),
                new ScriptAction("craft", ["item", "count"], Atomic: true),
                new ScriptAction("smelt", ["item", "count", "fuel"], Atomic: true),
                new ScriptAction("withdraw", ["item", "count", "at"], Atomic: true),
                new ScriptAction("place", ["block", "at"], Atomic: true),
                new ScriptAction("place_turned", ["block", "at", "meta"], Atomic: true),
                new ScriptAction("use_block", ["at"], Atomic: true),
                new ScriptAction("eat", [], Atomic: true),
                new ScriptAction("harvest_generator", ["at", "count", "store", "stand"]),
                new ScriptAction("attack", ["target"]),
                new ScriptAction("equip", ["item", "mode", "from", "ench"], Atomic: true),
                new ScriptAction("face", ["at"]),
                .. more ?? [],
            ],
            Act = (name, args, ct) =>
            {
                Calls.Enqueue($"{name} {args.GetRawText()}");
                return On.TryGetValue(name, out var f) ? f(args, ct) : Task.FromResult(ActResult.Success($"{name} ok"));
            },
            Skills = () => ["move_items", "harvest_cane", "log", "give_items", "drop_items", "harvest_wheat", "excavate", "enter_portal", "strip_mine", "smelt_batch", "collect_furnace", "find_biome", "scout_biome", "hold_item", "fill_bucket", "empty_bucket", "move_lava"],
            Skill = (name, args, ct) =>
            {
                Calls.Enqueue($"skill:{name} {args.GetRawText()}");
                return On.TryGetValue("skill:" + name, out var f) ? f(args, ct) : Task.FromResult(ActResult.Success($"{name} ok"));
            },
            SkillIsAtomic = n => n == "move_items",
            SkillSchema = n => n == "move_items"
                ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"moves":{"type":"array"}}}""").RootElement.Clone()
                : n == "harvest_cane" ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"radius":{"type":"integer"}}}""").RootElement.Clone()
                : n == "excavate" ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"op":{"type":"string"},"box":{"type":"string"},"site":{"type":"string"},"rule":{"type":"string"},"at":{"type":"string"},"to":{"type":"integer"},"name":{"type":"string"},"size":{"type":"string"}}}""").RootElement.Clone()
                : n == "smelt_batch" ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"item":{"type":"string"},"count":{"type":"integer"},"wait":{"type":"boolean"}}}""").RootElement.Clone()
                : n == "strip_mine" ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"site":{"type":"string"},"length":{"type":"integer"}}}""").RootElement.Clone()
                : n is "find_biome" or "scout_biome" ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"biome":{"type":"string"},"dir":{"type":"integer"},"max":{"type":"integer"},"min_chunks":{"type":"integer"},"leg":{"type":"integer"}}}""").RootElement.Clone()
                : n == "harvest_wheat" ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"radius":{"type":"integer"},"replant":{"type":"boolean"},"part":{"type":"string"}}}""").RootElement.Clone()
                : n == "fill_bucket" ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"liquid":{"type":"string"},"pos":{"type":"string"}}}""").RootElement.Clone()
                : n == "empty_bucket" ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"pos":{"type":"string"}}}""").RootElement.Clone()
                : n == "move_lava" ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"to":{"type":"string"},"from":{"type":"string"}}}""").RootElement.Clone()
                : null,
            Wake = (_, reason) => Wakes.Enqueue(reason),
            Say = (t, _) => { Said.Enqueue(t); return Task.FromResult(ActResult.Success("said")); },
            Emote = (t, _) => { Emoted.Enqueue(t); return Task.FromResult(ActResult.Success("emoted")); },
            Ask = (p, q, _, _) => { Said.Enqueue($"{p}: {q}"); return Task.FromResult(AskReply); },
            WhereIs = (item, _) => Task.FromResult(Where(item)),
            NearestKnown = k => k == "home" ? new BlockPos(5, 65, 5) : null,
            OnlinePlayers = () => ["kiwi", "maid_chan", "robin"],
            ActionVeto = () => Veto,
            Log = l => Logs.Enqueue(l),
            Crew = crew,
            Checkpoints = checkpoints,
            StoredParams = storedParams,
            Body = body,
        };
        Host = host;
        C = new ScriptController(Host, options ?? Fast());
    }

    public static ScriptOptions Fast() => new() { ChatAutoResumeSeconds = 1, MaxPauseMinutes = 1 };

    /// <summary>"name {json}" with the JSON's keys sorted (Lua table order isn't defined).</summary>
    public List<string> Norm() => CallList.Select(Norm).ToList();

    public static string Norm(string call)
    {
        int i = call.IndexOf(' ');
        if (i < 0) return call;
        return call[..i] + " " + Sort(System.Text.Json.Nodes.JsonNode.Parse(call[(i + 1)..]))?.ToJsonString();
    }

    private static System.Text.Json.Nodes.JsonNode? Sort(System.Text.Json.Nodes.JsonNode? n) => n switch
    {
        System.Text.Json.Nodes.JsonObject o => new System.Text.Json.Nodes.JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value?.DeepClone())))),
        System.Text.Json.Nodes.JsonArray a => new System.Text.Json.Nodes.JsonArray(a.Select(x => Sort(x?.DeepClone())).ToArray()),
        _ => n?.DeepClone(),
    };

    public void Give(int slot, int id, int count, int dmg = 0) => World.Inventory.SetSlot(0, slot, new ItemStack((short)id, (byte)count, (short)dmg));

    public ScriptSession Start(string code, string name = "t", string? carry = null, string? resumeFrom = null, string? args = null,
        string? @params = null, string? role = null, string? resumeState = null)
    {
        var (s, err) = C.Start(new ScriptRequest
        {
            Name = name, Code = code, CarryJson = carry, ResumeFrom = resumeFrom, ArgsJson = args, Language = Lang, ParamsJson = @params,
            Role = role, ResumeState = resumeState,
        });
        Assert.True(s != null, err);
        return s!;
    }

    public async Task<ScriptSession> Run(string code, string name = "t", string? carry = null, string? resumeFrom = null, string? args = null,
        string? @params = null)
    {
        var s = Start(code, name, carry, resumeFrom, args, @params);
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        return s;
    }

    public static async Task Until(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    /// <summary>An action that blocks until cancelled (interruptible: goto/mine/wait) or released.</summary>
    public static Func<JsonElement, CancellationToken, Task<ActResult>> Blocking(TaskCompletionSource release, Action? started = null) =>
        async (_, ct) =>
        {
            started?.Invoke();
            try { await release.Task.WaitAsync(ct); return ActResult.Success("arrived"); }
            catch (OperationCanceledException) { return ActResult.Fail("cancelled"); }
        };

    public void Dispose() => C.Dispose();
}
