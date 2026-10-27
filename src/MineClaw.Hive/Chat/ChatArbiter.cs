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
/// <item>a message to several/all bots ("both of you", "everyone", "all maids") while a clone is live → main, which splits
///   the work and hands clones their part over IPC (clones never take group asks themselves);</item>
/// <item>"main, ..." / main's names → main;</item>
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
    private static readonly Regex MainVocative = new(@"^\W*(?:(?:hey|yo|ok|okay|oi)\s+)?main(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GroupAddress = new(
        @"(?<![A-Za-z0-9_])(?:both of (?:you|u|ya)|you both|u both|you two|all of (?:you|u|ya)|you all|y'?all|every(?:one|body)|all (?:the |you )?(?:maids?|bots|clones)|(?:maids|bots|clones|you guys),|hey (?:maids|bots|guys))(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly Dictionary<string, (string Owner, DateTimeOffset Until)> _sticky = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Conversation-thread leases: the bot that asked a player something / is watching their demo owns their replies.</summary>
    private readonly Dictionary<string, (string Owner, DateTimeOffset Until)> _threads = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>(player, open, ttl): THIS bot opened/closed a thread lease (a clone forwards it to main over IPC).</summary>
    public event Action<string, bool, TimeSpan>? LocalThreadChanged;

    // ---------------------------------------------------------------- thread ownership leases

    /// <summary>
    /// <paramref name="owner"/> asked <paramref name="player"/> something or is watching their demo: it owns that player's
    /// replies until <see cref="CloseThread"/> or the TTL. Other bots ignore those replies (unless the player names one).
    /// </summary>
    public void OpenThread(string player, string owner, TimeSpan ttl)
    {
        lock (_lock) _threads[player] = (owner, _time.GetUtcNow() + (ttl <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : ttl));
        if (string.Equals(owner, _self, StringComparison.OrdinalIgnoreCase)) { try { LocalThreadChanged?.Invoke(player, true, ttl); } catch (Exception) { } }
        Changed?.Invoke();
    }

    /// <summary>Ends <paramref name="owner"/>'s lease on <paramref name="player"/> (no-op when someone else holds it).</summary>
    public void CloseThread(string player, string owner)
    {
        bool removed;
        lock (_lock)
            removed = _threads.TryGetValue(player, out var t) && string.Equals(t.Owner, owner, StringComparison.OrdinalIgnoreCase) && _threads.Remove(player);
        if (!removed) return;
        if (string.Equals(owner, _self, StringComparison.OrdinalIgnoreCase)) { try { LocalThreadChanged?.Invoke(player, false, TimeSpan.Zero); } catch (Exception) { } }
        Changed?.Invoke();
    }

    /// <summary>The live thread owner for a player (null = nobody holds one).</summary>
    public string? ThreadOwner(string player)
    {
        lock (_lock) return LiveThread(player);
    }

    /// <summary>Who owns a player's conversation right now: thread lease, else sticky partner (null = nobody / main default).</summary>
    public string? ConversationOwner(string player)
    {
        lock (_lock)
        {
            if (LiveThread(player) is { } t) return t;
            return _sticky.TryGetValue(player, out var s) && s.Until > _time.GetUtcNow() ? s.Owner : null;
        }
    }

    /// <summary>Main: hands a player's conversation to <paramref name="owner"/> explicitly (sticky + any thread move over).</summary>
    public void HandOver(string player, string owner)
    {
        lock (_lock)
        {
            _threads.Remove(player);
            _sticky[player] = (owner, _time.GetUtcNow() + StickyWindow);
        }
        Changed?.Invoke();
    }

    // caller holds _lock
    private string? LiveThread(string player)
    {
        if (!_threads.TryGetValue(player, out var t)) return null;
        if (t.Until <= _time.GetUtcNow()) { _threads.Remove(player); return null; }
        bool ownerUp = string.Equals(t.Owner, _main, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Owner, _self, StringComparison.OrdinalIgnoreCase) || _live.Contains(t.Owner);
        return ownerUp ? t.Owner : null;
    }

    /// <summary>
    /// Wires this arbiter into a bot's chat router (main and clones): ownership of each player message, thread leases while
    /// this bot waits on a player, and never completing our own waiters with another bot's reply.
    /// </summary>
    public void Bind(ChatRouter chat)
    {
        chat.IsHiveBot = IsHiveBot;
        chat.Arbiter = Decide;
        chat.OwnedElsewhere = OwnedElsewhere;
        chat.ThreadChanged += (player, open, ttl) =>
        {
            if (open) OpenThread(player, _self, ttl);
            else CloseThread(player, _self);
        };
    }

    /// <summary>True when the message belongs to another bot: it holds the thread, or the player named it explicitly.</summary>
    public bool OwnedElsewhere(ChatMessage msg)
    {
        if (msg.Player == null) return false;
        var r = Route(msg);
        if (r.Owner == null) return false;
        return r.Explicit && !string.Equals(r.Owner, _self, StringComparison.OrdinalIgnoreCase);
    }

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
                _sticky.Where(kv => kv.Value.Until > now).ToDictionary(kv => kv.Key, kv => new StickyMsg(kv.Value.Owner, kv.Value.Until.ToUnixTimeMilliseconds())),
                _threads.Where(kv => kv.Value.Until > now).ToDictionary(kv => kv.Key, kv => new StickyMsg(kv.Value.Owner, kv.Value.Until.ToUnixTimeMilliseconds())));
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
            // Main's thread map is authoritative, except our own fresh leases it may not have echoed yet.
            var mine = _threads.Where(kv => string.Equals(kv.Value.Owner, _self, StringComparison.OrdinalIgnoreCase)).ToList();
            _threads.Clear();
            foreach (var (p, s) in r.Threads ?? []) _threads[p] = (s.Owner, DateTimeOffset.FromUnixTimeMilliseconds(s.UntilMs));
            foreach (var (p, t) in mine) if (!_threads.ContainsKey(p)) _threads[p] = t;
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

    /// <summary>True when the text addresses several/all bots at once ("both of you", "everyone", "all maids").</summary>
    public static bool IsGroupAddress(string? text) => text != null && GroupAddress.IsMatch(text);

    /// <summary>
    /// Main: a player line (not a hive bot's) addressing several bots while at least one clone is live, i.e. a group ask
    /// main should split between the bots.
    /// </summary>
    public bool IsGroupAsk(ChatMessage msg) =>
        msg.Player != null && !IsHiveBot(msg.Player) && Live.Count > 0 && IsGroupAddress(msg.Text);

    /// <summary>Owner + whether the owner was explicitly addressed (a clone named in the message).</summary>
    public (string? Owner, bool Explicit) Resolve(ChatMessage msg)
    {
        var r = Route(msg);
        return (r.Owner, r.Explicit);
    }

    /// <summary>Full routing verdict: owner, explicit address, group ask (owner is then main).</summary>
    public ChatRoute Route(ChatMessage msg)
    {
        if (msg.Player == null) return new(null, false, false);
        lock (_lock)
        {
            if (_hiveBots.Contains(msg.Player)) return new(null, false, false);
            // Several bots at once: main owns it and splits the work (clones get their part as an IPC task).
            if (_live.Count > 0 && msg.Kind != ChatKind.Emote && GroupAddress.IsMatch(msg.Text)) return new(_main, true, true);
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
            if (mm.Success && mm.Index < best) return new(_main, true, false);
            if (owner != null) return new(owner, true, false);
            if (MainVocative.IsMatch(msg.Text)) return new(_main, true, false); // "main do a little hop"
            // A bot that asked this player something (or is watching their demo) owns their replies until resolved/timeout.
            if (LiveThread(msg.Player) is { } holder) return new(holder, true, false);
            if (_sticky.TryGetValue(msg.Player, out var s) && s.Until > _time.GetUtcNow()
                && (string.Equals(s.Owner, _main, StringComparison.OrdinalIgnoreCase) || _live.Contains(s.Owner)))
                return new(s.Owner, false, false);
            return new(_main, false, false);
        }
    }

    /// <summary>
    /// Clone hook (<see cref="ChatRouter.ExplicitlyAddressed"/>): THIS bot was named in the message ("67, ...").
    /// </summary>
    public bool AddressesSelf(ChatMessage msg) =>
        Route(msg) is { Explicit: true, Group: false } r && string.Equals(r.Owner, _self, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>A clone left: its sticky conversations and thread leases fall back to main.</summary>
    public void Forget(string owner)
    {
        lock (_lock)
        {
            foreach (var k in _sticky.Where(kv => string.Equals(kv.Value.Owner, owner, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList()) _sticky.Remove(k);
            foreach (var k in _threads.Where(kv => string.Equals(kv.Value.Owner, owner, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList()) _threads.Remove(k);
        }
    }
}

/// <summary>Arbitration verdict for one player message.</summary>
public sealed record ChatRoute(string? Owner, bool Explicit, bool Group);
