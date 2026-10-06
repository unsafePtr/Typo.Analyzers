using Microsoft.CodeAnalysis.Testing;
using TypoAnalyzers.Analyzers;
using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.IdenticalOperandsAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class IdenticalOperandsAnalyzerTests
{
    // Markup: {|#0:...|} is the reported duplicate, {|#1:...|} the earlier operand it repeats.
    private static DiagnosticResult Repeat(string duplicate, string original, string op, int duplicateMarker = 0, int originalMarker = 1) =>
        Verify.Diagnostic(Descriptors.IdenticalSubExpressions)
            .WithLocation(duplicateMarker)
            .WithLocation(originalMarker)
            .WithArguments(duplicate, original, op);

    [Fact]
    public Task SqlSugar_DuplicateTypeTestOnProperty() => Verify.VerifyAsync(
        """
        public sealed class Column { public object Value { get; set; } }

        public static class C
        {
            public static bool IsNumber(Column it) =>
                it.Value is int
                || it.Value is long
                || {|#1:it.Value is short|}
                || {|#0:it.Value is short|}
                || it.Value is byte
                || it.Value is double;
        }
        """,
        Repeat("it.Value is short", "it.Value is short", "||"));

    [Fact]
    public Task SqlSugar_NullableTypeTestIsSameAsUnderlying() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool IsInt64(object value) =>
                {|#1:value is long|}
                || {|#0:value is long?|}
                || value is ulong?
                || {|#2:value is long?|};
        }
        """,
        Repeat("value is long?", "value is long", "||"),
        Repeat("value is long?", "value is long", "||", duplicateMarker: 2));

    [Fact]
    public Task Nethermind_LastPairDuplicated() => Verify.VerifyAsync(
        """
        public sealed class U256 { public ulong S0, S1, S2, S3; }

        public static class C
        {
            public static bool Same(U256 a, U256 b) =>
                a.S0 == b.S0 && a.S1 == b.S1 && {|#1:a.S2 == b.S2|} && {|#0:a.S2 == b.S2|};
        }
        """,
        Repeat("a.S2 == b.S2", "a.S2 == b.S2", "&&"));

    [Fact]
    public Task NonAdjacentDuplicate() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(int a) => {|#1:a == 1|} || a == 2 || {|#0:a == 1|};
        }
        """,
        Repeat("a == 1", "a == 1", "||"));

    [Fact]
    public Task CommutedComparison() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(int a, int b) => {|#1:a == b|} || {|#0:b == a|};
        }
        """,
        Repeat("b == a", "a == b", "||"));

    [Fact]
    public Task ImplicitAndExplicitThis() => Verify.VerifyAsync(
        """
        public sealed class C
        {
            private int X;

            public bool M() => {|#1:X == 1|} || {|#0:this.X == 1|};
        }
        """,
        Repeat("this.X == 1", "X == 1", "||"));

    [Fact]
    public Task XorHashCancelsOut() => Verify.VerifyAsync(
        """
        public sealed class C
        {
            public int X, Y;

            public int Hash() => X.GetHashCode() ^ {|#1:Y.GetHashCode()|} ^ {|#0:Y.GetHashCode()|};
        }
        """,
        Repeat("Y.GetHashCode()", "Y.GetHashCode()", "^"));

    [Fact]
    public Task DuplicateFlag() => Verify.VerifyAsync(
        """
        [System.Flags]
        public enum Access { None = 0, Read = 1, Write = 2 }

        public static class C
        {
            public static Access M() => {|#1:Access.Read|} | Access.Write | {|#0:Access.Read|};
        }
        """,
        Repeat("Access.Read", "Access.Read", "|"));

    [Fact]
    public Task DuplicatePatternInOrCombinator() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(object o) => o is {|#1:int|} or long or {|#0:int|};
        }
        """,
        Repeat("int", "int", "or"));

    [Fact]
    public Task Firefox_DuplicatePureCall() => Verify.VerifyAsync(
        """
        using System;

        public static class C
        {
            public static bool IsKnown(string protocol) =>
                protocol.Equals("http", StringComparison.OrdinalIgnoreCase)
                || {|#1:protocol.Equals("ftp", StringComparison.OrdinalIgnoreCase)|}
                || {|#0:protocol.Equals("ftp", StringComparison.OrdinalIgnoreCase)|};
        }
        """,
        Repeat("protocol.Equals(\"ftp\", StringComparison.OrdinalIgnoreCase)", "protocol.Equals(\"ftp\", StringComparison.OrdinalIgnoreCase)", "||"));

    [Fact]
    public Task QuakeStyleIndexDuplicate() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool IsZero(int[] dir) => dir[0] == 0 && {|#1:dir[1] == 0|} && {|#0:dir[1] == 0|};
        }
        """,
        Repeat("dir[1] == 0", "dir[1] == 0", "&&"));

    [Fact]
    public Task RepeatedNullCheck() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string? s) => {|#1:s != null|} && s.Length > 0 && {|#0:s != null|};
        }
        """,
        Repeat("s != null", "s != null", "&&"));

    [Fact]
    public Task MongoDbStyleSelfComparison() => Verify.VerifyAsync(
        """
        public sealed class Options
        {
            public int Port;
            public bool BuildIndexes;

            public bool SameAs(Options r) => Port == r.Port && {|#1:BuildIndexes|} == {|#0:BuildIndexes|};
        }
        """,
        Repeat("BuildIndexes", "BuildIndexes", "=="));

    [Theory]
    [InlineData("!=", "bool")]
    [InlineData("<", "bool")]
    [InlineData("<=", "bool")]
    [InlineData(">", "bool")]
    [InlineData(">=", "bool")]
    [InlineData("/", "int")]
    [InlineData("%", "int")]
    public Task SelfCancellingOperators(string op, string type) => Verify.VerifyAsync(
        $$"""
        public static class C
        {
            public static {{type}} M(int count) => {|#1:count|} {{op}} {|#0:count|};
        }
        """,
        Repeat("count", "count", op));

    [Fact]
    public Task SelfSubtraction() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(int width) => {|#1:width|} - {|#0:width|};
        }
        """,
        Repeat("width", "width", "-"));

    [Fact]
    public Task InsideLambdaAndLocalFunction() => Verify.VerifyAsync(
        """
        using System.Linq;

        public static class C
        {
            public static int[] M(int[] items)
            {
                bool Keep(int x) => {|#1:x > 0|} || {|#0:x > 0|};
                return items.Where(x => {|#3:x == 1|} || {|#2:x == 1|}).Where(Keep).ToArray();
            }
        }
        """,
        Repeat("x > 0", "x > 0", "||"),
        Repeat("x == 1", "x == 1", "||", duplicateMarker: 2, originalMarker: 3));

    [Fact]
    public Task NonShortCircuitBooleanAnd() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(bool a, bool b) => {|#1:a|} & b & {|#0:a|};
        }
        """,
        Repeat("a", "a", "&"));

    [Fact]
    public Task FrameworkOperatorWithIdenticalOperands() => Verify.VerifyAsync(
        """
        using System;

        public static class C
        {
            public static TimeSpan M(TimeSpan elapsed) => {|#1:elapsed|} - {|#0:elapsed|};
        }
        """,
        Repeat("elapsed", "elapsed", "-"));

    [Fact]
    public Task NoDiagnostics_ForValuesThatChangeBetweenReads() => Verify.VerifyAsync(
        """
        using System;

        public static class C
        {
            public static bool Guids() => Guid.NewGuid() == Guid.NewGuid();

            public static TimeSpan Clock() => DateTime.Now - DateTime.Now;

            public static bool Ticks() => Environment.TickCount > 0 || Environment.TickCount > 0;
        }
        """);

    [Fact]
    public Task NoDiagnostics_InGeneratedCode() => Verify.VerifyAsync(
        """
        // <auto-generated/>
        public static class C
        {
            public static bool M(int a) => a == 1 || a == 1;
        }
        """);

    [Fact]
    public Task NoDiagnostics_ForNullableNaNCheck() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool IsNaN(double? d) => d != d;

            public static bool IsNaNSingle(float? f) => f != f;
        }
        """);

    [Fact]
    public Task NoDiagnostics_ForNaNCheckOnOtherFloatingPointTypes() => Verify.VerifyAsync(
        """
        using System;
        using System.Numerics;
        using System.Runtime.InteropServices;

        public static class C
        {
            public static bool IsNaNHalf(Half h) => h != h;

            public static bool IsNotNaNHalf(Half? h) => h == h;

            public static bool IsNaNNative(NFloat f) => f != f;

            public static bool IsNaNIeee<T>(T x) where T : IFloatingPointIeee754<T> => x != x;

            public static bool IsNaNNumber<T>(T x) where T : INumber<T> => x != x;

            public static bool IsNotNaNNullable<T>(T? x) where T : struct, INumber<T> => x == x;
        }
        """);

    [Fact]
    public Task SelfComparisonOfValuesThatCannotBeNaN() => Verify.VerifyAsync(
        """
        using System;
        using System.Numerics;

        public static class C
        {
            public static bool Integer<T>(T a) where T : IBinaryInteger<T> => {|#1:a|} != {|#0:a|};

            public static bool Reference<T>(T a) where T : class => {|#3:a|} == {|#2:a|};

            public static bool Wide(Int128 a) => {|#5:a|} != {|#4:a|};
        }
        """,
        Repeat("a", "a", "!=", 0, 1),
        Repeat("a", "a", "==", 2, 3),
        Repeat("a", "a", "!=", 4, 5));

    [Fact]
    public Task NoDiagnostics_ForLegitimatePatterns() => Verify.VerifyAsync(
        """
        using System.IO;

        public enum Shade { Gray = 1, Grey = 1 }

        public sealed class Money
        {
            public static bool operator ==(Money? a, Money? b) => true;
            public static bool operator !=(Money? a, Money? b) => false;
            public override bool Equals(object? obj) => true;
            public override int GetHashCode() => 0;
        }

        public sealed class P { public int X; }

        public static class C
        {
            public static bool IsNaN(double d) => d != d;

            public static bool TwoReads(Stream r) => r.ReadByte() == 1 || r.ReadByte() == 1;

            public static bool Increments(int i) => i++ == 1 || i++ == 1;

            public static int Doubling(int x) => x + x + x * x;

            public static bool Reflexive(Money m) => m == m;

            public static bool DifferentInstances(P a, P b) => a.X == 1 || b.X == 1;

            public static bool EnumAliases(Shade c) => c == Shade.Gray || c == Shade.Grey;

            public static bool DifferentIndices(int[] d) => d[0] == 0 && d[1] == 0 && d[2] == 0;

            public static bool NewInstances() => new object() == new object();

            public static bool DifferentPatterns(object o) => o is int or long or short;

            public static bool DifferentConstants(int a) => a == 1 || a == 2 || a == 3;
        }
        """);
}
