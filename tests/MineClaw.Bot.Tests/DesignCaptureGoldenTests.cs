using System.Text;
using MineClaw.Bot.Blueprints;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins <c>design capture</c> (<see cref="DesignCapture.Op"/> → <see cref="DesignCapture.Save"/>) through the design tool: corners as
/// text or a list, a remembered structure, coordinates, an unknown spot, where the speaker stands (with and without a name), nobody
/// in sight, an untrusted speaker; and <see cref="DesignCapture.Resolve"/> / <see cref="DesignCapture.OnTopOfOriginal"/> over the
/// captured house. Every answer and the stored record. A refactor must leave <see cref="DesignCaptureGoldenData.Expected"/> unchanged.
/// Regenerate (only for an intended change) by setting WP02_GOLDEN_OUT.
/// </summary>
public class DesignCaptureGoldenTests
{
    private sealed record Case(string Name, object Input, string? Speaker, bool Spawn, bool Remember = false);

    private static IEnumerable<Case> Cases() =>
    [
        new("corners text", new { op = "capture", name = "Fox's house", corners = "0,63,0 6,67,4" }, "PoofImaFox", false),
        new("corners list", new { op = "capture", name = "box", corners = new[] { "0,64,0", "6,66,4" } }, "PoofImaFox", false),
        new("corners one", new { op = "capture", name = "box", corners = "0,64,0" }, "PoofImaFox", true),
        new("remembered", new { op = "capture", name = "the shed", at = "shed" }, "PoofImaFox", false, true),
        new("coords", new { op = "capture", at = "3,64,2" }, "PoofImaFox", false),
        new("unknown spot", new { op = "capture", name = "x", at = "over yonder" }, "PoofImaFox", false),
        new("standing in it", new { op = "capture", name = "Fox's house" }, "PoofImaFox", true),
        new("standing, no name", new { op = "capture" }, "PoofImaFox", true),
        new("what instead of name", new { op = "capture", what = "  my house  " }, "PoofImaFox", true),
        new("nobody in sight", new { op = "capture", name = "x" }, "PoofImaFox", false),
        new("untrusted", new { op = "capture", name = "kiwi's house" }, "kiwi", true),
        new("no speaker", new { op = "capture", name = "x" }, null, false),
    ];

    internal static async Task<string> Dump()
    {
        var sb = new StringBuilder();
        foreach (var c in Cases())
        {
            var (h, cl) = TestBot.Create();
            using var host = h;
            h.Runtime.Options.Self.TrustedPlayers = ["PoofImaFox"];
            var w = cl.World;
            w.ApplyChunkRegion(-48, 0, -32, 96, 128, 64, new byte[96 * 128 * 64], null, null, null);
            for (int x = -30; x <= 12; x++) for (int z = -10; z <= 12; z++) { w.SetBlock(x, 62, z, Blocks.Dirt, 0); w.SetBlock(x, 63, z, Blocks.Grass, 0); }
            StructureCaptureTests.House(w);
            if (c.Spawn) w.Entities.SpawnPlayer(7, c.Speaker ?? "PoofImaFox", new Vec3d(3.5, 64, 2.5), 0, 0);
            if (c.Remember)
                h.Runtime.Memory.UpsertStructure("shed", st =>
                {
                    st.Min = new MineClaw.Agent.Memory.BlockPos(0, 64, 0);
                    st.Max = new MineClaw.Agent.Memory.BlockPos(6, 66, 4);
                });
            Agent.TurnContext.Current = c.Speaker == null ? null : new Agent.TurnInfo(c.Speaker, [c.Speaker], false);
            try
            {
                sb.Append("== ").Append(c.Name).Append('\n');
                var r = await h.Run("design", c.Input);
                sb.Append("  ").Append(r.Ok ? "ok " : "ERR ").Append(r.Text).Append('\n');
                if (!r.Ok) continue;
                var store = h.Runtime.Memory;
                foreach (var s in store.Read(d => d.Structures.ToList()))
                    sb.Append("  structure ").Append(s.Name).Append(' ').Append(s.Kind).Append(' ').Append(s.Min).Append(' ').Append(s.Max).Append(" taught ")
                        .Append(s.TaughtBy).Append(" note ").Append(s.Note).Append(" linked ").Append(DesignCapture.LinkedDesign(s)).Append('\n');
                var show = await h.Run("design", new { op = "show", id = "1" });
                sb.Append("  show ").Append(show.Text.ReplaceLineEndings("\n").Replace("\n", " | ")).Append('\n');
                foreach (var what in new[] { "my house", "Fox's house", "the Fox's house", "PoofImaFox's house", "our house", "kiwi's house", "box", "1", "#1", "", "the shed" })
                    foreach (var who in new[] { "PoofImaFox", "kiwi", null })
                        sb.Append("  resolve [").Append(what).Append("] by ").Append(who ?? "-").Append(" => ")
                            .Append(ResolveId(h, what, who)).Append('\n');
                var again = await h.Run("design", new { op = "build", id = "1" });
                sb.Append("  rebuild here ").Append(again.Ok).Append(' ').Append(again.Text).Append('\n');
            }
            finally { Agent.TurnContext.Current = null; }
        }
        return sb.ToString();
    }

    private static string ResolveId(BotHost h, string what, string? who)
    {
        if (h.Blueprints?.Service.Store is not { } store) return "no store";
        return DesignCapture.Resolve(store, h.Runtime.Memory, what, who)?.Id.ToString() ?? "null";
    }

    [Fact]
    public async Task Captures_match_the_golden_dump()
    {
        var actual = (await Dump()).ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(DesignCaptureGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
