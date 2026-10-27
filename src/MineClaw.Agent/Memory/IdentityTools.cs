using MineClaw.Agent.Chat;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Memory;

/// <summary>
/// Lets players rename the bot in chat ("your name is now X", "we'll call you X"). The identity is persisted in
/// <see cref="MemoryStore"/> and pushed into the <see cref="ChatRouter"/> immediately; the agent's (uncached)
/// identity prompt block picks it up from the next turn.
/// </summary>
public static class IdentityTools
{
    public const int MaxAliases = 8;
    public const int MaxNameLength = 24;

    /// <summary>Applies a persisted identity (if any) to the router; call once at startup.</summary>
    public static void ApplyPersisted(MemoryStore memory, ChatRouter chat)
    {
        var id = memory.Read(d => d.Identity is { } i ? (DisplayName: i.DisplayName, Aliases: i.Aliases.ToArray()) : default);
        if (!string.IsNullOrWhiteSpace(id.DisplayName)) chat.SetIdentity(id.DisplayName, id.Aliases ?? []);
    }

    /// <summary>Validates/cleans a name; null if unusable.</summary>
    public static string? CleanName(string? raw)
    {
        var s = ChatText.Sanitize(raw ?? "").Trim().Trim('"', '\'', '.', '!', '?');
        return s.Length is > 0 and <= MaxNameLength ? s : null;
    }

    public static ToolResult SetName(MemoryStore memory, ChatRouter chat, string? name, bool keepOld, string? by)
    {
        var dn = CleanName(name);
        if (dn == null) return ToolResult.Error($"name must be 1-{MaxNameLength} plain chars");
        var aliases = chat.Aliases.ToList();
        if (keepOld && !chat.DisplayName.Equals(dn, StringComparison.OrdinalIgnoreCase)) aliases.Insert(0, chat.DisplayName);
        aliases.RemoveAll(a => a.Equals(dn, StringComparison.OrdinalIgnoreCase));
        if (aliases.Count > MaxAliases) aliases = aliases.Take(MaxAliases).ToList();
        Save(memory, chat, dn, aliases, by);
        return ToolResult.Success($"name now {dn}" + (aliases.Count > 0 ? $" (aka {string.Join(", ", aliases)})" : ""));
    }

    public static ToolResult AddAlias(MemoryStore memory, ChatRouter chat, string? alias, string? by)
    {
        var a = CleanName(alias);
        if (a == null) return ToolResult.Error($"alias must be 1-{MaxNameLength} plain chars");
        var aliases = chat.Aliases.ToList();
        if (a.Equals(chat.DisplayName, StringComparison.OrdinalIgnoreCase) || aliases.Contains(a, StringComparer.OrdinalIgnoreCase))
            return ToolResult.Success($"already answer to {a}");
        if (aliases.Count >= MaxAliases) return ToolResult.Error($"too many aliases (max {MaxAliases}); remove one first");
        aliases.Add(a);
        Save(memory, chat, chat.DisplayName, aliases, by);
        return ToolResult.Success($"aliases: {string.Join(", ", aliases)}");
    }

    public static ToolResult RemoveAlias(MemoryStore memory, ChatRouter chat, string? alias)
    {
        var aliases = chat.Aliases.ToList();
        if (aliases.RemoveAll(x => x.Equals(alias?.Trim(), StringComparison.OrdinalIgnoreCase)) == 0)
            return ToolResult.Error(chat.DisplayName.Equals(alias?.Trim(), StringComparison.OrdinalIgnoreCase)
                ? "that's the main name; use set_name" : "no such alias");
        Save(memory, chat, chat.DisplayName, aliases, null);
        return ToolResult.Success(aliases.Count > 0 ? $"aliases: {string.Join(", ", aliases)}" : "no aliases left");
    }

    private static void Save(MemoryStore memory, ChatRouter chat, string displayName, List<string> aliases, string? by)
    {
        memory.SetIdentity(displayName, aliases, by);
        chat.SetIdentity(displayName, aliases);
    }

    public static void RegisterAll(ToolRegistry registry, MemoryStore memory, ChatRouter chat)
    {
        registry.Register("set_name", "Change the name you go by in chat (when players rename you). Old name stays as an alias unless keep_old=false.",
            ToolSchema.Create().Str("name").Bool("keep_old", required: false).Str("by", "who named you", required: false).Build(),
            (input, _) => Task.FromResult(SetName(memory, chat, input.Str("name"), input.Bool("keep_old") ?? true, input.Str("by"))),
            cheapTier: true);

        registry.Register("add_alias", "Add a nickname you answer to.",
            ToolSchema.Create().Str("alias").Str("by", required: false).Build(),
            (input, _) => Task.FromResult(AddAlias(memory, chat, input.Str("alias"), input.Str("by"))), cheapTier: true);

        registry.Register("remove_alias", "Stop answering to a nickname.",
            ToolSchema.Create().Str("alias").Build(),
            (input, _) => Task.FromResult(RemoveAlias(memory, chat, input.Str("alias"))), cheapTier: true);
    }
}
