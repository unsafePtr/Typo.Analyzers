using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using TypoAnalyzers.Core;

namespace TypoAnalyzers.Analyzers;

/// <summary>
/// TYPO0008: the same field, auto-property, array element or collection entry assigned twice in a row with no read in between.
/// </summary>
/// <remarks>
/// Locals and parameters are left to IDE0059.
/// Properties with hand-written setters and properties from referenced assemblies are skipped because their setters may have side effects that make a second write meaningful.
/// Only a run of plain assignment statements is scanned; any other statement ends it.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RepeatedAssignmentAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] s_collectionTypeNames =
    [
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.SortedDictionary`2",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IDictionary`2",
    ];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.OverwrittenBeforeUse];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            var purity = new PurityOracle(start.Compilation);
            var collections = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var name in s_collectionTypeNames)
            {
                if (start.Compilation.GetTypeByMetadataName(name) is { } type)
                {
                    collections.Add(type);
                }
            }

            start.RegisterOperationAction(context => AnalyzeBlock(context, purity, collections), OperationKind.Block);
        });
    }

    private static void AnalyzeBlock(OperationAnalysisContext context, PurityOracle purity, HashSet<INamedTypeSymbol> collections)
    {
        var operations = ((IBlockOperation)context.Operation).Operations;
        for (var i = 0; i < operations.Length - 1; i++)
        {
            if (AsAssignment(operations[i]) is not { } first
                || !IsTracked(first.Target, purity, collections)
                || ReadSet.RootSymbol(first.Target) is not { } symbol)
            {
                continue;
            }

            for (var k = i + 1; k < operations.Length; k++)
            {
                if (AsAssignment(operations[k]) is not { } next)
                {
                    break;
                }

                if (OperationEquivalence.AreEquivalent(first.Target, next.Target))
                {
                    if (!References(next.Value, symbol))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Descriptors.OverwrittenBeforeUse,
                            next.Target.Syntax.GetLocation(),
                            additionalLocations: [first.Target.Syntax.GetLocation()],
                            messageArgs: [next.Target.Syntax.ToString()]));
                    }

                    break;
                }

                if (References(next, symbol) || CallsImpure(next, purity))
                {
                    break;
                }
            }
        }
    }

    private static ISimpleAssignmentOperation? AsAssignment(IOperation statement) =>
        statement is IExpressionStatementOperation { Operation: ISimpleAssignmentOperation { IsRef: false } assignment } ? assignment : null;

    private static bool IsTracked(IOperation target, PurityOracle purity, HashSet<INamedTypeSymbol> collections) => target switch
    {
        IFieldReferenceOperation field => !field.Field.IsVolatile && purity.IsFree(field.Instance),
        IArrayElementReferenceOperation element => purity.IsFree(element),
        IPropertyReferenceOperation { Property.IsIndexer: true } indexer =>
            collections.Contains(indexer.Property.ContainingType.OriginalDefinition) && purity.IsFree(indexer),
        IPropertyReferenceOperation property => IsAutoProperty(property.Property) && purity.IsFree(property.Instance),
        _ => false,
    };

    private static bool IsAutoProperty(IPropertySymbol property)
    {
        if (property.SetMethod is not { } setter || setter.DeclaringSyntaxReferences.IsEmpty)
        {
            return false;
        }

        foreach (var reference in setter.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not AccessorDeclarationSyntax { Body: null, ExpressionBody: null })
            {
                return false;
            }
        }

        return true;
    }

    private static bool References(IOperation operation, ISymbol symbol)
    {
        foreach (var descendant in operation.DescendantsAndSelf())
        {
            if (SymbolEqualityComparer.Default.Equals(ReadSet.RootSymbol(descendant), symbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CallsImpure(IOperation operation, PurityOracle purity)
    {
        foreach (var descendant in operation.DescendantsAndSelf())
        {
            if (descendant is IInvocationOperation invocation && !purity.IsPure(invocation.TargetMethod))
            {
                return true;
            }
        }

        return false;
    }
}
