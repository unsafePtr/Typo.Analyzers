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
/// Two variables are similar when they have the same type and the same name, ignoring case and a leading underscore or <c>m_</c>.
/// A survey of Nethereum showed that looser name matching only finds deliberate checks of a source object before it is copied.
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

        // Only the stored part counts, so the 'nameof(x)' of a 'x ?? throw' guard is not a transformed use of 'x'.
        var stored = Unwrap(value);
        foreach (var operation in stored.DescendantsAndSelf())
        {
            if (VariableOf(operation) is not { } source
                || SymbolEqualityComparer.Default.Equals(source, target)
                || ReferenceEquals(operation, stored)
                || !conditionReads.Contains(source)
                || !AreSimilar(target, source))
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
    // So does 'value ?? throw …', which stores the source itself or assigns nothing at all.
    private static IOperation Unwrap(IOperation value)
    {
        while (true)
        {
            switch (value)
            {
                case IConversionOperation conversion:
                    value = conversion.Operand;
                    break;

                case ICoalesceOperation coalesce when SkipConversions(coalesce.WhenNull) is IThrowOperation:
                    value = coalesce.Value;
                    break;

                default:
                    return value;
            }
        }
    }

    private static IOperation SkipConversions(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static ISymbol? VariableOf(IOperation operation) => operation switch
    {
        ILocalReferenceOperation { Local.IsConst: false } local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IFieldReferenceOperation { Field: { IsConst: false } field } => field,
        IPropertyReferenceOperation property => property.Property,
        _ => null,
    };

    // Same name apart from the prefix and the same type, as in '_value' and 'value'; 'options' next to a config's '_options' of another type is a different thing.
    private static bool AreSimilar(ISymbol target, ISymbol source) =>
        string.Equals(Normalize(target.Name), Normalize(source.Name), StringComparison.OrdinalIgnoreCase)
        && Normalize(target.Name).Length >= MinimumNameLength
        && SymbolEqualityComparer.Default.Equals(TypeOf(target), TypeOf(source));

    private static ITypeSymbol? TypeOf(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        _ => null,
    };

    private static string Normalize(string name)
    {
        var trimmed = name.TrimStart('_');
        return trimmed.StartsWith("m_", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
    }
}
