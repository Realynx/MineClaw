using System.Text.Json;
using MineClaw.App.Supervisor;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>A world whose entities come from <see cref="HiveEntityMerge"/> over two bodies' trackers (mutable for delta tests).</summary>
internal sealed class TwoBodyWorld : ISupervisorWorld
{
    public static readonly DateTime T0 = new(2026, 10, 28, 6, 0, 0, DateTimeKind.Utc);
    public List<Entity> A = [], B = [];

    public IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max) => [];
    public IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols) => [];
    public IReadOnlyList<WorldBot> Bots() =>
        [new("maid_chan", "main", true, 0, 0, 65, 0, 0, 0, 0, 20, 20, null, null, null, [], true, 0),
         new("maid_chan_67", "67", false, 0, 9, 65, 9, 0, 0, 0, 20, 20, null, null, null, [], true, 0)];
    public IReadOnlyList<WorldEntity> Entities(int? dim) =>
        HiveEntityMerge.Merge([new BodyEntities("maid_chan", 0, A), new BodyEntities("maid_chan_67", 0, B)],
            new HashSet<string>(["maid_chan", "maid_chan_67"], StringComparer.OrdinalIgnoreCase), T0.AddSeconds(30));
    public WorldOverlays Overlays(int dim) => new(DateTimeOffset.UnixEpoch, dim, [], [], [], [], [], []);
}

public class HiveEntityStreamTests
{
    private static Entity Item(int id, double x, int item, int count, int damage = 0, DateTime? at = null) =>
        new Entity(id, EntityKind.Item, new Vec3d(x, 64.13, 2.5), new Vec3d(0, -0.04, 0), Item: new ItemStack((short)item, (byte)count, (short)damage))
        { SpawnedAt = TwoBodyWorld.T0, UpdatedAt = at ?? TwoBodyWorld.T0 };

    private static Entity Mob(int id, int type, double x, int flags = 0, DateTime? at = null) =>
        new Entity(id, EntityKind.Mob, new Vec3d(x, 64, 0), TypeId: type, Yaw: 90,
            Metadata: new Dictionary<int, object> { [0] = (sbyte)flags, [16] = (sbyte)5 }) { SpawnedAt = TwoBodyWorld.T0, UpdatedAt = at ?? TwoBodyWorld.T0 };

    private static Entity Player(int id, string name, int flags = 0) =>
        new Entity(id, EntityKind.Player, new Vec3d(1, 64, 1), Name: name, Yaw: 180, Metadata: new Dictionary<int, object> { [0] = (sbyte)flags })
        { SpawnedAt = TwoBodyWorld.T0, UpdatedAt = TwoBodyWorld.T0 };

    [Fact]
    public void Merge_takes_every_bodys_entities_once_freshest_wins_and_skips_hive_bots()
    {
        var w = new TwoBodyWorld
        {
            A = [Item(10, 1.0, 4, 12), Player(11, "PoofImaFox"), Mob(12, MobTypes.Zombie, 5), Player(20, "maid_chan_67")],
            B = [Item(10, 1.6, 4, 12, at: TwoBodyWorld.T0.AddSeconds(2)), Mob(13, MobTypes.Sheep, 40), Player(21, "maid_chan")],
        };
        var list = w.Entities(null);
        Assert.Equal([10, 11, 12, 13], list.Select(e => e.Id!.Value).Order().ToArray());
        var drop = list.Single(e => e.Id == 10);
        Assert.Equal(1.6, drop.X);                 // the body that heard of it last
        Assert.Equal("maid_chan_67", drop.By);
        Assert.Equal("item", list[0].Kind);        // drops first
        Assert.Equal("maid_chan", list.Single(e => e.Id == 13 - 1).By);
        Assert.DoesNotContain(list, e => e.Name is "maid_chan" or "maid_chan_67");
        Assert.Equal(5, list.Single(e => e.Id == 13).Variant); // sheep fleece / slime size from metadata 16
    }

    private static (SupervisorServer, TwoBodyWorld) Server()
    {
        var w = new TwoBodyWorld();
        var s = new SupervisorServer(new SupervisorOptions { Token = "s3cret" }, new FakeSupervisorData())
        {
            World = new SupervisorWorldRoutes(w, new WorldRouteLimits(EntitiesPerSec: 1000, EntitiesBurst: 1000)),
        };
        return (s, w);
    }

