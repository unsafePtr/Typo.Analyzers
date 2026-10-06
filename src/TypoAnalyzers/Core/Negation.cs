using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace TypoAnalyzers.Core;

/// <summary>
/// Relates two conditions beyond plain equivalence: the same test spelled differently, or one test being the exact negation of the other.
/// </summary>
/// <remarks>
/// Relational complements such as <c>a &lt; b</c> and <c>a &gt;= b</c> are only opposite for non-floating-point operands, because both are false when either side is NaN.
/// </remarks>
internal static class Negation
{
    /// <summary>
    /// True when both conditions test the same thing, including different spellings of a null test such as <c>x == null</c> and <c>x is null</c>.
    /// </summary>
    public static bool AreSame(IOperation x, IOperation y) =>
        OperationEquivalence.AreEquivalent(x, y)
        || (TryGetNullTest(x, out var xValue, out var xIsNull)
            && TryGetNullTest(y, out var yValue, out var yIsNull)
            && xIsNull == yIsNull
            && OperationEquivalence.AreEquivalent(xValue, yValue));

    /// <summary>
    /// A hash consistent with <see cref="AreSame"/>, so callers comparing many conditions only need the full check when hashes match.
    /// </summary>
    public static int GetHashCode(IOperation condition) =>
        TryGetNullTest(condition, out var value, out var isNull)
            ? unchecked((OperationEquivalence.GetHashCode(value) * 31) + (isNull ? 1 : 2))
            : OperationEquivalence.GetHashCode(condition);

    public static bool AreOpposite(IOperation x, IOperation y)
    {
        x = Skip(x);
        y = Skip(y);
        if (x is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } notX)
        {
            return AreSame(notX.Operand, y);
        }

        if (y is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null } notY)
        {
            return AreSame(x, notY.Operand);
        }

        if (TryGetNullTest(x, out var xValue, out var xIsNull) && TryGetNullTest(y, out var yValue, out var yIsNull))
        {
            return xIsNull != yIsNull && OperationEquivalence.AreEquivalent(xValue, yValue);
        }

        switch (x)
        {
            case IBinaryOperation { OperatorMethod: null } left when y is IBinaryOperation { OperatorMethod: null } right:
                return Complement(left.OperatorKind) == right.OperatorKind
                    && (left.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals || !IsFloatingPoint(left.LeftOperand.Type))
                    && ((OperationEquivalence.AreEquivalent(left.LeftOperand, right.LeftOperand) && OperationEquivalence.AreEquivalent(left.RightOperand, right.RightOperand))
                        || (left.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
                            && OperationEquivalence.AreEquivalent(left.LeftOperand, right.RightOperand)
                            && OperationEquivalence.AreEquivalent(left.RightOperand, right.LeftOperand)));

            // 'o is string' binds as a type check while 'o is not string' binds as a negated type pattern.
            case IIsTypeOperation { IsNegated: false } left when y is IIsPatternOperation { Pattern: INegatedPatternOperation { Pattern: ITypePatternOperation pattern } } right:
                return SymbolEqualityComparer.Default.Equals(left.TypeOperand, pattern.MatchedType) && OperationEquivalence.AreEquivalent(left.ValueOperand, right.Value);

            case IIsPatternOperation { Pattern: INegatedPatternOperation { Pattern: ITypePatternOperation pattern } } left when y is IIsTypeOperation { IsNegated: false } right:
                return SymbolEqualityComparer.Default.Equals(right.TypeOperand, pattern.MatchedType) && OperationEquivalence.AreEquivalent(right.ValueOperand, left.Value);

            case IIsPatternOperation left when y is IIsPatternOperation right:
                return OperationEquivalence.AreEquivalent(left.Value, right.Value)
                    && ((left.Pattern is INegatedPatternOperation negatedLeft && OperationEquivalence.PatternsEquivalent(negatedLeft.Pattern, right.Pattern))
                        || (right.Pattern is INegatedPatternOperation negatedRight && OperationEquivalence.PatternsEquivalent(left.Pattern, negatedRight.Pattern)));

            default:
                return false;
        }
    }

    private static bool TryGetNullTest(IOperation operation, out IOperation value, out bool isNull)
    {
        switch (Skip(operation))
        {
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals } binary when IsNullLiteral(binary.RightOperand):
                value = binary.LeftOperand;
                isNull = binary.OperatorKind == BinaryOperatorKind.Equals;
                return true;

            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals } binary when IsNullLiteral(binary.LeftOperand):
                value = binary.RightOperand;
                isNull = binary.OperatorKind == BinaryOperatorKind.Equals;
                return true;

            case IIsPatternOperation { Pattern: IConstantPatternOperation constant } isPattern when IsNullLiteral(constant.Value):
                value = isPattern.Value;
                isNull = true;
                return true;

            case IIsPatternOperation { Pattern: INegatedPatternOperation { Pattern: IConstantPatternOperation constant } } isPattern when IsNullLiteral(constant.Value):
                value = isPattern.Value;
                isNull = false;
                return true;

            default:
                value = null!;
                isNull = false;
                return false;
        }
    }

    private static bool IsNullLiteral(IOperation operation) =>
        Skip(operation) is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } };

    private static BinaryOperatorKind Complement(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Equals => BinaryOperatorKind.NotEquals,
        BinaryOperatorKind.NotEquals => BinaryOperatorKind.Equals,
        BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThanOrEqual,
        BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThan,
        BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThanOrEqual,
        BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThan,
        _ => BinaryOperatorKind.None,
    };

    private static bool IsFloatingPoint(ITypeSymbol? type) => type switch
    {
        INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable => IsFloatingPoint(nullable.TypeArguments[0]),
        _ => type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double,
    };

    private static IOperation Skip(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }
}
