using MineClaw.App.Tui;
using MineClaw.App.Viewport;

namespace MineClaw.App.Tests;

/// <summary>Pins <c>TuiFrame.CellStyle</c> (WP07: the switch became a table): every cell kind, players by name, and an unknown kind.</summary>
public class TuiCellStyleTests
{
    [Theory]
    [InlineData(CellKind.Void, null)] [InlineData(CellKind.Stone, "grey")] [InlineData(CellKind.Dirt, "olive")] [InlineData(CellKind.Water, "blue")]
    [InlineData(CellKind.Lava, "bold red")] [InlineData(CellKind.Log, "darkorange3")] [InlineData(CellKind.Leaves, "green")]
    [InlineData(CellKind.Chest, "bold yellow")] [InlineData(CellKind.Furnace, "silver")] [InlineData(CellKind.Workbench, "orange3")]
    [InlineData(CellKind.Other, "white")] [InlineData(CellKind.Self, "bold black on yellow")] [InlineData(CellKind.Hive, "bold black on fuchsia")]
    [InlineData(CellKind.Mob, "bold red")] [InlineData(CellKind.Item, "yellow")] [InlineData((CellKind)99, null)] [InlineData((CellKind)(-1), null)]
    public void Each_kind_has_its_style(CellKind kind, string? style) => Assert.Equal(style, TuiFrame.CellStyle(new ViewCell('x', kind)));

    [Theory]
    [InlineData("poofimafox", "bold lime")] [InlineData("Kiwi", "bold fuchsia")] [InlineData("kiwi", "bold fuchsia")] [InlineData(null, "bold hotpink")]
    [InlineData("", "bold hotpink")] [InlineData("maid_chan_67", "bold orange1")]
    public void Players_take_a_stable_colour_by_name(string? name, string style) =>
        Assert.Equal(style, TuiFrame.CellStyle(new ViewCell('P', CellKind.Player, name)));
}
