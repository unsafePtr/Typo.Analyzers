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
    public Task Swap_NamedArguments() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Guid customerId, Guid invoiceId) => Api.ProcessInvoice(customerId: {|#0:invoiceId|}, invoiceId: {|#1:customerId|});
        }
        """,
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
        }
        """,
        Namesake("orderId", "invoiceId", "invoiceId"));

    [Fact]
    public Task Namesake_SiblingProperty() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Size size) => Api.Resize(size.Width, {|#0:size.Depth|});
        }
        """,
        Namesake("size.Depth", "height", "size.Height"));

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

    [Fact]
    public Task Swap_SingleLetterNames() => Verify.VerifyAsync(
        """
        using System;

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

        public static class C
        {
            public static int ConvertRgbToHsv(int r, int b, int g) => r + b + g;

            public static int Run(Color theColor) => ConvertRgbToHsv(theColor.R, {|#0:theColor.G|}, {|#1:theColor.B|});

            public static double Angle(Vector diff) => Math.Atan2({|#2:diff.X|}, {|#3:diff.Y|});
        }
        """,
        Swapped("'theColor.G' and 'theColor.B'", "'b' and 'g'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'diff.X' and 'diff.Y'", "'y' and 'x'"));

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
            public static void Run(Size size, long height, int width, int? maxHeight, int? maxWidth)
            {
                Api.Resize({|#0:(int)height|}, {|#1:-width|});
                Api.Resize({|#2:size?.Height ?? 0|}, {|#3:size?.Width ?? 0|});
                Api.Resize({|#4:maxHeight.Value|}, {|#5:maxWidth.GetValueOrDefault() + 1|});
            }
        }
        """,
        Swapped("'(int)height' and '-width'", "'width' and 'height'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'size?.Height ?? 0' and 'size?.Width ?? 0'", "'width' and 'height'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(4).WithLocation(5).WithArguments("'maxHeight.Value' and 'maxWidth.GetValueOrDefault() + 1'", "'width' and 'height'"));

    [Fact]
    public Task Swap_ExtensionReceiverIndexerAndConstructorInitializer() => Verify.VerifyAsync(
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

            public static void Copy(Buffer source, Buffer destination) => {|#0:destination|}.CopyInto({|#1:source|});

            public static int Read(Grid grid, int row, int column) => grid[{|#2:column|}, {|#3:row|}];
        }
        """,
        Swapped("'destination' and 'source'", "'source' and 'destination'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'column' and 'row'", "'row' and 'column'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(4).WithLocation(5).WithArguments("'height' and 'width'", "'width' and 'height'"));

    [Fact]
    public Task Swap_ShortenedAndMistypedNames() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Copy(byte[] source, byte[] destination) { }

            public static void Run(byte[] src, byte[] dst, int w, int h, int heigth, int widht)
            {
                Copy({|#0:dst|}, {|#1:src|});
                Api.Resize({|#2:h|}, {|#3:w|});
                Api.Resize({|#4:heigth|}, {|#5:widht|});
            }
        }
        """,
        Swapped("'dst' and 'src'", "'source' and 'destination'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(2).WithLocation(3).WithArguments("'h' and 'w'", "'width' and 'height'"),
        Verify.Diagnostic(Descriptors.SwappedArguments).WithLocation(4).WithLocation(5).WithArguments("'heigth' and 'widht'", "'width' and 'height'"));

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

    [Fact]
    public Task Namesake_UnusedLocal() => Verify.VerifyAsync(
        Prelude + """
        public static class C
        {
            public static void Run(Order order)
            {
                var customerId = order.CustomerId;
                var invoiceId = order.InvoiceId;
                var orderId = order.OrderId;
                Api.ProcessInvoice(customerId, {|#0:orderId|});
            }
        }
        """,
        Namesake("orderId", "invoiceId", "invoiceId"));

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
