using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace TypoAnalyzers.Core;

/// <summary>
/// Semantic structural equality of operation trees: <c>this.X</c> equals <c>X</c>, implicit conversions are ignored, commutative operators match in either order, and <c>a &lt; b</c> matches <c>b &gt; a</c>.
/// </summary>
/// <remarks>
/// Unknown operation kinds compare as different, so new language constructs can only cause missed reports, never false ones.
/// Trees too deep to walk safely (see <see cref="Limits.CanRecurse"/>) compare as different for the same reason.
/// </remarks>
internal static class OperationEquivalence
{
    public static bool AreEquivalent(IOperation? x, IOperation? y) => AreEquivalent(x, y, 0);

    public static bool PatternsEquivalent(IPatternOperation x, IPatternOperation y) => PatternsEquivalent(x, y, 0);

    /// <summary>
    /// A hash consistent with <see cref="AreEquivalent(IOperation?, IOperation?)"/>: equivalent operations always hash alike, so long operand lists only need a full comparison when hashes match.
    /// </summary>
    /// <remarks>
    /// Commutative operators and argument lists, which are matched in any order, combine their parts with addition so that order does not change the hash.
    /// </remarks>
    public static int GetHashCode(IOperation? operation) => Hash(operation, 0);

    public static int GetPatternHashCode(IPatternOperation pattern) => HashPattern(pattern, 0);

    private static int Hash(IOperation? operation, int depth)
    {
        operation = SkipImplicitConversions(operation);
        if (operation is null)
        {
            return 0;
        }

        // Past the depth limit AreEquivalent answers false, so any shared value keeps the hash consistent.
        if (!Limits.CanRecurse(depth))
        {
            return (int)operation.Kind;
        }

        var next = depth + 1;
        var hash = (int)operation.Kind;
        switch (operation)
        {
            case ILiteralOperation:
            case IDefaultValueOperation:
            case INameOfOperation:
            case ISizeOfOperation:
                return Mix(hash, operation.ConstantValue.HasValue ? operation.ConstantValue.Value?.GetHashCode() ?? 0 : 0);

            case ILocalReferenceOperation local:
                return Mix(hash, SymbolEqualityComparer.Default.GetHashCode(local.Local));

            case IParameterReferenceOperation parameter:
                return Mix(hash, SymbolEqualityComparer.Default.GetHashCode(parameter.Parameter));

            case IInstanceReferenceOperation instance:
                return Mix(hash, (int)instance.ReferenceKind);

            case IFieldReferenceOperation field:
                return Mix(Mix(hash, SymbolEqualityComparer.Default.GetHashCode(field.Field)), Hash(field.Instance, next));

            case IPropertyReferenceOperation property:
                return Mix(Mix(Mix(hash, SymbolEqualityComparer.Default.GetHashCode(property.Property)), Hash(property.Instance, next)), HashArguments(property.Arguments, next));

            case IArrayElementReferenceOperation element:
                hash = Mix(hash, Hash(element.ArrayReference, next));
                foreach (var index in element.Indices)
                {
                    hash = Mix(hash, Hash(index, next));
                }

                return hash;

            case IInvocationOperation invocation:
                return Mix(Mix(Mix(hash, SymbolEqualityComparer.Default.GetHashCode(invocation.TargetMethod)), Hash(invocation.Instance, next)), HashArguments(invocation.Arguments, next));

            case IBinaryOperation binary:
            {
                var kind = binary.OperatorKind;
                var left = Hash(binary.LeftOperand, next);
                var right = Hash(binary.RightOperand, next);
                if (kind is BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual)
                {
                    (kind, left, right) = (Mirror(kind), right, left);
                }

                hash = Mix(hash, (int)kind);
                return IsCommutative(kind) ? Mix(hash, unchecked(left + right)) : Mix(Mix(hash, left), right);
            }

            case IUnaryOperation unary:
                return Mix(Mix(hash, (int)unary.OperatorKind), Hash(unary.Operand, next));

            case IConversionOperation conversion:
                return Mix(Mix(hash, SymbolEqualityComparer.Default.GetHashCode(conversion.Type)), Hash(conversion.Operand, next));

            case IIsTypeOperation isType:
                return Mix(Mix(hash, SymbolEqualityComparer.Default.GetHashCode(StripNullable(isType.TypeOperand))), Hash(isType.ValueOperand, next));

            case IIsPatternOperation isPattern:
                return Mix(Mix(hash, Hash(isPattern.Value, next)), HashPattern(isPattern.Pattern, next));

            case IConditionalAccessOperation access:
                return Mix(Mix(hash, Hash(access.Operation, next)), Hash(access.WhenNotNull, next));

            case IConditionalOperation conditional:
                return Mix(Mix(Mix(hash, Hash(conditional.Condition, next)), Hash(conditional.WhenTrue, next)), Hash(conditional.WhenFalse, next));

            case ICoalesceOperation coalesce:
                return Mix(Mix(hash, Hash(coalesce.Value, next)), Hash(coalesce.WhenNull, next));

            case ITypeOfOperation typeOf:
                return Mix(hash, SymbolEqualityComparer.Default.GetHashCode(typeOf.TypeOperand));

            default:
                return hash;
        }
    }

