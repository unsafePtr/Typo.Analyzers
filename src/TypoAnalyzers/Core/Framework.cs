using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace TypoAnalyzers.Core;

/// <summary>
/// Separates .NET's own types from the user's, for rules that skip user-defined equality because tests legitimately assert its reflexivity.
/// </summary>
/// <remarks>
/// Assemblies are recognised by Microsoft's strong-name public key tokens, which every framework and reference assembly carries.
/// </remarks>
internal static class Framework
{
    private static readonly byte[][] s_publicKeyTokens =
    [
        [0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89],
        [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a],
        [0x7c, 0xec, 0x85, 0xd7, 0xbe, 0xa7, 0x79, 0x8e],
        [0xcc, 0x7b, 0x13, 0xff, 0xcd, 0x2d, 0xdd, 0x51],
        [0x31, 0xbf, 0x38, 0x56, 0xad, 0x36, 0x4e, 0x35],
        [0xad, 0xb9, 0x79, 0x38, 0x29, 0xdd, 0xae, 0x60],
    ];

    /// <summary>
    /// True for built-in types, enums, arrays and types from framework assemblies; Nullable&lt;T&gt; follows its underlying type.
    /// </summary>
    public static bool IsFrameworkValue(ITypeSymbol? type) => type switch
    {
        null => false,
        IArrayTypeSymbol => true,
        { TypeKind: TypeKind.Enum } => true,
        INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable => IsFrameworkValue(nullable.TypeArguments[0]),
        { SpecialType: not SpecialType.None } => true,
        _ => IsFrameworkAssembly(type.ContainingAssembly),
    };

    private static bool IsFrameworkAssembly(IAssemblySymbol? assembly)
    {
        if (assembly is null)
        {
            return false;
        }

        var token = assembly.Identity.PublicKeyToken;
        foreach (var candidate in s_publicKeyTokens)
        {
            if (Matches(token, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(ImmutableArray<byte> token, byte[] candidate)
    {
        if (token.Length != candidate.Length)
        {
            return false;
        }

        for (var i = 0; i < candidate.Length; i++)
        {
            if (token[i] != candidate[i])
            {
                return false;
            }
        }

        return true;
    }
}
