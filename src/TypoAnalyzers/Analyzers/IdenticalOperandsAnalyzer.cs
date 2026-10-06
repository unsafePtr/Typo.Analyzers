using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using TypoAnalyzers.Core;

namespace TypoAnalyzers.Analyzers;

/// <summary>
/// TYPO0001: a repeated operand in a <c>||</c>, <c>&amp;&amp;</c>, <c>|</c>, <c>&amp;</c>, <c>^</c>, <c>or</c> or <c>and</c> chain, or identical operands of a comparison, subtraction, division or remainder.
/// </summary>
/// <remarks>
/// Chains are flattened first because the left-associative tree hides <c>a || b || a</c> from a plain left-versus-right comparison.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IdenticalOperandsAnalyzer : DiagnosticAnalyzer
{
    [ThreadStatic]
    private static List<int>? t_hashes;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.IdenticalSubExpressions];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            var purity = new PurityOracle(start.Compilation);
            start.RegisterOperationAction(context => AnalyzeBinary(context, purity), OperationKind.Binary);
            start.RegisterOperationAction(AnalyzeBinaryPattern, OperationKind.BinaryPattern);
        });
    }

    private static void AnalyzeBinary(OperationAnalysisContext context, PurityOracle purity)
    {
        var binary = (IBinaryOperation)context.Operation;
        if (IsChainOperator(binary.OperatorKind))
        {
            if (binary.OperatorMethod is null && OperandChain.IsRoot(binary))
            {
                AnalyzeChain(context, binary, purity);
            }
        }
        else if (IsConstantWhenOperandsMatch(binary)
            && purity.IsFree(binary.LeftOperand)
            && OperationEquivalence.AreEquivalent(binary.LeftOperand, binary.RightOperand))
        {
            Report(context, binary.RightOperand, binary.LeftOperand, OperatorText(binary));
        }
    }

    private static void AnalyzeChain(OperationAnalysisContext context, IBinaryOperation root, PurityOracle purity)
    {
        var operands = OperandChain.Rent();
        var hashes = RentHashes();
        try
        {
            OperandChain.Flatten(root, operands);
            foreach (var operand in operands)
            {
                hashes.Add(OperationEquivalence.GetHashCode(operand));
            }

            for (var i = 1; i < operands.Count; i++)
            {
                var candidate = operands[i];
                if (!purity.IsFree(candidate))
                {
                    continue;
                }

                for (var j = 0; j < i; j++)
                {
                    if (hashes[j] == hashes[i] && OperationEquivalence.AreEquivalent(operands[j], candidate))
                    {
                        Report(context, candidate, operands[j], OperatorText(root));
                        break;
                    }
                }
            }
        }
        finally
        {
            ReturnHashes(hashes);
            OperandChain.Return(operands);
        }
    }

    private static void AnalyzeBinaryPattern(OperationAnalysisContext context)
    {
        var root = (IBinaryPatternOperation)context.Operation;
        if (!OperandChain.IsRoot(root))
        {
            return;
        }

        var operands = OperandChain.Rent();
        var hashes = RentHashes();
        try
        {
            OperandChain.Flatten(root, operands);
            foreach (var operand in operands)
            {
                hashes.Add(OperationEquivalence.GetPatternHashCode((IPatternOperation)operand));
            }

            for (var i = 1; i < operands.Count; i++)
            {
                var candidate = (IPatternOperation)operands[i];
                for (var j = 0; j < i; j++)
                {
                    if (hashes[j] == hashes[i] && OperationEquivalence.PatternsEquivalent((IPatternOperation)operands[j], candidate))
                    {
                        Report(context, candidate, operands[j], root.OperatorKind == BinaryOperatorKind.Or ? "or" : "and");
                        break;
                    }
                }
            }
        }
        finally
        {
            ReturnHashes(hashes);
            OperandChain.Return(operands);
        }
    }

    private static List<int> RentHashes()
    {
        var hashes = t_hashes ?? new List<int>(8);
        t_hashes = null;
        return hashes;
    }

    private static void ReturnHashes(List<int> hashes)
    {
        hashes.Clear();
        t_hashes = hashes;
    }

    private static bool IsChainOperator(BinaryOperatorKind kind) => kind
        is BinaryOperatorKind.ConditionalOr
        or BinaryOperatorKind.ConditionalAnd
        or BinaryOperatorKind.Or
        or BinaryOperatorKind.And
        or BinaryOperatorKind.ExclusiveOr;

    private static bool IsConstantWhenOperandsMatch(IBinaryOperation binary)
    {
        switch (binary.OperatorKind)
        {
            case BinaryOperatorKind.Equals:
            case BinaryOperatorKind.NotEquals:
                // 'x != x' is the classic NaN test, also on float? and double?.
                if (IsFloatingPoint(binary.LeftOperand.Type))
                {
                    return false;
                }

                break;

            case BinaryOperatorKind.LessThan:
            case BinaryOperatorKind.LessThanOrEqual:
            case BinaryOperatorKind.GreaterThan:
            case BinaryOperatorKind.GreaterThanOrEqual:
            case BinaryOperatorKind.Subtract:
            case BinaryOperatorKind.Divide:
            case BinaryOperatorKind.Remainder:
                break;

            default:
                return false;
        }

        // Tests legitimately check reflexivity of user-defined operators, for example 'Assert.True(money == money)'.
        return binary.OperatorMethod is null || Framework.IsFrameworkValue(binary.OperatorMethod.ContainingType);
    }

    private static bool IsFloatingPoint(ITypeSymbol? type) => type switch
    {
        INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable => IsFloatingPoint(nullable.TypeArguments[0]),
        _ => type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double,
    };

    // In C# every IBinaryOperation is bound from a BinaryExpressionSyntax; tuple equality and patterns have operations of their own.
    private static string OperatorText(IBinaryOperation binary) => ((BinaryExpressionSyntax)binary.Syntax).OperatorToken.Text;

    private static void Report(OperationAnalysisContext context, IOperation duplicate, IOperation original, string operatorText) =>
        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.IdenticalSubExpressions,
            duplicate.Syntax.GetLocation(),
            additionalLocations: [original.Syntax.GetLocation()],
            messageArgs: [duplicate.Syntax.ToString(), original.Syntax.ToString(), operatorText]));
}
