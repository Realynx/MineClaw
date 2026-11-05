namespace MineClaw.Hive.Story;

/// <summary>
/// What a built cell may turn into on its own and still be the design's block (docs/web/story-mode.md "Builds reconcile with the
/// world"; live 11-05 after the reconcile went out: dirt floors reopened ×22 in 7 minutes as their dirt grew grass or was tilled, and
/// the clears of the farmland were refused as a farm). A small explicit table per version, nothing else:
/// <list type="bullet">
/// <item>Ground: dirt, grass and farmland (b1.8.1), plus coarse dirt and podzol (dirt's metas), mycelium and grass path (1.12.2)
/// are one block for a build: grass spreads over dirt, a hoe tills it, a shovel makes a path.</item>
/// <item>Runtime states of one block: a lit furnace, a lit redstone lamp, glowing redstone ore, an unlit redstone torch, a powered
/// repeater or comparator (their facing is still compared).</item>
/// </list>
/// A decayed leaf, a broken block, a block swapped for another stay real mismatches. Snow on top, water beside, or anything in an
/// open cell never flips a solid one: an open cell is "to place" anyway.
/// </summary>
internal static class StoryDrift
{
    /// <summary>The ground family (any meta): b1.8.1 grass 2, dirt 3, farmland 60.</summary>
    private static readonly HashSet<int> BetaGround = [2, 3, 60];

    /// <summary>1.12.2 adds mycelium 110 and grass path 208 (coarse dirt and podzol are dirt 3's metas 1 and 2).</summary>
    private static readonly HashSet<int> ModernGround = [2, 3, 60, 110, 208];

    /// <summary>A block's runtime twin → the block designs use (lit furnace → furnace, …).</summary>
    private static readonly Dictionary<int, int> Twins = new()
    {
        [62] = 61,   // lit furnace
        [124] = 123, // lit redstone lamp
        [74] = 73,   // glowing redstone ore
        [75] = 76,   // unlit redstone torch → redstone torch
        [94] = 93,   // powered repeater
        [150] = 149, // powered comparator
    };

    /// <summary>Both are ground a build counts as the same (a designed dirt cell holding grass or farmland is built).</summary>
    public static bool SameGround(int want, int got, bool modern)
    {
        var family = modern ? ModernGround : BetaGround;
        return family.Contains(want) && family.Contains(got);
    }

    /// <summary>The block a design means by <paramref name="id"/> (a runtime twin is its block).</summary>
    public static int Canon(int id) => Twins.TryGetValue(id, out var c) ? c : id;
}
