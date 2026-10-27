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
    public ScriptController C { get; }

    public Rig(ScriptOptions? options = null)
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
            Actions =
            [
                new ScriptAction("goto", ["to", "dist"]),
                new ScriptAction("mine", ["block", "count", "radius"]),
                new ScriptAction("chop_tree", ["at"]),
                new ScriptAction("deposit", ["at", "items"], Atomic: true),
                new ScriptAction("dig", ["at"], Atomic: true),
                new ScriptAction("craft", ["item", "count"], Atomic: true),
                new ScriptAction("smelt", ["item", "count", "fuel"], Atomic: true),
                new ScriptAction("withdraw", ["item", "count", "at"], Atomic: true),
                new ScriptAction("place", ["block", "at"], Atomic: true),
                new ScriptAction("use_block", ["at"], Atomic: true),
                new ScriptAction("eat", [], Atomic: true),
            ],
            Act = (name, args, ct) =>
            {
                Calls.Enqueue($"{name} {args.GetRawText()}");
                return On.TryGetValue(name, out var f) ? f(args, ct) : Task.FromResult(ActResult.Success($"{name} ok"));
            },
            Skills = () => ["move_items", "harvest_cane", "log"],
            Skill = (name, args, ct) =>
            {
                Calls.Enqueue($"skill:{name} {args.GetRawText()}");
                return On.TryGetValue("skill:" + name, out var f) ? f(args, ct) : Task.FromResult(ActResult.Success($"{name} ok"));
            },
            SkillIsAtomic = n => n == "move_items",
            SkillSchema = n => n == "move_items"
                ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"moves":{"type":"array"}}}""").RootElement.Clone()
                : n == "harvest_cane" ? (JsonElement?)JsonDocument.Parse("""{"type":"object","properties":{"radius":{"type":"integer"}}}""").RootElement.Clone() : null,
            Wake = (_, reason) => Wakes.Enqueue(reason),
            Say = (t, _) => { Said.Enqueue(t); return Task.FromResult(ActResult.Success("said")); },
            Emote = (t, _) => { Emoted.Enqueue(t); return Task.FromResult(ActResult.Success("emoted")); },
            Ask = (p, q, _, _) => { Said.Enqueue($"{p}: {q}"); return Task.FromResult(AskReply); },
            WhereIs = (item, _) => Task.FromResult<string?>(item == "iron" ? "iron: chest 3,64,2 (x12)" : null),
            NearestKnown = k => k == "home" ? new BlockPos(5, 65, 5) : null,
            OnlinePlayers = () => ["kiwi", "maid_chan", "robin"],
            ActionVeto = () => Veto,
            Log = l => Logs.Enqueue(l),
        };
        C = new ScriptController(host, options ?? Fast());
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

    public ScriptSession Start(string code, string name = "t", string? carry = null, string? resumeFrom = null, string? args = null)
    {
        var (s, err) = C.Start(new ScriptRequest { Name = name, Code = code, CarryJson = carry, ResumeFrom = resumeFrom, ArgsJson = args });
        Assert.True(s != null, err);
        return s!;
    }

    public async Task<ScriptSession> Run(string code, string name = "t", string? carry = null, string? resumeFrom = null, string? args = null)
    {
        var s = Start(code, name, carry, resumeFrom, args);
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
