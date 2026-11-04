using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Character;

/// <summary>
/// Prepares in-character lines in the background (docs/design/reactive-character.md): when the context changed or a likely
/// moment ran out of lines, ONE cheap batched call (the memory tier: this is wording, never script authoring) writes a few
/// lines per moment for the current context — "what are you doing?", greeting Fox, thanks, an "ow!", "omw" — in the persona's
/// voice, coloured by mood and each player's relationship (the [self] block), avoiding what was said lately. Every line is
/// linted; the bank tags them with the context so they expire when it changes. Bounded: one call in flight, at most
/// <see cref="CharacterOptions.MaxPrefetchPerHour"/>, never while the budget is paused or nobody is online.
/// </summary>
public sealed class LinePrefetcher
{
    public const string Purpose = "line_prefetch";

    private readonly LineBank _bank;
    private readonly ILlmClient _llm;
    private readonly UsageTracker _usage;
    private readonly CharacterOptions _o;
    private readonly Func<string> _persona;
    private readonly Func<string> _name;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly Queue<DateTimeOffset> _calls = new();
    private int _inFlight;

    public LinePrefetcher(LineBank bank, ILlmClient llm, UsageTracker usage, CharacterOptions options, Func<string> persona, Func<string> name,
        TimeProvider? time = null, ILogger? log = null)
    {
        _bank = bank;
        _llm = llm;
        _usage = usage;
        _o = options;
        _persona = persona;
        _name = name;
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>The live context (null = nothing to prepare for).</summary>
    public Func<CharacterContext?>? Context { get; set; }

    /// <summary>The [self] block for these players (mood, relationships, nicknames), or null.</summary>
    public Func<IReadOnlyList<string>, string?>? Self { get; set; }

    /// <summary>Prefetch calls made (telemetry, tests).</summary>
    public int Calls { get; private set; }

    /// <summary>(moment, player) keys the current context still needs lines for.</summary>
    public IReadOnlyList<(string Moment, string? Player)> Needed(CharacterContext ctx)
    {
        var list = new List<(string, string?)>();
        foreach (var m in _o.Moments)
        {
            if (m is "greet" or "bye")
            {
                foreach (var p in ctx.Players.Take(3))
                    if (_bank.Count(m, p) == 0) list.Add((m, p));
            }
            else if (_bank.Count(m) == 0) list.Add((m, null));
        }
        return list;
    }

    /// <summary>One refresh: sync the bank's context, and if moments are missing (and the budget allows) one call. Returns lines added.</summary>
    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        if (MissingMoments(out var ctx, out var sig) is not { } need) return 0;
        if (!TryReserveCall()) return 0;
        try
        {
            string? self = null;
            try { self = Self?.Invoke(ctx.Players); } catch (Exception) { }
            var response = await _llm.CompleteAsync(new LlmRequest
            {
                Tier = ModelTier.Cheap,
                System = [new LlmSystemBlock(SystemPrompt(_name(), _persona(), self, _o))],
                Messages = [LlmMessage.User(UserPrompt(ctx, need, _bank.RecentSaid(), _o.LinesPerMoment))],
                MaxTokens = Math.Max(120, _o.MaxTokens),
                CacheHistory = false,
                Purpose = Purpose,
            }, ct).ConfigureAwait(false);
            Calls++;
            _usage.Record(response.Model, response.Usage, ModelTier.Cheap);
            if (_bank.Context != sig) return 0; // the world moved on while it was writing
            int added = AddLines(response.Text, need, ctx, sig);
            _log?.LogInformation("line prefetch: {Added} line(s) for {Moments} ({Context})", added, string.Join(",", need.Select(n => n.Moment + (n.Player != null ? "@" + n.Player : ""))), ctx.Activity);
            return added;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log?.LogDebug(ex, "line prefetch failed");
            return 0;
        }
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    }

    /// <summary>
    /// Syncs the bank's context, then the moments to prefetch: null when prefetch is off, nobody is around, nothing is missing or
    /// the budget is tight.
    /// </summary>
    private IReadOnlyList<(string Moment, string? Player)>? MissingMoments(out CharacterContext ctx, out string sig)
    {
        ctx = CharacterContext.Idle;
        sig = "";
        if (!_o.Prefetch || Context?.Invoke() is not { } current) return null;
        ctx = current;
        sig = ctx.Signature;
        int dropped = _bank.SetContext(sig);
        if (dropped > 0) _log?.LogDebug("line bank: context changed, {N} line(s) expired", dropped);
        if (ctx.Players.Count == 0) return null; // nobody to talk to
        var need = Needed(ctx);
        if (need.Count == 0) return null;
        return _usage.CheckMode() is BudgetMode.Normal or BudgetMode.Economy ? need : null;
    }

    /// <summary>One call at a time, at most <see cref="CharacterOptions.MaxPrefetchPerHour"/> an hour.</summary>
    private bool TryReserveCall()
    {
        var now = _time.GetUtcNow();
        lock (_calls)
        {
            while (_calls.Count > 0 && now - _calls.Peek() > TimeSpan.FromHours(1)) _calls.Dequeue();
            if (_calls.Count >= Math.Max(0, _o.MaxPrefetchPerHour)) return false;
            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return false;
            _calls.Enqueue(now);
            return true;
        }
    }

