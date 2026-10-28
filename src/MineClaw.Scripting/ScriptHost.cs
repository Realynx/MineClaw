using System.Text.Json;
using MineClaw.World;

namespace MineClaw.Scripting;

/// <summary>Outcome of one host action as the script sees it: <c>{ok, msg, count}</c>. Game failures are results, never throws.</summary>
public readonly record struct ActResult(bool Ok, string Msg)
{
    public static ActResult Success(string msg) => new(true, msg);
    public static ActResult Fail(string msg) => new(false, msg);
}

/// <summary>
/// A game tool exposed to scripts as a function. <paramref name="Params"/> is the positional order
/// (<c>mine('cobblestone', 16)</c> → <c>{block, count}</c>). <paramref name="Atomic"/> actions (click sequences, digs) are
/// never cut short by a pause, cancel or interrupt: they finish and the script stops at the next safe point.
/// </summary>
public sealed record ScriptAction(string Name, IReadOnlyList<string> Params, bool Atomic = false);

/// <summary>Something that happened while a script runs, already classified by the host (see <see cref="ScriptController.Deliver"/>).</summary>
public enum ScriptEventKind
{
    /// <summary>Danger or any event at/above the interrupt priority: pauses (aborting an interruptible action) unless an on('danger') handler takes it.</summary>
    Danger,
    /// <summary>Chat addressed to the bot: feeds wait_for/on('chat'); otherwise pauses (auto-resume).</summary>
    Chat,
    /// <summary>A trusted player said "stop": hard pause, no auto-resume.</summary>
    Stop,
    /// <summary>A player joined (wait_for('join')).</summary>
    Join,
    /// <summary>An edge-triggered condition the script listens for (hungry, invFull, toolBroke, night, day, a timer, when()).</summary>
    Signal,
}

/// <summary>
/// <paramref name="Topic"/> refines the kind for handlers and machines: danger → "lowhp" | "void" | "died" | "danger";
/// signals → their name ("hungry", "invFull", "toolBroke:pickaxe", "night", "after:5000"...). Null = the kind's own name.
/// </summary>
public sealed record ScriptEvent(ScriptEventKind Kind, string Text, string? Player = null, string? Topic = null)
{
    /// <summary>"danger", "lowhp", "chat", "hungry", ...</summary>
    public string Name => Topic ?? Kind.ToString().ToLowerInvariant();

