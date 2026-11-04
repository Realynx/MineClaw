using System.Text;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Client;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins <see cref="BlueprintService.PickCrew"/> (who builds) over every request shape (none, a count, names: main, short and full
/// names, clones, unknown, duplicates, with a count), three crews (none, main alone, three bodies), with and without hive jobs,
/// a design under and over the split size. A refactor must leave <see cref="PickCrewGoldenData.Expected"/> unchanged.
/// Regenerate (only for an intended change) by setting WP02_GOLDEN_OUT.
/// </summary>
public class PickCrewGoldenTests
{
    private sealed class NamedBody(string name) : IBuildBody
    {
        public string Name => name;
        public WorldState World => throw new NotSupportedException();
        public Task<ActionResult> GotoAsync(BlockPos stand, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult> PlaceAsync(BuildStep step, int itemId, int damage, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult> DigAsync(BlockPos pos, CancellationToken ct) => throw new NotSupportedException();
        public int Count(int itemId, int damage) => 0;
        public Task<string?> EnsureAsync(int itemId, int damage, int count, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ClutchAsync(CancellationToken ct) => throw new NotSupportedException();
        public bool IsProtected(BlockPos p, int id) => false;
    }

    /// <summary>Idle clones (no names) or the named ones it knows.</summary>
    private sealed class FakeJobs : IBuildJobs
    {
        private static readonly string[] Idle = ["Alice_69", "Alice_67", "clone3"];
        private static readonly string[] Known = ["Alice_69", "Alice_67", "clone3", "69"];

        public IReadOnlyList<CrewMember> Candidates(IReadOnlyList<string>? names = null) =>
            (names == null ? Idle : names.Where(n => Known.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .Select(n => new CrewMember(n, default)).ToList();

        public string? StartLane(string body, long blueprintId, int lane) => null;
    }

    private static IEnumerable<(string Name, CrewRequest? Req)> Requests() =>
    [
        ("none", null),
        ("count 1", new CrewRequest(null, 1)),
        ("count 1 named", new CrewRequest(["67"], 1)),
        ("count 2", new CrewRequest(null, 2)),
        ("count 5", new CrewRequest(null, 5)),
        ("count 0", new CrewRequest(null, 0)),
        ("count -1", new CrewRequest(null, -1)),
        ("empty names", new CrewRequest([], null)),
        ("main", new CrewRequest(["main"], null)),
        (" Main ", new CrewRequest([" Main "], null)),
        ("67", new CrewRequest(["67"], null)),
        ("alice_68 + main", new CrewRequest(["alice_68", "main"], null)),
        ("69 (clone)", new CrewRequest(["69"], null)),
        ("clone3 + 67", new CrewRequest(["clone3", "67"], null)),
        ("nobody", new CrewRequest(["nobody"], null)),
        ("67 twice", new CrewRequest(["67", "Alice_67"], null)),
        ("67 68 69 clone3, count 2", new CrewRequest(["67", "68", "69", "clone3"], 2)),
        ("67 68 69 clone3, count 3", new CrewRequest(["67", "68", "69", "clone3"], 3)),
        ("alice_67 + 69, count 1", new CrewRequest(["Alice_67", "69"], 1)),
    ];

    internal static string Dump()
    {
        var (plan, _) = BlueprintNormalizer.FromText(BlueprintTests.Hut, new BlockPos(-2, 64, -2));
        var sb = new StringBuilder();
        sb.Append("plan blocks ").Append(plan!.Blocks.Count).Append('\n');
        string[][] crews = [[], ["Alice"], ["Alice", "Alice_67", "Alice_68"]];
        foreach (int split in new[] { 100000, 1 })
            foreach (bool jobs in new[] { false, true })
            {
                var s = new BlueprintService(new BlueprintOptions { SplitAboveBlocks = split }, new BlueprintStore(AgentDb.Open(null)), new BlueprintPreview())
                {
                    Site = () => throw new NotSupportedException(), Crew = () => new SingleBodyCrew(new NamedBody("Alice")),
                    ResolveSite = (_, _) => (null, "none"),
                };
                if (jobs) s.Jobs = new FakeJobs();
                foreach (var crew in crews)
                {
                    var bodies = crew.Select(n => (IBuildBody)new NamedBody(n)).ToList();
                    sb.Append("== split>").Append(split).Append(jobs ? " jobs" : " no jobs").Append(" bodies [").Append(string.Join(",", crew)).Append("]\n");
                    foreach (var (name, req) in Requests())
                    {
                        var (local, remote) = s.PickCrew(plan, req, bodies);
                        sb.Append("  ").Append(name).Append(" => local [").Append(string.Join(",", local.Select(b => b.Name)))
                            .Append("] remote [").Append(string.Join(",", remote.Select(m => m.Name))).Append("]\n");
                    }
                }
            }
        return sb.ToString();
    }

    [Fact]
    public void Crews_match_the_golden_dump()
    {
        var actual = Dump().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(PickCrewGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
