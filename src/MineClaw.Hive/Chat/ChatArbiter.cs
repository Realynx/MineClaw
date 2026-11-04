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
    /// <summary>A clone's number as a whole token anywhere in the line (never part of a coordinate like 6,67,8).</summary>
    private readonly Dictionary<string, Regex> _number = new(StringComparer.OrdinalIgnoreCase);
    private Regex _mainRegex = new("(?!)");
    private static readonly Regex MainVocative = new(@"^\W*(?:(?:hey|yo|ok|okay|oi)\s+)?main(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GroupAddress = new(
        @"(?<![A-Za-z0-9_])(?:both of (?:you|u|ya)|you both|u both|you two|all of (?:you|u|ya)|you all|y'?all|every(?:one|body)|all (?:the |you )?(?:maids?|bots|clones)|(?:maids|bots|clones|you guys),|hey (?:maids|bots|guys))(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly Dictionary<string, (string Owner, DateTimeOffset Until)> _sticky = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Conversation-thread leases: the bot that asked a player something / is watching their demo owns their replies.</summary>
    private readonly Dictionary<string, (string Owner, DateTimeOffset Until)> _threads = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Main handed a player's request to a clone: that clone owns their follow-ups (explicitly) for <see cref="CloneWindow"/>.</summary>
    private readonly Dictionary<string, (string Owner, DateTimeOffset Until)> _delegated = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private string _main;
    private string[] _mainNames;
    private HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);

    public ChatArbiter(string self, string main, IEnumerable<string> clones, IEnumerable<string>? mainNames = null, TimeSpan? sticky = null, TimeProvider? time = null,
        TimeSpan? cloneWindow = null)
    {
        _self = self;
        _main = main;
        _time = time ?? TimeProvider.System;
        StickyWindow = sticky ?? TimeSpan.FromSeconds(45);
        CloneWindow = cloneWindow ?? TimeSpan.FromSeconds(30);
        Clones = clones.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _hiveBots = new HashSet<string>([main, .. Clones], StringComparer.OrdinalIgnoreCase);
        _mainNames = (mainNames ?? [main]).ToArray();
        Rebuild();
    }

    public string Self => _self;
    public string Main => _main;
    /// <summary>Every clone account the hive knows. Dynamic: main adds an account when it allocates it (<see cref="AddClone"/>),
    /// a clone learns them from main's roster.</summary>
    public IReadOnlyList<string> Clones { get; private set; }

    /// <summary>Subhive body → the hive whose process drives it (its brain hears the body's name). Main and hives keep their own.</summary>
    private readonly Dictionary<string, string> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="body"/> is a subhive body driven by <paramref name="host"/>'s brain (null = no longer): a line naming it
    /// ("113 stop") belongs to that hive, never "another bot's" to nobody (live 10-29: 420's bodies weren't in any arbiter).
    /// Learns the name as a clone account too.
    /// </summary>
    public void SetHost(string body, string? host)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        body = body.Trim();
        lock (_lock)
        {
            if (host == null) _hosts.Remove(body);
            else _hosts[body] = host.Trim();
            if (!body.Equals(_main, StringComparison.OrdinalIgnoreCase) && !Clones.Contains(body, StringComparer.OrdinalIgnoreCase))
            {
                Clones = [.. Clones, body];
                _hiveBots.Add(body);
                Rebuild();
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Main: keeps the hosts in step with the ownership tree (every subhive body → its owner; main's own → main).</summary>
    public void Follow(Hierarchy.HiveTree tree)
    {
        void Put(Hierarchy.HiveNode n)
        {
            AddClone(n.Account); // every depth and kind is family, not only directly supervised process slots
            if (n.Kind != Hierarchy.CloneKind.Subhive) return;
            SetHost(n.Account, n.Owner.Equals(Hierarchy.HiveTree.Root, StringComparison.OrdinalIgnoreCase) ? _main : n.Owner);
        }
        foreach (var n in tree.All()) Put(n);
        tree.NodePut += Put;
        tree.NodeRemoved += a => { if (HostOf(a) != null) SetHost(a, null); };
    }

    /// <summary>The hive driving <paramref name="body"/>, or null when it isn't a hosted subhive body.</summary>
    public string? HostOf(string body) { lock (_lock) return _hosts.GetValueOrDefault(body); }

    // caller holds _lock: who answers a line naming bot n. A hosted body → its host (main relays when the host is unreachable);
    // this bot itself → this bot (it is plainly online, whatever a stale roster says: live 10-29 420 ignored "420 come here");
    // an offline clone → main.
    private string Answerer(string n)
    {
        if (_hosts.TryGetValue(n, out var h))
            return h.Equals(_self, StringComparison.OrdinalIgnoreCase) || h.Equals(_main, StringComparison.OrdinalIgnoreCase) || _live.Contains(h) ? h : _main;
        if (n.Equals(_self, StringComparison.OrdinalIgnoreCase)) return n;
        return IsCloneName(n) && !_live.Contains(n) ? _main : n;
    }

    /// <summary>A newly allocated clone account (no-op when known). Rebuilds the address patterns and pushes the roster.</summary>
    public bool AddClone(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_lock)
        {
            if (name.Equals(_main, StringComparison.OrdinalIgnoreCase) || Clones.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
            Clones = [.. Clones, name.Trim()];
            _hiveBots.Add(name.Trim());
            Rebuild();
        }
        Changed?.Invoke();
        return true;
    }
    public TimeSpan StickyWindow { get; }
    /// <summary>
    /// How long a clone keeps a player's unnamed follow-ups after it last took a line from them or spoke to them (user
    /// requirement 10-27: unnamed chat is main's; a clone only continues its own conversation, ~30 s). Main's own sticky uses
    /// <see cref="StickyWindow"/>; a request main hands over (<see cref="HandOver"/>) keeps the longer of the two.
    /// </summary>
    public TimeSpan CloneWindow { get; }
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
            _delegated.Remove(player);
            _sticky[player] = (owner, _time.GetUtcNow() + (StickyWindow > CloneWindow ? StickyWindow : CloneWindow));
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Main: it just handed <paramref name="player"/>'s request to <paramref name="clone"/> (hive assign). The player's follow-ups
    /// for the next <see cref="CloneWindow"/> ("just like 5", "never mind") go to that clone, which is now in the conversation;
    /// unless another bot holds the player's thread right now (main waiting on an answer keeps it).
    /// </summary>
    public void Delegated(string player, string clone)
    {
        lock (_lock)
        {
            if (LiveThread(player) is { } holder && !string.Equals(holder, clone, StringComparison.OrdinalIgnoreCase)) return;
            var until = _time.GetUtcNow() + CloneWindow;
            _delegated[player] = (clone, until);
            _sticky[player] = (clone, until);
        }
        Changed?.Invoke();
    }

    // caller holds _lock: a live delegation of the player's request (owner online), else null.
    private string? LiveDelegation(string player)
    {
        if (!_delegated.TryGetValue(player, out var d)) return null;
        if (d.Until <= _time.GetUtcNow()) { _delegated.Remove(player); return null; }
        return string.Equals(d.Owner, _self, StringComparison.OrdinalIgnoreCase) || _live.Contains(d.Owner) ? d.Owner : null;
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
        return r.Explicit && !string.Equals(r.Owner, _self, StringComparison.OrdinalIgnoreCase) && !r.Names(_self);
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
                _sticky.Where(kv => kv.Value.Until > now).ToDictionary(kv => kv.Key, kv => new StickyMsg(kv.Value.Owner, kv.Value.Until.ToUnixTimeMilliseconds(),
                    _delegated.TryGetValue(kv.Key, out var d) && d.Until > now && string.Equals(d.Owner, kv.Value.Owner, StringComparison.OrdinalIgnoreCase))),
                _threads.Where(kv => kv.Value.Until > now).ToDictionary(kv => kv.Key, kv => new StickyMsg(kv.Value.Owner, kv.Value.Until.ToUnixTimeMilliseconds())),
                Clones.ToArray());
        }
    }

    /// <summary>Clone: adopt main's roster.</summary>
    public void Apply(RosterMsg r)
    {
        lock (_lock)
        {
            _main = r.Main;
            _hiveBots.Add(r.Main);
            // Dynamic accounts: main's roster names every clone (and the live ones at least).
            var known = (r.Clones ?? []).Concat(r.Live).Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals(r.Main, StringComparison.OrdinalIgnoreCase)
                && !Clones.Contains(n, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (known.Length > 0)
            {
                Clones = [.. Clones, .. known];
                foreach (var n in known) _hiveBots.Add(n);
            }
            _live = new HashSet<string>(r.Live, StringComparer.OrdinalIgnoreCase);
            _mainNames = r.MainNames;
            _sticky.Clear();
            _delegated.Clear();
            foreach (var (p, s) in r.Sticky)
            {
                _sticky[p] = (s.Owner, DateTimeOffset.FromUnixTimeMilliseconds(s.UntilMs));
                if (s.Delegated) _delegated[p] = _sticky[p];
            }
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
        var names = _mainNames.Concat([_main]).Concat(["clone", "bot", "maid chan", "maid", "sis"]).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length)
            .Select(n => string.Join(@"[\s_-]*", n.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)));
        string nameAlt = string.Join("|", names);
        var own = _mainNames.Concat([_main]).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length)
            .Select(n => string.Join(@"[\s_-]*", n.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)));
        _mainRegex = new Regex($@"(?<![A-Za-z0-9_])@?(?:{string.Join("|", own)})(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        _explicit.Clear();
        _bare.Clear();
        _number.Clear();
        foreach (var c in Clones)
        {
            var num = NumberOf(c);
            var user = string.Join(@"[\s_-]*", c.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
            var parts = new List<string> { $@"(?<![A-Za-z0-9_]){user}(?![A-Za-z0-9])" };
            if (num != null)
            {
                parts.Add($@"^[\s@#]*{num}(?![0-9])");                                  // "67, come here"
                _number[c] = new Regex($@"(?<![0-9A-Za-z_.,#-]){num}(?![0-9A-Za-z_]|[.,]-?[0-9]|,\s*-?[0-9]+\s*,\s*-?[0-9])", RegexOptions.CultureInvariant);
                parts.Add($@"(?<![A-Za-z0-9_])(?:{nameAlt})[\s_#-]*{num}(?![0-9])");    // "maid chan 420", "clone 69"
                parts.Add($@"(?<![A-Za-z0-9_])#{num}(?![0-9])");                         // "#1337"
                parts.Add($@"(?<![0-9A-Za-z_.,#-]){num}[\s,]+(?:maid|clone|bot|sis)(?![A-Za-z0-9])"); // "69 maid, ..."
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
        msg.Player != null && !IsHiveBot(msg.Player) && Live.Count > 0 && (IsGroupAddress(msg.Text) || Route(msg).Group);

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
            var named = AddressedBots(msg.Text, msg.Kind, out bool delegated);
            // A clone that isn't online can't answer: "69 bring me a water pail" while 69 is logged out is a request to main,
            // which spawns it or does it (live 3, 14:13:32: nobody answered until "maid 69?!").
            named = named.Select(Answerer).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Two or more bots named in one line ("67 and 69, come here", "hey 67 ... and maid chan"): each named bot hears
            // it, main coordinates (it owns the line and splits the work; the named clones answer their own part).
            if (named.Count >= 2) return new(_main, true, true, named);
            if (named.Count == 1) return new(named[0], true, false);
            // "and have clone 69 harvest and replant trees" / "have 69 come in and do the same": a clone as the OBJECT of a
            // request to main. Main is addressed (it delegates); before, such lines rode along as "(not to you)" context and
            // main later decided an unaddressed "go farm the trees" must have been meant for 69 (live 3, 14:24:16).
            if (delegated) return new(_main, true, false);
            return UnnamedRoute(msg.Player);
        }
    }

    /// <summary>A line naming no bot: the bot holding this player's thread or delegation, else their sticky partner (main or a live
    /// clone), else main. Caller holds <see cref="_lock"/>.</summary>
    private ChatRoute UnnamedRoute(string player)
    {
        // A bot that asked this player something (or is watching their demo) owns their replies until resolved/timeout.
        if (LiveThread(player) is { } holder) return new(holder, true, false);
        // Main just handed this player's request to a clone: their follow-ups ("just like 5") are that clone's.
        if (LiveDelegation(player) is { } doer) return new(doer, true, false);
        if (_sticky.TryGetValue(player, out var s) && s.Until > _time.GetUtcNow()
            && (string.Equals(s.Owner, _main, StringComparison.OrdinalIgnoreCase) || _live.Contains(s.Owner)))
            return new(s.Owner, false, false);
        return new(_main, false, false);
    }

    // Words after which a bot's name/number is a mention, not an address ("tell 67 to...", "get 67 cobble", "with 67?").
    private static readonly HashSet<string> RefWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "for", "with", "from", "than", "of", "at", "by", "about", "like", "and", "or", "nor", "is", "was", "are", "were", "be",
        "been", "am", "tell", "ask", "give", "gave", "send", "sent", "bring", "brought", "get", "got", "have", "has", "had", "let",
        "make", "made", "need", "needs", "want", "wants", "help", "told", "asked", "see", "saw", "call", "called", "me", "it", "that",
        "this", "the", "a", "an", "x", "only", "just", "around", "over", "under", "maybe", "exactly", "least", "most", "all", "did",
        "does", "do", "can", "could", "will", "would", "should", "where", "what", "who", "how", "whats", "wheres", "whos", "if",
        "when", "on", "in", "into", "onto", "my", "your", "his", "her", "their", "our", "take", "took", "use", "used", "mine", "dig",
        "collect", "grab", "fetch", "gather", "chop", "craft", "place", "put", "drop", "toss", "deposit", "withdraw", "leave", "keep",
        "spawn", "recall", "respawn", "summon", "pick", "picked", "not", "both", "either", "between", "via", "per",
    };
    // Address words: a bare clone number right after one is a vocative ("hello 67?", "hey 69, just deliver tools", "ok 67 go",
    // "maid 69?!", "good job 67!"). Live 3: a bare number counts ONLY next to one of these or at the start of a line/sentence.
    private static readonly HashSet<string> AddressWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "hey", "hi", "hello", "yo", "oi", "hiya", "heya", "sup", "ok", "okay", "thanks", "thank", "thx", "ty", "please", "pls", "plz",
        "sorry", "dear", "nice", "gj", "gm", "gn", "bye", "cya", "wb", "welcome", "oy", "ayo", "yay",
        "maid", "maids", "chan", "clone", "bot", "sis", "sister", "job", "done", "work",
    };
    // Address words that may also FOLLOW the number ("69 maid, ..." / "67 chan").
    private static readonly HashSet<string> TrailingAddress = new(StringComparer.OrdinalIgnoreCase) { "maid", "chan", "clone", "bot", "sis" };
    // A number after these is a vocative only when a comma/colon follows ("also 67, this block is yours", not "and 69 will").
    private static readonly HashSet<string> Discourse = new(StringComparer.OrdinalIgnoreCase)
    {
        "also", "so", "and", "but", "now", "then", "well", "alright", "oh", "um", "uh", "anyway", "btw", "wait", "right", "yes", "yeah",
        "yea", "no", "nope", "good", "great",
    };
    // A clone right after these, in an imperative to the hive, is the object of a request to main ("have clone 69 harvest").
    private static readonly HashSet<string> DelegationVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "have", "tell", "ask", "send", "let", "make", "spawn", "respawn", "recall", "summon", "call",
    };
    // What may come before that verb: fillers, "can you", main's address ("maid, have 69 ...").
    private static readonly HashSet<string> ImperativeLead = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "also", "so", "ok", "okay", "now", "then", "please", "pls", "plz", "can", "could", "would", "will", "you", "u", "ya",
        "maid", "chan", "main", "hey", "yo", "oh", "btw", "next", "just", "go", "lets", "let's",
    };
    private static readonly Regex LastWord = new(@"([A-Za-z']+)[^A-Za-z0-9']*$", RegexOptions.CultureInvariant);
    private static readonly Regex FirstWord = new(@"^\s*([A-Za-z]+)(?![A-Za-z])", RegexOptions.CultureInvariant);
    private static readonly Regex Joiner = new(@"^\s*(?:,|&|\+|and|n|or)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CoordBefore = new(@"-?[0-9]+\s*,\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex WordRx = new(@"[A-Za-z']+", RegexOptions.CultureInvariant);

    private bool IsCloneName(string bot) => Clones.Contains(bot, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every hive bot addressed in the line, in order of first mention (all configured clones and main; Route sends an address
    /// to an offline clone to main). A clone's login/"maid chan 67"/"#67"/"clone 67" and main's names address it anywhere
    /// unless a mention word precedes them ("tell maid chan 67 ..."). A bare clone number is an address ONLY at the start of
    /// a line or sentence ("69 bring me...", "69, ...", "nice. 69 can you"), right after an address word ("hey 69", "maid 69",
    /// "good job 67!"), right before one ("69 maid, ..."), after a discourse word with a comma ("also 67, ..."), or chained
    /// to an addressed bot ("67 and 69"); an ordinary number in a sentence never is ("I counted 69!", "get 67 cobble",
    /// "with 67?", "and 69 will be on crafting duty", "6,67,8"). Caller holds <see cref="_lock"/>.
    /// </summary>
    private List<string> AddressedBots(string text, ChatKind kind, out bool delegated)
    {
        delegated = false;
        var cands = Candidates(text, kind);
        var named = new List<string>();
        int prevEnd = -1;
        foreach (var x in cands)
        {
            if (x.Index < prevEnd) continue; // overlaps the previous addressed hit
            if (!IsAddress(text, x.Index, x.End, x.Strong, prevEnd))
            {
                if (IsCloneName(x.Bot) && IsDelegation(text[..x.Index])) delegated = true;
                continue;
            }
            prevEnd = x.End;
            if (!named.Contains(x.Bot, StringComparer.OrdinalIgnoreCase)) named.Add(x.Bot);
        }
        return named;
    }

    /// <summary>Every mention of a hive bot in the line, in order (strong before weak at one spot); a weak (bare number) hit inside a
    /// strong one is the same mention ("maid chan 67", "67, come" at the start) and is left out.</summary>
    private List<(string Bot, int Index, int End, bool Strong)> Candidates(string text, ChatKind kind)
    {
        var cands = new List<(string Bot, int Index, int End, bool Strong)>();
        foreach (var c in Clones) CloneCandidates(c, text, kind, cands); // offline clones too: an address to one goes to main (Route)
        var clonesStrong = cands.Where(x => x.Strong).ToList();
        foreach (Match m in _mainRegex.Matches(text))
            if (!clonesStrong.Any(x => x.Index < m.Index + m.Length && m.Index < x.End)) // "maid chan 69" is 69, not main
                cands.Add((_main, m.Index, m.Index + m.Length, true));
        if (MainVocative.Match(text) is { Success: true } mv) cands.Add((_main, mv.Index, mv.Index + mv.Length, true)); // "main spin"
        var strong = cands.Where(x => x.Strong).ToList();
        return cands.Where(x => x.Strong || !strong.Any(y => y.Index <= x.Index && x.End <= y.End))
            .OrderBy(x => x.Index).ThenByDescending(x => x.Strong).ToList();
    }

    /// <summary>One clone's mentions: its login/explicit names (strong), its bare number (weak), and in an emote its bare name (strong).</summary>
    private void CloneCandidates(string c, string text, ChatKind kind, List<(string Bot, int Index, int End, bool Strong)> cands)
    {
        foreach (Match m in _explicit[c].Matches(text)) cands.Add((c, m.Index, m.Index + m.Length, true));
        if (_number.TryGetValue(c, out var n))
            foreach (Match m in n.Matches(text)) cands.Add((c, m.Index, m.Index + m.Length, false));
        if (kind == ChatKind.Emote && _bare.TryGetValue(c, out var b))
            foreach (Match m in b.Matches(text)) cands.Add((c, m.Index, m.Index + m.Length, true)); // "* kiwi pats 67"
    }

    private static bool IsAddress(string text, int index, int end, bool strong, int prevAddressedEnd)
    {
        var before = text[..index];
        if (StartsClause(before)) return true;
        if (prevAddressedEnd >= 0 && prevAddressedEnd <= index && Joiner.IsMatch(text[prevAddressedEnd..index])) return true; // "67 and 69"
        var lw = LastWord.Match(before) is { Success: true } w ? w.Groups[1].Value.Trim('\'') : "";
        if (strong) return !RefWords.Contains(lw);
        return IsBareNumberAddress(text, before, end, lw);
    }

    /// <summary>The mention starts the line or a sentence.</summary>
    private static bool StartsClause(string before)
    {
        var trimmed = before.TrimEnd().TrimStart(' ', '\t', '@', '#');
        return trimmed.Length == 0 || trimmed[^1] is '.' or '!' or '?' or ';';
    }

    /// <summary>A bare clone number (after <paramref name="lw"/>) is an address next to an address word, or after a discourse word
    /// with a comma/colon; never in coordinates or as an ordinary number in a sentence.</summary>
    private static bool IsBareNumberAddress(string text, string before, int end, string lw)
    {
        if (CoordBefore.IsMatch(before)) return false;                              // "14, 67, -5"
        if (AddressWords.Contains(lw)) return true;                                 // "hey 69", "maid 69?!"
        var after = text[end..];
        if (FirstWord.Match(after) is { Success: true } fw && TrailingAddress.Contains(fw.Groups[1].Value)) return true; // "69 maid"
        char next = after.TrimStart() is { Length: > 0 } a ? a[0] : '\0';
        return Discourse.Contains(lw) && (next is ',' or ':');
    }

    /// <summary>
    /// The clone after <paramref name="before"/> is the object of a request to main: its clause ends in a delegation verb
    /// ("have", "tell", "send", "spawn"...) and everything before the verb is filler ("and", "can you", "maid,"): an
    /// imperative to the hive. "I have 69 logs" is not ("I" isn't filler).
    /// </summary>
    private static bool IsDelegation(string before)
    {
        int cut = before.LastIndexOfAny(['.', '!', '?', ';']);
        var words = WordRx.Matches(before[(cut + 1)..]).Select(m => m.Value.Trim('\'')).Where(x => x.Length > 0).ToList();
        if (words.Count == 0 || !DelegationVerbs.Contains(words[^1])) return false;
        return words.Take(words.Count - 1).All(ImperativeLead.Contains);
    }

    /// <summary>
    /// Clone hook (<see cref="ChatRouter.ExplicitlyAddressed"/>): THIS bot was named in the message ("67, ...").
    /// </summary>
    public bool AddressesSelf(ChatMessage msg) =>
        Route(msg) is { Explicit: true } r && (!r.Group && string.Equals(r.Owner, _self, StringComparison.OrdinalIgnoreCase) || r.Names(_self));

    /// <summary>
    /// ChatRouter hook (<see cref="ChatRouter.Arbiter"/>): whether THIS bot handles the message. An explicitly addressed clone
    /// always takes it; otherwise the owner (sticky partner, else main) takes it only if its own router also considers it
    /// addressed (reply window, solo player, name) — so "poofimafox look at this" goes to nobody.
    /// </summary>
    public bool Decide(ChatMessage msg, bool locallyAddressed)
    {
        var r = Route(msg);
        var (owner, isExplicit) = (r.Owner, r.Explicit);
        if (owner == null || msg.Player == null) return false;
        // A line naming several bots: every named bot answers its part; main (the owner) coordinates and keeps the sticky.
        if (r.Group && r.Names(_self) && !IsMain) return true;
        bool mine = string.Equals(owner, _self, StringComparison.OrdinalIgnoreCase);
        bool handle = mine && (isExplicit || locallyAddressed);
        NoteDecision(msg, msg.Player, r, owner, handle);
        return handle;
    }

    /// <summary>The sticky partner and group hold after <see cref="Decide"/>; an explicit address taken here raises <see cref="AddressedHere"/>.</summary>
    private void NoteDecision(ChatMessage msg, string player, ChatRoute r, string owner, bool handle)
    {
        if (handle) Accept(player, _self);
        if (handle && r.Explicit) { try { AddressedHere?.Invoke(msg); } catch (Exception) { } }
        else if (IsMain && r.Explicit) Accept(player, owner); // main is the sticky authority (pushed to clones)
        if (IsMain && r.Group) lock (_lock) _groupHold[player] = _time.GetUtcNow() + GroupHold;
    }

    /// <summary>A player named THIS bot (or a body it drives) and this bot takes the line: a direct order (pre-empts a logout).</summary>
    public event Action<ChatMessage>? AddressedHere;

    /// <summary>How long after a group/multi-named line main ignores clones' chat_ok for that player (they all answered it).</summary>
    public static readonly TimeSpan GroupHold = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, DateTimeOffset> _groupHold = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Main: a clone reports it took a player's message (chat_ok). Ignored right after a line naming several bots: each named
    /// clone answers its part, but the conversation stays with main (otherwise the sticky flips to whichever clone was last).
    /// </summary>
    public void AcceptFromClone(string player, string clone)
    {
        lock (_lock)
            if (_groupHold.TryGetValue(player, out var until) && until > _time.GetUtcNow()) return;
        Accept(player, clone);
    }

    /// <summary>Records that <paramref name="owner"/> is talking with <paramref name="player"/>.</summary>
    public void Accept(string player, string owner)
    {
        var window = string.Equals(owner, _main, StringComparison.OrdinalIgnoreCase) ? StickyWindow : CloneWindow;
        lock (_lock)
        {
            // A handed-over request keeps its longer lease: a quick "ok" from the clone must not shorten it.
            var until = _time.GetUtcNow() + window;
            if (_sticky.TryGetValue(player, out var cur) && string.Equals(cur.Owner, owner, StringComparison.OrdinalIgnoreCase) && cur.Until > until) until = cur.Until;
            _sticky[player] = (owner, until);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Main: a clone spoke to <paramref name="player"/> (its line was addressed to them): their unnamed follow-ups continue
    /// with that clone for <see cref="CloneWindow"/> — unless another bot holds the player's conversation right now.
    /// </summary>
    public void SpokeFromClone(string player, string clone)
    {
        lock (_lock)
        {
            if (_groupHold.TryGetValue(player, out var until) && until > _time.GetUtcNow()) return;
            if (LiveThread(player) is { } holder && !string.Equals(holder, clone, StringComparison.OrdinalIgnoreCase)) return;
            if (_sticky.TryGetValue(player, out var s) && s.Until > _time.GetUtcNow() && !string.Equals(s.Owner, clone, StringComparison.OrdinalIgnoreCase)) return;
        }
        Accept(player, clone);
    }

    /// <summary>A clone left: its sticky conversations and thread leases fall back to main.</summary>
    public void Forget(string owner)
    {
        lock (_lock)
        {
            foreach (var k in _sticky.Where(kv => string.Equals(kv.Value.Owner, owner, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList()) _sticky.Remove(k);
            foreach (var k in _threads.Where(kv => string.Equals(kv.Value.Owner, owner, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList()) _threads.Remove(k);
            foreach (var k in _delegated.Where(kv => string.Equals(kv.Value.Owner, owner, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList()) _delegated.Remove(k);
        }
    }
}

/// <summary>Arbitration verdict for one player message.</summary>
/// <param name="Addressed">Every bot named in the line when it named several (owner is then main, <paramref name="Group"/> true).</param>
public sealed record ChatRoute(string? Owner, bool Explicit, bool Group, IReadOnlyList<string>? Addressed = null)
{
    public bool Names(string bot) => Addressed?.Contains(bot, StringComparer.OrdinalIgnoreCase) == true;
}
