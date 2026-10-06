using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.SimilarRunAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class SimilarRunAnalyzerTests
{
    private static DiagnosticResult Anomaly(string actual, string expected) =>
        Verify.Diagnostic(Descriptors.SimilarLineAnomaly).WithLocation(0).WithArguments(actual, expected);

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
}
