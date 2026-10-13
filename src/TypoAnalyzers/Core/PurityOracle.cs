using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace TypoAnalyzers.Core;

/// <summary>
/// Decides whether evaluating an expression twice gives the same value with no observable effect, which is what makes a repeated operand a mistake rather than a deliberate second call.
/// </summary>
/// <remarks>
/// Property getters and operators are assumed pure by convention, as other static analyzers also assume.
/// Methods count as pure through the rules for object, enums, Nullable&lt;T&gt;, DateTime, primitives and string, or through the type tables below.
/// The read-only MemoryExtensions overloads and the Equals/CompareTo/GetHashCode convention also count; nothing else does.
/// Most analyzers consult the oracle, so <see cref="For"/> builds it once per compilation and they all share that instance.
/// The analyzers sharing it run concurrently, so the oracle must stay read-only once the constructor returns.
/// </remarks>
internal sealed class PurityOracle
{
    // The table holds compilations weakly, so each oracle dies with its compilation, which the IDE replaces on every edit.
    private static readonly ConditionalWeakTable<Compilation, PurityOracle> s_byCompilation = new();

    private static readonly string[] s_pureTypeNames =
    [
        "System.Math",
        "System.MathF",
        "System.Guid",
        "System.TimeSpan",
        "System.DateTimeOffset",
        "System.StringComparer",
    ];

    // Only the static members of these are pure; HashCode.Add, for example, mutates the instance.
    // Path.GetRandomFileName and Path.GetTempFileName return a new name on every call, so IsPure excludes them by name.
    private static readonly string[] s_pureStaticTypeNames =
    [
        "System.HashCode",
        "System.Convert",
        "System.BitConverter",
        "System.IO.Path",
    ];

    private static readonly string[] s_lookupTypeNames =
    [
        "System.Linq.Enumerable",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IDictionary`2",
        "System.Collections.Generic.IReadOnlyDictionary`2",
        "System.Collections.Generic.ISet`1",
        "System.Collections.Generic.IReadOnlySet`1",
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.SortedSet`1",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.SortedDictionary`2",
        "System.Collections.Immutable.ImmutableArray`1",
        "System.Collections.Immutable.ImmutableHashSet`1",
        "System.Collections.Immutable.ImmutableDictionary`2",
        "System.Buffers.SearchValues`1",
    ];

    private readonly INamedTypeSymbol? _stream;
    private readonly INamedTypeSymbol? _memoryExtensions;
    private readonly INamedTypeSymbol? _span;
    private readonly INamedTypeSymbol? _memory;
    private readonly HashSet<INamedTypeSymbol> _pureTypes = new(SymbolEqualityComparer.Default);
    private readonly HashSet<INamedTypeSymbol> _pureStaticTypes = new(SymbolEqualityComparer.Default);
    private readonly HashSet<INamedTypeSymbol> _lookupTypes = new(SymbolEqualityComparer.Default);
    private readonly HashSet<string> _memoryExtensionReaders = new(StringComparer.Ordinal);

    private PurityOracle(Compilation compilation)
    {
        _stream = compilation.GetTypeByMetadataName("System.IO.Stream");
        _memoryExtensions = compilation.GetTypeByMetadataName("System.MemoryExtensions");
        _span = compilation.GetTypeByMetadataName("System.Span`1");
        _memory = compilation.GetTypeByMetadataName("System.Memory`1");
        foreach (var member in _memoryExtensions?.GetMembers() ?? [])
        {
            if (member is IMethodSymbol method && !HasWritableParameter(method))
            {
                _memoryExtensionReaders.Add(method.Name);
            }
        }

        AddTypes(compilation, s_pureTypeNames, _pureTypes);
        AddTypes(compilation, s_pureStaticTypeNames, _pureStaticTypes);
        AddTypes(compilation, s_lookupTypeNames, _lookupTypes);
    }

