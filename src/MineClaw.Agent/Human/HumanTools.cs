using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Human;

/// <summary>Human-in-the-loop tools: ask a player something and wait; watch a player demonstrate a structure.</summary>
public static class HumanTools
{
    public const string AskPlayerDescription =
        "Ask a player a real question. Always give default (your pick) and keep working on it: the answer " +
        "comes back as chat. risky=true for builds/void/rare items. Replies and small talk are plain text.";

    /// <summary>Error returned (nothing is sent) when ask_player is called without a question.</summary>
    public const string NotAQuestion = "not sent: ask_player is only for questions you need answered. To reply, write plain text.";

    private static readonly System.Text.RegularExpressions.Regex Negative = new(
        @"^\W*(?:no|nope|nah|don'?t|do not|skip|leave (?:it|that)|not now|none)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex Yes = new(
        @"^\W*(?:y|yes|yep|yup|yeah|ya|ye|ok|okay|k|kk|sure|fine|sounds good|do (?:that|it)|go (?:ahead|for it)|that'?s fine|default|whatever)\W*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>True when an answer just confirms the default ("yep", "sure", or it repeats the default).</summary>
    public static bool Agrees(string answer, string? dflt)
    {
        var a = (answer ?? "").Trim();
        if (a.Length == 0) return false;
        if (Yes.IsMatch(a)) return true;
        if (string.IsNullOrWhiteSpace(dflt)) return false;
        static string N(string x) => string.Join(' ', System.Text.RegularExpressions.Regex.Matches(x.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value));
        var (na, nd) = (N(a), N(dflt));
        return na.Length > 0 && !Negative.IsMatch(a) && (na == nd || (nd.Length >= 3 && na.Contains(nd) && na.Length <= nd.Length + 12));
    }

    /// <param name="events">Where answers to non-blocking questions go (as chat from the player, so they wake a sleeping brain).</param>
    /// <param name="timedOut">Called when a question went with its default (the bot returns to its standing order).</param>
    /// <param name="askedBefore">Hive dedupe (question, player) → the earlier ask by another hive bot and its answer, or null.</param>
    public static void RegisterAskPlayer(ToolRegistry registry, ChatRouter chat, QuestionOptions? options = null, EventQueue? events = null,
        Action<string>? timedOut = null, Func<string, string?, string?>? askedBefore = null)
    {
        var q = options ?? new QuestionOptions();
        // Brain only: the worker never talks to players and the cheap tier replies in plain text.
        registry.Register("ask_player", AskPlayerDescription,
            ToolSchema.Create().Str("question", "ends with ?").Str("default", required: false)
                .Str("name", "player to ask; omit for anyone", required: false)
                .Bool("risky", required: false).Int("timeout", $"seconds, default {q.TimeoutSeconds}", required: false).Build(),
            new AskPlayerCall(chat, q, events, timedOut, askedBefore).RunAsync, tiers: ToolTiers.Main);
    }

    /// <summary>One registered ask_player tool: the captured options plus the steps of a call (refuse, dedupe, phrase, ask).</summary>
    private sealed class AskPlayerCall(ChatRouter chat, QuestionOptions q, EventQueue? events, Action<string>? timedOut,
        Func<string, string?, string?>? askedBefore)
    {
        public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
        {
            var question = input.ReqStr("question").Trim();
            // A normal answer sent through ask_player swallows the player's next message and holds the turn: refuse it.
            if (!question.Contains('?')) return ToolResult.Error(NotAQuestion);
            var name = input.Str("name");
            // Another hive bot asked this player the same thing minutes ago: never ask twice (user requirement 10-27).
            if (askedBefore?.Invoke(question, name) is { } prior) return NotAskedAgain(prior, input.Str("default")?.Trim());
            int timeout = Math.Clamp(input.Int("timeout") ?? q.TimeoutSeconds, Math.Max(1, q.MinTimeoutSeconds), Math.Max(q.MinTimeoutSeconds, q.MaxTimeoutSeconds));
            var text = Phrase(question, name);
            var (dflt, risky) = DefaultFor(input, question);
            if (dflt == null || events == null) return await AskBlockingAsync(text, name, timeout, ct).ConfigureAwait(false);
            return await AskNonBlockingAsync(events, question, text, name, timeout, dflt, risky, ct).ConfigureAwait(false);
        }

        private static ToolResult NotAskedAgain(string prior, string? d0) =>
            ToolResult.Success($"not asked again: {prior}" + (string.IsNullOrEmpty(d0) ? "" : $"; go ahead with \"{d0}\" unless that answer says otherwise"));

        private string Phrase(string question, string? name)
        {
            var text = name != null && !question.Contains(name, StringComparison.OrdinalIgnoreCase) ? $"{name}, {question}" : question;
            // Coordinates only when the player used them first or asked for them; otherwise landmarks ("the one by the lava").
            return Questions.Humanize(text, chat.RecentFrom(name), chat.Landmark);
        }

        private (string? Default, bool Risky) DefaultFor(JsonElement input, string question)
        {
            var dflt = input.Str("default")?.Trim();
            if (dflt?.Length == 0) dflt = null;
            // Unsafe or irreversible choices never proceed on a guess: their only default is the safe one.
            bool risky = input.Bool("risky") == true || q.IsRisky(question + " " + dflt);
            if (risky && (dflt == null || !Negative.IsMatch(dflt))) dflt = q.SafeDefault;
            return (dflt, risky);
        }

        private IDisposable? Hold(string? name, int timeout) => name != null ? chat.HoldThread(name, TimeSpan.FromSeconds(timeout + 5)) : null;

        private Task SayAsync(string text, string? name, CancellationToken ct) => chat.SayAsync(text, audience: name != null ? [name] : null, ct: ct);

        /// <summary>Legacy blocking ask (no recommended answer): hold the turn until they reply.</summary>
        private async Task<ToolResult> AskBlockingAsync(string text, string? name, int timeout, CancellationToken ct)
        {
            using var thread = Hold(name, timeout);
            // Register the waiter before speaking so a fast reply can't be missed.
            var wait = chat.WaitForReplyAsync(name, TimeSpan.FromSeconds(timeout), ct);
            await SayAsync(text, name, ct).ConfigureAwait(false);
            var reply = await wait.ConfigureAwait(false);
            return reply != null ? ToolResult.Success(reply) : ToolResult.Error($"no reply in {timeout}s");
        }

        /// <summary>Non-blocking: the waiter outlives this turn (the bot keeps working on the default meanwhile).</summary>
        private async Task<ToolResult> AskNonBlockingAsync(EventQueue events, string question, string text, string? name, int timeout,
            string dflt, bool risky, CancellationToken ct)
        {
            var lease = Hold(name, timeout);
            var pending = chat.WaitForReplyAsync(name, TimeSpan.FromSeconds(timeout), CancellationToken.None);
            await SayAsync(text, name, ct).ConfigureAwait(false);
            _ = FollowUpAsync(chat, events, q, question, name, dflt, risky, pending, lease, timedOut);
            return ToolResult.Success($"asked{(name != null ? " " + name : "")}; don't wait: go ahead with \"{dflt}\" now" +
                (risky ? " (do everything except the risky part)" : "") + ". Their answer comes back as chat; amend the job if it differs.");
        }
    }

    /// <summary>Answer → chat from the player (wakes the brain to amend) or a quiet confirmation; timeout → say so, use the default.</summary>
    private static async Task FollowUpAsync(ChatRouter chat, EventQueue events, QuestionOptions q, string question, string? name, string dflt,
        bool risky, Task<string?> pending, IDisposable? lease, Action<string>? timedOut)
    {
        string? reply;
        bool late = false;
        try { reply = await pending.ConfigureAwait(false); }
        catch (Exception) { reply = null; }
        finally { lease?.Dispose(); }

        if (reply == null)
        {
            reply = await TimedOutAsync(chat, events, q, question, name, dflt, risky, timedOut).ConfigureAwait(false);
            if (reply == null) return;
            late = true;
        }
        PostAnswer(events, question, name, dflt, reply, late);
    }

    /// <summary>The question went with its default: say so, note it, then wait the late-answer window (null: no late answer).</summary>
    private static async Task<string?> TimedOutAsync(ChatRouter chat, EventQueue events, QuestionOptions q, string question, string? name,
        string dflt, bool risky, Action<string>? timedOut)
    {
        var line = q.TimeoutText(name, dflt, risky, question); // never the raw default when it is an instruction (live 3, 14:35:08)
        if (line.Length > 0)
        {
            try { await chat.SayAsync(line, audience: name != null ? [name] : null).ConfigureAwait(false); }
            catch (Exception) { }
        }
        events.Post(GameEvent.Notice($"question timed out: \"{TextUtil.Truncate(question, 80)}\" -> went with \"{dflt}\"", EventPriority.Background));
        try { timedOut?.Invoke(question); } catch (Exception) { }
        if (name == null || q.LateAnswerSeconds <= 0) return null;
        // A late answer still applies: their next line within the window comes back marked as such.
        try { return await chat.WaitForReplyAsync(name, TimeSpan.FromSeconds(q.LateAnswerSeconds), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { return null; }
    }

    private static void PostAnswer(EventQueue events, string question, string? name, string dflt, string reply, bool late)
    {
        int c = reply.IndexOf(": ", StringComparison.Ordinal);
        var player = c > 0 ? reply[..c] : name ?? "someone";
        var answer = ReplyIntent.Strip(reply);
        var q80 = TextUtil.Truncate(question, 80);
        if (Agrees(answer, dflt))
            events.Post(GameEvent.Notice($"{player} answered \"{q80}\": {answer} (same as your default)", EventPriority.Background));
        else
            events.Post(GameEvent.Chat(player, $"{answer} ({(late ? "maybe a late answer" : "answer")} to your \"{q80}\"; you went with \"{dflt}\": amend the job if this changes it)"));
    }

    /// <summary>Registers <c>watch_demo</c> (only when the client layer supplies a demonstration source).</summary>
    public static void RegisterWatchDemo(ToolRegistry registry, ChatRouter chat, MemoryStore memory, IDemonstrationSource source)
    {
        registry.Register(new DelegateTool("watch_demo",
            DemoWait.ToolDescription,
            ToolSchema.Create().Str("player").Str("structure").Int("max_seconds", "default 120", required: false).Build(),
            async (input, ct) =>
            {
                var player = input.ReqStr("player");
                var name = input.ReqStr("structure");
                int max = Math.Clamp(input.Int("max_seconds") ?? 120, 10, 600);
                var known = memory.GetStructure(name);
                (BlockPos, BlockPos)? area = null;
                if (known?.Min != null)
                {
                    var mx = known.Max ?? known.Min;
                    area = (new BlockPos(known.Min.X - 4, known.Min.Y - 3, known.Min.Z - 4), new BlockPos(mx.X + 4, mx.Y + 3, mx.Z + 4));
                }

                await using var session = await source.StartAsync(player, area, ct).ConfigureAwait(false);
                var waited = await DemoWait.RunAsync(chat, session, player, max, null, ct).ConfigureAwait(false);

                var actions = await session.StopAsync(ct).ConfigureAwait(false);
                var steps = DemonstrationSummarizer.Summarize(actions, known?.Min);
                if (steps.Count == 0) return ToolResult.Error("saw nothing to learn" + DemoWait.SaidSuffix(waited));
                memory.UpsertStructure(name, s =>
                {
                    s.Steps.Clear();
                    s.Steps.AddRange(steps);
                    s.TaughtBy = player;
                    s.Confidence = Math.Max(s.Confidence, 0.7);
                });
                return ToolResult.Success($"learned {name} ({steps.Count} steps): " + string.Join("; ", steps) + DemoWait.SaidSuffix(waited));
            }, longRunning: false));
    }
}
