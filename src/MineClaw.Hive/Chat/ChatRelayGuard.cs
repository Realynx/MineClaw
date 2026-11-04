using System.Text.RegularExpressions;
using MineClaw.Hive.Supervisor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Chat;

/// <summary>
/// Main never instructs a clone through public chat: clones ignore hive bots' lines (by design), so a relayed assignment is
/// lost. Live 3, 14:34:35: main said "69, robin wants you on the cobble gen, not the trees"; 69 never got it, its question
/// timed out and it went back to the trees. This guard reads main's outgoing text: a sentence that opens by addressing a
/// live clone ("69, ...", "hey 67: ...", "maid chan 69, ...") with an instruction goes to that clone over IPC — a
/// reassignment ("wants you on", "switch to", "instead", "not the ...") as a hive task (it replaces the clone's task and
/// standing order), any other instruction as a note. The chat line itself is kept as flavour. One hook call on main's
/// speech gate.
/// </summary>
public sealed class ChatRelayGuard
{
    private readonly HiveSupervisor _hive;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _sent = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public ChatRelayGuard(HiveSupervisor hive, ILogger? log = null, TimeProvider? time = null)
    {
        _hive = hive;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    private static readonly Regex Sentence = new(@"(?<=[.!?])\s+", RegexOptions.CultureInvariant);
    // "69, ..." / "hey 69: ..." / "maid chan 69, ..." / "maid_chan_69, ..." / "clone 69, ..." at the start of a sentence.
    private static readonly Regex Vocative = new(
        @"^\W*(?:(?:hey|ok|okay|oh|so|and|also)\s+)?(?:(?:maid[\s_-]*chan|clone|maid)[\s_#-]*)?#?(?<num>\d{2,4})(?![\d,.]\d)\s*[,:]\s*(?<rest>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Instruction = new(
        @"\b(?:wants? you|need(?:s)? you|you(?:'re| are) (?:on|doing)|go|come|mine|chop|craft|bring|get|take|switch|stop|start|help|do|work|move|head|deposit|drop|give|build|place|dig|harvest|farm|instead|keep|grab|fetch|cut)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Reassign = new(
        @"\b(?:wants? you (?:on|to)|need(?:s)? you (?:on|to)|switch(?:ing)? to|instead|not the|change of plans?|new job|you(?:'re| are) on)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What main will say: unchanged; instructions to clones found in it are sent over IPC as a side effect.</summary>
    public string Filter(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        foreach (var raw in Sentence.Split(text))
        {
            if (InstructionIn(raw) is not { } ins || !FirstTime(ins.Clone + "|" + ins.Text)) continue;
            Relay(ins.Clone, ins.Slot, ins.Text);
        }
        return text;
    }

    /// <summary>A sentence addressed to a live clone by number that instructs it ("69, go mine"); null = chat ("69, good job!").</summary>
    private (string Clone, CloneSlot Slot, string Text)? InstructionIn(string raw)
    {
        var m = Vocative.Match(raw.Trim());
        if (!m.Success) return null;
        if (_hive.Resolve(m.Groups["num"].Value) is not { } clone || _hive.Slot(clone) is not { IsLive: true } slot) return null;
        var rest = m.Groups["rest"].Value.Trim();
        return rest.Length < 4 || !Instruction.IsMatch(rest) ? null : (clone, slot, rest);
    }

    /// <summary>Not relayed in the last two minutes (the gate runs per send path); records it now.</summary>
    private bool FirstTime(string key)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_sent.TryGetValue(key, out var at) && now - at < TimeSpan.FromMinutes(2)) return false;
            _sent[key] = now;
            return true;
        }
    }

    /// <summary>A reassignment becomes a hive task; anything else a note on the clone's running task.</summary>
    private void Relay(string clone, CloneSlot slot, string rest)
    {
        if (Reassign.IsMatch(rest))
        {
            var r = _hive.Spawn($"From main: {rest}", slot.Number);
            _log.LogInformation("hive: main's chat to {Clone} became a hive task: {Rest} -> {Result}", clone, rest, r);
            return;
        }
        bool ok = _hive.Note(clone, $"[hive update from main] {rest} (main said it in chat; hive chat never reaches you)");
        _log.LogInformation("hive: main's chat to {Clone} became a note ({Ok}): {Rest}", clone, ok, rest);
    }
}
