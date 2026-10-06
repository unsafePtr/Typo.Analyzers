using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace TypoAnalyzers.Core;

/// <summary>
/// Flattens a chain of one associative operator into its operands, so <c>a || b || a</c> yields [a, b, a] even though the tree is <c>(a || b) || a</c>.
/// </summary>
/// <remarks>
/// Buffers are thread-static because operation callbacks run concurrently and are hot.
/// Only one operand buffer is cached per thread, so a nested rental is safe but allocates.
/// </remarks>
internal static class OperandChain
{
    [ThreadStatic]
    private static List<IOperation>? t_operands;

    [ThreadStatic]
    private static Stack<IOperation>? t_pending;

    public static bool IsRoot(IBinaryOperation binary) =>
        binary.Parent is not IBinaryOperation parent || parent.OperatorKind != binary.OperatorKind || parent.OperatorMethod is not null;

    public static bool IsRoot(IBinaryPatternOperation pattern) =>
        pattern.Parent is not IBinaryPatternOperation parent || parent.OperatorKind != pattern.OperatorKind;

    public static List<IOperation> Rent()
    {
        var operands = t_operands ?? new List<IOperation>(8);
        t_operands = null;
        return operands;
    }

    public static void Return(List<IOperation> operands)
    {
        operands.Clear();
        t_operands = operands;
    }

    public static void Flatten(IBinaryOperation root, List<IOperation> operands)
    {
        var pending = t_pending ??= new Stack<IOperation>(8);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is IBinaryOperation binary && binary.OperatorKind == root.OperatorKind && binary.OperatorMethod is null)
            {
                pending.Push(binary.RightOperand);
                pending.Push(binary.LeftOperand);
            }
            else
            {
                operands.Add(current);
            }
        }
    }

    public static void Flatten(IBinaryPatternOperation root, List<IOperation> operands)
    {
        var pending = t_pending ??= new Stack<IOperation>(8);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is IBinaryPatternOperation binary && binary.OperatorKind == root.OperatorKind)
            {
                pending.Push(binary.RightPattern);
                pending.Push(binary.LeftPattern);
            }
            else
            {
                operands.Add(current);
            }
        }
    }
}
