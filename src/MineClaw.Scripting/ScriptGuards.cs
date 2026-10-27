namespace MineClaw.Scripting;

/// <summary>A limit breach (instructions, time, actions, busy loop, memory, call depth). Sticky: every later hook rethrows it.</summary>
public sealed class ScriptLimitException(string message) : Exception(message);

/// <summary>Cancel / replace / supersede reached a safe point. Sticky like limits.</summary>
public sealed class ScriptStopException(string reason) : Exception(reason);

/// <summary><c>fail"msg"</c> or a binding misuse (unknown item, bad args): ends the script as failed.</summary>
public sealed class ScriptFailException(string message) : Exception(message);
