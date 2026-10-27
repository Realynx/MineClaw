using System.Text.RegularExpressions;
using MineClaw.Agent.Chat;
using MineClaw.Hive.Ipc;

namespace MineClaw.Hive.Chat;

/// <summary>
/// Decides which hive bot answers a player message — the same deterministic rule in every process, so each message gets
/// exactly one replier without any game-chat coordination:
/// <list type="number">
/// <item>a line from a hive bot (main or any clone, incl. /me) is hive-internal: nobody;</item>
/// <item>a message that addresses a live clone ("67, come here", "maid chan 420", "#69", "clone 1337") → that clone
///   (first one mentioned);</item>
/// <item>otherwise the player's sticky partner (the hive bot that last took a message from them, within the window);</item>
/// <item>otherwise the main bot (which still applies its own addressed-or-not rules).</item>
/// </list>
/// The main process is the authority for the live roster and the sticky map and pushes them to clones (<see cref="RosterMsg"/>).
/// </summary>
public sealed class ChatArbiter
{
    private readonly object _lock = new();
    private readonly string _self;
    private readonly HashSet<string> _hiveBots;
    private readonly Dictionary<string, Regex> _explicit = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Regex> _bare = new(StringComparer.OrdinalIgnoreCase);
    private Regex _mainRegex = new("(?!)");
    private readonly Dictionary<string, (string Owner, DateTimeOffset Until)> _sticky = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private string _main;
    private string[] _mainNames;
    private HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);

    public ChatArbiter(string self, string main, IEnumerable<string> clones, IEnumerable<string>? mainNames = null, TimeSpan? sticky = null, TimeProvider? time = null)
    {
        _self = self;
        _main = main;
        _time = time ?? TimeProvider.System;
        StickyWindow = sticky ?? TimeSpan.FromSeconds(45);
        Clones = clones.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _hiveBots = new HashSet<string>([main, .. Clones], StringComparer.OrdinalIgnoreCase);
        _mainNames = (mainNames ?? [main]).ToArray();
        Rebuild();
    }

    public string Self => _self;
    public string Main => _main;
    public IReadOnlyList<string> Clones { get; }
    public TimeSpan StickyWindow { get; }
    public bool IsMain => string.Equals(_self, _main, StringComparison.OrdinalIgnoreCase);

    /// <summary>Raised when the sticky map or roster changed here (main pushes the new roster to clones).</summary>
    public event Action? Changed;

    public bool IsHiveBot(string name) { lock (_lock) return _hiveBots.Contains(name); }

    /// <summary>"maid_chan_67" → "67" (the clone's number), or null.</summary>
    public static string? NumberOf(string clone) => Regex.Match(clone, @"(\d+)$") is { Success: true } m ? m.Groups[1].Value : null;

    public IReadOnlyCollection<string> Live { get { lock (_lock) return _live.ToArray(); } }

    /// <summary>Main: which clones are online (connected and in game).</summary>
    public void SetLive(IEnumerable<string> live, IEnumerable<string>? mainNames = null)
    {
        lock (_lock)
        {
            _live = new HashSet<string>(live, StringComparer.OrdinalIgnoreCase);
            if (mainNames != null) { _mainNames = mainNames.ToArray(); Rebuild(); }
        }
        Changed?.Invoke();
    }

    public RosterMsg ToRoster()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            return new RosterMsg(_main, _mainNames, _live.ToArray(),
                _sticky.Where(kv => kv.Value.Until > now).ToDictionary(kv => kv.Key, kv => new StickyMsg(kv.Value.Owner, kv.Value.Until.ToUnixTimeMilliseconds())));
        }
    }

    /// <summary>Clone: adopt main's roster.</summary>
    public void Apply(RosterMsg r)
    {
        lock (_lock)
        {
            _main = r.Main;
            _hiveBots.Add(r.Main);
            _live = new HashSet<string>(r.Live, StringComparer.OrdinalIgnoreCase);
            _mainNames = r.MainNames;
            _sticky.Clear();
            foreach (var (p, s) in r.Sticky) _sticky[p] = (s.Owner, DateTimeOffset.FromUnixTimeMilliseconds(s.UntilMs));
            Rebuild();
        }
    }

    private void Rebuild()
    {
        // Names the hive answers to (main's display name/aliases/login): "<name> 67" addresses clone 67.
        var names = _mainNames.Concat([_main]).Concat(["clone", "bot", "maid chan"]).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length)
            .Select(n => string.Join(@"[\s_-]*", n.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)));
        string nameAlt = string.Join("|", names);
        var own = _mainNames.Concat([_main]).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length)
            .Select(n => string.Join(@"[\s_-]*", n.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)));
        _mainRegex = new Regex($@"(?<![A-Za-z0-9_])@?(?:{string.Join("|", own)})(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        _explicit.Clear();
        _bare.Clear();
        foreach (var c in Clones)
        {
            var num = NumberOf(c);
            var user = string.Join(@"[\s_-]*", c.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
            var parts = new List<string> { $@"(?<![A-Za-z0-9_]){user}(?![A-Za-z0-9])" };
            if (num != null)
            {
                parts.Add($@"^[\s@#]*{num}(?![0-9])");                                  // "67, come here"
                parts.Add($@"(?<![A-Za-z0-9_])(?:{nameAlt})[\s_#-]*{num}(?![0-9])");    // "maid chan 420", "clone 69"
                parts.Add($@"(?<![A-Za-z0-9_])#{num}(?![0-9])");                         // "#1337"
                _bare[c] = new Regex($@"(?<![0-9]){num}(?![0-9])", RegexOptions.CultureInvariant);
            }
            _explicit[c] = new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    /// <summary>The bot that should handle this message: null for hive-internal lines, else a hive login.</summary>
    public string? OwnerOf(ChatMessage msg) => Resolve(msg).Owner;

    /// <summary>Owner + whether the owner was explicitly addressed (a clone named in the message).</summary>
    public (string? Owner, bool Explicit) Resolve(ChatMessage msg)
    {
        if (msg.Player == null) return (null, false);
        lock (_lock)
        {
            if (_hiveBots.Contains(msg.Player)) return (null, false);
            int best = int.MaxValue;
            string? owner = null;
            foreach (var c in Clones)
            {
                if (!_live.Contains(c)) continue;
                var m = _explicit[c].Match(msg.Text);
                if (!m.Success && msg.Kind == ChatKind.Emote && _bare.TryGetValue(c, out var b)) m = b.Match(msg.Text); // "* kiwi pats 67"
                if (m.Success && m.Index < best) { best = m.Index; owner = c; }
            }
            // Main named before (or instead of) any clone: main was addressed explicitly ("claude chan, ..." ends a clone's sticky).
            var mm = _mainRegex.Match(msg.Text);
            if (mm.Success && mm.Index < best) return (_main, true);
            if (owner != null) return (owner, true);
            if (_sticky.TryGetValue(msg.Player, out var s) && s.Until > _time.GetUtcNow()
                && (string.Equals(s.Owner, _main, StringComparison.OrdinalIgnoreCase) || _live.Contains(s.Owner)))
                return (s.Owner, false);
            return (_main, false);
        }
    }

    /// <summary>
    /// ChatRouter hook (<see cref="ChatRouter.Arbiter"/>): whether THIS bot handles the message. An explicitly addressed clone
    /// always takes it; otherwise the owner (sticky partner, else main) takes it only if its own router also considers it
    /// addressed (reply window, solo player, name) — so "poofimafox look at this" goes to nobody.
    /// </summary>
    public bool Decide(ChatMessage msg, bool locallyAddressed)
    {
        var (owner, isExplicit) = Resolve(msg);
        if (owner == null || msg.Player == null) return false;
        bool mine = string.Equals(owner, _self, StringComparison.OrdinalIgnoreCase);
        bool handle = mine && (isExplicit || locallyAddressed);
        if (handle) Accept(msg.Player, _self);
        else if (IsMain && isExplicit) Accept(msg.Player, owner); // main is the sticky authority (pushed to clones)
        return handle;
    }

    /// <summary>Records that <paramref name="owner"/> is talking with <paramref name="player"/>.</summary>
    public void Accept(string player, string owner)
    {
        lock (_lock) _sticky[player] = (owner, _time.GetUtcNow() + StickyWindow);
        Changed?.Invoke();
    }

    /// <summary>A clone left: its sticky conversations fall back to main.</summary>
    public void Forget(string owner)
    {
        lock (_lock) foreach (var k in _sticky.Where(kv => string.Equals(kv.Value.Owner, owner, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList()) _sticky.Remove(k);
    }
}
