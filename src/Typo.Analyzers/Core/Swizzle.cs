using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Typo.Core;

/// <summary>
/// Recognises a member whose name spells its call's arguments in order, which states their order instead of slipping into it.
/// </summary>
internal static class Swizzle
{
    // A swizzle such as 'Yyzw => new(Y, Y, Z, W)' or 'Yx => new(Y, X)' names its arguments in order, so the repeated or reordered name is stated.
    // A slip such as 'Xyzw => new(Y, Y, Z, W)' no longer spells the name and still reports.
    public static bool Spells(ISymbol? member, ImmutableArray<IArgumentOperation> arguments)
    {
        var name = member is IMethodSymbol { AssociatedSymbol: IPropertySymbol property } ? property.Name : member?.Name;
        if (name is null)
        {
            return false;
        }

        var offset = 0;
        foreach (var argument in arguments)
        {
            var argumentName = PlainName(argument.Value);
            if (argumentName is null
                || offset + argumentName.Length > name.Length
                || string.Compare(name, offset, argumentName, 0, argumentName.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            offset += argumentName.Length;
        }

        return offset == name.Length;
    }

    private static string? PlainName(IOperation value)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        return value switch
        {
            IFieldReferenceOperation { Instance: null or IInstanceReferenceOperation } field => field.Field.Name,
            IPropertyReferenceOperation { Instance: null or IInstanceReferenceOperation, Arguments.Length: 0 } property => property.Property.Name,
            IParameterReferenceOperation parameter => parameter.Parameter.Name,
            _ => null,
        };
    }
}
