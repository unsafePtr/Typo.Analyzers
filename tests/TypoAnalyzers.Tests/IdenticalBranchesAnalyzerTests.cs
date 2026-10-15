using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.IdenticalBranchesAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class IdenticalBranchesAnalyzerTests
{
    // Markup: TYPO0006 marks the condition, the 'then' part and the 'else' part with consecutive markers from 'marker'.
    private static DiagnosticResult Identical(string construct, string condition, int marker = 0) =>
        Verify.Diagnostic(Descriptors.IdenticalBranches)
            .WithLocation(marker)
            .WithLocation(marker + 1)
            .WithLocation(marker + 2)
            .WithArguments(construct, condition);

    // Markup: TYPO0007 marks the duplicate with 'marker' and the original branch with the next one.
    private static DiagnosticResult Duplicate(string originalLabel, int marker = 0) =>
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(marker).WithLocation(marker + 1).WithArguments(originalLabel);

    [Fact]
    public Task SwitchPatternVariablesOfReferenceTypesCallSameMember() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;

        public static class C
        {
            public static void M(object value, bool verbose)
            {
                switch (value)
                {
                    {|#1:case Array items:|}
                        if (verbose) Console.WriteLine(items.ToString());
                        break;
                    {|#0:case IEnumerable<int> items:|}
                        if (verbose) Console.WriteLine(items.ToString());
                        break;
                }
            }
        }
        """,
        Duplicate("Array items"));

    [Fact]
    public Task SwitchPatternVariablesCallingDifferentMembersAreSilent() => Verify.VerifyAsync(
        """
        using System;

        public sealed class A { public int Area() => 1; }

        public sealed class B { public int Area() => 2; }

        public static class C
        {
            public static void M(object value)
            {
                switch (value)
                {
                    case A shape:
                        Console.WriteLine(shape.Area());
                        break;
                    case B shape:
                        Console.WriteLine(shape.Area());
                        break;
                }
            }
        }
        """);

    [Fact]
    public Task IfAndElseIdentical() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public static class C
        {
            public static void M(bool condition, List<int> list)
            {
                if ({|#0:condition|})
                {|#1:{
                    list.Add(1);
                }|}
                else
                {|#2:{
                    list.Add(1);
                }|}
            }
        }
        """,
        Verify.Diagnostic(Descriptors.IdenticalBranches).WithLocation(0).WithLocation(1).WithLocation(2).WithArguments("if", "condition"));

    [Fact]
    public Task TernaryArmsIdentical() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(int a, int x) => {|#0:a > 0|} ? {|#1:x|} : {|#2:x|};
        }
        """,
        Verify.Diagnostic(Descriptors.IdenticalBranches).WithLocation(0).WithLocation(1).WithLocation(2).WithArguments("?:", "a > 0"));

    [Fact]
    public Task DotNet10_SwitchCasePassesNeighbouringEnum() => Verify.VerifyAsync(
        """
        public enum HelperId { TypeHandle, MethodHandle, MethodDictionary }

        public enum FixupKind { TypeHandle, MethodHandle, MethodDictionary }

        public static class C
        {
            public static int Map(HelperId id)
            {
                switch (id)
                {
                    case HelperId.TypeHandle:
                        return Lookup(FixupKind.TypeHandle);
                    {|#1:case HelperId.MethodHandle:|}
                        return Lookup(FixupKind.MethodHandle);
                    {|#0:case HelperId.MethodDictionary:|}
                        return Lookup(FixupKind.MethodHandle);
                    default:
                        return -1;
                }
            }

            private static int Lookup(FixupKind kind) => (int)kind;
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("HelperId.MethodHandle"));

    [Fact]
    public Task SwitchExpressionArmsIdentical() => Verify.VerifyAsync(
        """
        public enum HelperId { TypeHandle, MethodHandle, MethodDictionary }

        public enum FixupKind { TypeHandle, MethodHandle, MethodDictionary }

        public static class C
        {
            public static int Map(HelperId id) => id switch
            {
                HelperId.TypeHandle => Lookup(FixupKind.TypeHandle),
                HelperId.MethodHandle => {|#1:Lookup(FixupKind.MethodHandle)|},
                HelperId.MethodDictionary => {|#0:Lookup(FixupKind.MethodHandle)|},
                _ => -1,
            };

            private static int Lookup(FixupKind kind) => (int)kind;
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("HelperId.MethodHandle"));

    [Fact]
    public Task ElseIfBranchesIdentical() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static void Log(string message)
            {
            }

            public static void M(int a)
            {
                if (a > 0)
                {
                    {|#1:Log("positive");|}
                }
                else if (a < 0)
                {
                    {|#0:Log("positive");|}
                }
            }
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("a > 0"));

    [Fact]
    public Task FinalElseDuplicatesEarlierBranch() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static void Log(string message)
            {
            }

            public static void M(int a)
            {
                if (a > 0)
                {
                    {|#1:Log("x");|}
                }
                else if (a < 0)
                {
                    Log("y");
                }
                else
                {
                    {|#0:Log("x");|}
                }
            }
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("a > 0"));

    [Fact]
    public Task CaseDuplicatesDefaultSection() => Verify.VerifyAsync(
        """
        public enum Kind { A, B }

        public static class C
        {
            private static int Lookup(int value) => value;

            public static int Map(Kind kind)
            {
                switch (kind)
                {
                    {|#1:default:|}
                        return Lookup(0);
                    {|#0:case Kind.B:|}
                        return Lookup(0);
                }
            }
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("default"));

    [Fact]
    public Task NoDiagnostics_ForTrivialElseIfBodies() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(int a)
            {
                if (a > 0)
                {
                    return a * 2;
                }
                else if (a < 0)
                {
                    return a * 2;
                }

                return 0;
            }
        }
        """);

    [Fact]
    public Task SameTypedPatternVariablesAreStillDuplicates() => Verify.VerifyAsync(
        """
        public sealed class Circle
        {
            public double Area() => 1;
        }

        public static class C
        {
            private static double Log(Circle circle) => circle.Area();

            public static double M(object shape)
            {
                switch (shape)
                {
                    {|#1:case Circle s when s.Area() > 10:|}
                        return Log(s);
                    {|#0:case Circle s:|}
                        return Log(s);
                    default:
                        return 0;
                }
            }
        }
        """,
        Verify.Diagnostic(Descriptors.DuplicateBranchBody).WithLocation(0).WithLocation(1).WithArguments("Circle s"));

    [Fact]
    public Task NoDiagnostics_ForPatternVariablesOfDifferentTypes() => Verify.VerifyAsync(
        """
        public abstract class Shape
        {
        }

        public sealed class Circle : Shape
        {
            public double Area() => 1;
        }

        public sealed class Square : Shape
        {
            public double Area() => 2;
        }

        public static class C
        {
            public static double Area(Shape shape)
            {
                switch (shape)
                {
                    case Circle s:
                        return s.Area();
                    case Square s:
                        return s.Area();
                    default:
                        return 0;
                }
            }

            public static double AreaExpression(Shape shape) => shape switch
            {
                Circle s => s.Area(),
                Square s => s.Area(),
                _ => 0,
            };
        }
        """);

    [Fact]
    public Task NoDiagnostics_ForIdiomaticDuplicates() => Verify.VerifyAsync(
        """
        using System;

        public enum Kind { A, B, C }

        public static class C
        {
            public static bool IsSpecial(Kind kind) => kind switch
            {
                Kind.A => true,
                Kind.B => true,
                _ => false,
            };

            public static int Throwing(Kind kind)
            {
                switch (kind)
                {
                    case Kind.A:
                        throw new NotSupportedException();
                    case Kind.B:
                        throw new NotSupportedException();
                    default:
                        return 0;
                }
            }

            public static int Different(bool flag, int x, int y)
            {
                if (flag)
                {
                    return x;
                }
                else
                {
                    return y;
                }
            }

            public static void Empty(bool flag)
            {
                if (flag)
                {
                }
                else
                {
                }
            }
        }
        """);

    [Fact]
    public Task PixiEditor_BothOrientationsResetTheSameTransform() => Verify.VerifyAsync(
        """
        public enum Orientation { Horizontal, Vertical }

        public sealed class Control
        {
        }

        public sealed class DockableTabDragBehavior
        {
            private Orientation orientation;

            private static void SetTranslateTransform(Control control, double x, double y)
            {
            }

            public void PointerMoved(Control targetContainer, double targetStart, double draggedStart, double draggedDeltaStart, double draggedDeltaEnd, double targetMid)
            {
                if (targetStart > draggedStart && draggedDeltaEnd >= targetMid)
                {
                    SetTranslateTransform(targetContainer, -1, 0);
                }
                else if (targetStart < draggedStart && draggedDeltaStart <= targetMid)
                {
                    SetTranslateTransform(targetContainer, 1, 0);
                }
                else
                {
                    if ({|#0:orientation == Orientation.Horizontal|})
                    {|#1:{
                        SetTranslateTransform(targetContainer, 0, 0);
                    }|}
                    else
                    {|#2:{
                        SetTranslateTransform(targetContainer, 0, 0);
                    }|}
                }
            }
        }
        """,
        Identical("if", "orientation == Orientation.Horizontal"));

    [Fact]
    public Task AvalonStudio_LambdaSetsBuiltInTypeEitherWay() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;
        using System.Linq;

        public sealed class Parameter
        {
            public string BuiltInType { get; set; }
            public string Type { get; set; }
        }

        public static class InvocationContext
        {
            private static (bool inbuilt, string name)? GetReturnType(string parameter) => null;

            public static List<Parameter> BuildParameters(IEnumerable<string> parameters) =>
                parameters.Select(parameter =>
                {
                    var info = GetReturnType(parameter);
                    var result = new Parameter();
                    if (info.HasValue)
                    {
                        if ({|#0:info.Value.inbuilt|})
                        {|#1:{
                            result.BuiltInType = info.Value.name;
                        }|}
                        else
                        {|#2:{
                            result.BuiltInType = info.Value.name;
                        }|}
                    }

                    return result;
                }).ToList();
        }
        """,
        Identical("if", "info.Value.inbuilt"));

    [Fact]
    public Task Orleans_ReverseOrderStoresInTheSameOrder() => Verify.VerifyAsync(
        """
        using System.Threading.Tasks;

        public sealed class StateStorage
        {
            public Task<string> Store(string etag, object metadata, object pendingStates, int? commitUpTo, int? abortAfter) => Task.FromResult(etag);
        }

        public class TransactionalStateStorageTestRunner
        {
            private readonly StateStorage stateStorage = new();

            public virtual async Task ConfirmOneAndCancelOne(string etag, object metadata, object emptyPendingStates, bool useTwoSteps = false, bool reverseOrder = false)
            {
                if (useTwoSteps)
                {
                    if ({|#0:reverseOrder|})
                    {|#1:{
                        etag = await stateStorage.Store(etag, metadata, emptyPendingStates, 1, null);

                        _ = await stateStorage.Store(etag, metadata, emptyPendingStates, null, 1);
                    }|}
                    else
                    {|#2:{
                        etag = await stateStorage.Store(etag, metadata, emptyPendingStates, 1, null);

                        _ = await stateStorage.Store(etag, metadata, emptyPendingStates, null, 1);
                    }|}
                }
                else
                {
                    _ = await stateStorage.Store(etag, metadata, emptyPendingStates, 1, 1);
                }
            }
        }
        """,
        Identical("if", "reverseOrder"));

    [Fact]
    public Task Barotrauma_MidRoundSyncGetsTheSameEvents() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public sealed class Client
        {
            public bool NeedsMidRoundSync { get; set; }
        }

        public sealed class NetEntityEvent
        {
        }

        public sealed class ServerEntityEventManager
        {
            private List<NetEntityEvent> GetEventsToSync(Client client) => new();

            public List<NetEntityEvent> Write(Client client)
            {
                List<NetEntityEvent> eventsToSync = null;
                if ({|#0:client.NeedsMidRoundSync|})
                {|#1:{
                    eventsToSync = GetEventsToSync(client);
                }|}
                else
                {|#2:{
                    eventsToSync = GetEventsToSync(client);
                }|}

                return eventsToSync;
            }
        }
        """,
        Identical("if", "client.NeedsMidRoundSync"));

    [Fact]
    public Task Nethermind_RejectedBlockStillReportsSuccess() => Verify.VerifyAsync(
        """
        using System.Threading;
        using System.Threading.Tasks;

        public enum StatusCode { Success, InvalidRequest }

        public sealed class ApiResponse(StatusCode statusCode)
        {
            public StatusCode StatusCode { get; } = statusCode;
        }

        public sealed class BeaconNodeFacade
        {
            private Task<bool> AcceptAsync(object signedBlock, CancellationToken cancellationToken) => Task.FromResult(signedBlock is not null);

            public async Task<ApiResponse> PublishBlockAsync(object signedBlock, CancellationToken cancellationToken)
            {
                bool acceptedLocally = false;
                acceptedLocally = await AcceptAsync(signedBlock, cancellationToken);
                if ({|#0:acceptedLocally|})
                {|#1:{
                    return new ApiResponse(StatusCode.Success);
                }|}
                else
                {|#2:{
                    return new ApiResponse(StatusCode.Success);
                }|}
            }
        }
        """,
        Identical("if", "acceptedLocally"));

    [Fact]
    public Task RunUO_OverflowClampsToMaxValueEitherWay() => Verify.VerifyAsync(
        """
        using System;

        public sealed class BinaryFileWriter
        {
            private TimeSpan m_Last;

            public void WriteDeltaTime(DateTime value)
            {
                long ticks = value.Ticks;
                long now = DateTime.UtcNow.Ticks;

                TimeSpan d;
                try
                {
                    d = new TimeSpan(ticks - now);
                }
                catch
                {
                    if ({|#0:ticks < now|})
                        {|#1:d = TimeSpan.MaxValue;|}
                    else
                        {|#2:d = TimeSpan.MaxValue;|}
                }

                m_Last = d;
            }
        }
        """,
        Identical("if", "ticks < now"));

    [Fact]
    public Task Avalonia_AscendingSortAlsoDescends() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;
        using System.Linq;

        public sealed class DataGridSortDescription
        {
            private readonly bool _descending;
            private readonly IComparer<object> InternalComparer = Comparer<object>.Default;

            public DataGridSortDescription(bool descending) => _descending = descending;

            private object GetValue(object item) => item;

            public IOrderedEnumerable<object> ThenBy(IOrderedEnumerable<object> seq)
            {
                if ({|#0:_descending|})
                {|#1:{
                    return seq.ThenByDescending(o => GetValue(o), InternalComparer);
                }|}
                else
                {|#2:{
                    return seq.ThenByDescending(o => GetValue(o), InternalComparer);
                }|}
            }
        }
        """,
        Identical("if", "_descending"));

    [Fact]
    public Task FastReport_SplitBranchesDeclareTheSameLocals() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public sealed class CharWithIndex
        {
        }

        public sealed class RunText(List<CharWithIndex> chars, float left)
        {
            public List<CharWithIndex> Chars => chars;

            public float Width => left;
        }

        public static class HtmlTextRenderer
        {
            public static RunText Split(float availableWidth, out RunText secondPart, CharWithIndex[] chars, int point, int size, float left, RunText r)
            {
                if ({|#0:r.Width > availableWidth|})
                {|#1:{
                    List<CharWithIndex> list = new List<CharWithIndex>();
                    for (int i = point; i < size; i++)
                        list.Add(chars[i]);
                    secondPart = new RunText(list, left + r.Width);
                    list.Clear();
                    for (int i = 0; i < point; i++)
                        list.Add(chars[i]);
                    r = new RunText(list, left);

                    return r;
                }|}
                else
                {|#2:{
                    List<CharWithIndex> list = new List<CharWithIndex>();
                    for (int i = point; i < size; i++)
                        list.Add(chars[i]);
                    secondPart = new RunText(list, left + r.Width);
                    list.Clear();
                    for (int i = 0; i < point; i++)
                        list.Add(chars[i]);
                    r = new RunText(list, left);
                    return r;
                }|}
            }
        }
        """,
        Identical("if", "r.Width > availableWidth"));

    [Fact]
    public Task InferNet_ComparerReturnsTheSameResultForBothSecondOrders() => Verify.VerifyAsync(
        """
        public readonly struct Pair<T1, T2>(T1 first, T2 second)
        {
            public T1 First { get; } = first;
            public T2 Second { get; } = second;
        }

        public sealed class RegexpTreeBuilder
        {
            public int Compare(Pair<int, int> x, Pair<int, int> y)
            {
                if (x.First < y.First)
                {
                    if ({|#0:x.Second >= y.Second|})
                    {|#1:{
                        return 1;
                    }|}
                    else
                    {|#2:{
                        return 1;
                    }|}
                }
                else if (x.First > y.First)
                {
                    if ({|#3:x.Second <= y.Second|})
                    {|#4:{
                        return -1;
                    }|}
                    else
                    {|#5:{
                        return -1;
                    }|}
                }

                return 0;
            }
        }
        """,
        Identical("if", "x.Second >= y.Second"),
        Identical("if", "x.Second <= y.Second", marker: 3));

    [Fact]
    public Task Roslyn_EvenAndOddIterationsStartThreadsInTheSameOrder() => Verify.VerifyAsync(
        """
        using System.Threading;

        public static class GetSemanticInfoTests
        {
            public static void IndexerMemberRace()
            {
                for (int i = 0; i < 20; i++)
                {
                    var thread1 = new Thread(() => { });
                    var thread2 = new Thread(() => { });

                    if ({|#0:i % 2 == 0|})
                    {|#1:{
                        thread1.Start();
                        thread2.Start();
                    }|}
                    else
                    {|#2:{
                        thread1.Start();
                        thread2.Start();
                    }|}

                    thread1.Join();
                    thread2.Join();
                }
            }
        }
        """,
        Identical("if", "i % 2 == 0"));

    [Fact]
    public Task CoreFx_ReliabilitySectionRunsTheSameTryCatch() => Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;

        internal static class AsyncHelper
        {
            internal static void ContinueTask(TaskCompletionSource<object> completion, Action onSuccess, object connectionToDoom, object connectionToAbort)
            {
                if ({|#0:connectionToDoom != null || connectionToAbort != null|})
                {|#1:{
                    try
                    {
                        onSuccess();
                    }
                    catch (Exception e)
                    {
                        completion.SetException(e);
                    }
                }|}
                else
                {|#2:{
                    try
                    {
                        onSuccess();
                    }
                    catch (Exception e)
                    {
                        completion.SetException(e);
                    }
                }|}
            }
        }
        """,
        Identical("if", "connectionToDoom != null || connectionToAbort != null"));

    [Fact]
    public Task Umbraco_ClassStylesAppendedLikeOtherStyles() => Verify.VerifyAsync(
        """
        public sealed class StyleProperty
        {
            public string Alias { get; set; }
            public string Text { get; set; }
        }

        public sealed class TinyMce
        {
            public string Styles(StyleProperty[] properties)
            {
                var styles = string.Empty;
                foreach (var p in properties)
                {
                    if ({|#0:p.Alias.StartsWith(".")|})
                        {|#1:styles += p.Text + "=" + p.Alias;|}
                    else
                        {|#2:styles += p.Text + "=" + p.Alias;|}
                }

                return styles;
            }
        }
        """,
        Identical("if", "p.Alias.StartsWith(\".\")"));

    [Fact]
    public Task Garnet_FlushIntervalIgnoresDebugger() => Verify.VerifyAsync(
        """
        using System;
        using System.Diagnostics;

        public class FileLoggerOutput
        {
            private readonly TimeSpan flushInterval =
                                                    {|#0:Debugger.IsAttached|} ?
                                                    {|#1:TimeSpan.FromMilliseconds(10)|} :
                                                    {|#2:TimeSpan.FromMilliseconds(10)|};

            public TimeSpan FlushInterval => flushInterval;
        }
        """,
        Identical("?:", "Debugger.IsAttached"));

    [Fact]
    public Task RunUO_ChestItemIdIgnoresRandomChoice() => Verify.VerifyAsync(
        """
        public static class Utility
        {
            public static bool RandomBool() => true;

            public static int RandomList(params int[] list) => list[0];
        }

        public sealed class TreasureChestLevel2
        {
            public int ItemID { get; set; }
            public int GumpID { get; set; }

            private void SetChestAppearance()
            {
                bool UseFirstItemId = Utility.RandomBool();

                switch (Utility.RandomList(0, 1, 2, 3, 4, 5, 6, 7))
                {
                    case 0:
                        this.ItemID = UseFirstItemId ? 0xe3c : 0xe3d;
                        this.GumpID = 0x49;
                        break;

                    case 6:
                        this.ItemID = ({|#0:UseFirstItemId|} ? {|#1:0xe7f|} : {|#2:0xe7f|});
                        this.GumpID = 0x3e;
                        break;

                    case 7:
                        this.ItemID = ({|#3:UseFirstItemId|} ? {|#4:0xe77|} : {|#5:0xe77|});
                        this.GumpID = 0x3e;
                        break;
                }
            }
        }
        """,
        Identical("?:", "UseFirstItemId"),
        Identical("?:", "UseFirstItemId", marker: 3));

    [Fact]
    public Task Mono_PressedHighlightIgnoresSystemColors() => Verify.VerifyAsync(
        """
        using System.Drawing;

        public class ProfessionalColorTable
        {
            private bool use_system_colors;
            private Color button_pressed_highlight;

            public Color ButtonPressedHighlight => button_pressed_highlight;

            public ProfessionalColorTable(bool useSystemColors) => use_system_colors = useSystemColors;

            private void CalculateColors()
            {
                button_pressed_highlight = {|#0:use_system_colors|} ?
                                           {|#1:Color.FromArgb(150, 179, 225)|} :
                                           {|#2:Color.FromArgb(150, 179, 225)|};
            }
        }
        """,
        Identical("?:", "use_system_colors"));

    [Fact]
    public Task Roslyn_SingularMessageForEveryCount() => Verify.VerifyAsync(
        """
        using System;

        internal static class ScriptingResources
        {
            internal static string PlusAdditionalError => "+ additional {0} error";

            internal static string PlusAdditionalErrors => "+ additional {0} errors";
        }

        internal sealed class CommandLineRunner
        {
            private void DisplayDiagnostics(int notShown)
            {
                Console.Out.WriteLine(
                  string.Format({|#0:(notShown == 1)|} ?
                    {|#1:ScriptingResources.PlusAdditionalError|} :
                    {|#2:ScriptingResources.PlusAdditionalError|},
                    notShown));
            }
        }
        """,
        Identical("?:", "(notShown == 1)"));

    [Fact]
    public Task IronPython_NullHandlersFallBackToThemselves() => Verify.VerifyAsync(
        """
        public sealed class TryExpression
        {
            public object Finally { get; set; }
            public object Handlers { get; set; }
        }

        public sealed class DebugInfoRewriter
        {
            private static TryExpression MakeTry(object @finally, object handlers) => new() { Finally = @finally, Handlers = handlers };

            protected TryExpression VisitTry(TryExpression node, object newHandlers, object newFinally)
            {
                if (newHandlers != null || newFinally != null)
                {
                    node = MakeTry(
                      newFinally != null ? newFinally : node.Finally,
                      {|#0:newHandlers != null|} ? {|#1:newHandlers|} : {|#2:newHandlers|}
                    );
                }

                return node;
            }
        }
        """,
        Identical("?:", "newHandlers != null"));

    [Fact]
    public Task MonoDevelop_PreviousDocumentSwitchesToNext() => Verify.VerifyAsync(
        """
        public enum WindowCommands
        {
            NextDocument,
            PrevDocument,
            OpenDocumentList,
            OpenWindowList,
            SplitWindowVertically,
            SplitWindowHorizontally,
            UnsplitWindow,
            SwitchSplitWindow,
            SwitchNextDocument,
            SwitchPreviousDocument
        }

        public static class CommandService
        {
            public static void DispatchCommand(object command)
            {
            }
        }

        public class WindowCommandHandler
        {
            public static bool EnableDocumentSwitchDialog { get; set; }

            protected static void Switch(bool next)
            {
                if (!EnableDocumentSwitchDialog)
                {
                    CommandService.DispatchCommand(
                      {|#0:next|} ? {|#1:WindowCommands.NextDocument|} :
                             {|#2:WindowCommands.NextDocument|});
                    return;
                }
            }
        }
        """,
        Identical("?:", "next"));

    [Fact]
    public Task Osu_RoomTypeChangeUpdatesTheName() => Verify.VerifyAsync(
        """
        using System.ComponentModel;

        public sealed class Room
        {
            public string Name { get; set; }
            public int Type { get; set; }
            public int QueueMode { get; set; }
            public string Password { get; set; }
        }

        public sealed class MultiplayerMatchSettingsOverlay
        {
            private void updateRoomName()
            {
            }

            private void updateRoomQueueMode()
            {
            }

            private void updateRoomPassword()
            {
            }

            private void onRoomPropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                switch (e.PropertyName)
                {
                    {|#1:case nameof(Room.Name):|}
                        updateRoomName();
                        break;

                    {|#0:case nameof(Room.Type):|}
                        updateRoomName();
                        break;

                    case nameof(Room.QueueMode):
                        updateRoomQueueMode();
                        break;

                    case nameof(Room.Password):
                        updateRoomPassword();
                        break;
                }
            }
        }
        """,
        Duplicate("nameof(Room.Name)"));

    [Fact]
    public Task Files_SizeOnlyProgressCopiesSizeAndCount() => Verify.VerifyAsync(
        """
        using System;
        using System.Numerics;

        public sealed class StatusCenterItemProgressModel
        {
            public long TotalSize { get; set; }
            public long ItemsCount { get; set; }
            public long ProcessedSize { get; set; }
            public long ProcessedItemsCount { get; set; }
            public double ProcessingSizeSpeed { get; set; }
            public double ProcessingItemsCountSpeed { get; set; }
        }

        public static class SizeExtensions
        {
            public static string ToSizeString(this double size) => size.ToString();
        }

        public sealed class StatusCenterItem
        {
            private Vector2 point;

            public int ProgressPercentage { get; set; }
            public string SpeedText { get; set; }

            private void ReportProgress(StatusCenterItemProgressModel value)
            {
                switch (value.TotalSize, value.ItemsCount)
                {
                    {|#1:case (not 0, not 0):|}
                        ProgressPercentage = Math.Clamp((int)(value.ProcessedSize * 100.0 / value.TotalSize), 0, 100);
                        SpeedText = $"{value.ProcessingSizeSpeed.ToSizeString()}/s";
                        point = new((float)(value.ProcessedSize * 100.0 / value.TotalSize), (float)value.ProcessingSizeSpeed);
                        break;

                    {|#0:case (not 0, _):|}
                        ProgressPercentage = Math.Clamp((int)(value.ProcessedSize * 100.0 / value.TotalSize), 0, 100);
                        SpeedText = $"{value.ProcessingSizeSpeed.ToSizeString()}/s";
                        point = new((float)(value.ProcessedSize * 100.0 / value.TotalSize), (float)value.ProcessingSizeSpeed);
                        break;

                    case (_, not 0):
                        ProgressPercentage = Math.Clamp((int)(value.ProcessedItemsCount * 100.0 / value.ItemsCount), 0, 100);
                        SpeedText = $"{value.ProcessingItemsCountSpeed:0} items/s";
                        point = new((float)(value.ProcessedItemsCount * 100.0 / value.ItemsCount), (float)value.ProcessingItemsCountSpeed);
                        break;

                    default:
                        SpeedText = string.Empty;
                        break;
                }
            }
        }
        """,
        Duplicate("(not 0, not 0)"));

    [Fact]
    public Task Radarr_RecommendedByAllTimeRequestsYearlyList() => Verify.VerifyAsync(
        """
        public enum TraktPopularListType
        {
            Trending,
            Popular,
            Anticipated,
            BoxOffice,
            TopWatchedByWeek,
            TopWatchedByMonth,
            TopWatchedByYear,
            TopWatchedByAllTime,
            RecommendedByWeek,
            RecommendedByMonth,
            RecommendedByYear,
            RecommendedByAllTime,
        }

        public sealed class TraktPopularSettings
        {
            public int TraktListType { get; set; }
        }

        public sealed class TraktPopularRequestGenerator
        {
            public TraktPopularSettings Settings { get; set; }

            private string GetMoviesRequest()
            {
                var link = "https://api.trakt.tv/";

                switch (Settings.TraktListType)
                {
                    case (int)TraktPopularListType.Trending:
                        link += "movies/trending";
                        break;
                    case (int)TraktPopularListType.Popular:
                        link += "movies/popular";
                        break;
                    case (int)TraktPopularListType.Anticipated:
                        link += "movies/anticipated";
                        break;
                    case (int)TraktPopularListType.BoxOffice:
                        link += "movies/boxoffice";
                        break;
                    case (int)TraktPopularListType.TopWatchedByWeek:
                        link += "movies/watched/weekly";
                        break;
                    case (int)TraktPopularListType.TopWatchedByMonth:
                        link += "movies/watched/monthly";
                        break;
                    case (int)TraktPopularListType.TopWatchedByYear:
                        link += "movies/watched/yearly";
                        break;
                    case (int)TraktPopularListType.TopWatchedByAllTime:
                        link += "movies/watched/all";
                        break;
                    case (int)TraktPopularListType.RecommendedByWeek:
                        link += "movies/recommended/weekly";
                        break;
                    case (int)TraktPopularListType.RecommendedByMonth:
                        link += "movies/recommended/monthly";
                        break;
                    {|#1:case (int)TraktPopularListType.RecommendedByYear:|}
                        link += "movies/recommended/yearly";
                        break;
                    {|#0:case (int)TraktPopularListType.RecommendedByAllTime:|}
                        link += "movies/recommended/yearly";
                        break;
                }

                return link;
            }
        }
        """,
        Duplicate("(int)TraktPopularListType.RecommendedByYear"));

    [Fact]
    public Task Radarr_RemovedDownloadReportedAsFailed() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public enum DownloadItemStatus { Queued, Paused, Downloading, Completed, Failed, Warning }

        public sealed class Aria2Status
        {
            public string Status { get; set; }
        }

        public sealed class DownloadClientItem
        {
            public DownloadItemStatus Status { get; set; }
        }

        public sealed class Aria2
        {
            public IEnumerable<DownloadClientItem> GetItems(IEnumerable<Aria2Status> torrents)
            {
                foreach (var torrent in torrents)
                {
                    var status = DownloadItemStatus.Queued;
                    switch (torrent.Status)
                    {
                        case "active":
                            status = DownloadItemStatus.Downloading;
                            break;
                        case "waiting":
                            status = DownloadItemStatus.Queued;
                            break;
                        case "paused":
                            status = DownloadItemStatus.Paused;
                            break;
                        {|#1:case "error":|}
                            status = DownloadItemStatus.Failed;
                            break;
                        case "complete":
                            status = DownloadItemStatus.Completed;
                            break;
                        {|#0:case "removed":|}
                            status = DownloadItemStatus.Failed;
                            break;
                    }

                    yield return new DownloadClientItem { Status = status };
                }
            }
        }
        """,
        Duplicate("\"error\""));

    [Fact]
    public Task DotNet9_UInt64VariantCreatedFromInt64() => Verify.VerifyAsync(
        """
        using System;
        using System.Globalization;
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Marshalling;

        internal static class DynamicVariantExtensions
        {
            public static void SetAsIConvertible(this ref ComVariant variant, IConvertible value)
            {
                TypeCode tc = value.GetTypeCode();
                CultureInfo ci = CultureInfo.CurrentCulture;

                switch (tc)
                {
                    case TypeCode.Empty: break;
                    case TypeCode.Object:
                        variant = ComVariant.CreateRaw(VarEnum.VT_UNKNOWN, nint.Zero); break;
                    case TypeCode.DBNull:
                        variant = ComVariant.Null; break;
                    case TypeCode.Boolean:
                        variant = ComVariant.Create<bool>(value.ToBoolean(ci)); break;
                    case TypeCode.Char:
                        variant = ComVariant.Create<ushort>(value.ToChar(ci)); break;
                    case TypeCode.SByte:
                        variant = ComVariant.Create<sbyte>(value.ToSByte(ci)); break;
                    case TypeCode.Byte:
                        variant = ComVariant.Create<byte>(value.ToByte(ci)); break;
                    case TypeCode.Int16:
                        variant = ComVariant.Create(value.ToInt16(ci)); break;
                    case TypeCode.UInt16:
                        variant = ComVariant.Create(value.ToUInt16(ci)); break;
                    case TypeCode.Int32:
                        variant = ComVariant.Create(value.ToInt32(ci)); break;
                    case TypeCode.UInt32:
                        variant = ComVariant.Create(value.ToUInt32(ci)); break;
                    {|#1:case TypeCode.Int64:|}
                        variant = ComVariant.Create(value.ToInt64(ci)); break;
                    {|#0:case TypeCode.UInt64:|}
                        variant = ComVariant.Create(value.ToInt64(ci)); break;
                    case TypeCode.Single:
                        variant = ComVariant.Create(value.ToSingle(ci)); break;
                    case TypeCode.Double:
                        variant = ComVariant.Create(value.ToDouble(ci)); break;
                    case TypeCode.Decimal:
                        variant = ComVariant.Create(value.ToDecimal(ci)); break;
                    case TypeCode.DateTime:
                        variant = ComVariant.Create(value.ToDateTime(ci)); break;
                    case TypeCode.String:
                        variant = ComVariant.Create(new BStrWrapper(value.ToString(ci))); break;

                    default:
                        throw new NotSupportedException();
                }
            }
        }
        """,
        Duplicate("TypeCode.Int64"));

    [Fact]
    public Task EtoForms_SecondFixedPanelSizesTheFirstColumn() => Verify.VerifyAsync(
        """
        using sw = System.Windows;

        namespace System.Windows
        {
            public enum GridUnitType { Auto, Pixel, Star }

            public readonly struct GridLength(double value, GridUnitType type)
            {
                public double Value => value;

                public GridUnitType GridUnitType => type;
            }
        }

        public enum SplitterFixedPanel { Panel1, Panel2, None }

        public sealed class SplitterHandler
        {
            public SplitterFixedPanel FixedPanel { get; set; }

            private void SetLength(int index, sw.GridLength length)
            {
            }

            private void UpdateColumnSizing()
            {
                switch (FixedPanel)
                {
                    {|#1:case SplitterFixedPanel.Panel1:|}
                        SetLength(0, new sw.GridLength(1, sw.GridUnitType.Star));
                        break;
                    {|#0:case SplitterFixedPanel.Panel2:|}
                        SetLength(0, new sw.GridLength(1, sw.GridUnitType.Star));
                        break;
                    case SplitterFixedPanel.None:
                        SetLength(0, new sw.GridLength(1, sw.GridUnitType.Star));
                        SetLength(2, new sw.GridLength(1, sw.GridUnitType.Star));
                        break;
                }
            }
        }
        """,
        Duplicate("SplitterFixedPanel.Panel1"));

    [Fact]
    public Task DotNetNuke_WeeksAddDays() => Verify.VerifyAsync(
        """
        using System;

        public static class PropertyAccessTests
        {
            private static DateTime CalculateTime(int lapse, string measurement)
            {
                var nextTime = new DateTime();
                switch (measurement)
                {
                    case "s":
                        nextTime = DateTime.Now.AddSeconds(lapse);
                        break;
                    case "m":
                        nextTime = DateTime.Now.AddMinutes(lapse);
                        break;
                    case "h":
                        nextTime = DateTime.Now.AddHours(lapse);
                        break;
                    {|#1:case "d":|}
                        nextTime = DateTime.Now.AddDays(lapse);
                        break;
                    {|#0:case "w":|}
                        nextTime = DateTime.Now.AddDays(lapse);
                        break;
                    case "mo":
                        nextTime = DateTime.Now.AddMonths(lapse);
                        break;
                    case "y":
                        nextTime = DateTime.Now.AddYears(lapse);
                        break;
                }

                return nextTime;
            }
        }
        """,
        Duplicate("\"d\""));

    [Fact]
    public Task DotNetNuke_Ipv6FilterResolvesIpv4() => Verify.VerifyAsync(
        """
        public enum AddressType
        {
            IPv4 = 0,
            IPv6 = 1,
        }

        public static class NetworkUtils
        {
            public static string GetAddress(string host, AddressType addressFamily) => host;
        }

        public static class RequestFilterModule
        {
            private static string FilterRequest(string[] varArray, string varVal)
            {
                switch (varArray[1])
                {
                    {|#1:case "IPv4":|}
                        varVal = NetworkUtils.GetAddress(varVal, AddressType.IPv4);
                        break;
                    {|#0:case "IPv6":|}
                        varVal = NetworkUtils.GetAddress(varVal, AddressType.IPv4);
                        break;
                }

                return varVal;
            }
        }
        """,
        Duplicate("\"IPv4\""));

    [Fact]
    public Task PeachPie_NotNullAttributeDecodedAsAllowNull() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;
        using System.Reflection;

        [Flags]
        public enum FlowAnalysisAnnotations
        {
            None = 0,
            AllowNull = 1,
            DisallowNull = 2,
            MaybeNullWhenTrue = 4,
            MaybeNullWhenFalse = 8,
            MaybeNull = MaybeNullWhenTrue | MaybeNullWhenFalse,
            NotNullWhenTrue = 16,
            NotNullWhenFalse = 32,
            NotNull = NotNullWhenTrue | NotNullWhenFalse,
        }

        public static class ReflectionUtils
        {
            private static bool TryGetBoolArgument(CustomAttributeData attr, out bool value)
            {
                value = false;
                return false;
            }

            private static FlowAnalysisAnnotations DecodeFlowAnalysisAttributes(IEnumerable<CustomAttributeData> attributes)
            {
                var result = FlowAnalysisAnnotations.None;

                foreach (var attr in attributes)
                {
                    switch (attr.AttributeType.FullName)
                    {
                        {|#1:case "System.Diagnostics.CodeAnalysis.AllowNullAttribute":|}
                            result |= FlowAnalysisAnnotations.AllowNull;
                            break;
                        case "System.Diagnostics.CodeAnalysis.DisallowNullAttribute":
                            result |= FlowAnalysisAnnotations.DisallowNull;
                            break;
                        case "System.Diagnostics.CodeAnalysis.MaybeNullAttribute":
                            result |= FlowAnalysisAnnotations.MaybeNull;
                            break;
                        case "System.Diagnostics.CodeAnalysis.MaybeNullWhenAttribute":
                            if (TryGetBoolArgument(attr, out bool maybeNullWhen))
                            {
                                result |= maybeNullWhen ? FlowAnalysisAnnotations.MaybeNullWhenTrue
                                                        : FlowAnalysisAnnotations.MaybeNullWhenFalse;
                            }
                            break;
                        {|#0:case "System.Diagnostics.CodeAnalysis.NotNullAttribute":|}
                            result |= FlowAnalysisAnnotations.AllowNull;
                            break;
                    }
                }

                return result;
            }
        }
        """,
        Duplicate("\"System.Diagnostics.CodeAnalysis.AllowNullAttribute\""));

    [Fact]
    public Task Ryujinx_UpperCaseOperatorParsedAsRemainder() => Verify.VerifyAsync(
        """
        public abstract class BaseNode
        {
        }

        public sealed class Demangler
        {
            private readonly string _mangled;
            private int _position;

            public Demangler(string mangled) => _mangled = mangled;

            private char Peek(int offset = 0) => _mangled[_position + offset];

            private BaseNode ParseBinaryExpression(string name) => null;

            private BaseNode ParseExpression()
            {
                switch (Peek(1))
                {
                    case 'l':
                        _position += 2;
                        return ParseBinaryExpression("<<");
                    {|#1:case 'm':|}
                        _position += 2;
                        return ParseBinaryExpression("%");
                    {|#0:case 'M':|}
                        _position += 2;
                        return ParseBinaryExpression("%");
                    default:
                        return null;
                }
            }
        }
        """,
        Duplicate("'m'"));

    [Fact]
    public Task IlSpy_EnumValueShownWithLiteralIcon() => Verify.VerifyAsync(
        """
        using System;

        public enum MemberIcon
        {
            Field,
            FieldReadOnly,
            Literal,
            EnumValue,
            Property,
            Indexer,
            Method,
            Constructor,
            VirtualMethod,
            Operator,
            ExtensionMethod,
            PInvokeMethod,
            Event,
        }

        public sealed class ImageSource
        {
        }

        public static class Images
        {
            public static readonly ImageSource Field = new();
            public static readonly ImageSource FieldReadOnly = new();
            public static readonly ImageSource Literal = new();
            public static readonly ImageSource EnumValue = new();
            public static readonly ImageSource Property = new();
            public static readonly ImageSource Indexer = new();
            public static readonly ImageSource Method = new();
            public static readonly ImageSource Constructor = new();
            public static readonly ImageSource VirtualMethod = new();
            public static readonly ImageSource Operator = new();
            public static readonly ImageSource ExtensionMethod = new();
            public static readonly ImageSource PInvokeMethod = new();
            public static readonly ImageSource Event = new();
        }

        public sealed class MemberIconCache
        {
            public ImageSource GetBaseImage(MemberIcon icon)
            {
                ImageSource baseImage;
                switch (icon)
                {
                    case MemberIcon.Field:
                        baseImage = Images.Field;
                        break;
                    case MemberIcon.FieldReadOnly:
                        baseImage = Images.FieldReadOnly;
                        break;
                    {|#1:case MemberIcon.Literal:|}
                        baseImage = Images.Literal;
                        break;
                    {|#0:case MemberIcon.EnumValue:|}
                        baseImage = Images.Literal;
                        break;
                    case MemberIcon.Property:
                        baseImage = Images.Property;
                        break;
                    case MemberIcon.Indexer:
                        baseImage = Images.Indexer;
                        break;
                    case MemberIcon.Method:
                        baseImage = Images.Method;
                        break;
                    case MemberIcon.Constructor:
                        baseImage = Images.Constructor;
                        break;
                    case MemberIcon.VirtualMethod:
                        baseImage = Images.VirtualMethod;
                        break;
                    case MemberIcon.Operator:
                        baseImage = Images.Operator;
                        break;
                    case MemberIcon.ExtensionMethod:
                        baseImage = Images.ExtensionMethod;
                        break;
                    case MemberIcon.PInvokeMethod:
                        baseImage = Images.PInvokeMethod;
                        break;
                    case MemberIcon.Event:
                        baseImage = Images.Event;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(icon), $"MemberIcon.{icon} is not supported!");
                }

                return baseImage;
            }
        }
        """,
        Duplicate("MemberIcon.Literal"));

    [Fact]
    public Task OpenXmlSdk_EndElementSkippedLikeStartElement() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Xml;

        public enum ElementState { Null, Start, End, MiscNode, EOF }

        public sealed class OpenXmlPartReader
        {
            private readonly XmlReader _xmlReader;
            private readonly Stack<object> _elementStack = new();
            private ElementState _elementState;

            public OpenXmlPartReader(XmlReader xmlReader) => _xmlReader = xmlReader;

            private void ThrowIfNull()
            {
            }

            private void GetElementInformation()
            {
            }

            private void InnerSkip()
            {
                Debug.Assert(_xmlReader != null);

                switch (_elementState)
                {
                    case ElementState.Null:
                        ThrowIfNull();
                        break;

                    case ElementState.EOF:
                        return;

                    {|#1:case ElementState.Start:|}
                        _xmlReader.Skip();
                        _elementStack.Pop();
                        GetElementInformation();
                        return;

                    {|#0:case ElementState.End:|}
                    case ElementState.MiscNode:
                        _xmlReader.Skip();
                        _elementStack.Pop();
                        GetElementInformation();
                        return;
                }

                _elementState = ElementState.Null;
            }
        }
        """,
        Duplicate("ElementState.Start"));

    [Fact]
    public Task CoreFx_ControlLightTranslatedAsButtonFace() => Verify.VerifyAsync(
        """
        using System.Drawing;

        public static class ColorTranslatorPort
        {
            public static string ToHtml(Color c)
            {
                string colorString = string.Empty;
                switch (c.ToKnownColor())
                {
                    case KnownColor.ActiveBorder:
                        colorString = "activeborder";
                        break;
                    case KnownColor.ActiveCaption:
                        colorString = "activecaption";
                        break;
                    {|#1:case KnownColor.Control:|}
                        colorString = "buttonface";
                        break;
                    {|#0:case KnownColor.ControlLight:|}
                        colorString = "buttonface";
                        break;
                    case KnownColor.ControlText:
                        colorString = "buttontext";
                        break;
                }

                return colorString;
            }
        }
        """,
        Duplicate("KnownColor.Control"));

    [Fact]
    public Task CoreFx_UInt32ConvertedLikeSignedTypes() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections;
        using System.Globalization;

        public enum CimType { SInt8, UInt8, SInt16, UInt16, SInt32, UInt32, SInt64, UInt64 }

        public static class WmiGenerator
        {
            private static string ConvertToNumericValueAndAddToArray(CimType cimType, object numericValue, ArrayList arrayToAdd, out string enumType)
            {
                string retFunctionName = string.Empty;
                enumType = string.Empty;

                switch (cimType)
                {
                    {|#1:case CimType.UInt8:|}
                    case CimType.SInt8:
                    case CimType.SInt16:
                    case CimType.UInt16:
                    case CimType.SInt32:
                        arrayToAdd.Add(System.Convert.ToInt32(
                                         numericValue,
                                         (IFormatProvider)CultureInfo.InvariantCulture
                                                                     .GetFormat(typeof(int))));
                        retFunctionName = "ToInt32";
                        enumType = "System.Int32";
                        break;

                    {|#0:case CimType.UInt32:|}
                        arrayToAdd.Add(System.Convert.ToInt32(
                                         numericValue,
                                         (IFormatProvider)CultureInfo.InvariantCulture
                                                                     .GetFormat(typeof(int))));
                        retFunctionName = "ToInt32";
                        enumType = "System.Int32";
                        break;
                }

                return retFunctionName;
            }
        }
        """,
        Duplicate("CimType.UInt8"));

    [Fact]
    public Task Stride_WComponentWritesZ() => Verify.VerifyAsync(
        """
        using System;
        using System.Numerics;

        public enum VectorComponent { X, Y, Z, W }

        public sealed class RotationComponentCurveViewModel
        {
            public VectorComponent Component { get; set; }

            public Vector4 AddPoint(Vector4 value, Vector2 realPoint)
            {
                switch (Component)
                {
                    case VectorComponent.X:
                        value.X = realPoint.Y;
                        break;

                    case VectorComponent.Y:
                        value.Y = realPoint.Y;
                        break;

                    {|#1:case VectorComponent.Z:|}
                        value.Z = realPoint.Y;
                        break;

                    {|#0:case VectorComponent.W:|}
                        value.Z = realPoint.Y;
                        break;

                    default:
                        throw new NotSupportedException();
                }

                return value;
            }
        }
        """,
        Duplicate("VectorComponent.Z"));

    [Fact]
    public Task QuantConnect_OpenInterestTickReturnsTheSameProperties() => Verify.VerifyAsync(
        """
        using System;

        public enum SecuritySeedData { None, OpenInterest, OpenInterestTick, TradeTick, Fundamentals }

        public static class SecurityCacheTests
        {
            public static string[] GetPropertiesBy(SecuritySeedData type)
            {
                switch (type)
                {
                    {|#3:case SecuritySeedData.None:|}
                        return new string[0];

                    {|#1:case SecuritySeedData.OpenInterest:|}
                        return new[] { "OpenInterest" };

                    {|#0:case SecuritySeedData.OpenInterestTick:|}
                        return new[] { "OpenInterest" };

                    case SecuritySeedData.TradeTick:
                        return new[] { "Price", "Volume" };

                    {|#2:case SecuritySeedData.Fundamentals:|}
                        return new string[0];

                    default:
                        throw new ArgumentOutOfRangeException(nameof(type), type, null);
                }
            }
        }
        """,
        Duplicate("SecuritySeedData.OpenInterest"),
        Duplicate("SecuritySeedData.None", marker: 2));

    [Fact]
    public Task AkkaNet_DecrementCaseIncrementsLikeIncrement() => Verify.VerifyAsync(
        """
        using System;

        public abstract class CounterBase
        {
            protected abstract void Persist(object message, Action<object> handler);

            private int _value;

            protected void OnCommand(object message)
            {
                switch (message as string)
                {
                    {|#1:case "inc":|}
                        _value++;
                        Persist(message, _ => { });
                        break;

                    {|#0:case "dec":|}
                        _value++;
                        Persist(message, _ => { });
                        break;

                    default:
                        return;
                }
            }
        }
        """,
        Duplicate("\"inc\""));

    [Fact]
    public Task AkkaNet_ReplaceModeUsesThePrependLambda() => Verify.VerifyAsync(
        """
        using System;

        public enum AddMode { Append, Prepend, Replace }

        public sealed class Config
        {
            public Config WithFallback(Config other) => this;
        }

        public sealed class Builder
        {
            private Config _configuration = new();

            private Builder Add(Func<Config, Config, Config> configurator, Config hocon)
            {
                _configuration = configurator(_configuration, hocon);
                return this;
            }

            public Builder Add(Config hocon, AddMode mode) => mode switch
            {
                AddMode.Append => Add((config, add) => config.WithFallback(add), hocon),
                AddMode.Prepend => {|#1:Add((config, add) => add.WithFallback(config), hocon)|},
                AddMode.Replace => {|#0:Add((config, add) => add.WithFallback(config), hocon)|},
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
        }
        """,
        Duplicate("AddMode.Prepend"));
}
