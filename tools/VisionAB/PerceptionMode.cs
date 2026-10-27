using System.Globalization;
using System.Text.Json;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Perception.Vision.Eval;

/// <summary>
/// Vision v2 eval modes (no db snapshot needed: the cases are today's live failures, built in code + the portal-trip fixture):
///   dotnet run --project tools/VisionAB -- sheets [--out=...]                        write each case's contact sheet (no API)
///   dotnet run --project tools/VisionAB -- perception [--arms=a,b] [--only=c1,c2] [--max-calls=20] [--cap=0.50] [--record]
/// The live mode is opt-in, hard-capped at 20 calls and $0.50; --record writes the raw answers to
/// tests/fixtures/vision-eval/recorded.json so the tests replay real model output offline. Keys are never printed.
/// </summary>
static class PerceptionMode
{
    public static int Sheets(string root, string outDir)
    {
        var fixture = File.ReadAllText(Path.Combine(root, "tests", "fixtures", "portal-trip", "nether.txt"));
        var dualGen = File.ReadAllText(Path.Combine(root, "tests", "fixtures", "vision-eval", "dual-gen.txt"));
        foreach (var c in VisionEval.Cases(fixture, dualGen))
        {
            var s = VisionEval.Sheet(c);
            File.WriteAllBytes(Path.Combine(outDir, $"sheet-{c.Id}.png"), s.Png);
            File.WriteAllText(Path.Combine(outDir, $"sheet-{c.Id}.txt"), s.Legend + "\nQ: " + c.Question + "\nexpected: " + c.Expected);
            Console.WriteLine($"{c.Id,-20} {s.Width}x{s.Height} ~{s.Tokens} tok");
        }
        Console.WriteLine($"wrote sheets to {outDir}");
        return 0;
    }

    public static async Task<int> Live(string root, string outDir, Func<string, string?> arg, LlmOptions llm)
    {
        const int HardMaxCalls = 20;
        const decimal HardCapUsd = 0.50m;
        int maxCalls = Math.Min(HardMaxCalls, int.TryParse(arg("max-calls"), out var mc) ? mc : HardMaxCalls);
        decimal cap = Math.Min(HardCapUsd, decimal.TryParse(arg("cap"), NumberStyles.Number, CultureInfo.InvariantCulture, out var cv) ? cv : HardCapUsd);
        var fixture = File.ReadAllText(Path.Combine(root, "tests", "fixtures", "portal-trip", "nether.txt"));
        var only = arg("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dualGen = File.ReadAllText(Path.Combine(root, "tests", "fixtures", "vision-eval", "dual-gen.txt"));
        var cases = VisionEval.Cases(fixture, dualGen).Where(c => only == null || only.Contains(c.Id)).ToList();
        var armNames = arg("arms")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arms = VisionEvalArm.Default.Where(a => armNames == null || armNames.Contains(a.Name)).ToList();
        var recorder = new RecordingLlm(new AnthropicLlmClient(llm));
        var prices = new PriceTable();
        var rows = new List<VisionEvalRow>();
        decimal spent = 0;
        int calls = 0;
        Console.WriteLine($"live perception eval: {cases.Count} cases x {arms.Count} arms, max {maxCalls} calls, cap ${cap}; key set");
        var log = Path.Combine(outDir, $"perception-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        foreach (var c in cases)
        {
            var sheet = VisionEval.Sheet(c, cutAway: arg("nocut") == null); // --nocut=1: the pre-fix sheet (roofs hide what's under them)
            foreach (var arm in arms)
            {
                if (calls >= maxCalls || spent >= cap - 0.02m) { Console.WriteLine($"stopping: {calls} calls, ${spent:0.0000}"); goto done; }
                recorder.Case = c.Id;
                var row = await VisionEval.RunAsync(c, arm, recorder, prices, CancellationToken.None, sheet);
                if (row.InputTokens > 0 || row.Error != null) calls++; // an exact (voxel) answer makes no call
                spent += row.Usd;
                rows.Add(row);
                Console.WriteLine($"{c.Id,-19} {arm.Name,-18} {(row.Ok ? "OK " : "BAD")} raw {(row.RawOk ? "ok " : "bad")} in {row.InputTokens,5} out {row.OutputTokens,4} " +
                    $"brain {row.BrainTokens,4} ${row.Usd:0.00000} {row.LatencyMs,5}ms halluc {row.Mismatched}/{row.Checked}  {Trunc(row.Answer, 90)}{(row.Error != null ? " ERR " + row.Error : "")}");
                File.AppendAllText(log, JsonSerializer.Serialize(new { @case = c.Id, arm = arm.Name, row.Ok, row.RawOk, row.Answer, expected = c.Expected, row.InputTokens,
                    row.OutputTokens, row.BrainTokens, row.Usd, row.LatencyMs, row.Checked, row.Mismatched, row.Error }) + "\n");
            }
        }
    done:
        Console.WriteLine();
        Console.WriteLine(VisionEval.Report(rows));
        Console.WriteLine($"total {calls} calls ${spent:0.0000} (cap ${cap}); log {log}");
        if (arg("record") != null || Environment.GetCommandLineArgs().Contains("--record"))
        {
            var dir = Path.Combine(root, "tests", "fixtures", "vision-eval");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "recorded.json");
            // Merge: a partial re-run replaces only its own (case, model) answers.
            var all = File.Exists(path) ? JsonSerializer.Deserialize<List<RecordedAnswer>>(File.ReadAllText(path)) ?? [] : [];
            all.RemoveAll(o => recorder.Recorded.Any(n => n.Case == o.Case && n.Model == o.Model));
            all.AddRange(recorder.Recorded);
            File.WriteAllText(path, JsonSerializer.Serialize(all.OrderBy(x => x.Case).ThenBy(x => x.Model).ToList(), new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"recorded {recorder.Recorded.Count} answers ({all.Count} in total) to {path}");
        }
        return 0;
    }

    private static string Trunc(string s, int n) => (s.Length <= n ? s : s[..n]).Replace('\n', ' ');

    /// <summary>Passes calls through and keeps (case, model) → raw text + usage for the offline replay.</summary>
    private sealed class RecordingLlm(ILlmClient inner) : ILlmClient
    {
        public string Case { get; set; } = "";
        public List<RecordedAnswer> Recorded { get; } = new();

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            var r = await inner.CompleteAsync(request, ct);
            Recorded.Add(new RecordedAnswer(Case, request.Model ?? r.Model, request.Purpose, r.Text, r.Usage.InputTokens + r.Usage.CacheReadTokens + r.Usage.CacheWriteTokens, r.Usage.OutputTokens));
            return r;
        }
    }

    private sealed record RecordedAnswer(string Case, string Model, string Purpose, string Text, long InputTokens, long OutputTokens);
}
