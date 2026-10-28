using MineClaw.Bot.Bodies;

namespace MineClaw.Bot.Game;

/// <summary>
/// Type checks on a game client go through here: a <see cref="BodyRoutingGameClient"/> is first resolved to the client of
/// the body in scope (else the focus), so `is ClaimAwareGameClient` / `is MinecraftGameClient` answer as they did before
/// tools were registered against the router.
/// </summary>
public static class GameClients
{
    /// <summary>The client a call on <paramref name="c"/> acts on right now.</summary>
    public static IGameClient Resolve(IGameClient c) => c is BodyRoutingGameClient r ? r.Target : c;

    /// <summary>The claim-aware wrapper a call on <paramref name="c"/> goes through, or null (no hive board).</summary>
    public static ClaimAwareGameClient? Claimed(IGameClient c) => Resolve(c) as ClaimAwareGameClient;

    /// <summary>The client under any claim wrapper (macros the wrapper doesn't know, e.g. chop_tree).</summary>
    public static IGameClient Unclaimed(IGameClient c)
    {
        var t = Resolve(c);
        return (t as ClaimAwareGameClient)?.Inner ?? t;
    }

    /// <summary>The real-client adapter under <paramref name="c"/>, or null (a test fake).</summary>
    public static MinecraftGameClient? Minecraft(IGameClient c) => Unclaimed(c) as MinecraftGameClient;
}
