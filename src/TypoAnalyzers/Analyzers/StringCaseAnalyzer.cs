using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using TypoAnalyzers.Core;

namespace TypoAnalyzers.Analyzers;

/// <summary>
/// TYPO0010: a value passed through ToLower/ToUpper is compared with, or searched for, text that has letters of the other case.
/// </summary>
/// <remarks>
/// Covers ==, !=, Equals, Contains, StartsWith, EndsWith, IndexOf, LastIndexOf, Replace, 'is' patterns and switch cases.
/// The text side may be a literal, a constant, a property whose getter returns a literal, an interpolated string or a concatenation; only its constant parts are checked, which is enough because each of them must appear verbatim.
/// A local counts as case-normalized when its initializer is ToLower/ToUpper and nothing assigns it again.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StringCaseAnalyzer : DiagnosticAnalyzer
{
    private enum Case
    {
        None,
        Lower,
        Upper,
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.StringCaseMismatch];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            var purity = new PurityOracle(start.Compilation);
            start.RegisterOperationAction(context => AnalyzeBinary(context, purity), OperationKind.Binary);
            start.RegisterOperationAction(context => AnalyzeInvocation(context, purity), OperationKind.Invocation);
            start.RegisterOperationAction(context => AnalyzeIsPattern(context, purity), OperationKind.IsPattern);
            start.RegisterOperationAction(context => AnalyzeSwitch(context, purity), OperationKind.Switch);
            start.RegisterOperationAction(context => AnalyzeSwitchExpression(context, purity), OperationKind.SwitchExpression);
        });
    }

    private static void AnalyzeBinary(OperationAnalysisContext context, PurityOracle purity)
    {
        var binary = (IBinaryOperation)context.Operation;
        if (binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
            && binary.LeftOperand.Type?.SpecialType == SpecialType.System_String)
        {
            CheckPair(context, binary.LeftOperand, binary.RightOperand, purity);
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, PurityOracle purity)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.ContainingType.SpecialType != SpecialType.System_String || IgnoresCase(invocation))
        {
            return;
        }

        // Every static string.Equals overload takes two strings and every listed instance method takes the searched text first.
        if (method.IsStatic)
        {
            if (method.Name == "Equals")
            {
                CheckPair(context, ArgumentAt(invocation, 0), ArgumentAt(invocation, 1), purity);
            }

            return;
        }

        if (method.Name is "Equals" or "Contains" or "StartsWith" or "EndsWith" or "IndexOf" or "LastIndexOf" or "Replace"
            && CaseOf(invocation.Instance, purity) is var target and not Case.None)
        {
            CheckText(context, ArgumentAt(invocation, 0), target, purity);
        }
    }

    private static void AnalyzeIsPattern(OperationAnalysisContext context, PurityOracle purity)
    {
        var isPattern = (IIsPatternOperation)context.Operation;
        CheckPatterns(context, isPattern.Value, isPattern.Pattern, purity);
    }

    private static void AnalyzeSwitch(OperationAnalysisContext context, PurityOracle purity)
    {
        var switchOperation = (ISwitchOperation)context.Operation;
        if (CaseOf(switchOperation.Value, purity) is var target and not Case.None)
        {
            foreach (var section in switchOperation.Cases)
            {
                foreach (var clause in section.Clauses)
                {
                    switch (clause)
                    {
                        case ISingleValueCaseClauseOperation single:
                            CheckConstant(context, single.Value, target);
                            break;

                        case IPatternCaseClauseOperation pattern:
                            CheckConstantPatterns(context, pattern.Pattern, target);
                            break;
                    }
                }
            }
        }
    }

    private static void AnalyzeSwitchExpression(OperationAnalysisContext context, PurityOracle purity)
    {
        var switchExpression = (ISwitchExpressionOperation)context.Operation;
        if (CaseOf(switchExpression.Value, purity) is var target and not Case.None)
        {
            foreach (var arm in switchExpression.Arms)
            {
                CheckConstantPatterns(context, arm.Pattern, target);
            }
        }
    }

    private static void CheckPatterns(OperationAnalysisContext context, IOperation value, IPatternOperation pattern, PurityOracle purity)
    {
        if (CaseOf(value, purity) is var target and not Case.None)
        {
            CheckConstantPatterns(context, pattern, target);
        }
    }

    private static void CheckConstantPatterns(OperationAnalysisContext context, IPatternOperation pattern, Case target)
    {
        foreach (var operation in pattern.DescendantsAndSelf())
        {
            if (operation is IConstantPatternOperation constant)
            {
                CheckConstant(context, constant.Value, target);
            }
        }
    }

    private static void CheckPair(OperationAnalysisContext context, IOperation left, IOperation right, PurityOracle purity)
    {
        if (CaseOf(left, purity) is var leftCase and not Case.None)
        {
            CheckText(context, right, leftCase, purity);
        }
        else if (CaseOf(right, purity) is var rightCase and not Case.None)
        {
            CheckText(context, left, rightCase, purity);
        }
    }

    private static void CheckText(OperationAnalysisContext context, IOperation text, Case target, PurityOracle purity)
    {
        if (HasConflictingLetter(text, target) || CaseOf(text, purity) == Opposite(target))
        {
            Report(context, text, target);
        }
    }

    private static void CheckConstant(OperationAnalysisContext context, IOperation value, Case target)
    {
        if (HasConflictingLetter(value, target))
        {
            Report(context, value, target);
        }
    }

    private static Case CaseOf(IOperation? operation, PurityOracle purity, bool followLocals = true)
    {
        operation = SkipImplicitConversions(operation);
        switch (operation)
        {
            case IInvocationOperation { TargetMethod.ContainingType.SpecialType: SpecialType.System_String } invocation:
                return invocation.TargetMethod.Name switch
                {
                    "ToLower" or "ToLowerInvariant" => Case.Lower,
                    "ToUpper" or "ToUpperInvariant" => Case.Upper,
                    _ => Case.None,
                };

            case IConditionalAccessOperation access:
                return CaseOf(access.WhenNotNull, purity, followLocals);

            case ILocalReferenceOperation local when followLocals:
                return CaseOfLocal(local, purity);

            default:
                return Case.None;
        }
    }

    // Operations handed to analyzers always carry their semantic model, and a source local has exactly one declaration.
    private static Case CaseOfLocal(ILocalReferenceOperation reference, PurityOracle purity)
    {
        if (reference.Local.DeclaringSyntaxReferences[0].GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } initializer }
            || reference.SemanticModel!.GetOperation(initializer) is not { } value)
        {
            return Case.None;
        }

        var initialCase = CaseOf(value, purity, followLocals: false);
        if (initialCase == Case.None)
        {
            return Case.None;
        }

        var root = (IOperation)reference;
        while (root.Parent is not null)
        {
            root = root.Parent;
        }

        return ReadSet.Of(reference).MayBeChangedBy(root, purity) ? Case.None : initialCase;
    }

    private static bool HasConflictingLetter(IOperation operation, Case target, int depth = 0)
    {
        if (!Limits.CanRecurse(depth))
        {
            return false;
        }

        operation = SkipImplicitConversions(operation)!;
        switch (operation)
        {
            case { ConstantValue: { HasValue: true, Value: string text } }:
                return HasConflictingLetter(text, target);

            case { ConstantValue: { HasValue: true, Value: char character } }:
                return Conflicts(character, target);

            case IInterpolatedStringOperation interpolated:
                foreach (var part in interpolated.Parts)
                {
                    if (part is IInterpolatedStringTextOperation { Text.ConstantValue: { HasValue: true, Value: string piece } }
                        && HasConflictingLetter(piece, target))
                    {
                        return true;
                    }
                }

                return false;

            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } concatenation:
                return HasConflictingLetter(concatenation.LeftOperand, target, depth + 1) || HasConflictingLetter(concatenation.RightOperand, target, depth + 1);

            case IPropertyReferenceOperation property when LiteralReturnedBy(property.Property) is { } text:
                return HasConflictingLetter(text, target);

            default:
                return false;
        }
    }

    // A getter that only returns a literal, as in 'LocalResourceDirectory => "App_LocalResources"', is a constant unless an override can replace it.
    private static string? LiteralReturnedBy(IPropertySymbol property)
    {
        if (property.IsVirtual || property.IsAbstract || property.IsOverride || property.GetMethod is not { DeclaringSyntaxReferences.Length: 1 } getter)
        {
            return null;
        }

        var returned = getter.DeclaringSyntaxReferences[0].GetSyntax() switch
        {
            ArrowExpressionClauseSyntax arrow => arrow.Expression,
            AccessorDeclarationSyntax { ExpressionBody: { } arrow } => arrow.Expression,
            AccessorDeclarationSyntax { Body.Statements: [ReturnStatementSyntax { Expression: { } expression }] } => expression,
            _ => null,
        };
        return returned is LiteralExpressionSyntax { Token.Value: string text } ? text : null;
    }

    private static bool HasConflictingLetter(string text, Case target)
    {
        foreach (var character in text)
        {
            if (Conflicts(character, target))
            {
                return true;
            }
        }

        return false;
    }

    // A letter only conflicts when it has a mapping to the target case; letters such as 'ß' have no single-character upper case.
    // Invariant mappings keep the verdict independent of the culture the compiler runs under.
    private static bool Conflicts(char character, Case target) => target == Case.Lower
        ? char.IsUpper(character) && char.ToLowerInvariant(character) != character
        : char.IsLower(character) && char.ToUpperInvariant(character) != character;

    // A CultureInfo argument alone keeps the comparison case-sensitive; only a StringComparison or the ignoreCase flag can change that.
    private static bool IgnoresCase(IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            switch (argument.Parameter?.Type)
            {
                case { Name: "StringComparison", ContainingNamespace.Name: "System" }:
                    // CurrentCultureIgnoreCase, InvariantCultureIgnoreCase and OrdinalIgnoreCase are the odd values; a non-constant comparison is unknown.
                    if (argument.Value.ConstantValue is not { HasValue: true, Value: int comparison } || comparison % 2 == 1)
                    {
                        return true;
                    }

                    break;

                case { SpecialType: SpecialType.System_Boolean } when argument.Value.ConstantValue is not { HasValue: true, Value: false }:
                    return true;
            }
        }

        return false;
    }

    // A bound invocation carries one argument per parameter, so the lookup always succeeds.
    private static IOperation ArgumentAt(IInvocationOperation invocation, int ordinal) =>
        invocation.Arguments.First(argument => argument.Parameter?.Ordinal == ordinal).Value;

    private static IOperation? SkipImplicitConversions(IOperation? operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static Case Opposite(Case value) => value == Case.Lower ? Case.Upper : Case.Lower;

    private static void Report(OperationAnalysisContext context, IOperation text, Case target) =>
        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.StringCaseMismatch,
            text.Syntax.GetLocation(),
            messageArgs: [text.Syntax.ToString(), target == Case.Lower ? "lower" : "upper"]));
}
