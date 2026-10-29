using MineClaw.MemoryExplorer.LiveHive.Gpu;
using MineClaw.Render;
using MineClaw.Render.Models;
using Xunit;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>A test that needs the user's 1.12.2 client jar (read at runtime, never committed): skipped where it isn't installed.</summary>
public sealed class Jar1122FactAttribute : FactAttribute
{
    internal static readonly Lazy<TextureAtlas?> Atlas = new(() =>
    {
        var o = new TextureOptions { Version = Release1122Assets.Version, AllowDownload = false };
        var jar = TextureLocator.CandidateJars(o).FirstOrDefault(j => File.Exists(j) && Release1122Assets.IsModernJar(j));
        return jar == null ? null : Release1122Assets.FromJar(jar);
    });

    public Jar1122FactAttribute()
    {
        var o = new TextureOptions { Version = Release1122Assets.Version };
        if (!TextureLocator.CandidateJars(o).Any(j => File.Exists(j) && Release1122Assets.IsModernJar(j)))
            Skip = "the 1.12.2 client jar is not installed on this machine";
    }
}

/// <summary>Multi-version P5: the Explorer's GPU freecam meshes 1.12.2 blocks from the same models as the CPU raycaster.</summary>
[Collection("avalonia")]
public class GpuFreecam1122Tests
{
    [Jar1122Fact]
    public void Mesher_draws_1122_models_on_the_atlas_block_rows()
    {
        var atlas = Jar1122FactAttribute.Atlas.Value!;
        var models = atlas.Models!;
        var m = new SectionMesher { Textured = true, Models = models };
        // Stone + grass (cubes, culled between them), stairs (a model), glass (a cutout cube).
        var w = new SparseVoxels().Set(4, 68, 4, 1).Set(5, 68, 4, 2).Set(7, 68, 4, 53, 0).Set(9, 68, 4, 20);
        m.Build(w, 0, 4, 0);
        // stone 5 + grass 5 faces, glass 6, the stairs' model quads (two boxes: ≥ 10).
        Assert.True(m.Solid.Quads >= 26, $"{m.Solid.Quads} quads");
        var rows = Enumerable.Range(0, m.Solid.Count).Select(i => m.Solid.V[i]).ToList();
        Assert.Contains(rows, v => v.Row >= GpuAtlas.BlockRow);                                // model textures
        // The grass side's fringe: a second quad on the overlay texture (its own biome tint), drawn over the side.
        int fringe = models.Textures.IndexOf("blocks/grass_side_overlay");
        Assert.Contains(rows, v => v.Row == GpuAtlas.BlockRow + fringe / GpuAtlas.Cols && v.Col == fringe % GpuAtlas.Cols);

        var (aw, ah, rgba) = GpuAtlas.Build(atlas);
        Assert.Equal(GpuAtlas.RowsFor(atlas) * 16, ah);
        Assert.True(GpuAtlas.RowsFor(atlas) > GpuAtlas.Rows && GpuAtlas.RowsFor(atlas) < 256);
        // Stone's texture landed in its cell.
        int stone = models.Textures.IndexOf("blocks/stone");
        Assert.Equal(models.Textures.Pixels(stone)[0], GpuAtlas.Texel(rgba, aw, stone % GpuAtlas.Cols * 16, (GpuAtlas.BlockRow + stone / GpuAtlas.Cols) * 16));
    }
}
