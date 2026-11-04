using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Story;

/// <summary>A pawn body's speed boost as the client runs it (<see cref="IStoryHost.Speed"/>).</summary>
/// <param name="Requested">The multiplier the colony asked for (1, 2, 3).</param>
/// <param name="Effective">What the body runs at after the server's rejections stepped it down.</param>
/// <param name="ServerFactor">The server's own share (a Speed effect on 1.12.2; 1 = none).</param>
/// <param name="Rollbacks">Server corrections of boosted moves since the body logged in.</param>
public sealed record PawnSpeed(double Requested, double Effective, double ServerFactor, int Rollbacks);

/// <summary>
/// Fast and Faster (docs/web/story-mode.md "Speed"; Fox 11-04: "next to the pause button … add a Fast and Faster mode. They should just
/// change the movement speed through the network itself"). <c>options.speed</c>: normal | fast (×2) | faster (×3), per colony, saved.
/// <list type="bullet">
/// <item>Only the colony's own pawns: each online pawn's body gets the multiplier (<see cref="IStoryHost.SetSpeed"/>), every tick of the
/// director (it is idempotent); a pawn that leaves or a released colony goes back to normal. Main and other bodies never change.</item>
/// <item>The client moves the body further each tick (MinecraftClient.Speed.cs); when the server keeps teleporting it back, the body
/// steps itself down (3 → 2 → 1) and the colony raises one alert per level ("Server rejected faster movement — using Fast").</item>
/// <item><c>options.speed_effect</c> (default on): on 1.12.2 with an op account, the server is asked for a Speed effect on each
/// boosted pawn, once, as a toggle (<c>/effect &lt;pawn&gt; minecraft:speed 1000000 1|2 true</c>, through the command gate's scoped
/// exception: managed accounts only, the blocklist applies, audited), so the server sees legit faster movement and the client adds only
/// the rest. It is sent again only when the level changes, the pawn comes back online, or the body no longer has it (death, milk) —
/// checked at most every <see cref="EffectRecheck"/>; Fox 11-04: a 30 s effect renewed every 20 s "is spamming the hell out of the logs".
/// Back to normal (or the option off) clears the effects the colony gave. b1.8.1 has no effects.</item>
/// </list>
/// </summary>
public sealed partial class StoryColony
{
    public static readonly string[] SpeedLevels = ["normal", "fast", "faster"];
    /// <summary>A body that should have the colony's Speed effect but shows none (it died, drank milk) gets it again at most this often.</summary>
    public static readonly TimeSpan EffectRecheck = TimeSpan.FromSeconds(60);
    /// <summary>The effect's length: 1.12.2's maximum, so it is a toggle (cleared at normal speed), never a timer to renew.</summary>
    public const int EffectSeconds = 1_000_000;

    private readonly Dictionary<string, (int Amp, DateTimeOffset At)> _effects = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<double> _speedAlerted = new();

    public static double SpeedMultiplier(string level) => level switch { "fast" => 2, "faster" => 3, _ => 1 };

    public static string SpeedLevel(double multiplier) => multiplier >= 2.999 ? "faster" : multiplier >= 1.999 ? "fast" : "normal";

    /// <summary><c>POST …/options {speed?, speed_effect?}</c>: null leaves one as it is; an error for a bad level.</summary>
    public string? SetSpeed(string? speed, bool? effect)
    {
        if (speed == null && effect == null) return null;
        if (speed != null && !SpeedLevels.Contains(speed)) return $"speed must be {string.Join("|", SpeedLevels)}";
        lock (_lock)
        {
            if (speed != null && speed != Meta.Speed)
            {
                Meta.Speed = speed;
                _speedAlerted.Clear();
                _log.LogInformation("story {Colony}: speed {Speed}", Id, speed);
            }
            if (effect is { } e) Meta.SpeedEffect = e;
            Touch();
        }
        SpeedTick();
        return null;
    }

    private void SpeedOptionsJson(JsonObject o)
    {
        o["speed"] = Meta.Speed;
        o["speed_effect"] = Meta.SpeedEffect;
    }

    /// <summary>The pawn's speed now (state <c>pawns[].speed</c>): null while offline.</summary>
    private JsonObject? PawnSpeedJson(PawnState p)
    {
        if (Host.Speed(p.Name) is not { } s) return null;
        return new JsonObject
        {
            ["level"] = SpeedLevel(s.Effective), ["multiplier"] = Math.Round(s.Effective, 2), ["requested"] = Math.Round(s.Requested, 2),
            ["server_factor"] = Math.Round(s.ServerFactor, 2), ["rollbacks"] = s.Rollbacks,
        };
    }

