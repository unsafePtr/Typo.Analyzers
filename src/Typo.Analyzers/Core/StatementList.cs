using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Typo.Core;

/// <summary>
/// The lists of consecutive statements that a rule comparing neighboring statements visits.
/// </summary>
/// <remarks>
/// The statements of a switch section belong to its <see cref="ISwitchCaseOperation"/>, not to a block, whatever its labels are: constants, patterns or <c>when</c> clauses.
/// A section written with braces holds a single block, which is visited as a block, so no statement is visited twice.
/// </remarks>
internal static class StatementList
{
    public static ImmutableArray<OperationKind> Kinds { get; } = [OperationKind.Block, OperationKind.SwitchCase];

    public static ImmutableArray<IOperation> Of(IOperation operation) => operation switch
    {
        IBlockOperation block => block.Operations,
        ISwitchCaseOperation section => section.Body,
        _ => [],
    };
}
