// Offline replay of a .mcrec packet recording through the bot's world state, entity tracker, world indexer, activity
// tracker and danger rules. Prints the detected-event timeline; --json writes a stable summary for diffing two runs.
//   dotnet run --project tools/Replay -- logs/recordings/20261026-201500.mcrec [--json out.json] [--index] [--speed 10] [--self maid_chan]
//   dotnet run --project tools/Replay -- --synthetic tests/fixtures/recordings/synthetic_calibration.mcrec   (regenerates the fixture)
using MineClaw.Replay;

var (input, json, self, synth, index, speed) = ParseArgs(args);
if (synth != null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(synth))!);
    File.WriteAllBytes(synth, SyntheticRecording.Build());
    File.WriteAllText(Path.ChangeExtension(synth, ".truth.txt"), SyntheticRecording.Truth.Replace("\r\n", "\n") + "\n");
    Console.WriteLine($"wrote {synth} and its .truth.txt");
    return 0;
}
if (input == null)
{
    Console.Error.WriteLine("usage: Replay <file.mcrec> [--json out.json] [--index] [--speed N] [--self name] | --synthetic <out.mcrec>");
    return 64;
}
var result = ReplayHarness.Run(input, new ReplayOptions { SelfName = self, IncludeIndex = index, Speed = speed });
Console.Write(result.RenderTimeline());
if (json != null)
{
    File.WriteAllText(json, result.ToJson());
    Console.WriteLine($"summary → {json}");
}
return 0;

static (string? Input, string? Json, string? Self, string? Synth, bool Index, double Speed) ParseArgs(string[] args)
{
    string? input = null, json = null, self = null, synth = null;
    bool index = false;
    double speed = 0;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--json": json = args[++i]; break;
            case "--index": index = true; break;
            case "--speed": speed = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
            case "--self": self = args[++i]; break;
            case "--synthetic": synth = args[++i]; break;
            case "-h" or "--help": input = null; i = args.Length; break;
            default: input = args[i]; break;
        }
    }
    return (input, json, self, synth, index, speed);
}
