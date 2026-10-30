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
        if (op == "unbind")
        {
            var b = Library.Get(name)?.Script?.Binding;
            if (b == null) return ToolResult.Error($"no binding '{name}' ({Library.List()})");
            // A player may remove their own binding; anyone else's needs a trusted player.
            if (speaker != null && b.CreatedBy != null && !speaker.Equals(b.CreatedBy, StringComparison.OrdinalIgnoreCase) && !IsTrusted(speaker))
                return ToolResult.Error($"{name} is {b.CreatedBy}'s binding: only they or a trusted player can remove it");
            Library.Unbind(name, "removed" + (speaker != null ? " by " + speaker : ""));
            return ToolResult.Success($"ok: {name} unbound (the script stays saved)");
        }

        var spec = ParamsOf(input) is { } pj ? JsonNode.Parse(pj) as JsonObject : null;
        var on = spec?["on"]?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(on)) return ToolResult.Error("bind needs params.on: a game event (" + string.Join(" ", GameEventCatalog.Names) + ") or a chat intent (thanks, come, greeting...)");
        var ev = GameEventCatalog.Canonical(on) ?? (Intents.Contains(on) ? on.ToLowerInvariant() : null);
        if (ev == null) return ToolResult.Error($"unknown trigger '{on}': events {string.Join(" ", GameEventCatalog.Names)}; or a chat intent ({string.Join(",", Intents)})");
        var filter = new JsonObject();
        // kind: reaction|binding is the binding's own kind; any other value filters the event (a gesture's kind:'nod', a mob kind).
        var kindArg = spec!["kind"]?.ToString();
        bool bindKind = kindArg is null or "reaction" or "binding";
        foreach (var (k, v) in spec!) if (!BindKeys.Contains(k) || (k.Equals("kind", StringComparison.OrdinalIgnoreCase) && !bindKind)) filter[k] = v?.DeepClone();
        var kind = (bindKind && kindArg is "reaction") || speaker == null ? "reaction" : "binding";
        var forPlayer = spec["for"]?.ToString();
        double? minutes = spec["minutes"] is JsonValue mv && mv.TryGetValue<double>(out var m) ? m : null;

        // The script: authored apart (goal) with the event as its args, or an existing saved one.
        string? note = null;
        if (ToolInput.Str(input, "goal") is { Length: > 0 } goal)
        {
            var brief = goal.Trim() + $"\n(This is a standing binding: it runs once each time {ev} fires, with the event in args " +
                $"({Fields(ev)}). Keep it short: no loops, no waiting for more events, at most one say; end when done.)";
            var authorInput = JsonSerializer.SerializeToElement(new { name, goal = brief, args = Example(ev, speaker) });
            var w = await AuthorScriptAsync(name, brief, authorInput, ct).ConfigureAwait(false);
            if (!w.Ok) return ToolResult.Error(w.Summary);
            note = w.Summary;
        }
        var code = Library.Get(name)?.Script?.Code;
        if (string.IsNullOrWhiteSpace(code)) return ToolResult.Error($"no saved script '{name}': send a goal so the author writes it");

        bool world = ScriptBindings.ActsOnWorld(code);
        bool trusted = speaker != null && IsTrusted(speaker);
        if (world && (!trusted || kind == "reaction"))
        {
            Library.Unbind(name, "refused: acts on the world");
            return ToolResult.Error(speaker == null || kind == "reaction"
                ? $"{name} acts on the world (dig/place/items): your own reactions may only move, look, gesture and talk"
                : $"{name} acts on the world (dig/place/items): only a trusted player can set that up; {speaker} can have harmless ones (follow, gestures, lines)");
        }
        var binding = new ScriptBinding
        {
            On = ev, Filter = filter.Count > 0 ? filter.ToJsonString() : null, For = forPlayer, Kind = kind, World = world,
            CreatedBy = speaker, CreatorTrusted = trusted, CreatedAt = now,
            Expires = minutes is { } mm && mm > 0 ? now + TimeSpan.FromMinutes(Math.Min(mm, 7 * 24 * 60)) : null,
        };
        Library.Bind(name, binding);
        Bindings?.Invalidate();
        return ToolResult.Success((note != null ? "ok: " + note + " | " : "ok: ") +
            $"bound {binding.Describe(name, now)}: runs instantly when it happens, no thinking; op=unbind name removes it");
    }

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
            o[f] = f switch
            {
                "player" or "by" => player ?? "PoofImaFox",
                "pos" => new JsonObject { ["x"] = 3, ["y"] = 64, ["z"] = -2 },
                "block" or "from" => "log",
                "to" => "air",
                "held" => "stone_axe",
                "item" => "cobblestone",
                "count" => 1,
                "cause" => "melee",
                "text" => "thanks maid",
                "intent" => GameEventCatalog.Canonical(ev) == null ? ev : "banter",
                "d" => 3,
                "mob" => "zombie",
                "hp" => 12,
                "target" => ev == "player.gesture" ? new JsonObject { ["x"] = 5, ["y"] = 64, ["z"] = -6 } : "me",
                "kind" => "nod",
                "strength" => 0.8,
                "at" => "me",
                "answer" => "yes",
                "slot" => "held",
                "time" => 12500,
                "rain" => true,
                _ => "x",
            };
        }
        return o;
    }
}
