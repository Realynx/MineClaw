using System.Text.RegularExpressions;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Brainstem;

/// <summary>A player's item hand-over the bot picked up: everything that arrived within the debounce, per item.</summary>
public sealed record Handover(string Player, IReadOnlyList<(int ItemId, int Count)> Items, DateTimeOffset At)
{
    public string ItemsText => string.Join(", ", Items.Select(i => $"{i.Count} {HandoverWatch.ItemWord(i.ItemId, i.Count)}"));
}

/// <summary>
/// A spoken promise the brain made, waiting for its condition ("I'll make bread the second you hand it over", "once I get the
/// logs I'll build the floor"). <see cref="ItemId"/> = what has to arrive (null = any hand-over from <see cref="From"/>).
/// </summary>
public sealed record PendingPromise(string Intent, string Condition, string? From, int? ItemId, int MinCount, int HaveAtPromise, DateTimeOffset At)
{
    /// <summary>True: the condition is a hand-over from <see cref="From"/>; false: the bot getting the item by any means.</summary>
    public bool ByHandover { get; init; }
}

/// <summary>
/// Zero-token brainstem watch for two kinds of wake the brain otherwise misses (live 10-27 21:04: Fox said "I have wheat come
/// here I'll give it to you, make bread", the bot said "I'll make bread the second you hand it over", the ledger booked
/// +21 wheat at 21:04:29 and nothing woke the brain for 40 s until Fox typed "gave you the wheat"):
/// <list type="bullet">
/// <item><b>Hand-overs.</b> An item entity that spawns at a player's eye height (a toss: EntityHuman.a(ItemStack, boolean) spawns
/// it at locY - 0.3 + 1.62) or just in front of them along their look is attributed to that player; when the bot picks it up,
/// stacks arriving within <see cref="Debounce"/> become one event: "PoofimaFox gave you 21 wheat". Drops near a block that was
/// just broken (the bot's own mining) and bots' tosses (hive hand-offs report themselves) never count.</item>
/// <item><b>Promises.</b> The bot's own chat line "I'll X when/once/the second Y" (or "once Y, I'll X") is parsed in C# into a
/// pending expectation; the item comes from the condition, else the partner's last line, else the recipe of what X makes.
/// When it arrives the brain is woken with the original intent ("you said you'd make bread when PoofimaFox handed it over;
/// you now have 21 wheat"). No LLM call.</item>
/// </list>
/// Thread-safe; the host feeds it (spawns, pickups, block breaks, chat) and polls it.
/// </summary>
public sealed class HandoverWatch(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly Dictionary<int, (string Player, DateTimeOffset At, bool Counted)> _thrown = new();
    private readonly List<(BlockPos Pos, DateTimeOffset At)> _breaks = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingPromise> _promises = new();
    private readonly Dictionary<string, (string Text, DateTimeOffset At)> _lastLine = new(StringComparer.OrdinalIgnoreCase);
    private (string Player, DateTimeOffset At)? _partner;

    private sealed class Bucket
    {
        public readonly Dictionary<int, int> Items = new();
        /// <summary>Counted tosses (entity → stack) not picked up by me yet; <see cref="Taken"/>: picked up by someone else.</summary>
        public readonly Dictionary<int, (int Item, int Count)> Unpicked = new(), Taken = new();
        public DateTimeOffset Last;
    }

    /// <summary>
    /// Tosses/stacks of one hand-over within this window = one event (live 10-27 23:31:06-10: planks, 10 pressure plates and
    /// 12 fences thrown over 4 s, never acknowledged).
    /// </summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>How long a spoken promise waits for its condition.</summary>
    public TimeSpan PromiseLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A player as the watch needs them: feet position and look (degrees, Minecraft convention).</summary>
    public readonly record struct PlayerView(string Name, Vec3d Feet, float Yaw, float Pitch);

    // ------------------------------------------------------------------ world inputs

    /// <summary>A block became air (dig/harvest): drops spawning next to it are block drops, not tosses.</summary>
    public void OnBlockGone(BlockPos pos)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            _breaks.Add((pos, now));
            _breaks.RemoveAll(b => now - b.At > TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// An item entity appeared (0x15). <paramref name="players"/> = the other (non-bot) players around. With the stack known
    /// (<paramref name="itemId"/> &gt; 0) the toss itself counts as the hand-over: it wakes the brain once the tosses settle even
    /// if the bot never picks the items up (live 10-27 23:31: fences thrown at the bot standing in the gen were never collected,
    /// so the pickup-only watch never woke anyone).
    /// </summary>
    public string? OnItemSpawn(int entityId, Vec3d pos, IEnumerable<PlayerView> players, int itemId = 0, int count = 0)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_breaks.Any(b => now - b.At <= TimeSpan.FromSeconds(2) && b.Pos.Center.DistanceTo(pos) <= 1.6)) return null;
            var who = Thrower(pos, players);
            if (who is null) return null;
            bool counted = itemId > 0;
            _thrown[entityId] = (who, now, counted);
            foreach (var k in _thrown.Where(t => now - t.Value.At > TimeSpan.FromMinutes(5)).Select(t => t.Key).ToList()) _thrown.Remove(k);
            if (counted)
            {
                AddLocked(who, itemId, count, now);
                _buckets[who].Unpicked[entityId] = (itemId, Math.Max(1, count));
            }
            return who;
        }
    }

    /// <summary>Which single player threw an item spawning at <paramref name="item"/>: at their eye height right where they stand,
    /// or a little ahead along their look (the spawn packet can lag the toss by a tick or two). Null = nobody / ambiguous.</summary>
    public static string? Thrower(Vec3d item, IEnumerable<PlayerView> players)
    {
        string? best = null;
        double bestScore = double.MaxValue;
        int hits = 0;
        foreach (var p in players)
        {
            if (ThrowScore(item, p) is not { } score) continue;
            hits++;
            if (score < bestScore) { bestScore = score; best = p.Name; }
        }
        return hits >= 1 ? best : null;
    }

    /// <summary>How well <paramref name="p"/> fits as the thrower (lower = better): the toss itself at their eye height, or a
    /// little ahead along their look; null when they couldn't have thrown it.</summary>
    private static double? ThrowScore(Vec3d item, PlayerView p)
    {
        double dx = item.X - p.Feet.X, dz = item.Z - p.Feet.Z, dy = item.Y - p.Feet.Y;
        double h = Math.Sqrt(dx * dx + dz * dz);
        if (h <= 0.6 && dy is > 0.9 and < 1.8) return h; // the toss itself: locY - 0.3 + 1.62 ≈ +1.32
        double yaw = p.Yaw * Math.PI / 180;
        double fx = -Math.Sin(yaw), fz = Math.Cos(yaw);
        double along = dx * fx + dz * fz, side = Math.Abs(-dx * fz + dz * fx);
        if (along is > 0 and <= 3.0 && side <= 1.0 && dy is > -1.6 and < 1.8) return 1 + side + Math.Abs(along - 1);
        return null;
    }

    /// <summary>The bot picked up <paramref name="entityId"/>; true when it was a hand-over (added to that player's bucket).</summary>
    public bool OnPickup(int entityId, int itemId, int count)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (!_thrown.Remove(entityId, out var t)) return false;
            if (t.Counted)
            {
                if (_buckets.TryGetValue(t.Player, out var open)) { open.Last = now; open.Unpicked.Remove(entityId); }
                return true; // the toss already counted it
            }
            AddLocked(t.Player, itemId, count, now);
            return true;
        }
    }

    /// <summary>Someone else (a player, another hive body) picked up <paramref name="entityId"/>: if it was a toss to me, it never
    /// reached my inventory.</summary>
    public void OnTakenByOther(int entityId)
    {
        lock (_lock)
        {
            if (!_thrown.Remove(entityId, out var t) || !t.Counted || !_buckets.TryGetValue(t.Player, out var b)) return;
            if (b.Unpicked.Remove(entityId, out var st)) b.Taken[entityId] = st;
        }
    }

    // caller holds _lock
    private void AddLocked(string player, int itemId, int count, DateTimeOffset now)
    {
        if (!_buckets.TryGetValue(player, out var b)) _buckets[player] = b = new Bucket();
        b.Items[itemId] = b.Items.GetValueOrDefault(itemId) + Math.Max(1, count);
        b.Last = now;
    }

    // ------------------------------------------------------------------ chat inputs

    /// <summary>An addressed player line (kept as context for the bot's next promise: "I have wheat, make bread").</summary>
    public void OnPlayerLine(string player, string text)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            _lastLine[player] = (text, now);
            _partner = (player, now);
        }
    }

    private static readonly Regex PromiseAfter = new(
        @"\b(?:i'?ll|i\s+will|will|gonna|going\s+to)\s+(?<act>(?:\w+[\s']+){0,5}?\w+)\s*,?\s+(?:(?:right\s+)?when|once|as\s+soon\s+as|the\s+(?:second|moment|minute|instant)|after|whenever)\s+(?<cond>[^.!?;]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PromiseBefore = new(
        @"\b(?:(?:right\s+)?when|once|as\s+soon\s+as|the\s+(?:second|moment)|after)\s+(?<cond>[^,.!?;]+),\s*(?:then\s+)?(?:i'?ll|i\s+will|i'?m\s+gonna)\s+(?<act>[^.!?;]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HandoverCond = new(
        @"\byou\s+(?:\w+\s+)?(?:hand|give|toss|throw|drop|pass|bring|send|get)\w*\b|\b(?:i\s+)?(?:get|receive)s?\s+(?:it|them)\s+from\s+you\b|\bit\s+(?:arrives|lands|gets\s+here)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GetCond = new(
        @"\b(?:i|we)\s+(?:get|have|got|grab|collect|find|gather|chop|mine)\s+(?:the\s+|some\s+|enough\s+|my\s+)?(?<n>\d+\s+)?(?<what>[\w ]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The bot's own chat line: a promise with a condition becomes a pending expectation (one per line, newest wins for the
    /// same item). Null = no promise in it.
    /// </summary>
    public PendingPromise? OnBotLine(string text, Func<int, int> have)
    {
        if (PromiseParts(text) is not { } parts) return null;
        var (act, cond) = parts;
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            string? partner = _partner is { } pt && now - pt.At <= TimeSpan.FromMinutes(3) ? pt.Player : null;
            bool handover = HandoverCond.IsMatch(cond);
            int min = 1;
            int? item = PromisedItem(act, cond, handover, partner, now, ref min);
            if (!handover && item is null) return null; // "once I'm done" etc.: nothing concrete to watch for
            var p = new PendingPromise(act, cond, handover ? partner : null, item, min, item is { } id ? have(id) : 0, now) { ByHandover = handover };
            _promises.RemoveAll(x => x.ItemId == p.ItemId && string.Equals(x.From, p.From, StringComparison.OrdinalIgnoreCase));
            _promises.Add(p);
            return p;
        }
    }

    /// <summary>
    /// The item a promise waits for: the one the bot itself gets ("once I get 3 logs"), else for "make bread when you hand it
    /// over" the one the condition names, the partner offered ("I have wheat"), or the recipe's input. Caller holds _lock.
    /// </summary>
    private int? PromisedItem(string act, string cond, bool handover, string? partner, DateTimeOffset now, ref int min)
    {
        int? item = handover ? null : GottenItem(cond, ref min);
        return item ?? ItemNamedIn(act, cond) ?? PartnerOffered(act, partner, now) ?? RecipeInput(act);
    }

    /// <summary>The promised act and its condition ("make bread" / "you hand it over"), either order; null when the line holds none.</summary>
    private static (string Act, string Cond)? PromiseParts(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = PromiseAfter.Match(text);
        if (!m.Success) m = PromiseBefore.Match(text);
        if (!m.Success) return null;
        string act = Clean(m.Groups["act"].Value), cond = Clean(m.Groups["cond"].Value);
        return act.Length == 0 || cond.Length == 0 ? null : (act, cond);
    }

    /// <summary>"once I get 3 logs": the item (and how many, into <paramref name="min"/>) the bot itself gets.</summary>
    private static int? GottenItem(string cond, ref int min)
    {
        if (GetCond.Match(cond) is not { Success: true } g) return null;
        var item = HandoffItem(g.Groups["what"].Value);
        if (int.TryParse(g.Groups["n"].Value.Trim(), out var n) && n > 0) min = n;
        return item;
    }

    /// <summary>An item the condition names, unless it's what the act makes.</summary>
    private static int? ItemNamedIn(string act, string cond) =>
        Perception.HandoffIntent.ItemIn(cond) is { } c && !IsProductOf(act, c) ? c : null;

    /// <summary>The item the partner offered in their last line (within 3 minutes), unless it's what the act makes.</summary>
    private int? PartnerOffered(string act, string? partner, DateTimeOffset now)
    {
        if (partner is null || !_lastLine.TryGetValue(partner, out var line) || now - line.At > TimeSpan.FromMinutes(3)) return null;
        return ItemsIn(line.Text).FirstOrDefault(i => !IsProductOf(act, i), -1) is var li and >= 0 ? li : null;
    }

    public IReadOnlyList<PendingPromise> Promises { get { lock (_lock) return _promises.ToList(); } }

    // ------------------------------------------------------------------ output

    /// <summary>
    /// Settled hand-overs (no new stack for <see cref="Debounce"/>) and promises whose condition is now met, as one wake text each:
    /// "PoofimaFox gave you 21 wheat; you said you'd make bread when PoofimaFox handed it over (now 21 wheat)" /
    /// "you said you'd build the floor once you got the logs; you now have 12 oak log". <paramref name="have"/> = inventory count.
    /// </summary>
    public List<(string Text, string? Player)> Poll(Func<int, int> have)
    {
        var now = _time.GetUtcNow();
        var outp = new List<(string, string?)>();
        lock (_lock)
        {
            _promises.RemoveAll(p => now - p.At > PromiseLifetime);
            foreach (var (player, b) in _buckets.Where(kv => now - kv.Value.Last >= Debounce).ToList())
            {
                _buckets.Remove(player);
                outp.Add((HandoverText(player, b) + (KeptPromise(player, b, have) ?? AckHint), player));
            }
            // An open bucket may still carry the promised item: wait for it to settle so it's one wake, not two.
            if (_buckets.Count == 0)
                foreach (var p in _promises.Where(p => p.ItemId is { } id && have(id) >= Math.Max(p.MinCount, p.HaveAtPromise + 1)).ToList())
                {
                    _promises.Remove(p);
                    outp.Add((MetPromiseText(p, have), p.From));
                }
        }
        return outp;
    }

    /// <summary>"PoofimaFox gave you 21 wheat", or what was tossed and still lies on the ground / went to someone else. Only
    /// items actually picked up count as received.</summary>
    private static string HandoverText(string player, Bucket b)
    {
        var got = new Dictionary<int, int>(b.Items);
        foreach (var (item, n) in b.Unpicked.Values.Concat(b.Taken.Values)) got[item] = got.GetValueOrDefault(item) - n;
        var h = new Handover(player, got.Where(kv => kv.Value > 0).Select(kv => (kv.Key, kv.Value)).ToList(), b.Last);
        var ground = Stacks(b.Unpicked.Values);
        var taken = Stacks(b.Taken.Values);
        var text = h.Items.Count > 0 ? $"{player} gave you {h.ItemsText}"
            : ground.Count > 0 ? $"{player} tossed you {Text(ground)}" : $"{player} tossed {Text(taken)} toward you";
        if (ground.Count > 0)
            text += (h.Items.Count > 0 ? $"; {Text(ground)} still" : ", but it's still") + " on the ground: not in your inventory until you pick it up (collect_drops)";
        if (taken.Count > 0) text += $"; {Text(taken)} was picked up by someone else, not you";
        return text;
    }

    /// <summary>The promise this hand-over keeps ("; you said you'd make bread when PoofimaFox handed it over (now 21 wheat)"),
    /// removed; null when none. Caller holds _lock.</summary>
    private string? KeptPromise(string player, Bucket b, Func<int, int> have)
    {
        var kept = _promises.FirstOrDefault(p => (p.From is null || p.From.Equals(player, StringComparison.OrdinalIgnoreCase))
                                                  && (p.ItemId is null || b.Items.ContainsKey(p.ItemId.Value)));
        if (kept is null) return null;
        _promises.Remove(kept);
        return $"; you said you'd {kept.Intent} when {player} handed it over" + (kept.ItemId is { } id ? $" (now {have(id)} {ItemWord(id, have(id))})" : "");
    }

    /// <summary>"you said you'd build the floor once you got the logs; you now have 12 oak log".</summary>
    private static string MetPromiseText(PendingPromise p, Func<int, int> have)
    {
        int n = have(p.ItemId!.Value);
        string cond = p.ByHandover ? (p.From is { } f ? $"when {f} handed it over" : "once you got it") : "once " + Second(p.Condition);
        return $"you said you'd {p.Intent} {cond}; you now have {n} {ItemWord(p.ItemId.Value, n)}";
    }

    private static List<(int Item, int Count)> Stacks(IEnumerable<(int Item, int Count)> stacks) =>
        stacks.GroupBy(x => x.Item).Select(g => (g.Key, g.Sum(x => x.Count))).ToList();

    private static string Text(List<(int Item, int Count)> stacks) => string.Join(", ", stacks.Select(i => $"{i.Count} {ItemWord(i.Item, i.Count)}"));

    /// <summary>Appended to a plain hand-over wake: one cheap thank-you ("ty Foxy, got 12 fences!"), nothing more.</summary>
    public const string AckHint = " (thank them in one short line)";

    // ------------------------------------------------------------------ helpers

    private static string Clean(string s) => Regex.Replace(s.Trim().TrimEnd(',', ' ', '~'), @"\s+(?:uwu|owo|nya|~+)$", "", RegexOptions.IgnoreCase).Trim();

    /// <summary>"I get the logs" → "you get the logs" (the wake is written to the bot in the second person).</summary>
    private static string Second(string cond) =>
        Regex.Replace(Regex.Replace(cond, @"\bi\b", "you", RegexOptions.IgnoreCase), @"\bmy\b", "your", RegexOptions.IgnoreCase);

    private static int? HandoffItem(string words)
    {
        var w = Regex.Replace(words.Trim(), @"\s+(?:back|first|now|then|here|over).*$", "", RegexOptions.IgnoreCase);
        return Perception.HandoffIntent.ItemIn(w);
    }

    private static IEnumerable<int> ItemsIn(string text)
    {
        foreach (Match w in Regex.Matches(text.ToLowerInvariant(), "[a-z_]{3,}"))
            if (Perception.HandoffIntent.ItemIn(w.Value) is { } id) yield return id;
    }

    /// <summary>"make bread" → bread is the product, not the awaited item.</summary>
    private static bool IsProductOf(string act, int item) => ItemsIn(act).Contains(item);

    /// <summary>The main input of what the action makes ("make bread" → wheat, "craft planks" → log).</summary>
    private static int? RecipeInput(string act)
    {
        foreach (var product in ItemsIn(act))
        {
            var r = Recipes.ForOutput(product, -1).FirstOrDefault();
            if (r is null) continue;
            var input = r.Totals.OrderByDescending(t => t.Count).Select(t => (int?)t.Ingredient.Id).FirstOrDefault();
            if (input is not null) return input;
        }
        return null;
    }

    public static string ItemWord(int id, int count)
    {
        var name = Items.Exists(id) ? Items.Get(id).Name : Blocks.Exists(id) ? Blocks.Get(id).Name : "item " + id;
        name = name.Replace('_', ' ');
        return name;
    }
}
