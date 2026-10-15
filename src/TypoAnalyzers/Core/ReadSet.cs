using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace TypoAnalyzers.Core;

/// <summary>
/// Answers "can this code change what that expression reads?" for callers that skip a report when it can.
/// </summary>
/// <remarks>
/// <see cref="MayBeChangedBy"/> answers conservatively, so its callers only report when the answer is a confident no.
/// <see cref="IsWrittenBy"/> needs evidence of a write, so its callers still report across an unknown call.
/// Writes are matched by symbol regardless of instance, so <c>other.X = 1</c> counts as changing <c>this.X</c>.
/// Passing a <c>Span&lt;T&gt;</c> or <c>Memory&lt;T&gt;</c> argument writes the array or span it views, as in <c>value.TryFormat(buffer, out _)</c>.
/// In <see cref="MayBeChangedBy"/>, a call to a method not known to be pure counts as a change when the expression reads state.
/// State is a mutable field, a property, an array element or the result of a call.
/// A member of string or a primitive, such as <c>s.Length</c> or <c>string.IsNullOrEmpty(s)</c>, is not state of its own.
/// The same call also counts when a lambda or local function in the method writes a local or parameter the expression reads.
/// An await counts as a change when the expression reads state, and a dynamic call always does.
/// </remarks>
internal readonly struct ReadSet
{
    private readonly HashSet<ISymbol> _symbols;
    private readonly bool _readsThis;

    private ReadSet(HashSet<ISymbol> symbols, bool readsState, bool readsThis)
    {
        _symbols = symbols;
        _readsThis = readsThis;
        ReadsState = readsState;
    }

    public bool ReadsState { get; }

    public static ReadSet Of(IOperation expression)
    {
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var readsState = false;
        var readsThis = false;
        foreach (var operation in expression.DescendantsAndSelf())
        {
            switch (operation)
            {
                case ILocalReferenceOperation local:
                    symbols.Add(local.Local);
                    break;

                case IParameterReferenceOperation parameter:
                    symbols.Add(parameter.Parameter);
                    break;

                case IFieldReferenceOperation field:
                    symbols.Add(field.Field);
                    readsState |= !field.Field.IsReadOnly && !field.Field.IsConst;
                    readsThis |= field.Instance is IInstanceReferenceOperation;
                    break;

                case IPropertyReferenceOperation property:
                    symbols.Add(property.Property);
                    readsState |= !IsImmutableMember(property.Property);
                    readsThis |= property.Instance is IInstanceReferenceOperation;
                    break;

                case IInvocationOperation invocation:
                    readsState |= !IsImmutableMember(invocation.TargetMethod);
                    break;

                case IArrayElementReferenceOperation:
                    readsState = true;
                    break;
            }
        }

        return new ReadSet(symbols, readsState, readsThis);
    }

    // A member of string or a primitive reads only its receiver and arguments, which the descendants already account for.
    // CopyTo writes an array, and the array argument is tracked as a write through the argument.
    private static bool IsImmutableMember(ISymbol member) =>
        member.ContainingType.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_String
        && member.Name != "CopyTo";

    public bool Contains(ISymbol? symbol) => symbol is not null && _symbols.Contains(symbol);

    /// <summary>
    /// Answers whether the region shows evidence of writing something the expression reads.
    /// Unlike <see cref="MayBeChangedBy"/>, it does not count a call merely because the method is not known to be pure.
    /// </summary>
    /// <remarks>
    /// On a struct, a method that is neither readonly nor known to be pure counts, as <c>Utf8JsonReader.Read</c> moves the reader.
    /// A readonly variable such as an <c>in</c> parameter is copied before such a call, which leaves the variable unchanged.
    /// On a class, an interface or a type parameter, only a method that <see cref="PurityOracle.AdvancesInstance"/> accepts counts.
    /// </remarks>
    public bool IsWrittenBy(IOperation region, PurityOracle purity)
    {
        foreach (var operation in region.DescendantsAndSelf())
        {
            if (Writes(operation, purity) || MutatesReadInstance(operation, purity))
            {
                return true;
            }
        }

        return false;
    }

    public bool MayBeChangedBy(IOperation region, PurityOracle purity, TextSpan? skip = null)
    {
        bool? writtenByClosure = null;
        foreach (var operation in region.DescendantsAndSelf())
        {
            if (skip is { } span && span.Contains(operation.Syntax.Span))
            {
                continue;
            }

            if (Writes(operation, purity))
            {
                return true;
            }

            switch (operation)
            {
                case IInvocationOperation invocation when !purity.IsPure(invocation.TargetMethod):
                    if (ReadsState || (writtenByClosure ??= IsWrittenByClosure(region, purity)))
                    {
                        return true;
                    }

                    break;

                case IAwaitOperation when ReadsState:
                case IDynamicInvocationOperation:
                    return true;
            }
        }

        return false;
    }

    public static ISymbol? RootSymbol(IOperation? operation) => operation switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        IArrayElementReferenceOperation element => RootSymbol(element.ArrayReference),
        IConversionOperation conversion => RootSymbol(conversion.Operand),
        _ => null,
    };

    private bool Writes(IOperation operation, PurityOracle purity)
    {
        switch (operation)
        {
            case IDeconstructionAssignmentOperation deconstruction:
                foreach (var target in deconstruction.Target.DescendantsAndSelf())
                {
                    if (Contains(RootSymbol(target)))
                    {
                        return true;
                    }
                }

                return false;

            case IAssignmentOperation assignment:
                return Contains(RootSymbol(assignment.Target));

            case IIncrementOrDecrementOperation increment:
                return Contains(RootSymbol(increment.Target));

            case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } argument:
                return Contains(RootSymbol(argument.Value));

            case IArgumentOperation argument when purity.WritesThrough(argument):
                return Contains(ViewedSymbol(argument.Value));

            default:
                return false;
        }
    }

    // A span made with AsSpan, AsMemory or Slice views the same memory as its source.
    private static ISymbol? ViewedSymbol(IOperation operation) => operation switch
    {
        IConversionOperation conversion => ViewedSymbol(conversion.Operand),
        IInvocationOperation { TargetMethod: { IsStatic: true, Name: "AsSpan" or "AsMemory" }, Arguments: [var source, ..] } => ViewedSymbol(source.Value),
        IInvocationOperation { TargetMethod.Name: "Slice", Instance: { } instance } => ViewedSymbol(instance),
        _ => RootSymbol(operation),
    };

    private bool MutatesReadInstance(IOperation operation, PurityOracle purity) =>
        operation is IInvocationOperation { TargetMethod: { IsStatic: false } method, Instance: { Type: { } type } instance }
        && (type.IsValueType
            ? !method.IsReadOnly && !purity.IsPure(method) && !IsReadOnlyVariable(instance)
            : purity.AdvancesInstance(method))
        && (instance is IInstanceReferenceOperation ? _readsThis : Contains(RootSymbol(instance)));

    private static bool IsReadOnlyVariable(IOperation instance) => instance switch
    {
        IParameterReferenceOperation { Parameter.RefKind: RefKind.In or RefKind.RefReadOnlyParameter } => true,
        IFieldReferenceOperation { Field.IsReadOnly: true } => true,
        ILocalReferenceOperation { Local.RefKind: RefKind.RefReadOnly } => true,
        _ => false,
    };

    private bool IsWrittenByClosure(IOperation anywhereInMethod, PurityOracle purity)
    {
        var root = anywhereInMethod;
        while (root.Parent is not null)
        {
            root = root.Parent;
        }

        foreach (var operation in root.DescendantsAndSelf())
        {
            if (operation is not (IAnonymousFunctionOperation or ILocalFunctionOperation))
            {
                continue;
            }

            foreach (var inner in operation.Descendants())
            {
                if (Writes(inner, purity))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
