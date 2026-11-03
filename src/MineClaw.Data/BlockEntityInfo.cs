namespace MineClaw.Data;

/// <summary>One banner layer: the pattern code ("cr", "bo", "gra"…) and its dye colour (<c>EnumDyeColor</c> dye damage, 0 black..15 white).</summary>
public sealed record BannerPattern(string Pattern, int Color);

/// <summary>
/// What a block entity looks like, for a renderer (the browser world viewer; the bot itself never reads it): its kind plus the
/// few facts that change how it is drawn. Every fact is null when the version or the server doesn't send it.
/// </summary>
/// <param name="Kind">sign | bed | banner | skull | flower_pot | chest | trapped_chest | ender_chest | mob_spawner | jukebox |
/// note_block | another block entity id without its "minecraft:" prefix.</param>
public sealed record BlockEntityInfo(string Kind)
{
    /// <summary>A sign's four lines, plain text.</summary>
    public IReadOnlyList<string>? Lines { get; init; }
    /// <summary>A 1.12.2 sign's four lines with their styles as legacy § codes (null when none has a style): the viewer's rich text.</summary>
    public IReadOnlyList<string>? Formatted { get; init; }
    /// <summary>A bed's colour (1.12.2 <c>EnumDyeColor</c> meta, 14 red) or a banner's base colour (dye damage).</summary>
    public int? Color { get; init; }
    /// <summary>A banner's layers, bottom first.</summary>
    public IReadOnlyList<BannerPattern>? Patterns { get; init; }
    /// <summary>A skull's type (0 skeleton, 1 wither skeleton, 2 zombie, 3 player, 4 creeper, 5 dragon).</summary>
    public int? Skull { get; init; }
    /// <summary>A skull's rotation on the floor (0..15, sixteenths of a turn).</summary>
    public int? Rotation { get; init; }
    /// <summary>A player skull's owner name.</summary>
    public string? Owner { get; init; }
    /// <summary>A flower pot's plant (item name without "minecraft:"), with <see cref="Data"/> its damage.</summary>
    public string? Item { get; init; }
    public int? Data { get; init; }
    /// <summary>A mob spawner's mob (entity name without "minecraft:").</summary>
    public string? Mob { get; init; }
    /// <summary>A jukebox's record (item name without "minecraft:").</summary>
    public string? Record { get; init; }
    /// <summary>A note block's pitch (0..24).</summary>
    public int? Note { get; init; }

    public static BlockEntityInfo Sign(IReadOnlyList<string> lines, IReadOnlyList<string>? formatted = null) =>
        new("sign") { Lines = lines.ToArray(), Formatted = formatted?.ToArray() };
}