    /// <summary>Whether a listener for <paramref name="topic"/> takes this event: the exact topic, its family ("danger" takes every
    /// danger, "toolBroke" every "toolBroke:x"), or "chat:&lt;text&gt;" (addressed chat containing the text).</summary>
    public bool Matches(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return false;
        topic = topic.Trim();
        if (string.Equals(topic, Name, StringComparison.OrdinalIgnoreCase)) return true;
        string kind = Kind.ToString().ToLowerInvariant();
        if (string.Equals(topic, kind, StringComparison.OrdinalIgnoreCase) && Kind != ScriptEventKind.Signal) return true;
        int c = topic.IndexOf(':');
        if (c > 0)
        {
            var head = topic[..c];
            var tail = topic[(c + 1)..].Trim();
            if (Kind == ScriptEventKind.Chat && head.Equals("chat", StringComparison.OrdinalIgnoreCase))
                return tail.Length == 0 || Text.Contains(tail, StringComparison.OrdinalIgnoreCase);
            return false;
        }
        // "toolBroke" takes "toolBroke:pickaxe".
        return Name.StartsWith(topic + ":", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A lane's checkpoint snapshots survive restarts here (script name + lane body → label → snapshot JSON).</summary>
public interface IScriptCheckpointStore
{
    void Save(string script, string? lane, string label, string snapshot);
    string? Load(string script, string? lane, string label);
}

/// <summary>
/// The port a script talks to. The engine never touches the network, the LLM or server commands directly: every capability
/// is one of these delegates, wired by the Bot layer to the same gated tools the models use (EdgeGuard, survival gating,
/// protection, claims, spawn protection all still apply). Tests plug in fakes.
/// </summary>
public sealed class ScriptHost
{
    public required WorldState World { get; init; }
    public string Username { get; init; } = "bot";

    /// <summary>Game tools available as script functions.</summary>
    public IReadOnlyList<ScriptAction> Actions { get; init; } = [];
    public Func<string, JsonElement, CancellationToken, Task<ActResult>> Act { get; init; } =
        (n, _, _) => Task.FromResult(ActResult.Fail($"no action {n}"));

    /// <summary>Catalog skill names (each also becomes a function alias when the name is free).</summary>
    public Func<IReadOnlyCollection<string>> Skills { get; init; } = () => [];
    public Func<string, JsonElement, CancellationToken, Task<ActResult>> Skill { get; init; } =
        (n, _, _) => Task.FromResult(ActResult.Fail($"no skill '{n}'"));
    /// <summary>A skill's args schema (positional order + array params), or null.</summary>
    public Func<string, JsonElement?> SkillSchema { get; init; } = _ => null;
    /// <summary>Skills that must not be cut short (inventory click sequences).</summary>
    public Func<string, bool> SkillIsAtomic { get; init; } = _ => false;

    /// <summary>Persona chat through the normal sender (command gate included).</summary>
    public Func<string, CancellationToken, Task<ActResult>> Say { get; init; } = (_, _) => Task.FromResult(ActResult.Success("said"));
    /// <summary>True when this bot's chat is muted (a hive clone speaks only when addressed): say/emote then don't fail the
    /// script; the text goes back to the brain in the script's result as report output.</summary>
    public Func<string, bool>? Muted { get; init; }
    /// <summary>/me action text (without "/me ").</summary>
    public Func<string, CancellationToken, Task<ActResult>> Emote { get; init; } = (_, _) => Task.FromResult(ActResult.Success("emoted"));
    /// <summary>Posts a question and waits for that player's reply (null player = anyone); null on timeout.</summary>
    public Func<string?, string, TimeSpan, CancellationToken, Task<string?>> Ask { get; init; } = (_, _, _, _) => Task.FromResult<string?>(null);
    /// <summary>Known chests/places holding an item (terse), or null.</summary>
    public Func<string, CancellationToken, Task<string?>> WhereIs { get; init; } = (_, _) => Task.FromResult<string?>(null);
    /// <summary>Nearest known thing of a kind from memory (chest, furnace, waypoint...) when no block of that name is in view.</summary>
    public Func<string, BlockPos?> NearestKnown { get; init; } = _ => null;
    public Func<BlockPos, int, bool> IsProtected { get; init; } = (_, _) => false;
    public Func<IEnumerable<string>>? OnlinePlayers { get; init; }
    /// <summary><c>wake"reason"</c>: the script asks for the (sleeping) brain; the host posts a waking event with the state.</summary>
    public Action<ScriptSession, string> Wake { get; init; } = (_, _) => { };
    /// <summary>Non-null = actions are refused right now (budget low...); the script stops with this reason.</summary>
    public Func<string?> ActionVeto { get; init; } = () => null;
    public Action<string>? Log { get; init; }

    /// <summary>This lane's body ("67"; null = the main body).</summary>
    public string? Body { get; set; }
    /// <summary>Several bodies (in-process lanes or hive clones): <c>body()</c>, <c>parallel()</c>, <c>spawn()</c>, <c>crew.*</c>. Null = one body.</summary>
    public Crew.ICrewPort? Crew { get; set; }
    /// <summary>Durable checkpoint snapshots (cp carry, machine state), so a restart resumes where it was. Null = memory only.</summary>
    public IScriptCheckpointStore? Checkpoints { get; init; }
    /// <summary>The saved tunables of a script (merged under the run's own params by <c>params({...})</c>), or null.</summary>
    public Func<string, string?>? StoredParams { get; init; }
}
