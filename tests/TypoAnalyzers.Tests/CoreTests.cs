using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TypoAnalyzers.Core;
using Xunit;

namespace TypoAnalyzers.Tests;

public sealed class CoreTests
{
    private const string Prelude = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Threading.Tasks;

        public sealed class Box
        {
            public int X;
            public int Y;
            public readonly int R;
            public int[] Items = [];
            public Box Next;
            public int P { get; set; }
            public int this[int index] => index;
            public int M() => X;
            public static int Plain(int value) => value;
        }

        public sealed class Money : IComparable<Money>
        {
            public override bool Equals(object other) => true;
            public override int GetHashCode() => 0;
            public int CompareTo(Money other) => 0;
        }

        public enum Color { Red, Green }

        public struct Point { public int X; }

        public static class Helpers
        {
            public static void Ref(ref int value) => value++;
            public static void Out(out int value) => value = 0;
        }

        """;

    public static TheoryData<string, string, bool> EquivalenceCases => new()
    {
        { "i", "i", true },
        { "i", "j", false },
        { "a.X", "a.X", true },
        { "a.X", "b.X", false },
        { "a.X", "a.Y", false },
        { "a.Next.X", "a.Next.X", true },
        { "a.P", "a.P", true },
        { "a[1]", "a[1]", true },
        { "a[1]", "a[2]", false },
        { "arr[i]", "arr[i]", true },
        { "arr[i]", "arr[j]", false },
        { "arr.Length", "arr.Length", true },
        { "i + j", "j + i", true },
        { "i - j", "j - i", false },
        { "i == j", "j == i", true },
        { "i < j", "j > i", true },
        { "i <= j", "j >= i", true },
        { "i < j", "j >= i", false },
        { "i < j", "i > j", false },
        { "-i", "-i", true },
        { "-i", "~i", false },
        { "!(i > 0)", "!(i > 0)", true },
        { "(long)i", "(long)i", true },
        { "(long)i", "(short)i", false },
        { "1", "1L", false },
        { "o is string", "o is string", true },
        { "o is int", "o is int?", true },
        { "o is string", "o is int", false },
        { "i is > 5 and < 10", "i is < 10 and > 5", true },
        { "i is > 5", "i is >= 5", false },
        { "i is not 3", "i is not 3", true },
        { "i is 1 or 2", "i is 2 or 1", true },
        { "o is string _", "o is string _", true },
        { "a?.X", "a?.X", true },
        { "a?.X", "b?.X", false },
        { "i > 0 ? i : j", "i > 0 ? i : j", true },
        { "i > 0 ? i : j", "i > 0 ? j : i", false },
        { "n ?? 0", "n ?? 0", true },
        { "n ?? 0", "n ?? 1", false },
        { "typeof(int)", "typeof(int)", true },
        { "typeof(int)", "typeof(long)", false },
        { "default(int)", "default(int)", true },
        { "nameof(a)", "nameof(a)", true },
        { "sizeof(int)", "sizeof(int)", true },
        { "a.M()", "a.M()", true },
        { "Box.Plain(i)", "Box.Plain(j)", false },
        { "Math.Max(i, j)", "Math.Max(i, j)", true },
        { "new Box()", "new Box()", false },
        { "$\"{i}\"", "$\"{i}\"", false },
        { "o is string x1", "o is string x2", false },
        { "i is 1 or _", "i is 1 or _", true },
        { "s is { Length: 1 }", "s is { Length: 1 }", false },
    };

    public static TheoryData<string, string, bool> SameTestCases => new()
    {
        { "s == null", "s is null", true },
        { "null == s", "s is null", true },
        { "s != null", "s is not null", true },
        { "s == null", "s != null", false },
        { "i > 0", "i > 0", true },
        { "i > 0", "0 < i", true },
        { "s != null", "null != s", true },
    };

    public static TheoryData<string, string, bool> OppositeCases => new()
    {
        { "s == null", "s != null", true },
        { "s is null", "s is not null", true },
        { "s == null", "s is not null", true },
        { "i > 0", "!(i > 0)", true },
        { "!(i > 0)", "i > 0", true },
        { "i < j", "i >= j", true },
        { "i > j", "i <= j", true },
        { "i != j", "i == j", true },
        { "i >= j", "i < j", true },
        { "i <= j", "i > j", true },
        { "i == j", "j != i", true },
        { "d < 1.0", "d >= 1.0", false },
        { "dn < 1.0", "dn >= 1.0", false },
        { "i > 0", "o is string", false },
        { "i + j", "i - j", false },
        { "o is string", "o is not string", true },
        { "o is not string", "o is string", true },
        { "i is 1 or 2", "i is not (1 or 2)", true },
        { "i == j", "i == j", false },
        { "s is null", "s == null", false },
        { "i > 0", "j > 0", false },
    };

    public static TheoryData<string, bool> PurityCases => new()
    {
        { "i", true },
        { "a.X", true },
        { "a.P", true },
        { "arr[i]", true },
        { "s.Length", true },
        { "s.ToLower()", true },
        { "Math.Max(i, j)", true },
        { "HashCode.Combine(i, j)", true },
        { "list.Contains(i)", true },
        { "list.Count", true },
        { "a.Equals(b)", true },
        { "i.CompareTo(j)", true },
        { "o is string", true },
        { "a?.X", true },
        { "n ?? 0", true },
        { "i > 0 ? i : j", true },
        { "typeof(int)", true },
        { "n.GetValueOrDefault()", true },
        { "day.HasFlag(DayOfWeek.Monday)", true },
        { "dt.AddDays(1)", true },
        { "m.Equals(m)", true },
        { "m.GetHashCode()", true },
        { "m.CompareTo(m)", true },
        { "a.M()", false },
        { "Box.Plain(i)", false },
        { "i++", false },
        { "new Box()", false },
        { "list.Remove(i)", false },
        { "int.TryParse(s, out var parsed)", false },
        { "Guid.NewGuid()", false },
        { "DateTime.Now", false },
        { "sw.ElapsedMilliseconds", false },
    };

    public static TheoryData<string, bool> ExitCases => new()
    {
        { "return;", true },
        { "throw new Exception();", true },
        { "break;", true },
        { "continue;", true },
        { "i++;", false },
        { "{ i++; return; }", true },
        { "{ }", false },
        { "if (j > 0) { return; } else { break; }", true },
        { "if (j > 0) { return; }", false },
        { "if (j > 0) { return; } else { i++; }", false },
    };

    public static TheoryData<string, string, string, bool> MutationCases => new()
    {
        { "", "i > 0", "j = 1;", false },
        { "", "i > 0", "i = 1;", true },
        { "", "i > 0", "i++;", true },
        { "", "i > 0", "(i, j) = (1, 2);", true },
        { "", "i > 0", "(j, _) = (1, 2);", false },
        { "Action log = () => Console.WriteLine();", "i > 0", "log();", false },
        { "", "i > 0", "Helpers.Ref(ref i);", true },
        { "", "i > 0", "Helpers.Out(out i);", true },
        { "", "i > 0", "a.M();", false },
        { "", "a.X > 0", "a.M();", true },
        { "", "a.X > 0", "Math.Max(1, 2);", false },
        { "", "a.X > 0", "a.Y = 1;", false },
        { "", "a.X > 0", "b.X = 1;", true },
        { "", "arr[0] > 0", "arr[1] = 2;", true },
        { "", "((int[])o)[0] > 0", "((int[])o)[0] = 1;", true },
        { "", "a.R > 0", "a.M();", false },
        { "", "a.X > 0", "await Task.Yield();", true },
        { "", "i > 0", "await Task.Yield();", false },
        { "", "i > 0", "dyn.Foo();", true },
        { "void Bump() { i++; }", "i > 0", "Bump();", true },
        { "Action bump = () => i++;", "i > 0", "bump();", true },
        { "", "i > 0", "Console.WriteLine();", false },
    };

    [Theory]
    [MemberData(nameof(EquivalenceCases))]
    public async Task Equivalence(string first, string second, bool expected)
    {
        var (_, x, y) = await TwoExpressionsAsync(first, second);

        Assert.Equal(expected, OperationEquivalence.AreEquivalent(x, y));
        Assert.Equal(expected, OperationEquivalence.AreEquivalent(y, x));

        // Duplicate detection only compares operands whose hashes match, so equivalence must imply equal hashes.
        var (xHash, yHash) = (OperationEquivalence.GetHashCode(x), OperationEquivalence.GetHashCode(y));
        if (expected)
        {
            Assert.Equal(xHash, yHash);
        }
    }

    [Theory]
    [MemberData(nameof(SameTestCases))]
    public async Task SameTest(string first, string second, bool expected)
    {
        var (_, x, y) = await TwoExpressionsAsync(first, second);

        Assert.Equal(expected, Negation.AreSame(x, y));
        if (expected)
        {
            Assert.Equal(Negation.GetHashCode(x), Negation.GetHashCode(y));
        }
    }

    [Theory]
    [MemberData(nameof(OppositeCases))]
    public async Task Opposite(string first, string second, bool expected)
    {
        var (_, x, y) = await TwoExpressionsAsync(first, second);

        Assert.Equal(expected, Negation.AreOpposite(x, y));
    }

    [Theory]
    [MemberData(nameof(PurityCases))]
    public async Task Purity(string expression, bool expected)
    {
        var (compilation, x, _) = await TwoExpressionsAsync(expression, "0");

        Assert.Equal(expected, new PurityOracle(compilation).IsFree(x));
    }

    [Theory]
    [MemberData(nameof(ExitCases))]
    public async Task AlwaysExits(string statement, bool expected)
    {
        var source = Prelude + $$"""
            public static class C
            {
                public static void Test(int i, int j)
                {
                    while (i > 0)
                    {
                        {{statement}}
                    }
                }
            }
            """;
        var (compilation, tree) = await CompileWithoutErrorsAsync(source);
        var loop = tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<WhileStatementSyntax>().Single();
        var body = (BlockSyntax)loop.Statement;
        var operation = compilation.GetSemanticModel(tree).GetOperation(body.Statements[0], TestContext.Current.CancellationToken);

        Assert.Equal(expected, Jumps.AlwaysExits(operation));
    }

    [Theory]
    [MemberData(nameof(MutationCases))]
    public async Task MayBeChanged(string prelude, string condition, string region, bool expected)
    {
        var source = Prelude + $$"""
            public static class C
            {
                public static async Task Test(Box a, Box b, int i, int j, int[] arr, dynamic dyn, object o)
                {
                    {{prelude}}
                    var condition = {{condition}};
                    {
                        {{region}}
                    }
                }
            }
            """;
        var (compilation, tree) = await CompileWithoutErrorsAsync(source);
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "Test");
        var conditionSyntax = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(d => d.Identifier.Text == "condition").Initializer!.Value;
        var regionSyntax = method.Body!.Statements.OfType<BlockSyntax>().Last();

        var reads = ReadSet.Of(model.GetOperation(conditionSyntax, TestContext.Current.CancellationToken)!);
        var changed = reads.MayBeChangedBy(model.GetOperation(regionSyntax, TestContext.Current.CancellationToken)!, new PurityOracle(compilation));

        Assert.Equal(expected, changed);
    }

    [Fact]
    public async Task FrameworkValues()
    {
        var (compilation, _) = await CompileWithoutErrorsAsync(Prelude);
        var int32 = compilation.GetSpecialType(SpecialType.System_Int32);
        var nullable = compilation.GetSpecialType(SpecialType.System_Nullable_T);
        var point = compilation.GetTypeByMetadataName("Point")!;

        Assert.True(Framework.IsFrameworkValue(int32));
        Assert.True(Framework.IsFrameworkValue(compilation.GetSpecialType(SpecialType.System_String)));
        Assert.True(Framework.IsFrameworkValue(compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!.Construct(int32)));
        Assert.True(Framework.IsFrameworkValue(compilation.CreateArrayTypeSymbol(int32)));
        Assert.True(Framework.IsFrameworkValue(compilation.GetTypeByMetadataName("System.DayOfWeek")));
        Assert.True(Framework.IsFrameworkValue(compilation.GetTypeByMetadataName("System.TimeSpan")));
        Assert.True(Framework.IsFrameworkValue(nullable.Construct(int32)));
        Assert.True(Framework.IsFrameworkValue(compilation.GetTypeByMetadataName("Color")));
        Assert.False(Framework.IsFrameworkValue(compilation.GetTypeByMetadataName("Box")));
        Assert.False(Framework.IsFrameworkValue(point));
        Assert.False(Framework.IsFrameworkValue(nullable.Construct(point)));
        Assert.False(Framework.IsFrameworkValue(null));
        Assert.False(Framework.IsFrameworkValue(compilation.DynamicType));
    }

    private static async Task<(Compilation Compilation, IOperation First, IOperation Second)> TwoExpressionsAsync(string first, string second)
    {
        var source = Prelude + $$"""
            public static class C
            {
                public static void Test(
                    Box a, Box b, int i, int j, int? n, double d, double? dn, string s, object o, int[] arr, List<int> list, Stopwatch sw, Money m, DayOfWeek day, DateTime dt)
                {
                    var first = {{first}};
                    var second = {{second}};
                }
            }
            """;
        var (compilation, tree) = await CompileWithoutErrorsAsync(source);
        var model = compilation.GetSemanticModel(tree);
        var declarators = tree.GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(d => d.Identifier.Text is "first" or "second")
            .ToDictionary(d => d.Identifier.Text);
        return (
            compilation,
            model.GetOperation(declarators["first"].Initializer!.Value, TestContext.Current.CancellationToken)!,
            model.GetOperation(declarators["second"].Initializer!.Value, TestContext.Current.CancellationToken)!);
    }

    // A typo in a test snippet would otherwise silently test error recovery instead of the intended construct.
    private static async Task<(Compilation Compilation, SyntaxTree Tree)> CompileWithoutErrorsAsync(string source)
    {
        var compilation = await AllAnalyzers.CompileAsync(source, TestContext.Current.CancellationToken);
        var errors = compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString());
        Assert.Empty(errors);
        return (compilation, compilation.SyntaxTrees.Single());
    }
}
