using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Typo.Core;

namespace Typo.Analyzers;

/// <summary>
/// TYPO0015 and TYPO0016: an argument named like a parameter other than the one it is passed for, as in <c>ProcessInvoice(invoiceId, customerId)</c> for <c>ProcessInvoice(Guid customerId, Guid invoiceId)</c>.
/// </summary>
/// <remarks>
/// A name is compared as words, in lower case and without a field prefix, so <c>_invoiceId</c>, <c>InvoiceId</c> and <c>order.InvoiceId</c> all read 'invoice', 'id'.
/// Two parameters are told apart by the words only one of them has: 'customer' and 'invoice' for customerId and invoiceId.
/// An argument names another parameter when it holds every such word of that parameter and none of its own parameter's.
/// An argument is read level by level: the member first, and its receiver only when the member says nothing about either parameter, as 'Id' does in <c>invoice.Id</c>.
/// TYPO0015 reports arguments that name each other's parameters, in a swap or a longer rotation, when the call still binds with them exchanged.
/// TYPO0016 reports an argument that names another parameter with nothing naming its own in return.
/// The other parameter must accept the argument, and the same value passed for both parameters is left to TYPO0012.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ArgumentNameAnalyzer : DiagnosticAnalyzer
{
    private const int MaximumParameters = 32;
    private const int MaximumLevels = 4;
    private const int MaximumSteps = 16;

    // Shorter words differ from many others by one letter, as 'min' and 'max' do.
    private const int MinimumEditLength = 5;

    // A shorter run of letters is found inside most longer words, as 'id' is in 'index'.
    private const int MinimumContractionLength = 3;

    private static readonly HashSet<string> s_reversalWords = new(StringComparer.Ordinal)
    {
        "backward", "backwards", "complement", "endian", "flip", "flipped", "inverse", "invert", "inverted", "landscape", "mirror", "mirrored", "opposite", "portrait",
        "reciprocal", "reverse", "reversed", "rotate", "rotated", "rotation", "swap", "swapped", "transpose", "transposed", "undo",
    };

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
            start.RegisterOperationAction(context => Analyze(context, table), OperationKind.Invocation, OperationKind.ObjectCreation, OperationKind.PropertyReference);
        });
    }

    // Implicit calls (collection-initializer Add, LINQ query methods, attribute constructors) have no argument list the user wrote.
    private static void Analyze(OperationAnalysisContext context, WordTable table)
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

        var levels = t_levels ??= new List<ImmutableArray<string>>(MaximumLevels);
        Candidate[]? candidates = null;
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            if (argument.ArgumentKind != ArgumentKind.Explicit || argument.Parameter is not { } own)
            {
                continue;
            }

            levels.Clear();
            AddLevels(argument.Value, table, levels);
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

                var match = Names(levels, table.Of(target.Name), ownWords);
                if (match.Tier == Tier.None || (best.Target is not null && !match.IsBetterThan(best.Match)))
                {
                    continue;
                }

                var at = IndexOf(arguments, target);
                if (Accepts(target, own, argument) && (at < 0 || !OperationEquivalence.AreEquivalent(argument.Value, arguments[at].Value)))
                {
                    best = new Candidate(match, target, at);
                }
            }

            if (best.Target is not null)
            {
                candidates ??= new Candidate[arguments.Length];
                candidates[i] = best;
            }
            else if (IsNamedForSomethingElse(levels, ownWords, out var shared))
            {
                ReportNamesake(context, callee, arguments, argument, shared);
            }
        }

        if (candidates is not null)
        {
            Report(context, callee, arguments, candidates, table);
        }
    }

    // An argument that names the parameter of one that names it back, directly or through others, is part of a swap; any other is reported on its own.
    private static void Report(OperationAnalysisContext context, ISymbol callee, ImmutableArray<IArgumentOperation> arguments, Candidate[] candidates, WordTable table)
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

            ReportSwap(context, callee, arguments, candidates, cycle);
        }

        for (var i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].Target is not null && !swapped[i])
            {
                ReportMisnamed(context, callee, arguments, candidates[i], i, table);
            }
        }
    }

    private static void ReportSwap(OperationAnalysisContext context, ISymbol callee, ImmutableArray<IArgumentOperation> arguments, Candidate[] candidates, List<int> cycle)
    {
        var call = context.Operation.Syntax;
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
        var named = false;
        var forwarded = true;
        ISymbol? forwarder = null;
        var locations = new List<Location>(cycle.Count - 1);
        var passed = new List<string>(cycle.Count);
        var declared = new List<string>(cycle.Count);
        foreach (var member in cycle)
        {
            var argument = arguments[member];
            var expression = ExpressionOf(argument);
            passed.Add(Quote.Of(expression));
            declared.Add(argument.Parameter!.Name);
            if (member != cycle[0])
            {
                locations.Add(expression.GetLocation());
                match = Match.Worse(match, candidates[member].Match);
            }

            named |= IsNamed(argument);
            var source = SkipConversions(argument.Value) is IParameterReferenceOperation reference ? reference.Parameter.ContainingSymbol : null;
            forwarded &= source is not null && (forwarder is null || SymbolEqualityComparer.Default.Equals(forwarder, source));
            forwarder ??= source;
        }

        var properties = Properties(callee, match, named);
        properties["shape"] = cycle.Count == 2 ? "swap" : "rotation";
        AddContext(properties, "forwarded", forwarded);
        AddContext(properties, "recursive", SymbolEqualityComparer.Default.Equals(callee.OriginalDefinition, context.ContainingSymbol.OriginalDefinition));
        AddContext(properties, "reversal", IsReversal(context.ContainingSymbol) || IsReversal(callee));
        AddContext(properties, "bothOrders", HasCorrectedOrderElsewhere(context.Operation, callee, arguments, candidates, cycle));
        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.SwappedArguments,
            ExpressionOf(arguments[cycle[0]]).GetLocation(),
            additionalLocations: locations,
            properties: properties.ToImmutable(),
            messageArgs: [Join(passed), Join(declared)]));
    }

    private static void ReportMisnamed(OperationAnalysisContext context, ISymbol callee, ImmutableArray<IArgumentOperation> arguments, Candidate candidate, int index, WordTable table)
    {
        var argument = arguments[index];
        var expression = ExpressionOf(argument);
        var properties = Properties(callee, candidate.Match, IsNamed(argument));
        properties["shape"] = "misnamed";
        properties["target"] = TargetState(arguments, candidate, table);
        if (context.Operation.SemanticModel is { } model && FindNamesake(model, argument, expression, out _) is { } namesake)
        {
            properties["inScope"] = IsReferenced(context.Operation, namesake) ? "used" : "unused";
        }

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
    private static void ReportNamesake(OperationAnalysisContext context, ISymbol callee, ImmutableArray<IArgumentOperation> arguments, IArgumentOperation argument, bool shared)
    {
        var expression = ExpressionOf(argument);
        if (context.Operation.SemanticModel is not { } model || FindNamesake(model, argument, expression, out var display) is not { } namesake)
        {
            return;
        }

        foreach (var other in arguments)
        {
            // The same value for two parameters is TYPO0012's finding.
            if (other != argument && OperationEquivalence.AreEquivalent(other.Value, argument.Value))
            {
                return;
            }

            var passed = SkipConversions(other.Value) switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation reference => reference.Parameter,
                IMemberReferenceOperation member => member.Member,
                _ => (ISymbol?)null,
            };
            if (SymbolEqualityComparer.Default.Equals(passed, namesake))
            {
                return;
            }
        }

        var properties = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        properties["callee"] = callee.ContainingType is { } type ? type.Name + "." + callee.Name : callee.Name;
        properties["shape"] = "namesake";
        properties["kind"] = display.Length > namesake.Name.Length ? "sibling" : namesake.Kind.ToString();
        properties["inScope"] = namesake is ILocalSymbol or IParameterSymbol && !IsReferenced(context.Operation, namesake) ? "unused" : "used";
        AddContext(properties, "shared", shared);
        AddContext(properties, "named", IsNamed(argument));
        AddContext(properties, "sameType", SymbolEqualityComparer.Default.Equals(TypeOf(namesake), SkipConversions(argument.Value).Type));
        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.ArgumentPassedOverNamesake,
            expression.GetLocation(),
            properties: properties.ToImmutable(),
            messageArgs: [Quote.Of(expression), argument.Parameter!.Name, display]));
    }

    // A variable or member in reach that is spelled like the argument's own parameter and that the parameter would accept.
    // It is looked for by name in scope first, then among the members of the object the argument is read from.
    private static ISymbol? FindNamesake(SemanticModel model, IArgumentOperation argument, SyntaxNode expression, out string display)
    {
        display = string.Empty;
        var parameter = argument.Parameter!;
        var name = parameter.Name;
        if (name.Length == 0)
        {
            return null;
        }

        var value = SkipConversions(argument.Value);
        var passed = value switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation reference => reference.Parameter,
            IMemberReferenceOperation member => member.Member,
            _ => (ISymbol?)null,
        };
        var position = expression.SpanStart;
        var lower = char.ToLowerInvariant(name[0]) + name.Substring(1);
        var upper = char.ToUpperInvariant(name[0]) + name.Substring(1);
        foreach (var spelling in (string[])[lower, upper, "_" + lower])
        {
            foreach (var symbol in model.LookupSymbols(position, name: spelling))
            {
                if (IsNamesake(model, symbol, passed, parameter, position))
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

        foreach (var spelling in (string[])[upper, lower])
        {
            foreach (var symbol in model.LookupSymbols(position, owner, spelling))
            {
                if (!symbol.IsStatic && IsNamesake(model, symbol, passed, parameter, position))
                {
                    display = Quote.Of(receiver) + "." + symbol.Name;
                    return symbol;
                }
            }
        }

        return null;
    }

    // Lookup also returns locals declared later in the block, which the call cannot have meant.
    private static bool IsNamesake(SemanticModel model, ISymbol symbol, ISymbol? passed, IParameterSymbol parameter, int position)
    {
        if (SymbolEqualityComparer.Default.Equals(symbol, passed)
            || TypeOf(symbol) is not { } type
            || (symbol is ILocalSymbol local && !local.Locations.Any(location => location.IsInSource && location.SourceSpan.Start < position)))
        {
            return false;
        }

        return model.Compilation.ClassifyConversion(type, parameter.Type).IsImplicit;
    }

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
    private static string TargetState(ImmutableArray<IArgumentOperation> arguments, Candidate candidate, WordTable table)
    {
        if (candidate.At < 0)
        {
            return "omitted";
        }

        var levels = new List<ImmutableArray<string>>(MaximumLevels);
        AddLevels(arguments[candidate.At].Value, table, levels);
        if (levels.Count == 0)
        {
            return "unnamed";
        }

        foreach (var word in levels[0])
        {
            if (Best(word, table.Of(candidate.Target!.Name), []) != Tier.None)
            {
                return "fits";
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
    // A call of one argument, a cast, an await, a sign, a constant offset or a null fallback keeps the name of what it is applied to.
    private static void AddLevels(IOperation? value, WordTable table, List<ImmutableArray<string>> levels)
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
                    AddQualifier(member.Instance, member, member.Member, table, levels);
                    value = member.Instance;
                    continue;

                case IInvocationOperation invocation:
                    levels.Add(table.Of(invocation.TargetMethod.Name));
                    AddQualifier(invocation.Instance, invocation, invocation.TargetMethod, table, levels);
                    value = invocation.Instance
                        ?? (invocation.Arguments.Length == 1 || (invocation.TargetMethod.IsExtensionMethod && invocation.Arguments.Length > 0) ? invocation.Arguments[0].Value : null);
                    continue;

                case IObjectCreationOperation created:
                    if (created.Type is { } createdType)
                    {
                        levels.Add(table.Of(createdType.Name));
                    }

                    value = created.Arguments.Length == 1 ? created.Arguments[0].Value : null;
                    continue;

                default:
                    return;
            }
        }
    }

    // A static member written with its type, as Limits.MaxWidth, carries the type's name as its receiver.
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

    private static string Join(List<string> items)
    {
        var text = "'" + items[0] + "'";
        for (var i = 1; i < items.Count; i++)
        {
            text += (i == items.Count - 1 ? " and '" : ", '") + items[i] + "'";
        }

        return text;
    }

    // Names repeat throughout a compilation, so each is split into words once.
    private sealed class WordTable
    {
        private readonly ConcurrentDictionary<string, ImmutableArray<string>> _words = new(StringComparer.Ordinal);

        public ImmutableArray<string> Of(string name) => _words.GetOrAdd(name, static name => Words.Split(name));
    }

    private readonly record struct Match(Tier Tier, Cover Cover, int Level)
    {
        // A complete match beats a partial one, then the surer kind of word match, then the fuller name.
        public bool IsBetterThan(Match other) => Rank() < other.Rank();

        public static Match Worse(Match x, Match y) => x.Rank() >= y.Rank() ? x : y;

        private int Rank() => ((Cover == Cover.Partial ? 1 : 0) * 1000) + ((int)Tier * 100) + ((int)Cover * 10) + Level;
    }

    private readonly record struct Candidate(Match Match, IParameterSymbol? Target, int At);
}
