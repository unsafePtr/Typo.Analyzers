using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Typo.Core;

namespace Typo.Analyzers;

/// <summary>
/// TYPO0015 to TYPO0017: an argument named like a parameter other than the one it is passed for, as in <c>ProcessInvoice(invoiceId, customerId)</c> for <c>ProcessInvoice(Guid customerId, Guid invoiceId)</c>.
/// </summary>
/// <remarks>
/// A name is compared as words, in lower case and without a field prefix, so <c>_invoiceId</c>, <c>InvoiceId</c> and <c>order.InvoiceId</c> all read 'invoice', 'id'.
/// Two parameters are told apart by the words only one of them has: 'customer' and 'invoice' for customerId and invoiceId.
/// An argument names another parameter when it holds every such word of that parameter and none of its own parameter's.
/// An argument is read level by level: the member first, and its receiver only when the member says nothing about either parameter, as 'Id' does in <c>invoice.Id</c>.
/// TYPO0015 reports arguments that name each other's parameters, in a swap or a longer rotation, when the call still binds with them exchanged.
/// TYPO0016 reports an argument that names another parameter with nothing naming its own in return.
/// TYPO0017 reports an argument named for something else while a variable or member spelled like its parameter is at hand.
/// The other parameter must accept the argument, and the same value passed for both parameters is left to TYPO0012.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ArgumentNameAnalyzer : DiagnosticAnalyzer
{
    private const int MaximumParameters = 32;
    private const int MaximumLevels = 4;
    private const int MaximumSteps = 16;

    // 'typo_argument_match = loose' in .editorconfig restores the shapes that the default, tight matching leaves out; see Report.
    private const string MatchKey = "typo_argument_match";

    // Shorter words differ from many others by one letter, as 'min' and 'max' do.
    private const int MinimumEditLength = 5;

    // A shorter run of letters is found inside most longer words, as 'id' is in 'index'.
    private const int MinimumContractionLength = 3;

    private static readonly FrozenSet<string> s_reversalWords = new[]
    {
        "backward", "backwards", "complement", "desc", "descending", "endian", "flip", "flipped", "inverse", "invert", "inverted", "landscape", "mirror", "mirrored", "opposite", "portrait",
        "reciprocal", "reverse", "reversed", "rotate", "rotated", "rotation", "swap", "swapped", "transpose", "transposed", "undo",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> s_positionWords = new[]
    {
        "a", "alpha", "b", "blue", "bottom", "c", "d", "first", "green", "i", "imaginary", "j", "k", "left", "lhs", "m", "n", "p", "q", "real", "red", "rhs", "right",
        "second", "third", "top", "u", "v", "w", "x", "y", "z",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> s_orderFreeMethods = new[]
    {
        "Add", "Distance", "DistanceSquared", "Dot", "Equals", "Intersect", "Intersects", "Max", "Min", "Mul", "Multiply", "Swap", "Union", "Xor",
    }.ToFrozenSet(StringComparer.Ordinal);

    [ThreadStatic]
    private static List<ImmutableArray<string>>? t_levels;

    private enum Tier
    {
        None,
        Word,
        Abbreviation,
        Edit,
        Initial,
    }

    private enum Cover
    {
        Exact,
        Full,
        Distinguishing,
        Partial,
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [Descriptors.SwappedArguments, Descriptors.ArgumentNamedLikeAnotherParameter, Descriptors.ArgumentPassedOverNamesake];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            var table = new WordTable();
            var types = new TypeFacts(start.Compilation);
            start.RegisterOperationBlockStartAction(block =>
            {
                var loose = block.OperationBlocks.FirstOrDefault()?.Syntax.SyntaxTree is { } tree && IsLoose(block.Options, tree);
                var scope = new MemberScope(block.OwningSymbol, table, types, loose);
                block.RegisterOperationAction(scope.Analyze, OperationKind.Invocation, OperationKind.ObjectCreation, OperationKind.PropertyReference);
                if (!loose)
                {
                    block.RegisterOperationBlockEndAction(scope.Report);
                }
            });
        });
    }

    // Implicit calls (collection-initializer Add, LINQ query methods, attribute constructors) have no argument list the user wrote.
    private static void Analyze(OperationAnalysisContext context, WordTable table, MemberScope scope)
    {
        var operation = context.Operation;
        if (operation.IsImplicit)
        {
            return;
        }

        ImmutableArray<IArgumentOperation> arguments;
        ISymbol callee;
        ImmutableArray<IParameterSymbol> parameters;
        switch (operation)
        {
            case IInvocationOperation invocation:
                (arguments, callee, parameters) = (invocation.Arguments, invocation.TargetMethod, invocation.TargetMethod.Parameters);
                break;

            case IObjectCreationOperation { Constructor: { } constructor } creation:
                (arguments, callee, parameters) = (creation.Arguments, constructor, constructor.Parameters);
                break;

            case IPropertyReferenceOperation { Arguments.Length: >= 2 } indexer:
                (arguments, callee, parameters) = (indexer.Arguments, indexer.Property, indexer.Property.Parameters);
                break;

            default:
                return;
        }

        if (parameters.Length is < 2 or > MaximumParameters || Swizzle.Spells(context.ContainingSymbol, arguments))
        {
            return;
        }

        var loose = scope.Loose;
        var levels = t_levels ??= new List<ImmutableArray<string>>(MaximumLevels);
        var numbered = NumberedParameters(parameters);
        Candidate[]? candidates = null;
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            if (argument.ArgumentKind != ArgumentKind.Explicit || argument.Parameter is not { } own)
            {
                continue;
            }

            levels.Clear();
            AddLevels(argument.Value, table, levels, loose);
            if (levels.Count == 0)
            {
                continue;
            }

            var ownWords = table.Of(own.Name);
            if (levels.Count == 1 && levels[0].SequenceEqual(ownWords))
            {
                continue;
            }

            var best = default(Candidate);
            foreach (var target in parameters)
            {
                if (target.Ordinal == own.Ordinal || target.IsParams)
                {
                    continue;
                }

                var swapOnly = IsSet(numbered, target);
                var match = swapOnly ? WholeName(levels, table.Of(target.Name)) : Names(levels, table.Of(target.Name), ownWords);
                if (match.Tier == Tier.None
                    || (!loose && match.Tier == Tier.Initial)
                    || (best.Target is not null && !match.IsBetterThan(best.Match)))
                {
                    continue;
                }

                var at = IndexOf(arguments, target);
                if ((at >= 0 || !IsFilledByCompiler(target))
                    && Accepts(target, own, argument)
                    && (at < 0 || !OperationEquivalence.AreEquivalent(argument.Value, arguments[at].Value)))
                {
                    best = new Candidate(match, target, at, swapOnly);
                }
            }

            if (best.Target is not null)
            {
                candidates ??= new Candidate[arguments.Length];
                candidates[i] = best;
            }
            else if (!IsSet(numbered, own) && IsNamedForSomethingElse(levels, ownWords, out var shared))
            {
                ReportNamesake(context, callee, arguments, argument, shared, scope, loose);
            }
        }

        if (candidates is not null)
        {
            Report(context, callee, arguments, candidates, table, scope, loose);
        }
    }

    private static bool IsLoose(AnalyzerOptions options, SyntaxTree tree) =>
        options.AnalyzerConfigOptionsProvider.GetOptions(tree).TryGetValue(MatchKey, out var value)
        && string.Equals(value.Trim(), "loose", StringComparison.OrdinalIgnoreCase);

    // Parameters numbered alike, as x0 and x1 or val1 and val2, are told apart by position, so their names say nothing of what each takes.
    // Callers number their own variables from another base or in another order, as x1 and x2 passed for x0 and x1.
    // So only an argument with such a parameter's whole name points at it, and only in a swap, and a numbered parameter has no namesake.
    private static uint NumberedParameters(ImmutableArray<IParameterSymbol> parameters)
    {
        var numbered = 0u;
        for (var i = 0; i < parameters.Length; i++)
        {
            var stem = Stem(parameters[i].Name);
            if (stem.Length == parameters[i].Name.Length)
            {
                continue;
            }

            for (var j = i + 1; j < parameters.Length; j++)
            {
                var other = Stem(parameters[j].Name);
                if (other.Length < parameters[j].Name.Length && stem.SequenceEqual(other))
                {
                    numbered |= (1u << i) | (1u << j);
                }
            }
        }

        return numbered;
    }

    private static ReadOnlySpan<char> Stem(string name)
    {
        var end = name.Length;
        while (end > 0 && name[end - 1] is >= '0' and <= '9')
        {
            end--;
        }

        return name.AsSpan(0, end);
    }

    private static bool IsSet(uint numbered, IParameterSymbol parameter) => (numbered & (1u << parameter.Ordinal)) != 0;

    private static Match WholeName(List<ImmutableArray<string>> levels, ImmutableArray<string> target) =>
        levels[0].SequenceEqual(target) ? new Match(Tier.Word, levels.Count == 1 ? Cover.Exact : Cover.Full, 0) : default;

    // The compiler fills an omitted caller-info parameter, as it fills paramName of ArgumentException.ThrowIfNullOrEmpty.
    private static bool IsFilledByCompiler(IParameterSymbol parameter)
    {
        foreach (var attribute in parameter.GetAttributes())
        {
            if (attribute.AttributeClass is
                {
                    Name: "CallerArgumentExpressionAttribute" or "CallerMemberNameAttribute" or "CallerFilePathAttribute" or "CallerLineNumberAttribute",
                    ContainingNamespace.Name: "CompilerServices",
                })
            {
                return true;
            }
        }

        return false;
    }

    // An argument that names the parameter of one that names it back, directly or through others, is part of a swap; any other is reported on its own.
    // Tight matching, the default, reports what corpus review found to be a likely mistake rather than a loose fit:
    // - a swap whose arguments name each other's parameters by whole words, an abbreviation or a slip of the keyboard, but not by a lone initial;
    // - no swap that looks deliberate or changes nothing (see ReportSwap);
    // - an argument holding exactly another parameter's name, which the caller named itself, while that parameter is left out or gets something not named for it (see IsClearlyMisnamed);
    // - a namesake only when it is a parameter of the same type that the member never reads, in a member free to use it (see ReportNamesake);
    // - a swap or a namesake once per member (see MemberScope).
    private static void Report(OperationAnalysisContext context, ISymbol callee, ImmutableArray<IArgumentOperation> arguments, Candidate[] candidates, WordTable table, MemberScope scope, bool loose)
    {
        var swapped = new bool[candidates.Length];
        for (var i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].Target is null || swapped[i])
            {
                continue;
            }

            var cycle = new List<int> { i };
            var next = candidates[i].At;
            while (next >= 0 && next != i && candidates[next].Target is not null && cycle.Count < candidates.Length)
            {
                cycle.Add(next);
                next = candidates[next].At;
            }

            if (next != i)
            {
                continue;
            }

            foreach (var member in cycle)
            {
                swapped[member] = true;
            }

            ReportSwap(context, callee, arguments, candidates, cycle, table, scope, loose);
        }

        for (var i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].Target is null || swapped[i] || candidates[i].SwapOnly)
            {
                continue;
            }

            var target = TargetState(arguments, candidates[i], table.Of(arguments[i].Parameter!.Name), table, loose);
            if (loose || IsClearlyMisnamed(context.Operation, callee, arguments, i, candidates[i], target, table))
            {
                ReportMisnamed(context, callee, arguments, candidates[i], i, target, scope);
            }
        }
    }

    // The argument must have exactly the other parameter's name, since a longer one such as 'maxValue' only shares the word of 'value'.
    // Parameters told apart only by position or a single letter, such as first and second or the 'a' of Sub(a, b), hold places, not meanings.
    // A named argument shows that its writer saw the parameter names, so only one naming a parameter left out is a slip.
    // A string that spells the argument's name labels it, as "name" does in Add("name", name), and a receiver called on, as in span.SequenceEqual(other), never shows its parameter's name.
    // Corpus review found the misnamed arguments among values the caller named itself: a parameter it forwards, a member of its own object, and a local standing where a parameter gets a fixed value such as Environment.NewLine.
    // A local more often holds a word of many meanings, such as 'name' or 'stream', and a call, a constant or another object's member is named for what it is, not for the parameter it fills.
    private static bool IsClearlyMisnamed(IOperation call, ISymbol callee, ImmutableArray<IArgumentOperation> arguments, int index, Candidate candidate, string target, WordTable table)
    {
        var argument = arguments[index];
        var other = candidate.At >= 0 ? arguments[candidate.At] : null;
        if (candidate.Match is not { Tier: Tier.Word, Cover: Cover.Exact }
            || target == "fits"
            || (target != "omitted" && IsNamed(argument))
            || !IsCallersOwn(argument.Value, other?.Value)
            || NamesPositions([argument.Parameter!.Name, candidate.Target!.Name], table)
            || IsCalledOn(callee, argument)
            || (other is not null && (IsCalledOn(callee, other) || Labels(other.Value, argument.Value)))
            || IsExpectedToThrow(call))
        {
            return false;
        }

        // 'timestamp' passed as 'receivedTimestamp' is a plainer name for what its own parameter takes.
        var levels = new List<ImmutableArray<string>>(MaximumLevels);
        AddLevels(argument.Value, table, levels, loose: false);
        var own = table.Of(argument.Parameter!.Name);
        for (var level = 0; level <= candidate.Match.Level && level < levels.Count; level++)
        {
            if (!ContainsAll(own, levels[level]))
            {
                return true;
            }
        }

        return false;
    }

    // Tight matching skips a swap that looks deliberate: named arguments, a call that the same member also makes in the other order, a reversal word, a vector turned as (v.Y, -v.X) is, and the components of one value in another order.
    // It also skips a swap that cannot matter or is meant to fail: of a receiver called on, of the two operands of an order-free method, in a negated result, or inside Assert.Throws.
    private static void ReportSwap(OperationAnalysisContext context, ISymbol callee, ImmutableArray<IArgumentOperation> arguments, Candidate[] candidates, List<int> cycle, WordTable table, MemberScope scope, bool loose)
    {
        var call = context.Operation.Syntax;
        var named = false;
        var calledOn = false;
        var parameters = new string[cycle.Count];
        for (var i = 0; i < cycle.Count; i++)
        {
            named |= IsNamed(arguments[cycle[i]]);
            calledOn |= IsCalledOn(callee, arguments[cycle[i]]);
            parameters[i] = arguments[cycle[i]].Parameter!.Name;
        }

        var turned = TurnsVector(arguments, cycle);
        var components = HaveOneOwner(arguments, cycle) && NamesPositions(parameters, table);
        var operands = SwapsOperands(callee, arguments, cycle);
        var negated = IsNegated(context.Operation);
        var throws = IsExpectedToThrow(context.Operation);
        if (!loose && (named || calledOn || turned || components || operands || negated || throws))
        {
            return;
        }

        var reversal = IsReversal(context.ContainingSymbol) || IsReversal(callee) || ReadsLikeReversal(call, arguments, cycle);
        var bothOrders = HasCorrectedOrderElsewhere(context.Operation, callee, arguments, candidates, cycle);
        if (!loose && (reversal || bothOrders))
        {
            return;
        }

        if (context.Operation.SemanticModel is { } model)
        {
            var moved = new Dictionary<SyntaxNode, SyntaxNode>();
            foreach (var member in cycle)
            {
                var place = ExpressionOf(arguments[candidates[member].At]);
                moved[place] = ExpressionOf(arguments[member]).WithTriviaFrom(place);
            }

            if (!Speculation.CallBinds(model, call, call.ReplaceNodes(moved.Keys, (original, _) => moved[original])))
            {
                return;
            }
        }

        // Roslyn lists arguments in source order, so ascending indexes read left to right.
        cycle.Sort();
        var match = candidates[cycle[0]].Match;
        var forwarded = true;
        ISymbol? forwarder = null;
        var locations = new List<Location>(cycle.Count - 1);
        var passed = new List<string>(cycle.Count);
        var declared = new List<string>(cycle.Count);
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        var ordinals = 0u;
        foreach (var member in cycle)
        {
            var argument = arguments[member];
            var expression = ExpressionOf(argument);
            passed.Add(Quote.Of(expression));
            declared.Add(argument.Parameter!.Name);
            sources.Add(SourceOf(argument.Value));
            ordinals |= 1u << argument.Parameter.Ordinal;
            if (member != cycle[0])
            {
                locations.Add(expression.GetLocation());
                match = Match.Worse(match, candidates[member].Match);
            }

            var source = SkipConversions(argument.Value) is IParameterReferenceOperation reference ? reference.Parameter.ContainingSymbol : null;
            forwarded &= source is not null && (forwarder is null || SymbolEqualityComparer.Default.Equals(forwarder, source));
            forwarder ??= source;
        }

        var properties = Properties(callee, match, named);
        properties["shape"] = cycle.Count == 2 ? "swap" : "rotation";
        AddContext(properties, "forwarded", forwarded);
        AddContext(properties, "recursive", SymbolEqualityComparer.Default.Equals(callee.OriginalDefinition, context.ContainingSymbol.OriginalDefinition));
        AddContext(properties, "reversal", reversal);
        AddContext(properties, "turned", turned);
        AddContext(properties, "bothOrders", bothOrders);
        properties["sources"] = string.Join(",", sources);
        AddContext(properties, "calledOn", calledOn);
        AddContext(properties, "components", components);
        AddContext(properties, "operands", operands);
        AddContext(properties, "negated", negated);
        AddContext(properties, "throws", throws);
        var diagnostic = Diagnostic.Create(
            Descriptors.SwappedArguments,
            ExpressionOf(arguments[cycle[0]]).GetLocation(),
            additionalLocations: locations,
            properties: properties.ToImmutable(),
            messageArgs: [Join(passed), Join(declared)]);
        if (loose)
        {
            context.ReportDiagnostic(diagnostic);
        }
        else
        {
            scope.Add(callee.OriginalDefinition, ordinals, diagnostic);
        }
    }

    // Members of one value passed in another order to parameters named for positions or components, as in new Vector(v.Y, v.X), Atan2(d.X, d.Y) or a colour built from B, G and R.
    // Corpus review found these to change axes or channel order on purpose; two values' members, as in Compare(b.Items, a.Items), stay reported.
    private static bool HaveOneOwner(ImmutableArray<IArgumentOperation> arguments, List<int> cycle)
    {
        IOperation? owner = null;
        foreach (var member in cycle)
        {
            if (SkipConversions(arguments[member].Value) is not IMemberReferenceOperation { Instance: { } instance }
                || (owner is not null && !OperationEquivalence.AreEquivalent(owner, instance)))
            {
                return false;
            }

            owner ??= instance;
        }

        return true;
    }

    // Names told apart only by words for a position or a component, as x and y, left and right or red and blue, or by a single letter.
    private static bool NamesPositions(IReadOnlyList<string> names, WordTable table)
    {
        foreach (var name in names)
        {
            if (name.Length == 1)
            {
                return true;
            }
        }

        var distinct = false;
        foreach (var name in names)
        {
            foreach (var word in table.Of(name))
            {
                if (IsInEvery(word, names, table))
                {
                    continue;
                }

                if (!s_positionWords.Contains(word))
                {
                    return false;
                }

                distinct = true;
            }
        }

        return distinct;
    }

    private static bool IsInEvery(string word, IReadOnlyList<string> names, WordTable table)
    {
        foreach (var name in names)
        {
            if (!table.Of(name).Contains(word))
            {
                return false;
            }
        }

        return true;
    }

    // The two operands of a method whose result does not depend on their order, as Max(a, b), Dot(a, b) and Equals(a, b) do not.
    // The name alone does not say so, as Dictionary.Add(key, value) shows, so the two must be the first two parameters, declared with one type.
    private static bool SwapsOperands(ISymbol callee, ImmutableArray<IArgumentOperation> arguments, List<int> cycle) =>
        cycle.Count == 2
        && callee.OriginalDefinition is IMethodSymbol { Parameters: [var first, var second, ..] } method
        && s_orderFreeMethods.Contains(method.Name)
        && arguments[cycle[0]].Parameter!.Ordinal + arguments[cycle[1]].Parameter!.Ordinal == 1
        && SymbolEqualityComparer.Default.Equals(first.Type, second.Type);

    // A reversal word in the swapped arguments' names or in a condition that guards the call, as in 'invertedAsks' or 'IsTransposed() ? new Point(p.Y, p.X) : p'.
    private static bool ReadsLikeReversal(SyntaxNode call, ImmutableArray<IArgumentOperation> arguments, List<int> cycle)
    {
        foreach (var member in cycle)
        {
            if (NamesReversal(ExpressionOf(arguments[member])))
            {
                return true;
            }
        }

        for (var (child, node) = (call, call.Parent); node is not null and not MemberDeclarationSyntax; (child, node) = (node, node.Parent))
        {
            var condition = node switch
            {
                ConditionalExpressionSyntax conditional when child != conditional.Condition => conditional.Condition,
                IfStatementSyntax statement when child != statement.Condition => statement.Condition,
                _ => null,
            };
            if (condition is not null && NamesReversal(condition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NamesReversal(SyntaxNode node)
    {
        foreach (var identifier in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            foreach (var word in Words.Split(identifier.Identifier.ValueText))
            {
                if (s_reversalWords.Contains(word))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Two members of one object exchanged with a sign changed turn a vector a quarter or half round, as (v.Y, -v.X) does.
    private static bool TurnsVector(ImmutableArray<IArgumentOperation> arguments, List<int> cycle)
    {
        if (cycle.Count != 2)
        {
            return false;
        }

        var (first, firstNegated) = Unsigned(arguments[cycle[0]].Value);
        var (second, secondNegated) = Unsigned(arguments[cycle[1]].Value);
        return (firstNegated || secondNegated)
            && first is IMemberReferenceOperation { Instance: { } firstInstance }
            && second is IMemberReferenceOperation { Instance: { } secondInstance }
            && OperationEquivalence.AreEquivalent(firstInstance, secondInstance);
    }

    private static (IOperation Value, bool Negated) Unsigned(IOperation value) =>
        SkipConversions(value) is IUnaryOperation { OperatorKind: UnaryOperatorKind.Minus } negation ? (SkipConversions(negation.Operand), true) : (SkipConversions(value), false);

    private static void ReportMisnamed(OperationAnalysisContext context, ISymbol callee, ImmutableArray<IArgumentOperation> arguments, Candidate candidate, int index, string target, MemberScope scope)
    {
        var argument = arguments[index];
        var expression = ExpressionOf(argument);
        var properties = Properties(callee, candidate.Match, IsNamed(argument));
        properties["shape"] = "misnamed";
        properties["target"] = target;
        if (context.Operation.SemanticModel is { } model && FindNamesake(model, context.ContainingSymbol, argument, expression, scope, out _) is { } namesake)
        {
            properties["inScope"] = IsReferenced(context.Operation, namesake) ? "used" : "unused";
        }

        var targetValue = candidate.At >= 0 ? arguments[candidate.At].Value : null;
        properties["source"] = SourceOf(argument.Value);
        properties["targetSource"] = targetValue is null ? "omitted" : SourceOf(targetValue);
        AddContext(properties, "labelled", targetValue is not null && Labels(targetValue, argument.Value));
        AddContext(properties, "calledOn", IsCalledOn(callee, argument) || (candidate.At >= 0 && IsCalledOn(callee, arguments[candidate.At])));
        AddContext(properties, "throws", IsExpectedToThrow(context.Operation));

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.ArgumentNamedLikeAnotherParameter,
            expression.GetLocation(),
            properties: properties.ToImmutable(),
            messageArgs: [Quote.Of(expression), argument.Parameter!.Name, candidate.Target!.Name]));
    }

    // The argument has words its parameter lacks and lacks words its parameter has, as 'orderId' does for 'invoiceId'.
    // One that only adds to the parameter's name ('newInvoiceId'), shortens it ('id') or is named through its receiver ('invoice.Id') fits loosely and is left alone.
    private static bool IsNamedForSomethingElse(List<ImmutableArray<string>> levels, ImmutableArray<string> own, out bool shared)
    {
        shared = false;
        for (var level = 1; level < levels.Count; level++)
        {
            foreach (var word in levels[level])
            {
                if (Best(word, own, []) != Tier.None)
                {
                    return false;
                }
            }
        }

        var extra = false;
        foreach (var word in levels[0])
        {
            var fits = Best(word, own, []) != Tier.None;
            shared |= fits;
            extra |= !fits;
        }

        if (!extra)
        {
            return false;
        }

        foreach (var name in own)
        {
            if (Best(name, levels[0], []) == Tier.None)
            {
                return true;
            }
        }

        return false;
    }

    // A namesake that this call passes for another parameter is misplaced, not overlooked, and that is the other rules' finding.
    // An 'out var' declares a new variable, so its name is the one chosen for what the call writes.
    // Tight matching needs a parameter of the argument's type that the member never reads, since corpus review found no bug behind a local namesake.
    // It skips a parameter whose member has its signature set elsewhere and often no use for it: a lambda, a local function, an override, an explicit interface implementation or an event handler.
    private static void ReportNamesake(OperationAnalysisContext context, ISymbol callee, ImmutableArray<IArgumentOperation> arguments, IArgumentOperation argument, bool shared, MemberScope scope, bool loose)
    {
        var expression = ExpressionOf(argument);
        if (SkipConversions(argument.Value) is IDeclarationExpressionOperation
            || (!loose && !HasUnreadParameterSpelledLike(scope, context.Operation, argument))
            || context.Operation.SemanticModel is not { } model
            || FindNamesake(model, context.ContainingSymbol, argument, expression, scope, out var display) is not { } namesake)
        {
            return;
        }

        foreach (var other in arguments)
        {
            // The same value for two parameters is TYPO0012's finding.
            if ((other != argument && OperationEquivalence.AreEquivalent(other.Value, argument.Value))
                || SymbolEqualityComparer.Default.Equals(SymbolOf(other.Value), namesake))
            {
                return;
            }
        }

        var owner = namesake is IParameterSymbol parameter ? OwnerOf(parameter) : null;
        var sameType = SymbolEqualityComparer.Default.Equals(TypeOf(namesake), SkipConversions(argument.Value).Type);
        if (!loose && (owner is null or "lambda" or "localFunction" or "override" or "explicit" or "handler" || !sameType))
        {
            return;
        }

        var unused = namesake switch
        {
            IParameterSymbol parameterNamesake => !scope.IsRead(parameterNamesake, context.Operation),
            ILocalSymbol => !IsReferenced(context.Operation, namesake),
            _ => false,
        };
        if (!loose && !unused)
        {
            return;
        }

        var properties = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        properties["callee"] = callee.ContainingType is { } type ? type.Name + "." + callee.Name : callee.Name;
        properties["shape"] = "namesake";
        properties["kind"] = display.Length > namesake.Name.Length ? "sibling" : namesake.Kind.ToString();
        properties["inScope"] = unused ? "unused" : "used";
        AddContext(properties, "shared", shared);
        AddContext(properties, "named", IsNamed(argument));
        AddContext(properties, "sameType", sameType);
        properties["source"] = SourceOf(argument.Value);
        if (owner is not null)
        {
            properties["owner"] = owner;
        }

        AddContext(properties, "alias", namesake is ILocalSymbol local && Aliases(model, local, argument.Value));
        var diagnostic = Diagnostic.Create(
            Descriptors.ArgumentPassedOverNamesake,
            expression.GetLocation(),
            properties: properties.ToImmutable(),
            messageArgs: [Quote.Of(expression), argument.Parameter!.Name, display]);
        if (loose)
        {
            context.ReportDiagnostic(diagnostic);
        }
        else
        {
            scope.Add(namesake, 0, diagnostic);
        }
    }

    // A variable or member in reach that is spelled like the argument's own parameter and that the parameter would accept.
    // It is looked for by name in scope first, then among the members of the object the argument is read from.
    // A lookup costs more than the rest of the analyzer, so a spelling that nothing in reach declares is not looked up (see MemberScope.MayDeclare).
    private static ISymbol? FindNamesake(SemanticModel model, ISymbol container, IArgumentOperation argument, SyntaxNode expression, MemberScope scope, out string display)
    {
        display = string.Empty;
        var parameter = argument.Parameter!;
        if (parameter.Name.Length == 0)
        {
            return null;
        }

        var value = SkipConversions(argument.Value);
        var passed = SymbolOf(value);
        var position = expression.SpanStart;
        var spellings = scope.Spellings(parameter.Name);
        foreach (var spelling in spellings)
        {
            if (!scope.MayDeclare(spelling))
            {
                continue;
            }

            foreach (var symbol in model.LookupSymbols(position, name: spelling))
            {
                if (IsReadableAt(symbol, container, expression) && IsNamesake(model, symbol, passed, parameter))
                {
                    display = symbol.Name;
                    return symbol;
                }
            }
        }

        if (value is not IMemberReferenceOperation { Instance: { Type: { } owner } instance } || instance is IInstanceReferenceOperation || instance.Syntax is not ExpressionSyntax receiver)
        {
            return null;
        }

        // Upper case first, the usual spelling of another object's member, then lower case.
        for (var i = 1; i >= 0; i--)
        {
            var spelling = spellings[i];
            if (!MayHaveMember(owner, spelling))
            {
                continue;
            }

            foreach (var symbol in model.LookupSymbols(position, owner, spelling))
            {
                if (!symbol.IsStatic && IsNamesake(model, symbol, passed, parameter))
                {
                    display = Quote.Of(receiver) + "." + symbol.Name;
                    return symbol;
                }
            }
        }

        return null;
    }

    // Tight matching reports only a parameter of the argument's type that the member never reads, its own or one of its type's primary constructor.
    // Most arguments have none, and this check spares them the symbol lookups of FindNamesake, which cost more than all the rest of the analyzer.
    private static bool HasUnreadParameterSpelledLike(MemberScope scope, IOperation call, IArgumentOperation argument)
    {
        var name = argument.Parameter!.Name;
        var type = SkipConversions(argument.Value).Type;
        if (name.Length == 0 || type is null)
        {
            return false;
        }

        foreach (var parameter in scope.Parameters)
        {
            if (IsSpelledLike(parameter.Name, name) && SymbolEqualityComparer.Default.Equals(parameter.Type, type) && !scope.IsRead(parameter, call))
            {
                return true;
            }
        }

        return false;
    }

    // The spellings FindNamesake looks up: the name with its first letter in either case, and the lower-case one behind an underscore.
    private static bool IsSpelledLike(string candidate, string name)
    {
        var start = candidate.Length == name.Length + 1 && candidate[0] == '_' ? 1 : 0;
        var first = candidate.Length == name.Length + start ? candidate[start] : '\0';
        return first != '\0'
            && (first == char.ToLowerInvariant(name[0]) || (start == 0 && first == char.ToUpperInvariant(name[0])))
            && string.CompareOrdinal(candidate, start + 1, name, 1, name.Length - 1) == 0;
    }

    // Whether a type, a type it derives from or, for an interface, an interface it extends may have a member of that name; a type parameter or an array always may.
    private static bool MayHaveMember(ITypeSymbol type, string name)
    {
        if (type is not INamedTypeSymbol named)
        {
            return true;
        }

        for (var current = named; current is not null; current = current.BaseType)
        {
            if (current.MemberNames.Contains(name))
            {
                return true;
            }
        }

        if (named.TypeKind == TypeKind.Interface)
        {
            foreach (var @interface in named.AllInterfaces)
            {
                if (@interface.MemberNames.Contains(name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasGlobalStaticImport(Compilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree.GetRoot() is CompilationUnitSyntax unit && HasStaticImport(unit.Usings, globalOnly: true))
            {
                return true;
            }
        }

        return false;
    }

    // Whether a file imports a type's static members, at its top or in any namespace it declares.
    private static bool HasStaticImport(SyntaxNode root) => root switch
    {
        CompilationUnitSyntax unit => HasStaticImport(unit.Usings, globalOnly: false) || unit.Members.Any(HasStaticImport),
        BaseNamespaceDeclarationSyntax space => HasStaticImport(space.Usings, globalOnly: false) || space.Members.Any(HasStaticImport),
        _ => false,
    };

    private static bool HasStaticImport(SyntaxList<UsingDirectiveSyntax> usings, bool globalOnly)
    {
        foreach (var directive in usings)
        {
            if (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) && (!globalOnly || directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNamesake(SemanticModel model, ISymbol symbol, ISymbol? passed, IParameterSymbol parameter) =>
        !SymbolEqualityComparer.Default.Equals(symbol, passed)
        && TypeOf(symbol) is { } type
        && model.Compilation.ClassifyConversion(type, parameter.Type).IsImplicit;

    // Lookup also returns names the call cannot read: locals declared later in the block, locals of top-level statements seen from a type, and instance state seen from a static member.
    // A static lambda or local function reads nothing of the code around it but static members (CS8421).
    // A primary constructor parameter cannot be read in another constructor's initializer or in a nested type (CS9105).
    private static bool IsReadableAt(ISymbol symbol, ISymbol container, SyntaxNode at) =>
        (symbol.IsStatic || !IsBehindStaticFunction(symbol, at))
        && symbol switch
        {
            ILocalSymbol local => local.Locations.Any(location => location.IsInSource && location.SourceSpan.Start < at.SpanStart)
                && SymbolEqualityComparer.Default.Equals(MemberOf(local.ContainingSymbol), MemberOf(container)),
            IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } constructor } parameter when IsPrimary(constructor) =>
                !container.IsStatic
                && SymbolEqualityComparer.Default.Equals(MemberOf(container).ContainingType, constructor.ContainingType)
                && !at.Ancestors().Any(node => node is ConstructorInitializerSyntax)
                && !IsStoredInMember(parameter, constructor.ContainingType),
            IParameterSymbol parameter => SymbolEqualityComparer.Default.Equals(MemberOf(parameter.ContainingSymbol), MemberOf(container)),
            _ => symbol.IsStatic || !container.IsStatic,
        };

    private static bool IsBehindStaticFunction(ISymbol symbol, SyntaxNode at)
    {
        foreach (var node in at.Ancestors())
        {
            var isStatic = node switch
            {
                AnonymousFunctionExpressionSyntax function => function.Modifiers.Any(SyntaxKind.StaticKeyword),
                LocalFunctionStatementSyntax function => function.Modifiers.Any(SyntaxKind.StaticKeyword),
                _ => false,
            };
            if (isStatic && !symbol.Locations.Any(location => location.SourceTree == node.SyntaxTree && node.Span.Contains(location.SourceSpan)))
            {
                return true;
            }

            if (node is MemberDeclarationSyntax)
            {
                break;
            }
        }

        return false;
    }

    // The member that a lambda or local function belongs to.
    private static ISymbol MemberOf(ISymbol symbol)
    {
        while (symbol.ContainingSymbol is { } owner and not INamedTypeSymbol and not INamespaceSymbol)
        {
            symbol = owner;
        }

        return symbol;
    }

    private static bool IsPrimary(IMethodSymbol constructor) =>
        constructor.DeclaringSyntaxReferences is [var reference] && reference.GetSyntax() is TypeDeclarationSyntax;

    // What a namesake parameter belongs to, given as a diagnostic tag; an override, an interface member or an event handler takes parameters it may have no use for.
    private static string OwnerOf(IParameterSymbol parameter) => parameter.ContainingSymbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } => "lambda",
        IMethodSymbol { MethodKind: MethodKind.LocalFunction } => "localFunction",
        IMethodSymbol { MethodKind: MethodKind.Constructor } constructor when IsPrimary(constructor) => "primary",
        IMethodSymbol { Parameters: [{ Name: "sender", Type.SpecialType: SpecialType.System_Object }, _] } => "handler",
        IMethodSymbol { IsOverride: true } => "override",
        IMethodSymbol { ExplicitInterfaceImplementations.IsEmpty: false } => "explicit",
        IMethodSymbol method when ImplementsInterface(method) => "implicit",
        IMethodSymbol { IsVirtual: true } or IMethodSymbol { IsAbstract: true } => "virtual",
        _ => "method",
    };

    private static bool ImplementsInterface(IMethodSymbol method)
    {
        var type = method.ContainingType;
        foreach (var @interface in type.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers(method.Name))
            {
                if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // A local that holds the very value passed, as 'using var message = response;' does for response.
    private static bool Aliases(SemanticModel model, ILocalSymbol local, IOperation value)
    {
        foreach (var reference in local.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } initializer }
                && initializer.SyntaxTree == model.SyntaxTree
                && model.GetOperation(initializer) is { } operation
                && OperationEquivalence.AreEquivalent(SkipConversions(operation), SkipConversions(value)))
            {
                return true;
            }
        }

        return false;
    }

    // A primary constructor parameter copied into a member is read through that member, as a record's positional parameters are through their properties.
    // C# warns (CS9124) when such a parameter is read in a method as well, so a method would pass the member, not the parameter.
    private static bool IsStoredInMember(IParameterSymbol parameter, INamedTypeSymbol type)
    {
        if (type.IsRecord || !type.GetMembers(parameter.Name).IsEmpty)
        {
            return true;
        }

        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not TypeDeclarationSyntax declaration)
            {
                continue;
            }

            foreach (var member in declaration.Members)
            {
                switch (member)
                {
                    case BaseFieldDeclarationSyntax field:
                        foreach (var variable in field.Declaration.Variables)
                        {
                            if (Reads(variable.Initializer, parameter.Name))
                            {
                                return true;
                            }
                        }

                        break;

                    case PropertyDeclarationSyntax property when Reads(property.Initializer, parameter.Name):
                        return true;
                }
            }
        }

        return false;
    }

    private static bool Reads(SyntaxNode? initializer, string name) =>
        initializer is not null
        && initializer.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier => string.Equals(identifier.Identifier.ValueText, name, StringComparison.Ordinal));

    private static ITypeSymbol? TypeOf(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        IFieldSymbol field => field.Type,
        IPropertySymbol { IsIndexer: false } property => property.Type,
        _ => null,
    };

    private static bool IsReferenced(IOperation call, ISymbol symbol)
    {
        if (symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } constructor } && IsPrimary(constructor))
        {
            return IsReadInType(constructor.ContainingType, symbol.Name);
        }

        var root = call;
        while (root.Parent is { } parent)
        {
            root = parent;
        }

        foreach (var operation in root.DescendantsAndSelf())
        {
            var referenced = operation switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                IMemberReferenceOperation member => member.Member,
                _ => (ISymbol?)null,
            };
            if (SymbolEqualityComparer.Default.Equals(referenced, symbol))
            {
                return true;
            }
        }

        return false;
    }

    // A primary constructor parameter is read anywhere in its type, the base type's arguments included, so the whole declaration is searched by name.
    // A named argument and a member of another object only share the spelling.
    private static bool IsReadInType(INamedTypeSymbol type, string name)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            foreach (var identifier in reference.GetSyntax().DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (string.Equals(identifier.Identifier.ValueText, name, StringComparison.Ordinal)
                    && identifier.Parent is not NameColonSyntax
                    && !(identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ImmutableDictionary<string, string?>.Builder Properties(ISymbol callee, Match match, bool named)
    {
        var properties = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        properties["callee"] = callee.ContainingType is { } type ? type.Name + "." + callee.Name : callee.Name;
        properties["match"] = match.Tier.ToString();
        properties["cover"] = match.Cover.ToString();
        properties["level"] = match.Level == 0 ? "member" : "receiver";
        AddContext(properties, "named", named);
        return properties;
    }

    private static void AddContext(ImmutableDictionary<string, string?>.Builder properties, string key, bool value)
    {
        if (value)
        {
            properties[key] = "true";
        }
    }

    // What stands where the argument's name points: nothing, something without a name, something named for that parameter, or something else.
    // It is judged on the words that set that parameter apart from the argument's own, so 'orderId' does not fit 'invoiceId' next to 'customerId'.
    private static string TargetState(ImmutableArray<IArgumentOperation> arguments, Candidate candidate, ImmutableArray<string> own, WordTable table, bool loose)
    {
        if (candidate.At < 0)
        {
            return "omitted";
        }

        var levels = new List<ImmutableArray<string>>(MaximumLevels);
        AddLevels(arguments[candidate.At].Value, table, levels, loose);
        if (levels.Count == 0)
        {
            return "unnamed";
        }

        var target = table.Of(candidate.Target!.Name);
        var except = CountExcept(target, own) > 0 ? own : [];
        foreach (var level in levels)
        {
            foreach (var word in level)
            {
                if (Best(word, target, except) != Tier.None)
                {
                    return "fits";
                }
            }
        }

        return "other";
    }

    private static bool IsReversal(ISymbol? symbol)
    {
        for (; symbol is IMethodSymbol or IPropertySymbol or INamedTypeSymbol; symbol = symbol.ContainingSymbol)
        {
            foreach (var word in Words.Split(symbol.Name))
            {
                if (s_reversalWords.Contains(word))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Another call of the same method in this member already passes these arguments the way their names read, so both orders are in use.
    private static bool HasCorrectedOrderElsewhere(IOperation call, ISymbol callee, ImmutableArray<IArgumentOperation> arguments, Candidate[] candidates, List<int> cycle)
    {
        var root = call;
        while (root.Parent is { } parent)
        {
            root = parent;
        }

        foreach (var other in root.DescendantsAndSelf())
        {
            var (method, others) = other switch
            {
                IInvocationOperation invocation => (invocation.TargetMethod, invocation.Arguments),
                IObjectCreationOperation { Constructor: { } constructor } creation => (constructor, creation.Arguments),
                _ => default((ISymbol?, ImmutableArray<IArgumentOperation>)),
            };
            if (method is null || ReferenceEquals(other, call) || !SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, callee.OriginalDefinition))
            {
                continue;
            }

            var corrected = true;
            foreach (var member in cycle)
            {
                var at = IndexOf(others, candidates[member].Target!);
                if (at < 0 || !OperationEquivalence.AreEquivalent(others[at].Value, arguments[member].Value))
                {
                    corrected = false;
                    break;
                }
            }

            if (corrected)
            {
                return true;
            }
        }

        return false;
    }

    // The argument's name as lists of words, the most specific first: the member, then each receiver, as [id] and [invoice] for invoice.Id.
    // A cast, an await, a sign, a constant offset or a null fallback keeps the name of what it is applied to.
    // Loose matching also names a call of one argument, as Math.Abs(height), or a 'new' of one, by that argument; tight matching names them by the method or type only.
    private static void AddLevels(IOperation? value, WordTable table, List<ImmutableArray<string>> levels, bool loose)
    {
        for (var steps = 0; value is not null && steps < MaximumSteps && levels.Count < MaximumLevels; steps++)
        {
            switch (value)
            {
                case IConversionOperation conversion:
                    value = conversion.Operand;
                    continue;

                case IAwaitOperation awaited:
                    value = awaited.Operation;
                    continue;

                case ICoalesceOperation coalesce:
                    value = coalesce.Value;
                    continue;

                case IUnaryOperation { OperatorKind: UnaryOperatorKind.Minus or UnaryOperatorKind.Plus } unary:
                    value = unary.Operand;
                    continue;

                case IBinaryOperation binary:
                    value = binary.RightOperand.ConstantValue.HasValue ? binary.LeftOperand
                        : binary.LeftOperand.ConstantValue.HasValue ? binary.RightOperand
                        : null;
                    continue;

                case IConditionalAccessOperation access:
                    value = access.WhenNotNull;
                    continue;

                case IConditionalAccessInstanceOperation:
                    value = AccessedValue(value);
                    continue;

                case IArrayElementReferenceOperation element:
                    value = element.ArrayReference;
                    continue;

                case IDelegateCreationOperation creation:
                    value = creation.Target;
                    continue;

                case IDeclarationExpressionOperation declaration:
                    value = declaration.Expression;
                    continue;

                case ILocalReferenceOperation local:
                    levels.Add(table.Of(local.Local.Name));
                    return;

                case IParameterReferenceOperation parameter:
                    levels.Add(table.Of(parameter.Parameter.Name));
                    return;

                // 'this' passed on its own is named by its type; as a receiver it adds nothing to the member's name.
                case IInstanceReferenceOperation instance:
                    if (levels.Count == 0 && instance.Type is { } type)
                    {
                        levels.Add(table.Of(type.Name));
                    }

                    return;

                case IPropertyReferenceOperation { Property.IsIndexer: true } indexed:
                    value = indexed.Instance;
                    continue;

                case IMemberReferenceOperation member:
                    levels.Add(table.Of(member.Member.Name));
                    if (loose)
                    {
                        AddQualifier(member.Instance, member, member.Member, table, levels);
                    }

                    value = member.Instance;
                    continue;

                case IInvocationOperation invocation:
                    levels.Add(table.OfCall(invocation.TargetMethod.Name));
                    if (loose)
                    {
                        AddQualifier(invocation.Instance, invocation, invocation.TargetMethod, table, levels);
                    }

                    value = ReceiverOf(invocation, loose);
                    continue;

                case IObjectCreationOperation created:
                    if (created.Type is { } createdType)
                    {
                        levels.Add(table.Of(createdType.Name));
                    }

                    value = loose && created.Arguments.Length == 1 ? created.Arguments[0].Value : null;
                    continue;

                default:
                    return;
            }
        }
    }

    private static IOperation? ReceiverOf(IInvocationOperation invocation, bool loose) =>
        invocation.Instance
        ?? ((loose && invocation.Arguments.Length == 1) || (invocation.TargetMethod.IsExtensionMethod && invocation.Arguments.Length > 0) ? invocation.Arguments[0].Value : null);

    // In loose matching a static member written with its type, as Limits.MaxWidth, carries the type's name as its receiver.
    // Tight matching leaves it out, because the type names what kind of value it is, as in ClientOrderId.None, not which one.
    private static void AddQualifier(IOperation? instance, IOperation operation, ISymbol member, WordTable table, List<ImmutableArray<string>> levels)
    {
        var name = operation.Syntax is InvocationExpressionSyntax invocation ? invocation.Expression : operation.Syntax;
        if (instance is null && member.IsStatic && name is MemberAccessExpressionSyntax && member.ContainingType is { } type && levels.Count < MaximumLevels)
        {
            levels.Add(table.Of(type.Name));
        }
    }

    // The receiver that a '?.' placeholder stands for: the operand of the conditional access whose right side holds the placeholder.
    private static IOperation? AccessedValue(IOperation placeholder)
    {
        var child = placeholder;
        for (var parent = placeholder.Parent; parent is not null; (child, parent) = (parent, parent.Parent))
        {
            if (parent is IConditionalAccessOperation access && access.WhenNotNull == child)
            {
                return access.Operation;
            }
        }

        return null;
    }

    // Whether the argument names 'target' and not 'own', judged on the words only one of the two parameters has.
    // A word that matches its own parameter at least as well as the target says the argument is where it belongs.
    // It settles the matter unless another word matches the target more surely, as 'vector' does next to the 'M' of vectorM passed for 'matrix'.
    private static Match Names(List<ImmutableArray<string>> levels, ImmutableArray<string> target, ImmutableArray<string> own)
    {
        var targetOnly = CountExcept(target, own);
        if (targetOnly == 0)
        {
            // Every word of the target is in the argument's own parameter too, as 'id' is in 'parentId', so only the whole name tells them apart.
            return CountExcept(own, target) > 0 && levels.Count == 1 && levels[0].SequenceEqual(target) ? new Match(Tier.Word, Cover.Exact, 0) : default;
        }

        for (var level = 0; level < levels.Count; level++)
        {
            var words = levels[level];
            var forOwn = Tier.None;
            var forTarget = Tier.None;
            foreach (var word in words)
            {
                var toOwn = Best(word, own, target);
                var toTarget = Best(word, target, own);
                if (IsBetter(toTarget, toOwn))
                {
                    forTarget = IsBetter(toTarget, forTarget) ? toTarget : forTarget;
                }
                else if (IsBetter(toOwn, forOwn))
                {
                    forOwn = toOwn;
                }
            }

            if (forOwn != Tier.None && !IsBetter(forTarget, forOwn))
            {
                return default;
            }

            var matched = 0;
            var worst = Tier.None;
            foreach (var name in target)
            {
                if (own.Contains(name))
                {
                    continue;
                }

                var best = Tier.None;
                foreach (var word in words)
                {
                    var tier = Compare(word, name);
                    if (IsBetter(tier, best) && IsBetter(tier, Best(word, own, target)))
                    {
                        best = tier;
                    }
                }

                if (best != Tier.None)
                {
                    matched++;
                    worst = worst == Tier.None || IsBetter(worst, best) ? best : worst;
                }
            }

            if (matched == 0)
            {
                continue;
            }

            var cover = matched < targetOnly ? Cover.Partial
                : worst != Tier.Word || !ContainsAll(words, target) ? Cover.Distinguishing
                : levels.Count == 1 && words.Length == target.Length ? Cover.Exact
                : Cover.Full;
            return new Match(worst, cover, level);
        }

        return default;
    }

    private static Tier Best(string word, ImmutableArray<string> names, ImmutableArray<string> except)
    {
        var best = Tier.None;
        foreach (var name in names)
        {
            if (!except.Contains(name) && Compare(word, name) is var tier && IsBetter(tier, best))
            {
                best = tier;
            }
        }

        return best;
    }

    private static bool IsBetter(Tier tier, Tier than) => tier != Tier.None && (than == Tier.None || tier < than);

    // The same word, a shortening of it ('src' for 'source', 'len' for 'length'), a single initial ('w' for 'width') or one slip of the keyboard ('heigth' for 'height').
    private static Tier Compare(string argument, string parameter)
    {
        if (string.Equals(argument, parameter, StringComparison.Ordinal))
        {
            return Tier.Word;
        }

        var (shorter, longer) = argument.Length <= parameter.Length ? (argument, parameter) : (parameter, argument);
        if (shorter[0] == longer[0] && char.IsLetter(shorter[0]) && shorter.Length < longer.Length)
        {
            if (longer.StartsWith(shorter, StringComparison.Ordinal))
            {
                return shorter.Length == 1 ? Tier.Initial : Tier.Abbreviation;
            }

            if (shorter.Length >= MinimumContractionLength && IsSubsequence(shorter, longer))
            {
                return Tier.Abbreviation;
            }
        }

        return shorter.Length >= MinimumEditLength && IsOneEdit(shorter, longer) ? Tier.Edit : Tier.None;
    }

    private static bool IsSubsequence(string shorter, string longer)
    {
        var next = 0;
        foreach (var c in longer)
        {
            if (next < shorter.Length && shorter[next] == c)
            {
                next++;
            }
        }

        return next == shorter.Length;
    }

    // One letter replaced, added or dropped, or two neighbours exchanged.
    private static bool IsOneEdit(string shorter, string longer)
    {
        if (longer.Length - shorter.Length > 1)
        {
            return false;
        }

        var i = 0;
        while (i < shorter.Length && shorter[i] == longer[i])
        {
            i++;
        }

        if (shorter.Length < longer.Length)
        {
            return SameFrom(shorter, i, longer, i + 1);
        }

        return i < shorter.Length
            && (SameFrom(shorter, i + 1, longer, i + 1)
                || (i + 1 < shorter.Length && shorter[i] == longer[i + 1] && shorter[i + 1] == longer[i] && SameFrom(shorter, i + 2, longer, i + 2)));
    }

    private static bool SameFrom(string x, int xStart, string y, int yStart) => x.AsSpan(xStart).SequenceEqual(y.AsSpan(yStart));

    private static int CountExcept(ImmutableArray<string> words, ImmutableArray<string> except)
    {
        var count = 0;
        foreach (var word in words)
        {
            if (!except.Contains(word))
            {
                count++;
            }
        }

        return count;
    }

    private static bool ContainsAll(ImmutableArray<string> words, ImmutableArray<string> all)
    {
        foreach (var word in all)
        {
            if (!words.Contains(word))
            {
                return false;
            }
        }

        return true;
    }

    // The parameter the name points at must take the argument as written: the same ref kind, and a type the argument converts to implicitly.
    private static bool Accepts(IParameterSymbol target, IParameterSymbol own, IArgumentOperation argument)
    {
        if (target.RefKind != own.RefKind)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(target.Type, own.Type))
        {
            return true;
        }

        return target.RefKind is not (RefKind.Ref or RefKind.Out)
            && argument.SemanticModel is { } model
            && ExpressionOf(argument) is ExpressionSyntax expression
            && model.ClassifyConversion(expression, target.Type).IsImplicit;
    }

    private static int IndexOf(ImmutableArray<IArgumentOperation> arguments, IParameterSymbol parameter)
    {
        for (var i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].ArgumentKind == ArgumentKind.Explicit && arguments[i].Parameter?.Ordinal == parameter.Ordinal)
            {
                return i;
            }
        }

        return -1;
    }

    // The receiver of an extension method called as 'receiver.Method(...)' is an argument without an argument node.
    private static SyntaxNode ExpressionOf(IArgumentOperation argument) => argument.Syntax switch
    {
        ArgumentSyntax syntax => syntax.Expression,
        AttributeArgumentSyntax syntax => syntax.Expression,
        var syntax => syntax,
    };

    private static bool IsNamed(IArgumentOperation argument) => argument.Syntax is ArgumentSyntax { NameColon: not null };

    private static IOperation SkipConversions(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static ISymbol? SymbolOf(IOperation value) => SkipConversions(value) switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation reference => reference.Parameter,
        IMemberReferenceOperation member => member.Member,
        _ => null,
    };

    // What an argument reads, given as a diagnostic tag: a parameter, a local, a member of this object or type, a member of another object, a constant or a computed value.
    private static string SourceOf(IOperation value) => SkipConversions(value) switch
    {
        IParameterReferenceOperation => "parameter",
        ILocalReferenceOperation => "local",
        IFieldReferenceOperation { Field.IsConst: true } => "constant",
        ILiteralOperation => "literal",
        INameOfOperation => "nameof",
        IInstanceReferenceOperation => "this",
        IMemberReferenceOperation { Instance: null } => "static",
        IMemberReferenceOperation { Instance: IInstanceReferenceOperation } => "own",
        IMemberReferenceOperation => "member",
        IInvocationOperation => "call",
        IObjectCreationOperation => "new",
        _ => "other",
    };

    // A string that spells the argument's name labels it, as "name" does in Add("name", name) and nameof(value) in NotNull(value, nameof(value)).
    private static bool Labels(IOperation label, IOperation value) =>
        label.ConstantValue is { HasValue: true, Value: string text }
        && SymbolOf(value) is { } symbol
        && string.Equals(text, symbol.Name, StringComparison.OrdinalIgnoreCase);

    // The receiver of an extension method called on it, as span is in span.SequenceEqual(other), which shows the receiver and never its parameter's name.
    private static bool IsCalledOn(ISymbol callee, IArgumentOperation argument) =>
        callee is IMethodSymbol { IsExtensionMethod: true } && argument.Parameter?.Ordinal == 0 && argument.Syntax is not ArgumentSyntax;

    // A parameter the caller forwards, a member of its own object, or a local standing where the parameter it names gets a static member or a constant.
    private static bool IsCallersOwn(IOperation value, IOperation? other) => SkipConversions(value) switch
    {
        IParameterReferenceOperation => true,
        IMemberReferenceOperation { Instance: IInstanceReferenceOperation } => true,
        ILocalReferenceOperation => other is not null && SkipConversions(other) is IMemberReferenceOperation { Instance: null },
        _ => false,
    };

    // The call's result is negated or complemented, as in -Distance(to, from).
    private static bool IsNegated(IOperation call)
    {
        var parent = call.Parent;
        while (parent is IConversionOperation)
        {
            parent = parent.Parent;
        }

        return parent is IUnaryOperation { OperatorKind: UnaryOperatorKind.Minus or UnaryOperatorKind.Not or UnaryOperatorKind.BitwiseNegation };
    }

    // The call sits in a lambda handed to an assertion that expects it to throw, as Assert.Throws does, so its arguments may be wrong on purpose.
    private static bool IsExpectedToThrow(IOperation call)
    {
        for (var operation = call.Parent; operation is not null; operation = operation.Parent)
        {
            if (operation is IAnonymousFunctionOperation
                && operation.Parent is IDelegateCreationOperation { Parent: IArgumentOperation { Parent: IInvocationOperation assertion } }
                && assertion.TargetMethod.Name.IndexOf("Throw", StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string Join(List<string> items)
    {
        var text = "'" + items[0] + "'";
        for (var i = 1; i < items.Count; i++)
        {
            text += (i == items.Count - 1 ? " and '" : ", '") + items[i] + "'";
        }

        return text;
    }

    // What the calls of one member share: its matching mode, the parameters a namesake can be, which of them it reads, and the first report of each repeat.
    // Tight matching reports a swap, or a namesake passed over, once per member: at the first call that shows it.
    // A member that repeats one, as a table of constructor calls does, repeats one choice, so one report shows it, and fixing it means reading every such call.
    private sealed class MemberScope(ISymbol owner, WordTable table, TypeFacts types, bool loose)
    {
        private readonly object _gate = new();
        private Dictionary<(ISymbol Symbol, uint Parameters), Diagnostic>? _first;
        private ImmutableArray<IParameterSymbol> _parameters;
        private IOperation? _readIn;
        private Dictionary<IParameterSymbol, bool>? _read;
        private Reach? _reach;

        public bool Loose { get; } = loose;

        public string[] Spellings(string name) => table.Spellings(name);

        // Whether anything in reach of the member may be spelled so, judged without a lookup: a name in its own code, a parameter in reach, a member of an enclosing type or one it derives from, or any static import, whose members are not listed.
        // A local or a lambda's variable in reach is declared in the member's code, and a parameter in reach belongs to the member or to its type's primary constructor.
        public bool MayDeclare(string spelling)
        {
            var reach = Volatile.Read(ref _reach);
            if (reach is null)
            {
                Interlocked.CompareExchange(ref _reach, FindReach(), null);
                reach = _reach!;
            }

            if (reach.StaticImports || reach.Names.Contains(spelling))
            {
                return true;
            }

            for (var type = MemberOf(owner).ContainingType; type is not null; type = type.ContainingType)
            {
                if (MayHaveMember(type, spelling))
                {
                    return true;
                }
            }

            return false;
        }

        private Reach FindReach()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var staticImports = types.GlobalStaticImports;
            foreach (var reference in owner.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax();
                staticImports |= HasStaticImport(syntax.SyntaxTree.GetRoot());
                foreach (var token in syntax.DescendantTokens())
                {
                    if (token.IsKind(SyntaxKind.IdentifierToken))
                    {
                        names.Add(token.ValueText);
                    }
                }
            }

            foreach (var parameter in Parameters)
            {
                names.Add(parameter.Name);
            }

            return new Reach(names, staticImports);
        }

        private sealed record Reach(HashSet<string> Names, bool StaticImports);

        // The member's own parameters and those of its type's primary constructor.
        public ImmutableArray<IParameterSymbol> Parameters
        {
            get
            {
                if (_parameters.IsDefault)
                {
                    var member = MemberOf(owner);
                    var parameters = member is IMethodSymbol method ? method.Parameters : [];
                    if (member.ContainingType is { } type && types.PrimaryParameters(type) is { IsEmpty: false } primary)
                    {
                        parameters = parameters.AddRange(primary);
                    }

                    ImmutableInterlocked.InterlockedInitialize(ref _parameters, parameters);
                }

                return _parameters;
            }
        }

        public void Analyze(OperationAnalysisContext context) => ArgumentNameAnalyzer.Analyze(context, table, this);

        // Whether the member reads the parameter, asked once per parameter for the operation block the call is in.
        public bool IsRead(IParameterSymbol parameter, IOperation call)
        {
            var root = call;
            while (root.Parent is { } parent)
            {
                root = parent;
            }

            lock (_gate)
            {
                if (_readIn != root)
                {
                    _readIn = root;
                    _read?.Clear();
                }

                _read ??= new Dictionary<IParameterSymbol, bool>(SymbolEqualityComparer.Default);
                if (!_read.TryGetValue(parameter, out var read))
                {
                    read = IsReferenced(call, parameter);
                    _read[parameter] = read;
                }

                return read;
            }
        }

        // The symbol is the callee of a swap or the namesake passed over; the bits are the ordinals of the swapped parameters.
        public void Add(ISymbol symbol, uint parameters, Diagnostic diagnostic)
        {
            lock (_gate)
            {
                _first ??= new Dictionary<(ISymbol, uint), Diagnostic>(KeyComparer.Instance);
                if (!_first.TryGetValue((symbol, parameters), out var earlier) || diagnostic.Location.SourceSpan.Start < earlier.Location.SourceSpan.Start)
                {
                    _first[(symbol, parameters)] = diagnostic;
                }
            }
        }

        public void Report(OperationBlockAnalysisContext context)
        {
            if (_first is null)
            {
                return;
            }

            foreach (var diagnostic in _first.Values)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        private sealed class KeyComparer : IEqualityComparer<(ISymbol Symbol, uint Parameters)>
        {
            public static readonly KeyComparer Instance = new();

            public bool Equals((ISymbol Symbol, uint Parameters) x, (ISymbol Symbol, uint Parameters) y) =>
                x.Parameters == y.Parameters && SymbolEqualityComparer.Default.Equals(x.Symbol, y.Symbol);

            public int GetHashCode((ISymbol Symbol, uint Parameters) key) => SymbolEqualityComparer.Default.GetHashCode(key.Symbol) ^ (int)key.Parameters;
        }
    }

    // What the members of a compilation share about its types: each type's primary constructor parameters, and whether a file imports static members for every file.
    // A type builds its constructor list, and a constructor its syntax references, anew on each request, so each type is asked once.
    private sealed class TypeFacts(Compilation compilation)
    {
        private readonly ConcurrentDictionary<INamedTypeSymbol, ImmutableArray<IParameterSymbol>> _primaryParameters = new(SymbolEqualityComparer.Default);
        private readonly Lazy<bool> _globalStaticImports = new(() => HasGlobalStaticImport(compilation));

        public bool GlobalStaticImports => _globalStaticImports.Value;

        // A record's primary constructor parameters are read through its properties, so they are never namesakes (see IsStoredInMember).
        public ImmutableArray<IParameterSymbol> PrimaryParameters(INamedTypeSymbol type) => _primaryParameters.GetOrAdd(type, static type =>
        {
            if (!type.IsRecord)
            {
                foreach (var constructor in type.InstanceConstructors)
                {
                    if (!constructor.Parameters.IsEmpty && IsPrimary(constructor))
                    {
                        return constructor.Parameters;
                    }
                }
            }

            return [];
        });
    }

    // Names repeat throughout a compilation, so each is split into words once.
    // The tables start large because a project has thousands of names, and each growth of a table locks out every thread adding to it.
    // Analyzers may not read the processor count (RS1035), so the tables have a fixed number of lock stripes.
    private sealed class WordTable
    {
        private const int InitialCapacity = 4096;
        private const int Stripes = 32;

        private readonly ConcurrentDictionary<string, ImmutableArray<string>> _words = new(Stripes, InitialCapacity, StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ImmutableArray<string>> _calls = new(Stripes, InitialCapacity / 4, StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string[]> _spellings = new(Stripes, InitialCapacity / 4, StringComparer.Ordinal);

        // How a variable or member named like a parameter is spelled: the name in lower and upper camel case, and the lower one behind an underscore.
        public string[] Spellings(string name) => _spellings.GetOrAdd(name, static name =>
        {
            var lower = char.ToLowerInvariant(name[0]) + name.Substring(1);
            return [lower, char.ToUpperInvariant(name[0]) + name.Substring(1), "_" + lower];
        });

        public ImmutableArray<string> Of(string name) => _words.GetOrAdd(name, static name => Words.Split(name));

        // A conversion says what it makes, as ToBaseAmount, ToDateTime and AsSpan do, and its leading 'to' or 'as' names no parameter.
        public ImmutableArray<string> OfCall(string name) => _calls.GetOrAdd(name, static name =>
            name.Length > 2 && (name.StartsWith("To", StringComparison.Ordinal) || name.StartsWith("As", StringComparison.Ordinal)) && char.IsUpper(name[2])
                ? Words.Split(name.Substring(2))
                : Words.Split(name));
    }

    private readonly record struct Match(Tier Tier, Cover Cover, int Level)
    {
        // A complete match beats a partial one, then the surer kind of word match, then the fuller name.
        public bool IsBetterThan(Match other) => Rank() < other.Rank();

        public static Match Worse(Match x, Match y) => x.Rank() >= y.Rank() ? x : y;

        private int Rank() => ((Cover == Cover.Partial ? 1 : 0) * 1000) + ((int)Tier * 100) + ((int)Cover * 10) + Level;
    }

    private readonly record struct Candidate(Match Match, IParameterSymbol? Target, int At, bool SwapOnly);
}
