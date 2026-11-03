using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Supervisor;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary><c>GET /api/viewer/bodies/{name}/inventory</c>: main, a subhive body by its short name, offline bodies, both versions' slot layouts.</summary>
public class SupervisorInventoryTests
{
    private const string Token = "s3cret-token";
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Main and two bodies (a subhive body "102" of hive 109, and a 1.12.2-style pawn); nothing else resolves.</summary>
    private sealed class Data : IViewerData
    {
        public IReadOnlyCollection<string> TrustedPlayers => ["poofimafox"];
        public string? Resolve(string bot) => bot.ToLowerInvariant() switch
        {
            "main" or "maid_chan" => "maid_chan",
            "102" or "maid_chan_102" => "maid_chan_102",
            "109" or "maid_chan_109" => "maid_chan_109",
            _ => null,
        };
        public Task<bool> SendAsync(RemoteChat chat, CancellationToken ct) => Task.FromResult(true);
        public Task<ViewerNow?> NowAsync(string bot, CancellationToken ct) => Task.FromResult<ViewerNow?>(null);
        public IReadOnlyList<string> Brains() => ["maid_chan"];
        public Task<BrainContextPage?> ContextAsync(string brain, CancellationToken ct) => Task.FromResult<BrainContextPage?>(null);
        public IReadOnlyDictionary<long, MemoryDocInfo> Docs(IReadOnlyCollection<long> ids) => new Dictionary<long, MemoryDocInfo>();
    }

    private static (SupervisorServer Server, BodyInventories Inv, ManualClock Clock) Make(string version = "b1.8.1")
    {
        var o = new SupervisorOptions { Token = Token, Viewer = new SupervisorViewerOptions { Enabled = true } };
        var clock = new ManualClock(T0);
        var inv = new BodyInventories(clock) { Version = () => version };
        var routes = new SupervisorViewerRoutes(o, new Data(), new ViewerFeed(time: clock), new ViewerMemoryActivity(time: clock), clock) { Inventories = inv };
        return (new SupervisorServer(o, new FakeSupervisorData()) { Viewer = routes }, inv, clock);
    }

