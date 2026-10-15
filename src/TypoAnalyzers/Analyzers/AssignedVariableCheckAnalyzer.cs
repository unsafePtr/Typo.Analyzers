using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using TypoAnalyzers.Core;

namespace TypoAnalyzers.Analyzers;

/// <summary>
/// TYPO0014: a variable assigned a transformed copy of a similarly named one, followed by an <c>if</c> that tests the original, as in <c>_value = value &lt; 0 ? 0 : value; if (value == 0)</c>.
/// </summary>
/// <remarks>
/// Only the statement right before the <c>if</c> counts, and a plain copy such as <c>_name = name; if (name == null)</c> is left alone because both names hold the same value.
/// Two names are similar when, ignoring case and a leading underscore or <c>m_</c>, one contains the other and the shorter has at least three characters.
/// An earlier check of a differently assigned variable, as in <c>user2 = Find(id2); if (user1 == null)</c>, is TYPO0004's.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AssignedVariableCheckAnalyzer : DiagnosticAnalyzer
{
    private const int MinimumNameLength = 3;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.CheckedSourceInsteadOfAssigned];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(AnalyzeBlock, OperationKind.Block);
    }

    private static void AnalyzeBlock(OperationAnalysisContext context)
    {
        var operations = ((IBlockOperation)context.Operation).Operations;
        for (var i = 0; i < operations.Length - 1; i++)
        {
            if (operations[i + 1] is IConditionalOperation { Syntax: IfStatementSyntax } check
                && TryGetAssignment(operations[i], out var target, out var value))
            {
                Analyze(context, target, value, check.Condition);
            }
        }
    }

    private static bool TryGetAssignment(IOperation statement, out ISymbol target, out IOperation value)
    {
        switch (statement)
        {
            case IExpressionStatementOperation { Operation: ISimpleAssignmentOperation assignment } when VariableOf(assignment.Target) is { } symbol:
                target = symbol;
                value = assignment.Value;
                return true;

            case IVariableDeclarationGroupOperation { Declarations: [{ Declarators: [{ Initializer.Value: { } initializer } declarator] }] }:
                target = declarator.Symbol;
                value = initializer;
                return true;

            default:
                target = null!;
                value = null!;
                return false;
        }
    }

    private static void Analyze(OperationAnalysisContext context, ISymbol target, IOperation value, IOperation condition)
    {
        var conditionReads = ReadSet.Of(condition);
        if (conditionReads.Contains(target))
        {
            return;
        }

        var unwrapped = Unwrap(value);
        foreach (var operation in value.DescendantsAndSelf())
        {
            if (VariableOf(operation) is not { } source
                || SymbolEqualityComparer.Default.Equals(source, target)
                || ReferenceEquals(operation, unwrapped)
                || !conditionReads.Contains(source)
                || !AreSimilar(target.Name, source.Name))
            {
                continue;
            }

            foreach (var read in condition.DescendantsAndSelf())
            {
                if (SymbolEqualityComparer.Default.Equals(VariableOf(read), source))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.CheckedSourceInsteadOfAssigned,
                        read.Syntax.GetLocation(),
                        messageArgs: [target.Name, source.Name]));
                    return;
                }
            }
        }
    }

    // A plain or converted copy holds the same value as its source, so testing either one is equivalent.
    private static IOperation Unwrap(IOperation value)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        return value;
    }

    private static ISymbol? VariableOf(IOperation operation) => operation switch
    {
        ILocalReferenceOperation { Local.IsConst: false } local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IFieldReferenceOperation { Field: { IsConst: false } field } => field,
        IPropertyReferenceOperation property => property.Property,
        _ => null,
    };

    private static bool AreSimilar(string first, string second)
    {
        var x = Normalize(first);
        var y = Normalize(second);
        var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
        return shorter.Length >= MinimumNameLength && longer.Contains(shorter, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string name)
    {
        var trimmed = name.TrimStart('_');
        return trimmed.StartsWith("m_", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
    }
}
