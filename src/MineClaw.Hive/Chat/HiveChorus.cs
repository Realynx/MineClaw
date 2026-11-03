using MineClaw.Bot.Bodies;
using MineClaw.Client;
using MineClaw.Hive.Ipc;

namespace MineClaw.Hive.Chat;

/// <summary>A live chat endpoint. IsMain means the main bot, not a clone hive's primary body.</summary>
public sealed record ChorusSpeaker(string Name, bool IsMain, Func<bool> Online, Func<string, CancellationToken, Task> Say);

/// <summary>Plain-chat-only scheduled speech, independent of brains, chat arbitration and command tools.</summary>
public static class HiveChorus
{
    public const int LeadMilliseconds = 1500;
    public const int LateMilliseconds = 3000;

    public static string? Prepare(string? text, out string clean)
    {
        clean = "";
        if (string.IsNullOrWhiteSpace(text)) return "error: chorus needs text";
        if (text.Any(c => c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029')) return "error: chorus must be one line";
        clean = ChatText.Sanitize(text);
        if (clean.StartsWith('/')) return "error: chorus is plain chat only, never commands";
        if (text.Length > 100 || clean.Length > 100) return "error: chorus text must be at most 100 characters";
        if (clean.Length == 0) return "error: chorus needs printable text";
        return null;
    }

    public static bool Selected(ChorusMsg message, ChorusSpeaker speaker) => speaker.IsMain
        ? message.IncludeMain
        : message.Names == null || message.Names.Any(n => n.Equals(speaker.Name, StringComparison.OrdinalIgnoreCase)
            || n.Equals(ChatArbiter.NumberOf(speaker.Name), StringComparison.OrdinalIgnoreCase));

    /// <summary>The snapshot is taken before waiting: newly logged-in bodies never replay an old chorus.</summary>
    public static Task SayAtAsync(ChorusMsg message, IReadOnlyList<ChorusSpeaker> speakers, TimeProvider time, CancellationToken ct) =>
        Task.WhenAll(speakers.Where(s => s.Online() && Selected(message, s)).Select(s => SayOneAsync(message, s, time, ct)));

    private static async Task SayOneAsync(ChorusMsg message, ChorusSpeaker speaker, TimeProvider time, CancellationToken ct)
    {
        if (Prepare(message.Text, out var clean) != null) return; // validate again at the receiving endpoint
        var fire = DateTimeOffset.FromUnixTimeMilliseconds(message.FireAtMs);
        try
        {
            // Timers may round a fractional millisecond down: recheck UTC rather than firing early.
            while (fire - time.GetUtcNow() is var wait && wait > TimeSpan.Zero)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(wait.TotalMilliseconds)), time, ct).ConfigureAwait(false);
            var remaining = fire.AddMilliseconds(LateMilliseconds) - time.GetUtcNow();
            if (remaining < TimeSpan.Zero || !speaker.Online()) return;
            // Also bound a client's own outgoing-chat queue: never speak after the late window.
            using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromTicks(1), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            await speaker.Say(clean, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Use each body's connection, never focus routing or the normal clone speech filter.</summary>
    public static IReadOnlyList<ChorusSpeaker> Speakers(BodyRegistry? registry, bool main) => registry?.All
        .Where(b => b.Client != null)
        .Select(b => new ChorusSpeaker(b.Name, main && b.IsMain, () => b.Client!.IsPlaying,
            async (text, ct) => { await b.Client!.SendChatAsync(text, ct).ConfigureAwait(false); })).ToArray() ?? [];
}
