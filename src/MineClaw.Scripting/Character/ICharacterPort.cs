using System.Text.Json.Nodes;
using MineClaw.World;

namespace MineClaw.Scripting.Character;

/// <summary>
/// The persona's side of a script (the <c>character</c> SDK module): body language, prefetched in-character lines, read-only
/// self-profile and memory, recent chat. Read-mostly: the only writes are a head turn, a brainstem gesture (social priority:
/// never during a safety reflex, a fight, near a hostile or an edge) and a groomed memory note. The Bot layer wires it to the
/// brainstem, the line bank and the memory index; tests and the dry run leave it null (every call is then a harmless no-op).
/// </summary>
public interface ICharacterPort
{
    /// <summary>Turns the head (look overlay, no action lock) toward a player's eyes or a block centre. False = not in view / refused.</summary>
    bool Look(string? player, BlockPos? pos);

    /// <summary>A brainstem gesture: nod|shake|wave|greet|bow|bob|hop|excite|ack|look. Returns what played, or why not.</summary>
    ActResult Gesture(string name, string? target);

    /// <summary>A fresh prefetched in-character line for a moment (greet|thanks|status|hurt|come|follow|stop|bye|sorry|...), not
    /// said recently; null when none is ready (the script says its own fallback or stays quiet).</summary>
    string? Line(string kind, string? player);

    /// <summary>Marks a line as said (phrase memory: no repeats within the window).</summary>
    void Said(string line);

    /// <summary>{mood, energy, note} from the self-profile (hours-scale aggregates), or null.</summary>
    JsonNode? Mood();

    /// <summary>{player, affinity, trust, playful, nick, trusted, regular} for a player, or null when unknown.</summary>
    JsonNode? Relation(string player);

    /// <summary>Up to k memory hits [{id, text}] for a query (local retrieval, zero tokens).</summary>
    JsonNode? Recall(string query, int k);

    /// <summary>A short note into memory (groomed like every memory write).</summary>
    ActResult Remember(string text);

    /// <summary>Recent chat lines [{player, text, intent, ago}] (a player's only, or everyone's), newest last.</summary>
    JsonNode Heard(string? player, double seconds);

    /// <summary>The intent class of a chat line (come|follow|stop|give|status|thanks|praise|insult|greeting|farewell|help|question|command|banter).</summary>
    string Intent(string text);
}
