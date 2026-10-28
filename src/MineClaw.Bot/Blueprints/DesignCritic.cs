using System.Text.RegularExpressions;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Perception;
using MineClaw.Bot.Perception.Vision;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>The perception tier's brief, structured review of a preview: ok, or up to 3 issues (local blueprint coords).</summary>
public sealed record Critique(bool Ok, string Text, IReadOnlyList<string> Issues, decimal Usd, bool Skipped = false, bool Cached = false)
{
    public static Critique Skip(string why) => new(true, "", [], 0, Skipped: true) { Why = why };
    public string? Why { get; init; }
}

/// <summary>Visual critique of a rendered design against its description (the perception tier; fakes in tests).</summary>
public interface IDesignCritic
{
    Task<Critique> CritiqueAsync(DesignPicture picture, VoxelPlan plan, string description, CancellationToken ct);
}

/// <summary>
/// The perception model (Agent:Llm:Perception) looks at the contact sheet with the planned blocks drawn in place and answers the
/// shared strict-JSON schema: <c>answer</c> = "ok" or the main problem, <c>facts</c> = visual issues. Booked under the perception
/// tier, inside its hourly cap; cached by (picture, description). The brain never sees the image.
/// </summary>
public sealed partial class PerceptionCritic(Func<PerceptionService?> service, UsageTracker? usage) : IDesignCritic
{
    private readonly Dictionary<(ulong, string), Critique> _cache = new();
    private readonly object _lock = new();

    public static string Question(string description) =>
        "DESIGN REVIEW (nothing is built yet). Magenta '*' cells are the PLANNED build drawn in its real site; all else exists. " +
        $"The player asked for: \"{Trim(description, 200)}\". answer: \"ok\" if it fits that wish and looks like a sensible Minecraft build, " +
        "else the main problem in <=12 words. facts: up to 3 concrete visual problems (shape, proportions, roof, door/windows, odd blocks, " +
        "blocking a path or the gen), each with the coords of the blocks concerned. No praise, no style essays.";

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    [GeneratedRegex(@"^\s*(ok|okay|good|fits|looks good|matches)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OkRx();

    public async Task<Critique> CritiqueAsync(DesignPicture picture, VoxelPlan plan, string description, CancellationToken ct)
    {
        var key = (picture.Key, description.Trim().ToLowerInvariant());
        lock (_lock) if (_cache.TryGetValue(key, out var hit)) return hit with { Cached = true, Usd = 0 };
        if (service() is not { Model: { } model } svc || svc.Options.Direct) return Critique.Skip("no perception model configured");
        if (svc.Budget(auto: false) is { } over) return Critique.Skip(over);
        PerceptionResult r;
        try
        {
            r = await model.AskAsync(new PerceptionQuery(picture.Png, picture.Width, picture.Height, Question(description), picture.Legend), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { return Critique.Skip("perception failed: " + ex.GetType().Name); }
        decimal usd = 0;
        if (r.Usage != LlmUsage.Zero)
            try { usd = usage?.Record(r.Model, r.Usage, label: UsageTracker.PerceptionLabel) ?? 0; } catch (Exception) { }
        if (!r.Ok) return new Critique(true, "", [], usd, Skipped: true) { Why = r.Error };
        var issues = new List<string>();
        foreach (var f in r.Facts.Take(3))
        {
            var claim = f.Claim.Trim();
            if (claim.Length == 0) continue;
            // Coordinates the model read off the rulers, as the author's own (local) coords when they fall on the build.
            var at = f.Coords.Select(c => new BlockPos(c.X, c.Y, c.Z)).Where(p => plan.Inside(p)).Select(plan.Local).Take(2).ToList();
            issues.Add(at.Count > 0 ? $"{claim} @{string.Join(" ", at)}" : claim);
        }
        var answer = (r.Answer ?? "").Trim();
        bool ok = OkRx().IsMatch(answer) && issues.Count == 0 || answer.Length == 0 && issues.Count == 0;
        var text = ok ? "ok" : string.Join("; ", new[] { answer }.Where(a => a.Length > 0 && !OkRx().IsMatch(a)).Concat(issues));
        if (text.Length > 240) text = text[..239] + "…";
        var c = new Critique(ok, text, issues, usd);
        lock (_lock)
        {
            if (_cache.Count > 64) _cache.Clear();
            _cache[key] = c;
        }
        return c;
    }
}
