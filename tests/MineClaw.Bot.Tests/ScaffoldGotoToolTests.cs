using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>The real bot schema, JS argument mapper and goto tool must agree on placement-only recovery.</summary>
public class ScaffoldGotoToolTests
{
    [Theory]
    [InlineData("{scaffold:true}")]
    [InlineData("{scaffold:true,keep:{'5':12,'3':2}}")]
    [InlineData("{scaffold:true,keep:{planks:12,dirt:2}}")]
    public async Task Script_scaffold_options_reach_navigation_without_enabling_excavation(string options)
    {
        var (host, client) = TestBot.Create();
        using (host)
        {
            var result = await host.Run("script", new
            {
                op = "run", save = false, code = $"return go('3,65,-2',0,{options})",
            });
            Assert.True(result.Ok, result.Text);
            var move = Assert.Single(client.Gotos);
            Assert.IsType<GoalBlock>(move.Goal);
            Assert.True(move.Goal.IsReached(3, 65, -2));
            var actual = Assert.IsType<GotoOptions>(move.Options);
            Assert.True(actual.AllowPlace);
            Assert.False(actual.AllowBreak);
            Assert.False(actual.AllowPartial);
            Assert.NotNull(actual.BreakOnly);
            Assert.False(actual.BreakOnly(Blocks.Dirt, new BlockPos(0, 64, 0)));
            Assert.False(actual.BreakOnly(Blocks.StoneBricks, new BlockPos(0, 64, 0)));
            if (options.Contains("keep"))
            {
                Assert.Equal(12, actual.ScaffoldReserve![Blocks.Planks]);
                Assert.Equal(2, actual.ScaffoldReserve[Blocks.Dirt]);
            }
            else Assert.Null(actual.ScaffoldReserve);
        }
    }

    [Fact]
    public async Task Keep_only_is_named_and_aliases_reserve_the_largest_quantity_for_the_item()
    {
        var (host, client) = TestBot.Create();
        using (host)
        {
            var result = await host.Run("script", new
            {
                op = "run", save = false,
                code = "return go('3,65,-2',0,{keep:{'5':3,planks:12,dirt:0}})",
            });
            Assert.True(result.Ok, result.Text);
            var actual = Assert.Single(client.Gotos).Options!;
            Assert.False(actual.AllowPlace);
            Assert.False(actual.AllowBreak);
            Assert.True(actual.AllowPartial);
            Assert.Null(actual.BreakOnly);
            Assert.Equal(12, actual.ScaffoldReserve![Blocks.Planks]);
            Assert.Equal(0, actual.ScaffoldReserve[Blocks.Dirt]);
            Assert.Equal(2, actual.ScaffoldReserve.Count);
        }
    }

    [Fact]
    public async Task Existing_default_and_build_script_calls_keep_their_movement_behavior()
    {
        var (host, client) = TestBot.Create();
        using (host)
        {
            client.Give(36, Blocks.Cobblestone, 4);
            var result = await host.Run("script", new
            {
                op = "run", save = false,
                code = "go('3,65,-2'); go('3,65,-2',0,{build:true}); return go('3,65,-2',0,true)",
            });
            Assert.True(result.Ok, result.Text);
            var moves = client.Gotos.ToArray();
            Assert.Equal(3, moves.Length);
            Assert.IsType<GoalNear>(moves[0].Goal);
            Assert.False(moves[0].Options!.AllowBreak);
            Assert.False(moves[0].Options!.AllowPlace);
            foreach (var move in moves.Skip(1))
            {
                Assert.IsType<GoalBlock>(move.Goal);
                Assert.True(move.Options!.AllowBreak);
                Assert.True(move.Options.AllowPlace);
            }
            Assert.All(moves, move =>
            {
                Assert.True(move.Options!.AllowPartial);
                Assert.Null(move.Options.BreakOnly);
                Assert.Null(move.Options.ScaffoldReserve);
            });
        }
    }

    [Theory]
    [InlineData("{scaffold:true,keep:'planks'}")]
    [InlineData("{scaffold:true,keep:[]}")]
    [InlineData("{scaffold:true,keep:{not_a_real_item:1}}")]
    [InlineData("{scaffold:true,keep:{'0':1}}")]
    [InlineData("{scaffold:true,keep:{planks:-1}}")]
    [InlineData("{scaffold:true,keep:{planks:1.5}}")]
    [InlineData("{scaffold:true,keep:{planks:'12'}}")]
    [InlineData("{scaffold:true,keep:{planks:2147483648}}")]
    [InlineData("{scaffold:true,build:true}")]
    public async Task Invalid_reserves_or_conflicting_permissions_fail_before_navigation(string options)
    {
        var (host, client) = TestBot.Create();
        using (host)
        {
            var result = await host.Run("script", new
            {
                op = "run", save = false, code = $"return go('3,65,-2',0,{options})",
            });
            Assert.False(result.Ok, result.Text);
            Assert.Empty(client.Gotos);
            Assert.Empty(client.CallList);
        }
    }

    [Fact]
    public async Task Scaffold_recovery_does_not_acquire_clutch_materials_before_walking()
    {
        var (host, client) = TestBot.Create();
        using (host)
        {
            int acquisitionCalls = 0;
            Acquisition.Attach(client, new AcquisitionSources
            {
                RequestHandoff = (_, _) => { acquisitionCalls++; return Task.FromResult<string?>(null); },
                GeneratorCells = () => { acquisitionCalls++; return []; },
            });
            var result = await host.Run("script", new
            {
                op = "run", save = false, code = "return go('3,65,-2',0,{scaffold:true})",
            });
            Assert.True(result.Ok, result.Text);
            Assert.Equal(0, acquisitionCalls);
            Assert.StartsWith("goto ", Assert.Single(client.CallList));

            // The same empty inventory and sources exercise acquisition for the existing unrestricted build mode.
            Assert.True((await host.Run("goto", new { to = "3,65,-2", build = true })).Ok);
            Assert.True(acquisitionCalls > 0);
        }
    }
}
