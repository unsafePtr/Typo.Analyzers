using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.LoopIndexAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class LoopIndexAnalyzerTests
{
    [Fact]
    public Task Nethermind_LoopAlwaysReadsFirstElement() => Verify.VerifyAsync(
        """
        public sealed class Info
        {
            public int Hash;
        }

        public sealed class Level
        {
            public Info[] BlockInfos = [];
        }

        public static class C
        {
            public static bool Contains(Level currentLevel, int hash)
            {
                for (var i = 0; i < currentLevel.BlockInfos.Length; i++)
                {
                    if ({|#0:currentLevel.BlockInfos[0]|}.Hash == hash)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("currentLevel.BlockInfos", "i", "currentLevel.BlockInfos[0]"));

    [Fact]
    public Task ListIndexerWithConstant() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public static class C
        {
            public static bool AllPositive(List<int> seq)
            {
                for (var i = 0; i < seq.Count; i++)
                {
                    if ({|#0:seq[0]|} <= 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("seq", "i", "seq[0]"));

    [Fact]
    public Task StringIndexerWithConstant() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int CountSpaces(string text)
            {
                var count = 0;
                for (var i = 0; i < text.Length; i++)
                {
                    if ({|#0:text[0]|} == ' ')
                    {
                        count++;
                    }
                }

                return count;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("text", "i", "text[0]"));

    [Fact]
    public Task SpanIndexerWithConstant() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int Sum(System.ReadOnlySpan<int> values)
            {
                var total = 0;
                for (var i = 0; i < values.Length; i++)
                {
                    total += {|#0:values[0]|};
                }

                return total;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("values", "i", "values[0]"));

    [Fact]
    public Task BoundWrittenAsCountMinusOne() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public static class C
        {
            public static int Sum(List<int> list)
            {
                var total = 0;
                for (var i = 0; i <= list.Count - 1; i++)
                {
                    total += {|#0:list[0]|};
                }

                return total;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("list", "i", "list[0]"));

    [Fact]
    public Task ReverseLoop() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int Sum(int[] items)
            {
                var total = 0;
                for (var i = items.Length - 1; i >= 0; i--)
                {
                    total += {|#0:items[0]|};
                }

                return total;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("items", "i", "items[0]"));

    [Fact]
    public Task BoundOnTheLeft() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int Sum(int[] items)
            {
                var total = 0;
                for (var i = 0; items.Length > i; i++)
                {
                    total += {|#0:items[0]|};
                }

                return total;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("items", "i", "items[0]"));

    [Fact]
    public Task LongLengthBoundWithWidenedIndex() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static long Sum(long[] items)
            {
                long total = 0;
                for (var i = 0; i < items.LongLength; i++)
                {
                    total += {|#0:items[0]|};
                }

                return total;
            }
        }
        """,
        Verify.Diagnostic(Descriptors.ConstantIndexInLoop).WithLocation(0).WithArguments("items", "i", "items[0]"));

    [Fact]
    public Task NoDiagnostics_ForUnsupportedShapes() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static int[] GetItems() => new int[3];

            public static int Impure()
            {
                var total = 0;
                for (var i = 0; i < GetItems().Length; i++)
                {
                    total += GetItems()[0];
                }

                return total;
            }

            public static int NotEquals(int[] items)
            {
                var total = 0;
                for (var i = 0; i != items.Length; i++)
                {
                    total += items[0];
                }

                return total;
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_WhenLoopVariableIsUsed() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int Sum(int[] items)
            {
                var total = 0;
                for (var i = 0; i < items.Length; i++)
                {
                    total += items[i];
                }

                return total;
            }

            public static bool FirstIsSpecial(int[] items)
            {
                for (var i = 0; i < items.Length; i++)
                {
                    if (i == 0 && items[0] > 0)
                    {
                        return true;
                    }
                }

                return false;
            }

            public static int Other(int[] items, int[] header)
            {
                var total = 0;
                for (var i = 0; i < items.Length; i++)
                {
                    total += header[0];
                }

                return total;
            }
        }
        """);
}
