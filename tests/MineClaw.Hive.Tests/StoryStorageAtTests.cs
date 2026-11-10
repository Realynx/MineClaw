using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// <see cref="StoryColony.StorageAt"/> answers from a cell map (every stock scan asks it for each of 374 chests, live 11-10). The map
/// must give what a walk of the list gives, the first storage holding the cell, after any change to the list or to a storage's cells.
/// </summary>
public sealed class StoryStorageAtTests
{
    private static StorageState? Walk(StoryColony c, P3 at) => c.Storages.FirstOrDefault(s => s.Chest == at || s.DoubleWith == at);

    private static readonly P3[] Cells = Enumerable.Range(0, 12).Select(i => new P3(i, 64, 0)).Append(new P3(99, 64, 99)).ToArray();

    private static void Same(StoryColony c)
    {
        foreach (var at in Cells) Assert.Same(Walk(c, at), c.StorageAt(at));
    }

    [Fact]
    public void Each_kind_of_change_is_seen()
    {
        var (c, _, _) = StoryRig.Make();
        for (int i = 0; i < 6; i++) c.Storages.Add(new StorageState { Chest = Cells[i] });
        Same(c);
        c.Storages[2].DoubleWith = Cells[7]; // a double half joins
        Same(c);
        c.Storages.Insert(0, new StorageState { Chest = Cells[8], DoubleWith = Cells[3] }); // an earlier storage shadows a later one's cell
        Same(c);
        c.Storages.RemoveAt(4); // one goes and another comes: the same count
        c.Storages.Add(new StorageState { Chest = Cells[9] });
        Same(c);
        c.Storages[1] = new StorageState { Chest = Cells[10] }; // replaced in place
        Same(c);
        c.Storages[0].Chest = Cells[11]; // a chest moved
        Same(c);
        c.Storages[2].DoubleWith = null;
        Same(c);
        c.Storages.Reverse();
        Same(c);
        c.Storages.Clear();
        Same(c);
    }

    [Fact]
    public void A_seeded_run_of_changes_matches_the_list_walk()
    {
        var (c, _, _) = StoryRig.Make();
        var rng = new Random(7);
        P3 Cell() => Cells[rng.Next(Cells.Length - 1)];
        for (int step = 0; step < 600; step++)
        {
            switch (rng.Next(7))
            {
                case 0 or 1: c.Storages.Insert(rng.Next(c.Storages.Count + 1), new StorageState { Chest = Cell(), DoubleWith = rng.Next(3) == 0 ? Cell() : null }); break;
                case 2 when c.Storages.Count > 0: c.Storages.RemoveAt(rng.Next(c.Storages.Count)); break;
                case 3 when c.Storages.Count > 0: c.Storages[rng.Next(c.Storages.Count)].Chest = Cell(); break;
                case 4 when c.Storages.Count > 0: c.Storages[rng.Next(c.Storages.Count)].DoubleWith = rng.Next(2) == 0 ? Cell() : null; break;
                case 5 when c.Storages.Count > 0: c.Storages[rng.Next(c.Storages.Count)] = new StorageState { Chest = Cell() }; break;
                case 6 when c.Storages.Count > 1:
                    int a = rng.Next(c.Storages.Count), b = rng.Next(c.Storages.Count);
                    (c.Storages[a], c.Storages[b]) = (c.Storages[b], c.Storages[a]);
                    break;
            }
            Same(c);
        }
    }
}
