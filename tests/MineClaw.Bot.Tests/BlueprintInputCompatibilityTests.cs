using MineClaw.Bot.Blueprints;

namespace MineClaw.Bot.Tests;

public class BlueprintInputCompatibilityTests
{
    [Theory]
    [InlineData("build", "bp", true)]
    [InlineData("build", "id", true)]
    [InlineData("build", "id", false)]
    [InlineData("blueprint", "bp", true)]
    [InlineData("blueprint", "id", true)]
    [InlineData("blueprint", "id", false)]
    public async Task Saved_blueprint_ids_keep_numeric_and_id_alias_forms_without_bypassing_validation(string tool, string key, bool numeric)
    {
        var (host, _) = TestBot.Create();
        using (host)
        {
            var rec = host.Blueprints!.Service.Store.Insert(new BlueprintRecord
            {
                Name = "wall", Description = "a wall", Bot = "maid_chan", Status = DesignStatus.Accepted, AcceptedBy = "PoofImaFox",
                Dsl = "blueprint wall\nsize 3x1x1\npalette C=cobblestone\nlayer 0\nCCC",
            });
            var input = new Dictionary<string, object> { [key] = numeric ? rec.Id : rec.Id.ToString(), ["lane"] = 1 };
            var result = await host.Run(tool, input);
            var canonical = await host.Run(tool, new { bp = rec.Id.ToString(), lane = 1 });
            Assert.Equal(canonical.Ok, result.Ok);
            Assert.Equal(canonical.Text, result.Text);
            Assert.Contains(tool == "build" ? "no crew plan" : "wall", result.Text);

            // Compatibility is specific to saved ids, not general coercion or authority to execute a build.
            var invalid = await host.Run(tool, new { bp = true, lane = 1 });
            Assert.False(invalid.Ok);
            Assert.Contains("$.bp", invalid.Text);
            invalid = await host.Run(tool, new { bp = (string?)null, lane = 1 });
            Assert.False(invalid.Ok);
            Assert.Contains("$.bp", invalid.Text);
            if (tool == "build")
            {
                invalid = await host.Run(tool, new { bp = rec.Id, lane = "1" });
                Assert.False(invalid.Ok);
                Assert.Contains("$.lane", invalid.Text);
            }
        }
    }
}
