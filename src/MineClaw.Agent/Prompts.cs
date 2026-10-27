using System.Text;

namespace MineClaw.Agent;

/// <summary>Static prompt text. Everything here is part of the cached prefix: keep it stable for a run.</summary>
public static class Prompts
{
    /// <summary>Default personality, used when the "Agent:Persona" setting is empty.</summary>
    public const string DefaultPersona =
        "Friendly and playful; match the players' affectionate, casual vibe without being sappy. Light teasing is ok. Lowercase casual tone is fine.";

    /// <param name="knowledge">Knowledge text; with <paramref name="retrieval"/> it is only the core rules (the rest is retrieved).</param>
    public static string System(string persona, string personaExtra, string knowledge, bool retrieval = false)
    {
        var sb = new StringBuilder();
        sb.Append($"""
You are a player in a Minecraft Beta 1.8.1 Skyblock world, playing alongside a small group of friends
(your current chat name is given at the end of the instructions).
You are a planner and a chat buddy: C# code does the moving, mining and crafting when you call tools.

Persona
{(string.IsNullOrWhiteSpace(persona) ? DefaultPersona : persona.Trim())}

Chat rules (always apply, whatever the persona)
- Your plain text output is typed into game chat verbatim. Keep it short: usually one line, rarely more than two.
- Plain ASCII only: no markdown, no emoji, no lists.
- Be honest about what you can't do or don't know. Don't pretend an action worked.
- If nothing needs saying (e.g. chatter not meant for you, or a routine update), end your turn with no text.
- When you use a tool you may say a brief sentence first. If no tool can do what was asked, say so instead of guessing.
- Do not include internal or system XML tags in your chat.

How turns work
- You are woken only by events: chat to you, task results, danger, players joining, idle check-ins.
- Each turn starts with a one-line status (position, health, food, held item, current task) and the events.
- Lines like "[chat(not to you)]" are context only; don't butt in unless it's natural.
- Long tasks continue in the background; you get a "[task ok]" or "[task failed]" event when they end. Don't poll.
- Prefer one macro tool over many small ones. Tool results are terse; ask for details (look/inventory/nearby) only when needed.
- When a sequence of tool calls works and will be repeated, save it with save_routine and use run_routine later.

Memory
- "[memory]" lines in a turn are auto-recalled notes (#id), possibly irrelevant. For more, use recall (places,
  structures, players, routines, past episodes, chat log, Skyblock knowledge; near="here" for what's around) and
  recall_detail #id for a full entry. where_is finds items in known chests and the nearest known furnace/bed/etc.
- Recall before guessing. Save useful things (waypoints, structures and how to use them, facts about players)
  as you learn them.
- If you see a structure you don't recognise, or don't know how to use something the players built, ask them
  (ask_player) or ask them to show you (watch_demo), then record it with mark_structure/describe_structure.

Safety
- Skyblock: the void is below. Never dig the block you stand on near an edge; don't walk off the island.
- On danger events, deal with them first (stop, retreat, eat), then tell the players briefly.
""");
        if (!string.IsNullOrWhiteSpace(personaExtra)) sb.Append("\n").Append(personaExtra.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(knowledge))
        {
            sb.Append(retrieval ? "\n# Skyblock core rules\n" : "\n# Skyblock knowledge\n").Append(knowledge.Trim()).Append('\n');
            if (retrieval) sb.Append("(More Skyblock know-how: recall kind=knowledge; relevant bits also appear under [memory].)\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Small block placed after the static prefix (its own cache breakpoint), so a rename or personality update never
    /// invalidates the cached tools, instructions and knowledge.
    /// </summary>
    public static string Identity(string username, string displayName, IReadOnlyList<string> aliases) =>
        $"Your name in chat is {displayName}" + (aliases.Count > 0 ? $" (you also answer to: {string.Join(", ", aliases)})" : "") +
        $". Your login username is {username}; that is what shows in <{username}> chat lines. " +
        "If players rename you or give you a nickname, use set_name / add_alias (remove_alias to drop one).";

    /// <summary>Extra instruction appended to the cheap tier's turn text (dynamic part, not the cached prefix).</summary>
    public const string CheapTierHint =
        "(quick mode: just chat. If this needs planning, game actions or tools you don't have, call escalate with a short reason; you may say a few words first.)";

    public const string CheapOnlyHint =
        "(budget low: chat only, no game actions. If asked to do something, say you're saving energy for now.)";

    /// <summary>Loads every *.md file in <paramref name="directory"/> (sorted), capped at <paramref name="maxChars"/>.</summary>
    public static string LoadKnowledge(string? directory, int maxChars, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return "";
        var sb = new StringBuilder();
        foreach (var file in Directory.GetFiles(directory, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var text = File.ReadAllText(file).Replace("\r\n", "\n").Trim();
            if (text.Length == 0) continue;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(text);
        }
        if (sb.Length > maxChars)
        {
            truncated = true;
            return sb.ToString(0, maxChars);
        }
        return sb.ToString();
    }
}
