using System.Text.RegularExpressions;

namespace MineClaw.Agent.Routines;

/// <summary>
/// A standing trigger on a saved script (docs/design/reactive-character.md): "when I punch a log, chop that tree", "if anyone
/// hits me, guard me", or the brain's own learned reaction ("next time someone thanks you, do this instantly"). The binding
/// runner starts the script the moment the game event fires (no model in the loop), with the event's payload as its args.
/// Stored on the script's routine row, so it is versioned with it, hive-shared and survives restarts.
/// </summary>
public sealed class ScriptBinding
{
    /// <summary>A game event ("player.punchBlock", "player.hurt", "me.hurt", ...) or a chat intent ("thanks", "come").</summary>
    public string On { get; set; } = "";
    /// <summary>The event filter (JSON object: player, held, block, pos, region, cooldown, once, ...).</summary>
    public string? Filter { get; set; }
    /// <summary>Only this player's events trigger it (null = whoever the filter allows).</summary>
    public string? For { get; set; }
    public DateTimeOffset? Expires { get; set; }
    /// <summary>Who asked for it (null = the brain's own reaction).</summary>
    public string? CreatedBy { get; set; }
    public bool CreatorTrusted { get; set; }
    /// <summary>"binding" (a player's standing request) | "reaction" (the brain's learned social reaction: harmless only).</summary>
    public string Kind { get; set; } = "binding";
    /// <summary>The script acts on the world (dig, place, attack, items): only trusted players may create those.</summary>
    public bool World { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Fires, runs that ended ok / failed, skips (safety, body busy).</summary>
    public int Fires { get; set; }
    public int Ok { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    /// <summary>Reconcile verdicts on what it did: kept as is, amended (the brain added to it), undone/corrected.</summary>
    public int Kept { get; set; }
    public int Amended { get; set; }
    public int Corrected { get; set; }
    public DateTimeOffset? LastFired { get; set; }
    /// <summary>Why it was switched off ("demoted: corrected 2x, kept 0x", "expired", "removed by Fox").</summary>
    public string? OffNote { get; set; }

    public bool Live(DateTimeOffset now) => Enabled && (Expires is not { } e || e > now);

    /// <summary>"punch_mine: player.punchBlock {held:pickaxe} for PoofImaFox, 41 min left, fired 3x".</summary>
    public string Describe(string name, DateTimeOffset now) =>
        $"{name}: {On}" + (string.IsNullOrWhiteSpace(Filter) || Filter == "{}" ? "" : " " + Filter) +
        (For != null ? " for " + For : "") + (Expires is { } e ? (e > now ? $", {(int)Math.Ceiling((e - now).TotalMinutes)} min left" : ", expired") : "") +
        $", fired {Fires}x" + (Kind == "reaction" ? " (reaction)" : "") + (!Enabled ? " [off" + (OffNote != null ? ": " + OffNote : "") + "]" : "");
}

/// <summary>Binding operations on the script library (the script tool's op=bind|unbind|bindings, the binding runner).</summary>
public static class ScriptBindings
{
    /// <summary>A reaction the brain keeps having to correct is demoted after this many corrections (more than it was kept).</summary>
    public const int DemoteAfterCorrections = 2;

    /// <summary>Calls that act on the world or other players (dig/place/attack/take/give items, build...): trusted creators only.</summary>
    private static readonly Regex WorldCalls = new(
        @"\b(?:dig|mine|mineAt|mine_at|place|attack|defend|guard|guardArea|build|use_block|useBlock|use|deposit|withdraw|take|give|give_items|giveItems|toss|drop|drop_items|dropItems|craft|smelt|chop|chop_tree|chopTree|gen|harvest_generator|harvestGenerator|mineGen|harvestFarm|chopTrees|store|restock|deliver|move|move_items|moveItems|organize_chest|sort|eat|skill|collect|collect_drops|acquire|ensure|parallel|spawn|crew)\s*\(",
        RegexOptions.Compiled);

    /// <summary>True when the code calls anything beyond movement, gaze, gestures, chat and reads.</summary>
    public static bool ActsOnWorld(string code) => WorldCalls.IsMatch(StripStrings(code ?? ""));

    private static string StripStrings(string code) =>
        Regex.Replace(Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline), @"""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|`(?:[^`\\]|\\.)*`", "''");

    public static bool Bind(this ScriptLibrary lib, string name, ScriptBinding b)
    {
        var r = lib.Get(name);
        if (r?.Script is not { } s) return false;
        s.Binding = b;
        lib.Memory.SaveRoutine(r);
        return true;
    }

    /// <summary>Switches a binding off (kept for history); false when there's none by that name.</summary>
    public static bool Unbind(this ScriptLibrary lib, string name, string why)
    {
        var r = lib.Get(name);
        if (r?.Script?.Binding is not { } b) return false;
        b.Enabled = false;
        b.OffNote = why.Length <= 80 ? why : why[..80];
        lib.Memory.SaveRoutine(r);
        return true;
    }

    /// <summary>Every saved script with a binding (name, binding), live ones first.</summary>
    public static IReadOnlyList<(string Name, ScriptBinding Binding)> Bindings(this ScriptLibrary lib, bool includeOff = false)
    {
        var now = lib.Now();
        return lib.Scripts().Where(r => r.Script?.Binding is { } b && (includeOff || b.Live(now)))
            .Select(r => (r.Name, r.Script!.Binding!)).OrderByDescending(x => x.Item2.Live(now)).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Updates a binding's stats in its row (fires, outcomes, verdicts); a reaction corrected too often is demoted.</summary>
    public static ScriptBinding? Update(this ScriptLibrary lib, string name, Action<ScriptBinding> change)
    {
        var r = lib.Get(name);
        if (r?.Script?.Binding is not { } b) return null;
        change(b);
        if (b.Enabled && b.Corrected >= DemoteAfterCorrections && b.Corrected > b.Kept)
        {
            b.Enabled = false;
            b.OffNote = $"demoted: corrected {b.Corrected}x, kept {b.Kept}x";
        }
        if (b.Enabled && b.Expires is { } e && e <= lib.Now()) { b.Enabled = false; b.OffNote = "expired"; }
        lib.Memory.SaveRoutine(r);
        return b;
    }

    /// <summary>"bindings: punch_mine: player.punchBlock … | thanks_bow: player.say {intent:thanks} …" or "no bindings".</summary>
    public static string List(this ScriptLibrary lib)
    {
        var all = lib.Bindings(includeOff: true);
        if (all.Count == 0) return "no bindings";
        var now = lib.Now();
        return "bindings: " + string.Join(" | ", all.Take(12).Select(x => x.Binding.Describe(x.Name, now)));
    }
}
