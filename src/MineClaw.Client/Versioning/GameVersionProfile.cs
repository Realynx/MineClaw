using MineClaw.Data.Versioning;
using MineClaw.Protocol.Versioning;
using MineClaw.World.Versioning;

namespace MineClaw.Client.Versioning;

/// <summary>
/// Everything version-specific a client graph needs, resolved once per process before the graph is built
/// (docs/design/multi-version.md, "DI design"). The composition root (App) resolves the <see cref="GameVersion"/>
/// (config or server ping), asks <see cref="GameVersionProfiles.For"/> for the profile and injects it through
/// <see cref="ClientOptions.Profile"/>; every client, body and clone in the process gets the same instance. Render assets
/// are registered beside it by the Bot layer (Render isn't below Client).
/// </summary>
public sealed class GameVersionProfile(GameVersion version, IProtocolAdapter protocol, IPhysicsRules physics, ICombatRules combat,
    IHungerRules hunger, IGameData data)
{
    public GameVersion Version { get; } = version;
    public IProtocolAdapter Protocol { get; } = protocol;
    public IPhysicsRules Physics { get; } = physics;
    public ICombatRules Combat { get; } = combat;
    public IHungerRules Hunger { get; } = hunger;
    public IGameData Data { get; } = data;

    public override string ToString() => Version.ToString();
}

/// <summary>The implemented profiles: b1.8.1 and 1.12.2 (multi-version P1).</summary>
public static class GameVersionProfiles
{
    /// <summary>b1.8.1: the existing code behind the seams, unchanged (the default of <see cref="ClientOptions.Profile"/>).</summary>
    public static GameVersionProfile Beta181 { get; } = new(GameVersion.Beta181, Beta181ProtocolAdapter.Instance, Beta181PhysicsRules.Instance,
        Beta181CombatRules.Instance, Beta181HungerRules.Instance, Beta181GameData.Instance);

    /// <summary>
    /// 1.12.2 (protocol 340), multi-version P1: the translating connection (login, keep-alive, chat, game state, movement,
    /// chunks with a 256-high world) and step-0.6 physics. Combat, hunger and game data still use the b1.8.1 rules until
    /// P4/P5; dig, place, windows and entity use answer "not supported on 1.12.2 yet" (P2).
    /// </summary>
    public static GameVersionProfile Java1122 { get; } = new(GameVersion.Release1122,
        MineClaw.Protocol.Release1122.Release1122ProtocolAdapter.Instance, Release1122PhysicsRules.Instance,
        Beta181CombatRules.Instance, Beta181HungerRules.Instance, Beta181GameData.Instance);

    public static bool IsImplemented(GameVersion v) => v == GameVersion.Beta181 || v == GameVersion.Release1122;

    /// <summary>The profile for a version; throws <see cref="UnsupportedVersionException"/> for a version without one.</summary>
    public static GameVersionProfile For(GameVersion v)
    {
        if (v == GameVersion.Beta181) return Beta181;
        if (v == GameVersion.Release1122) return Java1122;
        throw new UnsupportedVersionException($"{v} is recognised but not implemented (this build speaks b1.8.1 and 1.12.2)");
    }
}
