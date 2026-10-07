using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using TypoAnalyzers.Core;

namespace TypoAnalyzers.Analyzers;

/// <summary>
/// TYPO0009: a for loop over a collection whose body reads <c>items[0]</c> and uses the loop variable, if at all, only to index other collections.
/// </summary>
/// <remarks>
/// The collection comes from the bound (<c>i &lt; items.Length</c>, <c>i &lt;= items.Count - 1</c>) or, for reverse loops such as <c>for (var i = items.Length - 1; i &gt;= 0; i--)</c>, from the initializer.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LoopIndexAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.ConstantIndexInLoop];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            var purity = new PurityOracle(start.Compilation);
            start.RegisterOperationAction(context => Analyze(context, purity), OperationKind.Loop);
        });
    }

    private static void Analyze(OperationAnalysisContext context, PurityOracle purity)
    {
        if (context.Operation is not IForLoopOperation { Locals.Length: 1 } loop
            || loop.Condition is not IBinaryOperation comparison
            || LoopedCollection(loop, comparison, loop.Locals[0]) is not { } collection
            || !purity.IsFree(collection))
        {
            return;
        }

        var index = loop.Locals[0];
        IOperation? constantAccess = null;
        foreach (var operation in loop.Body.DescendantsAndSelf())
        {
            switch (operation)
            {
                case ILocalReferenceOperation reference when SymbolEqualityComparer.Default.Equals(reference.Local, index):
                    if (!IndexesOtherCollection(reference, collection))
                    {
                        return;
                    }

                    break;

                case IArrayElementReferenceOperation { Indices.Length: 1 } element
                    when constantAccess is null
                        && element.Indices[0].ConstantValue.HasValue
                        && !IsBaseReference(element)
                        && OperationEquivalence.AreEquivalent(element.ArrayReference, collection):
                    constantAccess = element;
                    break;

                case IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1 } indexer
                    when constantAccess is null
                        && indexer.Arguments[0].Value.ConstantValue.HasValue
                        && !IsBaseReference(indexer)
                        && OperationEquivalence.AreEquivalent(indexer.Instance, collection):
                    constantAccess = indexer;
                    break;
            }
        }

        if (constantAccess is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.ConstantIndexInLoop,
                constantAccess.Syntax.GetLocation(),
                messageArgs: [collection.Syntax.ToString(), index.Name, constantAccess.Syntax.ToString()]));
        }
    }

    // Walking another collection in step, as in 'a[0].Equals(b[i])', leaves the looped one stuck at a constant; any other use, such as 'i == 0', may be what makes the constant right.
    private static bool IndexesOtherCollection(ILocalReferenceOperation reference, IOperation collection) => reference.Parent switch
    {
        IArrayElementReferenceOperation element => !OperationEquivalence.AreEquivalent(element.ArrayReference, collection),
        IArgumentOperation { Parent: IPropertyReferenceOperation { Property.IsIndexer: true } indexer } => !OperationEquivalence.AreEquivalent(indexer.Instance, collection),
        _ => false,
    };

    // 'ref items[0]' or '&items[0]' is the start of the buffer, as in 'Unsafe.Add(ref items[0], offset)', not a read of its first element.
    private static bool IsBaseReference(IOperation access) => access.Parent switch
    {
        IArgumentOperation argument => argument.Parameter?.RefKind is RefKind.Ref or RefKind.RefReadOnlyParameter
            || (argument.Parameter?.RefKind == RefKind.In && argument.Syntax is ArgumentSyntax { RefKindKeyword.RawKind: not 0 }),
        IAddressOfOperation => true,
        IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } } => true,
        _ => false,
    };

    private static IOperation? LoopedCollection(IForLoopOperation loop, IBinaryOperation comparison, ILocalSymbol index)
    {
        switch (comparison.OperatorKind)
        {
            case BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual when IsLocal(comparison.LeftOperand, index):
                return CollectionOfBound(comparison.RightOperand);

            case BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual when IsLocal(comparison.RightOperand, index):
                return CollectionOfBound(comparison.LeftOperand);

            case BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual when IsLocal(comparison.LeftOperand, index):
                return CollectionOfBound(InitialValue(loop, index));

            default:
                return null;
        }
    }

    // Accepts items.Length, items.Count or items.LongLength, optionally minus a constant.
    private static IOperation? CollectionOfBound(IOperation? bound)
    {
        bound = SkipImplicitConversions(bound);
        if (bound is IBinaryOperation { OperatorKind: BinaryOperatorKind.Subtract, RightOperand.ConstantValue.HasValue: true } subtraction)
        {
            bound = SkipImplicitConversions(subtraction.LeftOperand);
        }

        return bound is IPropertyReferenceOperation { Property.Name: "Length" or "Count" or "LongLength", Instance: { } collection } ? collection : null;
    }

    private static IOperation? InitialValue(IForLoopOperation loop, ILocalSymbol index)
    {
        foreach (var statement in loop.Before)
        {
            foreach (var operation in statement.DescendantsAndSelf())
            {
                if (operation is IVariableDeclaratorOperation { Initializer.Value: { } value } declarator
                    && SymbolEqualityComparer.Default.Equals(declarator.Symbol, index))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static bool IsLocal(IOperation operation, ILocalSymbol local) =>
        SkipImplicitConversions(operation) is ILocalReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Local, local);

    private static IOperation? SkipImplicitConversions(IOperation? operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }
}
