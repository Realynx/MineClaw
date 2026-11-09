namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    // A browser lease is runtime-only. It never changes draft, role, schedule or persisted colony settings.
    private readonly HashSet<string> _humanControlled = new(StringComparer.OrdinalIgnoreCase);

    public bool CanHumanControl(string name)
    {
        lock (_lock) return Meta.Active && Pawns.ContainsKey(name) && !_leaving.ContainsKey(name);
    }

    public bool SetHumanControl(string name, bool enabled)
    {
        lock (_lock)
        {
            if (!Pawns.TryGetValue(name, out var pawn))
            {
                if (!enabled) _humanControlled.Remove(name); // departure may win the race with lease cleanup
                return false;
            }
            if (enabled && (!Meta.Active || _leaving.ContainsKey(name))) return false;
            bool changed = enabled ? _humanControlled.Add(name) : _humanControlled.Remove(name);
            if (!changed) return true;
            if (_current.Remove(name, out var job)) Release(job);
            _started.Remove(name);
            _idleReason.Remove(name);
            _looks.Remove(name);
            pawn.Rev = Bump();
            return true;
        }
    }

    public bool HumanControlled(string name) { lock (_lock) return _humanControlled.Contains(name); }
}