    public static PurityOracle For(Compilation compilation) => s_byCompilation.GetValue(compilation, static c => new PurityOracle(c));

    public bool IsFree(IOperation? operation) => IsFree(operation, 0);

    private bool IsFree(IOperation? operation, int depth)
    {
        if (!Limits.CanRecurse(depth))
        {
            return false;
        }

        var next = depth + 1;
        switch (operation)
        {
            case null:
            case ILiteralOperation:
            case ILocalReferenceOperation:
            case IParameterReferenceOperation:
            case IInstanceReferenceOperation:
            case IConditionalAccessInstanceOperation:
            case ITypeOfOperation:
            case INameOfOperation:
            case ISizeOfOperation:
            case IDefaultValueOperation:
                return true;

            case IFieldReferenceOperation field:
                return IsFree(field.Instance, next);

            case IPropertyReferenceOperation property:
                return !IsClockProperty(property.Property) && IsFree(property.Instance, next) && ArgumentsFree(property.Arguments, next);

            case IArrayElementReferenceOperation element:
                return IsFree(element.ArrayReference, next) && AllFree(element.Indices, next);

            case IInvocationOperation invocation:
                return IsPure(invocation.TargetMethod) && IsFree(invocation.Instance, next) && ArgumentsFree(invocation.Arguments, next);

            case IConversionOperation conversion:
                return IsFree(conversion.Operand, next);

            case IUnaryOperation unary:
                return IsFree(unary.Operand, next);

            case IBinaryOperation binary:
                return IsFree(binary.LeftOperand, next) && IsFree(binary.RightOperand, next);

            case IIsTypeOperation isType:
                return IsFree(isType.ValueOperand, next);

            case IIsPatternOperation isPattern:
                return IsFree(isPattern.Value, next);

            case IConditionalAccessOperation access:
                return IsFree(access.Operation, next) && IsFree(access.WhenNotNull, next);

            case IConditionalOperation conditional:
                return IsFree(conditional.Condition, next) && IsFree(conditional.WhenTrue, next) && IsFree(conditional.WhenFalse, next);

            case ICoalesceOperation coalesce:
                return IsFree(coalesce.Value, next) && IsFree(coalesce.WhenNull, next);

            default:
                return false;
        }
    }

    public bool IsPure(IMethodSymbol method)
    {
        var type = method.ContainingType.OriginalDefinition;
        switch (type.SpecialType)
        {
            case SpecialType.System_Object:
                return method.Name is "Equals" or "ReferenceEquals" or "GetHashCode" or "GetType";

            case SpecialType.System_Enum:
            case SpecialType.System_Nullable_T:
            case SpecialType.System_DateTime:
                return true;

            case >= SpecialType.System_Boolean and <= SpecialType.System_String:
                // Strings and primitives are immutable, but CopyTo writes an array.
                // ReadSet tracks their writes through out parameters and Span<T> destinations, as in TryFormat.
                return method.Name != "CopyTo";
        }

        if (_pureTypes.Contains(type) || (method.IsStatic && _pureStaticTypes.Contains(type)))
        {
            return method.Name is not ("NewGuid" or "CreateVersion7" or "GetRandomFileName" or "GetTempFileName");
        }

        if (_lookupTypes.Contains(type))
        {
            return method.Name is "Contains" or "ContainsKey" or "ContainsValue" or "Any" or "Count" or "IndexOf";
        }

        if (SymbolEqualityComparer.Default.Equals(type, _memoryExtensions))
        {
            return OnlyReads(method);
        }

        return IsEqualityConvention(method);
    }

    // MoveNext advances an enumerator, and every Stream method moves, resizes or closes the stream.
    public bool AdvancesInstance(IMethodSymbol method)
    {
        if (method.Name == "MoveNext")
        {
            return IsEnumerator(method.ContainingType);
        }

        for (var declared = method; declared is not null; declared = declared.OverriddenMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(declared.ContainingType, _stream))
            {
                return true;
            }
        }

