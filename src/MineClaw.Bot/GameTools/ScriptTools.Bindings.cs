using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Tools;
using MineClaw.Scripting.Events;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Standing bindings (docs/design/reactive-character.md): <c>script{op:bind, name, goal, params:{on, ...filter, for, minutes}}</c>
/// has the Opus author write a short script that runs instantly whenever the event fires (the <see cref="Brainstem.BindingRunner"/>
/// starts it with the event as its args, no model in the loop); <c>op:bindings</c> lists them, <c>op:unbind name</c> switches one
/// off. Only a trusted player's binding may act on the world (dig, place, attack, items); everyone else's (and the brain's own
/// reactions) may only move, look, gesture and talk.
/// </summary>
public sealed partial class ScriptBridge
{
    /// <summary>Keys of op=bind's params that configure the binding itself (the rest is the event filter).</summary>
    private static readonly HashSet<string> BindKeys = new(StringComparer.OrdinalIgnoreCase) { "on", "for", "minutes", "kind" };

    /// <summary>Chat intents a binding may use as its trigger (= player.say with that intent).</summary>
    public static readonly IReadOnlySet<string> Intents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "come", "follow", "stop", "give", "status", "thanks", "praise", "insult", "greeting", "farewell", "help", "question", "command", "banter",
    };

    private async Task<ToolResult> BindingOpAsync(string op, JsonElement input, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (op == "bindings") return ToolResult.Success(Library.List());
        var rawName = ToolInput.Str(input, "name");
        if (string.IsNullOrWhiteSpace(rawName)) return ToolResult.Error(op + " needs name");
        var name = ScriptLibrary.ParseRef(CleanRef(rawName)).Name;
        var speaker = TurnContext.Current?.Speaker;
        if (op == "unbind") return Unbind(name, speaker);
        return await BindAsync(name, speaker, input, now, ct).ConfigureAwait(false);
    }

    private ToolResult Unbind(string name, string? speaker)
    {
        var b = Library.Get(name)?.Script?.Binding;
        if (b == null) return ToolResult.Error($"no binding '{name}' ({Library.List()})");
        // A player may remove their own binding; anyone else's needs a trusted player.
        if (speaker != null && b.CreatedBy != null && !speaker.Equals(b.CreatedBy, StringComparison.OrdinalIgnoreCase) && !IsTrusted(speaker))
            return ToolResult.Error($"{name} is {b.CreatedBy}'s binding: only they or a trusted player can remove it");
        Library.Unbind(name, "removed" + (speaker != null ? " by " + speaker : ""));
        return ToolResult.Success($"ok: {name} unbound (the script stays saved)");
    }

    /// <summary>The binding's own settings from op=bind's params: the event filter, its kind, who it's for and how long.</summary>
    private readonly record struct BindShape(JsonObject Filter, string Kind, string? For, double? Minutes);

    private async Task<ToolResult> BindAsync(string name, string? speaker, JsonElement input, DateTimeOffset now, CancellationToken ct)
    {
        var (spec, ev, triggerError) = BindTrigger(input);
        if (triggerError != null) return triggerError;
        var shape = ShapeOf(spec!, speaker);
        // The script: authored apart (goal) with the event as its args, or an existing saved one.
        string? note = null;
        if (ToolInput.Str(input, "goal") is { Length: > 0 } goal)
        {
            var w = await AuthorBindingAsync(name, goal, ev!, speaker, ct).ConfigureAwait(false);
            if (!w.Ok) return ToolResult.Error(w.Summary);
            note = w.Summary;
        }
        var code = Library.Get(name)?.Script?.Code;
        if (string.IsNullOrWhiteSpace(code)) return ToolResult.Error($"no saved script '{name}': send a goal so the author writes it");

        bool world = ScriptBindings.ActsOnWorld(code);
        bool trusted = speaker != null && IsTrusted(speaker);
        if (world && (!trusted || shape.Kind == "reaction")) return RefuseWorldBinding(name, speaker, shape.Kind);
        var binding = NewBinding(ev!, shape, world, speaker, trusted, now);
        Library.Bind(name, binding);
        Bindings?.Invalidate();
        return ToolResult.Success((note != null ? "ok: " + note + " | " : "ok: ") +
            $"bound {binding.Describe(name, now)}: runs instantly when it happens, no thinking; op=unbind name removes it");
    }

    /// <summary>params.on: a game event or a chat intent (else the error saying which exist).</summary>
    private static (JsonObject? Spec, string? Event, ToolResult? Error) BindTrigger(JsonElement input)
    {
        var spec = ParamsOf(input) is { } pj ? JsonNode.Parse(pj) as JsonObject : null;
        var on = spec?["on"]?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(on))
            return (spec, null, ToolResult.Error("bind needs params.on: a game event (" + string.Join(" ", GameEventCatalog.Names) + ") or a chat intent (thanks, come, greeting...)"));
        var ev = GameEventCatalog.Canonical(on) ?? (Intents.Contains(on) ? on.ToLowerInvariant() : null);
        if (ev == null)
            return (spec, null, ToolResult.Error($"unknown trigger '{on}': events {string.Join(" ", GameEventCatalog.Names)}; or a chat intent ({string.Join(",", Intents)})"));
        return (spec, ev, null);
    }

    private static BindShape ShapeOf(JsonObject spec, string? speaker)
    {
        // kind: reaction|binding is the binding's own kind; any other value filters the event (a gesture's kind:'nod', a mob kind).
        var kindArg = spec["kind"]?.ToString();
        bool bindKind = kindArg is null or "reaction" or "binding";
        var filter = BindFilter(spec, bindKind);
        var kind = (bindKind && kindArg is "reaction") || speaker == null ? "reaction" : "binding";
        var forPlayer = spec["for"]?.ToString();
        double? minutes = spec["minutes"] is JsonValue mv && mv.TryGetValue<double>(out var m) ? m : null;
        return new BindShape(filter, kind, forPlayer, minutes);
    }

    /// <summary>Every param that isn't the binding's own setting filters the event (a non-binding kind too).</summary>
    private static JsonObject BindFilter(JsonObject spec, bool bindKind)
    {
        var filter = new JsonObject();
        foreach (var (k, v) in spec)
            if (!BindKeys.Contains(k) || (k.Equals("kind", StringComparison.OrdinalIgnoreCase) && !bindKind)) filter[k] = v?.DeepClone();
        return filter;
    }

    private Task<AuthoredScript> AuthorBindingAsync(string name, string goal, string ev, string? speaker, CancellationToken ct)
    {
        var brief = goal.Trim() + $"\n(This is a standing binding: it runs once each time {ev} fires, with the event in args " +
            $"({Fields(ev)}). Keep it short: no loops, no waiting for more events, at most one say; end when done.)";
        var authorInput = JsonSerializer.SerializeToElement(new { name, goal = brief, args = Example(ev, speaker) });
        return AuthorScriptAsync(name, brief, authorInput, ct);
    }

    private ToolResult RefuseWorldBinding(string name, string? speaker, string kind)
    {
        Library.Unbind(name, "refused: acts on the world");
        return ToolResult.Error(speaker == null || kind == "reaction"
            ? $"{name} acts on the world (dig/place/items): your own reactions may only move, look, gesture and talk"
            : $"{name} acts on the world (dig/place/items): only a trusted player can set that up; {speaker} can have harmless ones (follow, gestures, lines)");
    }

    private static ScriptBinding NewBinding(string ev, BindShape shape, bool world, string? speaker, bool trusted, DateTimeOffset now) => new()
    {
        On = ev, Filter = shape.Filter.Count > 0 ? shape.Filter.ToJsonString() : null, For = shape.For, Kind = shape.Kind, World = world,
        CreatedBy = speaker, CreatorTrusted = trusted, CreatedAt = now,
        Expires = shape.Minutes is { } mm && mm > 0 ? now + TimeSpan.FromMinutes(Math.Min(mm, 7 * 24 * 60)) : null,
    };

    private bool IsTrusted(string player) =>
        _rt.Options.Self.TrustedPlayers.Any(t => t.Equals(player, StringComparison.OrdinalIgnoreCase));

    /// <summary>The binding runner (set by the host), told when a binding changes.</summary>
    public Brainstem.BindingRunner? Bindings { get; set; }

    private static string Fields(string ev) =>
        GameEventCatalog.All.FirstOrDefault(e => e.Name == ev)?.Fields is { } f ? "args." + f.Replace("?", "").Replace(",", ", args.") : "args.player, args.text, args.intent";

    /// <summary>An example payload for the author's brief (the dry run and the author see real field shapes).</summary>
    internal static JsonObject Example(string ev, string? player)
    {
        var o = new JsonObject();
        var fields = GameEventCatalog.All.FirstOrDefault(e => e.Name == ev)?.Fields ?? "player,text,intent";
        foreach (var raw in fields.Split(',', StringSplitOptions.TrimEntries))
        {
            var f = raw.TrimEnd('?');
            o[f] = ExampleValues.TryGetValue(f, out var value) ? value(ev, player) : "x";
        }
        return o;
    }

    /// <summary>The example value of each event field (a fresh node per call: a node has one parent).</summary>
    private static readonly Dictionary<string, Func<string, string?, JsonNode?>> ExampleValues = new(StringComparer.Ordinal)
    {
        ["player"] = (_, player) => player ?? "PoofImaFox",
        ["by"] = (_, player) => player ?? "PoofImaFox",
        ["pos"] = (_, _) => new JsonObject { ["x"] = 3, ["y"] = 64, ["z"] = -2 },
        ["block"] = (_, _) => "log",
        ["from"] = (_, _) => "log",
        ["to"] = (_, _) => "air",
        ["held"] = (_, _) => "stone_axe",
        ["item"] = (_, _) => "cobblestone",
        ["count"] = (_, _) => 1,
        ["cause"] = (_, _) => "melee",
        ["text"] = (_, _) => "thanks maid",
        ["intent"] = (ev, _) => GameEventCatalog.Canonical(ev) == null ? ev : "banter",
        ["d"] = (_, _) => 3,
        ["mob"] = (_, _) => "zombie",
        ["hp"] = (_, _) => 12,
        ["target"] = (ev, _) => ev == "player.gesture" ? new JsonObject { ["x"] = 5, ["y"] = 64, ["z"] = -6 } : "me",
        ["kind"] = (_, _) => "nod",
        ["strength"] = (_, _) => 0.8,
        ["at"] = (_, _) => "me",
        ["answer"] = (_, _) => "yes",
        ["slot"] = (_, _) => "held",
        ["time"] = (_, _) => 12500,
        ["rain"] = (_, _) => true,
    };
}
