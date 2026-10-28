namespace MineClaw.Bot.Bodies;

/// <summary>
/// The brain's bodies (one brain, many bodies). Step 1 holds exactly one, the main body; spawning clones in-process
/// (<see cref="Add"/>, the runner, slot policy) is step 2. <see cref="Focus"/> is what an unscoped body tool acts on.
/// </summary>
public sealed partial class BodyRegistry
{
    private readonly object _lock = new();
    private readonly List<IBody> _bodies = new();
    private IBody? _focus;

    public BodyRegistry(IBody main)
    {
        if (!main.IsMain) throw new ArgumentException("the first body must be the main body", nameof(main));
        Main = main;
        _bodies.Add(main);
    }

    /// <summary>The main body: always online, the only chat sender, the default focus.</summary>
    public IBody Main { get; }

    /// <summary>The body unscoped body tools act on: the last <see cref="SetFocus"/>, else main.</summary>
    /// <remarks>Lock-free: every unscoped router call (World in tight loops) reads it.</remarks>
    public IBody Focus => Volatile.Read(ref _focus) ?? Main;

    public IReadOnlyList<IBody> All { get { lock (_lock) return _bodies.ToArray(); } }

    public int Count { get { lock (_lock) return _bodies.Count; } }

    /// <summary>A body by login name or short name ("maid_chan_67" or "67"; "main" = the main body), or null.</summary>
    public IBody? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        lock (_lock)
            return _bodies.FirstOrDefault(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                ?? _bodies.FirstOrDefault(b => b.Short.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a body (step 2: the in-process spawn). A duplicate name or a second main is refused.</summary>
    internal void Add(IBody body)
    {
        lock (_lock)
        {
            if (body.IsMain) throw new InvalidOperationException("the registry already has its main body");
            if (_bodies.Any(b => b.Name.Equals(body.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"body {body.Name} is already registered");
            _bodies.Add(body);
        }
    }

    /// <summary>Removes a non-main body (despawned/retired); the focus falls back to main.</summary>
    internal bool Remove(IBody body)
    {
        if (body.IsMain) return false;
        lock (_lock)
        {
            if (ReferenceEquals(_focus, body)) Volatile.Write(ref _focus, null);
            return _bodies.Remove(body);
        }
    }

    /// <summary>Sets the focus body (null/unknown name = back to main). Returns the focus now in force.</summary>
    public IBody SetFocus(string? name)
    {
        var b = Find(name);
        lock (_lock)
        {
            Volatile.Write(ref _focus, b != null && !b.IsMain ? b : null);
            return _focus ?? Main;
        }
    }
}
