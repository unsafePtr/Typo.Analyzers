using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace TypoAnalyzers.Core;

internal static class Jumps
{
    /// <summary>
    /// True when the statement can never complete normally because every path returns, throws, breaks, continues or jumps away.
    /// </summary>
    public static bool AlwaysExits(IOperation? statement) => AlwaysExits(statement, 0);

    private static bool AlwaysExits(IOperation? statement, int depth) => Limits.CanRecurse(depth) && statement switch
    {
        IReturnOperation { Kind: not OperationKind.YieldReturn } => true,
        IThrowOperation or IBranchOperation => true,
        IBlockOperation { Operations.Length: > 0 } block => AlwaysExits(block.Operations[block.Operations.Length - 1], depth + 1),
        IConditionalOperation { WhenFalse: not null } conditional =>
            AlwaysExits(conditional.WhenTrue, depth + 1) && AlwaysExits(conditional.WhenFalse, depth + 1),
        _ => false,
    };
}
