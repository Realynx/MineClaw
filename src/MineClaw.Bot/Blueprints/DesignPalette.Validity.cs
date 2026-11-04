namespace MineClaw.Bot.Blueprints;

/// <summary>Which metas each block family may stand in (<see cref="DesignPalette.Valid"/>).</summary>
public sealed partial class DesignPalette
{
    /// <summary>A state being checked: the id's palette entries, and whether the version is 1.12.2.</summary>
    private readonly record struct StateQuery(int Id, int Meta, bool Modern, List<DesignBlock> Entries)
    {
        public bool Listed()
        {
            foreach (var e in Entries) if (e.Meta == Meta) return true;
            return false;
        }

        /// <summary>An entry with the same variant bits (the rest of the meta is facing/half).</summary>
        public bool Variant(int mask)
        {
            foreach (var e in Entries) if ((e.Meta & mask) == (Meta & mask)) return true;
            return false;
        }
    }

    private static readonly Dictionary<DesignFamily, Func<StateQuery, bool>> FamilyRules = new()
    {
        [DesignFamily.None] = q => q.Listed(),
        [DesignFamily.Stairs] = q => q.Meta <= (q.Modern ? 7 : 3),
        [DesignFamily.Slab] = q => q.Variant(7) && (q.Modern || q.Meta < 8),
        [DesignFamily.Log] = q => q.Variant(3) && (q.Modern || q.Meta < 4),
        [DesignFamily.Leaves] = q => q.Variant(3),
        [DesignFamily.Pillar] = q => q.Meta is 0 or 4 or 8,
        [DesignFamily.Quartz] = q => q.Meta <= 4,
        [DesignFamily.Door] = _ => true,
        [DesignFamily.Tall] = q => (q.Meta & 8) != 0 || q.Listed(),
        [DesignFamily.Bed] = q => (q.Meta & 4) == 0,
        [DesignFamily.Torch] = q => q.Meta is >= 1 and <= 5,
        [DesignFamily.Wall] = q => q.Meta is >= 2 and <= 5,
        [DesignFamily.Front] = q => q.Meta is 0 or (>= 2 and <= 5),
        [DesignFamily.Facing6] = q => q.Meta <= 5 && (q.Modern || q.Id != 23 || q.Meta >= 2), // a b1.8.1 dispenser faces sideways only
        [DesignFamily.Hopper] = q => q.Meta is 0 or (>= 2 and <= 5),
        [DesignFamily.Horizontal] = q => q.Meta <= MaxHorizontalMeta(q.Id),
        [DesignFamily.Gate] = q => q.Meta <= 7,
        [DesignFamily.Trapdoor] = q => q.Meta <= (q.Modern ? 15 : 7),
        [DesignFamily.Button] = q => q.Modern ? (q.Meta & 7) <= 5 : (q.Meta & 7) is >= 1 and <= 4,
        [DesignFamily.Lever] = q => q.Modern || (q.Meta & 7) is >= 1 and <= 6,
        [DesignFamily.Rail] = q => q.Meta <= 9,
        [DesignFamily.PoweredRail] = q => (q.Meta & 7) <= 5,
        [DesignFamily.Rotation16] = _ => true,
        [DesignFamily.Vine] = _ => true,
        [DesignFamily.Skull] = q => q.Meta is >= 1 and <= 5,
        [DesignFamily.Age] = q => q.Meta <= MaxAge(q.Id),
    };

    public bool Valid(int id, int meta)
    {
        if (meta is < 0 or > 15 || !_byId.TryGetValue(id, out var l)) return false;
        return FamilyRules.TryGetValue(DesignFacing.FamilyOf(id), out var rule) && rule(new StateQuery(id, meta, Version == DesignVersions.Release, l));
    }

    /// <summary>A <see cref="DesignFamily.Horizontal"/> block's highest meta.</summary>
    private static int MaxHorizontalMeta(int id) => id switch
    {
        86 or 91 or (>= 235 and <= 250) => 3,
        145 or 127 => 11,
        _ => 15,
    };

    /// <summary>The highest value of an <see cref="DesignFamily.Age"/> block's meta (its age, power or bites), else 1.</summary>
    private static readonly Dictionary<int, int> MaxAges = AgeTable(
        (15, [55, 81, 83, 147, 148, 151, 178]),
        (7, [59, 60, 78, 104, 105, 117, 141, 142]),
        (6, [92]),
        (5, [200]),
        (3, [115, 118, 207]));

    private static Dictionary<int, int> AgeTable(params (int Max, int[] Ids)[] groups)
    {
        var d = new Dictionary<int, int>();
        foreach (var (max, ids) in groups) foreach (var id in ids) d[id] = max;
        return d;
    }

    private static int MaxAge(int id) => MaxAges.GetValueOrDefault(id, 1);
}
