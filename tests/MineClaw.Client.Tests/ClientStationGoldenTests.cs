using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Pins the 1.12.2 station actions before the WP05 refactor reshaped them: every anvil plan outcome the FakeServer can reach (it has no
/// anvil window, so a planned job ends at the open), a brew, an enchant and the bottle filling at a cauldron and at a water source,
/// each with its result and every non-movement packet (see <see cref="ClientActionTraceGoldenTests.Trace"/>).
/// </summary>
public class ClientStationGoldenTests
{
    private static readonly BlockPos At = new(9, 64, 8);

    private static async Task<string> RunAsync(Action<FakeServer> setup, Func<MinecraftClient, Task<ActionResult>> act, int xp = 0)
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.XpLevel = xp;
            srv.OnLogin = srv.SendXp;
            setup(srv);
        }, release1122: true);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Snapshot.XpLevel == xp));
        Assert.True(await Harness.WaitAsync(() => c.World.IsLoaded(At.X, At.Z)));
        int from = s.Snapshot().Count;
        var r = await act(c);
        await Task.Delay(100);
        return r + "\n----\n" + ClientActionTraceGoldenTests.Trace(s.Snapshot().Skip(from));
    }

    private static void Anvil(FakeServer srv, params ItemStack[] items)
    {
        srv.Blocks[(At.X, At.Y, At.Z)] = MineClaw.Data.Anvil.Block;
        for (int i = 0; i < items.Length; i++) srv.Inventory[36 + i] = items[i];
    }

    public static IEnumerable<object[]> Cases() => Scenarios.Keys.Select(k => new object[] { k });

    private static readonly Dictionary<string, Func<Task<string>>> Scenarios = new()
    {
        ["anvil-none"] = () => RunAsync(_ => { }, c => c.AnvilAsync(At, "repair", 278)),
        ["anvil-no-item"] = () => RunAsync(s => Anvil(s), c => c.AnvilAsync(At, "repair", 278)),
        ["anvil-bad-mode"] = () => RunAsync(s => Anvil(s, Harness.Stack(278, 1, 100)), c => c.AnvilAsync(At, "polish", 278)),
        ["anvil-no-material-kind"] = () => RunAsync(s => Anvil(s, Harness.Stack(Items.Stick, 1)), c => c.AnvilAsync(At, "repair", Items.Stick)),
        ["anvil-undamaged"] = () => RunAsync(s => Anvil(s, Harness.Stack(278, 1)), c => c.AnvilAsync(At, "repair", 278)),
        ["anvil-no-diamond"] = () => RunAsync(s => Anvil(s, Harness.Stack(278, 1, 900)), c => c.AnvilAsync(At, "repair", 278)),
        ["anvil-repair-no-xp"] = () => RunAsync(s => Anvil(s, Harness.Stack(278, 1, 900), Harness.Stack(264, 3)), c => c.AnvilAsync(At, "repair", 278)),
        ["anvil-repair-opens"] = () => RunAsync(s => Anvil(s, Harness.Stack(278, 1, 900), Harness.Stack(264, 3)), c => c.AnvilAsync(At, "repair", 278), xp: 30),
        ["anvil-combine-one"] = () => RunAsync(s => Anvil(s, Harness.Stack(278, 1, 900)), c => c.AnvilAsync(At, "combine", 278), xp: 30),
        ["anvil-combine-opens"] = () => RunAsync(s => Anvil(s, Harness.Stack(278, 1, 900), Harness.Stack(278, 1, 1200)), c => c.AnvilAsync(At, "combine", 278), xp: 30),
        ["anvil-no-book"] = () => RunAsync(s => Anvil(s, Harness.Stack(278, 1, 900)), c => c.AnvilAsync(At, "book", 278), xp: 30),
        ["brew-swiftness"] = () => RunAsync(srv =>
        {
            srv.Blocks[(At.X, At.Y, At.Z)] = Brewing.StandBlock;
            srv.BrewingStands[(At.X, At.Y, At.Z)] = Enumerable.Repeat(ItemStack.Empty, 5).ToArray();
            for (int i = 0; i < 3; i++) srv.Inventory[36 + i] = FakeServer.Potion("water");
            srv.Inventory[39] = Harness.Stack(Brewing.NetherWart, 4);
            srv.Inventory[40] = Harness.Stack(353, 2);
            srv.Inventory[41] = Harness.Stack(Brewing.BlazePowder, 1);
        }, c => c.BrewAsync(At, "water", Brewing.Potion, Brewing.Chain(Brewing.Potion, "swiftness")!, 3)),
        ["brew-no-mix"] = () => RunAsync(srv =>
        {
            srv.Blocks[(At.X, At.Y, At.Z)] = Brewing.StandBlock;
            srv.BrewingStands[(At.X, At.Y, At.Z)] = Enumerable.Repeat(ItemStack.Empty, 5).ToArray();
            srv.Inventory[36] = FakeServer.Potion("water");
            srv.Inventory[37] = Harness.Stack(353, 1);
            srv.Inventory[38] = Harness.Stack(Brewing.BlazePowder, 1);
        }, c => c.BrewAsync(At, "water", Brewing.Potion,
            [new Brewing.Step(new Brewing.Ingredient(353, 0, "sugar"), "water", Brewing.Potion, "swiftness", Brewing.Potion)], 1)),
        ["brew-no-base"] = () => RunAsync(srv =>
        {
            srv.Blocks[(At.X, At.Y, At.Z)] = Brewing.StandBlock;
            srv.BrewingStands[(At.X, At.Y, At.Z)] = Enumerable.Repeat(ItemStack.Empty, 5).ToArray();
        }, c => c.BrewAsync(At, "water", Brewing.Potion, Brewing.Chain(Brewing.Potion, "swiftness")!, 3)),
        ["enchant-fortune"] = () => RunAsync(Table, c => c.EnchantAsync(At, 278, 3, Enchanting.ByName("fortune")!.Id, preferOnly: false), xp: 30),
        ["enchant-silk-only"] = () => RunAsync(Table, c => c.EnchantAsync(At, 278, 3, Enchanting.ByName("silk_touch")!.Id, preferOnly: true), xp: 10),
        ["enchant-no-xp"] = () => RunAsync(Table, c => c.EnchantAsync(At, 278, 3, null, preferOnly: false)),
        ["bottles-cauldron"] = () => RunAsync(srv =>
        {
            srv.Blocks[(At.X, At.Y, At.Z)] = Brewing.CauldronBlock;
            srv.Metas[(At.X, At.Y, At.Z)] = 3;
            srv.Inventory[36] = Harness.Stack(Brewing.GlassBottle, 2);
        }, c => c.FillBottlesAsync(At, 2)),
        ["bottles-none"] = () => RunAsync(srv =>
        {
            srv.Blocks[(At.X, At.Y, At.Z)] = Brewing.CauldronBlock;
            srv.Metas[(At.X, At.Y, At.Z)] = 3;
        }, c => c.FillBottlesAsync(At, 2)),
        ["bottles-dry"] = () => RunAsync(srv => srv.Inventory[36] = Harness.Stack(Brewing.GlassBottle, 2), c => c.FillBottlesAsync(At, 2)),
    };

    private static void Table(FakeServer srv)
    {
        srv.Blocks[(At.X, At.Y, At.Z)] = Enchanting.TableBlock;
        srv.EnchantingTables.Add((At.X, At.Y, At.Z));
        srv.Inventory[36] = Harness.Stack(278, 1);
        srv.Inventory[37] = Harness.Stack(Enchanting.Lapis, 5, Enchanting.LapisMeta);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Station_actions_send_the_pinned_packets(string name)
    {
        var trace = await Scenarios[name]();
        ClientStationGoldenData.Traces.TryGetValue(name, out var expected);
        if (expected?.ReplaceLineEndings("\n") != trace)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mineclaw-station-{name}-{DateTime.UtcNow:HHmmssfff}.txt");
            File.WriteAllText(path, trace);
            Assert.Fail($"station trace drifted (actual: {path})");
        }
    }
}
