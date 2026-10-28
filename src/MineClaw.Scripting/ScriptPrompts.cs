namespace MineClaw.Scripting;

/// <summary>
/// Every prompt word the script feature costs. The brain sees only the tiny <c>script</c> tool (name|goal, params, op) and one
/// cached line; it never writes code. The separate Opus author (<c>AuthorAgent</c>) gets the whole JS SDK reference, generated
/// from the SDK modules' doc comments, plus worked examples, in ITS OWN cached system prompt. The worker gets one line.
/// </summary>
public static class ScriptPrompts
{
    public const string ToolName = "script";

    /// <summary>The tool description's pointer (both prefixes).</summary>
    public const string BrainPointer = "name runs a saved job (params tune it); goal = Opus writes+saves a new one";

    /// <summary>Saved jobs run by name with params; a goal commissions the author. No code in the brain's schema.</summary>
    public const string ToolDescription = "JS job, runs while you sleep, 0 tokens: " + BrainPointer + ". Big/fuzzy: delegate.";

    /// <summary>Brain-only cached line: authoring goes to the separate Opus author; params retune a saved job without new code.</summary>
    public const string AuthorLine =
        "\nscript{name,goal}: Opus writes+saves a JS job apart, you get a summary. script{name,params} runs it tuned; op=save+params keeps them.\n";

    public static readonly string[] Ops = ["run", "status", "pause", "resume", "cancel", "replace", "save", "rollback", "list", "show"];

    /// <summary>Refusal for any non-brain tier that sends code.</summary>
    public const string BrainOnly = "scripts are written by the brain; return needs_input with what you need automated";

    /// <summary>The worker's whole script knowledge (cached worker prefix, ≤ 20 tokens).</summary>
    public const string WorkerLine = "\nscript: run saved ones by name+params; new code? ask in needs_input.\n";

    /// <summary>Lua code arriving where only JS is accepted.</summary>
    public const string JsOnly = "scripts are JavaScript now (Lua is legacy): send a goal and the author writes it";

    /// <summary>The worked examples the author sees (each one is lint-clean and runs in the tests).</summary>
    public static readonly IReadOnlyList<(string Title, string Code)> Examples =
    [
        ("gen mining with policies", """
            // purpose: mine n cobble at the gen and store it, staying fed and tooled
            const P = params({ n: 64, at: 'auto', chest: null })
            use(keepFed(), replaceToolWhenBroken('pickaxe'))
            const got = mineGen({ at: P.at, n: P.n, chest: P.chest })
            return `mined ${got} cobble`
            """),
        ("reactive state machine", """
            // purpose: mine the gen by day, guard it at night, retreat when hurt; stop after n cobble
            const P = params({ n: 128, at: 'auto', home: 'home' })
            const bb = { mined: 0 }
            return machine({
              initial: 'mine', bb, policies: [keepFed()],
              on: { lowhp: 'retreat', [when(() => bb.mined >= P.n)]: 'done' },
              states: {
                mine: { tick: () => { bb.mined += mineGen({ at: P.at, n: 16 }) }, on: { night: 'guard' } },
                guard: { tick: () => { guardArea({ radius: 8, home: P.home }) }, on: { day: 'mine' } },
                retreat: { enter: () => { go(P.home) }, on: { [after(30000)]: 'mine' } },
              },
            })
            """),
        ("two bodies at once", """
            // purpose: n cobble per body at the gen (auto cells), stored; one result line
            const P = params({ n: 64, bodies: ['67', '69'] })
            function miner(a) {
              const got = mineGen({ n: a.n })
              crew.inc('cobble', got)
              return `${me.body} ${got}`
            }
            function main() {
              const r = parallel(P.bodies.map(b => [b, 'miner', { n: P.n }]))
              return `gen crew: ${crew.get('cobble')} cobble (${r.join(', ')})`
            }
            """),
    ];

    /// <summary>The first example (lint-clean; tests run it).</summary>
    public static string Exemplar => Unindent(Examples[0].Code);

    private static readonly Lazy<string> System = new(() =>
        "You write JavaScript job scripts for a Minecraft Beta 1.8.1 skyblock bot (the Jint engine: ES2023, strict). Reply with ONLY " +
        "the script in one ```js block.\n" +
        "Rules: line 1 \"// purpose: <what it does>\". Tunables at the top: const P = params({n: 64, at: 'auto'}) (the brain retunes " +
        "them later without new code). Host calls are synchronous: no await, no Promise, no timers. Every game function returns " +
        "{ok, msg, n}: check ok (expect(r)) or call an SDK job that checks for you. Prefer the SDK: one call beats ten lines; never " +
        "re-implement a helper it has. Loops bounded (for, upto, until) or a machine() with a way to end; reactive jobs = machine() with " +
        "events/timers/conditions plus policies. Data goes to return/log, never fail() or say(). Several bodies: top level only " +
        "functions and constants, work in main(), roles run with parallel()/spawn(). After a draft you may get lint errors: fix them " +
        "all and answer with the WHOLE corrected script, nothing else.\n\n# SDK reference\n" + Sdk.SdkReference.Build() +
        "\n# Examples\n" + string.Join("\n", Examples.Select(e => $"## {e.Title}\n```js\n{Unindent(e.Code)}\n```\n")));

    /// <summary>
    /// The separate author context's system prompt (cached there, never in the brain's prefix): the rules, the generated SDK
    /// reference and the examples. The bot's own game functions ride in the brief.
    /// </summary>
    public static string AuthorSystem => System.Value;

    /// <summary>The author's brief: the brain's spec plus the facts the bridge pulled (game functions, saved scripts, places).</summary>
    public static string AuthorBrief(string name, string goal, string? constraints, string? args, string facts, int maxChars) =>
        $"Script name: {name}\nGoal: {goal}\n" +
        (string.IsNullOrWhiteSpace(constraints) ? "" : $"Constraints: {constraints}\n") +
        (string.IsNullOrWhiteSpace(args) ? "" : $"Example params: {args}\n") +
        facts + $"\nAt most {maxChars} chars of code.";

    /// <summary>The migration brief: one legacy Lua script to translate to JS with the SDK.</summary>
    public static string MigrationBrief(string name, string lua, string facts, int maxChars) =>
        $"Translate this saved Lua script \"{name}\" to JavaScript with the SDK. Same behaviour, same args (args.x → params with the " +
        $"same names and defaults), same result text; use SDK jobs/policies where they fit and drop what they make redundant.\n" +
        $"```lua\n{lua}\n```\n" + facts + $"\nAt most {maxChars} chars of code.";

    internal static string Unindent(string s)
    {
        var lines = s.Replace("\r", "").Split('\n');
        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())).Trim();
    }
}
