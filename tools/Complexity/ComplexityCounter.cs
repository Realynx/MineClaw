using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MineClaw.Complexity;

/// <summary>
/// McCabe complexity of one unit: 1 + one per decision point. Decision points (VS Code Metrics / Roslyn CA1502 convention):
/// if (so else-if counts once more), ?:, while, do, for, foreach, catch, &amp;&amp;, ||, ??, ??=,
/// each non-default case label of a switch statement, each switch-expression arm except a bare discard (<c>_ =&gt;</c>),
/// and each <c>and</c>/<c>or</c> pattern combinator. Not counted: else, default, goto, ?. (unless asked), when, is/not.
/// Nested units (local functions, and lambdas unless inlined) are measured on their own and not added here.
/// </summary>
internal sealed class ComplexityCounter(bool countConditionalAccess)
{
    private static readonly HashSet<SyntaxKind> Decisions =
    [
        SyntaxKind.IfStatement,
        SyntaxKind.ConditionalExpression,
        SyntaxKind.WhileStatement,
        SyntaxKind.DoStatement,
        SyntaxKind.ForStatement,
        SyntaxKind.ForEachStatement,
        SyntaxKind.ForEachVariableStatement,
        SyntaxKind.CatchClause,
        SyntaxKind.LogicalAndExpression,
        SyntaxKind.LogicalOrExpression,
        SyntaxKind.CoalesceExpression,
        SyntaxKind.CoalesceAssignmentExpression,
        SyntaxKind.CaseSwitchLabel,
        SyntaxKind.CasePatternSwitchLabel,
        SyntaxKind.AndPattern,
        SyntaxKind.OrPattern,
    ];

    /// <summary>Complexity of <paramref name="nodes"/>, not descending into nodes for which <paramref name="isUnit"/> holds.</summary>
    public int Measure(IEnumerable<SyntaxNode> nodes, Func<SyntaxNode, bool> isUnit)
    {
        var complexity = 1;
        foreach (var node in nodes)
        {
            complexity += node.DescendantNodesAndSelf(n => n == node || !isUnit(n)).Count(IsDecision);
        }

        return complexity;
    }

    private bool IsDecision(SyntaxNode node) => node switch
    {
        SwitchExpressionArmSyntax arm => arm.Pattern is not DiscardPatternSyntax || arm.WhenClause is not null,
        ConditionalAccessExpressionSyntax => countConditionalAccess,
        _ => Decisions.Contains(node.Kind()),
    };
}
