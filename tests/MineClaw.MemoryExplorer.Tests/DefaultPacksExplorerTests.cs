using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Admin.Shards.Packs;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>The Shards page's "Default packs": listing shards/defaults from index.json, the configurable folder, and the one-click
/// swap / add mode / remove mode previews through the local edit target (the existing diff-first import).</summary>
public sealed class DefaultPacksExplorerTests : IAsyncLifetime
{
    private FixtureDb _fx = null!;

    public async Task InitializeAsync() => _fx = await FixtureDb.CreateAsync();
    public Task DisposeAsync() { _fx.Dispose(); return Task.CompletedTask; }

    private static string Root() => ExplorerPaths.FindRoot(AppContext.BaseDirectory);

    private string BuildPacks()
    {
        var dir = Path.Combine(_fx.Dir, "defaults");
        DefaultPackBuilder.Build(new PackBuildOptions
        {
            Source = new MineClawDataSource(), KnowledgeDirectory = Path.Combine(Root(), "docs", "knowledge"), OutputDirectory = dir, IncludeVectors = false,
        });
        return dir;
    }

    [Fact]
    public void The_defaults_folder_lists_version_and_mode_packs_from_its_index()
    {
        var dir = BuildPacks();
        var list = ShardService.ListDefaults(dir);
        Assert.Equal(["b1.8.1-gamedata", "skyblock"], list.Select(p => p.Name));
        Assert.All(list, p => Assert.True(p.Ok, p.Error));
        var v = list[0];
        Assert.True(v.IsVersion);
        Assert.Equal("version pack · b1.8.1", v.KindLabel);
        Assert.Contains("gamedata", v.CountsText);
        Assert.Contains("no vectors", v.DetailText);
        Assert.Contains(DefaultPackBuilder.BuilderVersion, v.DetailText);
        Assert.Equal("mode pack · skyblock · b1.8.1", list[1].KindLabel);
        Assert.Contains("skyblock_b181.md", list[1].FilesText);

        // A pack file that went missing (or no longer matches index.json) shows why on its row.
        File.Delete(Path.Combine(dir, "skyblock.mcshard"));
        var after = ShardService.ListDefaults(dir);
        Assert.Contains("missing", after.Single(p => p.Name == "skyblock").Error);
        Assert.Empty(ShardService.ListDefaults(Path.Combine(_fx.Dir, "nowhere")));
    }

    [Fact]
    public void The_repo_ships_both_default_packs_and_the_folder_is_configurable()
    {
        var repo = Path.Combine(Root(), "shards", "defaults");
        var list = ShardService.ListDefaults(repo);
        Assert.Contains(list, p => p.Name == "b1.8.1-gamedata" && p.IsVersion && p.Ok);
        Assert.Contains(list, p => p.Name == "skyblock" && p.IsMode && p.Ok);
        Assert.All(list, p => Assert.True(p.Entry.Bytes < 20L * 1024 * 1024));

        Assert.Equal(Path.GetFullPath(repo), Path.GetFullPath(ShardService.DefaultPacksDir(FixtureDb.Config())));
        var custom = Path.Combine(_fx.Dir, "my-packs");
        var config = ExplorerConfig.Load(env: new Dictionary<string, string?> { ["MINECLAW_Explorer__DefaultPacksDir"] = custom });
        Assert.Equal(Path.GetFullPath(custom), ShardService.DefaultPacksDir(config));
    }

    [Fact]
    public async Task One_click_previews_open_the_diff_first_import_on_the_right_mode()
    {
        var dir = BuildPacks();
        var version = Path.Combine(dir, "b1.8.1-gamedata.mcshard");
        var skyblock = Path.Combine(dir, "skyblock.mcshard");
        Assert.True(ShardService.Describe(skyblock).IsModePack);
        Assert.Equal("mode pack skyblock (b1.8.1)", ShardService.Describe(skyblock).KindLabel);

        using var t = new LocalAdminTarget(_fx.Path, FixtureDb.Config(), new HashEmbeddings());
        var swap = await t.PreviewShardAsync(version, new ShardImportOptions { Mode = ShardImportMode.SwapPack }, CancellationToken.None);
        Assert.Null(swap.Refused);
        Assert.True(swap.Added > 400);
        var add = await t.PreviewShardAsync(skyblock, new ShardImportOptions { Mode = ShardImportMode.AddMode }, CancellationToken.None);
        Assert.Null(add.Refused);
        Assert.Equal(["skyblock"], add.ModesTo!);
        Assert.NotNull((await t.PreviewShardAsync(skyblock, new ShardImportOptions { Mode = ShardImportMode.SwapPack }, CancellationToken.None)).Refused);

        // Apply the mode, then the header label names it.
        var res = await t.ImportShardAsync(skyblock, new ShardImportOptions { Mode = ShardImportMode.AddMode, Who = "admin:test" }, CancellationToken.None);
        Assert.True(res.Ok, res.Message);
        Assert.Contains("skyblock", KnowledgePack.Active(t.Context.Db).Label);
        var remove = await t.PreviewShardAsync(skyblock, new ShardImportOptions { Mode = ShardImportMode.RemoveMode }, CancellationToken.None);
        Assert.Equal(4, remove.Removed);
        Assert.Equal([], remove.ModesTo!);
    }
}
