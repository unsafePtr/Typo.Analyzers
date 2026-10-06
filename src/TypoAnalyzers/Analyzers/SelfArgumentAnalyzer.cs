using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using TypoAnalyzers.Core;

namespace TypoAnalyzers.Analyzers;

/// <summary>
/// TYPO0002: <c>x.Equals(x)</c>, <c>x.CompareTo(x)</c>, <c>Equals(a, a)</c>, <c>comparer.Compare(a, a)</c>, <c>Math.Max(a, a)</c> and similar.
/// </summary>
/// <remarks>
/// Values of user-defined types are skipped because tests legitimately assert reflexivity, as in <c>Assert.True(money.Equals(money))</c>.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SelfArgumentAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.ComparedWithItself];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            var purity = new PurityOracle(start.Compilation);
            start.RegisterOperationAction(context => Analyze(context, purity), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, PurityOracle purity)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (!IsComparison(method) || !TryGetOperands(invocation, out var first, out var second))
        {
            return;
        }

        if (!Framework.IsFrameworkValue(first.Type) || !purity.IsFree(first) || !OperationEquivalence.AreEquivalent(first, second))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.ComparedWithItself,
            second.Syntax.GetLocation(),
            additionalLocations: [first.Syntax.GetLocation()],
            messageArgs: [second.Syntax.ToString(), method.Name]));
    }

    private static bool IsComparison(IMethodSymbol method) => method.Name switch
    {
        "Equals" or "ReferenceEquals" or "Compare" or "CompareTo" or "CompareOrdinal" or "SequenceEqual" => true,
        "Max" or "Min" => method.ContainingType.Name is "Math" or "MathF" && method.ContainingNamespace.Name == "System",
        _ => false,
    };

    private static bool TryGetOperands(IInvocationOperation invocation, out IOperation first, out IOperation second)
    {
        var parameters = invocation.TargetMethod.Parameters;
        if (parameters.Length >= 2 && SymbolEqualityComparer.Default.Equals(parameters[0].Type, parameters[1].Type))
        {
            first = ArgumentFor(invocation, 0);
            second = ArgumentFor(invocation, 1);
            return true;
        }

        if (invocation.Instance is { IsImplicit: false } instance && parameters.Length >= 1)
        {
            first = instance;
            second = ArgumentFor(invocation, 0);
            return true;
        }

        first = second = null!;
        return false;
    }

    // A bound invocation carries one argument per parameter, implicit defaults included, so the lookup always succeeds.
    private static IOperation ArgumentFor(IInvocationOperation invocation, int ordinal)
    {
        var value = invocation.Arguments.First(argument => argument.Parameter?.Ordinal == ordinal).Value;
        return value is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : value;
    }
}
