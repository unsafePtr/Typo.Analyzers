using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.RepeatedAssignmentAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class RepeatedAssignmentAnalyzerTests
{
    // Markup: {|#0:...|} is the second write, {|#1:...|} the first one.
    private static DiagnosticResult Overwritten(string target, int second = 0, int first = 1) =>
        Verify.Diagnostic(Descriptors.OverwrittenBeforeUse).WithLocation(second).WithLocation(first).WithArguments(target);

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
    public Task GetOnlyPropertyAssignedTwiceInConstructor() => Verify.VerifyAsync(
        """
        public sealed class C
        {
            public C(int id)
            {
                {|#1:Id|} = id;
                {|#0:Id|} = id + 1;
            }

            public int Id { get; }
        }
        """,
        Overwritten("Id"));

    [Fact]
    public Task NoDiagnostics_ForSettersFromOtherAssembliesOrRefGetters() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public sealed class C
        {
            private readonly int[] _slots = new int[4];
            private int _next;

            public ref int Next => ref _slots[_next++];

            public void Fill()
            {
                Next = 1;
                Next = 2;
            }

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

    // The setters between the two writes read the guard flag.
    [Fact]
    public Task NoDiagnostics_WhenSettersBetweenMayReadTheTarget() => Verify.VerifyAsync(
        """
        public sealed class C
        {
            private bool _isLoadingData;
            private int _items;
            private int _selected;
            private int _last;

            public int Items
            {
                get => _items;
                set
                {
                    _items = value;
                    if (!_isLoadingData)
                    {
                        Refresh();
                    }
                }
            }

            public int Selected
            {
                get => _selected;
                set => _selected = _isLoadingData ? _selected : value;
            }

            private void Refresh()
            {
            }

            public void Load(int items)
            {
                _isLoadingData = true;
                Items = items;
                _isLoadingData = false;
            }

            public void Increment()
            {
                _isLoadingData = true;
                _last = Selected++;
                _isLoadingData = false;
            }

            public void Compound(int delta)
            {
                _isLoadingData = true;
                _last = Selected += delta;
                _isLoadingData = false;
            }

            public void SetterFromOtherAssembly(System.Text.StringBuilder builder)
            {
                _isLoadingData = true;
                builder.Length = 0;
                _isLoadingData = false;
            }
        }
        """);

    // 'segment = segment.Next' moves the second write to another object.
    [Fact]
    public Task NoDiagnostics_WhenReceiverOrIndexChangesBetween() => Verify.VerifyAsync(
        """
        public sealed class Segment
        {
            public bool Processed;
            public Segment Next;
        }

        public static class C
        {
            public static void Walk(Segment segment)
            {
                segment.Processed = true;
                segment = segment.Next;
                segment.Processed = true;
            }

            public static void Fill(int[] values, int i)
            {
                values[i] = 1;
                i = i + 1;
                values[i] = 2;
            }

            public static void Swap(int[] values, int[] other)
            {
                values[0] = 1;
                values = other;
                values[0] = 2;
            }
        }
        """);

    [Fact]
    public Task ReceiverReadButNotWrittenBetweenStillReports() => Verify.VerifyAsync(
        """
        public sealed class Segment
        {
            public bool Processed;
            public Segment Next;
        }

        public static class C
        {
            private static Segment s_next;

            public static Segment Walk(Segment segment)
            {
                {|#1:segment.Processed|} = true;
                s_next = segment.Next;
                {|#0:segment.Processed|} = true;
                return s_next;
            }
        }
        """,
        Overwritten("segment.Processed"));

    [Fact]
    public Task InertSetterBetweenStillReports() => Verify.VerifyAsync(
        """
        public sealed class C
        {
            private int _count;
            private int _size;
            private int _notified;
            private int _guarded;

            public event System.EventHandler Changed;

            public int Size
            {
                get => _size;
                set => _size = System.Math.Max(0, value);
            }

            public int Notified
            {
                get => _notified;
                set
                {
                    _notified = value;
                    Changed?.Invoke(this, System.EventArgs.Empty);
                }
            }

            public int Guarded
            {
                get => _guarded;
                set => _guarded = _count > 0 ? value : 0;
            }

            public int Chained
            {
                get => _size;
                set => Notified = value;
            }

            public void Inert(int a)
            {
                {|#1:_count|} = 0;
                Size = a;
                _notified = Size += a;
                {|#0:_count|} = 1;
            }

            public void Notifying(int a)
            {
                _count = 0;
                Notified = a;
                _count = 1;
            }

            public void ReadingTheTarget(int a)
            {
                _count = 0;
                Guarded = a;
                _count = 1;
            }

            public void RunningAnotherSetter(int a)
            {
                _count = 0;
                Chained = a;
                _count = 1;
            }
        }
        """,
        Overwritten("_count"));

    [Fact]
    public Task AutoPropertiesAndInitializersBetweenStillReport() => Verify.VerifyAsync(
        """
        public sealed class Holder
        {
            public int Size { get; set; }
            public System.Collections.Generic.List<int> Items { get; set; } = new();
        }

        public sealed class C
        {
            private int _count;
            private int _other;

            public int X { get; set; }

            public int Other => _other;

            public void AutoBetween(int a)
            {
                {|#1:_count|} = 0;
                X = a;
                _other = X++;
                {|#0:_count|} = 1;
            }

            public void InitializerBetween(Holder holder, int a)
            {
                {|#3:_count|} = 0;
                holder = new Holder { Size = a };
                {|#2:_count|} = 1;
            }

            public void IndexerOfListedCollection(System.Collections.Generic.Dictionary<string, int> map, int a)
            {
                {|#5:_count|} = 0;
                map["a"] = a;
                {|#4:_count|} = 1;
            }
        }
        """,
        Overwritten("_count"),
        Overwritten("_count", 2, 3),
        Overwritten("_count", 4, 5));

    [Fact]
    public Task CopyConstructorReadsTheOtherObject() => Verify.VerifyAsync(
        """
        public sealed class Range
        {
            private int _start;
            private int _end;

            public Range(Range other)
            {
                {|#1:_start|} = other._start;
                _end = other._end;
                {|#0:_start|} = other._start + 1;
            }

            public int Length => _end - _start;
        }
        """,
        Overwritten("_start"));

    [Fact]
    public Task NoDiagnostics_WhenTheOtherObjectMayBeThisOne() => Verify.VerifyAsync(
        """
        using System.Collections;

        public struct Cursor
        {
            private int _position;

            public Cursor(ref Cursor other)
            {
                _position = 0;
                _position = other._position + 1;
            }

            public int Position => _position;
        }

        public sealed class Range
        {
            private int _start;
            private int _end;
            private Bag _bag = new();

            public Range()
            {
            }

            public int Length => _end - _start;

            public void CopyFrom(Range other)
            {
                _start = 0;
                _start = other._start + 1;
            }

            public Range(Range other, int value)
            {
                _start = value;
                _bag = new Bag { this };
                _start = other._start;
            }
        }

        public sealed class Bag : IEnumerable
        {
            public void Add(Range range)
            {
            }

            public IEnumerator GetEnumerator() => null;
        }
        """);

    [Fact]
    public Task QuantConnect_SessionIdCopiedAgainAfterOtherFields() => Verify.VerifyAsync(
        """
        using System;

        public sealed class BacktestNodePacket
        {
            public string SessionId = "";
            public int CompileId;
            public string Channel = "";
            public string BacktestId = "";
            public string OptimizationId = "";
            public string Name = "";
            public int UserId;
            public int ProjectId;
            public object TradeableDates;
        }

        public sealed class BacktestResult
        {
        }

        public static class Log
        {
            public static void Error(Exception err)
            {
            }
        }

        public sealed class BacktestResultPacket
        {
            public decimal Progress;
            public string SessionId = "";
            public DateTime PeriodFinish;
            public DateTime PeriodStart;
            public int CompileId;
            public string Channel = "";
            public string BacktestId = "";
            public string OptimizationId = "";
            public BacktestResult Results;
            public string Name = "";
            public int UserId;
            public int ProjectId;
            public object TradeableDates;

            public BacktestResultPacket(BacktestNodePacket job, BacktestResult results, DateTime endDate, DateTime startDate, decimal progress = 1m)
            {
                try
                {
                    Progress = Math.Round(progress, 3);
                    {|#1:SessionId|} = job.SessionId;
                    PeriodFinish = endDate;
                    PeriodStart = startDate;
                    CompileId = job.CompileId;
                    Channel = job.Channel;
                    BacktestId = job.BacktestId;
                    OptimizationId = job.OptimizationId;
                    Results = results;
                    Name = job.Name;
                    UserId = job.UserId;
                    ProjectId = job.ProjectId;
                    {|#0:SessionId|} = job.SessionId;
                    TradeableDates = job.TradeableDates;
                }
                catch (Exception err)
                {
                    Log.Error(err);
                }
            }
        }
        """,
        Overwritten("SessionId"));

    [Fact]
    public Task WolvenKit_SameUnknownFieldReadTwice() => Verify.VerifyAsync(
        """
        using System.IO;

        public sealed class CgiData
        {
            public uint Uk1 { get; set; }
            public uint Uk2 { get; set; }
            public uint Uk3 { get; set; }
            public uint Uk4 { get; set; }
            public uint Uk5 { get; set; }
            public uint Uk6 { get; set; }
        }

        public sealed class CgiDataReader
        {
            private readonly BinaryReader _reader;

            public CgiDataReader(BinaryReader reader) => _reader = reader;

            public CgiData ReadBuffer()
            {
                var data = new CgiData();
                data.Uk1 = _reader.ReadUInt32();
                data.Uk2 = _reader.ReadUInt32();
                data.Uk3 = _reader.ReadUInt32();
                var numBrck = _reader.ReadUInt32();
                var numSurf = _reader.ReadUInt32();
                var numProb = _reader.ReadUInt32();
                var numFact = _reader.ReadUInt32();
                var numTetr = _reader.ReadUInt32();
                data.Uk4 = _reader.ReadUInt32();
                {|#1:data.Uk5|} = _reader.ReadUInt32();
                {|#0:data.Uk5|} = _reader.ReadUInt32();
                return data;
            }
        }
        """,
        Overwritten("data.Uk5"));

    [Fact]
    public Task BtcPay_StoreNameCopiedTwice() => Verify.VerifyAsync(
        """
        public sealed class StoreBaseData
        {
            public string Name { get; set; }
            public string Website { get; set; }
            public int SpeedPolicy { get; set; }
        }

        public sealed class StoreData
        {
            public string StoreName { get; set; }
            public string StoreWebsite { get; set; }
            public int SpeedPolicy { get; set; }

            public object GetStoreBlob() => new();

            public void SetDefaultPaymentId(object paymentId)
            {
            }
        }

        public sealed class GreenfieldStoresController
        {
            private void ToModel(StoreBaseData restModel, StoreData model, object defaultPaymentMethod)
            {
                var blob = model.GetStoreBlob();
                {|#1:model.StoreName|} = restModel.Name;
                {|#0:model.StoreName|} = restModel.Name;
                model.StoreWebsite = restModel.Website;
                model.SpeedPolicy = restModel.SpeedPolicy;
                model.SetDefaultPaymentId(defaultPaymentMethod);
            }
        }
        """,
        Overwritten("model.StoreName"));

    [Fact]
    public Task Bitwarden_AmountDueReplacedByTotal() => Verify.VerifyAsync(
        """
        using System;

        public sealed class Invoice
        {
            public long AmountDue { get; set; }
            public DateTime Created { get; set; }
            public string HostedInvoiceUrl { get; set; }
            public string InvoicePdf { get; set; }
            public string Number { get; set; }
            public bool Paid { get; set; }
            public long Total { get; set; }
        }

        public class BillingInvoice
        {
            public BillingInvoice(Invoice inv)
            {
                {|#1:Amount|} = inv.AmountDue / 100M;
                Date = inv.Created;
                Url = inv.HostedInvoiceUrl;
                PdfUrl = inv.InvoicePdf;
                Number = inv.Number;
                Paid = inv.Paid;
                {|#0:Amount|} = inv.Total / 100M;
            }

            public decimal Amount { get; set; }
            public DateTime? Date { get; set; }
            public string Url { get; set; }
            public string PdfUrl { get; set; }
            public string Number { get; set; }
            public bool Paid { get; set; }
        }
        """,
        Overwritten("Amount"));

    [Fact]
    public Task DotNetNuke_InsertPositionRowShownTwice() => Verify.VerifyAsync(
        """
        public sealed class HtmlGenericControl
        {
            public bool Visible { get; set; }
        }

        public sealed class RadioButtonList
        {
            public int SelectedIndex { get; set; }
        }

        public sealed class Import
        {
            private readonly HtmlGenericControl divTabName = new();
            private readonly HtmlGenericControl divParentTab = new();
            private readonly HtmlGenericControl divInsertPositionRow = new();
            private readonly RadioButtonList optMode = new();

            private void DisplayNewRows()
            {
                this.divTabName.Visible = this.optMode.SelectedIndex == 0;
                this.divParentTab.Visible = this.optMode.SelectedIndex == 0;
                {|#1:this.divInsertPositionRow.Visible|} = this.optMode.SelectedIndex == 0;
                {|#0:this.divInsertPositionRow.Visible|} = this.optMode.SelectedIndex == 0;
            }
        }
        """,
        Overwritten("this.divInsertPositionRow.Visible"));

    [Fact]
    public Task DotNetNuke_PhysicalPathResetAfterOtherFields() => Verify.VerifyAsync(
        """
        public static class Constants
        {
            public const int CONTENT_ValidPortalId = 0;
            public const string FOLDER_ValidFolderRelativePath = "folder/";
            public const string FOLDER_ValidFolderPath = "C:\\folder";
            public const int FOLDER_ValidFolderMappingID = 1;
            public const int FOLDER_ValidFolderId = 2;
        }

        internal sealed class FolderInfoBuilder
        {
            private int portalId;
            private string folderPath;
            private string physicalPath;
            private int folderMappingID;
            private int folderId;

            internal FolderInfoBuilder()
            {
                this.portalId = Constants.CONTENT_ValidPortalId;
                this.folderPath = Constants.FOLDER_ValidFolderRelativePath;
                {|#1:this.physicalPath|} = Constants.FOLDER_ValidFolderPath;
                this.folderMappingID = Constants.FOLDER_ValidFolderMappingID;
                this.folderId = Constants.FOLDER_ValidFolderId;
                {|#0:this.physicalPath|} = string.Empty;
            }

            internal string Build() => $"{this.portalId}{this.folderPath}{this.physicalPath}{this.folderMappingID}{this.folderId}";
        }
        """,
        Overwritten("this.physicalPath"));

    [Fact]
    public Task LinqToDb_StopClearedThenSetFromParameter() => Verify.VerifyAsync(
        """
        using System.Linq.Expressions;

        public struct TransformInfo
        {
            public TransformInfo(Expression expression, bool stop, bool @continue)
            {
                Expression = expression;
                {|#1:Stop|}       = false;
                {|#0:Stop|}       = stop;
                Continue   = @continue;
            }

            public Expression Expression;
            public bool Stop;
            public bool Continue;
        }
        """,
        Overwritten("Stop"));

    [Fact]
    public Task PeachPie_NanosecondTimesOverwrittenBySeconds() => Verify.VerifyAsync(
        """
        public enum FileModeFlags
        {
            None = 0,
        }

        public struct Stat
        {
            public ulong st_dev;
            public ulong st_ino;
            public uint st_mode;
            public ulong st_nlink;
            public uint st_uid;
            public uint st_gid;
            public ulong st_rdev;
            public long st_size;
            public long st_atime;
            public long st_mtime;
            public long st_ctime;
            public long st_atime_nsec;
            public long st_mtime_nsec;
            public long st_ctime_nsec;
        }

        internal struct StatStruct
        {
            public uint st_dev;
            public ushort st_ino;
            public FileModeFlags st_mode;
            public short st_nlink;
            public short st_uid;
            public short st_gid;
            public uint st_rdev;
            public long st_size;
            public long st_atime;
            public long st_mtime;
            public long st_ctime;

            internal StatStruct(Stat stat)
            {
                st_dev = (uint)stat.st_dev;
                {|#1:st_ctime|} = stat.st_ctime_nsec;
                {|#3:st_mtime|} = stat.st_mtime_nsec;
                {|#5:st_atime|} = stat.st_atime_nsec;
                {|#0:st_ctime|} = stat.st_ctime;
                {|#4:st_atime|} = stat.st_atime;
                {|#2:st_mtime|} = stat.st_mtime;
                st_rdev = (uint)stat.st_rdev;
                st_gid = (short)stat.st_gid;
                st_uid = (short)stat.st_uid;
                st_nlink = (short)stat.st_nlink;
                st_mode = (FileModeFlags)stat.st_mode;
                st_ino = (ushort)stat.st_ino;
                st_size = stat.st_size;
            }
        }
        """,
        Overwritten("st_ctime"),
        Overwritten("st_mtime", second: 2, first: 3),
        Overwritten("st_atime", second: 4, first: 5));

    [Fact]
    public Task Emby_ChaptersCreatedTwice() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public sealed class ProgramChain
        {
        }

        public sealed class Chapter
        {
        }

        public sealed class Title
        {
            public List<ProgramChain> ProgramChains { get; set; }
            public List<Chapter> Chapters { get; set; }
            public uint TitleNumber { get; private set; }

            public Title(uint titleNum)
            {
                ProgramChains = new List<ProgramChain>();
                {|#1:Chapters|} = new List<Chapter>();
                {|#0:Chapters|} = new List<Chapter>();
                TitleNumber = titleNum;
            }
        }
        """,
        Overwritten("Chapters"));

    [Fact]
    public Task Unity_StaticFlagSetThroughTypeAndSimpleName() => Verify.VerifyAsync(
        """
        internal class Weaver
        {
            public static bool fail;

            public static bool IsValidTypeToGenerate(bool invalid)
            {
                if (invalid)
                {
                    {|#1:Weaver.fail|} = true;
                    {|#0:fail|} = true;
                    return false;
                }

                return true;
            }
        }
        """,
        Overwritten("fail"));

    [Fact]
    public Task PascalAbc_CompileUnitStoredTwice() => Verify.VerifyAsync(
        """
        public sealed class CodeCompileUnit
        {
        }

        public sealed class DesignSurface
        {
        }

        public sealed class CodeGen
        {
            public CodeCompileUnit GetCodeCompileUnit(object host) => new();
        }

        public class CodeDomHostLoader
        {
            private CodeCompileUnit codeCompileUnit = null;
            private DesignSurface Designer;

            public DesignSurface Surface => Designer;

            protected CodeCompileUnit Parse(CodeGen cg, object idh)
            {
                CodeCompileUnit ccu = null;
                DesignSurface ds = new DesignSurface();
                ccu = cg.GetCodeCompileUnit(idh);
                {|#1:codeCompileUnit|} = ccu;
                Designer = ds;
                {|#0:codeCompileUnit|} = ccu;
                return codeCompileUnit;
            }
        }
        """,
        Overwritten("codeCompileUnit"));

    [Fact]
    public Task GitExtensions_ExceptionPointersClearedThenSet() => Verify.VerifyAsync(
        """
        using System;
        using System.Runtime.InteropServices;

        public struct MINIDUMP_EXCEPTION_INFORMATION
        {
            public uint ThreadId;
            public IntPtr ExceptionPointers;
            public bool ClientPointers;
        }

        internal static class DumpWriter
        {
            [DllImport("kernel32.dll")]
            private static extern uint GetCurrentThreadId();

            private static MINIDUMP_EXCEPTION_INFORMATION Write()
            {
                MINIDUMP_EXCEPTION_INFORMATION exp;
                exp.ThreadId = GetCurrentThreadId();
                exp.ClientPointers = false;
                {|#1:exp.ExceptionPointers|} = IntPtr.Zero;
                {|#0:exp.ExceptionPointers|} = Marshal.GetExceptionPointers();
                return exp;
            }
        }
        """,
        Overwritten("exp.ExceptionPointers"));

    [Fact]
    public Task NasaWorldWind_ZCopiedIntoX() => Verify.VerifyAsync(
        """
        public class Point3d
        {
            public double X;
            public double Y;
            public double Z;

            public Point3d(Point3d P)
            {
                {|#1:X|} = P.X;
                Y = P.Y;
                {|#0:X|} = P.Z;
            }
        }
        """,
        Overwritten("X"));

    [Fact]
    public Task SharpDevelop_ComparisonResultReplacedByTrue() => Verify.VerifyAsync(
        """
        public sealed class IdentifierExpression
        {
            public string Identifier { get; set; }
        }

        public sealed class VariableReferenceVisitor
        {
            private readonly string _varName;
            private bool foundInvocations;

            public VariableReferenceVisitor(string varName) => _varName = varName;

            public bool FoundInvocations => foundInvocations;

            public void VisitInvocationExpression(IdentifierExpression idExpression)
            {
                {|#1:foundInvocations|} = (idExpression.Identifier == _varName);
                {|#0:foundInvocations|} = true;
            }
        }
        """,
        Overwritten("foundInvocations"));

    [Fact]
    public Task Stride_AtlasNameReplacedByEmpty() => Verify.VerifyAsync(
        """
        public class TexImage
        {
            public string Name { get; set; }
        }

        public sealed class TexAtlas : TexImage
        {
            public TexAtlas(TexImage atlas)
            {
                {|#1:Name|} = atlas.Name;
                {|#0:Name|} = "";
            }
        }
        """,
        Overwritten("Name"));

    [Fact]
    public Task Garnet_LastVotedEpochTakesCurrentEpochFirst() => Verify.VerifyAsync(
        """
        using System;

        public struct Worker
        {
            public string address;
            public int port;
            public string nodeid;
            public long configEpoch;
            public long lastVotedConfigEpoch;
            public int role;
            public string replicaOfNodeId;
            public long replicationOffset;
            public string hostname;
        }

        public sealed class ClusterConfig
        {
            private readonly object slotMap;
            private readonly Worker[] workers;

            public ClusterConfig(object slotMap, Worker[] workers)
            {
                this.slotMap = slotMap;
                this.workers = workers;
            }

            public ClusterConfig InitializeLocalWorker(string nodeId, string address, int port, long configEpoch, long currentConfigEpoch, long lastVotedConfigEpoch, int role, string replicaOfNodeId, string hostname)
            {
                Worker[] newWorkers = new Worker[workers.Length];
                Array.Copy(workers, newWorkers, workers.Length);
                newWorkers[1].address = address;
                newWorkers[1].port = port;
                newWorkers[1].nodeid = nodeId;
                newWorkers[1].configEpoch = configEpoch;
                {|#1:newWorkers[1].lastVotedConfigEpoch|} = currentConfigEpoch;
                {|#0:newWorkers[1].lastVotedConfigEpoch|} = lastVotedConfigEpoch;
                newWorkers[1].role = role;
                newWorkers[1].replicaOfNodeId = replicaOfNodeId;
                newWorkers[1].replicationOffset = 0;
                newWorkers[1].hostname = hostname;
                return new ClusterConfig(slotMap, newWorkers);
            }
        }
        """,
        Overwritten("newWorkers[1].lastVotedConfigEpoch"));

    [Fact]
    public Task Files_WidthOverwrittenWithHeight() => Verify.VerifyAsync(
        """
        public class FrameworkElement
        {
            public double Width { get; set; }
            public double Height { get; set; }
        }

        public sealed class Path : FrameworkElement
        {
            public object Data { get; set; }
        }

        public sealed class ThemedIconLayer
        {
            private object GetTemplateChild(string name) => new Path();

            private void SetPathData(object geometry, FrameworkElement element)
            {
                if (GetTemplateChild("LayerPathPart") is Path path)
                {
                    path.Data = geometry;
                    {|#1:path.Width|} = element.Width;
                    {|#0:path.Width|} = element.Height;
                }
            }
        }
        """,
        Overwritten("path.Width"));

    [Fact]
    public Task MSBuild_InheritedFlagCopiedTwice() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        internal sealed class ProjectInstance
        {
            private bool _explicitToolsVersionSpecified;
            private Dictionary<string, string> _properties = new();
            private Dictionary<string, string> _items = new();
            private HashSet<string> _actualTargets = new();
            private Dictionary<string, string> _environmentVariableProperties = new();

            internal ProjectInstance(ProjectInstance projectToInheritFrom)
            {
                {|#1:_explicitToolsVersionSpecified|} = projectToInheritFrom._explicitToolsVersionSpecified;
                _properties = new Dictionary<string, string>(projectToInheritFrom._properties);
                _items = new Dictionary<string, string>(projectToInheritFrom._items);
                _actualTargets = new HashSet<string>(projectToInheritFrom._actualTargets);
                _environmentVariableProperties = new Dictionary<string, string>(projectToInheritFrom._environmentVariableProperties);
                {|#0:_explicitToolsVersionSpecified|} = projectToInheritFrom._explicitToolsVersionSpecified;
            }
        }
        """,
        Overwritten("_explicitToolsVersionSpecified"));

    [Fact]
    public Task CodeContracts_CopiedMonitorFlagResetAfterSymbols() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public sealed class Variable
        {
        }

        internal sealed class Data
        {
            public bool IsReached;
            public bool InsideMonitor;
            private readonly List<Variable> symbols = new();

            public Data()
            {
            }

            private Data(Data state, Variable v)
            {
                this.IsReached = state.IsReached;
                {|#1:this.InsideMonitor|} = state.InsideMonitor;
                this.symbols = new List<Variable>(state.symbols) { v };
                {|#0:this.InsideMonitor|} = false;
            }

            public Data With(Variable v) => new(this, v);
        }
        """,
        Overwritten("this.InsideMonitor"));
}
