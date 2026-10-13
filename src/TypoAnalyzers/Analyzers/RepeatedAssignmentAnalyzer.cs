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
/// A property is tracked when it is get-only or has an auto-implemented setter declared in source. An indexer is tracked only on the listed collections.
/// Hand-written setters and setters from referenced assemblies may have side effects that make a second write meaningful.
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
            var purity = PurityOracle.For(start.Compilation);
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
        var constructor = context.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } method ? method : null;
        for (var i = 0; i < operations.Length - 1; i++)
        {
            if (AsAssignment(operations[i]) is not { } first
                || !IsTracked(first.Target, purity, collections)
                || ReadSet.RootSymbol(first.Target) is not { } symbol)
            {
                continue;
            }

            var writingConstructor = first.Target is IMemberReferenceOperation { Instance: IInstanceReferenceOperation } ? constructor : null;
            for (var k = i + 1; k < operations.Length; k++)
            {
                if (AsAssignment(operations[k]) is not { } next)
                {
                    break;
                }

                if (OperationEquivalence.AreEquivalent(first.Target, next.Target))
                {
                    if (!References(next.Value, symbol, writingConstructor))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Descriptors.OverwrittenBeforeUse,
                            next.Target.Syntax.GetLocation(),
                            additionalLocations: [first.Target.Syntax.GetLocation()],
                            messageArgs: [next.Target.Syntax.ToString()]));
                    }

                    break;
                }

                if (References(next, symbol, writingConstructor) || CallsImpure(next, purity))
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

    // A constructor assigning a get-only property writes its backing field and runs no code, unless the getter returns a ref.
    private static bool IsAutoProperty(IPropertySymbol property)
    {
        if (property.SetMethod is not { } setter)
        {
            return !property.ReturnsByRef;
        }

        if (setter.DeclaringSyntaxReferences.IsEmpty)
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

    // 'constructor' is set when the tracked write goes to this object inside a constructor; see ReadsOtherObject.
    private static bool References(IOperation operation, ISymbol symbol, IMethodSymbol? constructor)
    {
        foreach (var descendant in operation.DescendantsAndSelf())
        {
            if (SymbolEqualityComparer.Default.Equals(ReadSet.RootSymbol(descendant), symbol) && !ReadsOtherObject(descendant, constructor))
            {
                return true;
            }
        }

        return false;
    }

    // A constructor's by-value parameters are bound before the object exists, so in a copy constructor 'other._x' never reads the '_x' being written.
    private static bool ReadsOtherObject(IOperation reference, IMethodSymbol? constructor) =>
        constructor is not null
        && reference is IMemberReferenceOperation { Instance: { } instance }
        && SkipConversions(instance) is IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } parameter }
        && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, constructor);

    private static bool CallsImpure(IOperation operation, PurityOracle purity)
    {
        foreach (var descendant in operation.DescendantsAndSelf())
        {
            if (descendant is IInvocationOperation invocation && !purity.IsPure(invocation.TargetMethod) && !FillsNewCollection(invocation))
            {
                return true;
            }
        }

        return false;
    }

    // The Add calls of a collection initializer only fill the new framework collection, so they cannot touch the tracked target.
    private static bool FillsNewCollection(IInvocationOperation invocation) =>
        invocation.Instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver }
        && Framework.IsFrameworkValue(invocation.TargetMethod.ContainingType);

    private static IOperation SkipConversions(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }
}
