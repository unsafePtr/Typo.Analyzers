using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Typo.Core;

namespace Typo.Analyzers;

/// <summary>
/// TYPO0011 and TYPO0012: in consecutive similar lines or arguments, one item repeats a name from another where the pattern calls for a different one, as in <c>Z += other.Y</c> after <c>X += other.X; Y += other.Y;</c>.
/// </summary>
/// <remarks>
/// Each item becomes a shape hash plus its slots (symbol names and literals); consecutive items with the same shape form a run and their slots form columns.
/// Declarations and arguments start with a key-only slot for the declared local or the parameter, which can identify an item but is never reported.
/// In runs of three or more (TYPO0011), a key column is one whose fragments (names minus the column's shared prefix and suffix) are all different, so they identify each item.
/// A second column that matches the key on every item but one, and repeats another item's fragment on that one, is reported.
/// In a pair (TYPO0012) a changed column can never repeat another item, so the copy is a column that stays the same while the key is renamed, as <c>other.Width</c> does in <c>Width == other.Width &amp;&amp; Height == other.Width</c>.
/// A pair's key fragments are whole words, and every other changed column must follow the same rename.
/// The unchanged name must contain the first item's word exactly once and must not contain the second's.
/// The guards against deliberate repeats come from a survey of dotnet/runtime and aspnetcore and are explained on their methods.
/// Framework values, renamed enclosing calls and squares such as Multiply(x, x) guard pairs only.
/// Declaration chains such as <c>f8 = f4 * f4; f12 = f8 * f4</c> are skipped in runs and pairs alike.
/// The suggested name must exist in scope, which suppresses the classic false positive <c>halfWidth -= borderWidth; halfHeight -= borderWidth;</c> when there is no borderHeight.
/// Runs are scanned in statement blocks (expression statements and declarations), object initializers, &amp;&amp;/|| chains and argument lists.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SimilarRunAnalyzer : DiagnosticAnalyzer
{
    private const int MinimumRun = 3;

    private const string PairScanKey = "TYPO0012.scan";

    private static readonly (string Name, Source Source)[] s_sourceNames =
    [
        ("statements", Source.Statements),
        ("declarations", Source.Declarations),
        ("initializers", Source.Initializers),
        ("conditions", Source.Conditions),
        ("arguments", Source.Arguments),
    ];

    // Exact first, so the partial modes only add findings such as minX/maxY where key names wrap the other fragment.
    private static readonly Match[] s_modes = [Match.Exact, Match.KeyEndsWith, Match.KeyStartsWith];

    private static readonly Comparison<IOperation> s_byParameterOrdinal =
        static (x, y) => ((IArgumentOperation)x).Parameter!.Ordinal.CompareTo(((IArgumentOperation)y).Parameter!.Ordinal);

    private enum Match
    {
        Exact,
        KeyEndsWith,
        KeyStartsWith,
    }

    [Flags]
    private enum Source
    {
        None = 0,
        Statements = 1,
        Declarations = 2,
        Initializers = 4,
        Conditions = 8,
        Arguments = 16,
        All = Statements | Declarations | Initializers | Conditions | Arguments,
    }

    [ThreadStatic]
    private static List<Row>? t_rows;

    [ThreadStatic]
    private static List<Slot>? t_slots;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.SimilarLineAnomaly, Descriptors.SimilarPairAnomaly];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(AnalyzeBlock, OperationKind.Block);
        context.RegisterOperationAction(AnalyzeInitializer, OperationKind.ObjectOrCollectionInitializer);
        context.RegisterOperationAction(AnalyzeChain, OperationKind.Binary);
        context.RegisterOperationAction(AnalyzeArguments, OperationKind.Invocation, OperationKind.ObjectCreation);
    }

    private static void AnalyzeBlock(OperationAnalysisContext context)
    {
        var items = OperandChain.Rent();
        try
        {
            foreach (var operation in ((IBlockOperation)context.Operation).Operations)
            {
                switch (operation)
                {
                    case IExpressionStatementOperation statement:
                        items.Add(statement.Operation);
                        continue;

                    case IVariableDeclarationGroupOperation group:
                        AddDeclarators(context, group, items);
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

    // Each initialized local is an item; one without an initializer ends the run like any other statement.
    private static void AddDeclarators(OperationAnalysisContext context, IVariableDeclarationGroupOperation group, List<IOperation> items)
    {
        foreach (var declaration in group.Declarations)
        {
            foreach (var declarator in declaration.Declarators)
            {
                if (declarator.Initializer is not null)
                {
                    items.Add(declarator);
                    continue;
                }

                AnalyzeItems(context, items);
                items.Clear();
            }
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

    // Implicit calls (collection-initializer Add, LINQ query methods, interpolated-string handlers, attribute constructors) have no argument list the user wrote.
    private static void AnalyzeArguments(OperationAnalysisContext context)
    {
        var operation = context.Operation;
        if (operation.IsImplicit)
        {
            return;
        }

        var arguments = operation switch
        {
            IInvocationOperation invocation => invocation.Arguments,
            IObjectCreationOperation creation => creation.Arguments,
            _ => [],
        };
        if (arguments.Length < 2 || SpellsContainingMember(context.ContainingSymbol, arguments))
        {
            return;
        }

        var items = OperandChain.Rent();
        try
        {
            // Roslyn lists arguments in source order; parameter order makes new Vec(y: a, x: b) form the same rows as new Vec(b, a).
            var ordered = true;
            var previous = -1;
            foreach (var argument in arguments)
            {
                if (argument.Parameter is not { } parameter)
                {
                    return;
                }

                ordered &= parameter.Ordinal > previous;
                previous = parameter.Ordinal;
                items.Add(argument);
            }

            if (!ordered)
            {
                items.Sort(s_byParameterOrdinal);
            }

            AnalyzeItems(context, items);
        }
        finally
        {
            OperandChain.Return(items);
        }
    }

    // A swizzle such as 'Yyzw => new(Y, Y, Z, W)' names its arguments in order, so the repeated 'Y' is stated, not copied.
    // A slip such as 'Xyzw => new(Y, Y, Z, W)' no longer spells the name and still reports.
    private static bool SpellsContainingMember(ISymbol? member, ImmutableArray<IArgumentOperation> arguments)
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

    private static void AnalyzeItems(OperationAnalysisContext context, List<IOperation> items)
    {
        if (items.Count < 2)
        {
            return;
        }

        var rows = t_rows ??= new List<Row>(16);
        var slots = t_slots ??= new List<Slot>(64);
        try
        {
            // An item whose neighbours differ already at the top can never join a run, so describing it is skipped.
            int? previous = null;
            var current = TopShape(items[0]);
            for (var i = 0; i < items.Count; i++)
            {
                var next = i + 1 < items.Count ? TopShape(items[i + 1]) : null;
                var start = slots.Count;
                var hash = 17;
                var twin = current is not null && (current == previous || current == next);
                rows.Add(twin && DescribeItem(items[i], ref hash, slots) ? new Row(hash, start, slots.Count - start) : new Row(rows.Count, start, -1));
                previous = current;
                current = next;
            }

            for (var first = 0; first < rows.Count;)
            {
                var last = first;
                while (last + 1 < rows.Count && rows[last + 1].Hash == rows[first].Hash && rows[last + 1].Count == rows[first].Count)
                {
                    last++;
                }

                var count = last - first + 1;
                if (rows[first].Count >= 2 && count >= 2 && !IsChain(rows, slots, first, count))
                {
                    if (count >= MinimumRun)
                    {
                        AnalyzeRun(context, rows, slots, first, count);
                    }
                    else if (count == 2 && (PairSources(context) & SourceOf(items[first])) != 0)
                    {
                        AnalyzePair(context, rows, slots, first);
                    }
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

    // 'TYPO0012.scan = declarations, arguments' in .editorconfig keeps only pairs from those sources.
    // Unknown names are skipped, so a value with no known name keeps every source; the value is parsed in place because pairs are frequent.
    private static Source PairSources(OperationAnalysisContext context)
    {
        var options = context.Options.AnalyzerConfigOptionsProvider.GetOptions(context.Operation.Syntax.SyntaxTree);
        if (!options.TryGetValue(PairScanKey, out var value))
        {
            return Source.All;
        }

        var sources = Source.None;
        var rest = value.AsSpan();
        while (!rest.IsEmpty)
        {
            var comma = rest.IndexOf(',');
            var name = (comma < 0 ? rest : rest.Slice(0, comma)).Trim();
            rest = comma < 0 ? default : rest.Slice(comma + 1);
            foreach (var (text, source) in s_sourceNames)
            {
                if (name.Equals(text.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    sources |= source;
                }
            }
        }

        return sources == Source.None ? Source.All : sources;
    }

    // The remaining items are operands of a &&/|| chain.
    private static Source SourceOf(IOperation item) => item switch
    {
        IVariableDeclaratorOperation => Source.Declarations,
        IArgumentOperation => Source.Arguments,
        { Parent: IExpressionStatementOperation } => Source.Statements,
        { Parent: IObjectOrCollectionInitializerOperation } => Source.Initializers,
        _ => Source.Conditions,
    };

    // Mirrors the top of DescribeItem's hash, so items of different shapes never hash alike; null for an item that never joins a run.
    private static int? TopShape(IOperation item) => item switch
    {
        IVariableDeclaratorOperation { Initializer: { } initializer } => Mix((int)OperationKind.VariableDeclarator, Shape(initializer.Value)),
        IVariableDeclaratorOperation => null,
        IArgumentOperation { Syntax: ArgumentSyntax } argument => Mix((int)OperationKind.Argument, Shape(argument.Value)),
        IArgumentOperation => null,
        _ => Shape(item),
    };

    private static int Shape(IOperation operation)
    {
        operation = SkipImplicitConversions(operation);
        return operation switch
        {
            IBinaryOperation binary => Mix((int)operation.Kind, (int)binary.OperatorKind),
            ICompoundAssignmentOperation compound => Mix((int)operation.Kind, (int)compound.OperatorKind),
            IUnaryOperation unary => Mix((int)operation.Kind, (int)unary.OperatorKind),
            _ => (int)operation.Kind,
        };
    }

    // Declarations that read a local declared earlier in the same run are steps of one computation, as in f8 = f4 * f4; f12 = f8 * f4, not parallel copies.
    private static bool IsChain(List<Row> rows, List<Slot> slots, int first, int count)
    {
        var head = slots[rows[first].Start];
        if (!head.KeyOnly || head.Symbol is not ILocalSymbol)
        {
            return false;
        }

        var runStart = head.Operation.Syntax.SpanStart;
        for (var r = 1; r < count; r++)
        {
            var row = rows[first + r];
            var rowStart = slots[row.Start].Operation.Syntax.SpanStart;
            for (var s = row.Start + 1; s < row.Start + row.Count; s++)
            {
                if (slots[s].Symbol is ILocalSymbol local && IsDeclaredWithin(local, runStart, rowStart))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsDeclaredWithin(ILocalSymbol local, int start, int end)
    {
        foreach (var location in local.Locations)
        {
            if (location.IsInSource && location.SourceSpan.Start >= start && location.SourceSpan.Start < end)
            {
                return true;
            }
        }

        return false;
    }

    // Declarations and arguments start with the declared local or the parameter, so 'var minX = …' and 'x: …' are keyed by name.
    // Those slots are added here rather than in Describe, which would also add them for locals inside lambda bodies and change existing columns.
    // An argument the user did not write (a default value, a params array, the receiver of an extension call) never joins a run.
    private static bool DescribeItem(IOperation item, ref int hash, List<Slot> slots)
    {
        switch (item)
        {
            case IVariableDeclaratorOperation declarator:
                hash = Mix(hash, (int)OperationKind.VariableDeclarator);
                slots.Add(new Slot(declarator, declarator.Symbol, null, IsData: false, KeyOnly: true));
                return declarator.Initializer is { } initializer && Describe(initializer.Value, ref hash, slots, 0);

            case IArgumentOperation argument:
                if (argument.Syntax is not ArgumentSyntax)
                {
                    return false;
                }

                hash = Mix(hash, (int)OperationKind.Argument);
                slots.Add(new Slot(argument, argument.Parameter, null, IsData: false, KeyOnly: true));
                return Describe(argument.Value, ref hash, slots, 0);

            default:
                return Describe(item, ref hash, slots, 0);
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
                if (other == key || affixes[other] is not { } computedAffix || slots[rows[first].Start + other].KeyOnly)
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
                        Report(context, Descriptors.SimilarLineAnomaly, slot, oddName, expected);
                        return;
                    }
                }
            }
        }
    }

    // The second row is the one reported, because a copy is usually made from the line above and then edited.
    private static void AnalyzePair(OperationAnalysisContext context, List<Row> rows, List<Slot> slots, int first)
    {
        var columns = rows[first].Count;
        var a = rows[first].Start;
        var b = rows[first + 1].Start;

        var key = -1;
        for (var c = 0; c < columns; c++)
        {
            if (SameName(slots[a + c], slots[b + c]))
            {
                continue;
            }

            // Lines that also differ in table data, as in Hex(p, _a >> 24, _a >> 16), are not a rename of one word.
            if (slots[a + c].IsData || slots[b + c].IsData)
            {
                return;
            }

            if (key < 0)
            {
                key = c;
            }
        }

        if (key < 0 || !HasCopyCandidate(slots, a, b, key + 1, columns)
            || !TryRename(NameOf(slots[a + key]), NameOf(slots[b + key]), out var from, out var to))
        {
            return;
        }

        // Lines that differ in some other way, as in v.Set(p.X, p.Y) and w.Set(q.X, q.Z), are not a simple rename of one word.
        for (var c = key + 1; c < columns; c++)
        {
            if (SameName(slots[a + c], slots[b + c]))
            {
                continue;
            }

            if (!TryRename(NameOf(slots[a + c]), NameOf(slots[b + c]), out var otherFrom, out var otherTo)
                || !otherFrom.Equals(from, StringComparison.OrdinalIgnoreCase)
                || !otherTo.Equals(to, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        // Only columns after the key are candidates, because the copy is the value that should have followed the renamed target, as b.X in 'Y = b.X'.
        for (var c = key + 1; c < columns; c++)
        {
            var copy = slots[b + c];
            if (!IsCopyCandidate(slots[a + c], copy) || IsInsideRenamedColumn(slots, a, b, columns, key, copy.Operation) || IsMemberOfRenamedReceiver(slots, a, b, columns, copy.Operation))
            {
                continue;
            }

            var name = copy.Symbol!.Name;
            var at = IndexOfWord(name.AsSpan(), from, 0);
            if (at < 0 || IndexOfWord(name.AsSpan(), from, at + 1) >= 0 || IndexOfWord(name.AsSpan(), to, 0) >= 0)
            {
                continue;
            }

            var span = name.AsSpan(at, from.Length);
            var replacement = IsAllCaps(span) ? to.ToString().ToUpperInvariant() : Restyle(to.ToString(), span);
            var expected = name.Substring(0, at) + replacement + name.Substring(at + from.Length);

            // In X = p.Y + p.X; Y = p.Y + p.X both names already appear on both lines, so the second line was edited on purpose.
            if (IsSharedName(slots, a, b, columns, expected) || IsDeliberateRepeat(slots[b], copy, expected) || !Exists(copy, expected))
            {
                continue;
            }

            Report(context, Descriptors.SimilarPairAnomaly, copy, name, expected);
            return;
        }
    }

    // A member of a renamed receiver keeps its own name, as Row1 does in 'Lerp(matrix1.Row1, matrix2.Row1, amount)', where only the receiver and the parameter carry the changed digit.
    private static bool IsMemberOfRenamedReceiver(List<Slot> slots, int a, int b, int columns, IOperation copy)
    {
        if (copy is not IMemberReferenceOperation { Instance: { } instance })
        {
            return false;
        }

        var receiver = SkipImplicitConversions(instance);
        for (var c = 0; c < columns; c++)
        {
            if (ReferenceEquals(slots[b + c].Operation, receiver) && !SameName(slots[a + c], slots[b + c]))
            {
                return true;
            }
        }

        return false;
    }

    // When a column other than the key renames an enclosing access or call, the names inside it are shared on purpose.
    // An example is 'head' in 'headHead = Read(ref head.Inner.Head); headTail = Read(ref head.Inner.Tail);'.
    // Another is GetLower() in the arguments of 'Create(ZipLower(left.GetLower()), ZipUpper(left.GetLower()))'.
    private static bool IsInsideRenamedColumn(List<Slot> slots, int a, int b, int columns, int key, IOperation copy)
    {
        for (var c = 0; c < columns; c++)
        {
            if (c == key || SameName(slots[a + c], slots[b + c]))
            {
                continue;
            }

            for (var parent = copy.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent == slots[b + c].Operation)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Passing one variable for two parameters is often deliberate, as in Multiply(x, x) to square; it is reported only when the variable it should have been is never used.
    private static bool IsDeliberateRepeat(Slot row, Slot copy, string expected)
    {
        if (copy.Symbol is not (ILocalSymbol or IParameterSymbol)
            || row.Operation is not IArgumentOperation argument
            || SkipImplicitConversions(argument.Value) != copy.Operation)
        {
            return false;
        }

        var root = copy.Operation;
        while (root.Parent is { } parent)
        {
            root = parent;
        }

        foreach (var operation in root.Descendants())
        {
            var name = operation switch
            {
                ILocalReferenceOperation local => local.Local.Name,
                IParameterReferenceOperation parameter => parameter.Parameter.Name,
                _ => null,
            };
            if (string.Equals(name, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCopyCandidate(List<Slot> slots, int a, int b, int start, int columns)
    {
        for (var c = start; c < columns; c++)
        {
            if (IsCopyCandidate(slots[a + c], slots[b + c]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCopyCandidate(Slot first, Slot second) =>
        !second.KeyOnly
        && first.Symbol is not null
        && second.Symbol is not null
        && string.Equals(first.Symbol.Name, second.Symbol.Name, StringComparison.Ordinal)
        && !IsSharedOnPurpose(second);

    // Enum members, framework values such as float.MaxValue or Index.Start, and calls with written arguments such as Math.Max(maxX, 0) are repeated on purpose far more often than by copy-paste.
    // User constants such as MAX_WIDTH stay candidates.
    private static bool IsSharedOnPurpose(Slot slot)
    {
        switch (slot.Symbol)
        {
            case IFieldSymbol { IsStatic: true } field:
                return Framework.IsFrameworkValue(field.ContainingType);

            case IPropertySymbol { IsStatic: true } property:
                return Framework.IsFrameworkValue(property.ContainingType);

            case IMethodSymbol when slot.Operation is IInvocationOperation invocation:
                foreach (var argument in invocation.Arguments)
                {
                    if (argument.Syntax is ArgumentSyntax)
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static bool IsSharedName(List<Slot> slots, int a, int b, int columns, string name)
    {
        for (var c = 0; c < columns; c++)
        {
            if (SameName(slots[a + c], slots[b + c])
                && (slots[a + c].Symbol?.Name ?? slots[a + c].Constant as string) is { } shared
                && string.Equals(shared, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Splits two names into their shared affixes and the differing whole words, such as 'min' and 'max' in minX and maxX.
    // The affixes are cut back to word boundaries, which keeps the shared 'm' of minX and maxX out of the prefix.
    // A fragment must hold a letter or digit, so Width and MinWidth, where Width is all shared suffix, never pair.
    // Neither fragment may contain the other, so name and displayName do not pair either.
    private static bool TryRename(string x, string y, out ReadOnlySpan<char> from, out ReadOnlySpan<char> to)
    {
        var prefix = CommonPrefix(x, y);
        var suffix = Math.Min(CommonSuffix(x, y), Math.Min(x.Length, y.Length) - prefix);
        while (!IsWordBoundary(x.AsSpan(), prefix) || !IsWordBoundary(y.AsSpan(), prefix))
        {
            prefix--;
        }

        while (!IsWordBoundary(x.AsSpan(), x.Length - suffix) || !IsWordBoundary(y.AsSpan(), y.Length - suffix))
        {
            suffix--;
        }

        from = x.AsSpan(prefix, x.Length - prefix - suffix);
        to = y.AsSpan(prefix, y.Length - prefix - suffix);
        return HasLetterOrDigit(from)
            && HasLetterOrDigit(to)
            && IndexOfWord(from, to, 0) < 0
            && IndexOfWord(to, from, 0) < 0;
    }

    // Words are separated by any character that is not a letter or digit, by a lower-case letter followed by an upper-case one, by a letter next to a digit, and before the last capital of an acronym followed by a lower-case letter (XML|Http).
    // All-lower-case compounds such as 'minx' have no boundary inside.
    private static bool IsWordBoundary(ReadOnlySpan<char> name, int index)
    {
        if (index <= 0 || index >= name.Length)
        {
            return true;
        }

        var before = name[index - 1];
        var after = name[index];
        if (!char.IsLetterOrDigit(before) || !char.IsLetterOrDigit(after) || char.IsDigit(before) != char.IsDigit(after))
        {
            return true;
        }

        if (char.IsLower(before) && char.IsUpper(after))
        {
            return true;
        }

        return char.IsUpper(before) && char.IsUpper(after) && index + 1 < name.Length && char.IsLower(name[index + 1]);
    }

    private static int IndexOfWord(ReadOnlySpan<char> text, ReadOnlySpan<char> word, int start)
    {
        for (var i = start; i + word.Length <= text.Length; i++)
        {
            if (IsWordBoundary(text, i)
                && IsWordBoundary(text, i + word.Length)
                && text.Slice(i, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool HasLetterOrDigit(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    // MAX_WIDTH becomes MAX_HEIGHT; a single capital such as X is left to Restyle.
    private static bool IsAllCaps(ReadOnlySpan<char> text)
    {
        var letters = 0;
        foreach (var c in text)
        {
            if (char.IsLower(c))
            {
                return false;
            }

            if (char.IsLetter(c))
            {
                letters++;
            }
        }

        return letters >= 2;
    }

    private static bool SameName(Slot x, Slot y)
    {
        if (x.Symbol is not null || y.Symbol is not null)
        {
            return x.Symbol is not null && y.Symbol is not null && string.Equals(x.Symbol.Name, y.Symbol.Name, StringComparison.Ordinal);
        }

        return x.Constant is string text
            ? y.Constant is string other && string.Equals(text, other, StringComparison.Ordinal)
            : Equals(x.Constant, y.Constant);
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

    private static IOperation SkipImplicitConversions(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    // Returns false when the tree is too deep to describe; such a line never joins a run.
    private static bool Describe(IOperation operation, ref int hash, List<Slot> slots, int depth)
    {
        if (!Limits.CanRecurse(depth))
        {
            return false;
        }

        operation = SkipImplicitConversions(operation);
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

    // Both are non-empty: run fragments come from keys checked non-empty and from a line that matched the key, and pair fragments contain a letter or digit.
    // Invariant casing keeps suggestions such as 'items' from becoming 'İtems' under a Turkish culture.
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

    // A suggested local must already be declared, and a suggested member must be the same kind of member as the one it replaces.
    private static bool Exists(Slot slot, string expected)
    {
        switch (slot.Symbol)
        {
            case null:
                return true;

            case ILocalSymbol or IParameterSymbol:
            {
                if (slot.Operation.SemanticModel is not { } model)
                {
                    return false;
                }

                // Lookup also returns locals declared later in the block, which the line cannot have meant.
                var position = slot.Operation.Syntax.SpanStart;
                foreach (var symbol in model.LookupSymbols(position, name: expected))
                {
                    if (symbol is not ILocalSymbol local || DeclaredBefore(local, position))
                    {
                        return true;
                    }
                }

                return false;
            }

            default:
                for (var type = slot.Symbol.ContainingType; type is not null; type = type.BaseType)
                {
                    foreach (var member in type.GetMembers(expected))
                    {
                        if (IsSameKind(slot.Symbol, member))
                        {
                            return true;
                        }
                    }
                }

                return false;
        }
    }

    private static bool DeclaredBefore(ILocalSymbol local, int position)
    {
        foreach (var location in local.Locations)
        {
            if (location.IsInSource && location.SourceSpan.Start < position)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameKind(ISymbol reported, ISymbol suggested) =>
        reported is IFieldSymbol or IPropertySymbol ? suggested is IFieldSymbol or IPropertySymbol : reported.Kind == suggested.Kind;

    private static void Report(OperationAnalysisContext context, DiagnosticDescriptor descriptor, Slot slot, string oddName, string expected)
    {
        var text = Quote.Of(slot.Operation.Syntax);
        var at = text.LastIndexOf(oddName, StringComparison.Ordinal);
        var suggestion = at >= 0 ? text.Substring(0, at) + expected + text.Substring(at + oddName.Length) : expected;
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor,
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

    // A key-only slot (a declared local or a parameter name) can identify a row but is never the name reported.
    private readonly record struct Slot(IOperation Operation, ISymbol? Symbol, object? Constant, bool IsData, bool KeyOnly = false);
}
