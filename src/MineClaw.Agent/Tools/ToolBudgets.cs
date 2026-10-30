namespace MineClaw.Agent.Tools;

/// <summary>
/// The single source of the tool-definition token budgets (estimate: (name + description + schema chars) / 3.5 + 6 per tool),
/// shared by every budget test (Bot, Skills, Scripting). Tool definitions sit in the cached prefix (≈$0.0001 per call per
/// 500 tokens at cache-read prices); the real limit is bloat, so a new tool must earn its tokens or fold into <c>do</c>.
/// History: 2,000 → 2,300 (act, run_command) → 2,400 (amend_task, script ops) → 2,600 (standing_order, ask_player default)
/// → 2,700 (see, look map) → 2,800 with the <c>do</c> meta-tool, which replaced the brain's goto/follow/chop_tree/act and
/// lets the brain run mine/harvest/deposit/withdraw/craft/move_items without a delegate round. → 2,950 for <c>design</c> (≈130:
/// build from a description; its DSL cheat-sheet lives in the designer's own cached prompt, not the brain's). Its revision ops
/// (log/status/diff/commit/revert) came at no net cost: the op enum moved into the description (design ≈216 → ≈215).
/// </summary>
public static class ToolBudgets
{
    /// <summary>Brain (Opus) tool definitions, including the main process's merged hive tool.</summary>
    public const int Brain = 2950;

    /// <summary>The main process's hive tool (pinned by MineClaw.Hive.Tests.HiveToolTests).</summary>
    public const int HiveTool = 115;

    /// <summary>Worker (Sonnet) tool definitions.</summary>
    public const int Worker = 2250; // 2,000 → 2,150 (script, see) → 2,250 (concurrent tool-text growth, ≈2,180 measured)
}
