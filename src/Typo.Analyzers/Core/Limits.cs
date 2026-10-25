using System.Runtime.CompilerServices;

namespace Typo.Core;

internal static class Limits
{
    /// <remarks>
    /// A stack overflow inside an analyzer kills the compiler or IDE process instead of producing AD0001, and generated code can nest thousands of operators.
    /// Callers treat hitting the limit as "not equivalent", "not pure", "does not always exit", "no conflicting letter" or "no shape".
    /// Each of those answers suppresses a report, so the only effect is a missed report.
    /// </remarks>
    public const int MaxDepth = 256;

    // Below this depth the stack check is skipped, so ordinary expressions never pay for it.
    private const int CheckedDepth = 32;

    /// <remarks>
    /// A fixed depth alone is not enough: frames of the large equivalence switch take over a kilobyte, and analyzers can run on threads with small stacks.
    /// </remarks>
    public static bool CanRecurse(int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        if (depth < CheckedDepth)
        {
            return true;
        }

        try
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            return true;
        }
        catch (InsufficientExecutionStackException)
        {
            return false;
        }
    }
}