    /// <summary>
    /// Each director tick (and at once after a change): every online pawn's body gets the colony's multiplier, a stepped-down body
    /// raises its alert once, and the Speed effects are renewed or cleared.
    /// </summary>
    internal void SpeedTick()
    {
        var sends = new List<(string Pawn, int Amp, string Why)>();
        var downs = new List<(string Pawn, double To)>();
        lock (_lock)
        {
            double want = Meta.Active ? SpeedMultiplier(Meta.Speed) : 1;
            bool effects = Meta.SpeedEffect && Host.SpeedEffectAvailable;
            foreach (var p in Pawns.Values)
            {
                if (!Host.Live(p.Name).Online) { _effects.Remove(p.Name); continue; }
                SpeedPawn(p, want, effects, sends, downs);
            }
            foreach (var name in _effects.Keys.Where(n => !Pawns.ContainsKey(n)).ToList()) _effects.Remove(name);
        }
        AlertSpeedDowns(downs);
        SendSpeedEffects(sends);
    }

    /// <summary>One online body: its multiplier (normal while leaving) and grow-zone filter set, a stepped-down body noted, its Speed
    /// effect renewed or cleared.</summary>
    private void SpeedPawn(PawnState p, double want, bool effects, List<(string Pawn, int Amp, string Why)> sends, List<(string Pawn, double To)> downs)
    {
        bool leaving = _leaving.ContainsKey(p.Name);
        double target = leaving ? 1 : want;
        Host.SetSpeed(p.Name, target);
        Host.SetGrowZone(p.Name, leaving || !Meta.Active ? null : GrowZoneFilter());
        var now = Host.Speed(p.Name);
        double eff = Math.Min(target, now?.Effective ?? target);
        if (now != null && target > 1 && now.Effective < target - 0.001) downs.Add((p.Name, now.Effective));
        // The effect follows the effective level: fast → Speed II (amplifier 1), faster → Speed III (2).
        SpeedEffectFor(p.Name, SpeedAmp(effects, eff), eff, now, sends);
    }

    private static int SpeedAmp(bool effects, double eff) => effects && eff > 1.001 ? (eff >= 2.999 ? 2 : 1) : -1;

    private void SpeedEffectFor(string pawn, int amp, double eff, PawnSpeed? now, List<(string Pawn, int Amp, string Why)> sends)
    {
        var had = _effects.TryGetValue(pawn, out var h) ? h : (Amp: -1, At: DateTimeOffset.MinValue);
        if (amp >= 0)
        {
            // Once per level; again only when the body has lost it (its server factor is back to 1) and a minute has passed.
            bool lost = had.Amp == amp && (now?.ServerFactor ?? 1) <= 1.001 && Now - had.At >= EffectRecheck;
            if (had.Amp != amp || lost) { sends.Add((pawn, amp, $"story {Id}: {SpeedLevel(eff)} speed")); _effects[pawn] = (amp, Now); }
        }
        else if (had.Amp >= 0)
        {
            sends.Add((pawn, -1, $"story {Id}: normal speed"));
            _effects.Remove(pawn);
        }
    }

    /// <summary>A body the server stepped down says so once per level.</summary>
    private void AlertSpeedDowns(List<(string Pawn, double To)> downs)
    {
        foreach (var (pawn, to) in downs)
        {
            bool first;
            lock (_lock) first = _speedAlerted.Add(to);
            if (first) Alert("warn", $"Server rejected faster movement — using {(to >= 1.999 ? "Fast" : "normal speed")}", pawn);
        }
    }

    private void SendSpeedEffects(List<(string Pawn, int Amp, string Why)> sends)
    {
        foreach (var (pawn, amp, why) in sends)
        {
            string? err;
            try { err = Host.SpeedEffect(pawn, amp, why); } catch (Exception ex) { err = ex.Message; }
            if (err != null) _log.LogDebug("story {Colony}: speed effect for {Pawn} not sent: {Err}", Id, pawn, err);
            else _log.LogInformation("story {Colony}: {Pawn} speed effect {Effect}", Id, pawn, amp < 0 ? "cleared" : $"Speed {amp + 1}");
        }
    }

    /// <summary>A pawn leaving or a released colony: its bodies walk at normal speed again and lose the colony's Speed effect.</summary>
    internal void ResetSpeed(IEnumerable<string>? pawns = null)
    {
        List<(string Pawn, bool Effect)> list;
        lock (_lock)
        {
            list = (pawns ?? Pawns.Keys).Select(n => (n, _effects.Remove(n))).ToList();
        }
        foreach (var (pawn, effect) in list)
        {
            try
            {
                Host.SetSpeed(pawn, 1);
                Host.SetGrowZone(pawn, null);
                if (effect) Host.SpeedEffect(pawn, -1, $"story {Id}: normal speed");
            }
            catch (Exception ex) { _log.LogDebug(ex, "story {Colony}: resetting {Pawn}'s speed", Id, pawn); }
        }
    }
}
