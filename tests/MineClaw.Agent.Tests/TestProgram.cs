using MineClaw.Agent.Rag;
using MineClaw.Agent.Self;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Entry point of the test assembly (GenerateProgramFile=false). The test host never calls it; the multi-process test
/// runs this dll as a separate OS process (<c>dotnet exec MineClaw.Agent.Tests.dll self-writer db user n</c>) so two real
/// processes write the shared personality db at once.
/// </summary>
public static class TestProgram
{
    public const string WriterVerb = "self-writer";

    public static int Main(string[] args)
    {
        if (args.Length < 4 || args[0] != WriterVerb) return 0;
        try
        {
            var (path, user, n) = (args[1], args[2], int.Parse(args[3]));
            var t = new ManualTime();
            using var db = AgentDb.Open(path);
            var g = GrowthKit.Growth(db, t, user);
            // grow a shared-looking trait first (so whichever writer finishes last sees both members)
            for (int i = 0; i < 3; i++) g.Profile.Apply(new SelfChange("add", SelfSection.Trait, "hums while mining", Reason: "seen"), SelfProfile.SourceReflection);
            for (int i = 0; i < n; i++)
            {
                t.Advance(TimeSpan.FromMinutes(2));
                g.OnEpisode(GrowthKit.Ep(t, GrowthKit.Chat("kiwi", "good job " + user)));
                g.RecordClaimOutcome("kiwi", i % 3 == 0 ? ClaimOutcome.Refuted : ClaimOutcome.Verified);
                g.Profile.Apply(new SelfChange("add", SelfSection.Like, $"thing number {(char)('a' + i % 4)} here", Reason: "x"), SelfProfile.SourceTool);
            }
            g.PromoteToHive(force: true);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
