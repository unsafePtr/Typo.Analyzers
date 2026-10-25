using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Typo.Tests.Verifier<Typo.Analyzers.ComparisonOperandAnalyzer>;

namespace Typo.Tests;

public sealed class ComparisonOperandAnalyzerTests
{
    // Markup: {|#0:...|} is the repeated operand on the sum side, {|#1:...|} the sum, difference or product that holds it.
    private static DiagnosticResult Repeated(string operand, string expression) =>
        Verify.Diagnostic(Descriptors.RepeatedOperandAcrossComparison).WithLocation(0).WithLocation(1).WithArguments(operand, expression);

    [Fact]
    public Task EqualsItselfPlusOne() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(int x1) => x1 == {|#1:{|#0:x1|} + 1|};
        }
        """,
        Repeated("x1", "x1 + 1"));

    [Fact]
    public Task RepeatedOperandOnTheLeftSide() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(int count) => {|#1:{|#0:count|} - 1|} != count;
        }
        """,
        Repeated("count", "count - 1"));

    [Fact]
    public Task LessThanItselfTimesFactor() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(int x, int y) => x < {|#1:{|#0:x|} * y|};
        }
        """,
        Repeated("x", "x * y"));

    [Fact]
    public Task MemberAccessOperand() => Verify.VerifyAsync(
        """
        public sealed class P
        {
            public int Size;

            public bool M(P other) => other.Size != {|#1:{|#0:other.Size|} + Size|};
        }
        """,
        Repeated("other.Size", "other.Size + Size"));

    [Fact]
    public Task DifferentVariablesAreSilent() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(int x1, int x2) => x2 == x1 + 1;
        }
        """);

    [Fact]
    public Task ConstantOperandIsSilent() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(int y) => 2 < 2 * y;
        }
        """);

    [Fact]
    public Task StringConcatenationIsSilent() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string name) => name != name + "!";
        }
        """);

    [Fact]
    public Task CallWithSideEffectsIsSilent() => Verify.VerifyAsync(
        """
        public sealed class C
        {
            private int _state;

            private int Next() => ++_state;

            public bool M() => Next() == Next() + 1;
        }
        """);

    [Fact]
    public Task UnsignedOverflowGuardIsSilent() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(ulong nonce) => nonce + 1 < nonce;
        }
        """);

    [Fact]
    public Task CarryCheckOfASumIsSilent() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(uint a, uint b) => a + b < a;
        }
        """);
}