    private static int HashPattern(IPatternOperation pattern, int depth)
    {
        if (!Limits.CanRecurse(depth))
        {
            return (int)pattern.Kind;
        }

        var next = depth + 1;
        var hash = (int)pattern.Kind;
        switch (pattern)
        {
            case ITypePatternOperation type:
                return Mix(hash, SymbolEqualityComparer.Default.GetHashCode(type.MatchedType));

            case IConstantPatternOperation constant:
                return Mix(hash, Hash(constant.Value, next));

            case IRelationalPatternOperation relational:
                return Mix(Mix(hash, (int)relational.OperatorKind), Hash(relational.Value, next));

            case INegatedPatternOperation negated:
                return Mix(hash, HashPattern(negated.Pattern, next));

            case IBinaryPatternOperation binary:
                return Mix(Mix(hash, (int)binary.OperatorKind), unchecked(HashPattern(binary.LeftPattern, next) + HashPattern(binary.RightPattern, next)));

            case IDeclarationPatternOperation declaration:
                return Mix(hash, SymbolEqualityComparer.Default.GetHashCode(declaration.MatchedType));

            default:
                return hash;
        }
    }

    private static int HashArguments(ImmutableArray<IArgumentOperation> arguments, int depth)
    {
        var sum = 0;
        foreach (var argument in arguments)
        {
            sum = unchecked(sum + Mix(SymbolEqualityComparer.Default.GetHashCode(argument.Parameter), Hash(argument.Value, depth)));
        }

        return sum;
    }

    private static int Mix(int hash, int value) => unchecked((hash * 31) + value);

