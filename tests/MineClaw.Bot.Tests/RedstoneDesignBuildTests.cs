using System.Globalization;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Data;
using MineClaw.World;
using BlockPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// A redstone design (the Design Studio's kind for circuits) never reaches the classic builder: it leaves out dust, repeaters,
/// levers, buttons, plates and pistons and can't set a delay, a comparator's mode or a lever, so what it placed would be half a
/// circuit. It is refused where every build of a saved design is checked (<see cref="BlueprintService.WorldRefusal"/>): the
/// brain's <c>design build</c> and the <c>build</c> tool, which is also what a pawn's blueprint order runs (story_pawn.js:
/// <c>blueprint: j => build(j.design)</c>). The same cells saved as a build are built as before.
/// </summary>
public class RedstoneDesignBuildTests
{
    private const string Fox = "PoofImaFox";
    private const int Dust = 55, Lever = 69;

    /// <summary>A cobblestone base with dust and a lever on it, saved from a studio canvas of the given kind.</summary>
    private static BlueprintRecord Saved(BlueprintService svc, string kind, string name)
    {
        var session = new DesignerSessionRecord { Owner = Fox, Canvas = new DesignerCanvas { Kind = kind } };
        session.Canvas.Apply([
            new CanvasCell(30, 0, 30, Blocks.Cobblestone, 0), new CanvasCell(31, 0, 30, Blocks.Cobblestone, 0), new CanvasCell(32, 0, 30, Blocks.Cobblestone, 0),
            new CanvasCell(30, 1, 30, Dust, 0), new CanvasCell(31, 1, 30, Lever, 5),
        ]);
        var res = DesignerSave.Save(svc, session, name, null, Fox, asNew: true);
        Assert.True(res.Ok, res.Text);
        return svc.Store.Get(res.DesignId)!;
    }

