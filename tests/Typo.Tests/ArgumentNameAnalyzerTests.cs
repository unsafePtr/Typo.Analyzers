using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Typo.Tests.Verifier<Typo.Analyzers.ArgumentNameAnalyzer>;

namespace Typo.Tests;

public sealed class ArgumentNameAnalyzerTests
{
    private const string Prelude = """
        using System;

        public sealed class Order
        {
            public Guid CustomerId { get; set; }
            public Guid InvoiceId { get; set; }
            public Guid OrderId { get; set; }
        }

        public sealed class Customer
        {
            public Guid Id { get; set; }
        }

        public sealed class Bill
        {
            public Guid Id { get; set; }
        }

        public sealed class Size
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public int Depth { get; set; }
        }

        public sealed record Invoice(Guid CustomerId, Guid InvoiceId);

        public static class Api
        {
            public static void ProcessInvoice(Guid customerId, Guid invoiceId) { }
            public static void Ship(Guid customerId, Guid invoiceId, Guid orderId) { }
            public static void Resize(int width, int height) { }
            public static void Log(object value) { }
            public static int GetWidth() => 1;
            public static int GetHeight() => 2;
        }

        """;

    private const string Loose = "typo_argument_match = loose";

    // Markup: {|#0:...|} is the first argument of a swap and {|#1:...|}, {|#2:...|} the others, in source order.
    private static DiagnosticResult Swapped(string passed, string declared, int count = 2)
    {
        var result = Verify.Diagnostic(Descriptors.SwappedArguments);
        for (var i = 0; i < count; i++)
        {
            result = result.WithLocation(i);
        }

        return result.WithArguments(passed, declared);
    }

    private static DiagnosticResult Misnamed(string argument, string passedAs, string names, int location = 0) =>
        Verify.Diagnostic(Descriptors.ArgumentNamedLikeAnotherParameter).WithLocation(location).WithArguments(argument, passedAs, names);

    private static DiagnosticResult Namesake(string argument, string passedAs, string namesake, int location = 0) =>
        Verify.Diagnostic(Descriptors.ArgumentPassedOverNamesake).WithLocation(location).WithArguments(argument, passedAs, namesake);

    [Fact]
    public Task Swap_Locals() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run()
            {
                var customerId = Guid.NewGuid();
                var invoiceId = Guid.NewGuid();
                Api.ProcessInvoice({|#0:invoiceId|}, {|#1:customerId|});
            }
        }
        """,
        Swapped("'invoiceId' and 'customerId'", "'customerId' and 'invoiceId'"));

    [Fact]
    public Task Swap_PropertiesOfOneObject() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Order order) => Api.ProcessInvoice({|#0:order.InvoiceId|}, {|#1:order.CustomerId|});
        }
        """,
        Swapped("'order.InvoiceId' and 'order.CustomerId'", "'customerId' and 'invoiceId'"));

    [Fact]
    public Task Swap_PrefixedFields() => Verify.VerifyAsync(
        Prelude + """
        public sealed class C
        {
            private Guid _customerId;
            private Guid m_invoiceId;

            public void Run() => Api.ProcessInvoice({|#0:m_invoiceId|}, {|#1:_customerId|});
        }
        """,
        Swapped("'m_invoiceId' and '_customerId'", "'customerId' and 'invoiceId'"));