    private static bool AreEquivalent(IOperation? x, IOperation? y, int depth)
    {
        x = SkipImplicitConversions(x);
        y = SkipImplicitConversions(y);
        if (x is null || y is null)
        {
            return x is null && y is null;
        }

        if (x.Kind != y.Kind || !Limits.CanRecurse(depth))
        {
            return false;
        }

        var next = depth + 1;
        switch (x)
        {
            case ILiteralOperation:
                return SameConstant(x, y);

            case ILocalReferenceOperation local:
                return SymbolEqualityComparer.Default.Equals(local.Local, ((ILocalReferenceOperation)y).Local);

            case IParameterReferenceOperation parameter:
                return SymbolEqualityComparer.Default.Equals(parameter.Parameter, ((IParameterReferenceOperation)y).Parameter);

            case IInstanceReferenceOperation instance:
                return instance.ReferenceKind == ((IInstanceReferenceOperation)y).ReferenceKind;

            case IFieldReferenceOperation field:
            {
                var other = (IFieldReferenceOperation)y;
                return SymbolEqualityComparer.Default.Equals(field.Field, other.Field) && AreEquivalent(field.Instance, other.Instance, next);
            }

            case IPropertyReferenceOperation property:
            {
                var other = (IPropertyReferenceOperation)y;
                return SymbolEqualityComparer.Default.Equals(property.Property, other.Property)
                    && AreEquivalent(property.Instance, other.Instance, next)
                    && ArgumentsEquivalent(property.Arguments, other.Arguments, next);
            }

            case IArrayElementReferenceOperation element:
            {
                var other = (IArrayElementReferenceOperation)y;
                return AreEquivalent(element.ArrayReference, other.ArrayReference, next) && SequenceEquivalent(element.Indices, other.Indices, next);
            }

            case IInvocationOperation invocation:
            {
                var other = (IInvocationOperation)y;
                return SymbolEqualityComparer.Default.Equals(invocation.TargetMethod, other.TargetMethod)
                    && AreEquivalent(invocation.Instance, other.Instance, next)
                    && ArgumentsEquivalent(invocation.Arguments, other.Arguments, next);
            }

            case IBinaryOperation binary:
            {
                var other = (IBinaryOperation)y;
                if (binary.OperatorKind == other.OperatorKind && SymbolEqualityComparer.Default.Equals(binary.OperatorMethod, other.OperatorMethod))
                {
                    return (AreEquivalent(binary.LeftOperand, other.LeftOperand, next) && AreEquivalent(binary.RightOperand, other.RightOperand, next))
                        || (IsCommutative(binary.OperatorKind) && AreEquivalent(binary.LeftOperand, other.RightOperand, next) && AreEquivalent(binary.RightOperand, other.LeftOperand, next));
                }

                return Mirror(binary.OperatorKind) is var mirrored and not BinaryOperatorKind.None
                    && mirrored == other.OperatorKind
                    && AreMirrorOperators(binary.OperatorMethod, other.OperatorMethod)
                    && AreEquivalent(binary.LeftOperand, other.RightOperand, next)
                    && AreEquivalent(binary.RightOperand, other.LeftOperand, next);
            }

            case IUnaryOperation unary:
            {
                var other = (IUnaryOperation)y;
                return unary.OperatorKind == other.OperatorKind
                    && SymbolEqualityComparer.Default.Equals(unary.OperatorMethod, other.OperatorMethod)
                    && AreEquivalent(unary.Operand, other.Operand, next);
            }

            case IConversionOperation conversion:
                return SameType(conversion.Type, y.Type) && AreEquivalent(conversion.Operand, ((IConversionOperation)y).Operand, next);

            case IIsTypeOperation isType:
            {
                var other = (IIsTypeOperation)y;
                return isType.IsNegated == other.IsNegated
                    && SameType(StripNullable(isType.TypeOperand), StripNullable(other.TypeOperand))
                    && AreEquivalent(isType.ValueOperand, other.ValueOperand, next);
            }

            case IIsPatternOperation isPattern:
            {
                var other = (IIsPatternOperation)y;
                return AreEquivalent(isPattern.Value, other.Value, next) && PatternsEquivalent(isPattern.Pattern, other.Pattern, next);
            }

            case IConditionalAccessOperation access:
            {
                var other = (IConditionalAccessOperation)y;
                return AreEquivalent(access.Operation, other.Operation, next) && AreEquivalent(access.WhenNotNull, other.WhenNotNull, next);
            }

            case IConditionalAccessInstanceOperation:
                return true;

            case IConditionalOperation conditional:
            {
                var other = (IConditionalOperation)y;
                return AreEquivalent(conditional.Condition, other.Condition, next)
                    && AreEquivalent(conditional.WhenTrue, other.WhenTrue, next)
                    && AreEquivalent(conditional.WhenFalse, other.WhenFalse, next);
            }

            case ICoalesceOperation coalesce:
            {
                var other = (ICoalesceOperation)y;
                return AreEquivalent(coalesce.Value, other.Value, next) && AreEquivalent(coalesce.WhenNull, other.WhenNull, next);
            }

            case ITypeOfOperation typeOf:
                return SameType(typeOf.TypeOperand, ((ITypeOfOperation)y).TypeOperand);

            case IDefaultValueOperation:
            case INameOfOperation:
            case ISizeOfOperation:
                return SameConstant(x, y);

            default:
                return false;
        }
    }

