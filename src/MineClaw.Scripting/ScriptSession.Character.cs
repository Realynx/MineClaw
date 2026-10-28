using System.Text.Json.Nodes;
using MineClaw.World;

namespace MineClaw.Scripting;

/// <summary>
/// The <c>character</c> SDK module's host calls (<see cref="Character.ICharacterPort"/>): body language and voice go through the
/// usual action path (safe point, limits, timings), reads are bounded per run. No port (tests, the dry run) = harmless no-ops.
/// </summary>
public sealed partial class ScriptSession
{
    /// <summary>Per run: memory reads, memory notes, head turns (a script can't flood memory or spin its head).</summary>
    public const int MaxRecallsPerRun = 20, MaxNotesPerRun = 3, MaxLooksPerRun = 400;

    private int _recalls, _notes, _looks;

    private Character.ICharacterPort? Port => _host.Character;

    internal bool CharLook(string? player, BlockPos? pos)
    {
        SafePoint();
        lock (_lock) { if (++_looks > MaxLooksPerRun) return false; }
        try { return Port?.Look(player, pos) ?? false; } catch (Exception) { return false; }
    }

    internal ActResult CharGesture(string name, string? target)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        if (n.Length == 0) throw Bug("gesture(name) needs a name: nod|shake|wave|greet|bow|bob|hop|excite|ack|look");
        return RunAction("gesture", atomic: true, _ =>
        {
            if (Port is not { } p) return Task.FromResult(ActResult.Success("gestured " + n));
            try { return Task.FromResult(p.Gesture(n, target)); }
            catch (Exception ex) { return Task.FromResult(ActResult.Fail(ex.Message)); }
        });
    }

    internal string? CharLine(string kind, string? player)
    {
        try { return Port?.Line((kind ?? "").Trim().ToLowerInvariant(), player); } catch (Exception) { return null; }
    }

    internal void CharSaid(string line)
    {
        try { Port?.Said(line); } catch (Exception) { }
    }

    internal JsonNode? CharMood() { try { return Port?.Mood(); } catch (Exception) { return null; } }

    internal JsonNode? CharRelation(string player) { try { return Port?.Relation(player); } catch (Exception) { return null; } }

    internal JsonNode? CharRecall(string query, int k)
    {
        if (string.IsNullOrWhiteSpace(query)) throw Bug("recall(query, k) needs a query");
        SafePoint();
        lock (_lock) { if (++_recalls > MaxRecallsPerRun) return new JsonArray(); }
        try { return Port?.Recall(query, Math.Clamp(k, 1, 8)) ?? new JsonArray(); } catch (Exception) { return new JsonArray(); }
    }

    internal ActResult CharRemember(string text)
    {
        var t = (text ?? "").Replace('\n', ' ').Trim();
        if (t.Length == 0) throw Bug("remember(text) needs a text");
        if (t.Length > 240) t = t[..240];
        lock (_lock) { if (++_notes > MaxNotesPerRun) return ActResult.Fail($"note limit ({MaxNotesPerRun}/run)"); }
        return RunAction("remember", atomic: true, _ =>
        {
            if (Port is not { } p) return Task.FromResult(ActResult.Success("noted"));
            try { return Task.FromResult(p.Remember(t)); }
            catch (Exception ex) { return Task.FromResult(ActResult.Fail(ex.Message)); }
        });
    }

    internal JsonNode CharHeard(string? player, double seconds)
    {
        try { return Port?.Heard(string.IsNullOrWhiteSpace(player) ? null : player, Math.Clamp(seconds, 1, 1800)) ?? new JsonArray(); }
        catch (Exception) { return new JsonArray(); }
    }

    internal string CharIntent(string text)
    {
        try { return Port?.Intent(text ?? "") ?? "banter"; } catch (Exception) { return "banter"; }
    }

    // ------------------------------------------------------------------ typed game events

    /// <summary>
    /// <c>on('player.punchBlock', {player, held, ...}, fn)</c>: a handler for a typed game event (or a family: "player") with an
    /// <see cref="Events.EventFilter"/>; noisy events never cut the running action unless asked (<c>cut:true</c>).
    /// </summary>
    internal void OnGameEvent(string topic, JsonNode? filter, Func<ScriptEvent, string?> fn, object? owner = null)
    {
        var t = Events.GameEventCatalog.Canonical(topic) ?? topic.Trim();
        var f = Events.EventFilter.Parse(filter);
        bool? cut = filter is JsonObject o && o["cut"] is JsonValue cv && cv.TryGetValue<bool>(out var c) ? c : null;
        bool cuts = cut ?? !Events.GameEventCatalog.IsNoisy(t);
        AddHandler(new Handler($"on('{t}')", e => e.Data != null && e.Matches(t) && f.Accepts(e.Data),
            e => f.TryFire(_time.GetUtcNow()) ? fn(e) : null, cuts, owner));
    }
}
