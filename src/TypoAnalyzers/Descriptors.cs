using Microsoft.CodeAnalysis;

namespace TypoAnalyzers;

internal static class Descriptors
{
    private const string Correctness = "Correctness";

    public static readonly DiagnosticDescriptor IdenticalSubExpressions = new(
        id: "TYPO0001",
        title: "Identical sub-expressions",
        messageFormat: "'{0}' repeats '{1}' in the same '{2}' expression",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The same operand appears twice in a chain of one operator, or on both sides of an operator whose result then becomes constant. One of the copies was usually meant to be edited after copy-paste.");

    public static readonly DiagnosticDescriptor ComparedWithItself = new(
        id: "TYPO0002",
        title: "Value compared with itself",
        messageFormat: "'{0}' is compared with itself in '{1}'",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A value is passed to its own comparison method, or both arguments of a comparison are the same, so the result is constant. Comparison methods are often copied and the 'other' side left unedited.");
}
