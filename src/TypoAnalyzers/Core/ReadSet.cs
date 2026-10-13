using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace TypoAnalyzers.Core;

/// <summary>
/// Answers "can this code change what that expression reads?" conservatively, so callers only report when the answer is a confident no.
/// </summary>
/// <remarks>
/// Writes are matched by symbol regardless of instance, so <c>other.X = 1</c> counts as changing <c>this.X</c>.
/// A call to a method not known to be pure counts as a change when the expression reads fields, properties or array elements, or when a lambda or local function in the same method writes one of the locals it reads.
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
                    readsState = true;
                    readsThis |= property.Instance is IInstanceReferenceOperation;
                    break;

                case IArrayElementReferenceOperation:
                case IInvocationOperation:
                    readsState = true;
                    break;
            }
        }

        return new ReadSet(symbols, readsState, readsThis);
    }

    public bool Contains(ISymbol? symbol) => symbol is not null && _symbols.Contains(symbol);

    /// <summary>
    /// Answers whether the region provably writes something the expression reads.
    /// Unlike <see cref="MayBeChangedBy"/>, it does not count a call merely because the method is not known to be pure.
    /// </summary>
    /// <remarks>
    /// On a struct, a method without the readonly modifier is proof, as <c>Utf8JsonReader.Read</c> moves the reader.
    /// A readonly variable such as an <c>in</c> parameter is copied before such a call, which leaves the variable unchanged.
    /// On a class or interface, only a method listed by <see cref="PurityOracle.AdvancesInstance"/> is proof.
    /// </remarks>
    public bool IsWrittenBy(IOperation region, PurityOracle purity)
    {
        foreach (var operation in region.DescendantsAndSelf())
        {
            if (Writes(operation) || MutatesReadInstance(operation, purity))
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

            if (Writes(operation))
            {
                return true;
            }

            switch (operation)
            {
                case IInvocationOperation invocation when !purity.IsPure(invocation.TargetMethod):
                    if (ReadsState || (writtenByClosure ??= IsWrittenByClosure(region)))
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

    private bool Writes(IOperation operation)
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

            default:
                return false;
        }
    }

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

    private bool IsWrittenByClosure(IOperation anywhereInMethod)
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
                if (Writes(inner))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