    /// <summary>Banks the reply's lines for the moments asked (each linted, none said recently); returns how many.</summary>
    private int AddLines(string? text, IReadOnlyList<(string Moment, string? Player)> need, CharacterContext ctx, string sig)
    {
        int added = 0;
        foreach (var (key, lines) in Parse(text))
        {
            var (moment, player) = SplitKey(key);
            if (!need.Any(n => n.Moment == moment && (n.Player == null || n.Player.Equals(player, StringComparison.OrdinalIgnoreCase)))) continue;
            foreach (var raw in lines.Take(Math.Max(1, _o.LinesPerMoment)))
            {
                if (Lint(raw, ctx, _o.MaxLineChars) is not { } line || _bank.RecentlySaid(line)) continue;
                _bank.Add(moment, player, line, sig);
                added++;
            }
        }
        return added;
    }

    private static (string Moment, string? Player) SplitKey(string key)
    {
        int at = key.IndexOf('@');
        return at > 0 ? (key[..at].Trim().ToLowerInvariant(), key[(at + 1)..].Trim()) : (key.Trim().ToLowerInvariant(), null);
    }

    public static string SystemPrompt(string name, string persona, string? self, CharacterOptions o) =>
        $"You are {name}, a maid bot playing Minecraft with your humans. Persona: {Trunc(persona, 420)}" +
        (string.IsNullOrWhiteSpace(self) ? "" : "\n" + Trunc(self, 600)) + $$"""

        Write short in-game chat lines you could say INSTANTLY in the situation given, in your own voice: casual, ASCII, one line
        each, at most {{o.MaxLineChars}} chars. Vary them (different openings, lengths, emotes); let mood and how you feel about
        each player show (nicknames, inside jokes if you have them). Only facts from the situation: no numbers, coordinates,
        items or promises beyond the moment. Moments: greet/bye = hello/goodbye to that player; thanks = answer to being
        thanked; praise = answer to a compliment; tease = a light comeback to a rude line (never mean); status = what you're
        doing right now (from "doing"); hurt = a quick "ow" when hit; come/follow = "on my way"/"right behind you"; stop = ok,
        stopping; onit = "on it" when starting a job someone asked for; sorry = a short apology. Don't reuse the lines listed as recently said.
        Output only JSON: {"moment": ["line", ...], "greet@Player": ["line", ...]}
        """;

    public static string UserPrompt(CharacterContext ctx, IEnumerable<(string Moment, string? Player)> need, IReadOnlyList<string> recent, int per)
    {
        var sb = new StringBuilder("[situation] ").Append(ctx.Describe()).Append('\n');
        sb.Append("[moments] ").Append(string.Join(", ", need.Select(n => n.Moment + (n.Player != null ? "@" + n.Player : "")))).Append($" ({per} lines each)\n");
        if (recent.Count > 0) sb.Append("[recently said] ").Append(string.Join(" | ", recent)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>{"moment": ["line", ...]} (code fences and stray text around it tolerated); unreadable = nothing.</summary>
    public static Dictionary<string, List<string>> Parse(string? text)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        if (a < 0 || b <= a) return map;
        try
        {
            using var doc = JsonDocument.Parse(text[a..(b + 1)]);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var list = new List<string>();
                if (p.Value.ValueKind == JsonValueKind.Array)
                    foreach (var x in p.Value.EnumerateArray()) { if (x.ValueKind == JsonValueKind.String && x.GetString() is { } s) list.Add(s); }
                else if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } one) list.Add(one);
                if (list.Count > 0) map[p.Name] = list;
            }
        }
        catch (JsonException) { }
        return map;
    }

    private static readonly Regex Coords = new(@"-?\d+\s*[, ]\s*-?\d+(?:\s*[, ]\s*-?\d+)?", RegexOptions.Compiled);
    private static readonly Regex Claims = new(@"\b(?:i'?ll|i will|gonna|going to)\b.*\b(?:build|craft|mine|bring|give|make|get you)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The cleaned line or null: one line, ≤ max chars, no command, no coordinates, no numbers the situation lacks, no new promises.</summary>
    public static string? Lint(string? text, CharacterContext ctx, int maxChars = 90)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Replace("\r", "").Trim().Trim('"', '\'', '`').Trim();
        if (t.Length == 0 || t.Length > maxChars || t.Contains('\n') || t.StartsWith('/') || t.Contains('{') || t.Contains('[')) return null;
        if (t.Any(c => c > 127)) return null; // the server's chat is ASCII
        if (Coords.IsMatch(t) || Claims.IsMatch(t)) return null;
        var situation = ctx.Describe();
        foreach (Match m in Regex.Matches(t, @"\d+"))
            if (!Regex.IsMatch(situation, $@"(?<!\d){m.Value}(?!\d)")) return null;
        return t;
    }

    private static string Trunc(string s, int max) { s = (s ?? "").Trim(); return s.Length <= max ? s : s[..max]; }
}
