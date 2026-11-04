using System.Globalization;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>Palette values: a block name plus an optional facing, variant or raw meta (<see cref="BlueprintParser.Resolve"/>).</summary>
public static partial class BlueprintParser
{
    private static readonly Dictionary<string, int> LogVariants = new() { ["oak"] = 0, ["spruce"] = 1, ["birch"] = 2 };
    private static readonly Dictionary<string, int> StoneBrickVariants = new() { ["plain"] = 0, ["mossy"] = 1, ["cracked"] = 2 };

    /// <summary>A versioned design's palette value: any state of its version's design palette.</summary>
    private static (PaletteEntry? Entry, string? Why) ResolveDesignBlock(char sym, string name, string? arg, string source)
    {
        if (!DesignPalette.Current.TryResolve(arg == null ? name : name + ":" + arg, out var st, out var bad)) return (null, bad);
        int m = BuildBlocks.KindOf(st.Id) == BuildKind.Door ? st.Meta & 7 : st.Meta; // the palette names the lower half
        return (new PaletteEntry(sym, st.Id, m, null, source), null);
    }

    /// <summary>A name the builder places: an item that is a block (or a door item, standing for its block), else a block name.</summary>
    private static bool TryBuildableBlock(string name, out int id, out int meta)
    {
        meta = 0;
        if (Items.ByNameWithDamage(name) is { } it && (it.Item.Id < 256 || it.Item.Id is Items.WoodenDoor or Items.IronDoor))
        {
            id = BlockOfItem(it.Item.Id);
            meta = Math.Max(0, it.Damage);
            return true;
        }
        id = 0;
        if (Blocks.ByName(name) is not { } b) return false;
        id = b.Id;
        return true;
    }

    private static int BlockOfItem(int itemId) => itemId switch { Items.WoodenDoor => Blocks.WoodenDoor, Items.IronDoor => Blocks.IronDoor, var x => x };

    /// <summary>Applies a palette value's ":arg" (a facing, a raw meta or a named variant); returns why it can't, else null.</summary>
    private static string? ApplyPaletteArg(int id, string arg, ref int meta, ref Dir? facing)
    {
        var a = arg.Trim().ToLowerInvariant();
        if (Dirs.Parse(a) is { } d)
        {
            if (!BuildBlocks.Directional(id)) return $"{Blocks.Get(id).Name} has no facing";
            facing = d;
            return null;
        }
        if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) meta = m;
        else if (VariantMeta(id, a) is { } v) meta = v;
        else return $"'{arg}' isn't a facing, variant or meta for {Blocks.Get(id).Name}";
        return null;
    }

    /// <summary>The meta of a named variant (wool:red, log:birch, slab:cobble, stone_bricks:mossy), or null.</summary>
    private static int? VariantMeta(int id, string a)
    {
        int found = id == Blocks.Wool ? WoolColorIndex(a)
            : id == Blocks.Log ? LogVariants.GetValueOrDefault(a, -1)
            : id == Blocks.Slab ? Array.IndexOf(SlabTypes, a.Replace("_", "").Replace("cobblestone", "cobble").Replace("wooden", "wood").Replace("planks", "wood"))
            : id == Blocks.StoneBricks ? StoneBrickVariants.GetValueOrDefault(a, -1)
            : -1;
        return found >= 0 ? found : null;
    }

    private static int WoolColorIndex(string a) =>
        Array.FindIndex(Items.WoolColors, c => c.Replace(" ", "").Equals(a.Replace("_", "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
}
