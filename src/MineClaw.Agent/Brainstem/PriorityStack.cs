namespace MineClaw.Agent.Brainstem;

/// <summary>Who may drive the body, highest priority first (lower value wins).</summary>
public enum BodyLayer
{
    /// <summary>Client safety reflexes: EdgeGuard holds, survival (heal retreat, eat), combat defence.</summary>
    Safety = 0,
    /// <summary>A player-commanded job: brain/worker activity, a running script, a reflex command, a standing order.</summary>
    Job = 1,
    /// <summary>The brainstem's speculative first step (walk/look) while the brain plans.</summary>
    Speculative = 2,
    /// <summary>Social idle behaviour: head tracking, glances, loose follow, thinking fidget.</summary>
    Social = 3,
    /// <summary>Nothing is driving the body.</summary>
    Idle = 4,
}

/// <summary>
/// The body's explicit priority stack: safety reflexes &gt; player-commanded job/script &gt; brainstem speculative step &gt;
/// social idle. The LLM sets jobs; the brainstem fills the gaps. A layer may act only while no higher layer is active, and
/// a higher layer preempts a lower one (a job supersedes speculation through the ActivityManager; safety lives in the client
/// tick and overrides every input regardless).
/// </summary>
public sealed class PriorityStack
{
    private readonly Func<bool> _safety, _job, _speculative, _social;

    /// <param name="safety">A client safety reflex is engaged right now.</param>
    /// <param name="job">A job, script or reflex command holds the body.</param>
    /// <param name="speculative">A speculative step is running.</param>
    /// <param name="social">A social behaviour is running.</param>
    public PriorityStack(Func<bool> safety, Func<bool> job, Func<bool> speculative, Func<bool> social)
    {
        _safety = safety;
        _job = job;
        _speculative = speculative;
        _social = social;
    }

    /// <summary>The highest active layer (<see cref="BodyLayer.Idle"/> when none).</summary>
    public BodyLayer Top
    {
        get
        {
            if (Safe(_safety)) return BodyLayer.Safety;
            if (Safe(_job)) return BodyLayer.Job;
            if (Safe(_speculative)) return BodyLayer.Speculative;
            if (Safe(_social)) return BodyLayer.Social;
            return BodyLayer.Idle;
        }
    }

    /// <summary>True when <paramref name="layer"/> may act now: no higher-priority layer is active.</summary>
    public bool MayAct(BodyLayer layer)
    {
        if (layer == BodyLayer.Safety) return true;
        // A layer that is itself active keeps going; anything above it blocks it.
        return Top >= layer;
    }

    /// <summary>"[body: job]" style label of the top layer (TUI/debug).</summary>
    public string Describe() => Top.ToString().ToLowerInvariant();

    private static bool Safe(Func<bool> f)
    {
        try { return f(); } catch (Exception) { return false; }
    }
}
