using Microsoft.CodeAnalysis;

namespace Typo;

internal static class Descriptors
{
    private const string Correctness = "Correctness";
    private const string HelpLinkBase = "https://github.com/unsafePtr/Typo.Analyzers#";

    public static readonly DiagnosticDescriptor IdenticalSubExpressions = new(
        id: "TYPO0001",
        title: "Identical sub-expressions",
        messageFormat: "'{0}' repeats '{1}' in the same '{2}' expression",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The same operand appears twice in a chain of one operator, or on both sides of an operator whose result then becomes constant. One of the copies was usually meant to be edited after copy-paste.",
        helpLinkUri: HelpLinkBase + "typo0001");

    public static readonly DiagnosticDescriptor ComparedWithItself = new(
        id: "TYPO0002",
        title: "Value compared with itself",
        messageFormat: "'{0}' is compared with itself in '{1}'",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A value is passed to its own comparison or string search method, or as both arguments of a comparison, Max or Min, so the call has a fixed result or just returns the value. Comparison methods are often copied and the 'other' side left unedited.",
        helpLinkUri: HelpLinkBase + "typo0002");

    public static readonly DiagnosticDescriptor RepeatedElseIfCondition = new(
        id: "TYPO0003",
        title: "Condition repeated in an if-else chain",
        messageFormat: "Condition '{0}' was already tested by an earlier branch of this if-else chain, so this branch never runs",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An else-if branch repeats a condition that an earlier branch already tested, so it is unreachable.",
        helpLinkUri: HelpLinkBase + "typo0003");

    public static readonly DiagnosticDescriptor RepeatedAdjacentCondition = new(
        id: "TYPO0004",
        title: "'if' statement repeats the condition of an earlier one",
        messageFormat: "Condition '{0}' is identical to the condition of the preceding 'if' statement",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An 'if' statement tests the same condition as the 'if' right before it, or as an earlier 'if' that always returns, throws or jumps away, and nothing in between can change it. Either the later one was meant to test something else, or the two should be merged.",
        helpLinkUri: HelpLinkBase + "typo0004");

    public static readonly DiagnosticDescriptor RepeatedNestedCondition = new(
        id: "TYPO0005",
        title: "Condition already checked by an enclosing 'if'",
        messageFormat: "Condition '{0}' is always {1} here because an enclosing 'if' already checked it",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A nested 'if' repeats a check that an enclosing 'if' already made, and nothing in between can change its value. The inner check was usually meant to test something else.",
        helpLinkUri: HelpLinkBase + "typo0005");

    public static readonly DiagnosticDescriptor IdenticalBranches = new(
        id: "TYPO0006",
        title: "Both branches are identical",
        messageFormat: "Both branches of this '{0}' are identical, so the condition '{1}' has no effect",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The 'then' and 'else' parts of an if statement or conditional expression are the same, so the condition does nothing.",
        helpLinkUri: HelpLinkBase + "typo0006");

    public static readonly DiagnosticDescriptor DuplicateBranchBody = new(
        id: "TYPO0007",
        title: "Two branches do the same thing",
        messageFormat: "This branch is identical to the branch for '{0}'",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Two cases of a switch, or two branches of an if-else chain, have identical bodies. When the bodies are meant to differ by one value, the copy was usually not edited; otherwise merge the labels.",
        helpLinkUri: HelpLinkBase + "typo0007");

    public static readonly DiagnosticDescriptor OverwrittenBeforeUse = new(
        id: "TYPO0008",
        title: "Value overwritten before it is used",
        messageFormat: "'{0}' is assigned again before its previous value is used",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The same field, property or element is assigned twice in a row with nothing reading it in between. The second assignment was usually meant for a different target.",
        helpLinkUri: HelpLinkBase + "typo0008");

    public static readonly DiagnosticDescriptor ConstantIndexInLoop = new(
        id: "TYPO0009",
        title: "Loop always reads the same element",
        messageFormat: "The loop over '{0}' never indexes it with '{1}' and always reads '{2}'",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A for loop iterates over a collection, but its body indexes the collection with a constant and uses the loop variable, if at all, only to index other collections.",
        helpLinkUri: HelpLinkBase + "typo0009");

    public static readonly DiagnosticDescriptor StringCaseMismatch = new(
        id: "TYPO0010",
        title: "Comparison with a string of the wrong case",
        messageFormat: "'{0}' can never match a string converted to {1} case",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A string converted with ToLower or ToUpper is compared with, or searched for, text containing letters of the other case, so the two never match.",
        helpLinkUri: HelpLinkBase + "typo0010");

    public static readonly DiagnosticDescriptor SimilarLineAnomaly = new(
        id: "TYPO0011",
        title: "Probable unedited copy in a sequence of similar lines",
        messageFormat: "'{0}' looks like an unedited copy from a neighbouring line or argument; '{1}' was probably intended",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "In a run of three or more similar statements, declarations, initializer members, conditions or arguments, each item pairs matching names such as X with X and Y with Y, but one item repeats a name from another where a different one was expected. This heuristic is reported as a suggestion.",
        helpLinkUri: HelpLinkBase + "typo0011");

    public static readonly DiagnosticDescriptor SimilarPairAnomaly = new(
        id: "TYPO0012",
        title: "Probable unedited copy in a pair of similar lines",
        messageFormat: "'{0}' looks like an unedited copy from the similar line or argument next to it; '{1}' was probably intended",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Two adjacent similar statements, declarations, initializer members, conditions or arguments rename one word, such as Width to Height, but the second still uses a name built from the first word, as in 'Width == other.Width && Height == other.Width'. This heuristic is reported as a suggestion and can be turned off separately from TYPO0011. The 'TYPO0012.scan' option in .editorconfig limits it to some kinds of items, as in 'TYPO0012.scan = declarations, arguments'.",
        helpLinkUri: HelpLinkBase + "typo0012");

    public static readonly DiagnosticDescriptor RepeatedOperandAcrossComparison = new(
        id: "TYPO0013",
        title: "Comparison operand repeated on the other side",
        messageFormat: "'{0}' is also an operand of '{1}' on the other side of the comparison",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "One side of a comparison is a variable and the other side is a sum, difference or product that contains the same variable, as in 'x == x + 1' or 'x < x * y'. The comparison is constant or can be simplified, and one of the two was usually meant to be a different variable. This heuristic is reported as a suggestion.",
        helpLinkUri: HelpLinkBase + "typo0013");

    public static readonly DiagnosticDescriptor CheckedSourceInsteadOfAssigned = new(
        id: "TYPO0014",
        title: "Condition checks the source of the variable just assigned",
        messageFormat: "'{0}' was just assigned from an expression using '{1}', but the condition tests '{1}' instead of '{0}'",
        category: Correctness,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A variable is assigned a transformed copy of another variable with a similar name, and the next 'if' tests the original instead of the variable just assigned, as in '_value = value < 0 ? 0 : value; if (value == 0)'. This heuristic is reported as a suggestion.",
        helpLinkUri: HelpLinkBase + "typo0014");
}
