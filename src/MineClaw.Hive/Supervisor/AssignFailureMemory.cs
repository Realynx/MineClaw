using System.Text.RegularExpressions;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Ipc;

namespace MineClaw.Hive.Supervisor;

/// <summary>
/// ToolCallGuard-style failure memory for hive assignments, keyed on (clone, task intent). Live 10-27 15:44-15:46: main sent
/// "fight mobs" to 67 three times (twice as a task, once as a script) and 67 refused each time for the same reason (hp 7,
/// food 0, no food anywhere). A remembered refusal blocks the same intent for that clone until something relevant changed:
/// the clone's hp, food or inventory, day/night, or a player re-asking with new information ("try again", "I gave her
/// bread"). A bare "she can try" is not new information. Otherwise main picks another clone or reports honestly.
/// </summary>
public sealed class AssignFailureMemory(TimeProvider? time = null)
{
    private sealed record Entry(string Bot, TaskIntent Intent, string Reason, DateTimeOffset At, int? Hp, int? Food, string Items, bool? Night);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();

    /// <summary>Upper bound on how long a refusal is remembered without any change.</summary>
    public TimeSpan Memory { get; set; } = TimeSpan.FromMinutes(30);

    public static string ItemsSig(IReadOnlyList<ItemMsg>? items) =>
        items == null ? "?" : string.Join(",", items.Where(i => i.Count > 0).OrderBy(i => i.Id).ThenBy(i => i.Damage).Select(i => $"{i.Id}:{i.Damage}x{i.Count}"));

    /// <summary>A clone's task for <paramref name="intent"/> failed with <paramref name="reason"/> (its body as it was then).</summary>
    public void Record(string clone, TaskIntent intent, string reason, CloneVitals v, bool? night)
    {
        // Live 10-27 21:53:36: "420 already refused mining: ... the cobble gen stalled: 4,64,6 turned to water ..." blocked an
        // immediate retry on another cell. Environment/transient failures are not refusals: they never block.
        if (Classify(reason) == FailureKind.Transient)
        {
            Clear(clone, intent);
            return;
        }
        var e = new Entry(clone, intent, OneLine(reason), _time.GetUtcNow(), v.Hp, v.Food, ItemsSig(v.Items), night);
        lock (_lock)
        {
            _entries.RemoveAll(x => Same(x, clone, intent));
            _entries.Add(e);
            if (_entries.Count > 40) _entries.RemoveAt(0);
        }
    }

    public enum FailureKind { State, Transient, Unknown }

