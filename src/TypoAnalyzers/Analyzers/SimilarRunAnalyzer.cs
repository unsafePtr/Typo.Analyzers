using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using TypoAnalyzers.Core;

namespace TypoAnalyzers.Analyzers;

/// <summary>
/// TYPO0011: in three or more consecutive similar lines, one line repeats a name from another line where the pattern calls for a different one, as in <c>Z += other.Y</c> after <c>X += other.X; Y += other.Y;</c>.
/// </summary>
/// <remarks>
/// Each line becomes a shape hash plus its slots (symbol names and literals); consecutive lines with the same shape form a run and their slots form columns.
/// A key column is one whose fragments (names minus the column's shared prefix and suffix) are all different, so they identify each line.
/// A second column that matches the key on every line but one, and repeats another line's fragment on that line, is reported.
/// The suggested name must exist in scope, which suppresses the classic false positive <c>halfWidth -= borderWidth; halfHeight -= borderWidth;</c> when there is no borderHeight.
/// Runs are scanned in statement blocks, object initializers and &amp;&amp;/|| chains.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SimilarRunAnalyzer : DiagnosticAnalyzer
{
    private const int MinimumRun = 3;

    // Exact first, so the partial modes only add findings such as minX/maxY where key names wrap the other fragment.
    private static readonly Match[] s_modes = [Match.Exact, Match.KeyEndsWith, Match.KeyStartsWith];

    private enum Match
    {
        Exact,
        KeyEndsWith,
        KeyStartsWith,
    }

    [ThreadStatic]
    private static List<Row>? t_rows;

    [ThreadStatic]
    private static List<Slot>? t_slots;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.SimilarLineAnomaly];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(AnalyzeBlock, OperationKind.Block);
        context.RegisterOperationAction(AnalyzeInitializer, OperationKind.ObjectOrCollectionInitializer);
        context.RegisterOperationAction(AnalyzeChain, OperationKind.Binary);
    }

    private static void AnalyzeBlock(OperationAnalysisContext context)
    {
        var items = OperandChain.Rent();
        try
        {
            foreach (var operation in ((IBlockOperation)context.Operation).Operations)
            {
                if (operation is IExpressionStatementOperation statement)
                {
                    items.Add(statement.Operation);
                    continue;
                }

                AnalyzeItems(context, items);
                items.Clear();
            }

            AnalyzeItems(context, items);
        }
        finally
        {
            OperandChain.Return(items);
        }
    }

    private static void AnalyzeInitializer(OperationAnalysisContext context)
    {
        var items = OperandChain.Rent();
        try
        {
            foreach (var initializer in ((IObjectOrCollectionInitializerOperation)context.Operation).Initializers)
            {
                if (initializer is ISimpleAssignmentOperation)
                {
                    items.Add(initializer);
                    continue;
                }

                AnalyzeItems(context, items);
                items.Clear();
            }

            AnalyzeItems(context, items);
        }
        finally
        {
            OperandChain.Return(items);
        }
    }

    private static void AnalyzeChain(OperationAnalysisContext context)
    {
        var binary = (IBinaryOperation)context.Operation;
        if (binary.OperatorKind is not (BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
            || binary.OperatorMethod is not null
            || !OperandChain.IsRoot(binary))
        {
            return;
        }

        var items = OperandChain.Rent();
        try
        {
            OperandChain.Flatten(binary, items);
            AnalyzeItems(context, items);
        }
        finally
        {
            OperandChain.Return(items);
        }
    }

    private static void AnalyzeItems(OperationAnalysisContext context, List<IOperation> items)
    {
        if (items.Count < MinimumRun)
        {
            return;
        }

        var rows = t_rows ??= new List<Row>(16);
        var slots = t_slots ??= new List<Slot>(64);
        try
        {
            foreach (var item in items)
            {
                var start = slots.Count;
                var hash = 17;
                rows.Add(Describe(item, ref hash, slots, 0) ? new Row(hash, start, slots.Count - start) : new Row(rows.Count, start, -1));
            }

            for (var first = 0; first < rows.Count;)
            {
                var last = first;
                while (last + 1 < rows.Count && rows[last + 1].Hash == rows[first].Hash && rows[last + 1].Count == rows[first].Count)
                {
                    last++;
                }

                if (last - first + 1 >= MinimumRun && rows[first].Count >= 2)
                {
                    AnalyzeRun(context, rows, slots, first, last - first + 1);
                }

                first = last + 1;
            }
        }
        finally
        {
            rows.Clear();
            slots.Clear();
        }
    }

    private static void AnalyzeRun(OperationAnalysisContext context, List<Row> rows, List<Slot> slots, int first, int count)
    {
        var columns = rows[first].Count;
        var names = new string[count, columns];
        for (var r = 0; r < count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                names[r, c] = NameOf(slots[rows[first + r].Start + c]);
            }
        }

        var affixes = new (int Prefix, int Suffix)?[columns];
        for (var c = 0; c < columns; c++)
        {
            affixes[c] = HasData(rows, slots, first, count, c) ? null : Affixes(names, count, c);
        }

        for (var key = 0; key < columns; key++)
        {
            if (affixes[key] is not { } keyAffix)
            {
                continue;
            }

            // Stripping the shared prefix of Value, Value1, Value2 leaves an empty fragment, so such keys are compared by full name instead.
            var fullNames = false;
            if (!AllDistinct(names, count, key, keyAffix))
            {
                if (!AllDistinct(names, count, key, (0, 0)))
                {
                    continue;
                }

                fullNames = true;
                keyAffix = (0, 0);
            }

            for (var other = 0; other < columns; other++)
            {
                if (other == key || affixes[other] is not { } computedAffix)
                {
                    continue;
                }

                (int Prefix, int Suffix) otherAffix = fullNames ? (0, 0) : computedAffix;

                foreach (var mode in s_modes)
                {
                    if (!TryFindOddLine(names, count, key, keyAffix, other, otherAffix, mode, out var odd, out var expectedFragment)
                        || !RepeatsAnotherLine(names, count, other, otherAffix, odd))
                    {
                        continue;
                    }

                    var oddName = names[odd, other];
                    var sample = Fragment(names[odd == 0 ? 1 : 0, other], otherAffix);
                    var expected = oddName.Substring(0, otherAffix.Prefix)
                        + Restyle(expectedFragment, sample)
                        + oddName.Substring(oddName.Length - otherAffix.Suffix);

                    var slot = slots[rows[first + odd].Start + other];
                    if (Exists(slot, expected))
                    {
                        Report(context, slot, oddName, expected);
                        return;
                    }
                }
            }
        }
    }

    // Finds the single line where the other column's fragment does not match the key, and the fragment that line should have had.
    private static bool TryFindOddLine(
        string[,] names,
        int count,
        int key,
        (int Prefix, int Suffix) keyAffix,
        int other,
        (int Prefix, int Suffix) otherAffix,
        Match mode,
        out int odd,
        out string expected)
    {
        odd = -1;
        expected = string.Empty;
        for (var r = 0; r < count; r++)
        {
            if (!Matches(Fragment(names[r, key], keyAffix), Fragment(names[r, other], otherAffix), mode))
            {
                if (odd >= 0)
                {
                    return false;
                }

                odd = r;
            }
        }

        if (odd < 0)
        {
            return false;
        }

        var oddKey = Fragment(names[odd, key], keyAffix);
        if (mode == Match.Exact)
        {
            expected = oddKey.ToString();
            return true;
        }

        // Matching lines show the stem each key adds around the other fragment, such as 'min' in minX/X; stripping a known stem from the odd key leaves the expected fragment.
        for (var r = 0; r < count; r++)
        {
            if (r == odd)
            {
                continue;
            }

            var keyFragment = Fragment(names[r, key], keyAffix);
            var otherLength = Fragment(names[r, other], otherAffix).Length;
            if (mode == Match.KeyEndsWith)
            {
                var stem = keyFragment.Slice(0, keyFragment.Length - otherLength);
                if (oddKey.Length > stem.Length && oddKey.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                {
                    expected = oddKey.Slice(stem.Length).ToString();
                    return true;
                }
            }
            else
            {
                var stem = keyFragment.Slice(otherLength);
                if (oddKey.Length > stem.Length && oddKey.EndsWith(stem, StringComparison.OrdinalIgnoreCase))
                {
                    expected = oddKey.Slice(0, oddKey.Length - stem.Length).ToString();
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Matches(ReadOnlySpan<char> keyFragment, ReadOnlySpan<char> otherFragment, Match mode) => mode switch
    {
        Match.Exact => keyFragment.Equals(otherFragment, StringComparison.OrdinalIgnoreCase),
        Match.KeyEndsWith => otherFragment.Length > 0 && keyFragment.Length > otherFragment.Length && keyFragment.EndsWith(otherFragment, StringComparison.OrdinalIgnoreCase),
        _ => otherFragment.Length > 0 && keyFragment.Length > otherFragment.Length && keyFragment.StartsWith(otherFragment, StringComparison.OrdinalIgnoreCase),
    };

    // Returns false when the tree is too deep to describe; such a line never joins a run.
    private static bool Describe(IOperation operation, ref int hash, List<Slot> slots, int depth)
    {
        if (!Limits.CanRecurse(depth))
        {
            return false;
        }

        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        var next = depth + 1;
        hash = Mix(hash, (int)operation.Kind);
        switch (operation)
        {
            case ILocalReferenceOperation local:
                slots.Add(new Slot(operation, local.Local, null, IsData: false));
                return true;

            case IParameterReferenceOperation parameter:
                slots.Add(new Slot(operation, parameter.Parameter, null, IsData: false));
                return true;

            case ILiteralOperation literal:
                slots.Add(new Slot(operation, null, literal.ConstantValue.Value, IsData(literal)));
                return true;

            case IInstanceReferenceOperation:
                return true;

            case IFieldReferenceOperation field:
                if (!DescribeOptional(field.Instance, ref hash, slots, next))
                {
                    return false;
                }

                slots.Add(new Slot(operation, field.Field, null, IsData: false));
                return true;

            case IPropertyReferenceOperation property:
                if (!DescribeOptional(property.Instance, ref hash, slots, next))
                {
                    return false;
                }

                foreach (var argument in property.Arguments)
                {
                    if (!Describe(argument.Value, ref hash, slots, next))
                    {
                        return false;
                    }
                }

                slots.Add(new Slot(operation, property.Property, null, IsData: false));
                return true;

            case IInvocationOperation invocation:
                if (!DescribeOptional(invocation.Instance, ref hash, slots, next))
                {
                    return false;
                }

                hash = Mix(hash, invocation.Arguments.Length);
                slots.Add(new Slot(operation, invocation.TargetMethod, null, IsData: false));
                foreach (var argument in invocation.Arguments)
                {
                    if (!Describe(argument.Value, ref hash, slots, next))
                    {
                        return false;
                    }
                }

                return true;

            case IBinaryOperation binary:
                hash = Mix(hash, (int)binary.OperatorKind);
                break;

            case ICompoundAssignmentOperation compound:
                hash = Mix(hash, (int)compound.OperatorKind);
                break;

            case IUnaryOperation unary:
                hash = Mix(hash, (int)unary.OperatorKind);
                break;
        }

        foreach (var child in operation.ChildOperations)
        {
            if (!Describe(child, ref hash, slots, next))
            {
                return false;
            }
        }

        hash = Mix(hash, -1);
        return true;
    }

    private static bool DescribeOptional(IOperation? operation, ref int hash, List<Slot> slots, int depth)
    {
        if (operation is null)
        {
            hash = Mix(hash, 0);
            return true;
        }

        return Describe(operation, ref hash, slots, depth);
    }

    // Numeric literals are names only when they index something, as in dst[2] = src[1]; elsewhere, as in bits[2] = 1, they are table data where repeats are normal.
    private static bool IsData(ILiteralOperation literal)
    {
        if (literal.ConstantValue.Value is string or char or null)
        {
            return false;
        }

        var parent = literal.Parent;
        while (parent is IConversionOperation { IsImplicit: true })
        {
            parent = parent.Parent;
        }

        return parent is not (IArrayElementReferenceOperation or IArgumentOperation { Parent: IPropertyReferenceOperation { Property.IsIndexer: true } });
    }

    private static bool HasData(List<Row> rows, List<Slot> slots, int first, int count, int column)
    {
        for (var r = 0; r < count; r++)
        {
            if (slots[rows[first + r].Start + column].IsData)
            {
                return true;
            }
        }

        return false;
    }

    private static (int Prefix, int Suffix)? Affixes(string[,] names, int count, int column)
    {
        var reference = names[0, column];
        var prefix = reference.Length;
        var suffix = reference.Length;
        var shortest = reference.Length;
        var constant = true;
        for (var r = 1; r < count; r++)
        {
            var name = names[r, column];
            constant &= name == reference;
            shortest = Math.Min(shortest, name.Length);
            prefix = Math.Min(prefix, CommonPrefix(reference, name));
            suffix = Math.Min(suffix, CommonSuffix(reference, name));
        }

        if (constant)
        {
            return null;
        }

        return (prefix, Math.Min(suffix, shortest - prefix));
    }

    private static bool AllDistinct(string[,] names, int count, int column, (int Prefix, int Suffix) affix)
    {
        for (var r = 0; r < count; r++)
        {
            var fragment = Fragment(names[r, column], affix);
            if (fragment.IsEmpty)
            {
                return false;
            }

            for (var s = 0; s < r; s++)
            {
                if (fragment.Equals(Fragment(names[s, column], affix), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool RepeatsAnotherLine(string[,] names, int count, int column, (int Prefix, int Suffix) affix, int row)
    {
        var fragment = Fragment(names[row, column], affix);
        for (var r = 0; r < count; r++)
        {
            if (r != row && fragment.Equals(Fragment(names[r, column], affix), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlySpan<char> Fragment(string name, (int Prefix, int Suffix) affix) =>
        name.AsSpan(affix.Prefix, name.Length - affix.Prefix - affix.Suffix);

    // Both are non-empty: key fragments are checked non-empty, and the sample comes from a line that matched the key.
    // Invariant casing keeps suggestions such as 'items' from becoming 'ıtems' under a Turkish culture.
    private static string Restyle(string fragment, ReadOnlySpan<char> sample)
    {
        if (char.IsUpper(sample[0]) && char.IsLower(fragment[0]))
        {
            return char.ToUpperInvariant(fragment[0]) + fragment.Substring(1);
        }

        if (char.IsLower(sample[0]) && char.IsUpper(fragment[0]))
        {
            return char.ToLowerInvariant(fragment[0]) + fragment.Substring(1);
        }

        return fragment;
    }

    private static bool Exists(Slot slot, string expected)
    {
        switch (slot.Symbol)
        {
            case null:
                return true;

            case ILocalSymbol or IParameterSymbol:
                return slot.Operation.SemanticModel is { } model
                    && !model.LookupSymbols(slot.Operation.Syntax.SpanStart, name: expected).IsEmpty;

            default:
                for (var type = slot.Symbol.ContainingType; type is not null; type = type.BaseType)
                {
                    if (!type.GetMembers(expected).IsEmpty)
                    {
                        return true;
                    }
                }

                return false;
        }
    }

    private static void Report(OperationAnalysisContext context, Slot slot, string oddName, string expected)
    {
        var text = slot.Operation.Syntax.ToString();
        var at = text.LastIndexOf(oddName, StringComparison.Ordinal);
        var suggestion = at >= 0 ? text.Substring(0, at) + expected + text.Substring(at + oddName.Length) : expected;
        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.SimilarLineAnomaly,
            slot.Operation.Syntax.GetLocation(),
            messageArgs: [text, suggestion]));
    }

    private static string NameOf(Slot slot) =>
        slot.Symbol?.Name ?? Convert.ToString(slot.Constant, CultureInfo.InvariantCulture) ?? string.Empty;

    private static int CommonPrefix(string x, string y)
    {
        var length = Math.Min(x.Length, y.Length);
        var i = 0;
        while (i < length && x[i] == y[i])
        {
            i++;
        }

        return i;
    }

    private static int CommonSuffix(string x, string y)
    {
        var length = Math.Min(x.Length, y.Length);
        var i = 0;
        while (i < length && x[x.Length - 1 - i] == y[y.Length - 1 - i])
        {
            i++;
        }

        return i;
    }

    private static int Mix(int hash, int value) => unchecked((hash * 31) + value);

    private readonly record struct Row(int Hash, int Start, int Count);

    private readonly record struct Slot(IOperation Operation, ISymbol? Symbol, object? Constant, bool IsData);
}
