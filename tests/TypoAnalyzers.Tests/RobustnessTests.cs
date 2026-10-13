using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using TypoAnalyzers.Core;
using Xunit;

namespace TypoAnalyzers.Tests;

public sealed class RobustnessTests
{
    // Code as it looks mid-typing in the IDE: missing operands, unknown symbols, unbalanced braces.
    public static TheoryData<string> IncompleteCode =>
    [
        "class C { bool M(int a) => a == || a == ; }",
        "class C { bool M(string s) => s.ToLower().Contains( ; }",
        "class C { void M() { if (Foo(bar) || Foo(bar)) { } } }",
        "class C { int M(object o) => o switch { int => Missing(), int => Missing(), }; }",
        "class C { void M(int[] a) { for (var i = 0; i < a.Length; i++) { if (a[0] == ) { } } } }",
        "class C { void M() { X = 1; X = 1; } }",
        "class C { void M(Unknown u) { if (u.A) { if (u.A) { } } if (u.A) { } } }",
        "class C { C() { First = first; Middle = middle; Last = middle; } }",
        "class C { void M(int x) { switch (x) { case 1: Do(); break; case 1: Do(); break; } } }",
        "class C { void M() { var v = new Vec { X = a.X, Y = a.Y, Z = a.Y }; } }",
        "class C { bool M(string s) => s.ToUpper() switch { \"ok\" => true, }",
        "class C { bool M(string s) => s.ToLower() == $\"A{",
        "class C { void M() { if (",
        "class C { int M(int[] a) { var t = 0; for (int i; i >= 0; i--) { t += a[0]; } return t; } }",
        "class C { bool M(string s) => string.Equals(s); }",
        "class C { void M(P b) { var a = b.X; var c = b. } }",
        "class C { void M(P p) { var a = p.X, ; var = p.Y; } }",
        "class C { void Foo(int x, int y) { } void M(P p) { Foo(p.X, p. } }",
        "class C { void Foo(int x, int y) { } void M(int a) { Foo(y: a, z: a); Foo(y: a, y: a); Foo(a, x: a); } }",
        "class C { C(int x, int y) { } C(int p) : this(p, ) { } }",
        "class C(int p) : B(p, p, ;",
    ];

    [Theory]
    [MemberData(nameof(IncompleteCode))]
    public async Task IncompleteCodeDoesNotCrashAnyAnalyzer(string source)
    {
        var run = await AllAnalyzers.RunAsync(source, TestContext.Current.CancellationToken);

        Assert.Empty(run.Exceptions);
        Assert.DoesNotContain(run.Diagnostics, diagnostic => diagnostic.Id == "AD0001");
    }

    [Fact]
    public async Task VeryLongChainsAreAnalyzedWithoutErrors()
    {
        var chain = string.Join(" || ", Enumerable.Range(0, 3000).Select(i => $"a == {i}"));
        var source = $$"""
            public sealed class Node
            {
                public int X;
                public int P { get; set; }
                public int this[bool flag] { get => 0; set { } }
                public int M() => X;
            }

            public static class C
            {
                private static void Use(bool value)
                {
                }

                public static int M(int a, Node p, Node q, string s)
                {
                    if ({{chain}}) { return 1; }
                    if ({{chain}}) { return 2; }
                    var x = {{chain}};
                    var y = {{chain}};
                    x = {{chain}};
                    y = {{chain}};
                    x = {{chain}};
                    Use({{chain}});
                    Use({{chain}});
                    Use({{chain}});
                    ({{chain}} ? p : q).X = 1;
                    ({{chain}} ? p : q).X = 2;
                    ({{chain}} ? p : q).X = 3;
                    ({{chain}} ? p : q).P = 1;
                    ({{chain}} ? p : q).P = 2;
                    ({{chain}} ? p : q).P = 3;
                    p[{{chain}}] = 1;
                    p[{{chain}}] = 2;
                    p[{{chain}}] = 3;
                    ({{chain}} ? p : q).M();
                    ({{chain}} ? p : q).M();
                    ({{chain}} ? p : q).M();
                    var text = s.ToLower() == "A" + s + {{string.Join(" + ", Enumerable.Range(0, 400).Select(_ => "s"))}};
                    return x && y && text ? 3 : 0;
                }
            }
            """;
        var compilation = await AllAnalyzers.CompileAsync(source, TestContext.Current.CancellationToken);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));

        var run = await AllAnalyzers.RunAsync(source, TestContext.Current.CancellationToken);

        Assert.Empty(run.Exceptions);
        Assert.DoesNotContain(run.Diagnostics, diagnostic => diagnostic.Id == "AD0001");
    }

    [Fact]
    public async Task LongRunsOfGuardClausesAreAnalyzed()
    {
        var guards = string.Concat(Enumerable.Range(0, 3000).Select(i => $"if (a == {i}) {{ return {i}; }}\n"));
        var source = $$"""
            public static class C
            {
                public static int M(int a)
                {
                    {{guards}}
                    if (a == 0) { return -1; }
                    return -2;
                }
            }
            """;

        var run = await AllAnalyzers.RunAsync(source, TestContext.Current.CancellationToken);

        Assert.Empty(run.Exceptions);
        Assert.Single(run.Diagnostics, diagnostic => diagnostic.Id == "TYPO0004");
    }

    [Fact]
    public async Task RecursiveHelpersStopAtTheDepthLimit()
    {
        var chain = string.Join(" || ", Enumerable.Range(0, 5000).Select(i => $"a == {i}"));
        var patterns = string.Join(" or ", Enumerable.Range(0, 600).Select(i => $"{i}"));
        var compilation = await AllAnalyzers.CompileAsync(
            $"public static class C {{ public static bool M(int a) => {chain}; public static bool P(int a) => a is {patterns}; }}",
            TestContext.Current.CancellationToken);
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var bodies = tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<ArrowExpressionClauseSyntax>().ToArray();
        var operation = model.GetOperation(bodies[0].Expression, TestContext.Current.CancellationToken)!;
        var pattern = ((IIsPatternOperation)model.GetOperation(bodies[1].Expression, TestContext.Current.CancellationToken)!).Pattern;
        var purity = PurityOracle.For(compilation);

        // A small stack turns a missing depth guard into a crash here rather than in a user's compiler or IDE.
        bool? equivalent = null;
        bool? free = null;
        bool? patternsEquivalent = null;
        var thread = new Thread(
            () =>
            {
                equivalent = OperationEquivalence.AreEquivalent(operation, operation);
                free = purity.IsFree(operation);
                patternsEquivalent = OperationEquivalence.PatternsEquivalent(pattern, pattern);
                _ = OperationEquivalence.GetHashCode(operation);
                _ = OperationEquivalence.GetPatternHashCode(pattern);
            },
            maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();

        Assert.False(equivalent);
        Assert.False(free);
        Assert.False(patternsEquivalent);
    }
}
