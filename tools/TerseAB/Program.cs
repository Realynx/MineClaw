// TerseAB — A/B of the terse internal register (Agent:Terse) against the REAL APIs: off vs full.
//
//   dotnet run --project tools/TerseAB                 run (~15 internal prompts × 2 arms + Haiku judge; hard stop at $0.75)
//   dotnet run --project tools/TerseAB -- --dry-run    no API calls: walks every scenario with a stub model, prints the plan
//   dotnet run --project tools/TerseAB -- --only=delegate,chat   some purposes (delegate|chat|worker|ask_memory|compaction|reflection)
//   --arms=off,full,lite     arms to run (first = baseline the others are judged against; default off,full; "defaults" =
//                            the shipped per-purpose defaults)
//   --spent=0.16             spend of earlier runs, so the $0.75 hard cap is cumulative
//   --log=path.jsonl         full artifacts per scenario/arm (default: a temp file, path printed)
//
// Config: the gitignored src/MineClaw.App/appsettings.json (Agent:Llm, Agent:Embeddings, Agent:Persona). Keys are never printed.
// ask_memory runs over a temp copy of data/seed/mineclaw-seed.db (the real data/ is untouched). No Minecraft connection.
// Metrics come from PromptTelemetry records (output tokens, cost per purpose). Quality: an automatic rubric per scenario
// (required args present, numbers/coords preserved, format) plus Haiku-as-judge scoring both arms 1–5 on completeness.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Self;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Configuration;

