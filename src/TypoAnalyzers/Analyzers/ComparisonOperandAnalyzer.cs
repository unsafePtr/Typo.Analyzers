using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using TypoAnalyzers.Core;

namespace TypoAnalyzers.Analyzers;

/// <summary>
/// TYPO0013: a comparison of a variable with a sum, difference or product that contains the same variable, as in <c>x == x + 1</c> or <c>x &lt; x * y</c>.
/// </summary>
/// <remarks>
/// With <c>==</c> and <c>!=</c> the result is fixed unless the other terms are zero, and with an ordering the shared term cancels out.
/// A constant shared term, as in <c>2 &lt; 2 * y</c>, and string concatenation are left alone.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ComparisonOperandAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.RepeatedOperandAcrossComparison];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            var purity = PurityOracle.For(start.Compilation);
            start.RegisterOperationAction(context => Analyze(context, purity), OperationKind.Binary);
        });
    }

    private static void Analyze(OperationAnalysisContext context, PurityOracle purity)
    {
        var comparison = (IBinaryOperation)context.Operation;
        if (comparison.OperatorMethod is not null
            || comparison.OperatorKind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals or BinaryOperatorKind.LessThan
                or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual))
        {
            return;
        }

        if (!TryReport(context, comparison.LeftOperand, comparison.RightOperand, purity))
        {
            TryReport(context, comparison.RightOperand, comparison.LeftOperand, purity);
        }
    }

    private static bool TryReport(OperationAnalysisContext context, IOperation single, IOperation compound, PurityOracle purity)
    {
        single = Skip(single);
        compound = Skip(compound);
        if (compound is not IBinaryOperation { OperatorMethod: null, OperatorKind: BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply } root
            || !IsNumeric(root.Type)
            || single.ConstantValue.HasValue
            || !purity.IsFree(single))
        {
            return false;
        }

        var terms = OperandChain.Rent();
        try
        {
            Flatten(root, root.OperatorKind, terms);
            foreach (var term in terms)
            {
                if (OperationEquivalence.AreEquivalent(single, term))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.RepeatedOperandAcrossComparison,
                        term.Syntax.GetLocation(),
                        additionalLocations: [compound.Syntax.GetLocation()],
                        messageArgs: [term.Syntax.ToString(), compound.Syntax.ToString()]));
                    return true;
                }
            }

            return false;
        }
        finally
        {
            OperandChain.Return(terms);
        }
    }

    // Sums and differences share one chain because 'x - y + x' still contains x, while a product only chains with products.
    private static void Flatten(IOperation operation, BinaryOperatorKind kind, List<IOperation> terms)
    {
        operation = Skip(operation);
        if (operation is IBinaryOperation { OperatorMethod: null } binary && IsSameFamily(binary.OperatorKind, kind) && IsNumeric(binary.Type))
        {
            Flatten(binary.LeftOperand, kind, terms);
            Flatten(binary.RightOperand, kind, terms);
        }
        else
        {
            terms.Add(operation);
        }
    }

    private static bool IsSameFamily(BinaryOperatorKind candidate, BinaryOperatorKind kind) =>
        kind == BinaryOperatorKind.Multiply
            ? candidate == BinaryOperatorKind.Multiply
            : candidate is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract;

    private static bool IsNumeric(ITypeSymbol? type) => type?.SpecialType is >= SpecialType.System_SByte and <= SpecialType.System_Double;

    private static IOperation Skip(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }
}