    private static bool PatternsEquivalent(IPatternOperation x, IPatternOperation y, int depth)
    {
        if (x.Kind != y.Kind || !Limits.CanRecurse(depth))
        {
            return false;
        }

        var next = depth + 1;
        switch (x)
        {
            case ITypePatternOperation type:
                return SameType(type.MatchedType, ((ITypePatternOperation)y).MatchedType);

            case IConstantPatternOperation constant:
                return AreEquivalent(constant.Value, ((IConstantPatternOperation)y).Value, next);

            case IRelationalPatternOperation relational:
            {
                var other = (IRelationalPatternOperation)y;
                return relational.OperatorKind == other.OperatorKind && AreEquivalent(relational.Value, other.Value, next);
            }

            case INegatedPatternOperation negated:
                return PatternsEquivalent(negated.Pattern, ((INegatedPatternOperation)y).Pattern, next);

            case IBinaryPatternOperation binary:
            {
                var other = (IBinaryPatternOperation)y;
                return binary.OperatorKind == other.OperatorKind
                    && ((PatternsEquivalent(binary.LeftPattern, other.LeftPattern, next) && PatternsEquivalent(binary.RightPattern, other.RightPattern, next))
                        || (PatternsEquivalent(binary.LeftPattern, other.RightPattern, next) && PatternsEquivalent(binary.RightPattern, other.LeftPattern, next)));
            }

            case IDeclarationPatternOperation declaration:
            {
                var other = (IDeclarationPatternOperation)y;
                return declaration.DeclaredSymbol is null
                    && other.DeclaredSymbol is null
                    && SameType(declaration.MatchedType, other.MatchedType);
            }

            case IDiscardPatternOperation:
                return true;

            default:
                return false;
        }
    }

    private static IOperation? SkipImplicitConversions(IOperation? operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    // Callers have already checked that both sides bind to the same member, and a bound call carries one argument per parameter, so both lists have the same parameters.
    private static bool ArgumentsEquivalent(ImmutableArray<IArgumentOperation> x, ImmutableArray<IArgumentOperation> y, int depth)
    {
        // Arguments are stored in evaluation order, which named arguments can reorder, so match them by parameter instead of by index.
        foreach (var argument in x)
        {
            var matched = false;
            foreach (var candidate in y)
            {
                if (SymbolEqualityComparer.Default.Equals(argument.Parameter, candidate.Parameter))
                {
                    matched = AreEquivalent(argument.Value, candidate.Value, depth);
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    // Only used for indices of equivalent arrays, which share a rank.
    private static bool SequenceEquivalent(ImmutableArray<IOperation> x, ImmutableArray<IOperation> y, int depth)
    {
        for (var i = 0; i < x.Length; i++)
        {
            if (!AreEquivalent(x[i], y[i], depth))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCommutative(BinaryOperatorKind kind) => kind
        is BinaryOperatorKind.Add
        or BinaryOperatorKind.Multiply
        or BinaryOperatorKind.And
        or BinaryOperatorKind.Or
        or BinaryOperatorKind.ExclusiveOr
        or BinaryOperatorKind.ConditionalAnd
        or BinaryOperatorKind.ConditionalOr
        or BinaryOperatorKind.Equals
        or BinaryOperatorKind.NotEquals;

    private static BinaryOperatorKind Mirror(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
        BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
        BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
        BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
        _ => BinaryOperatorKind.None,
    };

    // C# makes a type declare '<' with '>' and '<=' with '>='; like getters, they are trusted to agree.
    private static bool AreMirrorOperators(IMethodSymbol? x, IMethodSymbol? y)
    {
        if (x is null || y is null)
        {
            return x is null && y is null;
        }

        return SymbolEqualityComparer.Default.Equals(x.ContainingType, y.ContainingType)
            && x.Parameters.Length == 2
            && y.Parameters.Length == 2
            && SameType(x.Parameters[0].Type, y.Parameters[1].Type)
            && SameType(x.Parameters[1].Type, y.Parameters[0].Type);
    }

    private static bool SameConstant(IOperation x, IOperation y) =>
        SameType(x.Type, y.Type) && x.ConstantValue.HasValue && y.ConstantValue.HasValue && Equals(x.ConstantValue.Value, y.ConstantValue.Value);

    private static bool SameType(ITypeSymbol? x, ITypeSymbol? y) => SymbolEqualityComparer.Default.Equals(x, y);

    // For a boxed value 'v is long?' and 'v is long' are the same test, because boxing a Nullable<T> yields either null or a boxed T.
    private static ITypeSymbol StripNullable(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;
}
