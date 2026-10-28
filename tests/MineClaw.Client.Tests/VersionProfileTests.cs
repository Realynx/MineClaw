using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.Client.Versioning;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Multi-version P0 (docs/design/multi-version.md): the b1.8.1 profile wraps the existing code unchanged, and the
/// client opens and logs in through the profile's protocol adapter.</summary>
public class VersionProfileTests
{
    [Fact]
    public void DefaultProfileIsBeta181()
    {
        var o = new ClientOptions();
        Assert.Same(GameVersionProfiles.Beta181, o.Profile);
        Assert.Same(GameVersion.Beta181, o.Profile.Version);
        Assert.Same(GameVersion.Beta181, o.Profile.Protocol.Version);
        Assert.Equal("b1.8.1", o.Profile.Data.VersionId);
        Assert.Same(GameVersionProfiles.Beta181, GameVersionProfiles.For(GameVersion.Beta181));
    }

    [Fact]
    public void Release1122IsImplemented()
    {
        Assert.True(GameVersionProfiles.IsImplemented(GameVersion.Release1122));
        var p = GameVersionProfiles.For(GameVersion.Release1122);
        Assert.Same(GameVersionProfiles.Java1122, p);
        Assert.Same(GameVersion.Release1122, p.Version);
        Assert.Equal(256, p.Physics.WorldHeight);
        Assert.Equal(0.6, p.Physics.StepHeight, 6);
        Assert.Equal(256, p.Protocol.MaxChatLength);
        Assert.Same(GameVersion.Release1122, p.Protocol.Version);
    }

    [Fact]
    public void Beta181SeamsMatchTheStatics()
    {
        var p = GameVersionProfiles.Beta181;
        Assert.Equal(PlayerPhysics.StepHeight, p.Physics.StepHeight);
        Assert.Equal(PlayerPhysics.EyeHeight, p.Physics.EyeHeight);
        Assert.Equal((PlayerPhysics.Width, PlayerPhysics.Height), (p.Physics.Width, p.Physics.Height));
        Assert.Equal(128, p.Physics.WorldHeight);
        Assert.Equal(GameVersion.Beta181.WorldHeight, p.Physics.WorldHeight);
        Assert.Equal(0.98, p.Physics.Slipperiness(Blocks.Ice), 6);

        Assert.Equal(CombatRules.WeaponDamage(Items.DiamondSword), p.Combat.WeaponDamage(Items.DiamondSword));
        Assert.Equal(0, p.Combat.AttackCooldownTicks(Items.DiamondSword));
        Assert.True(p.Combat.SwordBlocks);
        Assert.Equal(CombatRules.InvulnerabilityTicks, p.Combat.InvulnerabilityTicks);

        Assert.Equal(HungerRules.RegenFood, p.Hunger.RegenFood);
        Assert.Equal(HungerRules.StarvationFloor(2), p.Hunger.StarvationFloor(2));
        Assert.False(p.Hunger.RegenCostsExhaustion);

        Assert.Same(Blocks.Get(Blocks.Stone), p.Data.Block(Blocks.Stone));
        Assert.Equal(Tools.DigTicks(Blocks.Stone, Items.WoodenPickaxe), p.Data.DigTicks(Blocks.Stone, Items.WoodenPickaxe));
        Assert.Equal(Smelting.ResultFor(Blocks.Cobblestone), p.Data.SmeltingFor(Blocks.Cobblestone));
        Assert.Equal(100, p.Protocol.MaxChatLength);
        Assert.IsType<HandshakePacket>(Assert.Single(p.Protocol.LoginStart("maid_chan")));
    }

    [Fact]
    public void PhysicsSeamTicksLikeTheStatic()
    {
        var w = new WorldState();
        w.LoadColumn(0, 0);
        for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        PhysicsState Fresh() => new() { Position = new Vec3d(8.5, 66, 8.5) };
        var a = Fresh();
        var b = Fresh();
        var input = new MoveInput { Forward = 1, Yaw = 30 };
        for (int i = 0; i < 40; i++)
        {
            PlayerPhysics.Tick(w, a, input);
            GameVersionProfiles.Beta181.Physics.Tick(w, b, input);
        }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.OnGround, b.OnGround);
    }

    /// <summary>With no ConnectionFactory the client connects and starts the login through the profile's adapter.</summary>
    [Fact]
    public async Task ClientConnectsAndLogsInThroughTheProfileAdapter()
    {
        using var server = new FakeServer();
        var adapter = new RecordingAdapter(server);
        var profile = new GameVersionProfile(GameVersion.Beta181, adapter, GameVersionProfiles.Beta181.Physics, GameVersionProfiles.Beta181.Combat,
            GameVersionProfiles.Beta181.Hunger, GameVersionProfiles.Beta181.Data);
        var client = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", Host = "example.invalid", Port = 25566, Profile = profile,
            TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero, HostileAlertRadius = 0,
        });
        await using var _ = client;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await client.ConnectAsync(cts.Token);
        Assert.True(client.IsPlaying);
        Assert.Equal(("example.invalid", 25566), adapter.ConnectedTo);
        Assert.Equal(["maid_chan"], adapter.LoginStarts);
        Assert.IsType<HandshakePacket>(server.Received.First());
    }

    private sealed class RecordingAdapter(FakeServer server) : IProtocolAdapter
    {
        public (string, int)? ConnectedTo;
        public List<string> LoginStarts { get; } = [];
        public GameVersion Version => GameVersion.Beta181;
        public int MaxChatLength => 100;

        public IGameConnection Connect(string host, int port, TimeSpan? connectTimeout = null)
        {
            ConnectedTo = (host, port);
            return server.CreateClientConnection(default);
        }

        public IGameConnection Open(Stream stream, PacketDirection inbound = PacketDirection.ClientBound, bool ownsStream = true) =>
            Beta181ProtocolAdapter.Instance.Open(stream, inbound, ownsStream);

        public IReadOnlyList<IPacket> LoginStart(string username)
        {
            LoginStarts.Add(username);
            return Beta181ProtocolAdapter.Instance.LoginStart(username);
        }
    }
}
