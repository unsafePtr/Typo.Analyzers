using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Typo.Tests.Verifier<Typo.Analyzers.SelfArgumentAnalyzer>;

namespace Typo.Tests;

public sealed class SelfArgumentAnalyzerTests
{
    // Markup: {|#0:...|} is the reported argument, {|#1:...|} the value it is compared with.
    private static DiagnosticResult Self(string operand, string method) =>
        Verify.Diagnostic(Descriptors.ComparedWithItself).WithLocation(0).WithLocation(1).WithArguments(operand, method);

    [Fact]
    public Task ReplaceWithSameOldAndNewValue() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static string M(string text) => text.Replace({|#1:"-"|}, {|#0:"-"|});
        }
        """,
        Self("\"-\"", "Replace"));

    [Fact]
    public Task IndexOfWithSameArgument() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string text) => {|#1:text|}.IndexOf({|#0:text|});
        }
        """,
        Self("text", "IndexOf"));

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

    [Theory]
    [InlineData("StartsWith")]
    [InlineData("EndsWith")]
    public Task StringSearchedForItself(string method) => Verify.VerifyAsync(
        $$"""
        public static class C
        {
            public static bool M(string prefix) => {|#1:prefix|}.{{method}}({|#0:prefix|}, System.StringComparison.Ordinal);
        }
        """,
        Self("prefix", method));

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

            public static bool Search(string text, string word) => text.Contains(word) || text.StartsWith(word);
        }

        public sealed class Version : System.IComparable<Version>
        {
            public int CompareTo(Version other) => 0;

            public int Implicit(Version other) => CompareTo(other);
        }
        """);

    [Fact]
    public Task Starlight_MaxOfSameTileSize() => Verify.VerifyAsync(
        """
        using System;

        public class NavmeshBase
        {
            public float TileWorldSizeX { get; set; }
            public float TileWorldSizeZ { get; set; }

            public int GetNearestForce(float bestDistance, int wmax)
            {
                var found = wmax;
                for (int w = 0; w < wmax; w++)
                {
                    if (bestDistance < (w - 2) * Math.Max({|#1:TileWorldSizeX|}, {|#0:TileWorldSizeX|}))
                    {
                        found = w;
                        break;
                    }
                }

                return found;
            }
        }
        """,
        Self("TileWorldSizeX", "Max"));

    [Fact]
    public Task PascalAbc_CompareWithSameListElement() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public class SymScope
        {
            public virtual bool IsEqual(SymScope ts) => false;
        }

        public class EnumScope : SymScope
        {
            private List<string> enum_consts = new List<string>();

            public override bool IsEqual(SymScope ts)
            {
                EnumScope es = ts as EnumScope;
                if (es == null)
                {
                    return false;
                }

                if (enum_consts.Count != es.enum_consts.Count)
                {
                    return false;
                }

                for (int i = 0; i < es.enum_consts.Count; i++)
                {
                    if (string.Compare({|#1:enum_consts[i]|}, {|#0:this.enum_consts[i]|}, true) != 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
        """,
        Self("this.enum_consts[i]", "Compare"));

    [Fact]
    public Task AwsSdk_ContainsWithItself() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;

        public sealed class CustomEvent
        {
            private readonly object _lock = new object();
            private readonly Dictionary<string, string> _attributes = new Dictionary<string, string>();

            public string GetAttribute(string attributeName)
            {
                if (string.IsNullOrEmpty(attributeName))
                {
                    throw new ArgumentNullException("attributeName");
                }

                string ret = null;
                lock (_lock)
                {
                    if ({|#1:attributeName|}.Contains({|#0:attributeName|}))
                    {
                        ret = _attributes[attributeName];
                    }
                }

                return ret;
            }
        }
        """,
        Self("attributeName", "Contains"));
}
