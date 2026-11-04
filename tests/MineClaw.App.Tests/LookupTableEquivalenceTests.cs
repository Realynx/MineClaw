using MineClaw.App.Supervisor;
using MineClaw.App.Viewport;

namespace MineClaw.App.Tests;

/// <summary>
/// WP07: switches that became lookup tables answer exactly as the switches did, over every input: <see cref="ViewportRenderer.KindOf"/>
/// (every char) and <see cref="BlockEntityColumns.KindOf"/> (every id from -1000 to 70000). The references are the original switches.
/// </summary>
public class LookupTableEquivalenceTests
{
    private static CellKind GlyphReference(char g) => g switch
    {
        '#' => CellKind.Stone, '.' => CellKind.Dirt, '~' => CellKind.Water, '!' => CellKind.Lava, 'T' => CellKind.Log,
        '*' => CellKind.Leaves, 'C' => CellKind.Chest, 'F' => CellKind.Furnace, 'W' => CellKind.Workbench,
        ' ' => CellKind.Void, _ => CellKind.Other,
    };

    private static string? BlockReference(int blockId) => blockId switch
    {
        63 or 68 => "sign", 26 => "bed", 176 or 177 => "banner", 144 => "skull", 140 => "flower_pot", 54 => "chest", 146 => "trapped_chest",
        130 => "ender_chest", 52 => "mob_spawner", 84 => "jukebox", 25 => "note_block", _ => null,
    };

    [Fact]
    public void Glyph_kinds_match_the_switch_for_every_char()
    {
        for (int c = char.MinValue; c <= char.MaxValue; c++)
            if (ViewportRenderer.KindOf((char)c) != GlyphReference((char)c)) Assert.Fail($"char {c}: {ViewportRenderer.KindOf((char)c)} != {GlyphReference((char)c)}");
    }

    [Fact]
    public void Block_entity_kinds_match_the_switch_for_every_id()
    {
        for (int id = -1000; id <= 70000; id++)
            if (BlockEntityColumns.KindOf(id) != BlockReference(id)) Assert.Fail($"id {id}: {BlockEntityColumns.KindOf(id)} != {BlockReference(id)}");
    }
}