    private static SupervisorRequest Get(string path, string method = "GET", string? token = Token) =>
        new(method, path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), token);

    private static JsonElement J(SupervisorReply r) => JsonDocument.Parse(r.Text).RootElement;

    private static WorldState Beta(int held = 0)
    {
        var w = new WorldState(inventory: InventoryModel.Beta181);
        w.Player.HeldSlot = held;
        return w;
    }

    [Fact]
    public async Task Main_answers_with_the_hotbar_first_then_main_slots_armour_head_to_feet_and_the_held_slot()
    {
        var (s, inv, _) = Make();
        await using var _s = s;
        var w = Beta(held: 2);
        w.Inventory.SetSlot(0, 36, new ItemStack(278, 1, 100));     // hotbar 0: a used diamond pickaxe
        w.Inventory.SetSlot(0, 38, new ItemStack(4, 64, 0));       // hotbar 2 (held): cobblestone
        w.Inventory.SetSlot(0, 44, new ItemStack(50, 12, 0));      // hotbar 8: torches
        w.Inventory.SetSlot(0, 9, new ItemStack(35, 3, 14));       // main, top-left: red wool
        w.Inventory.SetSlot(0, 35, new ItemStack(264, 2, 0));      // main, bottom-right: diamonds
        w.Inventory.SetSlot(0, 5, new ItemStack(306, 1, 20));      // helmet
        w.Inventory.SetSlot(0, 8, new ItemStack(301, 1, 0));       // boots
        w.Inventory.SetSlot(0, 1, new ItemStack(5, 4, 0));         // the crafting grid: never sent
        inv.Register("maid_chan", w, () => true);

        var r = await s.HandleAsync(Get("/api/viewer/bodies/main/inventory"));
        Assert.Equal(200, r.Status);
        var j = J(r);
        Assert.Equal("maid_chan", j.GetProperty("name").GetString());
        Assert.Equal("b1.8.1", j.GetProperty("version").GetString());
        Assert.Equal(2, j.GetProperty("held_slot").GetInt32());
        Assert.True(j.GetProperty("online").GetBoolean());
        Assert.Equal(T0.ToUnixTimeMilliseconds(), j.GetProperty("at").GetInt64());
        var slots = j.GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal([0, 2, 8, 9, 35], slots.Select(x => x.GetProperty("slot").GetInt32()).ToArray());
        var pick = slots[0];
        Assert.Equal(278, pick.GetProperty("id").GetInt32());
        Assert.Equal(100, pick.GetProperty("meta").GetInt32());
        Assert.Equal(1561, pick.GetProperty("durability").GetProperty("max").GetInt32());
        Assert.Equal(1461, pick.GetProperty("durability").GetProperty("left").GetInt32());
        Assert.False(pick.GetProperty("enchanted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, slots[1].GetProperty("durability").ValueKind);
        Assert.Equal(64, slots[1].GetProperty("count").GetInt32());
        Assert.Equal("cobblestone", slots[1].GetProperty("name").GetString());
        Assert.Equal("red_wool", slots[3].GetProperty("name").GetString());
        Assert.Equal("Red Wool", slots[3].GetProperty("display").GetString());
        var armor = j.GetProperty("armor").EnumerateArray().ToList();
        Assert.Equal(4, armor.Count);
        Assert.Equal(306, armor[0].GetProperty("id").GetInt32());
        Assert.Equal(39, armor[0].GetProperty("slot").GetInt32());
        Assert.Equal(JsonValueKind.Null, armor[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, armor[2].ValueKind);
        Assert.Equal(36, armor[3].GetProperty("slot").GetInt32());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("offhand").ValueKind); // b1.8.1 has none
        Assert.DoesNotContain(slots, x => x.GetProperty("id").GetInt32() == 5);
    }

    [Fact]
    public async Task A_subhive_body_answers_by_its_short_name_and_its_own_inventory()
    {
        var (s, inv, _) = Make();
        await using var _s = s;
        inv.Register("maid_chan", Beta(), () => true);
        var body = Beta(held: 1);
        body.Inventory.SetSlot(0, 37, new ItemStack(258, 1, 30));
        inv.Register("maid_chan_102", body, () => true);

        var r = await s.HandleAsync(Get("/api/viewer/bodies/102/inventory"));
        Assert.Equal(200, r.Status);
        var j = J(r);
        Assert.Equal("maid_chan_102", j.GetProperty("name").GetString());
        Assert.Equal(1, j.GetProperty("held_slot").GetInt32());
        var axe = Assert.Single(j.GetProperty("slots").EnumerateArray());
        Assert.Equal(1, axe.GetProperty("slot").GetInt32());
        Assert.Equal(250 - 30, axe.GetProperty("durability").GetProperty("left").GetInt32());
        // The login works too.
        Assert.Equal(200, (await s.HandleAsync(Get("/api/viewer/bodies/maid_chan_102/inventory"))).Status);
    }

    [Fact]
    public async Task An_offline_body_answers_its_last_known_inventory_and_an_unknown_one_is_404()
    {
        var (s, inv, clock) = Make();
        await using var _s = s;
        bool online = true;
        var w = Beta();
        w.Inventory.SetSlot(0, 36, new ItemStack(4, 10, 0));
        inv.Register("maid_chan_102", w, () => online);
        Assert.True(J(await s.HandleAsync(Get("/api/viewer/bodies/102/inventory"))).GetProperty("online").GetBoolean());

        // Gone: the world was cleared after the snapshot; the reply is the snapshot taken while it played, marked offline.
        online = false;
        w.Inventory.SetSlot(0, 36, ItemStack.Empty);
        clock.Now = T0.AddMinutes(5);
        var r = await s.HandleAsync(Get("/api/viewer/bodies/102/inventory"));
        Assert.Equal(200, r.Status);
        var j = J(r);
        Assert.False(j.GetProperty("online").GetBoolean());
        Assert.Equal(T0.ToUnixTimeMilliseconds(), j.GetProperty("at").GetInt64());
        Assert.Equal(10, Assert.Single(j.GetProperty("slots").EnumerateArray()).GetProperty("count").GetInt32());

        // A body of the hive whose client was never seen here (another process, still spawning): 404 with online false.
        var missing = await s.HandleAsync(Get("/api/viewer/bodies/109/inventory"));
        Assert.Equal(404, missing.Status);
        Assert.False(J(missing).GetProperty("online").GetBoolean());
        // Not a body at all.
        Assert.Equal(404, (await s.HandleAsync(Get("/api/viewer/bodies/nobody/inventory"))).Status);
        Assert.Equal(404, (await s.HandleAsync(Get("/api/viewer/bodies/..%2Fadmin/inventory"))).Status);
    }

    [Fact]
    public async Task A_disconnect_snapshot_is_kept_and_a_new_client_for_the_account_replaces_the_world()
    {
        var (s, inv, _) = Make();
        await using var _s = s;
        bool online = true;
        var w = Beta();
        w.Inventory.SetSlot(0, 40, new ItemStack(297, 5, 0));
        inv.Register("maid_chan_102", w, () => online);
        online = false;
        inv.Remember("maid_chan_102"); // the client's Disconnected
        w.Inventory.SetSlot(0, 40, ItemStack.Empty);
        var j = J(await s.HandleAsync(Get("/api/viewer/bodies/102/inventory")));
        Assert.Equal(4, Assert.Single(j.GetProperty("slots").EnumerateArray()).GetProperty("slot").GetInt32());

        // It logs in again with a fresh client: the live world answers.
        var again = Beta();
        again.Inventory.SetSlot(0, 36, new ItemStack(1, 7, 0));
        inv.Register("maid_chan_102", again, () => true);
        j = J(await s.HandleAsync(Get("/api/viewer/bodies/102/inventory")));
        Assert.True(j.GetProperty("online").GetBoolean());
        Assert.Equal(1, Assert.Single(j.GetProperty("slots").EnumerateArray()).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task A_1122_body_has_the_off_hand_at_40_and_enchanted_items_glint()
    {
        using var data = GameData.Override(Release1122GameData.Instance);
        var (s, inv, _) = Make(version: "1.12.2");
        await using var _s = s;
        var w = new WorldState(inventory: InventoryModel.Release1122);
        w.Player.HeldSlot = 0;
        var ench = new ItemTag([10, 0, 0, 0], new Dictionary<int, int> { [16] = 3 }) { Enchanted = true };
        w.Inventory.SetSlot(0, 36, new ItemStack(276, 1, 10) { Tag = ench });  // hotbar 0: an enchanted diamond sword
        w.Inventory.SetSlot(0, 6, new ItemStack(311, 1, 0) { Tag = ench });   // chestplate
        w.Inventory.SetSlot(0, InventoryModel.OffhandSlot, new ItemStack(442, 1, 5)); // shield in the off hand
        w.Inventory.SetSlot(0, 20, new ItemStack(262, 48, 0));                // main: arrows
        inv.Register("maid_s1_dara", w, () => true); // a Story pawn: not in the hive lists, still answers by login

        var r = await s.HandleAsync(Get("/api/viewer/bodies/maid_s1_dara/inventory"));
        Assert.Equal(200, r.Status);
        var j = J(r);
        Assert.Equal("1.12.2", j.GetProperty("version").GetString());
        var slots = j.GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal([0, 20], slots.Select(x => x.GetProperty("slot").GetInt32()).ToArray());
        Assert.True(slots[0].GetProperty("enchanted").GetBoolean());
        Assert.Equal("diamond_sword", slots[0].GetProperty("name").GetString());
        Assert.Equal(1561 - 10, slots[0].GetProperty("durability").GetProperty("left").GetInt32());
        Assert.False(slots[1].GetProperty("enchanted").GetBoolean());
        var armor = j.GetProperty("armor").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, armor[0].ValueKind);
        Assert.Equal(38, armor[1].GetProperty("slot").GetInt32());
        Assert.True(armor[1].GetProperty("enchanted").GetBoolean());
        var off = j.GetProperty("offhand");
        Assert.Equal(40, off.GetProperty("slot").GetInt32());
        Assert.Equal(442, off.GetProperty("id").GetInt32());
        Assert.Equal(5, off.GetProperty("meta").GetInt32());
    }

    [Fact]
    public async Task Viewers_within_half_a_second_share_one_snapshot_and_the_route_is_read_only_behind_the_token()
    {
        var (s, inv, clock) = Make();
        await using var _s = s;
        var w = Beta();
        w.Inventory.SetSlot(0, 36, new ItemStack(4, 1, 0));
        inv.Register("maid_chan", w, () => true);
        var a = J(await s.HandleAsync(Get("/api/viewer/bodies/main/inventory"))).GetProperty("at").GetInt64();
        w.Inventory.SetSlot(0, 36, new ItemStack(4, 2, 0));
        clock.Now = T0.AddMilliseconds(200);
        var shared = J(await s.HandleAsync(Get("/api/viewer/bodies/main/inventory")));
        Assert.Equal(a, shared.GetProperty("at").GetInt64());
        clock.Now = T0.AddMilliseconds(800);
        var fresh = J(await s.HandleAsync(Get("/api/viewer/bodies/main/inventory")));
        Assert.Equal(2, fresh.GetProperty("slots")[0].GetProperty("count").GetInt32());

        Assert.Equal(405, (await s.HandleAsync(Get("/api/viewer/bodies/main/inventory", method: "POST"))).Status);
        Assert.Equal(403, (await s.HandleAsync(Get("/api/viewer/bodies/main/inventory", token: null))).Status);
        Assert.Equal(403, (await s.HandleAsync(Get("/api/viewer/bodies/main/inventory", token: "wrong"))).Status);
    }
}