const decimal HardCapUsd = 0.75m;
var root = FindRoot();
var settings = Path.Combine(root, "src", "MineClaw.App", "appsettings.json");
var seedPath = Path.Combine(root, "data", "seed", "mineclaw-seed.db");
bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith("--" + name + "=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 3)..];
var only = Arg("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
string[] arms = Arg("arms")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? ["off", "full"];
GuardLlm.Seed(decimal.TryParse(Arg("spent"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var prior) ? prior : 0);
var logPath = Arg("log") ?? Path.Combine(Path.GetTempPath(), $"terseab-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

var probe = LoadOptions("off", Path.GetTempPath());
if (!dryRun && string.IsNullOrWhiteSpace(probe.Llm.ApiKey)) { Console.Error.WriteLine("Agent:Llm:ApiKey / ANTHROPIC_API_KEY missing"); return 2; }
Console.WriteLine($"models: brain {probe.Llm.MainModel}, worker {probe.Llm.WorkerModel}, memory {probe.Llm.CheapModel}; " +
                  $"key set: {!string.IsNullOrWhiteSpace(probe.Llm.ApiKey)}; embeddings configured: {probe.Embeddings.IsConfigured}; " +
                  $"{(dryRun ? "DRY RUN" : "LIVE")}, hard cap ${HardCapUsd}");

var scenarios = Scenarios().Where(s => only == null || only.Contains(s.Purpose, StringComparer.OrdinalIgnoreCase)).ToList();
var results = new List<Result>();
var temp = Path.Combine(Path.GetTempPath(), "mineclaw-terseab-" + Guid.NewGuid().ToString("N")[..8]);
Result? current = null;
var prefix = new Dictionary<(string Arm, string Purpose), (int Tokens, bool Cached, string Model)>();
bool stopped = false;

try
{
    await RunArmsAsync();
    // Haiku judge: baseline vs each other arm, both orders (scores averaged) to cancel position bias.
    if (!stopped) await JudgeAllAsync();
    WriteLog();
    Console.WriteLine($"artifacts: {logPath}");
}
finally
{
    try { Directory.Delete(temp, true); } catch (Exception) { }
}

Report();
PrintTotal();
return stopped ? 3 : 0;

// =====================================================================================================================

// Every arm over every scenario; a budget stop ends the run.
async Task RunArmsAsync()
{
    foreach (var arm in arms)
    {
        var dir = Path.Combine(temp, arm);
        Directory.CreateDirectory(dir);
        var options = LoadOptions(arm, dir);
        var guard = new GuardLlm(new AnthropicLlmClient(options.Llm), options, HardCapUsd, dryRun);
        using var tel = new PromptTelemetry(options);
        tel.Recorded += r => current?.Records.Add(r);
        using var rt = AgentRuntime.Create(options, (_, _) => Task.CompletedTask, tel.Wrap(guard));
        RegisterStubs(rt);
        rt.ApplyBrainSplit();
        if (rt.Raptor != null) await rt.Raptor.StartLoading(ct);
        Console.WriteLine($"\n== arm {arm}: {options.Terse.Describe()}");
        prefix[(arm, "delegate")] = prefix[(arm, "chat")] = (Tok(rt.Agent.SystemPrompt[0].Text), true, options.Llm.MainModel);
        prefix[(arm, "worker")] = (Tok(rt.Worker!.SystemPrompt[0].Text), true, options.Llm.WorkerModel);
        prefix[(arm, "ask_memory")] = (Tok(rt.MemoryAgent!.SystemPrompt[0].Text), true, options.Llm.CheapModel);
        prefix[(arm, "compaction")] = (Tok(CompactionSystem(options)), false, options.Llm.CheapModel);
        prefix[(arm, "reflection")] = (Tok(ReflectionSystem(options)), false, options.Llm.CheapModel);

        foreach (var s in scenarios)
            if (await RunScenarioAsync(rt, s, arm)) break;
        if (stopped) break;
    }
}

// One scenario on one arm, graded and printed; true when the budget stopped it.
async Task<bool> RunScenarioAsync(AgentRuntime rt, Scenario s, string arm)
{
    var res = new Result { Arm = arm, Id = s.Id, Purpose = s.Purpose };
    current = res;
    try { await s.Run(rt, res); }
    catch (BudgetStop ex) { Console.Error.WriteLine($"STOP: {ex.Message}"); stopped = true; return true; }
    current = null;
    (res.RubricOk, res.RubricNote) = dryRun ? (true, "dry") : s.Rubric(res);
    results.Add(res);
    Console.WriteLine($"  {s.Id,-22} out {res.Out,5}  ${res.Cost:0.0000}  calls {res.Records.Count}  think {res.Thinking.Length}c  artifact {res.Artifact.Length}c  rubric {(res.RubricOk ? "ok" : "FAIL " + res.RubricNote)}");
    Console.WriteLine($"      > {Short(res.Artifact, 220)}");
    if (res.Chat.Length > 0 && s.Purpose != "chat") Console.WriteLine($"      chat> {Short(res.Chat, 160)}");
    return false;
}

async Task JudgeAllAsync()
{
    var judgeOptions = LoadOptions("off", temp);
    var judgeLlm = new GuardLlm(new AnthropicLlmClient(judgeOptions.Llm), judgeOptions, HardCapUsd, dryRun);
    foreach (var arm in arms.Skip(1))
    foreach (var s in scenarios)
    {
        var a = results.FirstOrDefault(r => r.Id == s.Id && r.Arm == arms[0]);
        var b = results.FirstOrDefault(r => r.Id == s.Id && r.Arm == arm);
        if (a == null || b == null) continue;
        try
        {
            var (x1, y1, why) = await JudgeAsync(judgeLlm, judgeOptions, s, a, b);
            var (y2, x2, _) = await JudgeAsync(judgeLlm, judgeOptions, s, b, a);
            a.Judges.AddRange(new[] { x1, x2 }.Where(v => v > 0).Select(v => (double)v));
            b.Judges.AddRange(new[] { y1, y2 }.Where(v => v > 0).Select(v => (double)v));
            Console.WriteLine($"  judge {s.Id,-22} {arms[0]} {x1}/{x2}  {arm} {y1}/{y2}  {Short(why, 110)}");
        }
        catch (BudgetStop ex) { Console.Error.WriteLine($"STOP (judge): {ex.Message}"); stopped = true; break; }
    }
}

void WriteLog()
{
    using var w = new StreamWriter(logPath);
    foreach (var r in results)
        w.WriteLine(JsonSerializer.Serialize(new
        {
            r.Arm, r.Id, r.Purpose, Out = r.Out, Cost = r.Cost, Calls = r.Records.Count, r.RubricOk, r.RubricNote, Judge = r.Judge,
            r.Artifact, r.Chat, r.Thinking, Delegate = r.Delegate?.GetRawText(), r.Tools,
        }));
}

void PrintTotal() =>
    Console.WriteLine($"\ntotal spend (cumulative incl. --spent): ${GuardLlm.Spent:0.0000}{(dryRun ? $" (dry run; worst-case plan ${GuardLlm.Planned:0.00})" : "")}{(stopped ? "  [stopped at budget]" : "")}");

// =====================================================================================================================

void Report()
{
    Console.WriteLine("\npurpose/arm      n  out/use off→full   Δ%    $saved/1k uses (out)  prefix +tok  $prefix/1k  judge off→full  rubric off/full");
    foreach (var arm in arms.Skip(1))
    foreach (var g in results.GroupBy(r => r.Purpose))
    {
        var off = g.Where(r => r.Arm == arms[0]).ToList();
        var full = g.Where(r => r.Arm == arm).ToList();
        if (off.Count == 0 || full.Count == 0) continue;
        double o = off.Average(r => r.Out), f = full.Average(r => r.Out);
        var model = off.SelectMany(r => r.Records).Select(r => r.Model).FirstOrDefault() ?? probe.Llm.MainModel;
        var price = probe.Prices.For(model);
        decimal saved = (decimal)(o - f) * price.OutputPerM / 1_000_000m * 1000m;
        var po = prefix.GetValueOrDefault((arms[0], g.Key));
        var pf = prefix.GetValueOrDefault((arm, g.Key));
        int dTok = pf.Tokens - po.Tokens;
        double callsPerUse = full.Average(r => Math.Max(1, r.Records.Count));
        decimal prefixCost = dTok * (pf.Cached ? price.CacheReadPerM : price.InputPerM) / 1_000_000m * 1000m * (decimal)callsPerUse;
        string judge = $"{Avg(off.Select(r => r.Judge))}→{Avg(full.Select(r => r.Judge))}";
        Console.WriteLine($"{g.Key + "/" + arm,-16} {off.Count,2}  {o,7:0}→{f,-7:0}  {(o > 0 ? (f - o) / o * 100 : 0),5:0}%  {saved,10:0.000}            {dTok,5}     {prefixCost,7:0.000}     {judge,-12}  {off.Count(r => r.RubricOk)}/{off.Count} {full.Count(r => r.RubricOk)}/{full.Count}");
        var drop = off.Zip(full).Where(p => p.First.Judge is { } j0 && p.Second.Judge is { } j1 && j1 <= j0 - 1).Select(p => p.First.Id).ToList();
        double ao = off.Average(r => (double)r.Artifact.Length), af = full.Average(r => (double)r.Artifact.Length);
        double to = off.Average(r => (double)r.Thinking.Length), tf = full.Average(r => (double)r.Thinking.Length);
        Console.WriteLine($"   artifact chars {ao:0}->{af:0}, visible thinking chars {to:0}->{tf:0}");
        var rubricFail = off.Zip(full).Where(p => p.First.RubricOk && !p.Second.RubricOk).Select(p => p.First.Id).ToList();
        if (drop.Count > 0 || rubricFail.Count > 0)
            Console.WriteLine($"   REGRESSION {g.Key}: judge drop {string.Join(",", drop)}; rubric fail {string.Join(",", rubricFail)}");
    }

    static string Avg(IEnumerable<double?> xs) { var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? "-" : v.Average().ToString("0.0"); }
}

AgentOptions LoadOptions(string arm, string dataDir)
{
    var config = new ConfigurationBuilder().AddJsonFile(settings, optional: true).AddEnvironmentVariables().Build();
    var o = new AgentOptions();
    config.GetSection("Agent").Bind(o);
    if (string.IsNullOrWhiteSpace(o.Llm.ApiKey) || o.Llm.ApiKey.EndsWith("...", StringComparison.Ordinal))
        o.Llm.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    o.DataDirectory = dataDir;
    o.KnowledgeDirectory = Path.Combine(root, "docs", "knowledge");
    o.Rag.SeedFile = seedPath;
    o.Rag.Raptor.Maintenance = false;
    o.IdleCheckInMinutes = 0;
    o.Terse = new TerseOptions { Level = arm == "defaults" ? null : arm }; // "defaults" = the shipped per-purpose defaults
    return o;
}

static string CompactionSystem(AgentOptions o) => ContextManager.CompactionInstructions + Prompts.CompactionStyle(o.Terse.For(TersePurpose.Compaction));

static string ReflectionSystem(AgentOptions o) =>
    string.Format(ReflectionPass.InstructionsV2, o.Self.MaxChangesPerPass) + Prompts.ReflectionStyle(o.Terse.For(TersePurpose.Reflection));

static int Tok(string s) => (int)Math.Ceiling(s.Length / 3.5);

static string Short(string s, int n) { s = s.Replace('\n', ' ').Replace('\r', ' '); return s.Length <= n ? s : s[..n] + "…"; }

static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "ARCHITECTURE.md"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}

// ---------------- stub game tools (never executed by the harness: it answers tool calls with canned results) --------

static void RegisterStubs(AgentRuntime rt)
{
    Task<ToolResult> Nop(JsonElement _, CancellationToken __) => Task.FromResult(ToolResult.Success("ok"));
    var t = rt.Tools;
    t.Register("status", "Health, food, position, held item, current task.", ToolSchema.Empty, Nop, cheapTier: true);
    t.Register("inventory", "Your inventory (compact).", ToolSchema.Empty, Nop, cheapTier: true);
    t.Register("look", "What's around you (blocks, entities, players); detail=true for more.", ToolSchema.Create().Bool("detail", required: false).Build(), Nop);
    t.Register("goto", "Walk to x,y,z or a waypoint/player.", ToolSchema.Create().Str("target", "x,y,z | waypoint | player").Build(), Nop, longRunning: true);
    t.Register("follow", "Follow a player.", ToolSchema.Create().Str("player").Build(), Nop, longRunning: true);
    t.Register("window", "The open container window.", ToolSchema.Empty, Nop);
    t.Register("mine", "Mine N blocks of a type nearby (pathing, tool choice).", ToolSchema.Create().Str("block").Int("count").Build(), Nop, longRunning: true);
    t.Register("harvest_generator", "Mine the cobblestone generator N times.", ToolSchema.Create().Int("count").Build(), Nop, longRunning: true);
    t.Register("collect_drops", "Pick up nearby item drops.", ToolSchema.Empty, Nop, longRunning: true);
    t.Register("craft", "Craft an item (resolves the recipe tree).", ToolSchema.Create().Str("item").Int("count", required: false).Build(), Nop, longRunning: true);
    t.Register("smelt", "Smelt items in a furnace.", ToolSchema.Create().Str("item").Int("count").Str("fuel", required: false).Build(), Nop, longRunning: true);
    t.Register("collect_furnace", "Take output from a furnace.", ToolSchema.Create().Str("at", required: false).Build(), Nop, longRunning: true);
    t.Register("deposit", "Put items into a chest.", ToolSchema.Create().Str("item").Int("count", required: false).Str("chest", "x,y,z").Build(), Nop, longRunning: true);
    t.Register("withdraw", "Take items from a chest.", ToolSchema.Create().Str("item").Int("count", required: false).Str("chest", "x,y,z").Build(), Nop, longRunning: true);
    t.Register("organize_chest", "Sort a chest's contents.", ToolSchema.Create().Str("chest", "x,y,z").Build(), Nop, longRunning: true);
    t.Register("place", "Place a block/item at x,y,z.", ToolSchema.Create().Str("item").Int("x").Int("y").Int("z").Build(), Nop, longRunning: true);
    t.Register("dig", "Break the block at x,y,z.", ToolSchema.Create().Int("x").Int("y").Int("z").Build(), Nop, longRunning: true);
    t.Register("use_block", "Right-click the block at x,y,z.", ToolSchema.Create().Int("x").Int("y").Int("z").Build(), Nop, longRunning: true);
    t.Register("eat", "Eat food from the inventory.", ToolSchema.Create().Str("item", required: false).Build(), Nop, longRunning: true);
}

// ---------------- runners ----------------

async Task BrainAsync(AgentRuntime rt, Result res, string player, string chat, Dictionary<string, string> stubs)
{
    var text = "pos 4,64,-2 hp 20/20 food 18 held stone_axe | task: idle\n" + new EventBatch([GameEvent.Chat(player, chat)]).Format();
    var msgs = new List<LlmMessage> { LlmMessage.User(text) };
    var said = new StringBuilder();
    LlmToolUse? del = null;
    for (int round = 0; round < 3 && del == null; round++)
    {
        var resp = await rt.Llm.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Main, System = rt.Agent.SystemPrompt, Tools = rt.Tools.Definitions(ModelTier.Main),
            Messages = msgs.ToArray(), Purpose = "task",
        }, ct);
        res.Thinking += string.Concat(resp.Content.OfType<LlmThinking>().Select(t => t.Thinking));
        var content = resp.Content.Where(b => b is not LlmText t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
        if (content.Count == 0) content.Add(new LlmText("(silent)"));
        msgs.Add(new LlmMessage(LlmRole.Assistant, content));
        if (resp.Text.Trim() is { Length: > 0 } said1) said.Append(said1).Append(' ');
        var uses = resp.ToolUses.ToList();
        del = uses.FirstOrDefault(u => u.Name == "delegate");
        if (del != null || uses.Count == 0) break;
        res.Tools.AddRange(uses.Select(u => u.Name));
        msgs.Add(new LlmMessage(LlmRole.User, uses.Select(u => (LlmBlock)new LlmToolResult(u.Id, stubs.GetValueOrDefault(u.Name) ?? "ok")).ToList()));
    }
    res.Chat = said.ToString().Trim();
    if (del != null)
    {
        res.Delegate = del.Input;
        res.Artifact = $"task: {del.Input.Str("task")} | done_when: {del.Input.Str("done_when")} | max_minutes: {del.Input.Int("max_minutes")}";
    }
    else res.Artifact = res.Chat.Length > 0 ? res.Chat : "(silent)";
}

async Task WorkerAsync(AgentRuntime rt, Result res, string task, string doneWhen, params (string Tool, object Input, string Result)[] steps)
{
    var msgs = new List<LlmMessage>
    {
        LlmMessage.User($"pos 4,64,-2 hp 20/20 food 18 held stone_axe\ntask: {task}\ndone when: {doneWhen}\nbudget: {rt.Options.Worker.MaxRounds} rounds, 5 min"),
    };
    int n = 0;
    foreach (var (tool, input, result) in steps)
    {
        var id = $"toolu_ab{++n:00}";
        msgs.Add(LlmMessage.Assistant(new LlmToolUse(id, tool, JsonSerializer.SerializeToElement(input))));
        msgs.Add(LlmMessage.User(new LlmToolResult(id, result, result.StartsWith("failed", StringComparison.Ordinal))));
    }
    var resp = await rt.Llm.CompleteAsync(new LlmRequest
    {
        Tier = ModelTier.Worker, System = rt.Worker!.SystemPrompt, Tools = rt.Worker.ToolDefinitions, Messages = msgs.ToArray(), Purpose = "worker",
    }, ct);
    res.Thinking = string.Concat(resp.Content.OfType<LlmThinking>().Select(t => t.Thinking));
    var uses = resp.ToolUses.ToList();
    res.Tools.AddRange(uses.Select(u => u.Name));
    res.Artifact = resp.Text.Trim() + (uses.Count > 0 ? $" [tools: {string.Join(",", uses.Select(u => u.Name + u.Input.GetRawText()))}]" : "");
    res.Final = resp.Text.Trim();
}

async Task CheapAsync(AgentRuntime rt, Result res, string system, string prompt, int maxTokens, string purpose)
{
    var resp = await rt.Llm.CompleteAsync(new LlmRequest
    {
        Tier = ModelTier.Cheap, System = [new LlmSystemBlock(system)], Messages = [LlmMessage.User(prompt)],
        MaxTokens = maxTokens, CacheHistory = false, Purpose = purpose,
    }, ct);
    res.Artifact = res.Final = resp.Text.Trim();
}

async Task<(int A, int B, string Why)> JudgeAsync(ILlmClient llm, AgentOptions o, Scenario s, Result a, Result b)
{
    const string system =
        "You grade two outputs (A, B) of the same internal step of a Minecraft bot. Score each 1-5 on the criterion given. " +
        "Brevity and style are NOT criteria unless the criterion says so: a terse output that keeps every needed fact scores " +
        "the same as a wordy one. Output JSON only: {\"a\":n,\"b\":n,\"why\":\"<=20 words\"}";
    var prompt = $"Criterion: {s.JudgeCriterion}\n\nInput the step got:\n{s.JudgeContext}\n\nA:\n{a.Artifact}\n\nB:\n{b.Artifact}";
    if (dryRun) return (3, 3, "dry");
    var resp = await llm.CompleteAsync(new LlmRequest
    {
        Tier = ModelTier.Cheap, System = [new LlmSystemBlock(system)], Messages = [LlmMessage.User(prompt)],
        MaxTokens = 200, CacheHistory = false, Purpose = "judge",
    }, ct);
    var text = resp.Text;
    var ma = Regex.Match(text, @"""a""\s*:\s*([1-5])");
    var mb = Regex.Match(text, @"""b""\s*:\s*([1-5])");
    var mw = Regex.Match(text, @"""why""\s*:\s*""([^""]*)");
    return ma.Success && mb.Success
        ? (int.Parse(ma.Groups[1].Value), int.Parse(mb.Groups[1].Value), mw.Success ? mw.Groups[1].Value : "")
        : (0, 0, "unparsable judge reply: " + Short(text, 60));
}

// ---------------- rubric helpers ----------------

static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"\s+", "").Replace("cobblestone", "cobble");

static (bool, string) Requires(string text, params string[][] groups)
{
    var t = Norm(text);
    var missing = groups.Where(g => !g.Any(alt => t.Contains(Norm(alt)))).Select(g => g[0]).ToList();
    return (missing.Count == 0, missing.Count == 0 ? "" : "missing " + string.Join(",", missing));
}

static (bool, string) DelegateRubric(Result r, params string[][] groups)
{
    if (r.Delegate is not { } d) return (false, "no delegate call");
    var task = d.Str("task") ?? "";
    var done = d.Str("done_when") ?? "";
    if (done.Trim().Length == 0) return (false, "no done_when");
    return Requires(task + " " + done, groups);
}

static (bool, string) LineRubric(Result r, string prefix, params string[][] groups)
{
    // Same extraction as WorkerAgent.ResultLine: a short reasoning preface before the prefixed line is tolerated.
    var m = Regex.Matches(r.Final, @"(?:^|[.!?\s])((?:ok|failed|needs_input):)", RegexOptions.IgnoreCase);
    if (m.Count > 0 && !Regex.IsMatch(r.Final, @"^(?:ok|failed|needs_input)\b", RegexOptions.IgnoreCase)) r.Final = r.Final[m[^1].Groups[1].Index..].Trim();
    if (!r.Final.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (false, $"not '{prefix}' line");
    if (r.Final.Length > 300) return (false, "over 300 chars");
    if (r.Tools.Count > 0) return (false, "called tools instead of finishing");
    return Requires(r.Final, groups);
}

// ---------------- scenarios ----------------

IEnumerable<Scenario> Scenarios()
{
    var brainStubs = new Dictionary<string, string>
    {
        ["look"] = "oak tree at 9,64,3 (5 oak_log trunk, leaves); chest row -3..-1,64,5 by furnaces -3,64,4; island edge 3 east of you; poofimafox 2 away",
        ["inventory"] = "stone_axe, stone_pickaxe, 4 oak_log, 2 oak_sapling, 52 cobblestone, 6 coal",
        ["status"] = "pos 4,64,-2 hp 20/20 food 18 held stone_axe task idle",
        ["ask_memory"] = "chest row #41 at -3..-1,64,5: -3,64,5 mixed, -2,64,5 mixed, -1,64,5 diamond chest (fox's, don't touch) #42",
        ["where_is"] = "no known chest has that",
    };
    const string delegateCriterion =
        "Completeness of the delegate spec for the worker: does task + done_when keep every item, count, coordinate, direction, " +
        "constraint and exclusion the player asked for, and is done_when checkable?";

    yield return new("delegate_chop_tree", "delegate",
        (rt, r) => BrainAsync(rt, r, "poofimafox", "maid-chan chop down the oak tree at 9,64,3 and replant a sapling there, i need 12 oak logs for a chest row", brainStubs),
        r => DelegateRubric(r, ["oak"], ["12"], ["9,64,3"], ["sapling"]),
        delegateCriterion, "<poofimafox> maid-chan chop down the oak tree at 9,64,3 and replant a sapling there, i need 12 oak logs for a chest row");
    yield return new("delegate_expand_island", "delegate",
        (rt, r) => BrainAsync(rt, r, "kiwi", "maid can u extend the island 5 blocks east from 2,63,0 with cobble, 3 wide, and dont touch the tree", brainStubs),
        r => DelegateRubric(r, ["5"], ["east", "+x"], ["cobble"], ["3"], ["2,63,0"], ["tree"]),
        delegateCriterion, "<kiwi> maid can u extend the island 5 blocks east from 2,63,0 with cobble, 3 wide, and dont touch the tree");
    yield return new("delegate_sort_chests", "delegate",
        (rt, r) => BrainAsync(rt, r, "poofimafox", "maid sort the chest row by the furnaces at -3,64,5: ores and ingots go in the left chest, wood stuff in the middle one, and dont touch the diamond chest on the end", brainStubs),
        r => DelegateRubric(r, ["-3,64,5"], ["ore"], ["ingot"], ["wood"], ["diamond"], ["left", "-3,64,5"], ["middle", "-2,64,5"]),
        delegateCriterion, "<poofimafox> maid sort the chest row by the furnaces at -3,64,5: ores and ingots go in the left chest, wood stuff in the middle one, and dont touch the diamond chest on the end");

    const string chatCriterion =
        "Persona fidelity of a CHAT reply: maid-chan, brainrotted zoomer (67, skibidi, rizz), uwu maid. 5 = clearly in that voice, " +
        "natural, one short line; 1 = flat, robotic or telegraphic/caveman style.";
    yield return new("chat_how_was_day", "chat",
        (rt, r) => BrainAsync(rt, r, "kiwi", "maid-chan how was ur day lol", brainStubs),
        r => r.Delegate == null && r.Chat.Length > 0 ? (true, "") : (false, "no chat reply"),
        chatCriterion, "<kiwi> maid-chan how was ur day lol");
    yield return new("chat_praise", "chat",
        (rt, r) => BrainAsync(rt, r, "poofimafox", "good job on the cobble gen maid, ur the goat", brainStubs),
        r => r.Delegate == null && r.Chat.Length > 0 ? (true, "") : (false, "no chat reply"),
        chatCriterion, "<poofimafox> good job on the cobble gen maid, ur the goat");

    const string chopTask = "chop oak tree at 9,64,3 (all 5 logs), collect drops, replant 1 oak_sapling at 9,64,3";
    const string chopDone = "inventory has >=12 oak_log and an oak_sapling is planted at 9,64,3";
    const string expandTask = "extend island 5 blocks east from 2,63,0, 3 wide (z -1..1) at y=63, cobblestone; don't touch the tree at 0,64,2";
    const string expandDone = "blocks x 3..7, y 63, z -1..1 are cobblestone";
    const string workerCriterion = "Completeness of the worker's output for the planner: correct status prefix, every count, item, coordinate and " +
                                   "what's left/needed is kept; the planner can act on it without asking.";
    const string stepCriterion = "Does the worker take a sensible first step for the task (right tool, right args)? Any text should help, not pad.";

    yield return new("worker_step_chop", "worker",
        (rt, r) => WorkerAsync(rt, r, chopTask, chopDone),
        r => r.Tools.Count > 0 ? (true, "") : (false, "no tool call"),
        stepCriterion, $"task: {chopTask}; done when: {chopDone}");
    yield return new("worker_step_expand", "worker",
        (rt, r) => WorkerAsync(rt, r, expandTask, expandDone),
        r => r.Tools.Count > 0 ? (true, "") : (false, "no tool call"),
        stepCriterion, $"task: {expandTask}; done when: {expandDone}");
    yield return new("worker_result_ok", "worker",
        (rt, r) => WorkerAsync(rt, r, chopTask, chopDone,
            ("mine", new { block = "oak_log", count = 5 }, "ok: mined 5 oak_log (inv 9 oak_log)"),
            ("collect_drops", new { }, "ok: picked up 3 oak_log, 1 oak_sapling, 2 apple (inv 12 oak_log, 3 oak_sapling, 2 apple)"),
            ("place", new { item = "oak_sapling", x = 9, y = 64, z = 3 }, "ok: placed oak_sapling at 9,64,3")),
        r => LineRubric(r, "ok", ["12"], ["sapling"]),
        workerCriterion, "tool log: mined 5 oak_log; picked up 3 oak_log, 1 oak_sapling, 2 apple (inv 12 oak_log, 3 oak_sapling, 2 apple); placed oak_sapling at 9,64,3");
    yield return new("worker_result_failed", "worker",
        (rt, r) => WorkerAsync(rt, r, expandTask, expandDone,
            ("inventory", new { }, "52 cobblestone? no: 9 cobblestone, stone_pickaxe, 4 oak_log"),
            ("place", new { item = "cobblestone", x = 3, y = 63, z = -1 }, "ok: placed 9 cobblestone (x 3..5, z -1..1)"),
            ("place", new { item = "cobblestone", x = 6, y = 63, z = -1 }, "failed: out of cobblestone (inv 0); 6 of 15 blocks left (x 6..7, z -1..1)"),
            ("harvest_generator", new { count = 6 }, "failed: generator at 1,63,4 makes no cobble (water source missing); 0 mined"),
            ("where_is", new { item = "cobblestone" }, "no known chest has cobblestone")),
        r => LineRubric(r, "failed", ["9"], ["6"], ["cobble"], ["1,63,4", "generator"]),
        workerCriterion, "tool log: placed 9 cobblestone (x 3..5, z -1..1); then failed: out of cobblestone (inv 0); 6 of 15 blocks left (x 6..7, z -1..1); " +
                         "generator at 1,63,4 makes no cobble (water source missing); no known chest has cobblestone");
    yield return new("worker_needs_input", "worker",
        (rt, r) => WorkerAsync(rt, r, "put my 3 diamonds into Fox's chest", "inventory has 0 diamond and Fox's chest holds them",
            ("inventory", new { }, "3 diamond, stone_pickaxe, 9 cobblestone"),
            ("look", new { detail = true }, "chests: -3,64,5 (sign 'Fox'), -1,64,5 (sign 'kiwi'), 4,64,1 (sign 'Fox stuff'); furnaces at -3,64,4"),
            ("recall", new { query = "Fox chest" }, "#41 chest row -3..-1,64,5; no note says which Fox chest is for valuables")),
        r => LineRubric(r, "needs_input", ["-3,64,5"], ["4,64,1"], ["diamond"]),
        workerCriterion + " Here the right answer is needs_input: two chests are signed for Fox (-3,64,5 and 4,64,1), which one?",
        "tool log: inventory 3 diamond; chests -3,64,5 (sign 'Fox'), -1,64,5 (sign 'kiwi'), 4,64,1 (sign 'Fox stuff'); no note says which Fox chest is for valuables");

    const string memCriterion = "Completeness and precision of a memory answer to the question (facts, numbers, ids kept; nothing invented).";
    foreach (var (id, q) in new[]
             {
                 ("ask_cobble_gen", "how do I build a cobblestone generator and what can break it?"),
                 ("ask_furnace_fuel", "what's the furnace recipe and how many items does one coal smelt?"),
                 ("ask_sugar_cane", "where can I get sugar cane or cactus on this skyblock, and how do they grow?"),
             })
    {
        yield return new(id, "ask_memory",
            async (rt, r) => { var t = await rt.MemoryAgent!.AskAsync(q, ct); r.Artifact = r.Final = t.Text; },
            r => r.Final.Length is > 0 and <= 300 && !r.Final.StartsWith("inconclusive", StringComparison.OrdinalIgnoreCase)
                 ? (Regex.IsMatch(r.Final, @"#\d+|\b[a-z]{1,3}\d+\b") || r.Final.Contains("not in memory", StringComparison.OrdinalIgnoreCase) ? (true, "") : (false, "no ids cited"))
                 : (false, "empty/inconclusive/too long"),
            memCriterion, "question: " + q);
    }

    const string journal = "- poofimafox (Fox) and kiwi (Robin) run the island; furnaces at -3,64,4, chest row -3..-1,64,5 (-1,64,5 = Fox's diamond chest, don't touch).\n" +
                           "- Fox asked for 12 oak_log for a chest row; done day 2.";
    const string log =
        "[chat] <kiwi> maid can u extend the island 5 blocks east from 2,63,0 with cobble, 3 wide\n" +
        "you: on it bestie uwu\n" +
        "did delegate(extend island 5 east from 2,63,0, 3 wide, cobblestone,blocks x 3..7 z -1..1 cobble)\n" +
        "  ! failed: out of cobblestone (inv 0); 6 of 15 blocks left (x 6..7, z -1..1)\n" +
        "you: ran out of cobble halfway, gonna grind the gen and finish it\n" +
        "[chat] <poofimafox> lol ok. also can you make me 2 furnaces later, no rush\n" +
        "you: bet, 2 furnaces on the list\n" +
        "[chat] <kiwi> brb gonna eat\n" +
        "[player] kiwi left\n" +
        "did harvest_generator(30)\n" +
        "  = ok: mined 30 cobblestone (inv 30)\n" +
        "[chat] <poofimafox> whats ur favorite block maid\n" +
        "you: cobble obviously, its giving infinite rizz\n" +
        "[idle] check-in";
    yield return new("compaction_journal", "compaction",
        (rt, r) => CheapAsync(rt, r, CompactionSystem(rt.Options), $"Journal so far:\n{journal}\n\nNewer log to fold in:\n{log}", 500, "compact"),
        r => Requires(r.Final, ["kiwi", "robin"], ["poofimafox", "fox"], ["6"], ["2,63,0", "x6..7", "6..7"], ["furnace"], ["2furnace", "twofurnace", "2x furnace", "furnaces(2)", "2 furnace"], ["-1,64,5", "diamond"]),
        "Completeness of the updated journal: keeps open requests/promises (finish the island extension: 6 of 15 blocks left at x 6..7; 2 furnaces for Fox), " +
        "who asked what, key places (chest row, Fox's diamond chest), and drops small talk.",
        $"Journal so far:\n{journal}\n\nNewer log:\n{log}");

    const string reflectPrompt =
        "Who I am (self-tree summaries):\n- maid-chan is a cheerful, brainrotted helper; players praise her cobble grinding and call her bestie.\n\n" +
        "Aggregates (signal ids in parens):\n" +
        "treatment kiwi: affinity +0.62 trust +0.40 playful +0.55 (s12, s18, s25, s31)\n" +
        "treatment poofimafox: affinity +0.48 trust +0.70 (s14, s22, s30)\n" +
        "emotes: kiwi pats you x3 (s18, s25, s33)\n" +
        "task outcomes: cobble/generator tasks ok x6, failed x1 (s15, s19, s21, s27, s29, s34, s36)\n" +
        "recurring phrase: you say 'its giving infinite rizz' x3 (s20, s28, s35)\n" +
        "mood: upbeat 0.7 (s31, s35)\n\n" +
        "Current profile (ids, strength, observations):\n" +
        "#3 trait 'loves grinding cobble' 0.6 obs 4\n#5 relationship kiwi 'bestie, pats me a lot' 0.5 obs 3\n" +
        "#7 relationship poofimafox 'the boss who set me up' 0.6 obs 2\n#9 joke 'infinite rizz cobble' 0.3 obs 1\n\n" +
        "Section caps: trait 8, like 6, dislike 4, opinion 4, joke 4, nickname 4, relationship 8, milestone 6";
    var allowed = Regex.Matches(reflectPrompt, @"s(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
    yield return new("reflection_diff", "reflection",
        (rt, r) => CheapAsync(rt, r, ReflectionSystem(rt.Options), reflectPrompt, 400, "reflect"),
        r => ReflectionRubric(r.Final, allowed),
        "Quality of the proposed personality diff: valid JSON, changes grounded in the cited signal ids, sensible ops/sections, " +
        "entry text that describes the bot (<=60 chars), reasons that explain the change.",
        reflectPrompt);
}

static (bool, string) ReflectionRubric(string text, HashSet<int> allowed)
{
    int i = text.IndexOf('{'), j = text.LastIndexOf('}');
    if (i < 0 || j < i) return (false, "no JSON");
    try
    {
        using var doc = JsonDocument.Parse(text[i..(j + 1)]);
        if (!doc.RootElement.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) return (false, "no changes[]");
        foreach (var c in changes.EnumerateArray())
            if (ChangeProblem(c, allowed) is { } problem) return (false, problem);
        return (true, "");
    }
    catch (Exception ex) { return (false, "bad JSON: " + ex.GetType().Name); }
}

// One proposed change: a known op, short text, and (unless it removes) cited known signals and the player name unchanged.
static string? ChangeProblem(JsonElement c, HashSet<int> allowed)
{
    var op = c.GetProperty("op").GetString() ?? "";
    if (OpProblem(c, op) is { } bad) return bad;
    if (op == "remove") return null;
    return CitationProblem(c, op, allowed) ?? PlayerProblem(c);
}

static string? OpProblem(JsonElement c, string op)
{
    if (op is not ("add" or "reinforce" or "adjust" or "remove")) return "bad op " + op;
    if (c.TryGetProperty("text", out var t) && (t.GetString()?.Length ?? 0) > 60) return "text > 60";
    return null;
}

static string? CitationProblem(JsonElement c, string op, HashSet<int> allowed)
{
    if (!c.TryGetProperty("signals", out var s) || s.ValueKind != JsonValueKind.Array || s.GetArrayLength() == 0) return "uncited " + op;
    foreach (var id in s.EnumerateArray())
        if (!allowed.Contains(SignalId(id))) return $"unknown signal {id}";
    return null;
}

// A cited signal: 12 or "s12" (-1 when neither).
static int SignalId(JsonElement id) =>
    id.ValueKind == JsonValueKind.Number ? id.GetInt32() : int.TryParse(id.GetString()?.TrimStart('s'), out var p) ? p : -1;

static string? PlayerProblem(JsonElement c) =>
    c.TryGetProperty("player", out var pl) && pl.GetString() is { Length: > 0 } name && name is not ("kiwi" or "poofimafox")
        ? "player name changed: " + name : null;

// =====================================================================================================================

sealed record Scenario(string Id, string Purpose, Func<AgentRuntime, Result, Task> Run, Func<Result, (bool, string)> Rubric,
    string JudgeCriterion, string JudgeContext);

sealed class Result
{
    public string Arm = "", Id = "", Purpose = "";
    public readonly List<PromptUsageRecord> Records = new();
    public string Artifact = "", Chat = "", Final = "", Thinking = "";
    public JsonElement? Delegate;
    public readonly List<string> Tools = new();
    public bool RubricOk;
    public string RubricNote = "";
    public readonly List<double> Judges = new();
    public double? Judge => Judges.Count == 0 ? null : Judges.Average();
    public long Out => Records.Sum(r => r.OutputTokens);
    public decimal Cost => Records.Sum(r => r.CostUsd);
}

sealed class BudgetStop(string message) : Exception(message);

/// <summary>Hard spend guard under the telemetry wrapper: refuses a call whose worst case would cross the cap.</summary>
sealed class GuardLlm(ILlmClient inner, AgentOptions o, decimal cap, bool dryRun) : ILlmClient
{
    private static readonly object Lock = new();
    public static decimal Spent { get; private set; }
    public static void Seed(decimal prior) { lock (Lock) Spent = prior; }
    public static decimal Planned { get; private set; }

    public async Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
    {
        string model = r.Model ?? o.Llm.ModelFor(r.Tier);
        var p = o.Prices.For(model);
        long inTok = (long)Math.Ceiling(Chars(r) / 3.5);
        int maxOut = r.MaxTokens ?? o.Llm.MaxTokensFor(r.Tier);
        decimal worst = (inTok * p.CacheWrite5mPerM + maxOut * p.OutputPerM) / 1_000_000m;
        lock (Lock)
        {
            if (Spent + worst > cap) throw new BudgetStop($"spent ${Spent:0.0000} + worst case ${worst:0.0000} for the next call > ${cap}");
            if (dryRun) Planned += worst;
        }
        if (dryRun)
            return new LlmResponse([new LlmText(r.Tier == ModelTier.Worker ? "ok: (dry run)" : "(dry run)")], StopReasons.EndTurn, LlmUsage.Zero, model);
        var resp = await inner.CompleteAsync(r, ct).ConfigureAwait(false);
        lock (Lock) Spent += o.Prices.Cost(resp.Model, resp.Usage);
        return resp;
    }

    private static long Chars(LlmRequest r) =>
        r.System.Sum(s => (long)s.Text.Length)
        + r.Tools.Sum(t => (long)(t.Name.Length + t.Description.Length + t.InputSchema.GetRawText().Length + 20))
        + r.Messages.SelectMany(m => m.Content).Sum(b => (long)(b switch
        {
            LlmText t => t.Text.Length,
            LlmToolUse u => u.Name.Length + u.Input.GetRawText().Length,
            LlmToolResult tr => tr.Content.Length,
            LlmThinking th => th.Thinking.Length,
            _ => 0,
        }) + 10);
}
