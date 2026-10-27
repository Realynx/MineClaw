// Scores a live calibration session: replays the .mcrec offline and compares the detected events with a ground-truth
// file noted by hand during the session (see docs/calibration.md for the format). Prints precision/recall per action kind
// and the timing offsets (detected − noted).
//   dotnet run --project tools/CalibrationReport -- <session.mcrec> <truth.txt> [--tolerance 10] [--timeline] [--offset -1.5]
using MineClaw.Replay;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: CalibrationReport <session.mcrec> <truth.txt> [--tolerance seconds] [--offset seconds] [--timeline] [--self name]");
    return 64;
}
double tolerance = 10, offset = 0;
bool timeline = false;
string? self = null;
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--tolerance": tolerance = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        // Shift every noted time (e.g. the stopwatch was started 2 s after the recording: --offset 2).
        case "--offset": offset = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--timeline": timeline = true; break;
        case "--self": self = args[++i]; break;
    }
}
var replay = ReplayHarness.Run(args[0], new ReplayOptions { SelfName = self });
var truth = Calibration.ParseTruth(File.ReadAllText(args[1]))
    .Select(t => t with { OffsetMs = t.OffsetMs + (long)(offset * 1000) }).ToList();
if (timeline) Console.Write(replay.RenderTimeline());
Console.WriteLine($"{Path.GetFileName(args[0])}: {replay.Frames} frames, {ReplayResult.Clock(replay.DurationMs)}, {truth.Count} noted actions, ±{tolerance:0.#}s");
var score = Calibration.Score(replay, truth, TimeSpan.FromSeconds(tolerance));
Console.Write(score.Render());
var wakes = replay.Timeline.Where(e => e.Source == "wake").ToList();
Console.WriteLine($"wakes: {wakes.Count}" + (wakes.Count > 0 ? "" : " (none)"));
foreach (var w in wakes) Console.WriteLine("  " + w.Format());
return 0;
