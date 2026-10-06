using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.SelfArgumentAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class SelfArgumentAnalyzerTests
{
    // Markup: {|#0:...|} is the reported argument, {|#1:...|} the value it is compared with.
    private static DiagnosticResult Self(string operand, string method) =>
        Verify.Diagnostic(Descriptors.ComparedWithItself).WithLocation(0).WithLocation(1).WithArguments(operand, method);

    [Fact]
    public Task DotNet10_CompareToComparesFieldWithItself() => Verify.VerifyAsync(
        """
        using System;

        public sealed class MutableModule : IComparable<MutableModule>
        {
            private readonly int _index;

            public int CompareTo(MutableModule other) => {|#1:_index|}.CompareTo({|#0:_index|});
        }
        """,
        Self("_index", "CompareTo"));

    [Fact]
    public Task StaticEqualsWithSameArguments() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string name) => string.Equals({|#1:name|}, {|#0:name|});
        }
        """,
        Self("name", "Equals"));

    [Fact]
    public Task MathMaxWithSameArguments() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(int width, int height) => System.Math.Max({|#1:width|}, {|#0:width|});
        }
        """,
        Self("width", "Max"));

    [Fact]
    public Task ComparerWithSameArguments() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string a, string b) => System.StringComparer.Ordinal.Compare({|#1:a|}, {|#0:a|});
        }
        """,
        Self("a", "Compare"));

    [Fact]
    public Task SequenceEqualOnFrameworkCollection() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;
        using System.Linq;

        public static class C
        {
            public static bool M(List<int> list, List<int> other) => {|#1:list|}.SequenceEqual({|#0:list|});
        }
        """,
        Self("list", "SequenceEqual"));

    [Fact]
    public Task NamedArgumentsInAnyOrder() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string name) => string.Equals(b: {|#0:name|}, a: {|#1:name|});
        }
        """,
        Self("name", "Equals"));

    [Fact]
    public Task NoDiagnostics_ForLegitimateCalls() => Verify.VerifyAsync(
        """
        public sealed class Money
        {
            public override bool Equals(object obj) => true;
            public override int GetHashCode() => 0;
        }

        public static class C
        {
            private static int s_next;

            private static int Next() => s_next++;

            public static bool Reflexive(Money money) => money.Equals(money);

            public static bool Different(int a, int b) => a.Equals(b);

            public static bool TwoCalls() => Next().Equals(Next());

            public static int Max(int a, int b) => System.Math.Max(a, b);
        }

        public sealed class Version : System.IComparable<Version>
        {
            public int CompareTo(Version other) => 0;

            public int Implicit(Version other) => CompareTo(other);
        }
        """);
}