    /// <summary>Something about the clone itself (hp, food, tools/gear, a safety refusal): blocks until that changes.</summary>
    private static readonly Regex StateReason = new(
        @"\b(?:starv\w*|hungry|no food|out of food|food (?:is )?(?:at )?[0-9]|hp (?:is )?(?:at )?[0-9]|low (?:hp|health)|too weak|(?:(?:no|without|lacks?|lacking|missing|out of)\s+|need(?:s)?\s+(?:a |an |some )?)(?:stone |iron |wood(?:en)? |diamond )?(?:pick(?:axe)?|sword|axe|shovel|hoe|weapon|armou?r|tools?|bucket|shears)|not (?:safe|allowed)|unsafe|too dangerous|refus\w*|won'?t)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>The world, not the clone: a busy/stalled/flooded cell, contention, a blocked path, a race, a timeout.</summary>
    private static readonly Regex TransientReason = new(
        @"\b(?:stall\w*|isn'?t regenerating|not regenerating|turned (?:to|into) (?:water|lava|obsidian)|water|lava|flow\w*|busy|taken|in use|occupied|contention|racing|race|another (?:bot|clone|player|miner)|other miners?|someone (?:else )?(?:was )?(?:mining|using)|blocked|no path|unreachable|timed? ?out|gave 0|0 cobble|yield [0-9]+ ?%|regen\w*|needs repair)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>State-based (blocks a retry until hp/food/inventory/day changes) or transient (an immediate retry is fine).</summary>
    public static FailureKind Classify(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return FailureKind.Unknown;
        if (StateReason.IsMatch(reason)) return FailureKind.State;
        return TransientReason.IsMatch(reason) ? FailureKind.Transient : FailureKind.Unknown;
    }

    /// <summary>A success clears the memory for that (clone, intent).</summary>
    public void Clear(string clone, TaskIntent intent)
    {
        lock (_lock) _entries.RemoveAll(x => Same(x, clone, intent));
    }

    /// <summary>
    /// The remembered refusal still standing for (clone, intent), or null when there is none or something changed since:
    /// hp/food/inventory (<paramref name="now"/>, unknown fields count as unchanged), or day/night.
    /// </summary>
    public string? Blocking(string clone, TaskIntent intent, CloneVitals now, bool? night)
    {
        var t = _time.GetUtcNow();
        lock (_lock)
        {
            _entries.RemoveAll(x => t - x.At > Memory);
            var e = _entries.LastOrDefault(x => Same(x, clone, intent));
            if (e == null) return null;
            if (VitalsChanged(e, now) || (night is { } n && e.Night is { } n0 && n != n0))
            {
                _entries.Remove(e);
                return null;
            }
            return $"{e.Reason} ({Ago(t - e.At)} ago)";
        }
    }

    /// <summary>hp, food or the inventory differ from when the refusal was recorded (unknown fields count as unchanged).</summary>
    private static bool VitalsChanged(Entry e, CloneVitals now) =>
        (now.Hp is { } hp && e.Hp is { } hp0 && hp != hp0)
        || (now.Food is { } f && e.Food is { } f0 && f != f0)
        || (now.Items != null && e.Items != "?" && ItemsSig(now.Items) != e.Items);

    private static readonly Regex Retry = new(@"\b(?:try (?:it |that |again|one more)|try again|retry|do it again|one more (?:time|try)|send (?:her|him|it|them) (?:again|back))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Ask = new(@"\b(?:have|make|let|send|get|tell|ask|can|could|please|pls|go|mine|chop|fight|craft|build|bring|do)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Gave = new(@"\b(?:gave|give|given|giving|got|has|have|had|now has|fed|feed|dropped|brought|here'?s|healed|ate|eaten|full|crafted|made|left)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FoodTalk = new(@"\b(?:food|bread|apple|meat|pork\w*|fish|steak|cookie|stew|eat\w*|hungry|starv\w*|fed)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HpTalk = new(@"\b(?:hp|health|heal\w*|hearts?|regen\w*)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GearTalk = new(@"\b(?:sword|axe|pickaxe|pick|armou?r|helmet|chestplate|leggings|boots|tools?|weapon)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeTalk = new(@"\b(?:night|day|dark|morning|sun|evening)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A player line: an explicit retry, or new information that answers a remembered reason (food for "no food", a sword for
    /// "no weapon", "it's night now" for "no mobs in daylight") clears the matching entries (all clones, or the ones named).
    /// Returns how many were cleared.
    /// </summary>
    public int OnPlayerChat(string text, Func<string, bool>? names = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        bool retry = Retry.IsMatch(text);
        bool gave = Gave.IsMatch(text);
        bool asks = Ask.IsMatch(text);
        lock (_lock)
        {
            var named = _entries.Where(e => names?.Invoke(e.Bot) == true).ToList();
            var scope = named.Count > 0 ? named : _entries.ToList();
            int n = 0;
            foreach (var e in scope)
            {
                // A player re-asking for that clone by name ("have 420 mine the gen") always clears it.
                bool answers = retry || (gave && Answers(text, e.Reason)) || (asks && named.Contains(e));
                if (!answers) continue;
                _entries.Remove(e);
                n++;
            }
            return n;
        }
    }

    private static bool Answers(string text, string reason) =>
        (FoodTalk.IsMatch(text) && FoodTalk.IsMatch(reason))
        || (HpTalk.IsMatch(text) && (HpTalk.IsMatch(reason) || FoodTalk.IsMatch(reason)))
        || (GearTalk.IsMatch(text) && GearTalk.IsMatch(reason))
        || (TimeTalk.IsMatch(text) && TimeTalk.IsMatch(reason));

    public int Count { get { lock (_lock) return _entries.Count; } }

    private static bool Same(Entry e, string clone, TaskIntent intent) => e.Bot.Equals(clone, StringComparison.OrdinalIgnoreCase) && e.Intent == intent;

    private static string OneLine(string s)
    {
        s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return s.Length <= 160 ? s : s[..157] + "...";
    }

    private static string Ago(TimeSpan t) => t.TotalSeconds < 90 ? $"{(int)t.TotalSeconds}s" : $"{(int)t.TotalMinutes}m";
}
