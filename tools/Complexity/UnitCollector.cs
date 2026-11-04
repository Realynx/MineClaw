using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MineClaw.Complexity;

/// <summary>One measured function.</summary>
internal sealed record FunctionReport(
    string File, int Line, int EndLine, string Member, string Kind, int Complexity, int Loc, string Project);

/// <summary>
/// Finds the measurable units of a syntax tree: methods, constructors, destructors, operators, accessors,
/// expression-bodied properties/indexers, local functions, lambdas/anonymous methods (unless inlined) and top-level statements.
/// </summary>
internal sealed class UnitCollector(ComplexityCounter counter, bool inlineLambdas)
{
    /// <summary>Measures every unit in <paramref name="root"/>.</summary>
    public IEnumerable<(string Member, string Kind, int Complexity, FileLinePositionSpan Span)> Collect(CompilationUnitSyntax root)
    {
        foreach (var unit in root.DescendantNodes().Where(IsUnit))
        {
            yield return (NameOf(unit), KindOf(unit), counter.Measure([unit], IsUnit), unit.GetLocation().GetLineSpan());
        }

        var globals = root.Members.OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count > 0)
        {
            var span = globals[0].SyntaxTree.GetLineSpan(TextSpanOf(globals));
            yield return ("<top-level statements>", "top-level", counter.Measure(globals, IsUnit), span);
        }
    }

    private static Microsoft.CodeAnalysis.Text.TextSpan TextSpanOf(List<GlobalStatementSyntax> globals) =>
        Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(globals[0].SpanStart, globals[^1].Span.End);

    private bool IsUnit(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax m => m.Body is not null || m.ExpressionBody is not null,
        AccessorDeclarationSyntax a => a.Body is not null || a.ExpressionBody is not null,
        BasePropertyDeclarationSyntax p => ExpressionBodyOf(p) is not null,
        LocalFunctionStatementSyntax => true,
        AnonymousFunctionExpressionSyntax lambda => !inlineLambdas || EnclosingUnit(lambda) is null,
        _ => false,
    };

    private static ArrowExpressionClauseSyntax? ExpressionBodyOf(BasePropertyDeclarationSyntax p) => p switch
    {
        PropertyDeclarationSyntax prop => prop.ExpressionBody,
        IndexerDeclarationSyntax indexer => indexer.ExpressionBody,
        _ => null,
    };

    private SyntaxNode? EnclosingUnit(SyntaxNode node) => node.Ancestors().FirstOrDefault(a => a is not AnonymousFunctionExpressionSyntax && IsUnit(a));

    private static string KindOf(SyntaxNode node) => node switch
    {
        ConstructorDeclarationSyntax => "constructor",
        DestructorDeclarationSyntax => "destructor",
        OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => "operator",
        AccessorDeclarationSyntax => "accessor",
        BasePropertyDeclarationSyntax => "property",
        LocalFunctionStatementSyntax => "local function",
        AnonymousFunctionExpressionSyntax => "lambda",
        _ => "method",
    };

    /// <summary>Dotted name: containing types, then members, e.g. <c>StoryColony.PickWork.Score</c> or <c>Foo.Bar.lambda@L42</c>.</summary>
    private static string NameOf(SyntaxNode node)
    {
        var parts = node.AncestorsAndSelf().Select(SegmentOf).Where(s => s is not null).Reverse();
        return string.Join('.', parts);
    }

    private static string? SegmentOf(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.Text,
        MemberDeclarationSyntax member => MemberSegment(member),
        AccessorDeclarationSyntax a => a.Keyword.Text,
        LocalFunctionStatementSyntax local => local.Identifier.Text,
        VariableDeclaratorSyntax v when v.Parent?.Parent is FieldDeclarationSyntax => v.Identifier.Text,
        AnonymousFunctionExpressionSyntax lambda => $"lambda@L{lambda.GetLocation().GetLineSpan().StartLinePosition.Line + 1}",
        _ => null,
    };

    private static string? MemberSegment(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax => "ctor",
        DestructorDeclarationSyntax d => "~" + d.Identifier.Text,
        OperatorDeclarationSyntax op => "operator " + op.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax conv => "operator " + conv.Type,
        PropertyDeclarationSyntax prop => prop.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax ev => ev.Identifier.Text,
        _ => null,
    };
}
