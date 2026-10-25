using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Typo.Core;

namespace Typo.Analyzers;

/// <summary>
/// TYPO0003 is a condition repeated in an if-else chain.
/// TYPO0004 is an 'if' repeating the condition of the preceding one, or of an earlier one that always exits.
/// TYPO0005 is a nested 'if' or later else-if repeating, or negating, an enclosing check.
/// </summary>
/// <remarks>
/// All three share the "equivalent condition, and nothing in between can change it" question, which is why they live in one analyzer.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RepeatedConditionAnalyzer : DiagnosticAnalyzer
{
    [ThreadStatic]
    private static List<int>? t_hashes;

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
            var purity = PurityOracle.For(start.Compilation);
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
        var crossedLock = false;
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

                case ILockOperation:
                    crossedLock = true;
                    onlyElseIfLinks = false;
                    continue;

                case IConditionalOperation { Syntax: IfStatementSyntax } outer:
                    // A statement can only sit inside an if's condition through a lambda, which already ended the walk, so the child is the then or else branch.
                    var knownTrue = ReferenceEquals(child, outer.WhenTrue);
                    onlyElseIfLinks &= !knownTrue && child is IConditionalOperation { Syntax: IfStatementSyntax };

                    if (TryReportKnown(context, inner, outer, knownTrue, onlyElseIfLinks, crossedLoop, crossedLock, purity))
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

    // In a pure else-if chain, a whole condition repeating an earlier disjunct is left to TYPO0003, which reports exactly that.
    private static bool TryReportKnown(OperationAnalysisContext context, IConditionalOperation inner, IConditionalOperation outer, bool knownTrue, bool inElseIfChain, bool crossedLoop, bool crossedLock, PurityOracle purity)
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
                    // A repeated fact keeps the known value, and a true fact makes every test it excludes false.
                    // A false fact makes only its exact negation true, since 'a < b' and 'a >= b' are both false on NaN or null.
                    bool value;
                    if (Negation.AreSame(fact, part))
                    {
                        if (inElseIfChain && parts.Count == 1)
                        {
                            continue;
                        }

                        value = knownTrue;
                    }
                    else if (knownTrue ? Negation.Excludes(fact, part) : Negation.AreOpposite(fact, part))
                    {
                        value = !knownTrue;
                    }
                    else
                    {
                        continue;
                    }

                    // Double-checked locking: another thread may have changed shared state while this one waited for the lock.
                    var reads = ReadSet.Of(part);
                    if (crossedLock && reads.ReadsState)
                    {
                        return false;
                    }

                    var branch = knownTrue ? outer.WhenTrue : outer.WhenFalse!;
                    TextSpan? after = crossedLoop ? null : TextSpan.FromBounds(inner.Syntax.SpanStart, branch.Syntax.Span.End);
                    if (reads.MayBeChangedBy(branch, purity, after))
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

    // After an 'if' that always exits, control only reaches the following statements when its condition was false, so a later 'if' repeating it is dead even with statements in between.
    private static void AnalyzeBlock(OperationAnalysisContext context, PurityOracle purity)
    {
        var operations = ((IBlockOperation)context.Operation).Operations;
        if (operations.Length < 2)
        {
            return;
        }

        // Long runs of guard clauses would otherwise compare every pair of conditions in full.
        var hashes = t_hashes ?? new List<int>(operations.Length);
        t_hashes = null;
        try
        {
            foreach (var operation in operations)
            {
                hashes.Add(operation is IConditionalOperation { Syntax: IfStatementSyntax } conditional ? Negation.GetHashCode(conditional.Condition) : 0);
            }

            for (var i = 0; i < operations.Length - 1; i++)
            {
                if (operations[i] is not IConditionalOperation { Syntax: IfStatementSyntax, WhenFalse: null } first)
                {
                    continue;
                }

                var exits = Jumps.AlwaysExits(first.WhenTrue);
                var last = exits ? operations.Length - 1 : i + 1;
                for (var k = i + 1; k <= last; k++)
                {
                    if (hashes[k] == hashes[i]
                        && operations[k] is IConditionalOperation { Syntax: IfStatementSyntax } second
                        && purity.IsFree(second.Condition)
                        && Negation.AreSame(first.Condition, second.Condition))
                    {
                        var reads = ReadSet.Of(first.Condition);
                        var changed = exits ? AnyMayChange(operations, i + 1, k, reads, purity) : reads.MayBeChangedBy(first.WhenTrue, purity);
                        if (!changed)
                        {
                            Report(context, Descriptors.RepeatedAdjacentCondition, second.Condition, first.Condition);
                        }

                        break;
                    }
                }
            }
        }
        finally
        {
            hashes.Clear();
            t_hashes = hashes;
        }
    }

    // A label lets a jump reach the later statements without passing the first check.
    private static bool AnyMayChange(ImmutableArray<IOperation> operations, int start, int end, ReadSet reads, PurityOracle purity)
    {
        for (var k = start; k < end; k++)
        {
            if (operations[k] is ILabeledOperation || reads.MayBeChangedBy(operations[k], purity))
            {
                return true;
            }
        }

        return false;
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
            messageArgs: extra is null ? [Quote.Of(condition.Syntax)] : [Quote.Of(condition.Syntax), extra]));
}
