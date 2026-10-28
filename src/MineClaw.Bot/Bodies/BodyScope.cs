namespace MineClaw.Bot.Bodies;

/// <summary>
/// Ambient body for the current async flow (one brain, many bodies), the same pattern as <c>ToolCaller</c>: pushed explicitly
/// by each body's ActivityManager step, worker tool call and script lane (their <c>StepScope</c> hooks), flowing through
/// Task.Run and into the script thread (AsyncLocal). <see cref="BodyRoutingGameClient"/> acts on <see cref="Current"/>, else
/// on the registry's focus body. Null = no body in scope (the brain's own read-only lookups, chat, tests).
/// </summary>
public static class BodyScope
{
    private static readonly AsyncLocal<IBody?> _current = new();

    public static IBody? Current => _current.Value;

    /// <summary>Sets the body for the current async flow until disposed (null clears it).</summary>
    public static IDisposable Push(IBody? body)
    {
        var prev = _current.Value;
        _current.Value = body;
        return new Restore(prev);
    }

    private sealed class Restore(IBody? prev) : IDisposable
    {
        public void Dispose() => _current.Value = prev;
    }
}
