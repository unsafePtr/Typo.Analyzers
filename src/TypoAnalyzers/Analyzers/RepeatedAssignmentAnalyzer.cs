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
/// Run between the two writes, such a setter may also read the target, as the setters do around a guard flag in <c>_loading = true; Items = items; _loading = false;</c>, so it ends the run like an impure call.
/// Only a setter in the same file that is provably inert does not end it.
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
                            messageArgs: [Quote.Of(next.Target.Syntax)]));
                    }

                    break;
                }

                if (References(next, symbol, writingConstructor) || CallsImpure(next, symbol, purity, collections, context.CancellationToken))
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

    private static bool CallsImpure(IOperation operation, ISymbol target, PurityOracle purity, HashSet<INamedTypeSymbol> collections, CancellationToken cancellationToken)
    {
        foreach (var descendant in operation.DescendantsAndSelf())
        {
            switch (descendant)
            {
                case IInvocationOperation invocation when !purity.IsPure(invocation.TargetMethod) && !FillsNewCollection(invocation):
                case IAssignmentOperation { Target: IPropertyReferenceOperation assigned }
                    when RunsSetter(assigned, collections) && !IsInert(assigned, target, purity, collections, cancellationToken):
                case IIncrementOrDecrementOperation { Target: IPropertyReferenceOperation incremented }
                    when RunsSetter(incremented, collections) && !IsInert(incremented, target, purity, collections, cancellationToken):
                    return true;
            }
        }

        return false;
    }

    // A setter declared in this file is inert when it never names the target, calls nothing impure and runs no other hand-written setter, as 'set => _size = Math.Max(0, value);'.
    // A setter that raises an event is not, because the handler may read a guard flag, and one from another file or assembly cannot be checked here.
    private static bool IsInert(IPropertyReferenceOperation property, ISymbol target, PurityOracle purity, HashSet<INamedTypeSymbol> collections, CancellationToken cancellationToken)
    {
        if (property.Property.SetMethod is not { DeclaringSyntaxReferences.IsEmpty: false } setter || property.SemanticModel is not { } model)
        {
            return false;
        }

        foreach (var reference in setter.DeclaringSyntaxReferences)
        {
            if (reference.SyntaxTree != model.SyntaxTree || model.GetOperation(reference.GetSyntax(cancellationToken), cancellationToken) is not { } body)
            {
                return false;
            }

            foreach (var descendant in body.DescendantsAndSelf())
            {
                switch (descendant)
                {
                    case IInvocationOperation invocation when !purity.IsPure(invocation.TargetMethod):
                    case IAssignmentOperation { Target: IPropertyReferenceOperation assigned } when RunsSetter(assigned, collections):
                    case IIncrementOrDecrementOperation { Target: IPropertyReferenceOperation incremented } when RunsSetter(incremented, collections):
                        return false;
                }

                if (SymbolEqualityComparer.Default.Equals(ReadSet.RootSymbol(descendant), target))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // A member of an object initializer sets the new object, which code outside cannot see yet.
    private static bool RunsSetter(IPropertyReferenceOperation property, HashSet<INamedTypeSymbol> collections) =>
        property.Instance is not IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver }
        && (property.Property.IsIndexer
            ? !collections.Contains(property.Property.ContainingType.OriginalDefinition)
            : !IsAutoProperty(property.Property));

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
