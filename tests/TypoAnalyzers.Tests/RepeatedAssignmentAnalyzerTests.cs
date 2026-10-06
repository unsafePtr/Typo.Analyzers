using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.RepeatedAssignmentAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class RepeatedAssignmentAnalyzerTests
{
    // Markup: {|#0:...|} is the second write, {|#1:...|} the first one.
    private static DiagnosticResult Overwritten(string target) =>
        Verify.Diagnostic(Descriptors.OverwrittenBeforeUse).WithLocation(0).WithLocation(1).WithArguments(target);

    [Fact]
    public Task Qt_FieldAssignedTwice() => Verify.VerifyAsync(
        """
        public sealed class Pattern
        {
            private bool _repeatX;
            private bool _repeatY;

            public bool Repeats => _repeatX || _repeatY;

            public void Reset()
            {
                {|#1:_repeatY|} = false;
                {|#0:_repeatY|} = false;
            }
        }
        """,
        Overwritten("_repeatY"));

    [Fact]
    public Task ArrayElementWrittenTwice() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int[] Copy(int[] src)
            {
                var dst = new int[3];
                dst[0] = src[0];
                {|#1:dst[1]|} = src[1];
                {|#0:dst[1]|} = src[2];
                return dst;
            }
        }
        """,
        Overwritten("dst[1]"));

    [Fact]
    public Task AutoPropertyOverwrittenAfterUnrelatedWrite() => Verify.VerifyAsync(
        """
        public sealed class Point
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }

            public void Set(int a, int b, int c)
            {
                {|#1:X|} = a;
                Y = b;
                {|#0:X|} = c;
            }
        }
        """,
        Overwritten("X"));

    [Fact]
    public Task DictionaryEntryWrittenTwice() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public static class C
        {
            public static void Fill(Dictionary<string, int> map)
            {
                {|#1:map["a"]|} = 1;
                {|#0:map["a"]|} = 2;
            }
        }
        """,
        Overwritten("map[\"a\"]"));

    [Fact]
    public Task StaticFieldWrittenTwice() => Verify.VerifyAsync(
        """
        public static class Settings
        {
            public static int Timeout;
        }

        public static class C
        {
            public static void Configure()
            {
                {|#1:Settings.Timeout|} = 10;
                {|#0:Settings.Timeout|} = 20;
            }
        }
        """,
        Overwritten("Settings.Timeout"));

    [Fact]
    public Task WidenedFieldWrittenTwice() => Verify.VerifyAsync(
        """
        public sealed class Counter
        {
            public long Total;

            public void Reset()
            {
                {|#1:Total|} = 0;
                {|#0:Total|} = 1;
            }
        }
        """,
        Overwritten("Total"));

    [Fact]
    public Task NoDiagnostics_ForSettersFromOtherAssembliesOrGetOnlyProperties() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public sealed class C
        {
            public C()
            {
                Id = 1;
                Id = 2;
            }

            public int Id { get; }

            public static void Grow(List<int> list)
            {
                list.Capacity = 10;
                list.Capacity = 20;
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_ForDifferentInstancesOrMovingIndex() => Verify.VerifyAsync(
        """
        public sealed class Point
        {
            public int X { get; set; }
        }

        public static class C
        {
            public static void Instances(Point a, Point b)
            {
                a.X = 1;
                b.X = 2;
            }

            public static void MovingIndex(int[] dst)
            {
                var i = 0;
                dst[i++] = 1;
                dst[i++] = 2;
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_WhenValueIsUsedOrSetterMatters() => Verify.VerifyAsync(
        """
        public sealed class C
        {
            private int _count;
            private int _other;
            private volatile int _flag;
            private int _value;

            public int X { get; set; }
            public int Y { get; set; }

            public int Value
            {
                get => _value;
                set
                {
                    _value = value;
                    Changed();
                }
            }

            private void Changed()
            {
            }

            private int Compute() => _count * 2;

            public void ReadBetween(int a, int c)
            {
                X = a;
                Y = X;
                X = c;
            }

            public void SelfReference(int a)
            {
                X = a;
                X = X + 1;
            }

            public void CallBetween()
            {
                _count = 0;
                _other = Compute();
                _count = 1;
            }

            public void SetterWithSideEffect()
            {
                Value = 0;
                Value = 1;
            }

            public void VolatileHandshake()
            {
                _flag = 1;
                _flag = 0;
            }

            public int Locals()
            {
                var x = 1;
                x = 2;
                x = 3;
                return x;
            }
        }
        """);
}
