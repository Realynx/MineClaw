using System.Text.RegularExpressions;

namespace MineClaw.Hive.Hierarchy;

/// <summary>
/// "Hive:Accounts": dynamic clone accounts. Main allocates a name per spawn, whitelists it, and removes it from the whitelist when
/// the clone is released. An empty state is main alone on the whitelist; nothing assumes pre-existing clone accounts.
/// </summary>
public sealed class HiveAccountsConfig
{
    /// <summary>Every managed account starts with this. The whitelist exception only ever touches names with it.</summary>
    public string Prefix { get; set; } = "maid_chan_";
    /// <summary>Minecraft usernames are at most 16 characters.</summary>
    public int MaxLength { get; set; } = 16;
    /// <summary>First numeric id tried for a fresh account (the number is how players address a clone: "101, come here").</summary>
    public int FirstId { get; set; } = 101;
    public int LastId { get; set; } = 9999;
    /// <summary>The persisted managed list, under the data directory. Kept outside the db so a memory reset never orphans a
    /// whitelisted account.</summary>
    public string File { get; set; } = "hive-accounts.json";
    /// <summary>At startup, remove every managed account that is whitelisted but not running (crash cleanup).</summary>
    public bool ReconcileOnStart { get; set; } = true;
    /// <summary>Old fixed-roster names (Hive:CloneUsernames) are adopted into the managed list once, so the reconcile takes them off
    /// the whitelist and the allocator can reuse them (their saved spots and history). Their config entries are otherwise ignored.</summary>
    public bool AdoptLegacy { get; set; } = true;
    /// <summary>The model profile a DeepSeek hive brain runs on (Agent:Llm:Profiles; Ollama). Missing/unusable = "not available".</summary>
    public string DeepSeekProfile { get; set; } = "deepseek";
    /// <summary>Public chat lines per minute per hive (main and each clone) for lines nobody asked for. Addressed replies always
    /// pass but count. 0 = no budget.</summary>
    public double ChatLinesPerMinute { get; set; } = 4;
    /// <summary>Time a released hive's children get to deposit and log out before the hive itself is recalled.</summary>
    public double ReleaseChildSeconds { get; set; } = 200;
}

/// <summary>Name rules for managed accounts (pure; tested).</summary>
public static class AccountNames
{
    private static readonly Regex Valid = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A legal Minecraft name that starts with the prefix, fits <see cref="HiveAccountsConfig.MaxLength"/> and isn't main.</summary>
    public static bool IsManagedShape(string? name, HiveAccountsConfig cfg, string main)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        return n.Length <= Math.Min(16, Math.Max(3, cfg.MaxLength)) && Valid.IsMatch(n)
            && n.StartsWith(cfg.Prefix, StringComparison.OrdinalIgnoreCase) && n.Length > cfg.Prefix.Length
            && !n.Equals(main, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ids players could mistake for a quantity at the start of a line ("64 cobble", "10 more") are skipped.</summary>
    public static bool QuantityLike(int id) => id % 8 == 0 || id % 10 == 0 || id % 25 == 0;

    public static string For(HiveAccountsConfig cfg, int id) => cfg.Prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
