using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Typo.Core;

/// <summary>
/// Relates two conditions beyond plain equivalence: the same test spelled differently, an exact negation, or two exclusive tests.
/// </summary>
/// <remarks>
/// Relational complements such as <c>a &lt; b</c> and <c>a &gt;= b</c> never both hold, but both are false when either side is NaN or null.
/// So <see cref="Excludes"/> accepts them on any operands, and <see cref="AreOpposite"/> only when neither side is floating-point or nullable.
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

    /// <summary>
    /// True when exactly one of the two conditions holds.
    /// </summary>
    public static bool AreOpposite(IOperation x, IOperation y) => AreComplements(x, y, exhaustive: true);

    /// <summary>
    /// True when the two conditions never both hold, though both may be false.
    /// </summary>
    public static bool Excludes(IOperation x, IOperation y) => AreComplements(x, y, exhaustive: false);

    private static bool AreComplements(IOperation x, IOperation y, bool exhaustive)
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

        return x switch
        {
            IBinaryOperation { OperatorMethod: null } left when y is IBinaryOperation { OperatorMethod: null } right =>
                (Complement(left.OperatorKind) == right.OperatorKind
                    && (!exhaustive || left.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals || (!left.IsLifted && !IsFloatingPoint(left.LeftOperand.Type)))
                    && ((OperationEquivalence.AreEquivalent(left.LeftOperand, right.LeftOperand) && OperationEquivalence.AreEquivalent(left.RightOperand, right.RightOperand))
                        || (left.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
                            && OperationEquivalence.AreEquivalent(left.LeftOperand, right.RightOperand)
                            && OperationEquivalence.AreEquivalent(left.RightOperand, right.LeftOperand))))
                || (!exhaustive && AreDisjointRelations(left, right)),

            // 'o is string' binds as a type check while 'o is not string' binds as a negated type pattern.
            IIsTypeOperation { IsNegated: false } left when y is IIsPatternOperation { Pattern: INegatedPatternOperation { Pattern: ITypePatternOperation pattern } } right =>
                SymbolEqualityComparer.Default.Equals(left.TypeOperand, pattern.MatchedType) && OperationEquivalence.AreEquivalent(left.ValueOperand, right.Value),

            IIsPatternOperation { Pattern: INegatedPatternOperation { Pattern: ITypePatternOperation pattern } } left when y is IIsTypeOperation { IsNegated: false } right =>
                SymbolEqualityComparer.Default.Equals(right.TypeOperand, pattern.MatchedType) && OperationEquivalence.AreEquivalent(right.ValueOperand, left.Value),

            IIsPatternOperation left when y is IIsPatternOperation right =>
                OperationEquivalence.AreEquivalent(left.Value, right.Value)
                && ((left.Pattern is INegatedPatternOperation negatedLeft && OperationEquivalence.PatternsEquivalent(negatedLeft.Pattern, right.Pattern))
                    || (right.Pattern is INegatedPatternOperation negatedRight && OperationEquivalence.PatternsEquivalent(left.Pattern, negatedRight.Pattern))),

            _ => false,
        };
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

    // Reads both comparisons as relations between the same two operands, so 'a < b' and 'b < a' never both hold.
    // The result holds for NaN and null as well, since a comparison with either is false.
    private static bool AreDisjointRelations(IBinaryOperation left, IBinaryOperation right)
    {
        var leftRelations = Relations(left.OperatorKind);
        var rightRelations = Relations(right.OperatorKind);
        if (leftRelations == 0 || rightRelations == 0)
        {
            return false;
        }

        if (OperationEquivalence.AreEquivalent(left.LeftOperand, right.RightOperand) && OperationEquivalence.AreEquivalent(left.RightOperand, right.LeftOperand))
        {
            rightRelations = Mirror(rightRelations);
        }
        else if (!OperationEquivalence.AreEquivalent(left.LeftOperand, right.LeftOperand) || !OperationEquivalence.AreEquivalent(left.RightOperand, right.RightOperand))
        {
            return false;
        }

        return (leftRelations & rightRelations) == 0;
    }

    private const int Less = 1;
    private const int Same = 2;
    private const int Greater = 4;

    private static int Relations(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.LessThan => Less,
        BinaryOperatorKind.LessThanOrEqual => Less | Same,
        BinaryOperatorKind.Equals => Same,
        BinaryOperatorKind.NotEquals => Less | Greater,
        BinaryOperatorKind.GreaterThanOrEqual => Greater | Same,
        BinaryOperatorKind.GreaterThan => Greater,
        _ => 0,
    };

    private static int Mirror(int relations) => (relations & Same) | ((relations & Less) != 0 ? Greater : 0) | ((relations & Greater) != 0 ? Less : 0);

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

    private static bool IsFloatingPoint(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double;

    private static IOperation Skip(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }
}
