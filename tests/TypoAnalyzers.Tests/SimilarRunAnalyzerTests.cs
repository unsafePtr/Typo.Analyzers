using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.SimilarRunAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class SimilarRunAnalyzerTests
{
    private static DiagnosticResult Anomaly(string actual, string expected) =>
        Verify.Diagnostic(Descriptors.SimilarLineAnomaly).WithLocation(0).WithArguments(actual, expected);

    [Fact]
    public Task SourceSdk_LastLineKeepsPreviousMember() => Verify.VerifyAsync(
        """
        public sealed class Vec
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }

            public void Add(Vec other)
            {
                X += other.X;
                Y += other.Y;
                Z += {|#0:other.Y|};
            }
        }
        """,
        Anomaly("other.Y", "other.Z"));

    [Fact]
    public Task SourceSdk_LastCallRepeatsSetter() => Verify.VerifyAsync(
        """
        public sealed class Vec4
        {
            public void Set(int ix, int iy, int iz, int iw)
            {
                SetX(ix);
                SetY(iy);
                SetZ(iz);
                {|#0:SetZ(iw)|};
            }

            private void SetX(int v)
            {
            }

            private void SetY(int v)
            {
            }

            private void SetZ(int v)
            {
            }

            private void SetW(int v)
            {
            }
        }
        """,
        Anomaly("SetZ(iw)", "SetW(iw)"));

    [Fact]
    public Task ObjectInitializerCopiesWrongMember() => Verify.VerifyAsync(
        """
        public sealed class Rect
        {
            public int Left { get; set; }
            public int Top { get; set; }
            public int Right { get; set; }
            public int Bottom { get; set; }

            public static Rect CopyOf(Rect a) => new()
            {
                Left = a.Left,
                Top = a.Top,
                Right = a.Right,
                Bottom = {|#0:a.Right|},
            };
        }
        """,
        Anomaly("a.Right", "a.Bottom"));

    [Fact]
    public Task ConstructorAssignsWrongParameter() => Verify.VerifyAsync(
        """
        public sealed class Person
        {
            public string First { get; }
            public string Middle { get; }
            public string Last { get; }

            public Person(string first, string middle, string last)
            {
                First = first;
                Middle = middle;
                Last = {|#0:middle|};
            }
        }
        """,
        Anomaly("middle", "last"));

    [Fact]
    public Task ArrayCopyReadsPreviousIndex() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static void Copy(int[] dst, int[] src)
            {
                dst[0] = src[0];
                dst[1] = src[1];
                dst[2] = src[{|#0:1|}];
            }
        }
        """,
        Anomaly("1", "2"));

    [Fact]
    public Task ConditionChainComparesWrongMember() => Verify.VerifyAsync(
        """
        public sealed class Vec
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }

            public bool SameAs(Vec r) => X == r.X && Y == r.Y && Z == {|#0:r.Y|};
        }
        """,
        Anomaly("r.Y", "r.Z"));

    [Fact]
    public Task NumberedNames() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static void Copy(int q1, int q2, int q3, out int p1, out int p2, out int p3)
            {
                p1 = q1;
                p2 = q2;
                p3 = {|#0:q2|};
            }
        }
        """,
        Anomaly("q2", "q3"));

    [Fact]
    public Task ColorChannels() => Verify.VerifyAsync(
        """
        public readonly struct Color
        {
            public byte R { get; init; }
            public byte G { get; init; }
            public byte B { get; init; }
        }

        public sealed class Pixel
        {
            public byte R;
            public byte G;
            public byte B;

            public void Set(Color c)
            {
                R = c.R;
                G = c.G;
                B = {|#0:c.G|};
            }
        }
        """,
        Anomaly("c.G", "c.B"));

    [Fact]
    public Task WithExpressionInitializer() => Verify.VerifyAsync(
        """
        public sealed record Point(int X, int Y, int Z)
        {
            public Point CopyFrom(Point o) => this with { X = o.X, Y = o.Y, Z = {|#0:o.Y|} };
        }
        """,
        Anomaly("o.Y", "o.Z"));

    [Fact]
    public Task StringLiteralKeys() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public sealed class Vec
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
        }

        public static class C
        {
            public static void Save(Dictionary<string, int> map, Vec v)
            {
                map["x"] = v.X;
                map["y"] = v.Y;
                map["z"] = {|#0:v.Y|};
            }
        }
        """,
        Anomaly("v.Y", "v.Z"));

    [Fact]
    public Task BoundingBoxKeyEndsWithMember() => Verify.VerifyAsync(
        """
        using System;

        public sealed class Vec
        {
            public int X { get; set; }
            public int Y { get; set; }
        }

        public static class C
        {
            public static void Grow(Vec p, ref int minX, ref int minY, ref int maxX, ref int maxY)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, {|#0:p.X|});
            }
        }
        """,
        Anomaly("p.X", "p.Y"));

    [Fact]
    public Task KeyStartsWithMember() => Verify.VerifyAsync(
        """
        public sealed class Vec
        {
            public int X { get; set; }
            public int Y { get; set; }
        }

        public sealed class Bounds
        {
            public int xMin;
            public int yMin;
            public int xMax;
            public int yMax;

            public void Set(Vec low, Vec high)
            {
                xMin = low.X;
                yMin = low.Y;
                xMax = high.X;
                yMax = {|#0:high.X|};
            }
        }
        """,
        Anomaly("high.X", "high.Y"));

    [Fact]
    public Task LongerFragmentsWithStems() => Verify.VerifyAsync(
        """
        public sealed class Thickness
        {
            public int Left { get; set; }
            public int Top { get; set; }
        }

        public sealed class Style
        {
            public int marginLeft;
            public int marginTop;
            public int paddingLeft;
            public int paddingTop;

            public void Apply(Thickness margin, Thickness padding)
            {
                marginLeft = margin.Left;
                marginTop = margin.Top;
                paddingLeft = padding.Left;
                paddingTop = {|#0:padding.Left|};
            }
        }
        """,
        Anomaly("padding.Left", "padding.Top"));

    [Fact]
    public Task NestedInitializerSplitsTheRun() => Verify.VerifyAsync(
        """
        public sealed class Inner
        {
            public int Value { get; set; }
        }

        public sealed class Rect
        {
            public int Left { get; set; }
            public int Top { get; set; }
            public int Right { get; set; }
            public int Bottom { get; set; }
            public Inner Inner { get; } = new();

            public static Rect CopyOf(Rect a) => new()
            {
                Left = a.Left,
                Inner = { Value = 1 },
                Top = a.Top,
                Right = a.Right,
                Bottom = {|#0:a.Right|},
            };
        }
        """,
        Anomaly("a.Right", "a.Bottom"));

    [Fact]
    public Task AnomalyOnTheFirstLine() => Verify.VerifyAsync(
        """
        using System;

        public sealed class Vec
        {
            public int X { get; set; }
            public int Y { get; set; }
        }

        public static class C
        {
            public static void Grow(Vec p, ref int minX, ref int minY, ref int maxX, ref int maxY)
            {
                minX = Math.Min(minX, {|#0:p.Y|});
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }
        }
        """,
        Anomaly("p.Y", "p.X"));

    [Fact]
    public Task ImplicitConversionsKeepTheShape() => Verify.VerifyAsync(
        """
        public sealed class Vec
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
        }

        public sealed class Wide
        {
            public long X;
            public long Y;
            public long Z;

            public void Set(Vec v)
            {
                X = v.X;
                Y = v.Y;
                Z = {|#0:v.Y|};
            }
        }
        """,
        Anomaly("v.Y", "v.Z"));

    [Fact]
    public Task UnaryOperatorsInEveryLine() => Verify.VerifyAsync(
        """
        public sealed class Vec
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }

            public void Negate(Vec v)
            {
                X = -v.X;
                Y = -v.Y;
                Z = -{|#0:v.Y|};
            }
        }
        """,
        Anomaly("v.Y", "v.Z"));

    [Fact]
    public Task KeyWithoutDistinctFragmentsFallsBackToFullNames() => Verify.VerifyAsync(
        """
        public sealed class Source
        {
            public int Value { get; set; }
            public int Value1 { get; set; }
            public int Value2 { get; set; }
        }

        public sealed class Target
        {
            public int Value;
            public int Value1;
            public int Value2;

            public void Copy(Source s)
            {
                Value = s.Value;
                Value1 = s.Value1;
                Value2 = {|#0:s.Value1|};
            }
        }
        """,
        Anomaly("s.Value1", "s.Value2"));

    [Fact]
    public Task NoDiagnostics_WhenTheOddValueIsNotACopy() => Verify.VerifyAsync(
        """
        using System;

        public sealed class Vec
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
            public int W { get; set; }
        }

        public sealed class Stats
        {
            public int minX;
            public int minY;
            public int maxX;
            public int avgY;
            public int X;
            public int Y;
            public int Z;

            public void UnknownStem(Vec p)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                avgY = Math.Max(avgY, p.X);
            }

            public void NotRepeated(Vec o)
            {
                X = o.X;
                Y = o.Y;
                Z = o.W;
            }

            public static long[] WideTable()
            {
                var longs = new long[3];
                longs[0] = 0;
                longs[1] = 1;
                longs[2] = 1;
                return longs;
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_ForDataTables() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static void Set(string name, int value)
            {
            }

            public static int[] BitCounts()
            {
                var bits = new int[3];
                bits[0] = 0;
                bits[1] = 1;
                bits[2] = 1;
                return bits;
            }

            public static void Settings()
            {
                Set("x", 1);
                Set("y", 2);
                Set("z", 2);
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_WhenPatternIsIntentional() => Verify.VerifyAsync(
        """
        public sealed class Size
        {
            public int Width { get; set; }
            public int Height { get; set; }
        }

        public sealed class Box
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public int Depth { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }

            public void Shrink(int borderWidth, int halfWidth, int halfHeight, int halfDepth)
            {
                halfWidth -= borderWidth + 2;
                halfHeight -= borderWidth + 2;
                halfDepth -= borderWidth + 2;
            }

            public void Cube(Size size)
            {
                Width = size.Width;
                Height = size.Height;
                Depth = size.Height;
            }

            public void Rotate(Box b)
            {
                X = b.Y;
                Y = b.Z;
                Z = b.X;
            }

            public void Print()
            {
                System.Console.WriteLine("a");
                System.Console.WriteLine("b");
                System.Console.WriteLine("b");
            }

            public void TwoLines(Box b)
            {
                X = b.X;
                Y = b.X;
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_WhenTheSuggestionIsNotUsable() => Verify.VerifyAsync(
        """
        public sealed class Methods
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Value() => 0;
        }

        public sealed class Target
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
            public int Value { get; set; }

            public void SuggestionOfAnotherKind(Methods o)
            {
                X = o.X;
                Y = o.Y;
                Value = o.Y;
            }

            public void DeclaredLater(int x, int y)
            {
                X = x;
                Y = y;
                Z = y;
                var z = 0;
                Value = z;
            }
        }
        """);
}
