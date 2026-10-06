using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using TypoAnalyzers.Core;

namespace TypoAnalyzers.Analyzers;

/// <summary>
/// TYPO0003 repeated condition in an if-else chain, TYPO0004 adjacent 'if' statements with the same condition, TYPO0005 nested 'if' repeating an enclosing check.
/// </summary>
/// <remarks>
/// All three share the "equivalent condition, and nothing in between can change it" question, which is why they live in one analyzer.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RepeatedConditionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        Descriptors.RepeatedElseIfCondition,
        Descriptors.RepeatedAdjacentCondition,
        Descriptors.RepeatedNestedCondition,
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            var purity = new PurityOracle(start.Compilation);
            start.RegisterOperationAction(context => AnalyzeIf(context, purity), OperationKind.Conditional);
            start.RegisterOperationAction(context => AnalyzeBlock(context, purity), OperationKind.Block);
        });
    }

    private static void AnalyzeIf(OperationAnalysisContext context, PurityOracle purity)
    {
        var conditional = (IConditionalOperation)context.Operation;
        if (conditional.Syntax is not IfStatementSyntax)
        {
            return;
        }

        if (IsChainRoot(conditional) && conditional.WhenFalse is IConditionalOperation { Syntax: IfStatementSyntax })
        {
            AnalyzeElseIfChain(context, conditional, purity);
        }

        AnalyzeEnclosingChecks(context, conditional, purity);
    }

    private static void AnalyzeElseIfChain(OperationAnalysisContext context, IConditionalOperation root, PurityOracle purity)
    {
        var conditions = new List<IOperation>();
        for (IOperation? current = root; current is IConditionalOperation { Syntax: IfStatementSyntax } link; current = link.WhenFalse)
        {
            conditions.Add(link.Condition);
        }

        for (var i = 1; i < conditions.Count; i++)
        {
            for (var j = i - 1; j >= 0; j--)
            {
                // An impure condition between the two could change what the later one sees.
                if (!purity.IsFree(conditions[j]))
                {
                    break;
                }

                if (Covers(conditions[j], conditions[i], BinaryOperatorKind.ConditionalOr))
                {
                    Report(context, Descriptors.RepeatedElseIfCondition, conditions[i], conditions[j]);
                    break;
                }
            }
        }
    }

    private static void AnalyzeEnclosingChecks(OperationAnalysisContext context, IConditionalOperation inner, PurityOracle purity)
    {
        if (!purity.IsFree(inner.Condition))
        {
            return;
        }

        var crossedLoop = false;
        var onlyElseIfLinks = true;
        var child = (IOperation)inner;
        for (var ancestor = inner.Parent; ancestor is not null; child = ancestor, ancestor = ancestor.Parent)
        {
            switch (ancestor)
            {
                case IAnonymousFunctionOperation or ILocalFunctionOperation:
                    return;

                case ILoopOperation:
                    crossedLoop = true;
                    onlyElseIfLinks = false;
                    continue;

                case IConditionalOperation { Syntax: IfStatementSyntax } outer:
                    // A statement can only sit inside an if's condition through a lambda, which already ended the walk, so the child is the then or else branch.
                    var knownTrue = ReferenceEquals(child, outer.WhenTrue);
                    onlyElseIfLinks &= !knownTrue && child is IConditionalOperation { Syntax: IfStatementSyntax };

                    // Pure else-if chains are reported by TYPO0003.
                    if (!onlyElseIfLinks && TryReportKnown(context, inner, outer, knownTrue, crossedLoop, purity))
                    {
                        return;
                    }

                    continue;

                default:
                    onlyElseIfLinks = false;
                    continue;
            }
        }
    }

    private static bool TryReportKnown(OperationAnalysisContext context, IConditionalOperation inner, IConditionalOperation outer, bool knownTrue, bool crossedLoop, PurityOracle purity)
    {
        // Inside 'then' every conjunct of the outer condition is true; inside 'else' every disjunct is false.
        var kind = knownTrue ? BinaryOperatorKind.ConditionalAnd : BinaryOperatorKind.ConditionalOr;
        var known = OperandChain.Rent();
        var parts = OperandChain.Rent();
        try
        {
            Split(outer.Condition, kind, known);
            Split(inner.Condition, kind, parts);
            foreach (var part in parts)
            {
                foreach (var fact in known)
                {
                    // A repeated fact keeps the known value; its negation, as in 'if (s == null) { if (s != null) ... }', has the opposite one.
                    bool value;
                    if (Negation.AreSame(fact, part))
                    {
                        value = knownTrue;
                    }
                    else if (Negation.AreOpposite(fact, part))
                    {
                        value = !knownTrue;
                    }
                    else
                    {
                        continue;
                    }

                    var branch = knownTrue ? outer.WhenTrue : outer.WhenFalse!;
                    TextSpan? after = crossedLoop ? null : TextSpan.FromBounds(inner.Syntax.SpanStart, branch.Syntax.Span.End);
                    if (ReadSet.Of(part).MayBeChangedBy(branch, purity, after))
                    {
                        return false;
                    }

                    Report(context, Descriptors.RepeatedNestedCondition, part, fact, value ? "true" : "false");
                    return true;
                }
            }

            return false;
        }
        finally
        {
            OperandChain.Return(parts);
            OperandChain.Return(known);
        }
    }

    private static void AnalyzeBlock(OperationAnalysisContext context, PurityOracle purity)
    {
        var operations = ((IBlockOperation)context.Operation).Operations;
        for (var i = 1; i < operations.Length; i++)
        {
            if (operations[i - 1] is not IConditionalOperation { Syntax: IfStatementSyntax, WhenFalse: null } first
                || operations[i] is not IConditionalOperation { Syntax: IfStatementSyntax } second)
            {
                continue;
            }

            if (!purity.IsFree(second.Condition) || !Negation.AreSame(first.Condition, second.Condition))
            {
                continue;
            }

            if (Jumps.AlwaysExits(first.WhenTrue) || !ReadSet.Of(first.Condition).MayBeChangedBy(first.WhenTrue, purity))
            {
                Report(context, Descriptors.RepeatedAdjacentCondition, second.Condition, first.Condition);
            }
        }
    }

    private static bool IsChainRoot(IConditionalOperation conditional) =>
        conditional.Parent is not IConditionalOperation parent || !ReferenceEquals(parent.WhenFalse, conditional);

    private static bool Covers(IOperation earlier, IOperation later, BinaryOperatorKind kind)
    {
        if (Negation.AreSame(earlier, later))
        {
            return true;
        }

        if (earlier is not IBinaryOperation binary || binary.OperatorKind != kind)
        {
            return false;
        }

        var parts = OperandChain.Rent();
        try
        {
            OperandChain.Flatten(binary, parts);
            foreach (var part in parts)
            {
                if (Negation.AreSame(part, later))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            OperandChain.Return(parts);
        }
    }

    private static void Split(IOperation condition, BinaryOperatorKind kind, List<IOperation> parts)
    {
        if (condition is IBinaryOperation binary && binary.OperatorKind == kind && binary.OperatorMethod is null)
        {
            OperandChain.Flatten(binary, parts);
        }
        else
        {
            parts.Add(condition);
        }
    }

    private static void Report(OperationAnalysisContext context, DiagnosticDescriptor descriptor, IOperation condition, IOperation original, string? extra = null) =>
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor,
            condition.Syntax.GetLocation(),
            additionalLocations: [original.Syntax.GetLocation()],
            messageArgs: extra is null ? [condition.Syntax.ToString()] : [condition.Syntax.ToString(), extra]));
}
