using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryFuelClaimsTests
{
    [Fact]
    public void Mixed_wood_fuel_reserves_the_actual_spruce_and_birch_instead_of_nonexistent_oak()
    {
        var (c, _, w) = StoryRig.Make(data: Release1122GameData.Instance);
        var chest = new P3(0, 64, 9);
        var furnace = new P3(3, 64, 0);
        c.StorageOp("set", [chest], null, null, null);
        foreach (var stack in new[] { new Stack(12, 0, 8), new Stack(17, 1, 3), new Stack(17, 2, 3) })
            c.AddContents(c.StorageAt(chest)!, stack);
        w.Set(furnace.X, furnace.Y, furnace.Z, 61);
        Assert.Null(c.WorkbenchOp("designate", furnace).Error);
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["area"] = new JsonArray(0, 64, 5, 7, 64, 5), ["material"] = new JsonArray(20, 0),
        }), null).Error);

        c.EnsureMaterials();

        Assert.Equal(8, c.Builds.Single().Materials.Single().Crafting);
        var fuelClaims = c.State()["reservations"]!.AsArray()
            .Where(claim => claim!["item"]![0]!.GetValue<int>() == 17)
            .Select(claim => (Meta: claim!["item"]![1]!.GetValue<int>(), Count: claim["count"]!.GetValue<int>()))
            .OrderBy(claim => claim.Meta).ToArray();
        Assert.Equal([(1, 3), (2, 3)], fuelClaims);
        Assert.Equal(0, c.UnreservedFor((id, meta) => id == 17 && meta == 1, 134));
        Assert.Equal(0, c.UnreservedFor((id, meta) => id == 17 && meta == 2, 135));
    }
}