        return false;
    }

    // The callee can write the memory a Span<T> or Memory<T> argument views, unless it is a MemoryExtensions reader searching it.
    public bool WritesThrough(IArgumentOperation argument) =>
        argument.Parameter is { RefKind: RefKind.None } parameter
        && IsWritable(parameter)
        && !(argument.Parent is IInvocationOperation { TargetMethod: var method }
            && SymbolEqualityComparer.Default.Equals(method.ContainingType, _memoryExtensions)
            && IsSearchedSpan(parameter, method));

    private bool ArgumentsFree(ImmutableArray<IArgumentOperation> arguments, int depth)
    {
        foreach (var argument in arguments)
        {
            if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out || !IsFree(argument.Value, depth))
            {
                return false;
            }
        }

        return true;
    }

    private bool AllFree(ImmutableArray<IOperation> operations, int depth)
    {
        foreach (var operation in operations)
        {
            if (!IsFree(operation, depth))
            {
                return false;
            }
        }

        return true;
    }

    // Every MemoryExtensions method that writes takes a Span<T>, a Memory<T>, or a ref or out parameter.
    // A Span<T> receiver is still only read when the name also has an overload without one, as IndexOf and Trim do.
    // Through .NET 11 the in-place writers Sort, Reverse, Replace, ReplaceAny and ReplaceAnyExcept have no such overload.
    // .NET 10 lowers the priority of those Span<T> readers, so mainly older references and Trim, which returns a Span<T>, bind to them.
    // C# 14 binds array.Contains to MemoryExtensions instead of Enumerable, so this rule keeps repeated array lookups visible.
    private bool OnlyReads(IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (IsWritable(parameter) && !IsSearchedSpan(parameter, method))
            {
                return false;
            }
        }

        return true;
    }

    private bool HasWritableParameter(IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (IsWritable(parameter))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsWritable(IParameterSymbol parameter)
    {
        var type = parameter.Type.OriginalDefinition;
        return parameter.RefKind is RefKind.Ref or RefKind.Out
            || SymbolEqualityComparer.Default.Equals(type, _span)
            || SymbolEqualityComparer.Default.Equals(type, _memory);
    }

    private bool IsSearchedSpan(IParameterSymbol parameter, IMethodSymbol method) =>
        parameter.Ordinal == 0
        && parameter.RefKind == RefKind.None
        && SymbolEqualityComparer.Default.Equals(parameter.Type.OriginalDefinition, _span)
        && _memoryExtensionReaders.Contains(method.Name);

    // Getters are assumed pure, except clocks: two reads of DateTime.Now are not the same value.
    private static bool IsClockProperty(IPropertySymbol property) => property.ContainingType.Name switch
    {
        "DateTime" or "DateTimeOffset" => property.Name is "Now" or "UtcNow" or "Today",
        "Environment" => property.Name is "TickCount" or "TickCount64",
        "Stopwatch" => property.Name is "Elapsed" or "ElapsedMilliseconds" or "ElapsedTicks",
        _ => false,
    };

    private static bool IsEqualityConvention(IMethodSymbol method) => method.Name switch
    {
        "Equals" => method.ReturnType.SpecialType == SpecialType.System_Boolean,
        "CompareTo" or "GetHashCode" => method.ReturnType.SpecialType == SpecialType.System_Int32,
        _ => false,
    };

    private static bool IsEnumerator(INamedTypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_Collections_IEnumerator)
        {
            return true;
        }

        foreach (var implemented in type.AllInterfaces)
        {
            if (implemented.SpecialType == SpecialType.System_Collections_IEnumerator)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddTypes(Compilation compilation, string[] metadataNames, HashSet<INamedTypeSymbol> target)
    {
        foreach (var name in metadataNames)
        {
            if (compilation.GetTypeByMetadataName(name) is { } type)
            {
                target.Add(type);
            }
        }
    }
}
