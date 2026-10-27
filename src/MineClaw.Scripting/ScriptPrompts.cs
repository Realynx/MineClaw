namespace MineClaw.Scripting;

/// <summary>
/// Every prompt word the script feature costs. Only the Opus brain writes scripts: the cheat-sheet (≤ 420 tokens, rules +
/// one exemplar) sits in the BRAIN's cached system prompt; the worker gets one line (≤ 20 tokens) saying it may run saved
/// scripts by name. The <c>script</c> tool sits in both cached prefixes (≤ 160 tokens including its schema). Scripts are
/// model output (Opus output is the expensive token), so the Lua API is globals with short names and table args.
/// </summary>
public static class ScriptPrompts
{
    public const string ToolName = "script";

    /// <summary>The tool description's API pointer (both prefixes).</summary>
    public const string BrainPointer =
        "keep it short: go{\"x,y,z\"} mine{\"dirt\",9} chop{}, any skill by name -> ok,msg,n; inv\"log\" me.hp wait(s) wake\"why\"";

    /// <summary>Named runs with code are saved (versioned); a name alone (or name@v2) runs the saved script with args.</summary>
    public const string ToolDescription = "Lua job, runs while you sleep: >3 steps/loops (big: delegate). Named=saved. " + BrainPointer;

    public static readonly string[] Ops = ["run", "status", "pause", "resume", "cancel", "replace", "save", "rollback", "list"];

    /// <summary>Refusal for any non-brain tier that sends code.</summary>
    public const string BrainOnly = "scripts are written by the brain; return needs_input with what you need automated";

    /// <summary>The worker's whole script knowledge (cached worker prefix, ≤ 20 tokens).</summary>
    public const string WorkerLine = "\nscript: run saved ones by name; new code? ask in needs_input.\n";

    /// <summary>Brain system-prompt block (only the brain writes scripts). Keep ≤ 420 tokens (≈ 1470 chars).</summary>
    public const string CheatSheet = """

# script tool: Lua 5.2 in C#, no model calls. For >3 steps or loops, short.
Actions -> ok,msg,n; table args, positional or named: go{"x,y,z"|place|player} mine{"cobblestone",16} chop{} gen{"x,y,z"}
craft{"planks",4} smelt{"iron_ore",8} deposit{at?,{"log"}} take{"coal",8} place{"torch","x,y,z"} dig{"x,y,z"} use{"x,y,z"} eat{} give{p,"log",5}
move{{item="log",n="all",to="chest"}}, any skill by name, skill("name",{..})
Ask: inv"log" inv.free() me.hp me.food me.pos block(x,y,z) near"chest" where"iron" night() players()
Do: wait(s) waitfor("chat"|"item"|"block"|"night",filter,s) emote"waves" ask(p,q) wake"why" cp"label" prog(pct)
say"hi"=players only log(x)=debug return"x"=result fail"why"=errors
on("danger",fn) on("chat",{from="kiwi"},fn) carry{} survives replace, args{}=run args
Helpers: retry(n,fn) upto(n,fn) expect(ok,msg) room(n) ensure"pickaxe"|{"pickaxe","food"}
Reliable: check each ok,msg; bound loops (upto/for); cp before long phases; verify via inv; wear: ensure,
full inv: room, empty chest: expect; never go on silently.
Saved: line 1 "-- purpose"; one almost fits? call it with args or op=save that name, don't fork.
-- purpose: mine n cobble at the gen, store it
ensure"pickaxe" room(2) local b=inv"cobblestone"
cp"mine" upto(args.n or 16,function() expect(retry(3,function() return gen{args.at,1} end)) return inv.free()>0 end)
expect(inv"cobblestone">b,"mined nothing")
cp"store" local ok,m=deposit{} expect(ok,m) return "stored"
""";

    /// <summary>The cheat-sheet's exemplar (lint-clean; tests run it).</summary>
    public static string Exemplar => CheatSheet[CheatSheet.IndexOf("-- purpose: mine n cobble", StringComparison.Ordinal)..].Trim();
}
