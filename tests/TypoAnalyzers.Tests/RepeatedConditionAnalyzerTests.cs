using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.RepeatedConditionAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class RepeatedConditionAnalyzerTests
{
    // Markup: {|#0:...|} is the reported condition, {|#1:...|} the earlier condition it repeats.
    private static DiagnosticResult Expect(DiagnosticDescriptor descriptor, params object[] arguments) =>
        Verify.Diagnostic(descriptor).WithLocation(0).WithLocation(1).WithArguments(arguments);

    [Fact]
    public Task ElseIfRepeatsEarlierCondition() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(int a)
            {
                if ({|#1:a == 1|})
                {
                    return 1;
                }
                else if (a == 2)
                {
                    return 2;
                }
                else if ({|#0:a == 1|})
                {
                    return 3;
                }

                return 0;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "a == 1"));

    [Fact]
    public Task ElseIfRepeatsDisjunctOfEarlierCondition() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(int a)
            {
                if ({|#1:a == 1 || a == 2|})
                {
                    return 1;
                }
                else if ({|#0:a == 2|})
                {
                    return 2;
                }

                return 0;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "a == 2"));

    [Fact]
    public Task DotNet10_AdjacentIfsWithSameCondition() => Verify.VerifyAsync(
        """
        public struct Descriptor
        {
            public int Kind0;
            public int Kind1;
        }

        public static class C
        {
            public static int Size(Descriptor descriptor)
            {
                var size = 0;
                if ({|#1:descriptor.Kind0 == 1|})
                {
                    size += 1;
                }

                if ({|#0:descriptor.Kind0 == 1|})
                {
                    size += 2;
                }

                return size;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "descriptor.Kind0 == 1"));

    [Fact]
    public Task RepoDb_SecondCheckGuardsWrongCollection() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;

        public sealed class UpdateAllRequest
        {
            public IEnumerable<string> Fields { get; set; }
            public IEnumerable<string> Qualifiers { get; set; }

            public override int GetHashCode()
            {
                var hash = 0;
                if ({|#1:Fields != null|})
                {
                    foreach (var field in Fields)
                    {
                        hash = HashCode.Combine(hash, field);
                    }
                }

                if ({|#0:Fields != null|})
                {
                    foreach (var field in Qualifiers)
                    {
                        hash = HashCode.Combine(hash, field);
                    }
                }

                return hash;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "Fields != null"));

    [Fact]
    public Task Nethermind_AdjacentIfsAfterThrow() => Verify.VerifyAsync(
        """
        public sealed class Buffer
        {
            public int ReadableBytes { get; set; }
        }

        public static class C
        {
            public static void Check(Buffer buffer)
            {
                if ({|#1:buffer.ReadableBytes > 0|})
                {
                    throw new System.Exception("first");
                }

                if ({|#0:buffer.ReadableBytes > 0|})
                {
                    throw new System.Exception("second");
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "buffer.ReadableBytes > 0"));

    [Fact]
    public Task Nethermind_NestedRecurringCheck() => Verify.VerifyAsync(
        """
        public sealed class Logger
        {
            public bool IsInfo { get; set; }

            public void Info(string message)
            {
            }
        }

        public static class C
        {
            public static void Log(Logger logger)
            {
                if ({|#1:logger.IsInfo|})
                {
                    if ({|#0:logger.IsInfo|})
                    {
                        logger.Info("x");
                    }
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "logger.IsInfo", "true"));

    [Fact]
    public Task NestedCheckInElseIsAlwaysFalse() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string x)
            {
                if ({|#1:x == null|})
                {
                    return 0;
                }
                else
                {
                    if ({|#0:x == null|})
                    {
                        return 1;
                    }
                }

                return 2;
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "x == null", "false"));

    [Fact]
    public Task Runtime_DoubleCheckedLockingIsSilent() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static readonly object s_lock = new();
            private static object s_value;
            private static object Value { get; set; }

            public static object Get()
            {
                if (s_value == null)
                {
                    lock (s_lock)
                    {
                        if (s_value == null)
                        {
                            s_value = new object();
                        }
                    }
                }

                if (Value == null)
                {
                    lock (s_lock)
                    {
                        if (Value == null)
                        {
                            Value = new object();
                        }
                    }
                }

                return s_value;
            }
        }
        """);

    [Fact]
    public Task LockDoesNotHideRepeatedLocalCheck() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static readonly object s_lock = new();

            public static int M(string x)
            {
                if ({|#1:x == null|})
                {
                    lock (s_lock)
                    {
                        if ({|#0:x == null|})
                        {
                            return 1;
                        }
                    }
                }

                return 2;
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "x == null", "true"));

    [Fact]
    public Task LockInsideBothChecksStillReports() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static readonly object s_lock = new();
            private static object s_value;

            public static int M()
            {
                lock (s_lock)
                {
                    if ({|#1:s_value == null|})
                    {
                        if ({|#0:s_value == null|})
                        {
                            return 1;
                        }
                    }
                }

                return 2;
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "s_value == null", "true"));

    [Fact]
    public Task NestedCheckRepeatsConjunct() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string x, int y)
            {
                if ({|#1:x != null|} && y > 0)
                {
                    if ({|#0:x != null|})
                    {
                        return 1;
                    }
                }

                return 0;
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "x != null", "true"));

    [Fact]
    public Task ElseIfRepeatsNullPattern() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string value)
            {
                if ({|#1:value is null|})
                {
                    return 0;
                }
                else if (value.Length == 0)
                {
                    return 1;
                }
                else if ({|#0:value is null|})
                {
                    return 2;
                }

                return 3;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "value is null"));

    [Fact]
    public Task NestedOppositeCheckIsAlwaysFalse() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string s)
            {
                if ({|#1:s == null|})
                {
                    if ({|#0:s != null|})
                    {
                        return 1;
                    }
                }

                return 0;
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "s != null", "false"));

    [Fact]
    public Task NestedOppositeTypeTestInElseIsAlwaysTrue() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(object o)
            {
                if ({|#1:o is string|})
                {
                    return 0;
                }
                else
                {
                    if ({|#0:o is not string|})
                    {
                        return 1;
                    }
                }

                return 2;
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "o is not string", "true"));

    [Fact]
    public Task ElseIfRepeatsNullTestInAnotherSpelling() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string value)
            {
                if ({|#1:value == null|})
                {
                    return 0;
                }
                else if ({|#0:value is null|})
                {
                    return 1;
                }

                return 2;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "value is null"));

    [Fact]
    public Task AdjacentNullTestsInDifferentSpellings() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string value)
            {
                if ({|#1:value is null|})
                {
                    return 0;
                }

                if ({|#0:value == null|})
                {
                    return 1;
                }

                return 2;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "value == null"));

    [Fact]
    public Task CallsCannotChangeUncapturedLocals() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static bool Compute() => true;

            public static void M()
            {
                var ready = Compute();
                if ({|#1:!ready|})
                {
                    System.Console.WriteLine("a");
                }

                if ({|#0:!ready|})
                {
                    System.Console.WriteLine("b");
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "!ready"));

    [Fact]
    public Task NoDiagnostics_WhenClosureChangesCapturedLocal() => Verify.VerifyAsync(
        """
        using System;

        public static class C
        {
            public static int AdjacentWithLocalFunction()
            {
                var ready = false;
                void Prepare()
                {
                    ready = true;
                }

                if (!ready)
                {
                    Prepare();
                }

                if (!ready)
                {
                    return 1;
                }

                return 0;
            }

            public static int NestedWithLocalFunction()
            {
                var ready = false;
                void Prepare()
                {
                    ready = true;
                }

                if (!ready)
                {
                    Prepare();
                    if (!ready)
                    {
                        return 2;
                    }
                }

                return 0;
            }

            public static int AdjacentWithLambda()
            {
                var count = 0;
                Action increment = () => count++;
                if (count == 0)
                {
                    increment();
                }

                if (count == 0)
                {
                    return 1;
                }

                return 0;
            }

            public static void InsideLambda(bool flag)
            {
                if (flag)
                {
                    Action action = () =>
                    {
                        if (flag)
                        {
                            Console.WriteLine();
                        }
                    };
                    action();
                }
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_ForUnrelatedOrImpureConditions() => Verify.VerifyAsync(
        """
        public static class C
        {
            private static int s_position;

            private static bool Advance() => ++s_position > 10;

            public static int Unrelated(int a, int b)
            {
                if (a > 0)
                {
                    if (b > 0)
                    {
                        return 1;
                    }
                }

                return 0;
            }

            public static int ImpureBetween(int a)
            {
                if (a == 1)
                {
                    return 1;
                }
                else if (Advance())
                {
                    return 2;
                }
                else if (a == 1)
                {
                    return 3;
                }

                return 0;
            }

            public static int DisjunctionEarlierWithoutMatch(int a)
            {
                if (a == 1 || a == 2)
                {
                    return 1;
                }
                else if (a == 3)
                {
                    return 2;
                }

                return 0;
            }

            public static int ConjunctionEarlier(int a, int b)
            {
                if (a > 0 && b > 0)
                {
                    return 1;
                }
                else if (a > 1)
                {
                    return 2;
                }

                return 0;
            }

            public static int ImpureInner(int a)
            {
                if (a > 0)
                {
                    if (Advance())
                    {
                        return 1;
                    }
                }

                return 0;
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_WhenConditionCanChange() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;

        public static class C
        {
            private static bool Compute() => true;

            public static void Decrement(int count)
            {
                if (count > 0)
                {
                    count--;
                }

                if (count > 0)
                {
                    count = 0;
                }
            }

            public static void Drain(Queue<int> queue)
            {
                if (queue.Count > 0)
                {
                    queue.Dequeue();
                }

                if (queue.Count > 0)
                {
                    queue.Clear();
                }
            }

            public static void Upper(string text, char[] buffer)
            {
                if (buffer[0] == 'a')
                {
                    text.AsSpan().ToUpperInvariant(buffer);
                }

                if (buffer[0] == 'a')
                {
                    buffer[0] = 'b';
                }
            }

            public static void Recompute(bool flag)
            {
                if (flag)
                {
                    flag = Compute();
                    if (flag)
                    {
                        return;
                    }
                }
            }

            public static void Loop(int n)
            {
                if (n > 0)
                {
                    while (true)
                    {
                        if (n > 0)
                        {
                            n--;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            public static int Different(int a)
            {
                if (a == 1)
                {
                    return 1;
                }
                else if (a == 2)
                {
                    return 2;
                }

                return 0;
            }
        }
        """);

    [Fact]
    public Task NoDiagnostics_WhenLaterRepeatCanSeeAnotherValue() => Verify.VerifyAsync(
        """
        public sealed class Reader
        {
            private int _position;

            private void Advance()
            {
                _position++;
            }

            public int ChangedBetween(int count)
            {
                if (count == 0)
                {
                    return 0;
                }

                count = count - 1;
                if (count == 0)
                {
                    return 1;
                }

                return 2;
            }

            public int CallBetween()
            {
                if (_position == 0)
                {
                    return 0;
                }

                Advance();
                if (_position == 0)
                {
                    return 1;
                }

                return 2;
            }

            public int LabelBetween(int count, int total)
            {
                if (count == 0)
                {
                    return 0;
                }

            Retry:
                total++;
                if (count == 0)
                {
                    return total;
                }

                count--;
                goto Retry;
            }

            public int FirstDoesNotExit(int count, int total)
            {
                if (count == 0)
                {
                    total++;
                }

                total *= 2;
                if (count == 0)
                {
                    return total;
                }

                return 0;
            }
        }
        """);

    [Fact]
    public Task LinqToDb_NotEqualBranchRepeatedAfterDisjunction() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public enum Operator { Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual }

        public sealed class SqlPredicate
        {
            public Operator Operator { get; set; }

            public List<string> Reduce()
            {
                var conditions = new List<string>();
                if (Operator == Operator.Equal)
                {
                    conditions.Add("equal");
                }
                else
                if ({|#1:Operator == Operator.NotEqual|})
                {
                    conditions.Add("predicate");
                    conditions.Add("expr1 is not null");
                    conditions.Add("expr2 is null");
                }
                else
                if (Operator == Operator.LessOrEqual || Operator == Operator.GreaterOrEqual)
                {
                    conditions.Add("ordered");
                }
                else if ({|#0:Operator == Operator.NotEqual|})
                {
                    conditions.Add("predicate");
                    conditions.Add("expr1 is not null");
                }
                else
                {
                    conditions.Add("other");
                }

                return conditions;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "Operator == Operator.NotEqual"));

    [Fact]
    public Task Ryujinx_IndexerTestsTwoTwice() => Verify.VerifyAsync(
        """
        using System;

        public struct ZbcSetTableArguments
        {
            private uint element0;
            private uint element1;
            private uint element2;
            private uint element3;

            public uint this[int index]
            {
                get
                {
                    if (index == 0)
                    {
                        return element0;
                    }
                    else if (index == 1)
                    {
                        return element1;
                    }
                    else if ({|#1:index == 2|})
                    {
                        return element2;
                    }
                    else if ({|#0:index == 2|})
                    {
                        return element3;
                    }

                    throw new IndexOutOfRangeException();
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "index == 2"));

    [Fact]
    public Task AzurePowerShell_WorkloadTypeBranchCopied() => Verify.VerifyAsync(
        """
        using System;

        namespace Models
        {
            public enum WorkloadType { AzureVM, AzureSQLDatabase, AzureFiles }

            public enum BackupManagementType { AzureVM, AzureSql, AzureStorage }
        }

        public sealed class GetAzureRmRecoveryServicesBackupProtectionPolicy
        {
            public Models.WorkloadType WorkloadType { get; set; }

            public Models.BackupManagementType BackupManagementType { get; set; }

            public string ExecuteCmdlet()
            {
                string serviceClientProviderType = null;
                if (WorkloadType == Models.WorkloadType.AzureVM)
                {
                    serviceClientProviderType = "IaasVm";
                }
                else if ({|#1:WorkloadType == Models.WorkloadType.AzureFiles|})
                {
                    if (BackupManagementType != Models.BackupManagementType.AzureStorage)
                    {
                        throw new ArgumentException("Unsupported backup management type.");
                    }

                    serviceClientProviderType = "AzureFiles";
                }
                else if ({|#0:WorkloadType == Models.WorkloadType.AzureFiles|})
                {
                    if (BackupManagementType != Models.BackupManagementType.AzureStorage)
                    {
                        throw new ArgumentException("Unsupported backup management type.");
                    }

                    serviceClientProviderType = "AzureFiles";
                }

                return serviceClientProviderType;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "WorkloadType == Models.WorkloadType.AzureFiles"));

    [Fact]
    public Task CoreFx_TypeTestRepeatedAfterAnotherType() => Verify.VerifyAsync(
        """
        using System.Collections;
        using System.Xml;
        using System.Xml.Schema;

        public sealed class ImportContext
        {
            internal void Depends(XmlSchemaSimpleType item, ArrayList refs)
            {
                var content = item.Content;
                XmlSchemaType baseType = null;
                XmlQualifiedName baseName = XmlQualifiedName.Empty;
                if ({|#1:content is XmlSchemaSimpleTypeRestriction|})
                {
                    baseType = ((XmlSchemaSimpleTypeRestriction)content).BaseType;
                    baseName = ((XmlSchemaSimpleTypeRestriction)content).BaseTypeName;
                }
                else if (content is XmlSchemaSimpleTypeList)
                {
                    baseName = ((XmlSchemaSimpleTypeList)content).ItemTypeName;
                }
                else if ({|#0:content is XmlSchemaSimpleTypeRestriction|})
                {
                    baseName = ((XmlSchemaSimpleTypeRestriction)content).BaseTypeName;
                }

                refs.Add(baseType);
                refs.Add(baseName);
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "content is XmlSchemaSimpleTypeRestriction"));

    [Fact]
    public Task SubtitleEdit_ShadowNameTestedTwice() => Verify.VerifyAsync(
        """
        using System.Text;

        public sealed class SsaStyle
        {
            public string Name { get; set; }
            public decimal OutlineWidth { get; set; }
            public decimal ShadowWidth { get; set; }

            public string ToRawSsa(string styleFormat)
            {
                var sb = new StringBuilder();
                sb.Append("Style: ");
                var format = styleFormat.Split(',');
                for (int i = 0; i < format.Length; i++)
                {
                    string f = format[i].Trim();
                    if (f == "name")
                        sb.Append(Name);
                    else if ({|#1:f == "shadow"|})
                        sb.Append(OutlineWidth);
                    else if ({|#0:f == "shadow"|})
                        sb.Append(ShadowWidth);
                }

                return sb.ToString();
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "f == \"shadow\""));

    [Fact]
    public Task SubtitleEdit_BranchesShadowedByDisjunction() => Verify.VerifyAsync(
        """
        [System.Flags]
        public enum FontStyle { Regular = 0, Bold = 1, Italic = 2 }

        public sealed class Font
        {
            public Font(string family, FontStyle style)
            {
            }
        }

        public sealed class SsaStyle
        {
            public bool Bold { get; set; }
            public bool Italic { get; set; }
        }

        public sealed class ListViewSubItem
        {
            public Font Font { get; set; }
        }

        public static class SubStationAlphaStyles
        {
            public static void AddStyle(ListViewSubItem subItem, SsaStyle ssaStyle)
            {
                if ({|#1:ssaStyle.Bold || ssaStyle.Italic|})
                    subItem.Font = new Font("Arial", FontStyle.Bold | FontStyle.Italic);
                else if ({|#0:ssaStyle.Bold|})
                    subItem.Font = new Font("Arial", FontStyle.Bold);
                else if ({|#2:ssaStyle.Italic|})
                    subItem.Font = new Font("Arial", FontStyle.Italic);
                else if ({|#3:ssaStyle.Italic|})
                    subItem.Font = new Font("Arial", FontStyle.Regular);
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "ssaStyle.Bold"),
        Verify.Diagnostic(Descriptors.RepeatedElseIfCondition).WithLocation(2).WithLocation(1).WithArguments("ssaStyle.Italic"),
        Verify.Diagnostic(Descriptors.RepeatedElseIfCondition).WithLocation(3).WithLocation(2).WithArguments("ssaStyle.Italic"));

    [Fact]
    public Task MediaPortal_IntegerStyleTestedTwice() => Verify.VerifyAsync(
        """
        using System.Globalization;

        public sealed class Scanner
        {
            public object ScanNumber(string num, NumberStyles style)
            {
                if ({|#1:style == NumberStyles.Integer|})
                {
                    int ivalue;
                    if (int.TryParse(num, out ivalue))
                        return ivalue;
                    return long.Parse(num);
                }
                else if ({|#0:style == NumberStyles.Integer|})
                {
                    return double.Parse(num);
                }

                return num;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "style == NumberStyles.Integer"));

    [Fact]
    public Task NasaWorldWind_SettingPrefixesTestedTwice() => Verify.VerifyAsync(
        """
        using System.IO;

        public sealed class GpsTracker
        {
            public string Server;
            public bool NoDelay;

            public void LoadSettings(StreamReader sr)
            {
                while (true)
                {
                    var line = sr.ReadLine();
                    if (line == null || line.StartsWith("END UI CONTROLS"))
                        break;
                    if ({|#3:line.StartsWith("comboBoxAPRSInternetServer=")|})
                        Server = line.Substring(27);
                    else
                    if ({|#1:line.StartsWith("checkBoxNoDelay=")|})
                        NoDelay = line.EndsWith("True");
                    else
                    if ({|#0:line.StartsWith("checkBoxNoDelay=")|})
                        NoDelay = line.EndsWith("True");
                    else
                    if ({|#2:line.StartsWith("comboBoxAPRSInternetServer=")|})
                        Server = line.Substring(27);
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "line.StartsWith(\"checkBoxNoDelay=\")"),
        Verify.Diagnostic(Descriptors.RepeatedElseIfCondition).WithLocation(2).WithLocation(3).WithArguments("line.StartsWith(\"comboBoxAPRSInternetServer=\")"));

    [Fact]
    public Task WpfSamples_GreaterYTestedTwice() => Verify.VerifyAsync(
        """
        public readonly record struct Point(double X, double Y);

        public sealed class GlyphRun
        {
            public Point BaselineOrigin { get; init; }
        }

        public sealed class GlyphComparer
        {
            public int Compare(GlyphRun a, GlyphRun b)
            {
                var aPoint = a.BaselineOrigin;
                var bPoint = b.BaselineOrigin;
                var result = 0;
                if ({|#1:aPoint.Y > bPoint.Y|})
                {
                    return -1;
                }
                else if ({|#0:aPoint.Y > bPoint.Y|})
                {
                    result = 1;
                }
                else if (aPoint.X < bPoint.X)
                {
                    result = -1;
                }
                else if (aPoint.X > bPoint.X)
                {
                    result = 1;
                }

                return result;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "aPoint.Y > bPoint.Y"));

    [Fact]
    public Task XamarinForms_WidthConstraintTestedTwice() => Verify.VerifyAsync(
        """
        public sealed class SwitchRenderer
        {
            public int SwitchMinWidth { get; set; }

            public int GetDesiredSize(int widthConstraint, int heightConstraint)
            {
                int width = widthConstraint;
                if ({|#1:widthConstraint <= 0|})
                    width = SwitchMinWidth;
                else if ({|#0:widthConstraint <= 0|})
                    width = 100;
                return width * heightConstraint;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "widthConstraint <= 0"));

    [Fact]
    public Task SharpDevelop_CommandTestedTwice() => Verify.VerifyAsync(
        """
        using System.Windows.Input;

        public enum DockableContentState { None, Docked, FloatingWindow, Document }

        public static class DockableContentCommands
        {
            public static ICommand FloatingWindow { get; set; }
            public static ICommand ShowAsDocument { get; set; }
        }

        public sealed class DockableContent
        {
            public DockableContentState State { get; set; }

            public bool CanExecuteCommand(ICommand command)
            {
                if (command == DockableContentCommands.FloatingWindow)
                {
                    if (State == DockableContentState.FloatingWindow)
                    {
                        return false;
                    }
                }
                else if ({|#1:command == DockableContentCommands.ShowAsDocument|})
                {
                    if (State == DockableContentState.Document)
                    {
                        return false;
                    }
                }
                else if ({|#0:command == DockableContentCommands.ShowAsDocument|})
                {
                    if (State == DockableContentState.Document)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "command == DockableContentCommands.ShowAsDocument"));

    [Fact]
    public Task Flax_TypeofIntTestedTwice() => Verify.VerifyAsync(
        """
        using System;

        public sealed class ScriptType
        {
            public Type Type { get; set; }

            public bool IsEnum => Type.IsEnum;
        }

        public static class MemberComparison
        {
            public static object SetMemberValue(ScriptType type, object value)
            {
                if (type.IsEnum)
                    value = Convert.ChangeType(value, Enum.GetUnderlyingType(type.Type));
                else if (type.Type == typeof(byte))
                    value = Convert.ToByte(value);
                else if (type.Type == typeof(sbyte))
                    value = Convert.ToSByte(value);
                else if (type.Type == typeof(short))
                    value = Convert.ToInt16(value);
                else if ({|#1:type.Type == typeof(int)|})
                    value = Convert.ToInt32(value);
                else if (type.Type == typeof(long))
                    value = Convert.ToInt64(value);
                else if ({|#0:type.Type == typeof(int)|})
                    value = Convert.ToUInt16(value);
                return value;
            }
        }
        """,
        Expect(Descriptors.RepeatedElseIfCondition, "type.Type == typeof(int)"));

    [Fact]
    public Task ScreenToGif_MessageAppendedTwice() => Verify.VerifyAsync(
        """
        using System;
        using System.Diagnostics;
        using System.Threading.Tasks;

        public static class EncodingManager
        {
            public static async Task<string> Run(ProcessStartInfo procStartInfo)
            {
                var output = string.Empty;
                using (var process = new Process())
                {
                    process.StartInfo = procStartInfo;
                    process.Start();

                    var message = await process.StandardOutput.ReadToEndAsync();
                    var error = await process.StandardError.ReadToEndAsync();

                    if ({|#1:!string.IsNullOrWhiteSpace(message)|})
                        output += message + Environment.NewLine;

                    if ({|#0:!string.IsNullOrWhiteSpace(message)|})
                        output += message + Environment.NewLine;

                    process.WaitForExit(1000);
                }

                return output;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "!string.IsNullOrWhiteSpace(message)"));

    [Fact]
    public Task BtcPay_LocalFlagTestedTwiceAroundCalls() => Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;

        public sealed class HttpResponse
        {
            public void SetHeader(string name, string value)
            {
            }
        }

        public sealed class HttpContext
        {
            public HttpResponse Response { get; } = new HttpResponse();

            public void SetIsBitpayAPI(bool value)
            {
            }

            public void SetBitpayAuth(object auth)
            {
            }
        }

        public sealed class BTCPayMiddleware
        {
            private readonly Func<HttpContext, Task> _Next;

            public BTCPayMiddleware(Func<HttpContext, Task> next)
            {
                _Next = next;
            }

            private static object GetBitpayAuth(HttpContext httpContext, out bool isBitpayAuth)
            {
                isBitpayAuth = false;
                return null;
            }

            private static bool IsBitpayAPI(HttpContext httpContext, bool isBitpayAuth) => isBitpayAuth;

            public async Task Invoke(HttpContext httpContext)
            {
                var bitpayAuth = GetBitpayAuth(httpContext, out bool isBitpayAuth);
                var isBitpayAPI = IsBitpayAPI(httpContext, isBitpayAuth);
                httpContext.SetIsBitpayAPI(isBitpayAPI);
                if ({|#1:isBitpayAPI|})
                {
                    httpContext.Response.SetHeader("Access-Control-Allow-Origin", "*");
                    httpContext.SetBitpayAuth(bitpayAuth);
                }
                if ({|#0:isBitpayAPI|})
                {
                    await _Next(httpContext);
                    return;
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "isBitpayAPI"));

    [Fact]
    public Task Unity_UniformScalingBlockCopied() => Verify.VerifyAsync(
        """
        using System.Numerics;

        public static class BuiltinTools
        {
            public static Vector3 ResizeHandlesGUI(bool uniformScaling, int xHandle)
            {
                Vector3 scale = Vector3.One;
                if ({|#1:uniformScaling|})
                {
                    float refScale = (xHandle == 1 ? scale.Y : scale.X);
                    scale = Vector3.One * refScale;
                }

                if ({|#0:uniformScaling|})
                {
                    float refScale = (xHandle == 1 ? scale.Y : scale.X);
                    scale = Vector3.One * refScale;
                }

                return scale;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "uniformScaling"));

    [Fact]
    public Task Unity_HorizontalLockGuardsVerticalFlag() => Verify.VerifyAsync(
        """
        using System.Numerics;

        public sealed class ZoomableArea
        {
            private bool uniformScale;
            private bool hZoomLockedByDefault;

            public bool[] SetScaleFocused(Vector2 focalPoint, Vector2 newScale, bool lockHorizontal, bool lockVertical)
            {
                if (uniformScale)
                    lockHorizontal = lockVertical = false;
                else
                {
                    if ({|#1:hZoomLockedByDefault|})
                        lockHorizontal = !lockHorizontal;

                    if ({|#0:hZoomLockedByDefault|})
                        lockVertical = !lockVertical;
                }

                return [lockHorizontal, lockVertical];
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "hZoomLockedByDefault"));

    [Fact]
    public Task Emby_ImageKindTestRepeated() => Verify.VerifyAsync(
        """
        public sealed class LocalImageProvider
        {
            private bool AddImage(string name) => name.Length > 0;

            public bool PopulateImages(bool isEpisode, bool isSong, bool isPerson)
            {
                var added = false;
                if ({|#1:!isEpisode && !isSong && !isPerson|})
                {
                    added = AddImage("logo");
                    if (!added)
                    {
                        added = AddImage("clearlogo");
                    }
                }

                if ({|#0:!isEpisode && !isSong && !isPerson|})
                {
                    AddImage("art");
                }

                return added;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "!isEpisode && !isSong && !isPerson"));

    [Fact]
    public Task AwsSdk_ErrorCodeTestedTwice() => Verify.VerifyAsync(
        """
        using System;

        public sealed class ErrorResponse
        {
            public string Code { get; set; }
            public string Message { get; set; }
        }

        public sealed class LimitExceededException : Exception
        {
            public LimitExceededException(string message, Exception innerException)
                : base(message, innerException)
            {
            }
        }

        public static class CreateApiKeyResponseUnmarshaller
        {
            public static Exception UnmarshallException(ErrorResponse errorResponse, Exception innerException)
            {
                if ({|#1:errorResponse.Code != null && errorResponse.Code.Equals("LimitExceededException")|})
                {
                    return new LimitExceededException(errorResponse.Message, innerException);
                }

                if ({|#0:errorResponse.Code != null && errorResponse.Code.Equals("LimitExceededException")|})
                {
                    return new LimitExceededException(errorResponse.Message, innerException);
                }

                return innerException;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "errorResponse.Code != null && errorResponse.Code.Equals(\"LimitExceededException\")"));

    [Fact]
    public Task Infragistics_IndexCheckedTwice() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public static class DebugManager
        {
            public static void LogData(string text)
            {
            }
        }

        public sealed class BrushViewModel
        {
            public int SelectedBrushCollectionIndex { get; set; }

            public List<string> BrushCollectionList { get; } = new List<string>();

            public void OnPropertyChanged()
            {
                var index = this.SelectedBrushCollectionIndex;
                if ({|#1:index >= 0|})
                {
                    DebugManager.LogData(this.BrushCollectionList[index]);
                }
                if ({|#0:index >= 0|})
                {
                    this.SelectedBrushCollectionIndex = index;
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "index >= 0"));

    [Fact]
    public Task Roslyn_OldSourcesGuardNewSources() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;
        using System.Linq;

        public static class EditAndContinueTestHelpers
        {
            private static string ParseText(string source) => source;

            internal static int VerifySemantics(IEnumerable<string> oldTrees, IEnumerable<string> newTrees, string[] additionalOldSources, string[] additionalNewSources)
            {
                if ({|#1:additionalOldSources != null|})
                {
                    oldTrees = oldTrees.Concat(additionalOldSources.Select(s => ParseText(s)));
                }

                if ({|#0:additionalOldSources != null|})
                {
                    newTrees = newTrees.Concat(additionalNewSources.Select(s => ParseText(s)));
                }

                return oldTrees.Count() + newTrees.Count();
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "additionalOldSources != null"));

    [Fact]
    public Task GrandNode_WebpTestedTwice() => Verify.VerifyAsync(
        """
        public enum SKEncodedImageFormat { Bmp, Gif, Ico, Jpeg, Png, Wbmp, Webp }

        public static class PictureService
        {
            public static SKEncodedImageFormat EncodedImageFormat(string mimetype)
            {
                SKEncodedImageFormat defaultFormat = SKEncodedImageFormat.Jpeg;
                if (string.IsNullOrEmpty(mimetype))
                    return defaultFormat;

                mimetype = mimetype.ToLower();
                if (mimetype.Contains("jpeg") || mimetype.Contains("jpg") || mimetype.Contains("pjpeg"))
                    return defaultFormat;

                if (mimetype.Contains("png"))
                    return SKEncodedImageFormat.Png;

                if ({|#1:mimetype.Contains("webp")|})
                    return SKEncodedImageFormat.Webp;

                if ({|#0:mimetype.Contains("webp")|})
                    return SKEncodedImageFormat.Webp;

                if (mimetype.Contains("gif"))
                    return SKEncodedImageFormat.Gif;

                if (mimetype.Contains("bmp"))
                    return SKEncodedImageFormat.Png;

                if (mimetype.Contains("ico"))
                    return SKEncodedImageFormat.Ico;

                return defaultFormat;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "mimetype.Contains(\"webp\")"));

    [Fact]
    public Task Barotrauma_ArgumentCountCheckedTwiceInLambda() => Verify.VerifyAsync(
        """
        using System;
        using System.Numerics;

        public sealed class Client
        {
        }

        public static class DebugConsole
        {
            private static void SendConsoleMessage(string message, Client client)
            {
            }

            private static void ThrowError(string message)
            {
            }

            private static void AssignOnClientRequestExecute(string name, Action<Client, Vector2, string[]> onExecute)
            {
            }

            private static void InitProjectSpecific()
            {
                AssignOnClientRequestExecute(
                    "setclientcharacter",
                    (Client senderClient, Vector2 cursorWorldPos, string[] args) =>
                    {
                        if ({|#1:args.Length < 2|})
                        {
                            SendConsoleMessage("Invalid parameters.", senderClient);
                            return;
                        }

                        if ({|#0:args.Length < 2|})
                        {
                            ThrowError("Invalid parameters.");
                            return;
                        }
                    });
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "args.Length < 2"));

    [Fact]
    public Task DotNet6_NullArgumentCheckedTwice() => Verify.VerifyAsync(
        """
        using System;

        public static class JsonSerializer
        {
            public static object Deserialize(ReadOnlySpan<char> json, Type returnType, object options = null)
            {
                if ({|#1:returnType == null|})
                {
                    throw new ArgumentNullException(nameof(returnType));
                }

                if ({|#0:returnType == null|})
                {
                    throw new ArgumentNullException(nameof(returnType));
                }

                return json.Length;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "returnType == null"));

    [Fact]
    public Task DotNet6_ReadabilityCheckedTwice() => Verify.VerifyAsync(
        """
        using System;
        using System.IO;

        public sealed class DeflateManagedStream
        {
            internal DeflateManagedStream(Stream stream, long uncompressedSize = -1)
            {
                if (stream == null)
                    throw new ArgumentNullException(nameof(stream));
                if ({|#1:!stream.CanRead|})
                    throw new ArgumentException("Stream does not support reading.", nameof(stream));
                if ({|#0:!stream.CanRead|})
                    throw new ArgumentException("Stream does not support reading.", nameof(stream));
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "!stream.CanRead"));

    [Fact]
    public Task Emby_InputPathCheckedInsteadOfOutputPath() => Verify.VerifyAsync(
        """
        using System;

        public static class SkiaEncoder
        {
            public static string EncodeImage(string inputPath, string outputPath)
            {
                if ({|#1:string.IsNullOrWhiteSpace(inputPath)|})
                {
                    throw new ArgumentNullException("inputPath");
                }
                if ({|#0:string.IsNullOrWhiteSpace(inputPath)|})
                {
                    throw new ArgumentNullException("outputPath");
                }

                return outputPath;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "string.IsNullOrWhiteSpace(inputPath)"));

    [Fact]
    public Task Unity_EditorFlagTestedAfterReturn() => Verify.VerifyAsync(
        """
        public enum BuildTarget { NoTarget, StandaloneWindows }

        public sealed class CustomScriptAssembly
        {
            private bool IsCompatibleWithEditor() => true;

            public bool IsCompatibleWith(BuildTarget buildTarget, bool buildingForEditor)
            {
                if ({|#1:buildingForEditor|})
                    return IsCompatibleWithEditor();

                if ({|#0:buildingForEditor|})
                    buildTarget = BuildTarget.NoTarget;

                return buildTarget != BuildTarget.NoTarget;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "buildingForEditor"));

    [Fact]
    public Task Logify_ApiKeyCheckedInsteadOfReportFileName() => Verify.VerifyAsync(
        """
        using System;

        public sealed class SenderConfiguration
        {
            public string Url { get; set; }
            public string ApiKey { get; set; }
            public string ReportFileName { get; set; }
        }

        public sealed class Program
        {
            public bool ValidateConfiguration(SenderConfiguration config)
            {
                if (String.IsNullOrEmpty(config.Url))
                {
                    Console.WriteLine("ERROR: service Url not specified");
                    return false;
                }
                if ({|#1:String.IsNullOrEmpty(config.ApiKey)|})
                {
                    Console.WriteLine("ERROR: ApiKey not specified");
                    return false;
                }
                if ({|#0:String.IsNullOrEmpty(config.ApiKey)|})
                {
                    Console.WriteLine("ERROR: ReportFileName not specified");
                    return false;
                }

                return true;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "String.IsNullOrEmpty(config.ApiKey)"));

    [Fact]
    public Task NasaWorldWind_RetestedAfterBranchesThatBothExit() => Verify.VerifyAsync(
        """
        using System;
        using System.Diagnostics;

        public sealed class HtmlEditorException : Exception
        {
            public HtmlEditorException(string message)
                : base(message)
            {
            }
        }

        public sealed class HtmlEditor
        {
            private bool isCreated;
            private bool bLoadUrlWhenReady;
            private int iLoadAttempts;
            private string currentUrl;

            public void LoadUrl(String url)
            {
                if ({|#1:!isCreated|})
                {
                    Debug.WriteLine("Doc not created" + iLoadAttempts.ToString());
                    if (iLoadAttempts < 2)
                    {
                        this.bLoadUrlWhenReady = true;
                        return;
                    }
                    else
                    {
                        throw new HtmlEditorException("Document not created");
                    }
                }

                if ({|#0:!isCreated|}) return;
                currentUrl = url;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "!isCreated"));

    [Fact]
    public Task MonoDevelop_TextFormatTestedForImage() => Verify.VerifyAsync(
        """
        public enum TransferDataType { Text, Image, Uri }

        public sealed class Clipboard
        {
            public string WaitForText() => "";

            public object WaitForImage() => null;
        }

        public sealed class ClipboardBackend
        {
            private readonly Clipboard clipboard = new Clipboard();

            public object GetData(TransferDataType type)
            {
                if ({|#1:type == TransferDataType.Text|})
                    return clipboard.WaitForText();
                if ({|#0:type == TransferDataType.Text|})
                    return clipboard.WaitForImage();
                return null;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "type == TransferDataType.Text"));

    [Fact]
    public Task MlNet_PoolSizeBoundCheckedTwice() => Verify.VerifyAsync(
        """
        using System;

        public sealed class FastTreeTrainerOptions
        {
            public int NumberOfLeaves { get; set; }
            public int HistogramPoolSize { get; set; }
        }

        public interface IChannel
        {
            Exception Except(string message);
        }

        public abstract class BoostingFastTree
        {
            public FastTreeTrainerOptions FastTreeTrainerOptions { get; } = new FastTreeTrainerOptions();

            private protected void CheckOptions(IChannel ch)
            {
                if ({|#1:FastTreeTrainerOptions.NumberOfLeaves > 2 && FastTreeTrainerOptions.HistogramPoolSize > FastTreeTrainerOptions.NumberOfLeaves - 1|})
                {
                    throw ch.Except("Histogram pool size (ps) must be at least 2.");
                }

                if ({|#0:FastTreeTrainerOptions.NumberOfLeaves > 2 && FastTreeTrainerOptions.HistogramPoolSize > FastTreeTrainerOptions.NumberOfLeaves - 1|})
                {
                    throw ch.Except("Histogram pool size (ps) must be at most numLeaves - 1.");
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "FastTreeTrainerOptions.NumberOfLeaves > 2 && FastTreeTrainerOptions.HistogramPoolSize > FastTreeTrainerOptions.NumberOfLeaves - 1"));

    [Fact]
    public Task IronPython_MirroredComparisonAfterReturn() => Verify.VerifyAsync(
        """
        public readonly struct SourceLocation
        {
            public SourceLocation(int index)
            {
                Index = index;
            }

            public int Index { get; }

            public static bool operator <(SourceLocation left, SourceLocation right) => left.Index < right.Index;

            public static bool operator >(SourceLocation left, SourceLocation right) => left.Index > right.Index;

            public static int Compare(SourceLocation left, SourceLocation right)
            {
                if ({|#1:left < right|}) return -1;
                if ({|#0:right > left|}) return 1;
                return 0;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "right > left"));

    [Fact]
    public Task SemanticKernel_FormatRepeatedAfterOtherReturns() => Verify.VerifyAsync(
        """
        using System;

        public enum ChatOutputAudioFormat { Wav, Mp3, Opus, Flac, Pcm16 }

        public sealed class ChatAudioOptions
        {
            public ChatOutputAudioFormat OutputAudioFormat { get; set; }
        }

        public static class ClientCore
        {
            public static string GetAudioOutputMimeType(ChatAudioOptions audioOptions)
            {
                if (audioOptions is null)
                {
                    return null;
                }

                if ({|#1:audioOptions.OutputAudioFormat == ChatOutputAudioFormat.Wav|})
                {
                    return "audio/wav";
                }

                if (audioOptions.OutputAudioFormat == ChatOutputAudioFormat.Mp3)
                {
                    return "audio/mp3";
                }

                if (audioOptions.OutputAudioFormat == ChatOutputAudioFormat.Opus)
                {
                    return "audio/opus";
                }

                if ({|#0:audioOptions.OutputAudioFormat == ChatOutputAudioFormat.Wav|})
                {
                    return "audio/wav";
                }

                if (audioOptions.OutputAudioFormat == ChatOutputAudioFormat.Flac)
                {
                    return "audio/flac";
                }

                if (audioOptions.OutputAudioFormat == ChatOutputAudioFormat.Pcm16)
                {
                    return "audio/pcm16";
                }

                throw new NotSupportedException($"Unsupported audio output format '{audioOptions.OutputAudioFormat}'.");
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "audioOptions.OutputAudioFormat == ChatOutputAudioFormat.Wav"));

    [Fact]
    public Task Mono_StoreCheckedAgainInsteadOfCast() => Verify.VerifyAsync(
        """
        using System;

        public class SerializationStore
        {
        }

        public sealed class CodeDomSerializationStore : SerializationStore
        {
            public void AddObject(object value, bool absolute)
            {
            }
        }

        public sealed class CodeDomComponentSerializationService
        {
            public void SerializeCore(SerializationStore store, object value, bool absolute)
            {
                if (value == null)
                    throw new ArgumentNullException("value");
                if ({|#1:store == null|})
                    throw new ArgumentNullException("store");

                CodeDomSerializationStore codeDomStore = store as CodeDomSerializationStore;
                if ({|#0:store == null|})
                    throw new InvalidOperationException("store type unsupported");

                codeDomStore.AddObject(value, absolute);
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "store == null"));

    [Fact]
    public Task IronPython_FirstArgumentNotChecked() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;

        public sealed class PythonTuple
        {
        }

        public sealed class PythonType
        {
        }

        public static class PythonOps
        {
            public static Exception TypeError(string format, params object[] args) => new InvalidOperationException(format);
        }

        public static class DynamicHelpers
        {
            public static PythonType GetPythonType(object value) => null;
        }

        public sealed class Unpickler
        {
            private readonly Stack<object> stack = new Stack<object>();

            private object PopStack() => stack.Pop();

            public PythonType LoadNewObj()
            {
                PythonTuple args = PopStack() as PythonTuple;
                if ({|#1:args == null|})
                {
                    throw PythonOps.TypeError("expected second argument, got {0}", DynamicHelpers.GetPythonType(args));
                }
                PythonType cls = PopStack() as PythonType;
                if ({|#0:args == null|})
                {
                    throw PythonOps.TypeError("expected first argument, got {0}", DynamicHelpers.GetPythonType(args));
                }

                return cls;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "args == null"));

    [Fact]
    public Task Roslyn_KindRetestedAfterAnotherReturn() => Verify.VerifyAsync(
        """
        public enum SymbolKind { Local, Parameter, TypeParameter }

        public enum ErrorCode { ERR_LocalSameNameAsTypeParam }

        public sealed class DiagnosticBag
        {
            public void Add(ErrorCode code, object location, string name)
            {
            }
        }

        public static class InMethodBinder
        {
            internal static bool ReportConflictWithParameter(SymbolKind newSymbolKind, object newLocation, string name, DiagnosticBag diagnostics)
            {
                if ({|#1:newSymbolKind == SymbolKind.Parameter || newSymbolKind == SymbolKind.Local|})
                {
                    diagnostics.Add(ErrorCode.ERR_LocalSameNameAsTypeParam, newLocation, name);
                    return true;
                }
                if (newSymbolKind == SymbolKind.TypeParameter)
                {
                    return false;
                }
                if ({|#0:newSymbolKind == SymbolKind.Parameter || newSymbolKind == SymbolKind.Local|})
                {
                    diagnostics.Add(ErrorCode.ERR_LocalSameNameAsTypeParam, newLocation, name);
                    return true;
                }

                return false;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "newSymbolKind == SymbolKind.Parameter || newSymbolKind == SymbolKind.Local"));

    [Fact]
    public Task MonoDevelop_FirstResultCheckedAgain() => Verify.VerifyAsync(
        """
        public class ResolveResult
        {
        }

        public sealed class LocalResolveResult : ResolveResult
        {
        }

        public sealed class IndexerExpression
        {
            public object Target { get; set; }
        }

        public sealed class ResolveContext
        {
            public ResolveResult Resolve(object node) => null;
        }

        public sealed class ParameterCanBeDeclaredWithBaseTypeVisitor
        {
            private readonly ResolveContext context = new ResolveContext();

            public ResolveResult VisitIndexerExpression(IndexerExpression indexerExpression)
            {
                var localResolveResult = context.Resolve(indexerExpression.Target) as LocalResolveResult;
                if ({|#1:localResolveResult == null|})
                    return null;
                var resolveResult = context.Resolve(indexerExpression);
                if ({|#0:localResolveResult == null|})
                    return null;
                return resolveResult;
            }
        }
        """,
        Expect(Descriptors.RepeatedAdjacentCondition, "localResolveResult == null"));

    [Fact]
    public Task DotNetNuke_WholeConditionRepeatedInElse() => Verify.VerifyAsync(
        """
        public sealed class FriendlyUrlSettings
        {
            public bool RedirectUnfriendly { get; set; }
            public bool CheckForDuplicateUrls { get; set; }
        }

        public static class TabIndexController
        {
            private static string AddRedirectReasonToRewritePath(string rewritePath, string reason) => rewritePath + reason;

            private static void AddToTabDict(string tabPath, string rewritePath, bool checkForDuplicateUrls)
            {
            }

            public static void AddTabToTabDict(string tabPath, string rewritePath, bool customAliasUsedAndNotCurrent, FriendlyUrlSettings settings)
            {
                if ({|#1:customAliasUsedAndNotCurrent && settings.RedirectUnfriendly|})
                {
                    rewritePath = AddRedirectReasonToRewritePath(rewritePath, "Custom_Tab_Alias");
                    AddToTabDict(tabPath, rewritePath, settings.CheckForDuplicateUrls);
                }
                else
                {
                    if ({|#0:customAliasUsedAndNotCurrent && settings.RedirectUnfriendly|})
                    {
                        rewritePath = AddRedirectReasonToRewritePath(rewritePath, "Custom_Tab_Alias");
                        AddToTabDict(tabPath, rewritePath, settings.CheckForDuplicateUrls);
                    }
                    else
                    {
                        AddToTabDict(tabPath, rewritePath, settings.CheckForDuplicateUrls);
                    }
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "customAliasUsedAndNotCurrent && settings.RedirectUnfriendly", "false"));

    [Fact]
    public Task Ryujinx_FlagConjunctTestedAgain() => Verify.VerifyAsync(
        """
        using System.Diagnostics;

        public enum OperandType { I32, I64 }

        public static class RegisterAlias
        {
            public const int Aarch32Pc = 15;
        }

        public sealed class Operand
        {
            public OperandType Type { get; set; }
        }

        public sealed class ArmEmitterContext
        {
        }

        public static class InstEmitAluHelper
        {
            private static void EmitBxWritePc(ArmEmitterContext context, Operand value)
            {
            }

            private static void EmitAluWritePc(ArmEmitterContext context, Operand value)
            {
            }

            public static void EmitGenericAluStoreA32(ArmEmitterContext context, int rd, bool setFlags, Operand value)
            {
                Debug.Assert(value.Type == OperandType.I32);

                if (rd == RegisterAlias.Aarch32Pc && {|#1:setFlags|})
                {
                    if ({|#0:setFlags|})
                    {
                        EmitBxWritePc(context, value);
                    }
                    else
                    {
                        EmitAluWritePc(context, value);
                    }
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "setFlags", "true"));

    [Fact]
    public Task OnlyOffice_EmptinessCheckRepeatedInConjunction() => Verify.VerifyAsync(
        """
        using System;

        public static class CommonLinkUtility
        {
            public static string GetUserProfile(string user, bool absolute)
            {
                var queryParams = "";

                if ({|#1:!String.IsNullOrEmpty(user)|})
                {
                    var guid = Guid.Empty;
                    if ({|#0:!String.IsNullOrEmpty(user)|} && 32 <= user.Length && user[8] == '-')
                    {
                        guid = new Guid(user);
                    }

                    queryParams = guid != Guid.Empty ? "uid=" + guid : "user=" + user;
                }

                return absolute ? "/" + queryParams : queryParams;
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "!String.IsNullOrEmpty(user)", "true"));

    [Fact]
    public Task Telerik_NegatedFlagConjunctTestedAgain() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;

        public enum ItemState { Realizing, Realized, Recycling, Recycled }

        public sealed class RadDataBoundListBox
        {
            private readonly List<object> realizedItems = new List<object>();

            public bool ShowPullToRefreshWhenNoData { get; set; }

            private void StopPullToRefreshLoading(bool changeVisualState)
            {
            }

            private void HidePullToRefreshIndicator()
            {
            }

            internal void HandlePullToRefreshItemStateChanged(object item, ItemState state)
            {
                bool showIndicatorWhenNoData = this.ShowPullToRefreshWhenNoData;

                if (this.realizedItems.Count == 0 && {|#1:!showIndicatorWhenNoData|})
                {
                    if (state == ItemState.Recycled && {|#0:!showIndicatorWhenNoData|})
                    {
                        this.StopPullToRefreshLoading(false);
                        this.HidePullToRefreshIndicator();
                    }

                    return;
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "!showIndicatorWhenNoData", "true"));

    [Fact]
    public Task IronPython_NullCheckConjunctTestedAgain() => Verify.VerifyAsync(
        """
        using System;

        public sealed class BasicObject
        {
        }

        public sealed class TypeRef
        {
            public TypeRef(Type type)
            {
            }
        }

        public sealed class ModuleDef
        {
            public Type Extends { get; set; }
            public TypeRef Super { get; set; }
        }

        public sealed class ClassDef
        {
            public Type Inherits { get; set; }
        }

        public sealed class LibraryDef
        {
            public void ReflectType(ModuleDef def, ClassDef cls)
            {
                def.Super = null;
                if ({|#1:cls != null|} && def.Extends != typeof(BasicObject) && !def.Extends.IsInterface)
                {
                    if ({|#0:cls != null|} && cls.Inherits != null)
                    {
                        def.Super = new TypeRef(cls.Inherits);
                    }
                }
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "cls != null", "true"));

    [Fact]
    public Task CoreFx_NullCheckRepeatedInElse() => Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;
        using System.Reflection.Metadata;
        using System.Reflection.Metadata.Ecma335;

        public sealed class MetadataAggregator
        {
            private readonly IReadOnlyList<int> _baseTableRowCounts;
            private readonly IReadOnlyList<int> _baseHeapSizes;

            public MetadataAggregator(MetadataReader baseReader, IReadOnlyList<int> baseTableRowCounts, IReadOnlyList<int> baseHeapSizes)
            {
                if ({|#1:baseTableRowCounts == null|})
                {
                    if (baseReader == null)
                    {
                        throw new ArgumentNullException("deltaReaders");
                    }

                    if (baseReader.GetTableRowCount(TableIndex.EncMap) != 0)
                    {
                        throw new ArgumentException("Base reader must be a full metadata reader.", "baseReader");
                    }

                    CalculateBaseCounts(baseReader, out baseTableRowCounts, out baseHeapSizes);
                }
                else
                {
                    if ({|#0:baseTableRowCounts == null|})
                    {
                        throw new ArgumentNullException("baseTableRowCounts");
                    }
                }

                _baseTableRowCounts = baseTableRowCounts;
                _baseHeapSizes = baseHeapSizes;
            }

            private static void CalculateBaseCounts(MetadataReader baseReader, out IReadOnlyList<int> baseTableRowCounts, out IReadOnlyList<int> baseHeapSizes)
            {
                baseTableRowCounts = new int[MetadataTokens.TableCount];
                baseHeapSizes = new int[MetadataTokens.HeapCount];
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "baseTableRowCounts == null", "false"));

    [Fact]
    public Task CodeContracts_UnbracedNestedNullCheck() => Verify.VerifyAsync(
        """
        public class Document
        {
            private int[] lineOffsets;

            private void ComputeLineOffsets()
            {
                this.lineOffsets = new int[1];
            }

            private void DeleteLines(int offset, int lineCount)
            {
            }

            private void InsertLines(int offset, int lineCount)
            {
            }

            public virtual void InsertOrDeleteLines(int offset, int lineCount)
            {
                if ({|#1:this.lineOffsets == null|})
                    if ({|#0:this.lineOffsets == null|}) this.ComputeLineOffsets();
                if (lineCount < 0)
                    this.DeleteLines(offset, -lineCount);
                else
                    this.InsertLines(offset, lineCount);
            }
        }
        """,
        Expect(Descriptors.RepeatedNestedCondition, "this.lineOffsets == null", "true"));
}
