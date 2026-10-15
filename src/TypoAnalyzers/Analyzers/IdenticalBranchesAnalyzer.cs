using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TypoAnalyzers.Analyzers;

/// <summary>
/// TYPO0006 identical 'then' and 'else' parts, TYPO0007 two switch cases or else-if branches with identical bodies.
/// </summary>
/// <remarks>
/// Bodies are compared as syntax and then by binding, because equal text can bind to different symbols.
/// TYPO0007 ignores bodies without calls, object or array creation, assignments or increments, since <c>A => true, B => true</c> is idiomatic.
/// It also ignores a lone throw, though a switch section that wraps it in braces is still compared.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IdenticalBranchesAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        Descriptors.IdenticalBranches,
        Descriptors.DuplicateBranchBody,
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeIf, SyntaxKind.IfStatement);
        context.RegisterSyntaxNodeAction(AnalyzeConditionalExpression, SyntaxKind.ConditionalExpression);
        context.RegisterSyntaxNodeAction(AnalyzeSwitchStatement, SyntaxKind.SwitchStatement);
        context.RegisterSyntaxNodeAction(AnalyzeSwitchExpression, SyntaxKind.SwitchExpression);
    }

    private static void AnalyzeIf(SyntaxNodeAnalysisContext context)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.Else is not { } elseClause)
        {
            return;
        }

        if (elseClause.Statement is not IfStatementSyntax)
        {
            var whenTrue = Unwrap(ifStatement.Statement);
            if (!IsEmpty(whenTrue) && SameCode(whenTrue, Unwrap(elseClause.Statement), context.SemanticModel))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.IdenticalBranches,
                    ifStatement.Condition.GetLocation(),
                    additionalLocations: [ifStatement.Statement.GetLocation(), elseClause.Statement.GetLocation()],
                    messageArgs: ["if", ifStatement.Condition.ToString()]));
            }
        }

        if (ifStatement.Parent is not ElseClauseSyntax && elseClause.Statement is IfStatementSyntax)
        {
            AnalyzeElseIfChain(context, ifStatement);
        }
    }

    private static void AnalyzeElseIfChain(SyntaxNodeAnalysisContext context, IfStatementSyntax root)
    {
        var bodies = new List<(StatementSyntax Body, string Label)>();
        for (var link = root; link is not null; link = link.Else?.Statement as IfStatementSyntax)
        {
            bodies.Add((Unwrap(link.Statement), link.Condition.ToString()));
            if (link.Else is { Statement: not IfStatementSyntax } finalElse)
            {
                bodies.Add((Unwrap(finalElse.Statement), "else"));
            }
        }

        for (var i = 1; i < bodies.Count; i++)
        {
            if (!IsSubstantial(bodies[i].Body))
            {
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                if (SameCode(bodies[j].Body, bodies[i].Body, context.SemanticModel))
                {
                    ReportDuplicate(context, bodies[i].Body, bodies[j].Body, bodies[j].Label);
                    break;
                }
            }
        }
    }

    private static void AnalyzeConditionalExpression(SyntaxNodeAnalysisContext context)
    {
        var conditional = (ConditionalExpressionSyntax)context.Node;
        if (SameCode(conditional.WhenTrue, conditional.WhenFalse, context.SemanticModel))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.IdenticalBranches,
                conditional.Condition.GetLocation(),
                additionalLocations: [conditional.WhenTrue.GetLocation(), conditional.WhenFalse.GetLocation()],
                messageArgs: ["?:", conditional.Condition.ToString()]));
        }
    }

    private static void AnalyzeSwitchStatement(SyntaxNodeAnalysisContext context)
    {
        var sections = ((SwitchStatementSyntax)context.Node).Sections;
        for (var i = 1; i < sections.Count; i++)
        {
            if (!IsSubstantial(sections[i].Statements))
            {
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                if (SameCode(sections[j].Statements, sections[i].Statements, context.SemanticModel))
                {
                    ReportDuplicate(context, sections[i].Labels[0], sections[j].Labels[0], LabelText(sections[j].Labels[0]));
                    break;
                }
            }
        }
    }

    private static void AnalyzeSwitchExpression(SyntaxNodeAnalysisContext context)
    {
        var arms = ((SwitchExpressionSyntax)context.Node).Arms;
        for (var i = 1; i < arms.Count; i++)
        {
            if (!IsSubstantial(arms[i].Expression))
            {
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                if (SameCode(arms[j].Expression, arms[i].Expression, context.SemanticModel))
                {
                    ReportDuplicate(context, arms[i].Expression, arms[j].Expression, arms[j].Pattern.ToString());
                    break;
                }
            }
        }
    }

    private static void ReportDuplicate(SyntaxNodeAnalysisContext context, SyntaxNode duplicate, SyntaxNode original, string originalLabel) =>
        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.DuplicateBranchBody,
            duplicate.GetLocation(),
            additionalLocations: [original.GetLocation()],
            messageArgs: [originalLabel]));

    private static bool SameCode(SyntaxNode x, SyntaxNode y, SemanticModel model) =>
        SyntaxFactory.AreEquivalent(x, y, topLevel: false) && SameBindings(x, y, model);

    private static bool SameCode(SyntaxList<StatementSyntax> x, SyntaxList<StatementSyntax> y, SemanticModel model)
    {
        if (!SyntaxFactory.AreEquivalent(x, y, topLevel: false))
        {
            return false;
        }

        for (var i = 0; i < x.Count; i++)
        {
            if (!SameBindings(x[i], y[i], model))
            {
                return false;
            }
        }

        return true;
    }

    // Equal text can bind differently: in 'case Circle s: return s.Area();' and 'case Square s: return s.Area();' each 's' is another variable of another type.
    private static bool SameBindings(SyntaxNode x, SyntaxNode y, SemanticModel model)
    {
        using var left = x.DescendantNodesAndSelf().GetEnumerator();
        using var right = y.DescendantNodesAndSelf().GetEnumerator();
        while (left.MoveNext() && right.MoveNext())
        {
            if (left.Current is SimpleNameSyntax && !SameSymbol(model.GetSymbolInfo(left.Current).Symbol, model.GetSymbolInfo(right.Current).Symbol))
            {
                return false;
            }
        }

        return true;
    }

    // Locals and lambda parameters declared inside each copy, such as pattern variables or the 'o' of 'o => GetValue(o)', are interchangeable when name and type match.
    private static bool SameSymbol(ISymbol? x, ISymbol? y) => SymbolEqualityComparer.Default.Equals(x, y) || (x, y) switch
    {
        (ILocalSymbol left, ILocalSymbol right) => left.Name == right.Name && (SymbolEqualityComparer.Default.Equals(left.Type, right.Type) || BothPlainReferences(left.Type, right.Type)),
        (IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } } left, IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } } right) =>
            left.Name == right.Name && left.Ordinal == right.Ordinal && SymbolEqualityComparer.Default.Equals(left.Type, right.Type),
        _ => false,
    };

    // Members used through two reference-typed variables are compared by symbol, so 'arr.ToString()' on an Array and an IEnumerable<int> is the same call, while 'Circle.Area' and 'Square.Area' differ.
    // Strings and delegates are left out because their operators and conversions depend on the exact type.
    private static bool BothPlainReferences(ITypeSymbol x, ITypeSymbol y) =>
        x.IsReferenceType && y.IsReferenceType
        && x.SpecialType != SpecialType.System_String && y.SpecialType != SpecialType.System_String
        && x.TypeKind != TypeKind.Delegate && y.TypeKind != TypeKind.Delegate;

    private static string LabelText(SwitchLabelSyntax label) => label switch
    {
        CaseSwitchLabelSyntax value => value.Value.ToString(),
        CasePatternSwitchLabelSyntax pattern => pattern.Pattern.ToString(),
        _ => "default",
    };

    private static StatementSyntax Unwrap(StatementSyntax statement) =>
        statement is BlockSyntax { Statements.Count: 1 } block ? block.Statements[0] : statement;

    private static bool IsEmpty(StatementSyntax statement) =>
        statement is BlockSyntax { Statements.Count: 0 } or EmptyStatementSyntax;

    private static bool IsSubstantial(SyntaxList<StatementSyntax> statements)
    {
        foreach (var statement in statements)
        {
            if (IsSubstantial(statement))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSubstantial(SyntaxNode node)
    {
        if (node is ThrowStatementSyntax or ThrowExpressionSyntax)
        {
            return false;
        }

        foreach (var descendant in node.DescendantNodesAndSelf())
        {
            if (descendant is InvocationExpressionSyntax
                or BaseObjectCreationExpressionSyntax
                or ArrayCreationExpressionSyntax
                or ImplicitArrayCreationExpressionSyntax
                or AssignmentExpressionSyntax
                or PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression }
                or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression })
            {
                return true;
            }
        }

        return false;
    }
}
