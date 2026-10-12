using Microsoft.CodeAnalysis.Testing;
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
    public Task Runtime_SizeOfEnumVersusUnderlyingIsSilent() => Verify.VerifyAsync(
        """
        public enum Flags : ulong { None }

        public static class C
        {
            public static unsafe bool Same() => sizeof(Flags) == sizeof(ulong);
        }
        """);

    [Fact]
    public Task SizeOfSameTypeReports() => Verify.VerifyAsync(
        """
        public enum Flags : ulong { None }

        public static class C
        {
            public static unsafe bool Same() => {|#1:sizeof(Flags)|} == {|#0:sizeof(Flags)|};
        }
        """,
        Repeat("sizeof(Flags)", "sizeof(Flags)", "=="));

    [Fact]
    public Task Runtime_AwaitBetweenRepeatedReadsIsSilent() => Verify.VerifyAsync(
        """
        using System.Threading.Tasks;

        public static class C
        {
            public static async Task<bool> M(Task body, Task headers, bool duplex) =>
                body.IsCompleted
                || !duplex
                || await Task.WhenAny(body, headers).ConfigureAwait(false) == body
                || body.IsCompleted;
        }
        """);

    [Fact]
    public Task AwaitBetweenRepeatedFieldReadsIsSilent() => Verify.VerifyAsync(
        """
        using System.Threading.Tasks;

        public sealed class C
        {
            private bool _disposed;

            private Task<bool> FlushAsync() => Task.FromResult(false);

            public async Task<bool> M() => _disposed || await FlushAsync() || _disposed;
        }
        """);

    [Fact]
    public Task AwaitDoesNotHideRepeatedLocalRead() => Verify.VerifyAsync(
        """
        using System.Threading.Tasks;

        public static class C
        {
            public static async Task<bool> M(bool done, Task<bool> other) =>
                {|#1:done|} || await other || {|#0:done|};
        }
        """,
        Repeat("done", "done", "||"));

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

    [Fact]
    public Task RocketMan_SameFlagOnBothSidesOfOr() => Verify.VerifyAsync(
        """
        using System;

        [Flags]
        public enum ovrAvatarVisibilityFlags
        {
            FirstPerson = 1,
            ThirdPerson = 2,
        }

        public sealed class GameObject
        {
            public void SetActive(bool value)
            {
            }
        }

        public sealed class OvrAvatarRenderComponent
        {
            public GameObject gameObject = new();

            protected void UpdateActive(ovrAvatarVisibilityFlags mask)
            {
                bool showFirstPerson = (mask & ovrAvatarVisibilityFlags.FirstPerson) != 0;
                bool showThirdPerson = (mask & ovrAvatarVisibilityFlags.ThirdPerson) != 0;
                gameObject.SetActive({|#1:showThirdPerson|} || {|#0:showThirdPerson|});
            }
        }
        """,
        Repeat("showThirdPerson", "showThirdPerson", "||"));

    [Fact]
    public Task QuantConnect_SecurityTypeExcludedTwice() => Verify.VerifyAsync(
        """
        public enum SecurityType { Base, Equity, Option, Forex, Future, Cfd, Crypto, Index }

        public sealed class Security
        {
            public SecurityType Type { get; set; }
        }

        public static class ExanteBrokerageModel
        {
            public static bool CanSubmitOrder(Security security)
            {
                if (security.Type != SecurityType.Forex &&
                    security.Type != SecurityType.Equity &&
                    {|#1:security.Type != SecurityType.Index|} &&
                    security.Type != SecurityType.Option &&
                    security.Type != SecurityType.Future &&
                    security.Type != SecurityType.Cfd &&
                    security.Type != SecurityType.Crypto &&
                    {|#0:security.Type != SecurityType.Index|})
                {
                    return false;
                }

                return true;
            }
        }
        """,
        Repeat("security.Type != SecurityType.Index", "security.Type != SecurityType.Index", "&&"));

    [Fact]
    public Task ScottPlot_EqualsCallRepeated() => Verify.VerifyAsync(
        """
        using System;

        public class CoordinateRangeMutable : IEquatable<CoordinateRangeMutable>
        {
            public double Min { get; set; }
            public double Max { get; set; }

            public bool Equals(CoordinateRangeMutable? other)
            {
                if (other is null)
                {
                    return false;
                }

                return {|#1:Equals(Min, other.Min)|} && {|#0:Equals(Min, other.Min)|};
            }
        }
        """,
        Repeat("Equals(Min, other.Min)", "Equals(Min, other.Min)", "&&"));

    [Fact]
    public Task DotNet9_PointerFlagComparedWithItself() => Verify.VerifyAsync(
        """
        using System;

        public static class TypeBuilderImpl
        {
            private static bool MatchesTheFilter(Type[] argumentTypes, Type[] parameterTypes)
            {
                for (int i = 0; i < parameterTypes.Length; i++)
                {
                    Type argType = argumentTypes[i];
                    Type paramType = parameterTypes[i];

                    if (argType.IsArray != paramType.IsArray ||
                        argType.IsByRef != paramType.IsByRef ||
                        {|#1:argType.IsPointer|} != {|#0:argType.IsPointer|})
                    {
                        return false;
                    }
                }

                return true;
            }
        }
        """,
        Repeat("argType.IsPointer", "argType.IsPointer", "!="));

    [Fact]
    public Task ScreenToGif_SameMetadataFlagTwice() => Verify.VerifyAsync(
        """
        using System;

        [Flags]
        public enum FrameworkPropertyMetadataOptions
        {
            None = 0,
            AffectsMeasure = 1,
            AffectsArrange = 2,
        }

        public sealed class FrameworkPropertyMetadata
        {
            public FrameworkPropertyMetadata(object defaultValue, FrameworkPropertyMetadataOptions flags)
            {
            }
        }

        public sealed class DependencyProperty
        {
            public static DependencyProperty Register(string name, Type propertyType, Type ownerType, FrameworkPropertyMetadata metadata) => new();
        }

        public sealed class DynamicGrid
        {
            public static readonly DependencyProperty IsReversedProperty =
                DependencyProperty.Register(
                    "IsReversed",
                    typeof(bool),
                    typeof(DynamicGrid),
                    new FrameworkPropertyMetadata(
                        false,
                        {|#1:FrameworkPropertyMetadataOptions.AffectsMeasure|}
                        | {|#0:FrameworkPropertyMetadataOptions.AffectsMeasure|}));
        }
        """,
        Repeat("FrameworkPropertyMetadataOptions.AffectsMeasure", "FrameworkPropertyMetadataOptions.AffectsMeasure", "|"));

    [Fact]
    public Task Unity_MirroredComparisonRepeated() => Verify.VerifyAsync(
        """
        public sealed class FrameDebuggerEventDetailsView
        {
            private float m_RTBlackMinLevel;
            private float m_RTWhiteMaxLevel;

            public void DrawRenderTargetToolbar(float blackMinLevel, float whiteMaxLevel)
            {
                if ({|#1:blackMinLevel < whiteMaxLevel|} && {|#0:whiteMaxLevel > blackMinLevel|})
                {
                    m_RTBlackMinLevel = blackMinLevel;
                    m_RTWhiteMaxLevel = whiteMaxLevel;
                }
            }
        }
        """,
        Repeat("whiteMaxLevel > blackMinLevel", "blackMinLevel < whiteMaxLevel", "&&"));

    [Fact]
    public Task Unity_LightIntensityCheckedTwice() => Verify.VerifyAsync(
        """
        public sealed class Light
        {
            public float intensity;
        }

        public sealed class PreviewRenderUtility
        {
            public Light[] lights = new Light[2];
        }

        public sealed class AvatarPreview
        {
            private const float kDefaultIntensity = 1.4f;
            private readonly PreviewRenderUtility previewUtility = new();

            public void DoRenderPreview()
            {
                if ({|#1:previewUtility.lights[0].intensity != kDefaultIntensity|} ||
                    {|#0:previewUtility.lights[0].intensity != kDefaultIntensity|})
                {
                    SetupPreviewLightingAndFx();
                }
            }

            private void SetupPreviewLightingAndFx()
            {
                previewUtility.lights[0].intensity = kDefaultIntensity;
                previewUtility.lights[1].intensity = kDefaultIntensity;
            }
        }
        """,
        Repeat("previewUtility.lights[0].intensity != kDefaultIntensity", "previewUtility.lights[0].intensity != kDefaultIntensity", "||"));

    [Fact]
    public Task Unity_ParenthesizedDisjunctionRepeated() => Verify.VerifyAsync(
        """
        public enum StyleValueType { Keyword, Float, Dimension, Color, Enum }

        public struct StyleValueHandle
        {
            public StyleValueType valueType;
        }

        public struct StylePropertyValue
        {
            public StyleValueHandle handle;
        }

        public static class StyleSheetApplicator
        {
            public static int CompileBackgroundPosition(StylePropertyValue val1, StylePropertyValue val2, int valCount)
            {
                if (valCount == 2)
                {
                    if (({|#1:(val1.handle.valueType == StyleValueType.Dimension) || (val1.handle.valueType == StyleValueType.Float)|}) &&
                        ({|#0:(val1.handle.valueType == StyleValueType.Dimension) || (val1.handle.valueType == StyleValueType.Float)|}))
                    {
                        return 2;
                    }
                }

                return 0;
            }
        }
        """,
        Repeat(
            "(val1.handle.valueType == StyleValueType.Dimension) || (val1.handle.valueType == StyleValueType.Float)",
            "(val1.handle.valueType == StyleValueType.Dimension) || (val1.handle.valueType == StyleValueType.Float)",
            "&&"));

    [Fact]
    public Task WolvenKit_ContainsCalledTwiceOnPatternVariable() => Verify.VerifyAsync(
        """
        using System.IO;
        using System.Linq;

        public static class Oodle
        {
            private const string gameName = "Cyberpunk 2077";
            private const string exeName = "Cyberpunk2077.exe";

            public static string TryGetGameInstallDir(object programName, object installLocation)
            {
                string exePath = null;
                if (programName?.ToString() is string n && installLocation?.ToString() is string l)
                {
                    if ({|#1:n.Contains(gameName)|} || {|#0:n.Contains(gameName)|})
                    {
                        exePath = Directory.GetFiles(l, exeName, SearchOption.AllDirectories).First();
                    }
                }

                return exePath;
            }
        }
        """,
        Repeat("n.Contains(gameName)", "n.Contains(gameName)", "||"));

    [Fact]
    public Task DiIiS_SceneIdListedTwice() => Verify.VerifyAsync(
        """
        public sealed class SNOHandle
        {
            public int Id { get; set; }
        }

        public sealed class MapRevealSceneMessage
        {
            public bool MiniMapVisibility { get; set; }
        }

        public sealed class Scene
        {
            public SNOHandle SceneSNO { get; } = new();

            public MapRevealSceneMessage MapRevealMessage()
            {
                if (SceneSNO.Id == 183556 ||
                    {|#1:SceneSNO.Id == 183557|} ||
                    SceneSNO.Id == 183502 ||
                    SceneSNO.Id == 183505 ||
                    {|#0:SceneSNO.Id == 183557|} ||
                    SceneSNO.Id == 183519)
                {
                    return new MapRevealSceneMessage { MiniMapVisibility = true };
                }
                else
                {
                    return new MapRevealSceneMessage { MiniMapVisibility = false };
                }
            }
        }
        """,
        Repeat("SceneSNO.Id == 183557", "SceneSNO.Id == 183557", "||"));

    [Fact]
    public Task DotNet8_PointerSizeCheckedTwiceAroundCall() => Verify.VerifyAsync(
        """
        public readonly struct LayoutInt
        {
            public bool IsIndeterminate { get; }
            public int AsInt { get; }
        }

        public sealed class TargetDetails
        {
            public int PointerSize { get; }

            public LayoutInt GetObjectAlignment(LayoutInt fieldAlignment) => fieldAlignment;
        }

        public sealed class TypeSystemContext
        {
            public TargetDetails Target { get; } = new();
        }

        public static class MetadataFieldLayoutAlgorithm
        {
            public static bool RequiresAlign8(TypeSystemContext context, LayoutInt largestAlignmentRequired)
            {
                bool requiresAlign8
                    = !largestAlignmentRequired.IsIndeterminate
                    && {|#1:context.Target.PointerSize == 4|}
                    && context.Target.GetObjectAlignment(largestAlignmentRequired).AsInt > 4
                    && {|#0:context.Target.PointerSize == 4|};
                return requiresAlign8;
            }
        }
        """,
        Repeat("context.Target.PointerSize == 4", "context.Target.PointerSize == 4", "&&"));

    [Fact]
    public Task DotNet8_NamespaceComparedWithItself() => Verify.VerifyAsync(
        """
        public sealed class TypeReference
        {
            public string Name { get; set; }
            public string Namespace { get; set; }
        }

        public static class TypeReferenceExtensions
        {
            public static bool IsTypeOf<T>(this TypeReference tr)
            {
                var type = typeof(T);
                return tr.Name == type.Name && {|#1:tr.Namespace|} == {|#0:tr.Namespace|};
            }
        }
        """,
        Repeat("tr.Namespace", "tr.Namespace", "=="));

    [Fact]
    public Task EfCore_ConvertNodeTypeTwiceInWhenClause() => Verify.VerifyAsync(
        """
        using System;
        using System.Linq.Expressions;

        public static class NorthwindStringIncludeQueryTestBase
        {
            public static string GetPath(Expression expression)
            {
                switch (expression)
                {
                    case MemberExpression memberExpression:
                        return memberExpression.Member.Name;

                    case UnaryExpression unaryExpression
                        when {|#1:unaryExpression.NodeType == ExpressionType.Convert|}
                            || {|#0:unaryExpression.NodeType == ExpressionType.Convert|}
                            || unaryExpression.NodeType == ExpressionType.TypeAs:
                        return GetPath(unaryExpression.Operand);

                    default:
                        throw new InvalidOperationException("Invalid include path.");
                }
            }
        }
        """,
        Repeat("unaryExpression.NodeType == ExpressionType.Convert", "unaryExpression.NodeType == ExpressionType.Convert", "||"));

    [Fact]
    public Task MassTransit_NegatedDeliveryCheckTwice() => Verify.VerifyAsync(
        """
        using System.Threading.Tasks;

        public interface ReceiveContext
        {
            bool IsDelivered { get; }
        }

        public interface OutboxConsumeContext
        {
            ReceiveContext ReceiveContext { get; }

            Task NotifyConsumed();
        }

        public sealed class OutboxMessagePipe
        {
            public async Task Send(OutboxConsumeContext context)
            {
                if ({|#1:!context.ReceiveContext.IsDelivered|}
                    && {|#0:!context.ReceiveContext.IsDelivered|})
                {
                    await context.NotifyConsumed().ConfigureAwait(false);
                }
            }
        }
        """,
        Repeat("!context.ReceiveContext.IsDelivered", "!context.ReceiveContext.IsDelivered", "&&"));

    [Fact]
    public Task BtcPay_EventNameListedTwice() => Verify.VerifyAsync(
        """
        using System.Threading.Tasks;

        public class InvoiceEvent
        {
            public const string MarkedCompleted = "invoice_markedComplete";
            public const string MarkedInvalid = "invoice_markedInvalid";
            public const string Expired = "invoice_expired";
            public const string ExpiredPaidPartial = "invoice_expiredPaidPartial";
            public const string PaidInFull = "invoice_paidInFull";
            public const string FailedToConfirm = "invoice_failedToConfirm";
            public const string Confirmed = "invoice_confirmed";
            public const string Completed = "invoice_completed";

            public string Name { get; set; }
        }

        public sealed class BitpayIPNSender
        {
            private static Task Notify(InvoiceEvent e, bool sendMail) => Task.CompletedTask;

            public async Task Handle(InvoiceEvent e, bool fullNotifications)
            {
                var sendMail = true;
                if (fullNotifications)
                {
                    if (e.Name == InvoiceEvent.Expired ||
                        e.Name == InvoiceEvent.PaidInFull ||
                        {|#1:e.Name == InvoiceEvent.FailedToConfirm|} ||
                        e.Name == InvoiceEvent.MarkedInvalid ||
                        e.Name == InvoiceEvent.MarkedCompleted ||
                        {|#0:e.Name == InvoiceEvent.FailedToConfirm|} ||
                        e.Name == InvoiceEvent.Completed ||
                        e.Name == InvoiceEvent.ExpiredPaidPartial)
                    {
                        await Notify(e, sendMail);
                        sendMail = false;
                    }
                }

                if (e.Name == InvoiceEvent.Confirmed)
                {
                    await Notify(e, sendMail);
                }
            }
        }
        """,
        Repeat("e.Name == InvoiceEvent.FailedToConfirm", "e.Name == InvoiceEvent.FailedToConfirm", "||"));

    [Fact]
    public Task AkkaNet_EqualsComparesSettingTwice() => Verify.VerifyAsync(
        """
        public sealed class SubscriptionTimeoutSettings
        {
        }

        public sealed class StreamRefSettings
        {
        }

        public sealed class ActorMaterializerSettings
        {
            public int InitialInputBufferSize { get; }
            public int MaxInputBufferSize { get; }
            public string Dispatcher { get; }
            public SubscriptionTimeoutSettings SubscriptionTimeoutSettings { get; }
            public bool IsDebugLogging { get; }
            public int OutputBurstLimit { get; }
            public StreamRefSettings StreamRefSettings { get; }

            public override bool Equals(object obj)
            {
                if (!(obj is ActorMaterializerSettings s))
                {
                    return false;
                }

                return
                    s.InitialInputBufferSize == InitialInputBufferSize &&
                    s.MaxInputBufferSize == MaxInputBufferSize &&
                    s.Dispatcher == Dispatcher &&
                    {|#1:s.SubscriptionTimeoutSettings == SubscriptionTimeoutSettings|} &&
                    s.IsDebugLogging == IsDebugLogging &&
                    s.OutputBurstLimit == OutputBurstLimit &&
                    {|#0:s.SubscriptionTimeoutSettings == SubscriptionTimeoutSettings|} &&
                    s.StreamRefSettings == StreamRefSettings;
            }

            public override int GetHashCode() => InitialInputBufferSize;
        }
        """,
        Repeat("s.SubscriptionTimeoutSettings == SubscriptionTimeoutSettings", "s.SubscriptionTimeoutSettings == SubscriptionTimeoutSettings", "&&"));

    [Fact]
    public Task AvalonStudio_WidthCheckedTwiceInsideNegation() => Verify.VerifyAsync(
        """
        public sealed class Frame
        {
            public int Width { get; set; }
            public int Height { get; set; }
        }

        public class DrawingContext
        {
        }

        public class Control
        {
            public virtual void Render(DrawingContext context)
            {
            }
        }

        public sealed class RemoteWidget : Control
        {
            private Frame _lastFrame;

            private static void DrawFrame(DrawingContext context, Frame frame)
            {
            }

            public override void Render(DrawingContext context)
            {
                if (_lastFrame != null && !({|#1:_lastFrame.Width == 0|}
                                            || {|#0:_lastFrame.Width == 0|}))
                {
                    DrawFrame(context, _lastFrame);
                }

                base.Render(context);
            }
        }
        """,
        Repeat("_lastFrame.Width == 0", "_lastFrame.Width == 0", "||"));

    [Fact]
    public Task Barotrauma_IsNullOrEmptyTwice() => Verify.VerifyAsync(
        """
        public sealed class BannedPlayer
        {
            public string EndPoint { get; set; }

            public bool CompareTo(string endpointCompare)
            {
                if ({|#1:string.IsNullOrEmpty(EndPoint)|} || {|#0:string.IsNullOrEmpty(EndPoint)|})
                {
                    return false;
                }

                return EndPoint == endpointCompare;
            }
        }
        """,
        Repeat("string.IsNullOrEmpty(EndPoint)", "string.IsNullOrEmpty(EndPoint)", "||"));

    [Fact]
    public Task Squidex_WidthCheckedInsteadOfHeight() => Verify.VerifyAsync(
        """
        public sealed class Properties
        {
            public int PhotoWidth { get; set; }
            public int PhotoHeight { get; set; }
        }

        public sealed class AssetMetadata
        {
            public void SetPixelWidth(int value)
            {
            }

            public void SetPixelHeight(int value)
            {
            }
        }

        public static class FileTagAssetMetadataSource
        {
            public static void Enhance(Properties properties, AssetMetadata metadata)
            {
                var pw = properties.PhotoWidth;
                var ph = properties.PhotoHeight;

                if ({|#1:pw > 0|} && {|#0:pw > 0|})
                {
                    metadata.SetPixelWidth(pw);
                    metadata.SetPixelHeight(ph);
                }
            }
        }
        """,
        Repeat("pw > 0", "pw > 0", "&&"));

    [Fact]
    public Task EShopOnContainers_HostComparedWithItself() => Verify.VerifyAsync(
        """
        using System;

        public sealed class GrantUrlTesterService
        {
            public bool CheckSameOrigin(string urlHook, string url)
            {
                var firstUrl = new Uri(urlHook, UriKind.Absolute);
                var secondUrl = new Uri(url, UriKind.Absolute);

                return firstUrl.Scheme == secondUrl.Scheme &&
                       firstUrl.Port == secondUrl.Port &&
                       {|#1:firstUrl.Host|} == {|#0:firstUrl.Host|};
            }
        }
        """,
        Repeat("firstUrl.Host", "firstUrl.Host", "=="));

    [Fact]
    public Task DotNetNuke_ExcludedPropertyNamesRepeated() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;
        using System.Linq;
        using System.Reflection;

        public sealed class EditorConfig
        {
        }

        public static class SettingsUtil
        {
            internal static IEnumerable<PropertyInfo> GetEditorConfigProperties()
            {
                return
                    typeof(EditorConfig).GetProperties()
                        .Where(
                            info => !info.Name.Equals("Magicline_KeystrokeNext")
                                 && !info.Name.Equals("Magicline_KeystrokePrevious")
                                 && !info.Name.Equals("Plugins")
                                 && !info.Name.Equals("Codemirror_Theme")
                                 && !info.Name.Equals("Width")
                                 && !info.Name.Equals("Height")
                                 && !info.Name.Equals("ContentsCss")
                                 && {|#1:!info.Name.Equals("Templates_Files")|}
                                 && !info.Name.Equals("CustomConfig")
                                 && !info.Name.Equals("Skin")
                                 && {|#0:!info.Name.Equals("Templates_Files")|}
                                 && !info.Name.Equals("Toolbar")
                                 && !info.Name.Equals("Language")
                                 && {|#3:!info.Name.Equals("FileBrowserWindowWidth")|}
                                 && {|#5:!info.Name.Equals("FileBrowserWindowHeight")|}
                                 && {|#2:!info.Name.Equals("FileBrowserWindowWidth")|}
                                 && {|#4:!info.Name.Equals("FileBrowserWindowHeight")|}
                                 && !info.Name.Equals("FileBrowserUploadUrl")
                                 && !info.Name.Equals("FileBrowserImageUploadUrl")
                                 && !info.Name.Equals("FilebrowserImageBrowseLinkUrl")
                                 && !info.Name.Equals("FileBrowserImageBrowseUrl")
                                 && !info.Name.Equals("FileBrowserFlashUploadUrl")
                                 && !info.Name.Equals("FileBrowserFlashBrowseUrl")
                                 && !info.Name.Equals("FileBrowserBrowseUrl")
                                 && !info.Name.Equals("DefaultLinkProtocol"));
            }
        }
        """,
        Repeat("!info.Name.Equals(\"Templates_Files\")", "!info.Name.Equals(\"Templates_Files\")", "&&"),
        Repeat("!info.Name.Equals(\"FileBrowserWindowWidth\")", "!info.Name.Equals(\"FileBrowserWindowWidth\")", "&&", duplicateMarker: 2, originalMarker: 3),
        Repeat("!info.Name.Equals(\"FileBrowserWindowHeight\")", "!info.Name.Equals(\"FileBrowserWindowHeight\")", "&&", duplicateMarker: 4, originalMarker: 5));

    [Fact]
    public Task LinqToDb_TupleArityListedTwice() => Verify.VerifyAsync(
        """
        using System;

        public static class TypeExtensions
        {
            public static bool IsTupleType(this Type type)
            {
                if (type.IsGenericType)
                {
                    var genericDefinition = type.GetGenericTypeDefinition();
                    if (genericDefinition == typeof(Tuple<>)
                        || genericDefinition == typeof(Tuple<,>)
                        || genericDefinition == typeof(Tuple<,,>)
                        || genericDefinition == typeof(Tuple<,,,>)
                        || genericDefinition == typeof(Tuple<,,,,>)
                        || genericDefinition == typeof(Tuple<,,,,,>)
                        || genericDefinition == typeof(Tuple<,,,,,,>)
                        || {|#1:genericDefinition == typeof(Tuple<,,,,,,,>)|}
                        || {|#0:genericDefinition == typeof(Tuple<,,,,,,,>)|})
                    {
                        return true;
                    }
                }

                return false;
            }
        }
        """,
        Repeat("genericDefinition == typeof(Tuple<,,,,,,,>)", "genericDefinition == typeof(Tuple<,,,,,,,>)", "||"));

    [Fact]
    public Task Emby_CharacterExcludedTwice() => Verify.VerifyAsync(
        """
        public sealed class HttpListenerRequest
        {
            internal bool SetRequestLine(string req)
            {
                foreach (var c in req)
                {
                    int ic = (int)c;
                    if ((ic >= 'A' && ic <= 'Z') ||
                        (ic > 32 && c < 127 && c != '(' && c != ')' && {|#1:c != '<'|} &&
                        {|#0:c != '<'|} && c != '>' && c != '@' && c != ',' && c != ';' &&
                        c != ':' && c != '\\' && c != '"' && c != '/' && c != '[' &&
                        c != ']' && c != '?' && c != '=' && c != '{' && c != '}'))
                    {
                        continue;
                    }

                    return false;
                }

                return true;
            }
        }
        """,
        Repeat("c != '<'", "c != '<'", "&&"));

    [Fact]
    public Task Osu_HitResultCheckedTwiceInLambda() => Verify.VerifyAsync(
        """
        using System;

        public enum HitResult { None, Miss, Meh, Ok, Good, Great, Perfect }

        public sealed class JudgementResult
        {
            public HitResult Type { get; set; }
        }

        public sealed class HoldNote
        {
            public bool hasBroken;
        }

        public sealed class DrawableHoldNote
        {
            private readonly HoldNote holdNote = new();

            private static void ApplyResult(Action<JudgementResult> application) => application(new JudgementResult());

            public void CheckForResult(HitResult result)
            {
                ApplyResult(r =>
                {
                    if (holdNote.hasBroken
                        && ({|#1:result == HitResult.Perfect|} || {|#0:result == HitResult.Perfect|}))
                    {
                        result = HitResult.Good;
                    }

                    r.Type = result;
                });
            }
        }
        """,
        Repeat("result == HitResult.Perfect", "result == HitResult.Perfect", "||"));

    [Fact]
    public Task AvaloniaUi_FlagsXoredWithThemselves() => Verify.VerifyAsync(
        """
        using System;

        [Flags]
        public enum WindowStyles : uint
        {
            WS_OVERLAPPED = 0x00000000,
            WS_CAPTION = 0x00C00000,
            WS_THICKFRAME = 0x00040000,
        }

        public sealed class WindowImpl
        {
            private WindowStyles _style;

            public void UpdateWMStyles(Action change)
            {
                var style = _style;
                const WindowStyles controlledFlags = WindowStyles.WS_OVERLAPPED;
                change();
                style = style | {|#1:controlledFlags|} ^ {|#0:controlledFlags|};
                _style = style;
            }
        }
        """,
        Repeat("controlledFlags", "controlledFlags", "^"));

    [Fact]
    public Task AzureSdk_LengthComparedWithItself() => Verify.VerifyAsync(
        """
        using System;
        using System.Buffers;
        using System.IO;
        using System.Threading;
        using System.Threading.Tasks;

        public static class AzureBaseBuffersExtensions
        {
            public static async Task WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default)
            {
                byte[] array = null;
                try
                {
                    if (array == null || {|#1:buffer.Length|} < {|#0:buffer.Length|})
                    {
                        array = ArrayPool<byte>.Shared.Rent(buffer.Length);
                    }

                    buffer.CopyTo(array);
                    await stream.WriteAsync(array, 0, buffer.Length, cancellation).ConfigureAwait(false);
                }
                finally
                {
                    if (array != null)
                    {
                        ArrayPool<byte>.Shared.Return(array);
                    }
                }
            }
        }
        """,
        Repeat("buffer.Length", "buffer.Length", "<"));

    [Fact]
    public Task RoslynAnalyzers_AssertComparesPropertyWithItself() => Verify.VerifyAsync(
        """
        using System.Diagnostics;

        public abstract class AnalysisEntityBasedPredicateAnalysisData
        {
            protected AnalysisEntityBasedPredicateAnalysisData(AnalysisEntityBasedPredicateAnalysisData data1, AnalysisEntityBasedPredicateAnalysisData data2)
            {
                Debug.Assert({|#1:data1.IsReachableBlockData|} == {|#0:data1.IsReachableBlockData|});
            }

            public bool IsReachableBlockData { get; }
        }
        """,
        Repeat("data1.IsReachableBlockData", "data1.IsReachableBlockData", "=="));

    [Fact]
    public Task CoreFx_CharTypeFlagRepeatedInTable() => Verify.VerifyAsync(
        """
        public static class XmlUTF8TextReader
        {
            private static class CharType
            {
                public const byte None = 0x00;
                public const byte FirstName = 0x01;
                public const byte Name = 0x02;
                public const byte Whitespace = 0x04;
                public const byte Text = 0x08;
                public const byte AttributeText = 0x10;
                public const byte SpecialWhitespace = 0x20;
                public const byte Comment = 0x40;
            }

            private static readonly byte[] s_charType = new byte[]
            {
                CharType.None |
                {|#1:CharType.Comment|} |
                {|#0:CharType.Comment|} |
                CharType.Whitespace |
                CharType.Text |
                CharType.SpecialWhitespace,
                CharType.None |
                {|#3:CharType.Comment|} |
                {|#2:CharType.Comment|} |
                CharType.Whitespace,
            };
        }
        """,
        Repeat("CharType.Comment", "CharType.Comment", "|"),
        Repeat("CharType.Comment", "CharType.Comment", "|", duplicateMarker: 2, originalMarker: 3));

    [Fact]
    public Task InferNet_BindingFlagRepeated() => Verify.VerifyAsync(
        """
        using System.Reflection;

        public static class CodeBuilder
        {
            internal static BindingFlags ToMethodInternal(bool isStatic)
            {
                BindingFlags bf = BindingFlags.Default;
                if (isStatic)
                {
                    bf |= BindingFlags.Static;
                }

                bf |= {|#1:BindingFlags.Public|}
                      | BindingFlags.NonPublic
                      | {|#0:BindingFlags.Public|}
                      | BindingFlags.Instance;
                return bf;
            }
        }
        """,
        Repeat("BindingFlags.Public", "BindingFlags.Public", "|"));

    [Fact]
    public Task InferNet_IsInfinityCheckedOnFirstTransitionTwice() => Verify.VerifyAsync(
        """
        public readonly struct Weight
        {
            public double Value { get; }
        }

        public sealed class Transition
        {
            public bool IsEpsilon { get; }
            public Weight Weight { get; }
        }

        public static class Automaton
        {
            public static double MergeParallelTransitions(Transition transition1, Transition transition2)
            {
                if (!transition1.IsEpsilon && !transition2.IsEpsilon)
                {
                    if ({|#1:double.IsInfinity(transition1.Weight.Value)|} &&
                        {|#0:double.IsInfinity(transition1.Weight.Value)|})
                    {
                        return 1.0;
                    }

                    return transition1.Weight.Value + transition2.Weight.Value;
                }

                return 0;
            }
        }
        """,
        Repeat("double.IsInfinity(transition1.Weight.Value)", "double.IsInfinity(transition1.Weight.Value)", "&&"));

    [Fact]
    public Task PascalAbc_TokenKindCheckedTwice() => Verify.VerifyAsync(
        """
        public sealed class Token
        {
            public int Kind { get; set; }
        }

        public sealed class NRefactoryInsightWindowHandler
        {
            private readonly int eofToken, commaToken, openParensToken, closeParensToken, openBracketToken, closeBracketToken, openBracesToken, closeBracesToken, statementEndToken;
            private int bracketCount;

            public void InitializeOpenedInsightWindow(Token token)
            {
                if (token.Kind == openParensToken ||
                    {|#1:token.Kind == openBracketToken|} ||
                    {|#0:token.Kind == openBracketToken|})
                {
                    bracketCount++;
                }
            }
        }
        """,
        Repeat("token.Kind == openBracketToken", "token.Kind == openBracketToken", "||"));

    [Fact]
    public Task MediaPortal_WidthCheckedInsteadOfHeight() => Verify.VerifyAsync(
        """
        using System;
        using System.Drawing;

        public struct Message
        {
            public IntPtr WParam { get; set; }
            public IntPtr LParam { get; set; }
        }

        public sealed class MainForm
        {
            private int _screenBpp;
            private Size _screenSize;

            private void ApplyScreenChange()
            {
            }

            public void WndProc(ref Message m)
            {
                int bitDepth = m.WParam.ToInt32();
                int screenWidth = m.LParam.ToInt32() & 0xFFFF;
                int screenHeight = m.LParam.ToInt32() >> 16;
                if (bitDepth != _screenBpp ||
                    {|#1:screenWidth != _screenSize.Width|} ||
                    {|#0:screenWidth != _screenSize.Width|})
                {
                    ApplyScreenChange();
                }
            }
        }
        """,
        Repeat("screenWidth != _screenSize.Width", "screenWidth != _screenSize.Width", "||"));

    [Fact]
    public Task PowerShell_IndexerCheckedTwice() => Verify.VerifyAsync(
        """
        public sealed class PSPropertyInfo
        {
        }

        public sealed class PSMemberInfoCollection
        {
            public PSPropertyInfo this[string name] => null;
        }

        public sealed class PSObject
        {
            public PSMemberInfoCollection Properties { get; } = new();
        }

        public sealed class RemoteDataObject
        {
            public PSObject Data { get; } = new();
        }

        internal static class RemoteDataNameStrings
        {
            internal const string MinRunspaces = "MinRunspaces";
            internal const string MaxRunspaces = "MaxRunspaces";
        }

        public sealed class ServerRemoteSession
        {
            private bool clientRequestedRunspaceCount;

            internal void ExecuteConnect(RemoteDataObject connectRunspacePoolObject)
            {
                if
                (
                    {|#1:connectRunspacePoolObject.Data.Properties[RemoteDataNameStrings.MinRunspaces] != null|}
                    &&
                    {|#0:connectRunspacePoolObject.Data.Properties[RemoteDataNameStrings.MinRunspaces] != null|}
                )
                {
                    clientRequestedRunspaceCount = true;
                }
            }
        }
        """,
        Repeat(
            "connectRunspacePoolObject.Data.Properties[RemoteDataNameStrings.MinRunspaces] != null",
            "connectRunspacePoolObject.Data.Properties[RemoteDataNameStrings.MinRunspaces] != null",
            "&&"));

    [Fact]
    public Task PowerShell_NullCheckRepeated() => Verify.VerifyAsync(
        """
        using System;

        public sealed class ImportModuleCommand
        {
            internal Version BaseMinimumVersion { get; set; }
            internal Version BaseMaximumVersion { get; set; }

            public void ProcessRecord()
            {
                if ({|#1:BaseMaximumVersion != null|} &&
                    {|#0:BaseMaximumVersion != null|} &&
                    BaseMaximumVersion < BaseMinimumVersion)
                {
                    throw new ArgumentOutOfRangeException(nameof(BaseMaximumVersion));
                }
            }
        }
        """,
        Repeat("BaseMaximumVersion != null", "BaseMaximumVersion != null", "&&"));

    [Fact]
    public Task Mono_ColorComparedWithItself() => Verify.VerifyAsync(
        """
        using System.Drawing;

        public sealed class DataGridTableStyle
        {
            public Color LinkHoverColor { get; set; }
        }

        public class DataGrid
        {
            private readonly DataGridTableStyle grid_style = new();

            protected virtual bool ShouldSerializeLinkHoverColor()
            {
                return {|#1:grid_style.LinkHoverColor|} != {|#0:grid_style.LinkHoverColor|};
            }
        }
        """,
        Repeat("grid_style.LinkHoverColor", "grid_style.LinkHoverColor", "!="));

    [Fact]
    public Task Mono_IndexSubtractsItself() => Verify.VerifyAsync(
        """
        public sealed class ParameterData
        {
            public bool HasDefaultValue { get; set; }
        }

        public sealed class AParametersCollection
        {
            public ParameterData[] FixedParameters { get; set; }
            public bool HasParams { get; set; }
        }

        public static class OverloadResolver
        {
            public static bool BetterFunction(AParametersCollection candidate_pd, AParametersCollection best_pd, bool candidate_params, int j)
            {
                if (!candidate_params &&
                    !candidate_pd.FixedParameters[{|#1:j|} - {|#0:j|}].HasDefaultValue)
                {
                    return true;
                }

                if (!candidate_pd.FixedParameters[j - 1].HasDefaultValue &&
                    best_pd.FixedParameters[j - 1].HasDefaultValue)
                {
                    return true;
                }

                return candidate_pd.FixedParameters[j - 1].HasDefaultValue && best_pd.HasParams;
            }
        }
        """,
        Repeat("j", "j", "-"));

    [Fact]
    public Task XamarinForms_LeftCheckedInsteadOfBottom() => Verify.VerifyAsync(
        """
        public struct Thickness
        {
            public double Left { get; set; }
            public double Top { get; set; }
            public double Right { get; set; }
            public double Bottom { get; set; }

            internal bool IsDefault
            {
                get
                {
                    return {|#1:Left == 0|} && Top == 0 &&
                           Right == 0 && {|#0:Left == 0|};
                }
            }
        }
        """,
        Repeat("Left == 0", "Left == 0", "&&"));

    [Fact]
    public Task XamarinForms_ConstantFlagOrredWithItself() => Verify.VerifyAsync(
        """
        using System.Threading;

        public sealed class SplitOrderedList
        {
            private const int RwWait = 1;
            private const int RwWrite = 2;
            private const int RwRead = 4;

            private int _rwlock;

            public void EnterReadLock()
            {
                if ((Interlocked.Add(ref _rwlock, RwRead) &
                    ({|#1:RwWait|} | {|#0:RwWait|})) == 0)
                {
                    return;
                }

                Interlocked.Add(ref _rwlock, -RwRead);
            }
        }
        """,
        Repeat("RwWait", "RwWait", "|"));

    [Fact]
    public Task Xenko_KeyNullCheckRepeated() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;

        public sealed class MultiValueSortedDictionary<TKey, TValue>
        {
            public bool Remove(KeyValuePair<TKey, TValue> item)
            {
                if ({|#1:item.Key == null|} ||
                    {|#0:item.Key == null|})
                {
                    throw new ArgumentException();
                }

                return false;
            }
        }
        """,
        Repeat("item.Key == null", "item.Key == null", "||"));

    [Fact]
    public Task Xenko_DeeplyNestedConjunctRepeated() => Verify.VerifyAsync(
        """
        public struct Vector3
        {
            public float X;
            public float Y;
            public float Z;
        }

        public struct BoundingBox
        {
            public Vector3 Minimum;
            public Vector3 Maximum;
        }

        public struct BoundingSphere
        {
            public Vector3 Center;
            public float Radius;
        }

        public enum ContainmentType { Disjoint, Contains, Intersects }

        public static class Collision
        {
            public static ContainmentType BoxContainsSphere(ref BoundingBox box, ref BoundingSphere sphere)
            {
                if ((((box.Minimum.X + sphere.Radius <= sphere.Center.X) &&
                      (sphere.Center.X <= box.Maximum.X - sphere.Radius)) &&
                     (({|#1:box.Maximum.X - box.Minimum.X > sphere.Radius|}) &&
                     (box.Minimum.Y + sphere.Radius <= sphere.Center.Y))) &&
                    (((sphere.Center.Y <= box.Maximum.Y - sphere.Radius) &&
                      (box.Maximum.Y - box.Minimum.Y > sphere.Radius)) &&
                    (((box.Minimum.Z + sphere.Radius <= sphere.Center.Z) &&
                    (sphere.Center.Z <= box.Maximum.Z - sphere.Radius)) &&
                      ({|#0:box.Maximum.X - box.Minimum.X > sphere.Radius|}))))
                {
                    return ContainmentType.Contains;
                }

                return ContainmentType.Intersects;
            }
        }
        """,
        Repeat("box.Maximum.X - box.Minimum.X > sphere.Radius", "box.Maximum.X - box.Minimum.X > sphere.Radius", "&&"));

    [Fact]
    public Task Infragistics_LatitudeNaNCheckedTwice() => Verify.VerifyAsync(
        """
        using System;

        public sealed class Weather
        {
            public static readonly DateTime DateTimeInitial = DateTime.MinValue;

            public DateTime DateTime { get; set; }
        }

        public sealed class WeatherStation
        {
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public Weather Weather { get; set; }

            public bool IsValid
            {
                get
                {
                    var valid =
                        {|#1:double.IsNaN(Latitude)|} || {|#0:double.IsNaN(Latitude)|} ||
                        this.Weather.DateTime == Weather.DateTimeInitial;
                    return valid;
                }
            }
        }
        """,
        Repeat("double.IsNaN(Latitude)", "double.IsNaN(Latitude)", "||"));

    [Fact]
    public Task IronPython_TypeTestRepeated() => Verify.VerifyAsync(
        """
        public sealed class PythonDictionary
        {
        }

        public static class ObjectOps
        {
            public static object ReduceProtocol2(object self)
            {
                object dictIterator = null;
                if ({|#1:self is PythonDictionary|} || {|#0:self is PythonDictionary|})
                {
                    dictIterator = self;
                }

                return dictIterator;
            }
        }
        """,
        Repeat("self is PythonDictionary", "self is PythonDictionary", "||"));

    [Fact]
    public Task MonoDevelop_MembersComparedWithThemselves() => Verify.VerifyAsync(
        """
        public enum Accessibility { NotApplicable, Private, Public }

        public enum SymbolKind { Method, Property }

        public interface ISymbol
        {
            SymbolKind Kind { get; }
            Accessibility DeclaredAccessibility { get; }
            bool IsStatic { get; }
        }

        public sealed class AbstractImplementInterfaceService
        {
            public bool MembersMatch(ISymbol member1, ISymbol member2)
            {
                if (member1.Kind != member2.Kind)
                {
                    return false;
                }

                if ({|#1:member1.DeclaredAccessibility|} != {|#0:member1.DeclaredAccessibility|}
                    || {|#3:member1.IsStatic|} != {|#2:member1.IsStatic|})
                {
                    return false;
                }

                return true;
            }
        }
        """,
        Repeat("member1.DeclaredAccessibility", "member1.DeclaredAccessibility", "!="),
        Repeat("member1.IsStatic", "member1.IsStatic", "!=", duplicateMarker: 2, originalMarker: 3));

    [Fact]
    public Task MonoDevelop_EditorNullCheckRepeatedInHandler() => Verify.VerifyAsync(
        """
        using System;

        public sealed class TextEditor
        {
        }

        public sealed class Document
        {
            public TextEditor Editor { get; set; }

            public event EventHandler Closed;
        }

        public sealed class RazorCSharpParser
        {
            public void TryAddDocument(Document guiDoc)
            {
                if (guiDoc != null && guiDoc.Editor != null)
                {
                    guiDoc.Closed += (sender, args) =>
                    {
                        var doc = sender as Document;
                        if ({|#1:doc.Editor != null|} && {|#0:doc.Editor != null|})
                        {
                            doc.Editor = null;
                        }
                    };
                }
            }
        }
        """,
        Repeat("doc.Editor != null", "doc.Editor != null", "&&"));

    [Fact]
    public Task Umbraco_FocalPointTopCheckedTwice() => Verify.VerifyAsync(
        """
        public class ImageCropFocalPoint
        {
            public decimal Left { get; set; }
            public decimal Top { get; set; }
        }

        public class ImageCropDataSet
        {
            public ImageCropFocalPoint FocalPoint { get; set; }

            public bool HasFocalPoint()
            {
                return FocalPoint != null &&
                       {|#1:FocalPoint.Top != 0.5m|} && {|#0:FocalPoint.Top != 0.5m|};
            }
        }
        """,
        Repeat("FocalPoint.Top != 0.5m", "FocalPoint.Top != 0.5m", "&&"));

    [Fact]
    public Task Flax_MouseButtonFlagAndedWithItself() => Verify.VerifyAsync(
        """
        public enum MouseButton { Left, Middle, Right }

        public struct Float2
        {
            public float X;
            public float Y;
        }

        public class VisjectSurface
        {
            private bool _rightMouseDown;
            private bool _middleMouseDown;

            private void StartMouseCapture()
            {
            }

            private void Focus()
            {
            }

            public virtual bool OnMouseDown(Float2 location, MouseButton button)
            {
                if (_rightMouseDown || ({|#1:_middleMouseDown|} && {|#0:_middleMouseDown|}))
                {
                    StartMouseCapture();
                    Focus();
                    return true;
                }

                return false;
            }
        }
        """,
        Repeat("_middleMouseDown", "_middleMouseDown", "&&"));
}