    private static string Id(BlueprintRecord rec) => rec.Id.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_classic_builder_refuses_a_circuit_and_never_asks_for_a_body()
    {
        using var db = AgentDb.Open(null);
        var svc = new BlueprintService(new BlueprintOptions(), new BlueprintStore(db), new BlueprintPreview())
        {
            Site = () => throw new InvalidOperationException("no site in tests"), Crew = () => throw new InvalidOperationException("a body was asked for"),
            ResolveSite = (_, _) => (null, "n/a"), Trusted = () => [Fox],
        };
        svc.History = new DesignHistory(svc, new DesignRevisions(db));
        var circuit = Saved(svc, DesignerCanvas.KindRedstone, "door opener");
        Assert.Equal(DesignStatus.Accepted, circuit.Status); // a studio save is already accepted: only its kind stops the build

        var why = BlueprintService.RedstoneRefusal(circuit);
        Assert.Equal(why, svc.WorldRefusal(circuit));
        Assert.StartsWith($"#{circuit.Id} \"door opener\" is a redstone circuit: ", why);
        // The whole design, a part, a layer, by id or by name: every way into the build answers the same, and no body is asked for.
        var none = CancellationToken.None;
        Assert.Equal((false, why), await svc.BuildAsync(Id(circuit), null, null, null, none));
        Assert.Equal((false, why), await svc.BuildAsync(Id(circuit), 1, 2, null, none));
        Assert.Equal((false, why), await svc.BuildAsync(Id(circuit), null, null, 0, none));
        Assert.Equal((false, why), await svc.BuildAsync("door opener", null, null, null, none));
        Assert.Equal(DesignStatus.Accepted, svc.Store.Get(circuit.Id)!.Status); // never marked as building
        // A copy at another site would be the same circuit: its DSL keeps the line.
        Assert.True(RedstoneDesign.IsRedstone(circuit.Dsl));

        // The same cells saved as a build are this builder's as before: it goes on to ask for a body (this service has none).
        var wall = Saved(svc, DesignerCanvas.KindBuild, "wall");
        Assert.Null(svc.WorldRefusal(wall));
        var asked = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.BuildAsync(Id(wall), null, null, null, none));
        Assert.Equal("a body was asked for", asked.Message);
    }

    [Fact]
    public async Task The_build_tool_behind_a_pawns_blueprint_order_and_the_brains_design_build_both_refuse_it()
    {
        var (h, c) = TestBot.Create(new BotOptions { Blueprints = new BlueprintOptions { GateBlocks = 0 } });
        h.Runtime.Options.Self.TrustedPlayers = [Fox];
        for (int x = -6; x <= 6; x++) for (int z = -6; z <= 6; z++) c.World.SetBlock(x, 64, z, Blocks.Stone, 0);
        c.World.Inventory.SetSlot(0, 36, new ItemStack(Blocks.Cobblestone, 64, 0));
        var s = h.Blueprints!.Service;
        // Both stand on the stone floor, so nothing but the kind differs between them.
        BlueprintRecord Placed(string kind, string name)
        {
            var rec = Saved(s, kind, name);
            rec.Origin = new BlockPos(2, 65, 2);
            s.Store.Update(rec);
            return rec;
        }
        var circuit = Placed(DesignerCanvas.KindRedstone, "door opener");
        var wall = Placed(DesignerCanvas.KindBuild, "wall");

        // A pawn's blueprint order runs build(design): this tool.
        var order = await h.RunAs(Agent.Llm.ModelTier.Worker, "build", new { bp = Id(circuit) });
        Assert.False(order.Ok, order.Text);
        Assert.Equal(BlueprintService.RedstoneRefusal(circuit), order.Text);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("place "));
        Assert.Null(s.Plans!.Get(circuit.Id, 0)); // no plan was sliced for it either
        // The brain's own way in answers the same, before anyone's trust is asked about.
        var brain = await h.Run("design", new { op = "build", id = Id(circuit) });
        Assert.False(brain.Ok, brain.Text);
        Assert.Equal(BlueprintService.RedstoneRefusal(circuit), brain.Text);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("place "));

        // The build design with the very same cells goes ahead (its cobblestone; the dust and the lever are left out as before).
        var built = await h.RunAs(Agent.Llm.ModelTier.Worker, "build", new { bp = Id(wall) });
        Assert.Contains(c.CallList, x => x.StartsWith("place "));
        Assert.DoesNotContain("redstone circuit", built.Text);
        Assert.NotNull(s.Plans.Get(wall.Id, 0));
    }

    private sealed class OneAnswer(string dsl) : IBlueprintAuthor
    {
        public Task<AuthorTurn> WriteAsync(IReadOnlyList<LlmMessage> conversation, CancellationToken ct) =>
            Task.FromResult(new AuthorTurn("```\n" + dsl + "\n```", new LlmUsage(500, 300, 0, 0), "claude-opus-5-5", 0.05m));
    }

    [Fact]
    public async Task A_revised_circuit_is_still_a_circuit_whatever_the_author_wrote()
    {
        // The author answers with a plain hut and no comment line: the brain's design revise must not turn the circuit into a
        // build the builder would then accept.
        var (s, _, _, _) = BlueprintTests.Service(new OneAnswer(BlueprintTests.Hut));
        async Task<BlueprintRecord> Revised(string kind, string name)
        {
            var rec = Saved(s, kind, name);
            rec.Site = new BlockPos(0, 63, 0); // this fixture's island
            s.Store.Update(rec);
            Assert.StartsWith("revising", s.Revise(Id(rec), "make it a hut", Fox));
            await s.LastRun!;
            return s.Store.Get(rec.Id)!;
        }
        var circuit = await Revised(DesignerCanvas.KindRedstone, "door opener");
        Assert.StartsWith(RedstoneDesign.KindLine, circuit.Dsl);
        Assert.Contains("palette C=cobblestone", circuit.Dsl); // the author's revision, as written
        Assert.Single(circuit.Dsl.Split('\n'), l => l.Contains("kind redstone"));
        Assert.Equal(BlueprintService.RedstoneRefusal(circuit), s.WorldRefusal(circuit));
        Assert.DoesNotContain(BlueprintParser.Parse(circuit.Dsl).Issues, i => i.Level == IssueLevel.Error);

        // A build revised the same way gains no tag.
        var wall = await Revised(DesignerCanvas.KindBuild, "wall");
        Assert.Contains("palette C=cobblestone", wall.Dsl);
        Assert.False(RedstoneDesign.IsRedstone(wall.Dsl));
        Assert.Null(s.WorldRefusal(wall));
    }
}
