using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MineClaw.MemoryExplorer.Controls;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.Scripting;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>The debugger panel renders (headless) with a live snapshot, and the tiles get their "script" badge.</summary>
[Collection("avalonia")]
public class ScriptDebugViewTests
{
    private sealed class NoSource : IScriptDebugSource
    {
        public Task<BodyScriptDto> GetAsync(string body, bool code, CancellationToken ct) => throw new InvalidOperationException("not polled here");
    }

    [Fact]
    public Task Panel_renders_the_source_with_the_current_line() => HeadlessUi.Run(() =>
    {
        var vm = new ScriptDebugViewModel(new NoSource(), "67", "maid_chan_67 · 67", post: a => a());
        vm.Apply(new BodyScriptDto("67", true, new ScriptDebugSnapshot
        {
            SessionId = 1, Name = "walk", Status = "running", Live = true, Line = 2, LineLive = true, Code = "const a = 1\ngo(1, 64, 2)\nreturn 'ok'",
            Action = new ScriptActionView("goto", "1,64,2", 0.5, false, true), Params = JsonNode.Parse("""{"x": 1}"""),
            Locals = [new ScriptLocal("a", "1", "local")], Stack = [new ScriptFrame("go", "script", 2, 1, true), new ScriptFrame("(top level)", "script", 2, 1, true)],
        }), DateTime.UtcNow);
        var view = new ScriptDebugView { DataContext = vm };
        var window = new Window { Width = 640, Height = 720, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var lines = view.GetVisualDescendants().OfType<CodeLineView>().ToList();
        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, l => l.Inlines?.Count > 0 && l.Inlines.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == "go"));
        Assert.True(vm.Lines[1].IsCurrent);
        window.Close();
    });

    [Fact]
    public void Tiles_get_the_script_badge_by_login_or_short_name()
    {
        var cache = new HiveWorldCache();
        var mode = TileMode.All[1];
        var main = new BotTileViewModel(new BotDto("maid_chan", "main", true, 0, 0, 64, 0, null, null, null, 20, 20, null, null, null, null, true, 0), cache, mode);
        var c67 = new BotTileViewModel(new BotDto("maid_chan_67", "67", false, 0, 0, 64, 0, null, null, null, 20, 20, null, null, null, null, false, 0), cache, mode);
        var c69 = new BotTileViewModel(new BotDto("maid_chan_69", "69", false, 0, 0, 64, 0, null, null, null, 20, 20, null, null, null, null, false, 0), cache, mode);
        ScriptBadgePoller.Apply([main, c67, c69],
        [
            new LiveScriptRowDto("maid_chan", "main", "build_wall", "running", Line: 12),
            new LiveScriptRowDto("maid_chan_67", "67", "cobble_loop", "paused", Paused: true),
        ]);
        Assert.Equal("script build_wall · L12", main.ScriptBadge);
        Assert.True(c67.HasScript);
        Assert.Equal("script cobble_loop · paused", c67.ScriptBadge);
        Assert.False(c69.HasScript);
        ScriptBadgePoller.Apply([main, c67, c69], []);
        Assert.False(main.HasScript);
    }
}
