using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryStorageSignPublicationTests
{
    [Fact]
    public void Publication_copies_effective_sign_rules_and_keeps_unloaded_persisted_labels()
    {
        var (c, h, w) = StoryRig.Make();
        var chest = new P3(3, 64, 2);
        var half = chest.Add(1, 0, 0);
        var sign = half.Add(0, 0, -1);
        w.Set(chest.X, chest.Y, chest.Z, 54);
        w.Set(half.X, half.Y, half.Z, 54);
        w.Set(sign.X, sign.Y, sign.Z, 68, 2);
        w.SignRows.Add(new(sign, ["wood", "", "", ""]));
        c.ReconcileStorageSigns(true);
        var published = Assert.Single(h.StorageSignRules);
        Assert.Contains(chest, new[] { published.Chest, published.DoubleWith });
        Assert.Contains(half, new[] { published.Chest, published.DoubleWith });
        Assert.Equal(["wood"], published.Allow);
        c.StorageAt(chest)!.Signs[0].Allow.Add("food");
        Assert.Equal(["wood"], published.Allow); // a body holds no live list from the director
        w.Unloaded.Add((sign.X, sign.Z));
        c.ReconcileStorageSigns(true);
        Assert.Contains(Assert.Single(h.StorageSignRules).Signs, x => x.At == sign);
    }

    [Fact]
    public void Unregistered_ordinary_labels_publish_fingerprints_without_ui_restrictions()
    {
        var (c, h, w) = StoryRig.Make();
        var chest = new P3(3, 64, 2);
        var sign = chest.Add(0, 0, -1);
        w.Set(chest.X, chest.Y, chest.Z, 54);
        w.Set(sign.X, sign.Y, sign.Z, 68, 2);
        w.SignRows.Add(new(sign, ["Storehouse", "First floor", "", ""]));
        c.ReconcileStorageSigns(true);
        Assert.Null(c.StorageAt(chest));
        var published = Assert.Single(h.StorageSignRules);
        Assert.Equal(sign, Assert.Single(published.Signs).At);
        Assert.Equal(["*"], published.Allow);
        Assert.Empty(published.Deny);
    }
}
