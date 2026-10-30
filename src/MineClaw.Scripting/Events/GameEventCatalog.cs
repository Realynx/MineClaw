namespace MineClaw.Scripting.Events;

/// <summary>
/// One typed game event a script can bind to (<c>on(name, filter, fn)</c>, <c>react(...)</c>, a machine's <c>on</c>, a standing
/// binding): its payload fields and where the client observes it on each protocol version. <paramref name="Noisy"/> events
/// (frequent: swings, block changes, proximity) never cut the running action by default; the handler runs between actions.
/// </summary>
public sealed record GameEventSpec(string Name, string Fields, string B181, string R1122, bool Noisy = false);

/// <summary>
/// The broad, typed signal set delivered instantly to scripts and bindings (C#, no model in the loop). Every event comes from
/// what the client actually observes; <see cref="GameEventSpec.B181"/> / <see cref="GameEventSpec.R1122"/> say how. The payload is
/// the event object's fields (<c>e.player</c>, <c>e.pos</c> {x,y,z}, <c>e.block</c>, <c>e.held</c>, ...); filters are
/// <see cref="EventFilter"/>.
/// </summary>
public static class GameEventCatalog
{
    public static readonly IReadOnlyList<GameEventSpec> All =
    [
        new("player.punchBlock", "player,pos,block,held",
            "no dig packet for other players: arm swing (0x12 anim 1) + a raycast along their look (0x20/0x21 yaw/pitch) to the first solid block in 4.5, use-items skipped, same block debounced 1.5 s",
            "Block Break Animation (0x08) when forwarded, else the same swing + raycast"),
        new("player.swing", "player,held", "0x12 animation 1", "0x06 animation 0 (main hand)", Noisy: true),
        new("player.hurt", "player,by?,cause", "0x26 entity status 2 (or 0x12 anim 2); by = the nearest player/mob within 4 facing them, cause melee|mob|fall|unknown", "0x1B entity status 2 + the same heuristic"),
        new("player.died", "player,cause", "0x26 entity status 3", "0x1B entity status 3"),
        new("player.pickup", "player,item,count", "0x16 collect item (collector = the player)", "0x4B collect item"),
        new("player.toss", "player,item,count,pos", "an item entity (0x15) spawning at a player's eye height / ahead along their look", "0x00 spawn object (item) + metadata, same rule"),
        new("player.equip", "player,slot,item", "0x05 entity equipment (slot 0 = held)", "0x3F entity equipment"),
        new("player.sneak", "player", "0x12 animation 104 or metadata flag 0x02 on", "0x3C metadata flag 0x02 on"),
        new("player.unsneak", "player", "0x12 animation 105 or metadata flag 0x02 off", "0x3C metadata flag 0x02 off"),
        new("player.near", "player,d", "entity positions: came within dist (default 6) of me", "same", Noisy: true),
        new("player.leave", "player,d", "entity positions: went beyond dist (default 6), or despawned", "same", Noisy: true),
        new("player.join", "player", "player list (0xC9) / chat join line", "0x2E player list item add"),
        new("player.quit", "player", "player list (0xC9) remove / chat leave line", "0x2E player list item remove"),
        new("player.say", "player,text,intent", "0x03 chat <name> text, intent from ChatIntent (come|follow|stop|give|status|thanks|praise|insult|greeting|farewell|help|question|command|banter)", "0x0F chat (JSON → text)"),
        new("player.lookAt", "player,target,pos?", "their yaw/pitch: target 'me' (ray within 0.6 of my head, 16 blocks, held 1 s) or 'block' (pos)", "same", Noisy: true),
        new("player.gesture", "player,kind,strength,at?,answer?,target?,block?,pos",
            "kind nod|shake|bob|wave|jump|spin|stare|point (Perception/GestureDetector): look packets 0x20/0x21 (sent only at >= 8/256 turn, every 2 ticks) for nod/shake/spin/stare/point, moves for jumps, metadata flag 0x02 / anim 104-105 for bob, anim 1 swings into empty air for wave/point; at = the body they aimed it at ('me' or a bot), answer yes|no for nod|shake, target {x,y,z} + block|entity for point",
            "same from 0x28/0x27 look (>= 1/256 turn), 0x36 Entity Head Look for the head yaw, 0x3C metadata flag 0x02, 0x06 animation 0"),
        new("block.changed", "pos,from,to,by?", "0x35 block change / 0x34 multi-block; by = the player whose punch/use raycast hit it in the last 2 s", "0x0B block change / 0x10 multi", Noisy: true),
        new("item.dropped", "pos,item,count", "0x15 pickup spawn (item entity)", "0x00 spawn object type 2 + metadata"),
        new("mob.near", "mob,d,pos", "a hostile mob entity came within dist (default 8)", "same", Noisy: true),
        new("me.hurt", "by?,hp", "0x08 update health drop; by = the nearest attacker in 4", "0x41 update health"),
        new("me.lowHp", "hp", "0x08 update health crossing below 8 (once until healed above 12)", "0x41 update health"),
        new("time.dusk", "time", "0x04 time update crossing 12500", "0x47 time update"),
        new("time.dawn", "time", "0x04 time update crossing 23500 (or below 500)", "0x47 time update"),
        new("weather", "rain", "0x46 new state reason 1 (rain on) / 2 (off)", "0x1E change game state 1/2"),
    ];

    private static readonly Dictionary<string, GameEventSpec> ByName = All.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The canonical spelling of an event name ("player.punchblock" → "player.punchBlock"), or null.</summary>
    public static string? Canonical(string? name) => name != null && ByName.TryGetValue(name.Trim(), out var s) ? s.Name : null;

    /// <summary>A topic that names a game event or a family of them ("player" takes every player.* event).</summary>
    public static bool IsEvent(string? topic)
    {
        var t = (topic ?? "").Trim();
        if (t.Length == 0) return false;
        if (ByName.ContainsKey(t)) return true;
        return t is "player" or "block" or "item" or "mob" or "me" or "time";
    }

    public static bool IsNoisy(string topic) => ByName.TryGetValue(topic.Trim(), out var s) && s.Noisy;

    /// <summary>Every event name (lint's known topics).</summary>
    public static IEnumerable<string> Names => All.Select(e => e.Name);

    /// <summary>"player.punchBlock{player,pos,block,held} player.swing{player,held} ..." (the SDK doc line, the design doc).</summary>
    public static string Compact() => string.Join(" ", All.Select(e => $"{e.Name}{{{e.Fields}}}"));
}
