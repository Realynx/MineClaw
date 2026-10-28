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

    /// <summary>Brain-only cached line: instant triggers (standing bindings) exist and when to offer them (docs/design/reactive-character.md).</summary>
    public const string BindLine =
        "\nInstant triggers: script{op:bind,name,goal,params:{on:player.punchBlock|player.hurt|player.pickup|player.equip|player.say|me.hurt|..., player,held,block,for,minutes}} runs a short job the instant it happens, no thinking (\"punch the log you want chopped\"). Offer one when a player repeats a request. Social moments you handled (thanks, come...): bind kind:reaction so next time it plays at once. op=bindings lists, op=unbind removes.\n";

    public static readonly string[] Ops = ["run", "status", "pause", "resume", "cancel", "replace", "save", "rollback", "list", "show"];

    /// <summary>Binding ops (accepted, documented only in <see cref="BindLine"/>: no tool-definition bytes).</summary>
    public static readonly string[] BindOps = ["bind", "unbind", "bindings"];

    /// <summary>Refusal for any non-brain tier that sends code.</summary>
    public const string BrainOnly = "scripts are written by the brain; return needs_input with what you need automated";

    /// <summary>The worker's whole script knowledge (cached worker prefix, ≤ 20 tokens).</summary>
    public const string WorkerLine = "\nscript: run saved ones by name+params; new code? ask in needs_input.\n";

    /// <summary>Lua code arriving where only JS is accepted.</summary>
    public const string JsOnly = "scripts are JavaScript now (Lua is legacy): send a goal and the author writes it";

    /// <summary>
    /// The worked examples the author sees (each one is lint-clean and runs in the tests). The jobs the old SDK baked in as
    /// helpers (mineGen, harvestFarm, chopTrees, guardArea: now <c>99-compat.js</c>, deprecated) live here, composed from the
    /// primitives, so a strategy is something a script (or memory) chooses, never SDK code.
    /// </summary>
    public static readonly IReadOnlyList<(string Title, string Code)> Examples =
    [
        ("gen mining with policies", """
            // purpose: mine n cobble at the gen and store it, staying fed and tooled
            const P = params({ n: 64, at: 'auto', chest: null })
            use(keepFed(), replaceToolWhenBroken('pickaxe'))
            ensure('pickaxe')
            const r = expect(gather('cobblestone', P.n, { from: 'gen', at: P.at, stash: P.chest }))
            return `mined ${r.n} cobble`
            """),
        ("reactive state machine", """
            // purpose: mine the gen by day, guard it at night, retreat when hurt; stop after n cobble
            const P = params({ n: 128, at: 'auto', home: 'home' })
            const bb = { mined: 0 }
            return machine({
              initial: 'mine', bb, policies: [keepFed()],
              on: { lowhp: 'retreat', [when(() => bb.mined >= P.n)]: 'done' },
              states: {
                mine: { tick: () => { bb.mined += gather('cobblestone', 16, { from: 'gen', at: P.at, stash: true }).n }, on: { night: 'guard' } },
                guard: { tick: () => { const m = hostile(8); if (!m || !ok(attack(m.name))) wait(3) }, on: { day: 'mine' } },
                retreat: { enter: () => { go(P.home) }, on: { [after(30000)]: 'mine' } },
              },
            })
            """),
        ("two bodies at once", """
            // purpose: n cobble per body at the gen (auto cells), stored; one result line
            const P = params({ n: 64, bodies: ['67', '69'] })
            function miner(a) {
              const got = gather('cobblestone', a.n, { from: 'gen', stash: true }).n
              crew.inc('cobble', got)
              return `${me.body} ${got}`
            }
            function main() {
              const r = parallel(P.bodies.map(b => [b, 'miner', { n: P.n }]))
              return `gen crew: ${crew.get('cobble')} cobble (${r.join(', ')})`
            }
            """),
        ("chop trees, replant, wait for regrowth", """
            // purpose: chop the grown trees at the spots (else the nearest), replanting, until n logs; store them
            const P = params({ spots: null, n: 4, rounds: 20, waitS: 30, chest: null })
            const r = quota(P.n, left => {
              const trees = P.spots ? P.spots.filter(s => /log/.test(block(s))) : find('log', { limit: 1 })
              const done = forEach(trees, t => {
                const c = chop(t)
                if (ok(c) && /not replanted/.test(c.msg) && inv('sapling')) log('replant: ' + place('sapling', t).msg)
                return c
              }, { budget: left })
              if (done.n < left) wait(P.waitS)
              return done.n
            }, { max: P.rounds, idle: P.rounds })
            stash(['log'], { to: P.chest })
            return `chopped ${r.n}`
            """),
        ("harvest a farm in rounds", """
            // purpose: harvest the ripe wheat around here (replanting) each round, store it, wait for regrowth
            const P = params({ rounds: 3, r: 8, waitS: 120, chest: null })
            let n = 0
            for (let i = 1; i <= P.rounds; i++) {
              n += gather('wheat', 64, { from: 'harvest', r: P.r, stash: P.chest }).n
              if (i < P.rounds) wait(P.waitS)
            }
            return `harvested ${n} wheat`
            """),
        ("guard an area at night", """
            // purpose: patrol an area at night fighting hostiles that come near; retreat home at low hp; stop at day
            const P = params({ at: null, r: 8, home: 'home', low: 6, rounds: 40 })
            use(keepFed(), defendIfAttacked({ retreatBelow: P.low, home: P.home }))
            let fights = 0
            const fight = () => { const m = hostile(P.r); if (m && me.hp > P.low && ok(attack(m.name))) fights++; return m }
            on('mob.near', () => { fight() })
            patrol(P.at ? { at: P.at, r: P.r } : [me.pos], () => {
              if (me.hp <= P.low) { expect(go(P.home)); return 'stop' }
              if (!fight()) wait(3)
            }, { rounds: P.rounds, until: () => !night() })
            return `fights ${fights}`
            """),
    ];

    /// <summary>The first example (lint-clean; tests run it).</summary>
    public static string Exemplar => Unindent(Examples[0].Code);

    private static readonly Lazy<string> System = new(() =>
        "You write JavaScript job scripts for a Minecraft bot (the Jint engine: ES2023, strict); the world and game version come in " +
        "the brief, so never assume a skyblock or one version's item names. Reply with ONLY the script in one ```js block.\n" +
        "Rules: line 1 \"// purpose: <what it does>\". Tunables at the top: const P = params({n: 64, at: 'auto'}) (the brain retunes " +
        "them later without new code). Host calls are synchronous: no await, no Promise, no timers. Every game function returns " +
        "{ok, msg, n}, and so does every SDK primitive (gather, stash, handTo, makeRoom, patrol, quota, forEach): check ok (expect(r)). " +
        "Compose the primitives (examples below show common jobs); never re-implement one, never call a deprecated helper (mineGen, " +
        "harvestFarm, chopTrees, guardArea, store, restock, deliver, waitForDay). Loops bounded (for, upto, until) or a machine() with a way to end; reactive jobs = machine() with " +
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
        $"same names and defaults), same result text; compose the SDK primitives and policies where they fit and drop what they make redundant.\n" +
        $"```lua\n{lua}\n```\n" + facts + $"\nAt most {maxChars} chars of code.";

    internal static string Unindent(string s)
    {
        var lines = s.Replace("\r", "").Split('\n');
        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())).Trim();
    }
}
