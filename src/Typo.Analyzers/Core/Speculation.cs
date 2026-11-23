using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Typo.Core;

/// <summary>
/// Binds code as it would read after a suggested edit, so a rule suggests only what compiles.
/// </summary>
internal static class Speculation
{
    // Overloads and type inference may pick another method for the edited arguments, so the whole call is bound again.
    // A target-typed 'new(...)' cannot be bound on its own and is left alone.
    // Member bindings need their enclosing conditional-access chain while the edited inner call is bound.
    // Only an edit is blamed: a call that does not bind before the edit, as in code being typed, counts as binding.
    public static bool CallBinds(SemanticModel model, SyntaxNode call, SyntaxNode edited)
    {
        var context = call;
        for (var parent = call.Parent; parent is ExpressionSyntax; parent = parent.Parent)
        {
            if (parent is ConditionalAccessExpressionSyntax)
            {
                context = parent;
            }
        }

        if (context != call)
        {
            var annotation = new SyntaxAnnotation();
            var root = context.ReplaceNode(call, edited.WithAdditionalAnnotations(annotation));
            edited = root.GetAnnotatedNodes(annotation).Single();
        }

        SymbolInfo? after = edited switch
        {
            ImplicitObjectCreationExpressionSyntax => null,
            ExpressionSyntax expression => model.GetSpeculativeSymbolInfo(call.SpanStart, expression, SpeculativeBindingOption.BindAsExpression),
            ConstructorInitializerSyntax initializer => model.GetSpeculativeSymbolInfo(call.SpanStart, initializer),
            PrimaryConstructorBaseTypeSyntax baseType => model.GetSpeculativeSymbolInfo(call.SpanStart, baseType),
            _ => null,
        };

        return after is not { Symbol: null } || model.GetSymbolInfo(call).Symbol is null;
    }
}
