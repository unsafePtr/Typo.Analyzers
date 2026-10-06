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

    public static readonly DiagnosticDescriptor RepeatedElseIfCondition = new(
        id: "TYPO0003",
        title: "Condition repeated in an if-else chain",
        messageFormat: "Condition '{0}' was already tested by an earlier branch of this if-else chain, so this branch never runs",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An else-if branch repeats a condition that an earlier branch already tested, so it is unreachable.");

    public static readonly DiagnosticDescriptor RepeatedAdjacentCondition = new(
        id: "TYPO0004",
        title: "Adjacent 'if' statements with the same condition",
        messageFormat: "Condition '{0}' is identical to the condition of the preceding 'if' statement",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Two consecutive 'if' statements test the same condition and nothing in between can change it. Either the second one was meant to test something else, or the two should be merged.");

    public static readonly DiagnosticDescriptor RepeatedNestedCondition = new(
        id: "TYPO0005",
        title: "Condition already checked by an enclosing 'if'",
        messageFormat: "Condition '{0}' is always {1} here because an enclosing 'if' already checked it",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A nested 'if' repeats a check that an enclosing 'if' already made, and nothing in between can change its value. The inner check was usually meant to test something else.");

    public static readonly DiagnosticDescriptor IdenticalBranches = new(
        id: "TYPO0006",
        title: "Both branches are identical",
        messageFormat: "Both branches of this '{0}' are identical, so the condition '{1}' has no effect",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The 'then' and 'else' parts of an if statement or conditional expression are the same, so the condition does nothing.");

    public static readonly DiagnosticDescriptor DuplicateBranchBody = new(
        id: "TYPO0007",
        title: "Two branches do the same thing",
        messageFormat: "This branch is identical to the branch for '{0}'",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Two cases of a switch, or two branches of an if-else chain, have identical bodies. When the bodies are meant to differ by one value, the copy was usually not edited; otherwise merge the labels.");

    public static readonly DiagnosticDescriptor OverwrittenBeforeUse = new(
        id: "TYPO0008",
        title: "Value overwritten before it is used",
        messageFormat: "'{0}' is assigned again before its previous value is used",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The same field, property or element is assigned twice in a row with nothing reading it in between. The second assignment was usually meant for a different target.");
}
