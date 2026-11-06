using System.Reflection;
using MineClaw.Client.Survival;
using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Client.Tests;

/// <summary>
/// Exhaustive equivalence goldens for the client's id membership rules (complexity refactor WP05), taken from the pattern-match
/// implementations before they became sets: natural ground, dig forms, natural resources (both dimensions), right-click items,
/// the default deposit filter, shelter block ranks and wood names, over every id from -2 to 4200 under both versions' data (and a list of names).
/// </summary>
public class ClientMembershipGoldenTests
{
    private static readonly Func<int, bool> NaturalGround = typeof(MinecraftClient)
        .GetMethod("NaturalGround", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<int, bool>>();

    private static string Try(Func<object?> f)
    {
        try { return f()?.ToString() ?? "null"; }
        catch (Exception e) { return "!" + e.GetType().Name; }
    }

    [Fact]
    public void Id_membership_rules_match_the_golden() => SimRulesGoldenTests.Check("client-membership", "8B46C49B7F6503069CDC32DC3EABBC2A5F553B1C088E0246AABE0BA73A9CFEE4", sb =>
    {
        foreach (IGameData data in new IGameData[] { Beta181GameData.Instance, Release1122GameData.Instance })
        using (GameData.Override(data))
        for (int id = -2; id <= 4200; id++)
        {
            sb.Append(id).Append(':')
                .Append(NaturalGround(id) ? 'g' : '-')
                .Append(MinecraftClient.NaturalResource(id, 0) ? 'r' : '-')
                .Append(MinecraftClient.NaturalResource(id, -1) ? 'n' : '-')
                .Append(MinecraftClient.NaturalResource(id, 1) ? 'e' : '-')
                .Append(MinecraftClient.UsesRightClickAction(id) ? 'u' : '-')
                .Append(' ').Append(Shelter.BlockRank(id))
                .Append(' ').Append(Try(() => MinecraftClient.DefaultDepositFilter(new ItemStack((short)id, 1, 0))))
                .Append(' ').Append(Try(() => MinecraftClient.DefaultDepositFilter(new ItemStack((short)id, 0, 0))));
            for (int b = 0; b < 260; b += id is >= 0 and < 260 ? 1 : 300)
                if (MinecraftClient.SameDigForm(id, b)) sb.Append(" s").Append(b);
            sb.Append('\n');
        }
        string?[] woods =
        [
            null, "", " ", "oak", "OAK", " oak ", "oak tree", "spruce", "pine", "fir", "spruce tree", "pine tree", "fir tree", "birch",
            "birch tree", "jungle", "jungle tree", "acacia", "acacia tree", "dark oak", "darkoak", "dark_oak", "roofed oak",
            "dark oak tree", "roofed oak tree", "Dark Oak", "\toak\n", "oaks", "cherry", "mangrove",
        ];
        foreach (var w in woods) sb.Append('[').Append(w).Append("]=").Append(TreeScan.ParseWood(w)?.ToString() ?? "null").Append('\n');
    });
}
