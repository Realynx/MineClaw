using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Story;

/// <summary>A particular takeover's validity, readable without waiting for the director's work lock.</summary>
public sealed class StoryHumanControlClaim(string name)
{
    public string Name { get; } = name;
    private int _revoked;
    public bool Active => Volatile.Read(ref _revoked) == 0;
    public void Revoke() => Interlocked.Exchange(ref _revoked, 1);
}

public sealed partial class StoryColony
{
    // A browser lease is runtime-only. It never changes draft, role, schedule or persisted colony settings.
    private readonly HashSet<string> _humanControlled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StoryHumanControlClaim> _humanControlClaims = new(StringComparer.OrdinalIgnoreCase);

    public bool CanHumanControl(string name)
    {
        lock (_lock) return Meta.Active && Pawns.ContainsKey(name) && !_leaving.ContainsKey(name);
    }

    public bool SetHumanControl(string name, bool enabled)
    {
        if (enabled) return BeginHumanControl(name) != null;
        lock (_lock)
        {
            if (!_humanControlled.Contains(name) && !_humanControlClaims.ContainsKey(name)) return Pawns.ContainsKey(name);
            ForgetHumanControl(name);
            return HumanControlChanged(name);
        }
    }

    public StoryHumanControlClaim? BeginHumanControl(string name)
    {
        lock (_lock)
        {
            if (!Meta.Active || !Pawns.ContainsKey(name) || _leaving.ContainsKey(name)) return null;
            if (_humanControlClaims.TryGetValue(name, out var current) && current.Active) return current;
            var claim = new StoryHumanControlClaim(name);
            _humanControlClaims[name] = claim;
            _humanControlled.Add(name);
            HumanControlChanged(name);
            return claim;
        }
    }

    /// <summary>Slow bookkeeping after the client has already stopped. Identity fences delayed cleanup from a new takeover.
    /// The optional script restart shares that fence: it runs before another Begin can install its claim.</summary>
    public bool CompleteHumanControl(StoryHumanControlClaim claim, Action? restart = null)
    {
        claim.Revoke();
        lock (_lock)
        {
            if (!_humanControlClaims.TryGetValue(claim.Name, out var current) || !ReferenceEquals(current, claim)) return false;
            ForgetHumanControl(claim.Name);
            HumanControlChanged(claim.Name);
            if (Meta.Active && Pawns.ContainsKey(claim.Name) && !_leaving.ContainsKey(claim.Name))
            {
                try { restart?.Invoke(); }
                catch (Exception ex) { _log.LogDebug(ex, "story {Colony}: {Pawn} restart after human control deferred to watchdog", Id, claim.Name); }
            }
            return true;
        }
    }

    // Caller holds the director lock (roster changes also invalidate an active browser immediately).
    private void RevokeHumanControl(string name)
    {
        if (_humanControlClaims.TryGetValue(name, out var claim)) claim.Revoke();
    }

    private void ForgetHumanControl(string name)
    {
        if (_humanControlClaims.Remove(name, out var claim)) claim.Revoke();
        _humanControlled.Remove(name);
    }

    private bool HumanControlChanged(string name)
    {
        if (_current.Remove(name, out var job)) Release(job);
        _started.Remove(name);
        _idleReason.Remove(name);
        _looks.Remove(name);
        if (!Pawns.TryGetValue(name, out var pawn)) return false;
        pawn.Rev = Bump();
        return true;
    }

    public bool HumanControlled(string name) { lock (_lock) return _humanControlled.Contains(name); }
}