    [Fact]
    public Task Loose_SwapOfNamedArguments() => Verify.VerifyWithOptionsAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Guid customerId, Guid invoiceId) => Api.ProcessInvoice(customerId: {|#0:invoiceId|}, invoiceId: {|#1:customerId|});
        }
        """,
        Loose,
        Swapped("'invoiceId' and 'customerId'", "'customerId' and 'invoiceId'"));

    [Fact]
    public Task Swap_PascalCaseParametersOfRecord() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static Invoice Run(Guid customerId, Guid invoiceId) => new Invoice({|#0:invoiceId|}, {|#1:customerId|});

            public static Invoice Typed(Guid customerId, Guid invoiceId)
            {
                Invoice invoice = new({|#2:invoiceId|}, {|#3:customerId|});
                return invoice;
            }
        }
        """,
        Swapped("'invoiceId' and 'customerId'", "'CustomerId' and 'InvoiceId'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'invoiceId' and 'customerId'", "'CustomerId' and 'InvoiceId'"));

    [Fact]
    public Task Swap_SharedWordIsIgnored() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run()
            {
                var newWidth = 1;
                var newHeight = 2;
                Api.Resize({|#0:newHeight|}, {|#1:newWidth|});
            }
        }
        """,
        Swapped("'newHeight' and 'newWidth'", "'width' and 'height'"));

    [Fact]
    public Task Swap_NameCarriedByReceiver() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Customer customer, Bill invoice) => Api.ProcessInvoice({|#0:invoice.Id|}, {|#1:customer.Id|});
        }
        """,
        Swapped("'invoice.Id' and 'customer.Id'", "'customerId' and 'invoiceId'"));

    [Fact]
    public Task Swap_MethodCalls() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run() => Api.Resize({|#0:Api.GetHeight()|}, {|#1:Api.GetWidth()|});
        }
        """,
        Swapped("'Api.GetHeight()' and 'Api.GetWidth()'", "'width' and 'height'"));

    [Fact]
    public Task Swap_RotationOfThree() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Guid customerId, Guid invoiceId, Guid orderId) => Api.Ship({|#0:invoiceId|}, {|#1:orderId|}, {|#2:customerId|});
        }
        """,
        Swapped("'invoiceId', 'orderId' and 'customerId'", "'customerId', 'invoiceId' and 'orderId'", count: 3));

    [Fact]
    public Task Swap_FrameworkMethod() => Verify.VerifyAsync(
        """
        using System;

        public static class C
        {
            public static int Run(int value, int min, int max) => Math.Clamp(value, {|#0:max|}, {|#1:min|});
        }
        """,
        Swapped("'max' and 'min'", "'min' and 'max'"));

    [Fact]
    public Task Misnamed_NothingNamesItsParameterBack() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Guid invoiceId, Guid orderId) => Api.ProcessInvoice({|#0:invoiceId|}, orderId);
        }
        """,
        Misnamed("invoiceId", "customerId", "invoiceId"));

    [Fact]
    public Task Namesake_UnreadParameterNamedLikeTheParameter() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Guid customerId, Guid invoiceId, Guid orderId) => Api.ProcessInvoice(customerId, {|#0:orderId|});
            public static void Pascal(Guid CustomerId, Guid InvoiceId, Guid OrderId) => Api.ProcessInvoice(CustomerId, {|#1:OrderId|});
        }
        """,
        Namesake("orderId", "invoiceId", "invoiceId"),
        Namesake("OrderId", "invoiceId", "InvoiceId", location: 1));

    [Fact]
    public Task Loose_NamesakeSiblingProperty() => Verify.VerifyWithOptionsAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Size size) => Api.Resize(size.Width, {|#0:size.Depth|});
        }
        """,
        Loose,
        Namesake("size.Depth", "height", "size.Height"));

    // Names the member's own code never spells: a base class's field and a statically imported one.
    [Fact]
    public Task Loose_NamesakesDeclaredOutsideTheMember() => Verify.VerifyWithOptionsAsync(
        """
        using System;
        using static Defaults;

        public static class Defaults
        {
            public static readonly Guid InvoiceId = Guid.Empty;
        }

        public static class Api
        {
            public static void ProcessInvoice(Guid customerId, Guid invoiceId) { }
        }

        public class Base
        {
            protected Guid customerId;
        }

        public sealed class Derived : Base
        {
            public void Run(Guid orderId, Guid clientId) => Api.ProcessInvoice({|#0:clientId|}, {|#1:orderId|});
        }
        """,
        Loose,
        Namesake("clientId", "customerId", "customerId"),
        Namesake("orderId", "invoiceId", "InvoiceId", location: 1));

    [Fact]
    public Task Swap_OutArguments() => Verify.VerifyAsync(
        """
        public struct Texture { }

        public sealed class Resources
        {
            public Texture cameraDepthTexture;
            public Texture cameraNormalsTexture;
        }

        public sealed class Pass
        {
            public void Render(int graph, out Texture cameraNormalsTexture, out Texture cameraDepthTexture, ref int data)
            {
                cameraNormalsTexture = default;
                cameraDepthTexture = default;
            }

            public void Run(Resources frameResources, ref int data) =>
                Render(1, out {|#0:frameResources.cameraDepthTexture|}, out {|#1:frameResources.cameraNormalsTexture|}, ref data);
        }
        """,
        Swapped("'frameResources.cameraDepthTexture' and 'frameResources.cameraNormalsTexture'", "'cameraNormalsTexture' and 'cameraDepthTexture'"));

    // Single letters name one value's members when two values are compared, as a and b are here.
    [Fact]
    public Task Swap_SingleLetterNamesOfTwoValues() => Verify.VerifyAsync(
        """
        public sealed class Block
        {
            public int[] Variables = [];
            public int[] Expressions = [];
        }

        public static class Comparer
        {
            public static bool CompareList(int[] a, int[] b) => a.Length == b.Length;

            public static bool CompareBlock(Block a, Block b) => CompareList(a.Variables, b.Variables) && CompareList({|#0:b.Expressions|}, {|#1:a.Expressions|});
        }
        """,
        Swapped("'b.Expressions' and 'a.Expressions'", "'a' and 'b'"));

    private const string IdleSwaps = """
        using System;

        public sealed class Buffer { }

        public static class Extensions
        {
            public static void CopyInto(this Buffer source, Buffer destination) { }
        }

        public struct Color
        {
            public int R;
            public int G;
            public int B;
        }

        public struct Vector
        {
            public double X;
            public double Y;
        }

        public static class Numbers
        {
            public static int ConvertRgbToHsv(int r, int b, int g) => r + b + g;

            public static double Distance(Vector from, Vector to) => 0;

            public static double Offset(double from, double to) => to - from;
        }

        public static class Assert
        {
            public static void Throws<T>(Action action) { }
        }

        public static class C
        {
            public static void Copy(Buffer source, Buffer destination) => {|#0:destination|}.CopyInto({|#1:source|});

            public static int Run(Color theColor) => Numbers.ConvertRgbToHsv(theColor.R, {|#2:theColor.G|}, {|#3:theColor.B|});

            public static double Angle(Vector diff) => Math.Atan2({|#4:diff.X|}, {|#5:diff.Y|});

            public static double Length(Vector from, Vector to) => Numbers.Distance({|#6:to|}, {|#7:from|});

            public static double Back(double from, double to) => -Numbers.Offset({|#8:to|}, {|#9:from|});

            public static void Fails(double from, double to) => Assert.Throws<ArgumentException>(() => Numbers.Offset({|#10:to|}, {|#11:from|}));
        }
        """;

    // A receiver called on, one value's components in another order, the operands of an order-free method, a negated result and a call meant to throw.
    [Fact]
    public Task Silent_SwapsThatCannotMatter() => Verify.VerifyAsync(Unmarked(IdleSwaps));

    [Fact]
    public Task Loose_SwapsThatCannotMatter() => Verify.VerifyWithOptionsAsync(
        IdleSwaps,
        Loose,
        Swapped("'destination' and 'source'", "'source' and 'destination'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'theColor.G' and 'theColor.B'", "'b' and 'g'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(4).WithLocation(5).WithArguments("'diff.X' and 'diff.Y'", "'y' and 'x'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(6).WithLocation(7).WithArguments("'to' and 'from'", "'from' and 'to'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(8).WithLocation(9).WithArguments("'to' and 'from'", "'from' and 'to'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(10).WithLocation(11).WithArguments("'to' and 'from'", "'from' and 'to'"));

    private const string Repeats = Prelude + """
        public static class C
        {
            public static void Run(int width, int height)
            {
                Api.Resize({|#0:height|}, {|#1:width|});
                Api.Resize({|#2:height|}, {|#3:width|});
            }

            public static void Again(int width, int height) => Api.Resize({|#4:height|}, {|#5:width|});

            public static void Bill(Guid customerId, Guid invoiceId, Guid orderId)
            {
                Api.ProcessInvoice(customerId, {|#6:orderId|});
                Api.ProcessInvoice(customerId, {|#7:orderId|});
            }
        }
        """;

    // A member that repeats a swap or passes a namesake over at every call shows one choice, so tight matching reports its first call only.
    [Fact]
    public Task Swap_AndNamesakeOncePerMember() => Verify.VerifyAsync(
        Repeats.Replace("{|#2:height|}, {|#3:width|}", "height, width", StringComparison.Ordinal).Replace("{|#7:orderId|}", "orderId", StringComparison.Ordinal),
        Swapped("'height' and 'width'", "'width' and 'height'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(4).WithLocation(5).WithArguments("'height' and 'width'", "'width' and 'height'"),
        Namesake("orderId", "invoiceId", "invoiceId", location: 6));

    [Fact]
    public Task Loose_EveryRepeat() => Verify.VerifyWithOptionsAsync(
        Repeats,
        Loose,
        Swapped("'height' and 'width'", "'width' and 'height'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'height' and 'width'", "'width' and 'height'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(4).WithLocation(5).WithArguments("'height' and 'width'", "'width' and 'height'"),
        Namesake("orderId", "invoiceId", "invoiceId", location: 6),
        Namesake("orderId", "invoiceId", "invoiceId", location: 7));

    [Fact]
    public Task Swap_ArgumentsWithFewerOrMoreWords() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public sealed class SelectionChanged
        {
            public SelectionChanged(object source, IList<object> removedItems, IList<object> addedItems) { }
        }

        public static class Consts
        {
            public static readonly int[] NECESSARY_VIDEO_MIAS = [];
            public static readonly int[] NECESSARY_AUDIO_MIAS = [];
            public static readonly int[] NECESSARY_IMAGE_MIAS = [];
        }

        public static class C
        {
            public static SelectionChanged Short(object source, IList<object> added, IList<object> removed) => new(source, {|#0:added|}, {|#1:removed|});

            public static SelectionChanged Long(object source, IList<object> addedSelectedItems, IList<object> removedSelectedItems) =>
                new SelectionChanged(source, {|#2:addedSelectedItems|}, {|#3:removedSelectedItems|});

            public static void Create(string drive, int[] videoMIATypeIds, int[] imageMIATypeIds, int[] audioMIATypeIds) { }

            public static void Examine(string drive) => Create(drive, Consts.NECESSARY_VIDEO_MIAS, {|#4:Consts.NECESSARY_AUDIO_MIAS|}, {|#5:Consts.NECESSARY_IMAGE_MIAS|});
        }
        """,
        Swapped("'added' and 'removed'", "'removedItems' and 'addedItems'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'addedSelectedItems' and 'removedSelectedItems'", "'removedItems' and 'addedItems'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(4).WithLocation(5).WithArguments("'Consts.NECESSARY_AUDIO_MIAS' and 'Consts.NECESSARY_IMAGE_MIAS'", "'imageMIATypeIds' and 'audioMIATypeIds'"));

    [Fact]
    public Task Swap_ReceiverNamesTheHandle() => Verify.VerifyAsync(
        """
        using System;

        public sealed class Mat
        {
            public IntPtr CvPtr;
        }

        public static class C
        {
            public static void MatToVec(IntPtr vector, IntPtr matrix, IntPtr jacobian) { }

            public static void Run(Mat matrixM, Mat vectorM, Mat jacobianM) => MatToVec({|#0:matrixM.CvPtr|}, {|#1:vectorM.CvPtr|}, jacobianM.CvPtr);
        }
        """,
        Swapped("'matrixM.CvPtr' and 'vectorM.CvPtr'", "'vector' and 'matrix'"));

    [Fact]
    public Task Swap_ForwardingOverload() => Verify.VerifyAsync(
        """
        public abstract class Interaction
        {
            public abstract void Followup(string filePath, string fileName = null, string text = null);

            public void Send(string filePath, string text, string fileName) => Followup(filePath, {|#0:text|}, {|#1:fileName|});
        }
        """,
        Swapped("'text' and 'fileName'", "'fileName' and 'text'"));

    // 'id' is a word of 'parentId' too, so only the whole name 'id' tells which of the two an argument means.
    [Fact]
    public Task Swap_OneNameContainsTheOther() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static void Attach(int parentId, int id) { }

            public static void Run(int id, int parentId, int childId)
            {
                Attach({|#0:id|}, {|#1:parentId|});
                Attach(parentId, childId);
            }
        }
        """,
        Swapped("'id' and 'parentId'", "'parentId' and 'id'"));

    [Fact]
    public Task Swap_ThroughCastsSignsAndFallbacks() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Cast(long height, int width) => Api.Resize({|#0:(int)height|}, {|#1:-width|});

            public static void Fallback(Size size) => Api.Resize({|#2:size?.Height ?? 0|}, {|#3:size?.Width ?? 0|});

            public static void Value(int? maxHeight, int? maxWidth) => Api.Resize({|#4:maxHeight.Value|}, {|#5:maxWidth.GetValueOrDefault() + 1|});
        }
        """,
        Swapped("'(int)height' and '-width'", "'width' and 'height'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'size?.Height ?? 0' and 'size?.Width ?? 0'", "'width' and 'height'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(4).WithLocation(5).WithArguments("'maxHeight.Value' and 'maxWidth.GetValueOrDefault() + 1'", "'width' and 'height'"));

    [Fact]
    public Task Swap_ExtensionCalledStaticallyIndexerAndConstructorInitializer() => Verify.VerifyAsync(
        """
        public sealed class Buffer { }

        public static class Extensions
        {
            public static void CopyInto(this Buffer source, Buffer destination) { }
        }

        public sealed class Grid
        {
            public int this[int row, int column] => row + column;
        }

        public class Shape
        {
            public Shape(int width, int height) { }
        }

        public sealed class Box : Shape
        {
            public Box(int width, int height)
                : base({|#4:height|}, {|#5:width|})
            {
            }

            public static void Copy(Buffer source, Buffer destination) => Extensions.CopyInto({|#0:destination|}, {|#1:source|});

            public static int Read(Grid grid, int row, int column) => grid[{|#2:column|}, {|#3:row|}];
        }
        """,
        Swapped("'destination' and 'source'", "'source' and 'destination'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'column' and 'row'", "'row' and 'column'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(4).WithLocation(5).WithArguments("'height' and 'width'", "'width' and 'height'"));

    private const string ShortenedAndMistypedNames = Prelude + """
        public static class C
        {
            public static void Copy(byte[] source, byte[] destination) { }

            public static void Run(byte[] src, byte[] dst, int w, int h, int heigth, int widht)
            {
                Copy({|#0:dst|}, {|#1:src|});
                Api.Resize({|#2:heigth|}, {|#3:widht|});
                Api.Resize({|#4:h|}, {|#5:w|});
            }
        }
        """;

    // A lone initial fits too many words, so only loose matching takes 'h' for 'height'.
    [Fact]
    public Task Swap_ShortenedAndMistypedNames() => Verify.VerifyAsync(
        ShortenedAndMistypedNames.Replace("{|#4:h|}", "h", StringComparison.Ordinal).Replace("{|#5:w|}", "w", StringComparison.Ordinal),
        Swapped("'dst' and 'src'", "'source' and 'destination'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'heigth' and 'widht'", "'width' and 'height'"));

    [Fact]
    public Task Loose_SwapOfInitials() => Verify.VerifyWithOptionsAsync(
        ShortenedAndMistypedNames,
        Loose,
        Swapped("'dst' and 'src'", "'source' and 'destination'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'heigth' and 'widht'", "'width' and 'height'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(4).WithLocation(5).WithArguments("'h' and 'w'", "'width' and 'height'"));

    private const string DeliberateSwaps = """
        public readonly struct Vector
        {
            public Vector(double x, double y) => (X, Y) = (x, y);

            public double X { get; }

            public double Y { get; }
        }

        public static class Book
        {
            public static void Reset(double[] bids, double[] asks) { }
        }

        public static class C
        {
            public static Vector Normal(Vector d) => new Vector({|#0:d.Y|}, {|#1:-d.X|});

            public static Vector Orientate(Vector point, bool isTransposed) => isTransposed ? new Vector({|#2:point.Y|}, {|#3:point.X|}) : point;

            public static void Publish(double[] invertedAsks, double[] invertedBids) => Book.Reset({|#4:invertedAsks|}, {|#5:invertedBids|});

            public static void Named(double[] bids, double[] asks) => Book.Reset(bids: {|#6:asks|}, asks: {|#7:bids|});

            public static Vector Either(Vector p, bool vertical) => vertical ? new Vector({|#8:p.Y|}, {|#9:p.X|}) : new Vector(p.X, p.Y);
        }

        public sealed class PriceComparerDescending : System.Collections.Generic.IComparer<double>
        {
            public int Compare(double x, double y) => System.Collections.Generic.Comparer<double>.Default.Compare({|#10:y|}, {|#11:x|});
        }
        """;

    // A turned vector, a guarding reversal, a reversal word in the names, named arguments and both orders in one member.
    [Fact]
    public Task Silent_SwapsThatLookDeliberate() => Verify.VerifyAsync(Unmarked(DeliberateSwaps));

    [Fact]
    public Task Loose_SwapsThatLookDeliberate() => Verify.VerifyWithOptionsAsync(
        DeliberateSwaps,
        Loose,
        Swapped("'d.Y' and '-d.X'", "'x' and 'y'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'point.Y' and 'point.X'", "'x' and 'y'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(4).WithLocation(5).WithArguments("'invertedAsks' and 'invertedBids'", "'bids' and 'asks'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(6).WithLocation(7).WithArguments("'asks' and 'bids'", "'bids' and 'asks'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(8).WithLocation(9).WithArguments("'p.Y' and 'p.X'", "'x' and 'y'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(10).WithLocation(11).WithArguments("'y' and 'x'", "'x' and 'y'"));

    private const string LooseFits = Prelude + """
        public static class Feed
        {
            public static void SetLevels(int levels, long timestamp, long receivedTimestamp) { }
        }

        public static class Text
        {
            public static string Cut(string text, int startIndex, int length) => text;
        }

        public sealed class ClientTag
        {
            public static readonly ClientTag None = new();

            public static void Cancel(ClientTag joinTag, ClientTag clientTag) { }
        }

        public static class Field
        {
            public const int A = 3;

            public static int Sub(int a, int b) => a - b;

            public static int Square(int x) => x * x;
        }

        public static class C
        {
            public static void Run(Order order, Guid invoiceId, int levels, long eventTime, long timestamp, string text, int length, int maxBulks)
            {
                Feed.SetLevels(levels, eventTime, {|#0:timestamp|});
                Text.Cut(text, {|#1:length - 1|}, 1);
                Api.ProcessInvoice({|#2:invoiceId|}, order.InvoiceId);
                _ = Math.Clamp({|#4:maxBulks|}, 1, 4);
                Api.ProcessInvoice(customerId: {|#5:invoiceId|}, invoiceId: Guid.Empty);
                ClientTag.Cancel({|#6:ClientTag.None|}, null);
                _ = Field.Sub(Field.Square(levels), {|#7:Field.A|});
            }

            public static void Used(Order order)
            {
                var invoiceId = order.InvoiceId;
                Api.Log(invoiceId);
                var orderId = order.OrderId;
                Api.ProcessInvoice(order.CustomerId, {|#3:orderId|});
            }
        }
        """;

    // Tight matching leaves out a plainer name for what its own parameter takes, arithmetic, a parameter already served and a namesake the member reads.
    // It also leaves out a longer name holding a one-word parameter, a named argument beside the parameter it names, a static member's type and a one-letter parameter.
    [Fact]
    public Task Silent_LooseFits() => Verify.VerifyAsync(Unmarked(LooseFits));

    [Fact]
    public Task Loose_ReportsLooseFits() => Verify.VerifyWithOptionsAsync(
        LooseFits,
        Loose,
        Misnamed("timestamp", "receivedTimestamp", "timestamp"),
        Misnamed("length - 1", "startIndex", "length", location: 1),
        Misnamed("invoiceId", "customerId", "invoiceId", location: 2),
        Namesake("orderId", "invoiceId", "invoiceId", location: 3),
        Misnamed("maxBulks", "value", "max", location: 4),
        Misnamed("invoiceId", "customerId", "invoiceId", location: 5),
        Misnamed("ClientTag.None", "joinTag", "clientTag", location: 6),
        Misnamed("Field.A", "b", "a", location: 7));

    private static string Unmarked(string source) => System.Text.RegularExpressions.Regex.Replace(source, @"\{\|#\d+:(.*?)\|\}", "$1");

    // Numbered parameters are told apart by position, and callers number their variables from their own base.
    [Fact]
    public Task Swap_NumberedParametersOnlyByTheirWholeNames() => Verify.VerifyAsync(
        """
        using System;

        public static class Paint
        {
            public static int Blend(int color1, int color2, double amount) => color1;

            public static void Line(double x0, double y0, double x1, double y1) { }
        }

        public static class C
        {
            public static int Run(int color1, int color2, int color3, double x1, double y1, double x2, double y2)
            {
                Paint.Line(x1, y1, x2, y2);
                _ = Math.Min(x2, x1);
                _ = Paint.Blend(color2, color3, 0.5);
                return Paint.Blend({|#0:color2|}, {|#1:color1|}, 0.5);
            }
        }
        """,
        Swapped("'color2' and 'color1'", "'color1' and 'color2'"));

    [Fact]
    public Task Swap_ThroughConversionCalls() => Verify.VerifyAsync(
        """
        using System;

        public readonly struct Stamp
        {
            public DateTime ToDateTime() => default;
        }

        public sealed class Request
        {
            public Stamp From;
            public Stamp To;
        }

        public static class Trades
        {
            public static void Load(DateTime from, DateTime to) { }

            public static void Move(string ticker, string from, string to) { }

            public static void Quote(string side, decimal price) { }
        }

        public sealed class Desk
        {
            public string ToBotSide(string side) => side;

            public void Send(string side, decimal price) => Trades.Quote(ToBotSide(side), price);
        }

        public static class C
        {
            public static void Run(Request request, string asset, string fromAccount, string toAccount)
            {
                Trades.Load(request.From.ToDateTime(), request.To.ToDateTime());
                Trades.Move(asset.ToUpperInvariant(), fromAccount.ToLowerInvariant(), toAccount.ToLowerInvariant());
            }

            public static void Reload(Request request) => Trades.Load({|#0:request.To.ToDateTime()|}, {|#1:request.From.ToDateTime()|});
        }
        """,
        Swapped("'request.To.ToDateTime()' and 'request.From.ToDateTime()'", "'from' and 'to'"));

    [Fact]
    public Task Misnamed_NamesAnOmittedOptionalParameter() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static void Show(string message, string title = null, string caption = null) { }

            public static void Run(string message, string caption) => Show(message, {|#0:caption|});
        }
        """,
        Misnamed("caption", "title", "caption"));

    [Fact]
    public Task Misnamed_OtherArgumentHasNoName() => Verify.VerifyAsync(
        """
        using System;

        public static class C
        {
            public static Exception Run(string value, string message) => new ArgumentException(nameof(value), {|#0:message|});
        }
        """,
        Misnamed("message", "paramName", "message"));

    private const string NamesakesLeftAlone = Prelude + """
        public abstract class Handler
        {
            public abstract void Handle(Guid customerId, Guid invoiceId, Guid orderId);
        }

        public sealed class Override : Handler
        {
            public override void Handle(Guid customerId, Guid invoiceId, Guid orderId) => Api.ProcessInvoice(customerId, {|#0:orderId|});
        }

        public interface IHandler
        {
            void Handle(Guid customerId, Guid invoiceId, Guid orderId);
        }

        public sealed class Explicit : IHandler
        {
            void IHandler.Handle(Guid customerId, Guid invoiceId, Guid orderId) => Api.ProcessInvoice(customerId, {|#1:orderId|});
        }

        public static class Bus
        {
            public static void Raise(object sender, EventArgs args) { }
        }

        public sealed class Relay
        {
            public object Source { get; } = new();

            public void OnChanged(object sender, EventArgs e) => Bus.Raise({|#2:Source|}, e);
        }

        public static class C
        {
            public static void Run(Order order)
            {
                var customerId = order.CustomerId;
                var invoiceId = order.InvoiceId;
                var orderId = order.OrderId;
                Api.ProcessInvoice(customerId, {|#3:orderId|});
            }

            public static Action<Guid, Guid, Guid> Lambda() => (customerId, invoiceId, orderId) => Api.ProcessInvoice(customerId, {|#4:orderId|});

            public static void Typed(int width, short height, int depth) => Api.Resize(width, {|#5:depth|});
        }
        """;

    // Tight matching passes over a namesake in a member whose signature is set elsewhere, a local namesake and one of another type than the argument.
    [Fact]
    public Task Silent_NamesakesLeftAlone() => Verify.VerifyAsync(Unmarked(NamesakesLeftAlone));

    [Fact]
    public Task Loose_NamesakesLeftAlone() => Verify.VerifyWithOptionsAsync(
        NamesakesLeftAlone,
        Loose,
        Namesake("orderId", "invoiceId", "invoiceId"),
        Namesake("orderId", "invoiceId", "invoiceId", location: 1),
        Namesake("Source", "sender", "sender", location: 2),
        Namesake("orderId", "invoiceId", "invoiceId", location: 3),
        Namesake("orderId", "invoiceId", "invoiceId", location: 4),
        Namesake("depth", "height", "height", location: 5));

    private const string IdleMisnamings = """
        using System;

        public static class Text
        {
            public static void Show(string message, string title = null, string caption = null) { }

            public static void Put(string name, object value) { }

            public static int Pick(int first, int second) => first;

            public static bool Same(this string span, string other) => span == other;
        }

        public sealed class Settings
        {
            public string Caption { get; } = "";
        }

        public static class Assert
        {
            public static void Throws<T>(Action action) { }
        }

        public static class C
        {
            public static void Run(string message, string name, int second, string span, string text, Settings settings)
            {
                Text.Show(message, {|#0:message + "!"|});
                var caption = "Note";
                Text.Show(message, {|#1:caption|});
                Text.Show(message, {|#2:settings.Caption|});
                Text.Put("name", {|#3:name|});
                _ = Text.Pick({|#4:second|}, 3);
                _ = text.Same({|#5:span|});
            }

            public static void Fails(string message, string caption) => Assert.Throws<ArgumentException>(() => Text.Show(message, {|#6:caption|}));

            public static void Longer(string message, string captionText) => Text.Show(message, {|#7:captionText|});
        }
        """;

    // Tight matching passes over a computed value, a local or another object's member, a labelled value, names of positions, a receiver called on, a call meant to throw and a longer name.
    [Fact]
    public Task Silent_MisnamingsLeftAlone() => Verify.VerifyAsync(Unmarked(IdleMisnamings));

    [Fact]
    public Task Loose_MisnamingsLeftAlone() => Verify.VerifyWithOptionsAsync(
        IdleMisnamings,
        Loose,
        Misnamed("message + \"!\"", "title", "message"),
        Misnamed("caption", "title", "caption", location: 1),
        Misnamed("settings.Caption", "title", "caption", location: 2),
        Misnamed("name", "value", "name", location: 3),
        Misnamed("second", "first", "second", location: 4),
        Misnamed("span", "other", "span", location: 5),
        Misnamed("caption", "title", "caption", location: 6),
        Misnamed("captionText", "title", "caption", location: 7));

    // A local stands where the parameter it names gets a fixed value, as text does where Environment.NewLine goes in.
    [Fact]
    public Task Misnamed_LocalBesideAFixedValue() => Verify.VerifyAsync(
        """
        using System;

        public static class Tags
        {
            public static int Count(string text, string tag) => 0;

            public static string Fix(string input)
            {
                var text = input.Trim();
                return Count(Environment.NewLine, {|#0:text|}) > 1 ? text : input;
            }
        }
        """,
        Misnamed("text", "tag", "text"));

    [Fact]
    public Task Silent_ParameterTheCompilerFills() => Verify.VerifyAsync(
        """
        using System.Runtime.CompilerServices;

        public static class Guard
        {
            public static string NotEmpty(string argument, [CallerArgumentExpression(nameof(argument))] string paramName = null) => argument;
        }

        public static class Storage
        {
            public static string Open(string fileName) => Guard.NotEmpty(fileName);
        }
        """);

    // A static member cannot read a positional parameter, a field holds a copy of the primary constructor parameter, and an 'out var' names what the call writes.
    [Fact]
    public Task Silent_NamesakesTheCallCannotRead() => Verify.VerifyAsync(
        """
        public sealed record Indicators(double FlowUp, double FlowDown)
        {
            public static readonly Indicators Empty = new(double.NaN, double.NaN);
        }

        public static class Engine
        {
            public static void Configure(string startupFileName, bool push) { }
        }

        public sealed class Receiver(string startupFileName)
        {
            private string _strategyFileName = startupFileName;

            public void Reload() => Engine.Configure(_strategyFileName, true);

            public void Rename(string name) => _strategyFileName = name;
        }

        public static class Registry
        {
            public static bool TryGet(string key, out object instrumentManager)
            {
                instrumentManager = null;
                return false;
            }
        }

        public static class Router
        {
            public static bool Route(string key, object instrumentManager) => Registry.TryGet(key, out var destinationManager);
        }
        """);

    [Fact]
    public Task Silent_LocalsOfTopLevelStatements() => Verify.VerifyProgramAsync(
        """
        var slots = 10;
        System.Console.WriteLine(slots);

        public sealed class Book(int slots, int depth)
        {
            public int Size => slots + depth;
        }

        public static class Factory
        {
            public static Book Make(int maxLevels) => new(maxLevels, 0);
        }
        """);

    private const string Executors = """
        public sealed class NullLogger
        {
            public static readonly NullLogger Instance = new();
        }

        public sealed class Executor(string client, NullLogger logger)
        {
            public string Client => client;

            public NullLogger Logger => logger;
        }

        """;

    // Another constructor's initializer, a nested type and a static local function cannot read a primary constructor parameter, and passing one to the base type reads it.
    [Fact]
    public Task Silent_PrimaryConstructorParametersOutOfReachOrRead() => Verify.VerifyAsync(
        Executors + """
        public sealed class Client(string name, NullLogger logger)
        {
            public Client(string name) : this(name, NullLogger.Instance)
            {
            }

            public string Name => name;
        }

        public sealed class Service(NullLogger logger)
        {
            private sealed class Worker
            {
                public Executor Make() => new("worker", NullLogger.Instance);
            }
        }

        public sealed class Monitor(NullLogger logger)
        {
            public Executor Start()
            {
                return Run();

                static Executor Run() => new("monitor", NullLogger.Instance);
            }
        }

        public class TestBase(NullLogger logger)
        {
            public NullLogger Logger => logger;
        }

        public sealed class Tests(NullLogger logger) : TestBase(logger)
        {
            public Executor Make() => new("tests", NullLogger.Instance);
        }
        """);

    [Fact]
    public Task Namesake_UnreadPrimaryConstructorParameter() => Verify.VerifyAsync(
        Executors + """
        public sealed class Service(NullLogger logger)
        {
            public Executor Make() => new("service", {|#0:NullLogger.Instance|});
        }
        """,
        Namesake("NullLogger.Instance", "logger", "logger"));

    [Theory]
    [InlineData("private int _copy = Other.count;")]
    [InlineData("private int Copy { get; } = Other.count;")]
    [InlineData("private int _copy = Other.Make(count: 0);")]
    [InlineData("private int Read(int count) => count;")]
    public Task Namesake_PrimaryConstructorParameterIgnoresUnrelatedNames(string member) => Verify.VerifyAsync(
        $$"""
        public static class Other
        {
            public static int count;
            public static int Make(int count) => count;
        }

        public sealed class Service(int count)
        {
            {{member}}
            private int _height;

            public void Run()
            {
                Save({|#0:_height|}, false);
                Save(_height, false);
            }

            public void Again() => Save({|#1:_height|}, false);

            private static void Save(int count, bool enabled) { }
        }
        """,
        Namesake("_height", "count", "count"),
        Namesake("_height", "count", "count", location: 1));

    [Theory]
    [InlineData("private int _copy = count;")]
    [InlineData("private int Copy { get; } = count;")]
    [InlineData("private int Read() => count;")]
    public Task Silent_PrimaryConstructorParameterIsActuallyRead(string member) => Verify.VerifyAsync(
        $$"""
        public sealed class Service(int count)
        {
            {{member}}
            private int _height;

            public void Run() => Save(_height, false);

            private static void Save(int count, bool enabled) { }
        }
        """);

    [Theory]
    [InlineData("private int Read(int count) => count;", true)]
    [InlineData("private int Read() => count;", false)]
    public Task PrimaryConstructor_ReadsAreBoundAcrossPartialDeclarations(string member, bool reports)
    {
        var argument = reports ? "{|#0:_height|}" : "_height";
        var test = new CSharpAnalyzerTest<Typo.Analyzers.ArgumentNameAnalyzer, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
        };
        test.TestState.Sources.Add($$"""
            public sealed partial class Service(int count)
            {
                private int _height;
                public void Run() => Save({{argument}}, false);
                private static void Save(int count, bool enabled) { }
            }
            """);
        test.TestState.Sources.Add($$"""
            public sealed partial class Service
            {
                {{member}}
            }
            """);
        if (reports)
        {
            test.ExpectedDiagnostics.Add(Namesake("_height", "count", "count"));
        }

        return test.RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task Silent_NamesThatFitLoosely() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Order order, Guid id, Guid invoiceId, Guid newInvoiceId, Guid customerId, int w, int height, int minHeight)
            {
                Api.ProcessInvoice(id, newInvoiceId);
                Api.ProcessInvoice(order.CustomerId, id);
                Api.Resize(w, minHeight);
                Api.Log(invoiceId);
                Api.Log(customerId);
                Api.Log(height);
            }
        }
        """);

    [Fact]
    public Task Silent_CorrectCalls() => Verify.VerifyAsync(
        Prelude + """
        public sealed class C
        {
            private Guid _customerId;
            private Guid _invoiceId;

            public void Run(Order order, Customer customer, Bill invoice, Size size, Guid customerId, Guid invoiceId)
            {
                Api.ProcessInvoice(customerId, invoiceId);
                Api.ProcessInvoice(_customerId, _invoiceId);
                Api.ProcessInvoice(order.CustomerId, order.InvoiceId);
                Api.ProcessInvoice(customer.Id, invoice.Id);
                Api.ProcessInvoice(invoiceId: invoiceId, customerId: customerId);
                Api.Resize(size.Width, size.Height);
                Api.Resize(Api.GetWidth(), Api.GetHeight());
                Api.Resize(1, 2);
                _ = new Invoice(customerId, invoiceId);
            }
        }
        """);

    // The same value for both parameters is TYPO0012's finding, with its guard for deliberate repeats.
    [Fact]
    public Task Silent_SameValueTwice() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Guid customerId) => Api.ProcessInvoice(customerId, customerId);

            public static void WithTheOtherAtHand(Guid customerId, Guid invoiceId)
            {
                Api.ProcessInvoice(customerId, customerId);
                Api.Log(invoiceId);
            }
        }
        """);

    [Fact]
    public Task Silent_TypesDoNotAllowTheOtherOrder() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static void Write(string name, int count) { }

            public static void Run(string count, int name) => Write(count, name);
        }
        """);
}
