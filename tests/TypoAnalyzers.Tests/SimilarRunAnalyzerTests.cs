using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.SimilarRunAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class SimilarRunAnalyzerTests
{
    // One pair from every source TYPO0012.scan can name, in its order, then a TYPO0011 run the option must not touch.
    private const string PairInEverySource =
        """
        public readonly record struct Vec(int X, int Y);

        public sealed class Box
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }

            public void Statements(Box b)
            {
                X = b.X;
                Y = {|#0:b.X|};
            }

            public static int Declarations(Box b)
            {
                var x = b.X;
                var y = {|#1:b.X|};
                return x + y;
            }

            public static Box Initializers(Box b) => new Box { X = b.X, Y = {|#2:b.X|} };

            public bool Conditions(Box b) => X == b.X && Y == {|#3:b.X|};

            public static Vec Arguments(Box b) => new Vec(b.X, {|#4:b.X|});

            public void Run(Box b)
            {
                X = b.X;
                Y = b.Y;
                Z = {|#5:b.Y|};
            }
        }
        """;

    private static DiagnosticResult Anomaly(string actual, string expected, int location = 0) =>
        Verify.Diagnostic(Descriptors.SimilarLineAnomaly).WithLocation(location).WithArguments(actual, expected);

    private static DiagnosticResult Pair(string actual, string expected, int location = 0) =>
        Verify.Diagnostic(Descriptors.SimilarPairAnomaly).WithLocation(location).WithArguments(actual, expected);

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

    // A swizzle names its arguments in order, so its repeats are stated.
    [Fact]
    public Task SwizzleReportsOnlyWhenArgumentsBreakItsName() => Verify.VerifyAsync(
        """
        public struct Vector4
        {
            public float X, Y, Z, W;

            public Vector4(float x, float y, float z, float w)
            {
                X = x;
                Y = y;
                Z = z;
                W = w;
            }

            public Vector4 Yyzw
            {
                get => new Vector4(Y, Y, Z, W);
            }

            public readonly Vector4 Wzzw => new Vector4(W, Z, Z, W);

            public readonly Vector4 Xyzw => new Vector4({|#0:Y|}, Y, Z, W);
        }
        """,
        Anomaly("Y", "X"));

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

    [Fact]
    public Task ArgumentRun() => Verify.VerifyAsync(
        """
        public sealed class Rect
        {
            public Rect(int left, int top, int right, int bottom)
            {
            }

            public int Left { get; }
            public int Top { get; }
            public int Right { get; }
            public int Bottom { get; }

            public static Rect CopyOf(Rect r) => new Rect(r.Left, r.Top, r.Right, {|#0:r.Right|});
        }
        """,
        Anomaly("r.Right", "r.Bottom"));

    [Fact]
    public Task DeclarationRun() => Verify.VerifyAsync(
        """
        using System.Linq;

        public sealed class Point
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
        }

        public static class C
        {
            public static int Spread(Point[] pts)
            {
                var minX = pts.Min(p => p.X);
                var minY = pts.Min(p => p.Y);
                var minZ = pts.Min(p => {|#0:p.Y|});
                return minX + minY + minZ;
            }
        }
        """,
        Anomaly("p.Y", "p.Z"));

    [Fact]
    public Task LambdaBodiesWithLocalsKeepTheirColumns() => Verify.VerifyAsync(
        """
        using System;

        public sealed class Vec
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }

            private static int Compute(Func<int> f) => f();

            public void Set(Vec o)
            {
                X = Compute(() => { var t = o.X; return t; });
                Y = Compute(() => { var t = o.Y; return t; });
                Z = Compute(() => { var t = {|#0:o.Y|}; return t; });
            }
        }
        """,
        Anomaly("o.Y", "o.Z"));

    [Fact]
    public Task NoDiagnostics_ForDeclarationChains() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int Powers(int x)
            {
                var f2 = x * x;
                var f4 = f2 * f2;
                var f8 = f4 * f4;
                var f12 = f8 * f4;
                return f12;
            }
        }
        """);

    [Fact]
    public Task Pair_TwoAssignments() => Verify.VerifyAsync(
        """
        public sealed class Box
        {
            public int X { get; set; }
            public int Y { get; set; }

            public void TwoLines(Box b)
            {
                X = b.X;
                Y = {|#0:b.X|};
            }
        }
        """,
        Pair("b.X", "b.Y"));

    [Fact]
    public Task Pair_EqualsChain() => Verify.VerifyAsync(
        """
        public sealed class Size
        {
            public int Width { get; init; }
            public int Height { get; init; }

            public bool SameAs(Size other) => other is not null && Width == other.Width && Height == {|#0:other.Width|};
        }
        """,
        Pair("other.Width", "other.Height"));

    [Fact]
    public Task Pair_Declarations() => Verify.VerifyAsync(
        """
        using System.Linq;

        public sealed class Point
        {
            public int X { get; set; }
            public int Y { get; set; }
        }

        public static class C
        {
            public static int Spread(Point[] pts)
            {
                var minX = pts.Min(p => p.X);
                var minY = pts.Min(p => {|#0:p.X|});
                return minX + minY;
            }
        }
        """,
        Pair("p.X", "p.Y"));

    [Fact]
    public Task Pair_OldAndNewValue() => Verify.VerifyAsync(
        """
        public sealed class Change
        {
            public string OldValue { get; set; }
            public string NewValue { get; set; }
        }

        public static class C
        {
            public static bool Differs(Change e)
            {
                var oldValue = e.OldValue;
                var newValue = {|#0:e.OldValue|};
                return oldValue != newValue;
            }
        }
        """,
        Pair("e.OldValue", "e.NewValue"));

    [Fact]
    public Task Pair_ConstructorArguments() => Verify.VerifyAsync(
        """
        public readonly record struct Vec(int X, int Y);

        public sealed class Point
        {
            public int X { get; set; }
            public int Y { get; set; }
        }

        public static class C
        {
            public static Vec Positional(Point p) => new Vec(p.X, {|#0:p.X|});

            public static Vec Named(Point p) => new Vec(Y: {|#1:p.X|}, X: p.X);
        }
        """,
        Pair("p.X", "p.Y"),
        Pair("p.X", "p.Y", 1));

    [Fact]
    public Task Pair_ConstructorInitializers() => Verify.VerifyAsync(
        """
        public sealed class Point
        {
            public int X { get; set; }
            public int Y { get; set; }
        }

        public class Base
        {
            public Base(int x, int y)
            {
            }

            public Base(Point p)
                : this(p.X, {|#0:p.X|})
            {
            }
        }

        public sealed class Derived(Point p) : Base(p.X, {|#1:p.X|});
        """,
        Pair("p.X", "p.Y"),
        Pair("p.X", "p.Y", 1));

    [Fact]
    public Task Pair_ParameterPassedTwice() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static int Process(int left, int right) => left - right;

            public static int Run(int left, int right) => Process(left, {|#0:left|});
        }
        """,
        Pair("left", "right"));

    [Fact]
    public Task Pair_StringKeys() => Verify.VerifyAsync(
        """
        public sealed class Parameters
        {
            public void AddWithValue(string name, object value)
            {
            }
        }

        public sealed class Point
        {
            public int X { get; set; }
            public int Y { get; set; }
        }

        public static class C
        {
            public static void Bind(Parameters parameters, Point p)
            {
                parameters.AddWithValue("@x", p.X);
                parameters.AddWithValue("@y", {|#0:p.X|});
            }
        }
        """,
        Pair("p.X", "p.Y"));

    [Fact]
    public Task Pair_AllCapsConstant() => Verify.VerifyAsync(
        """
        public sealed class Layout
        {
            private const int MAX_WIDTH = 800;
            private const int MAX_HEIGHT = 600;
            private int width;
            private int height;

            public int Area => width * height;

            public void Reset()
            {
                width = MAX_WIDTH;
                height = {|#0:MAX_WIDTH|};
            }
        }
        """,
        Pair("MAX_WIDTH", "MAX_HEIGHT"));

    [Fact]
    public Task Pair_CallWithoutArguments() => Verify.VerifyAsync(
        """
        using System.Linq;

        public static class C
        {
            public static int Range(int[] v)
            {
                var minX = v.Min();
                var maxX = {|#0:v.Min()|};
                return maxX - minX;
            }
        }
        """,
        Pair("v.Min()", "v.Max()"));

    [Fact]
    public Task OnlyTheBrokenCallOfTwoIsReported() => Verify.VerifyAsync(
        """
        public sealed class Point
        {
            public int X { get; set; }
            public int Y { get; set; }
        }

        public static class C
        {
            private static void Move(int x, int y)
            {
            }

            public static void Run(Point d)
            {
                Move(d.X, {|#0:d.X|});
                Move(d.X, d.Y);
            }
        }
        """,
        Pair("d.X", "d.Y"));

    [Fact]
    public Task OnlyTheBrokenLineOfTwoIsReported() => Verify.VerifyAsync(
        """
        public sealed class Pen
        {
        }

        public sealed class Point
        {
            public int X { get; set; }
            public int Y { get; set; }
        }

        public static class C
        {
            private static void DrawLine(Pen pen, int x1, int y1, int x2, int y2)
            {
            }

            public static void Draw(Pen pen, Point a, Point b)
            {
                DrawLine(pen, a.X, a.Y, b.X, b.Y);
                DrawLine(pen, a.X, a.Y, b.X, {|#0:b.X|});
            }
        }
        """,
        Anomaly("b.X", "b.Y"));

    [Fact]
    public Task NoDiagnostics_ForPairsThatAreNotRenames() => Verify.VerifyAsync(
        """
        public sealed class Source
        {
            public int Width { get; set; }
            public string Name { get; set; }
            public string DisplayName { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int XToX { get; set; }
            public int XToY { get; set; }
            public int YToX { get; set; }
            public int YToY { get; set; }
            public int Max { get; set; }
            public int May { get; set; }
        }

        public sealed class Target
        {
            public int Width { get; set; }
            public int MinWidth { get; set; }
            public string Name { get; set; }
            public string DisplayName { get; set; }
            public int ScaleX { get; set; }
            public int ScaleY { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Value { get; set; }

            private static string Read(string key) => key;

            private static void Process(int left, int right)
            {
            }

            public void Extend(Source s)
            {
                Width = s.Width;
                MinWidth = s.Width;
            }

            public void Label(Source p)
            {
                Name = p.Name;
                DisplayName = p.Name;
            }

            public string Caption(Source p)
            {
                var name = p.Name;
                var displayName = p.Name;
                return name + displayName;
            }

            public void Scale(int scale)
            {
                ScaleX = scale;
                ScaleY = scale;
            }

            public string Literals()
            {
                var user = Read("name");
                var name = Read("name");
                return user + name;
            }

            public void Sum(Source p)
            {
                X = p.Y + p.X;
                Y = p.Y + p.X;
            }

            public void Twice(Source b)
            {
                X = b.XToX;
                Y = b.XToX;
            }

            public void NotAWord(Source b)
            {
                X = b.Max;
                Y = b.Max;
            }

            public void DeclaredLater(int x)
            {
                X = x;
                Y = x;
                var y = 0;
                Process(y, y);
            }

            public void OtherChanges(Target x, Target y, Source a, Source b)
            {
                x.Value = a.X;
                y.Value = b.X;
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_ForArgumentsThatAreNotCopies() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;

        public readonly record struct Size(int Width, int Height);

        public readonly record struct Point(int X, int Y);

        public sealed class Coords
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
        }

        public sealed class Edges
        {
            public int Left { get; set; }
            public int Top { get; set; }
        }

        public sealed class Setter
        {
            public void Set(int a, int b, int c)
            {
            }
        }

        public static class Extensions
        {
            public static int Pow(this int x, int y) => x * y;
        }

        [AttributeUsage(AttributeTargets.All)]
        public sealed class RangeAttribute(int min, int max) : Attribute
        {
            public int Span => max - min;
        }

        public sealed class Limits
        {
            public const int Min = 0;
            public const int Max = 10;

            [Range(Min, Min)]
            public int Value { get; set; }
        }

        public static class C
        {
            private static void Swap(ref int a, ref int b)
            {
            }

            private static void Log(string format, params object[] args)
            {
            }

            public static Size Square(int side) => new Size(side, side);

            public static object Tuple(Coords a) => System.Tuple.Create(a.X, a.X);

            public static int Largest(Coords a) => Math.Max(a.X, a.X);

            public static int Power(int x, int y) => x.Pow(x) + y;

            public static void Sets(Setter v, Setter w, Coords p, Coords q)
            {
                v.Set(p.X, p.Y, p.Y);
                w.Set(q.X, q.Y, q.Z);
            }

            public static Point Corner(Edges x, int y) => new Point(x.Left, x.Top) with { Y = y };

            public static void Exchange(int[] a, int i, int j, int b) => Swap(ref a[i], ref a[j]);

            public static string Text(Coords p) => $"{p.X}{p.X}";

            public static void Trace(Coords p) => Log("{X}{Y}", p.X, p.X);

            public static Dictionary<string, int> Map(Coords p) => new() { { "x", p.X }, { "y", p.X } };
        }
        """);

    // Each case is modelled on a false positive found in dotnet/runtime.
    [Fact]
    public Task NoDiagnostics_ForDeliberatePairs() => Verify.VerifyAsync(
        """
        using System;

        public sealed class Ends
        {
            public int Head;
            public int Tail;
        }

        public sealed class Segment
        {
            public Ends Inner = new();
        }

        public sealed class Half
        {
            public int GetLower() => 0;
            public int GetUpper() => 0;
        }

        public static class C
        {
            private static int Read(ref int value) => value;

            private static int ZipLower(int left, int right) => left;

            private static int ZipUpper(int left, int right) => right;

            private static int Create(int lower, int upper) => lower + upper;

            private static int Hex(int p, int a, int b) => p + a + b;

            private static int Shift(int bits, int key, int value) => bits + key + value;

            private static void Multiply(ref int x, ref int y, out int result) => result = x * y;

            public static int Queue(Segment head, Segment tail)
            {
                var headHead = Read(ref head.Inner.Head);
                var headTail = Read(ref head.Inner.Tail);
                return headHead + headTail + tail.Inner.Head;
            }

            public static int Zip(Half left, Half right) =>
                Create(
                    ZipLower(left.GetLower(), right.GetLower()),
                    ZipUpper(left.GetLower(), right.GetLower()));

            public static int Format(int p, int _a, int _b) => Hex(p, _a >> 24, _a >> 16) + _b;

            public static void Pack(int head, int tail, Ends p)
            {
                Shift(24, head, p.Head);
                Shift(16, tail, p.Head);
            }

            public static int Hypot(int x, int y)
            {
                Multiply(ref x, ref x, out var x2);
                Multiply(ref y, ref y, out var y2);
                return x2 + y2;
            }

            public static bool IsEmpty(Range range)
            {
                Index startIndex = range.Start;
                Index endIndex = range.End;
                return startIndex.Equals(Index.Start) && endIndex.Equals(Index.Start);
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_ForValuesSharedOnPurpose() => Verify.VerifyAsync(
        """
        using System;

        public enum TextAlign
        {
            Left,
            Right,
        }

        public sealed class Cell
        {
            public TextAlign Align { get; set; }
        }

        public sealed class Bounds
        {
            public int minX;
            public int maxX;
            public float minDistance;
            public float maxDistance;

            public void Clamp()
            {
                maxX = Math.Max(maxX, 0);
                minX = Math.Max(minX, 0);
            }

            public void Reset()
            {
                maxDistance = float.MaxValue;
                minDistance = float.MaxValue;
            }

            public static void Align(Cell left, Cell right)
            {
                left.Align = TextAlign.Left;
                right.Align = TextAlign.Left;
            }
        }
        """);

    [Theory]
    [InlineData("", new[] { 0, 1, 2, 3, 4 })]
    [InlineData("TYPO0012.scan = declarations, arguments", new[] { 1, 4 })]
    [InlineData("TYPO0012.scan = Statements,CONDITIONS", new[] { 0, 3 })]
    [InlineData("TYPO0012.scan =  initializers ,, unknown", new[] { 2 })]
    [InlineData("TYPO0012.scan = unknown", new[] { 0, 1, 2, 3, 4 })]
    [InlineData("TYPO0011.scan = arguments", new[] { 0, 1, 2, 3, 4 })]
    public Task Pair_ScanOption(string options, int[] reported) => Verify.VerifyWithOptionsAsync(
        PairInEverySource,
        options,
        [.. reported.Select(location => Pair("b.X", "b.Y", location)), Anomaly("b.Y", "b.Z", 5)]);

    [Fact]
    public Task Avalonia_LastRadiusCopiesPreviousOne() => Verify.VerifyAsync(
        """
        public readonly struct RelativePoint
        {
        }

        public sealed class RadialGradientBrush
        {
            public RelativePoint Center { get; set; }
            public RelativePoint GradientOrigin { get; set; }
            public double RadiusX { get; set; }
            public double RadiusY { get; set; }
        }

        public sealed class ImmutableRadialGradientBrush
        {
            public ImmutableRadialGradientBrush(RadialGradientBrush source)
            {
                Center = source.Center;
                GradientOrigin = source.GradientOrigin;
                RadiusX = source.RadiusX;
                RadiusY = {|#0:source.RadiusX|};
            }

            public RelativePoint Center { get; }
            public RelativePoint GradientOrigin { get; }
            public double RadiusX { get; }
            public double RadiusY { get; }
        }
        """,
        Anomaly("source.RadiusX", "source.RadiusY"));

    [Fact]
    public Task AzureSdk_EqualsComparesLanguageWithEncoding() => Verify.VerifyAsync(
        """
        using System;

        public enum SasProtocol
        {
            None,
            Https,
        }

        public readonly record struct SasIPRange(string Start, string End);

        public struct FileSasBuilder : IEquatable<FileSasBuilder>
        {
            public string CacheControl { get; set; }
            public string ContentDisposition { get; set; }
            public string ContentEncoding { get; set; }
            public string ContentLanguage { get; set; }
            public string ContentType { get; set; }
            public DateTimeOffset ExpiryTime { get; set; }
            public string FilePath { get; set; }
            public string Identifier { get; set; }
            public SasIPRange IPRange { get; set; }
            public string Permissions { get; set; }
            public SasProtocol Protocol { get; set; }
            public string ShareName { get; set; }
            public DateTimeOffset StartTime { get; set; }
            public string Version { get; set; }

            public bool Equals(FileSasBuilder other)
                => CacheControl == other.CacheControl
                && ContentDisposition == other.ContentDisposition
                && ContentEncoding == other.ContentEncoding
                && ContentLanguage == {|#0:other.ContentEncoding|}
                && ContentType == other.ContentType
                && ExpiryTime == other.ExpiryTime
                && FilePath == other.FilePath
                && Identifier == other.Identifier
                && IPRange == other.IPRange
                && Permissions == other.Permissions
                && Protocol == other.Protocol
                && ShareName == other.ShareName
                && StartTime == other.StartTime
                && Version == other.Version
                ;
        }
        """,
        Anomaly("other.ContentEncoding", "other.ContentLanguage"));

    [Fact]
    public Task Mono_EqualsComparesBaseValueWithFrequency() => Verify.VerifyAsync(
        """
        public enum PerformanceCounterType
        {
            NumberOfItems32,
            RawFraction,
        }

        public struct CounterSample
        {
            private long rawValue;
            private long baseValue;
            private long counterFrequency;
            private long systemFrequency;
            private long timeStamp;
            private long timeStamp100nSec;
            private long counterTimeStamp;
            private PerformanceCounterType counterType;

            public bool Equals(CounterSample other)
            {
                return
                    rawValue         == other.rawValue         &&
                    baseValue        == {|#0:other.counterFrequency|} &&
                    counterFrequency == other.counterFrequency &&
                    systemFrequency  == other.systemFrequency  &&
                    timeStamp        == other.timeStamp        &&
                    timeStamp100nSec == other.timeStamp100nSec &&
                    counterTimeStamp == other.counterTimeStamp &&
                    counterType      == other.counterType;
            }
        }
        """,
        Anomaly("other.counterFrequency", "other.baseValue"));

    [Fact]
    public Task OpenRa_LastResizeReadsPreviousLayer() => Verify.VerifyAsync(
        """
        public readonly struct MPos
        {
            public static readonly MPos Zero = default;
        }

        public readonly struct Size
        {
            public Size(int width, int height)
            {
            }
        }

        public sealed class CellLayer<T>
        {
            public T this[MPos pos] => default!;
        }

        public static class CellLayer
        {
            public static CellLayer<T> Resize<T>(CellLayer<T> layer, Size newSize, T defaultValue) => layer;
        }

        public sealed class TerrainTile
        {
        }

        public sealed class ResourceTile
        {
        }

        public sealed class Map
        {
            public CellLayer<TerrainTile> Tiles { get; set; }
            public CellLayer<ResourceTile> Resources { get; set; }
            public CellLayer<byte> Height { get; set; }
            public CellLayer<byte> Ramp { get; set; }

            public void Resize(int width, int height)
            {
                var oldMapTiles = Tiles;
                var oldMapResources = Resources;
                var oldMapHeight = Height;
                var oldMapRamp = Ramp;
                var newSize = new Size(width, height);

                Tiles = CellLayer.Resize(oldMapTiles, newSize, oldMapTiles[MPos.Zero]);
                Resources = CellLayer.Resize(
                    oldMapResources,
                    newSize,
                    oldMapResources[MPos.Zero]
                );
                Height = CellLayer.Resize(oldMapHeight, newSize, oldMapHeight[MPos.Zero]);
                Ramp = CellLayer.Resize(oldMapRamp, newSize, {|#0:oldMapHeight|}[MPos.Zero]);
            }
        }
        """,
        Anomaly("oldMapHeight", "oldMapRamp"));

    [Fact]
    public Task NorthStar_ReactionStrengthCopiesMovementStrength() => Verify.VerifyAsync(
        """
        using System;

        public enum ComfortLevel
        {
            Low,
            High,
        }

        [Serializable]
        public struct ComfortPreset
        {
            public float BoatMovementStrength;
            public float BoatReactionStrength;
        }

        public sealed class GlobalSettings
        {
            public ComfortPreset[] ComfortPresets = new ComfortPreset[2];
            public ComfortLevel ComfortLevel;
            public float BoatMovementStrength;
            public float BoatReactionStrength;

            public event Action<ComfortLevel> OnComfortLevelChanged;

            public void ComfortLevelChanged()
            {
                OnComfortLevelChanged?.Invoke(ComfortLevel);
                var comfortPreset = ComfortPresets[(int)ComfortLevel];
                BoatMovementStrength = comfortPreset.BoatMovementStrength;
                BoatReactionStrength = {|#0:comfortPreset.BoatMovementStrength|};
            }
        }
        """,
        Pair("comfortPreset.BoatMovementStrength", "comfortPreset.BoatReactionStrength"));

    [Fact]
    public Task Jellyfin_HeightFallsBackToWidth() => Verify.VerifyAsync(
        """
        public sealed class MediaStream
        {
            public int? Width { get; set; }
            public int? Height { get; set; }
        }

        public static class FFProbeVideoInfo
        {
            public static void FetchBdInfo(MediaStream blurayVideoStream, MediaStream ffmpegVideoStream)
            {
                if (blurayVideoStream is not null && ffmpegVideoStream is not null)
                {
                    blurayVideoStream.Width = blurayVideoStream.Width.GetValueOrDefault()
                          == 0 ? ffmpegVideoStream.Width : blurayVideoStream.Width;

                    blurayVideoStream.Height = blurayVideoStream.Height.GetValueOrDefault()
                          == 0 ? {|#0:ffmpegVideoStream.Width|} : blurayVideoStream.Height;
                }
            }
        }
        """,
        Pair("ffmpegVideoStream.Width", "ffmpegVideoStream.Height"));

    [Fact]
    public Task NBitcoin_SecondMaskReadsFirstHash() => Verify.VerifyAsync(
        """
        public interface IHasForkId
        {
        }

        public class Transaction
        {
        }

        public sealed class PSBTInput
        {
            private Transaction GetTransaction() => new();

            public bool SameSigHash(uint a, uint b)
            {
                if (a == b)
                {
                    return true;
                }

                if (GetTransaction() is not IHasForkId)
                {
                    return false;
                }

                a = ((uint)a & ~(0x40u));
                b = ((uint){|#0:a|} & ~(0x40u));
                return a == b;
            }
        }
        """,
        Pair("a", "b"));

    [Fact]
    public Task NorthStar_CurrentFrameKeepsPreviousTime() => Verify.VerifyAsync(
        """
        public struct Vector3
        {
            public static Vector3 operator +(Vector3 a, Vector3 b) => a;
        }

        public struct Frame
        {
            public Vector3 Position;
            public float Time;
        }

        public sealed class NPC_DynamicRigs
        {
            private Frame m_previousFrame;
            private Frame m_currentFrame;

            public void OffsetVerlet(Vector3 offset)
            {
                m_previousFrame = new Frame
                {
                    Position = m_previousFrame.Position + offset,
                    Time = m_previousFrame.Time,
                };
                m_currentFrame = new Frame
                {
                    Position = m_currentFrame.Position + offset,
                    Time = {|#0:m_previousFrame|}.Time,
                };
            }
        }
        """,
        Pair("m_previousFrame", "m_currentFrame"));

    [Fact]
    public Task StabilityMatrix_LastClipChecksPreviousName() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;
        using System.Linq;

        public sealed class HybridModelFile
        {
            public static readonly HybridModelFile Default = new();
            public static readonly HybridModelFile None = new();

            public string RelativePath { get; set; }
        }

        public sealed class ClientManager
        {
            public List<HybridModelFile> VaeModels { get; } = new();
            public List<HybridModelFile> Models { get; } = new();
            public List<HybridModelFile> ClipModels { get; } = new();
        }

        public sealed class ModelCardModel
        {
            public string SelectedVaeName { get; set; }
            public string SelectedRefinerName { get; set; }
            public string SelectedClip1Name { get; set; }
            public string SelectedClip2Name { get; set; }
            public string SelectedClip3Name { get; set; }
            public string SelectedClip4Name { get; set; }
        }

        public sealed class ModelCardViewModel
        {
            public ClientManager ClientManager { get; } = new();
            public HybridModelFile SelectedVae { get; set; }
            public HybridModelFile SelectedRefiner { get; set; }
            public HybridModelFile SelectedClip1 { get; set; }
            public HybridModelFile SelectedClip2 { get; set; }
            public HybridModelFile SelectedClip3 { get; set; }
            public HybridModelFile SelectedClip4 { get; set; }

            public void LoadStateFromJsonObject(ModelCardModel model)
            {
                SelectedVae = model.SelectedVaeName is null
                          ? HybridModelFile.Default
                          : ClientManager.VaeModels.FirstOrDefault(x => x.RelativePath ==
                model.SelectedVaeName);

                SelectedRefiner = model.SelectedRefinerName is null
                              ? HybridModelFile.None
                              : ClientManager.Models.FirstOrDefault(x => x.RelativePath ==
                model.SelectedRefinerName);

                SelectedClip1 = model.SelectedClip1Name is null
                            ? HybridModelFile.None
                            : ClientManager.ClipModels.FirstOrDefault(x => x.RelativePath ==
                model.SelectedClip1Name);

                SelectedClip2 = model.SelectedClip2Name is null
                            ? HybridModelFile.None
                            : ClientManager.ClipModels.FirstOrDefault(x => x.RelativePath ==
                model.SelectedClip2Name);

                SelectedClip3 = model.SelectedClip3Name is null
                            ? HybridModelFile.None
                            : ClientManager.ClipModels.FirstOrDefault(x => x.RelativePath ==
                model.SelectedClip3Name);

                SelectedClip4 = {|#0:model.SelectedClip3Name|} is null
                            ? HybridModelFile.None
                            : ClientManager.ClipModels.FirstOrDefault(x => x.RelativePath ==
                model.SelectedClip4Name);
            }
        }
        """,
        Anomaly("model.SelectedClip3Name", "model.SelectedClip4Name"));

    [Fact]
    public Task NasaWorldWind_ConstructorWritesXTwice() => Verify.VerifyAsync(
        """
        public sealed class Point3d
        {
            public double X;
            public double Y;
            public double Z;

            public Point3d(Point3d P)
            {
                X = P.X;
                Y = P.Y;
                {|#0:X|} = P.Z;
            }
        }
        """,
        Anomaly("X", "Z"));

    [Fact]
    public Task OpenCvSharp_TemplateSizePassedForTemporalSize() => Verify.VerifyAsync(
        """
        using System;

        public sealed class Mat
        {
            public IntPtr CvPtr => IntPtr.Zero;
        }

        internal static class NativeMethods
        {
            public static void photo_fastNlMeansDenoisingMulti(
                IntPtr[] srcImgs,
                int srcImgsLength,
                IntPtr dst,
                int imgToDenoiseIndex,
                int temporalWindowSize,
                float h,
                int templateWindowSize,
                int searchWindowSize)
            {
            }
        }

        public static class Cv2
        {
            public static void FastNlMeansDenoisingMulti(
                IntPtr[] srcImgPtrs,
                Mat dst,
                int imgToDenoiseIndex,
                int temporalWindowSize,
                float h = 3,
                int templateWindowSize = 7,
                int searchWindowSize = 21)
            {
                NativeMethods.photo_fastNlMeansDenoisingMulti(
                    srcImgPtrs,
                    srcImgPtrs.Length,
                    dst.CvPtr,
                    imgToDenoiseIndex,
                    {|#0:templateWindowSize|},
                    h,
                    templateWindowSize,
                    searchWindowSize);
            }
        }
        """,
        Anomaly("templateWindowSize", "temporalWindowSize"));

    [Fact]
    public Task RepoDb_FieldsPassedAsQualifiers() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;
        using System.Data;
        using System.Threading;
        using System.Threading.Tasks;

        public sealed class Field
        {
        }

        public interface ITrace
        {
        }

        public interface IStatementBuilder
        {
        }

        public static class DbConnectionExtension
        {
            private static Task<int> UpdateAllAsyncInternal<TEntity>(
                IDbConnection connection,
                string tableName,
                IEnumerable<TEntity> entities,
                IEnumerable<Field> qualifiers,
                int batchSize,
                IEnumerable<Field> fields,
                string hints,
                int? commandTimeout,
                string traceKey,
                IDbTransaction transaction,
                ITrace trace,
                IStatementBuilder statementBuilder,
                CancellationToken cancellationToken)
                where TEntity : class => Task.FromResult(0);

            public static Task<int> UpdateAllAsync<TEntity>(
                this IDbConnection connection,
                string tableName,
                IEnumerable<TEntity> entities,
                IEnumerable<Field> qualifiers,
                int batchSize = 100,
                IEnumerable<Field> fields = null,
                string hints = null,
                int? commandTimeout = null,
                string traceKey = "UpdateAll",
                IDbTransaction transaction = null,
                ITrace trace = null,
                IStatementBuilder statementBuilder = null,
                CancellationToken cancellationToken = default)
                where TEntity : class
            {
                return UpdateAllAsyncInternal<TEntity>(connection: connection,
                    tableName: tableName,
                    entities: entities,
                    qualifiers: {|#0:fields|},
                    batchSize: batchSize,
                    fields: fields,
                    hints: hints,
                    commandTimeout: commandTimeout,
                    traceKey: traceKey,
                    transaction: transaction,
                    trace: trace,
                    statementBuilder: statementBuilder,
                    cancellationToken: cancellationToken);
            }
        }
        """,
        Anomaly("fields", "qualifiers"));

    [Fact]
    public Task SilkNet_LerpPairsTheSameRowOfBothMatrices() => Verify.VerifyAsync(
        """
        public struct Vector3D<T> where T : unmanaged
        {
            public T X;
            public T Y;
            public T Z;
        }

        public static class Vector3D
        {
            public static Vector3D<T> Lerp<T>(Vector3D<T> value1, Vector3D<T> value2, T amount)
                where T : unmanaged => value1;
        }

        public struct Matrix2X3<T> where T : unmanaged
        {
            public Vector3D<T> Row1;
            public Vector3D<T> Row2;

            public Matrix2X3(Vector3D<T> row1, Vector3D<T> row2)
            {
                Row1 = row1;
                Row2 = row2;
            }
        }

        public static class Matrix2X3
        {
            public static unsafe Matrix2X3<T> Lerp<T>(Matrix2X3<T> matrix1, Matrix2X3<T> matrix2, T amount)
                where T : unmanaged
            {
                return new(Vector3D.Lerp(matrix1.Row1, matrix2.Row1, amount), Vector3D.Lerp(matrix1.Row2, matrix2.Row2, amount));
            }
        }
        """);

    [Fact]
    public Task SilkNet_ThirdResultRowRepeatsTheSecondRow() => Verify.VerifyAsync(
        """
        public struct Vec2
        {
            public float X;
            public float Y;

            public static Vec2 operator *(float scale, Vec2 value) => value;

            public static Vec2 operator +(Vec2 left, Vec2 right) => left;
        }

        public struct Matrix3X4
        {
            public float M11, M12, M13, M14;
            public float M21, M22, M23, M24;
            public float M31, M32, M33, M34;
        }

        public struct Matrix4X2
        {
            public Vec2 Row1, Row2, Row3, Row4;
        }

        public struct Matrix3X2
        {
            public Matrix3X2(Vec2 row1, Vec2 row2, Vec2 row3)
            {
            }

            public static Matrix3X2 Multiply(Matrix3X4 value1, Matrix4X2 value2)
            {
                return new(
                    value1.M11 * value2.Row1 + value1.M12 * value2.Row2 + value1.M13 * value2.Row3 + value1.M14 * value2.Row4,
                    value1.M21 * value2.Row1 + value1.M22 * value2.Row2 + value1.M23 * value2.Row3 + value1.M24 * value2.Row4,
                    {|#0:value1.M21|} * value2.Row1 + value1.M22 * value2.Row2 + value1.M23 * value2.Row3 + value1.M24 * value2.Row4
                );
            }
        }
        """,
        Anomaly("value1.M21", "value1.M31"));
}
