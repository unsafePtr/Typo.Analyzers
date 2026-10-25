using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Typo.Core;

namespace Typo.Analyzers;

/// <summary>
/// TYPO0001: a repeated operand in a <c>||</c>, <c>&amp;&amp;</c>, <c>|</c>, <c>&amp;</c>, <c>^</c>, <c>or</c> or <c>and</c> chain, or identical operands of a comparison, subtraction, division or remainder.
/// </summary>
/// <remarks>
/// Chains are flattened first because the left-associative tree hides <c>a || b || a</c> from a plain left-versus-right comparison.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IdenticalOperandsAnalyzer : DiagnosticAnalyzer
{
    [ThreadStatic]
    private static List<int>? t_hashes;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.IdenticalSubExpressions];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            var purity = PurityOracle.For(start.Compilation);
            start.RegisterOperationAction(context => AnalyzeBinary(context, purity), OperationKind.Binary);
            start.RegisterOperationAction(AnalyzeBinaryPattern, OperationKind.BinaryPattern);
        });
    }

    private static void AnalyzeBinary(OperationAnalysisContext context, PurityOracle purity)
    {
        var binary = (IBinaryOperation)context.Operation;
        if (IsChainOperator(binary.OperatorKind))
        {
            if (binary.OperatorMethod is null && OperandChain.IsRoot(binary))
            {
                AnalyzeChain(context, binary, purity);
            }
        }
        else if (IsConstantWhenOperandsMatch(binary, context.Compilation)
            && purity.IsFree(binary.LeftOperand)
            && OperationEquivalence.AreEquivalent(binary.LeftOperand, binary.RightOperand)
            && !CompilerWarns(binary))
        {
            Report(context, binary.RightOperand, binary.LeftOperand, OperatorText(binary));
        }
    }

    private static void AnalyzeChain(OperationAnalysisContext context, IBinaryOperation root, PurityOracle purity)
    {
        var operands = OperandChain.Rent();
        var hashes = RentHashes();
        try
        {
            OperandChain.Flatten(root, operands);
            foreach (var operand in operands)
            {
                hashes.Add(OperationEquivalence.GetHashCode(operand));
            }

            for (var i = 1; i < operands.Count; i++)
            {
                var candidate = operands[i];
                if (!purity.IsFree(candidate))
                {
                    continue;
                }

                for (var j = 0; j < i; j++)
                {
                    if (hashes[j] == hashes[i]
                        && OperationEquivalence.AreEquivalent(operands[j], candidate)
                        && !ChangedBetween(operands, j, i, purity))
                    {
                        Report(context, candidate, operands[j], OperatorText(root));
                        break;
                    }
                }
            }
        }
        finally
        {
            ReturnHashes(hashes);
            OperandChain.Return(operands);
        }
    }

    // In 't.IsCompleted || await Task.WhenAny(t, u) == t || t.IsCompleted' the await lets the second read see a different value.
    // On a Utf8JsonReader, '!r.Read() || r.TokenType != X || !r.Read() || r.TokenType != X' moves the struct between the reads.
    // On a class only MoveNext and Stream calls count, so 'p.Size == 4 && p.Align() > 4 && p.Size == 4' is a real bug.
    private static bool ChangedBetween(List<IOperation> operands, int first, int second, PurityOracle purity)
    {
        if (second - first < 2)
        {
            return false;
        }

        var reads = ReadSet.Of(operands[second]);
        for (var k = first + 1; k < second; k++)
        {
            if (reads.IsWrittenBy(operands[k], purity))
            {
                return true;
            }

            if (!reads.ReadsState)
            {
                continue;
            }

            foreach (var operation in operands[k].DescendantsAndSelf())
            {
                if (operation is IAwaitOperation)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AnalyzeBinaryPattern(OperationAnalysisContext context)
    {
        var root = (IBinaryPatternOperation)context.Operation;
        if (!OperandChain.IsRoot(root))
        {
            return;
        }

        var operands = OperandChain.Rent();
        var hashes = RentHashes();
        try
        {
            OperandChain.Flatten(root, operands);
            foreach (var operand in operands)
            {
                hashes.Add(OperationEquivalence.GetPatternHashCode((IPatternOperation)operand));
            }

            for (var i = 1; i < operands.Count; i++)
            {
                var candidate = (IPatternOperation)operands[i];
                for (var j = 0; j < i; j++)
                {
                    if (hashes[j] == hashes[i] && OperationEquivalence.PatternsEquivalent((IPatternOperation)operands[j], candidate))
                    {
                        Report(context, candidate, operands[j], root.OperatorKind == BinaryOperatorKind.Or ? "or" : "and");
                        break;
                    }
                }
            }
        }
        finally
        {
            ReturnHashes(hashes);
            OperandChain.Return(operands);
        }
    }

    private static List<int> RentHashes()
    {
        var hashes = t_hashes ?? new List<int>(8);
        t_hashes = null;
        return hashes;
    }

    private static void ReturnHashes(List<int> hashes)
    {
        hashes.Clear();
        t_hashes = hashes;
    }

    private static bool IsChainOperator(BinaryOperatorKind kind) => kind
        is BinaryOperatorKind.ConditionalOr
        or BinaryOperatorKind.ConditionalAnd
        or BinaryOperatorKind.Or
        or BinaryOperatorKind.And
        or BinaryOperatorKind.ExclusiveOr;

    private static bool IsConstantWhenOperandsMatch(IBinaryOperation binary, Compilation compilation)
    {
        switch (binary.OperatorKind)
        {
            case BinaryOperatorKind.Equals:
            case BinaryOperatorKind.NotEquals:
                // 'x != x' is the classic NaN test, on every IEEE type, their nullables and generic numbers that may be one.
                if (IsFloatingPoint(binary.LeftOperand.Type, compilation))
                {
                    return false;
                }

                break;

            case BinaryOperatorKind.LessThan:
            case BinaryOperatorKind.LessThanOrEqual:
            case BinaryOperatorKind.GreaterThan:
            case BinaryOperatorKind.GreaterThanOrEqual:
            case BinaryOperatorKind.Subtract:
            case BinaryOperatorKind.Divide:
            case BinaryOperatorKind.Remainder:
                break;

            default:
                return false;
        }

        // Tests legitimately check reflexivity of user-defined operators, for example 'Assert.True(money == money)'.
        return binary.OperatorMethod is null || Framework.IsFrameworkValue(binary.OperatorMethod.ContainingType);
    }

    // The compiler's CS1718 already reports a comparison of a local, parameter or field with itself, on every comparison operator and type.
    // A project that turns CS1718 off, with a pragma or in its settings, has said the comparison is meant.
    private static bool CompilerWarns(IBinaryOperation binary) =>
        binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
            or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
            or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual
        && IsPlainVariable(binary.LeftOperand);

    private static bool IsPlainVariable(IOperation operand) => SkipImplicitConversions(operand) switch
    {
        ILocalReferenceOperation or IParameterReferenceOperation => true,
        IFieldReferenceOperation { Instance: null or IInstanceReferenceOperation or ILocalReferenceOperation or IParameterReferenceOperation } => true,
        _ => false,
    };

    private static IOperation SkipImplicitConversions(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    // Half, NFloat and later IEEE types are recognised by IFloatingPointIeee754<TSelf>; float and double are named because targets before .NET 7 lack it.
    private static bool IsFloatingPoint(ITypeSymbol? type, Compilation compilation) => type switch
    {
        null => false,
        INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable => IsFloatingPoint(nullable.TypeArguments[0], compilation),
        { SpecialType: SpecialType.System_Single or SpecialType.System_Double } => true,
        { SpecialType: not SpecialType.None } => false,
        ITypeParameterSymbol parameter => MayHoldDouble(parameter, compilation),
        _ => IsIeee754(type),
    };

    private static bool IsIeee754(ITypeSymbol type)
    {
        foreach (var contract in type.AllInterfaces)
        {
            if (contract is { Name: "IFloatingPointIeee754", ContainingNamespace: { Name: "Numerics", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } })
            {
                return true;
            }
        }

        return false;
    }

    // Double meets INumber<T> but not IBinaryInteger<T> or 'class'; T : U is decided by U, which is not followed, so it never rules double out.
    private static bool MayHoldDouble(ITypeParameterSymbol parameter, Compilation compilation)
    {
        if (parameter.HasReferenceTypeConstraint)
        {
            return false;
        }

        var @double = compilation.GetSpecialType(SpecialType.System_Double);
        foreach (var constraint in parameter.ConstraintTypes)
        {
            if (constraint is not ITypeParameterSymbol
                && (constraint.TypeKind != TypeKind.Interface || !Implements(@double, constraint.OriginalDefinition)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Implements(ITypeSymbol type, ITypeSymbol contract)
    {
        foreach (var candidate in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, contract))
            {
                return true;
            }
        }

        return false;
    }

    // In C# every IBinaryOperation is bound from a BinaryExpressionSyntax; tuple equality and patterns have operations of their own.
    private static string OperatorText(IBinaryOperation binary) => ((BinaryExpressionSyntax)binary.Syntax).OperatorToken.Text;

    private static void Report(OperationAnalysisContext context, IOperation duplicate, IOperation original, string operatorText) =>
        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.IdenticalSubExpressions,
            duplicate.Syntax.GetLocation(),
            additionalLocations: [original.Syntax.GetLocation()],
            messageArgs: [Quote.Of(duplicate.Syntax), Quote.Of(original.Syntax), operatorText]));
}
