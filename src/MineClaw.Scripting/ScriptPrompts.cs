namespace MineClaw.Scripting;

/// <summary>
/// Every prompt word the script feature costs. The <c>script</c> tool sits in both cached prefixes (brain and worker; ≤ 150
/// tokens including its schema, with the brain's API pointer folded into the description, ≤ 60 tokens). The cheat-sheet
/// (≤ 250 tokens) goes into the worker's cached system prompt only. Scripts are model output (Opus output is the expensive
/// token), so the Lua API is globals with short names and table args: typical jobs are 2-6 lines.
/// </summary>
public static class ScriptPrompts
{
    public const string ToolName = "script";

    /// <summary>The brain's whole API knowledge (part of <see cref="ToolDescription"/>).</summary>
    public const string BrainPointer =
        "keep it short: go{\"x,y,z\"} mine{\"dirt\",9} chop{}, any skill by name -> ok,msg,n; inv\"log\" me.hp wait(s) wake\"why\"";

    /// <summary>Named runs with code are saved (versioned); a name alone runs the saved script.</summary>
    public const string ToolDescription = "Lua job, runs while you sleep: >3 steps/loops (big: delegate). Named=saved. " + BrainPointer;

    public static readonly string[] Ops = ["run", "status", "pause", "resume", "cancel", "replace"];

    /// <summary>Worker system-prompt block. Keep ≤ 250 tokens (≈ 875 chars).</summary>
    public const string CheatSheet = """

# script tool: Lua 5.2, runs in C# with no model calls. Use it for >3 steps or any loop. SHORT scripts (2-6 lines).
Actions return ok,msg,n; table args, positional or named: go{"x,y,z"|place|player} mine{"cobblestone",16} chop{}
craft{"planks",4} smelt{"iron_ore",8} deposit{} take{"coal",8} place{"torch","x,y,z"} dig{"x,y,z"} use{"x,y,z"} eat{}
move{{item="log",n="all",to="wood chest"}}, any catalog skill by name, skill("name",{..})
Ask: inv"log" inv.free() me.hp me.food me.pos block(x,y,z) near"chest" where"iron" night() players()
Do: wait(s) waitfor("chat"|"item"|"block"|"night",filter,s) say"hi" emote"waves" ask(p,q) wake"why" cp"label"
prog(pct) fail"msg" on("danger",fn) on("chat",{from="kiwi"},fn)  carry{} survives replace/resume_from; args{} = run args
e.g. while inv"log"<32 do local ok,m=chop{} if not ok then fail(m) end end deposit{}
""";
}
