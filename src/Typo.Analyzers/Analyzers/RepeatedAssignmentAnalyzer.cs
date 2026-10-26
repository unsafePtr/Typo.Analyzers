using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Typo.Core;

namespace Typo.Analyzers;

/// <summary>
/// TYPO0008: the same field, auto-property, array element or collection entry assigned twice in a row with no read in between.
/// </summary>
/// <remarks>
/// Locals and parameters are left to IDE0059.
/// A property is tracked when it is get-only or has an auto-implemented setter declared in source. An indexer is tracked only on the listed collections.
/// Hand-written setters and setters from referenced assemblies may have side effects that make a second write meaningful.
/// Run between the two writes, such a setter may also read the target, as the setters do around a guard flag in <c>_loading = true; Items = items; _loading = false;</c>, so it ends the run like an impure call.
/// Only a setter in the same file that is provably inert does not end it.
/// Only a run of plain assignment statements, null-conditional ones included, is scanned; any other statement ends it.
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
                || !ReceiversFree(operations[i], purity)
                || ReadSet.RootSymbol(first.Target) is not { } symbol
                || (i > 0 && ResetsAfterDispose(operations[i - 1], first)))
            {
                continue;
            }

            var writingConstructor = first.Target is IMemberReferenceOperation { Instance: IInstanceReferenceOperation } ? constructor : null;
            var location = LocationOf(operations[i], first.Target);
            for (var k = i + 1; k < operations.Length; k++)
            {
                if (AsAssignment(operations[k]) is not { } next)
                {
                    break;
                }

                if (SameTarget(operations[i], operations[k]))
                {
                    if (!References(next.Value, symbol, writingConstructor) && !CallsSourceReader(next.Value, symbol))
                    {
                        var overwrite = TargetOf(operations[k], next);
                        context.ReportDiagnostic(Diagnostic.Create(
                            Descriptors.OverwrittenBeforeUse,
                            overwrite,
                            additionalLocations: [TargetOf(operations[i], first)],
                            messageArgs: [Quote.Of(operations[k].Syntax, overwrite.SourceSpan)]));
                    }

                    break;
                }

                if (References(operations[k], symbol, writingConstructor)
                    || CallsImpure(operations[k], symbol, purity, collections, context.CancellationToken)
                    || Moves(location, operations[k], purity))
                {
                    break;
                }
            }
        }
    }

    // In 'x?.Dispose(); x = null; x = new Foo();' the null is what stays if the constructor throws, so the reset is observable.
    private static bool ResetsAfterDispose(IOperation previous, ISimpleAssignmentOperation first)
    {
        if (first.Value.ConstantValue is not { HasValue: true, Value: null } || previous is not IExpressionStatementOperation { Operation: var call })
        {
            return false;
        }

        var receiver = call switch
        {
            IInvocationOperation { TargetMethod.Name: "Dispose", Arguments.Length: 0, Instance: { } instance } => instance,
            IConditionalAccessOperation { WhenNotNull: IInvocationOperation { TargetMethod.Name: "Dispose", Arguments.Length: 0 } } access => access.Operation,
            _ => null,
        };
        return receiver is not null && OperationEquivalence.AreEquivalent(SkipConversions(receiver), first.Target);
    }

    // The second value can read the first one through a method declared in source, as a cached 'GetHashCode' that returns '_hashCode' unless it is -1.
    // A method counts when it is called on this object or statically, belongs to the target's type and names the target without another receiver.
    // So 'Name = Format(person)' still reports when 'Format' reads 'person.Name', and a method from a referenced assembly has no source here and never counts.
    private static bool CallsSourceReader(IOperation value, ISymbol target)
    {
        foreach (var descendant in value.DescendantsAndSelf())
        {
            var (method, instance) = descendant switch
            {
                IInvocationOperation invocation => (invocation.TargetMethod, invocation.Instance),
                IPropertyReferenceOperation property => (property.Property.GetMethod, property.Instance),
                _ => (null, null),
            };
            if (method is not null
                && instance is null or IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }
                && DerivesFrom(method.ContainingType, target.ContainingType)
                && NamesTarget(method, target.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DerivesFrom(INamedTypeSymbol? type, INamedTypeSymbol? baseType)
    {
        for (; type is not null && baseType is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, baseType.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NamesTarget(IMethodSymbol method, string name)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            foreach (var token in reference.GetSyntax().DescendantTokens())
            {
                if (token.ValueText == name && token.Parent is IdentifierNameSyntax identifier && !NamesOtherObjectsMember(identifier))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // 'p.Name' and 'p?.Name' name a member of another object, while 'Name', 'this.Name' and 'base.Name' name this object's.
    private static bool NamesOtherObjectsMember(IdentifierNameSyntax identifier) => identifier.Parent switch
    {
        MemberAccessExpressionSyntax access when access.Name == identifier => access.Expression is not (ThisExpressionSyntax or BaseExpressionSyntax),
        MemberBindingExpressionSyntax => true,
        _ => false,
    };

    // The receiver and the indexes say where the target is, as 'p' and 'i' do in 'p.Items[i]'.
    // Once a statement writes one of them, as 'p = p.Next' does, the next write goes to other storage and the first one is not dead.
    // In 'p?.Items[i] = 1' the target holds a placeholder for 'p', so the receivers come from the null-conditional accesses.
    private static ReadSet[] LocationOf(IOperation statement, IOperation target)
    {
        var receivers = new List<ReadSet>();
        for (var operation = ((IExpressionStatementOperation)statement).Operation; operation is IConditionalAccessOperation access; operation = access.WhenNotNull)
        {
            receivers.Add(ReadSet.Of(access.Operation));
        }

        return [.. receivers, .. LocationOf(target)];
    }

    private static ReadSet[] LocationOf(IOperation target) => target switch
    {
        IArrayElementReferenceOperation element => [ReadSet.Of(element.ArrayReference), .. element.Indices.Select(ReadSet.Of)],
        IPropertyReferenceOperation property => [.. Operands(property.Instance), .. property.Arguments.Select(argument => ReadSet.Of(argument.Value))],
        IMemberReferenceOperation member => Operands(member.Instance),
        _ => [],
    };

    private static ReadSet[] Operands(IOperation? instance) => instance is null or IInstanceReferenceOperation ? [] : [ReadSet.Of(instance)];

    private static bool Moves(ReadSet[] location, IOperation statement, PurityOracle purity)
    {
        foreach (var operand in location)
        {
            if (operand.IsWrittenBy(statement, purity))
            {
                return true;
            }
        }

        return false;
    }

    // 'a?.X = 1' and 'a?.B?.X = 1' wrap the assignment in one null-conditional access per '?'.
    // The assignment sits at the end of the WhenNotNull chain.
    private static ISimpleAssignmentOperation? AsAssignment(IOperation statement)
    {
        if (statement is not IExpressionStatementOperation { Operation: var operation })
        {
            return null;
        }

        while (operation is IConditionalAccessOperation access)
        {
            operation = access.WhenNotNull;
        }

        return operation is ISimpleAssignmentOperation { IsRef: false } assignment ? assignment : null;
    }

    private static bool ReceiversFree(IOperation statement, PurityOracle purity)
    {
        for (var operation = ((IExpressionStatementOperation)statement).Operation; operation is IConditionalAccessOperation access; operation = access.WhenNotNull)
        {
            if (!purity.IsFree(access.Operation))
            {
                return false;
            }
        }

        return true;
    }

    // Within a null-conditional access the target only holds a placeholder for the receiver.
    // The receivers are therefore compared level by level.
    private static bool SameTarget(IOperation first, IOperation next)
    {
        var x = ((IExpressionStatementOperation)first).Operation;
        var y = ((IExpressionStatementOperation)next).Operation;
        while (x is IConditionalAccessOperation xAccess && y is IConditionalAccessOperation yAccess)
        {
            if (!OperationEquivalence.AreEquivalent(xAccess.Operation, yAccess.Operation))
            {
                return false;
            }

            x = xAccess.WhenNotNull;
            y = yAccess.WhenNotNull;
        }

        return x is ISimpleAssignmentOperation xAssignment
            && y is ISimpleAssignmentOperation yAssignment
            && OperationEquivalence.AreEquivalent(xAssignment.Target, yAssignment.Target);
    }

    // The target of 'a?.X = 1' is only '.X' in the syntax tree, so the reported span starts at the receiver.
    private static Location TargetOf(IOperation statement, ISimpleAssignmentOperation assignment)
    {
        var write = ((IExpressionStatementOperation)statement).Operation.Syntax;
        return Location.Create(write.SyntaxTree, TextSpan.FromBounds(write.SpanStart, assignment.Target.Syntax.Span.End));
    }

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
            if (PurityOracle.CalledMethod(descendant) is { } method && !purity.IsPure(method) && !FillsNewCollection(descendant))
            {
                return true;
            }

            switch (descendant)
            {
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
                if (PurityOracle.CalledMethod(descendant) is { } method && !purity.IsPure(method))
                {
                    return false;
                }

                switch (descendant)
                {
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
    private static bool FillsNewCollection(IOperation call) =>
        call is IInvocationOperation { Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver } } invocation
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