    private static SupervisorRequest Get(string path, string query) =>
        new("GET", path, query.Split('&').Select(kv => kv.Split('=')).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "", StringComparer.OrdinalIgnoreCase), "s3cret");

    [Fact]
    public async Task Item_entities_carry_the_stack_motion_age_and_flags()
    {
        var (s, w) = Server();
        await using var srv = s;
        w.A = [Item(10, 1.25, 35, 3, damage: 14), Mob(12, MobTypes.Zombie, 5, flags: 0x01 | 0x02), Player(11, "PoofImaFox", flags: 0x08)];
        using var doc = JsonDocument.Parse((await s.HandleAsync(Get("/api/world/entities", "since=0"))).Text);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("full").GetBoolean());
        Assert.True(root.GetProperty("rev").GetInt64() > 0);
        var ents = root.GetProperty("entities").EnumerateArray().ToList();
        var item = ents.Single(e => e.GetProperty("kind").GetString() == "item");
        Assert.Equal(10, item.GetProperty("id").GetInt32());
        Assert.Equal(35, item.GetProperty("item").GetInt32());
        Assert.Equal(14, item.GetProperty("damage").GetInt32());
        Assert.Equal(3, item.GetProperty("count").GetInt32());
        Assert.Equal("wool", item.GetProperty("name").GetString());
        Assert.Equal(1.25, item.GetProperty("x").GetDouble());
        Assert.Equal(-0.04, item.GetProperty("vy").GetDouble());
        Assert.Equal(30, item.GetProperty("age").GetDouble());
        Assert.Equal("maid_chan", item.GetProperty("by").GetString());
        Assert.Equal(0, item.GetProperty("dim").GetInt32());
        Assert.False(item.TryGetProperty("flags", out _)); // nothing set: left out
        // The flags byte round-trips (burning + sneaking on the zombie, sprinting on the player).
        Assert.Equal(0x03, ents.Single(e => e.GetProperty("kind").GetString() == "mob").GetProperty("flags").GetInt32());
        Assert.Equal(0x08, ents.Single(e => e.GetProperty("kind").GetString() == "player").GetProperty("flags").GetInt32());
    }

    [Fact]
    public async Task Delta_stream_sends_only_changes_and_removals()
    {
        var (s, w) = Server();
        await using var srv = s;
        w.A = [Item(10, 1, 4, 12), Mob(12, MobTypes.Pig, 5)];
        long rev;
        using (var doc = JsonDocument.Parse((await s.HandleAsync(Get("/api/world/entities", "since=0"))).Text))
        {
            rev = doc.RootElement.GetProperty("rev").GetInt64();
            Assert.Equal(2, doc.RootElement.GetProperty("entities").GetArrayLength());
        }
        await Task.Delay(120); // past the rebuild interval
        w.A = [Item(10, 1, 4, 13), Mob(12, MobTypes.Pig, 5)]; // the stack grew; the pig didn't move
        using (var doc = JsonDocument.Parse((await s.HandleAsync(Get("/api/world/entities", $"since={rev}"))).Text))
        {
            Assert.False(doc.RootElement.GetProperty("full").GetBoolean());
            var ents = doc.RootElement.GetProperty("entities");
            Assert.Equal(1, ents.GetArrayLength());
            Assert.Equal(13, ents[0].GetProperty("count").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("removed").GetArrayLength());
            rev = doc.RootElement.GetProperty("rev").GetInt64();
        }
        await Task.Delay(120);
        w.A = [Mob(12, MobTypes.Pig, 5)]; // picked up
        using (var doc = JsonDocument.Parse((await s.HandleAsync(Get("/api/world/entities", $"since={rev}"))).Text))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("entities").GetArrayLength());
            var removed = doc.RootElement.GetProperty("removed");
            Assert.Equal(1, removed.GetArrayLength());
            Assert.Equal(0, removed[0][0].GetInt32());
            Assert.Equal(10, removed[0][1].GetInt32());
        }
    }
}
