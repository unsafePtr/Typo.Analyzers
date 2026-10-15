using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = TypoAnalyzers.Tests.Verifier<TypoAnalyzers.Analyzers.StringCaseAnalyzer>;

namespace TypoAnalyzers.Tests;

public sealed class StringCaseAnalyzerTests
{
    private static DiagnosticResult Mismatch(string text, string targetCase) =>
        Verify.Diagnostic(Descriptors.StringCaseMismatch).WithLocation(0).WithArguments(text, targetCase);

    [Fact]
    public Task LocalWithLowerCaseLiteralSearchedForMixedCase() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M()
            {
                string text = "abcde";
                return text.Contains({|#0:"AbCdE"|});
            }
        }
        """,
        Mismatch("\"AbCdE\"", "lower"));

    [Fact]
    public Task LocalWithUpperCaseLiteralComparedWithLowerCase() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M()
            {
                var text = "ABC";
                return text == {|#0:"abc"|};
            }
        }
        """,
        Mismatch("\"abc\"", "upper"));

    [Fact]
    public Task LocalLiteralReassignedIsSilent() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string other)
            {
                string text = "abcde";
                text = other;
                return text.Contains("AbCdE");
            }
        }
        """);

    [Fact]
    public Task LocalLiteralWithBothCasesIsSilent() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M()
            {
                string text = "AbcDe";
                return text.Contains("AbCdE");
            }
        }
        """);

    [Fact]
    public Task SqlSugar_InterpolatedNeedleWithUppercaseKeyword() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool Missing(string selectString, string pk) =>
                !selectString.ToLower().Contains({|#0:$" {pk.ToLower()} AS {pk.ToLower()}"|});
        }
        """,
        Mismatch("$\" {pk.ToLower()} AS {pk.ToLower()}\"", "lower"));

    [Fact]
    public Task LiteralNeedle() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string s) => s.ToLower().Contains({|#0:"AS"|});
        }
        """,
        Mismatch("\"AS\"", "lower"));

    [Fact]
    public Task EqualityOperator() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string s) => s.ToUpper() == {|#0:"abc"|};
        }
        """,
        Mismatch("\"abc\"", "upper"));

    [Fact]
    public Task StartsWithAfterInvariantLowering() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string url) => url.ToLowerInvariant().StartsWith({|#0:"Http"|});
        }
        """,
        Mismatch("\"Http\"", "lower"));

    [Fact]
    public Task ConcatenatedNeedle() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string sql, string pk) => sql.ToLower().Contains({|#0:" " + pk + " AS "|});
        }
        """,
        Mismatch("\" \" + pk + \" AS \"", "lower"));

    [Fact]
    public Task LocalComparedWithItsOppositeCase() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string value)
            {
                var lowerValue = value.ToLower();
                return lowerValue == {|#0:lowerValue.ToUpper()|};
            }
        }
        """,
        Mismatch("lowerValue.ToUpper()", "lower"));

    [Fact]
    public Task UpperComparedWithLower() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string s, string t) => s.ToUpper() == {|#0:t.ToLower()|};
        }
        """,
        Mismatch("t.ToLower()", "upper"));

    [Fact]
    public Task SwitchCaseNeverMatches() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string command)
            {
                switch (command.ToLower())
                {
                    case {|#0:"Select"|}:
                        return 1;
                    case "insert":
                        return 2;
                    default:
                        return 0;
                }
            }
        }
        """,
        Mismatch("\"Select\"", "lower"));

    [Fact]
    public Task IsPatternNeverMatches() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string status) => status.ToUpperInvariant() is {|#0:"ok"|} or "FAIL";
        }
        """,
        Mismatch("\"ok\"", "upper"));

    [Fact]
    public Task EndsWithUppercaseExtension() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool IsPng(string fileName) => fileName.ToLowerInvariant().EndsWith({|#0:".PNG"|});
        }
        """,
        Mismatch("\".PNG\"", "lower"));

    [Fact]
    public Task CharNeedle() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string s) => s.ToLower().Contains({|#0:'A'|});
        }
        """,
        Mismatch("'A'", "lower"));

    [Fact]
    public Task ConstantNeedle() => Verify.VerifyAsync(
        """
        public static class C
        {
            private const string Keyword = "SELECT";

            public static bool IsQuery(string sql) => sql.ToLower().StartsWith({|#0:Keyword|});
        }
        """,
        Mismatch("Keyword", "lower"));

    [Fact]
    public Task ConditionalAccessLowering() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string s) => s?.ToLower() == {|#0:"Yes"|};
        }
        """,
        Mismatch("\"Yes\"", "lower"));

    [Fact]
    public Task CultureSpecificLowering() => Verify.VerifyAsync(
        """
        using System.Globalization;

        public static class C
        {
            public static bool M(string s) => s.ToLower(CultureInfo.InvariantCulture) == {|#0:"Id"|};
        }
        """,
        Mismatch("\"Id\"", "lower"));

    [Fact]
    public Task SwitchExpressionArmNeverMatches() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string method) => method.ToUpper() switch
            {
                {|#0:"Get"|} => 1,
                "POST" => 2,
                _ => 0,
            };
        }
        """,
        Mismatch("\"Get\"", "upper"));

    [Fact]
    public Task StaticEqualsWithOrdinalComparison() => Verify.VerifyAsync(
        """
        using System;

        public static class C
        {
            public static bool M(string s) => string.Equals(s.ToUpperInvariant(), {|#0:"abc"|}, StringComparison.Ordinal);
        }
        """,
        Mismatch("\"abc\"", "upper"));

    [Fact]
    public Task SwitchPatternClauseNeverMatches() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static int M(string command)
            {
                switch (command.ToLower())
                {
                    case {|#0:"Get"|} or "post":
                        return 1;
                    default:
                        return 0;
                }
            }
        }
        """,
        Mismatch("\"Get\"", "lower"));

    [Fact]
    public Task ConstantOnTheLeft() => Verify.VerifyAsync(
        """
        public static class C
        {
            public static bool M(string s) => {|#0:"ABC"|} == s.ToLower();
        }
        """,
        Mismatch("\"ABC\"", "lower"));

    [Fact]
    public Task CultureComparisonWithoutIgnoreCase() => Verify.VerifyAsync(
        """
        using System.Globalization;

        public static class C
        {
            public static bool M(string s) => s.ToLower().EndsWith({|#0:"A"|}, false, CultureInfo.InvariantCulture);
        }
        """,
        Mismatch("\"A\"", "lower"));

    [Fact]
    public Task ExpressionBodiedPropertyNeedle() => Verify.VerifyAsync(
        """
        public static class Keys
        {
            public static string Header => "X-Token";
        }

        public static class C
        {
            public static bool M(string header) => header.ToLowerInvariant() == {|#0:Keys.Header|};
        }
        """,
        Mismatch("Keys.Header", "lower"));

    [Fact]
    public Task NoDiagnostics_WhenValueIsNotCaseNormalized() => Verify.VerifyAsync(
        """
        using System.Collections.Generic;
        using System.Globalization;

        public static class C
        {
            public static bool Trimmed(string s) => s.Trim() == "ABC";

            public static bool TrimmedLocal(string s)
            {
                var t = s.Trim();
                return t == "Abc";
            }

            public static bool LoopVariable(List<string> items)
            {
                foreach (var item in items)
                {
                    if (item == "Abc")
                    {
                        return true;
                    }
                }

                return false;
            }

            public static bool PatternVariable(object o) => o is string text && text == "Abc";

            public static bool InterpolationWithoutLetters(string s, int x) => s.ToLower() == $"abc{x}";

            public static bool IgnoreCaseFlag(string s) => s.ToLower().EndsWith("A", true, CultureInfo.InvariantCulture);

            public static bool Null(string s) => s.ToLower() == null;
        }
        """);

    [Fact]
    public Task NoDiagnostics_ForMatchingOrIgnoredCase() => Verify.VerifyAsync(
        """
        using System;

        public static class C
        {
            public static bool Correct(string s) => s.ToLower().Contains("as");

            public static bool IgnoreCase(string s) => s.ToLower().Contains("AS", StringComparison.OrdinalIgnoreCase);

            public static bool NonConstant(string s, string other) => s.ToLower() == other;

            public static bool NoLetters(string s) => s.ToLower().Contains("123 _-");

            public static bool NoUpperMapping(string s) => s.ToUpperInvariant() == "STRAßE";

            public static bool BothLowered(string a, string b) => a.ToLower() == b.ToLower();

            public static bool Reassigned(string s)
            {
                var lower = s.ToLower();
                lower = s;
                return lower == "Abc";
            }

            public static bool OverridableProperty(string s, Folder folder) => s.ToLower().EndsWith(folder.Name);

            public static bool ComputedProperty(string s) => s.ToLower().EndsWith(Folder.Computed);
        }

        public class Folder
        {
            public static string Computed => "App_" + System.Environment.MachineName;

            public virtual string Name => "App_Data";
        }
        """);

    [Fact]
    public Task DotNetNuke_LoweredPathSearchedForCamelCaseKey() => Verify.VerifyAsync(
        """
        public sealed class UrlAction
        {
            public string RewritePath { get; set; }
        }

        public sealed class AdvancedUrlRewriter
        {
            public void ProcessRequest(UrlAction result)
            {
                if (!result.RewritePath.ToLowerInvariant().Contains({|#0:"tabId="|}))
                {
                    result.RewritePath += "&tabId=0";
                }
            }
        }
        """,
        Mismatch("\"tabId=\"", "lower"));

    [Fact]
    public Task DotNetNuke_LoweredPathEndsWithPropertyReturningMixedCase() => Verify.VerifyAsync(
        """
        using System.IO;

        public static class Localization
        {
            public static string LocalResourceDirectory
            {
                get
                {
                    return "App_LocalResources";
                }
            }
        }

        public static class LanguagesController
        {
            public static bool HasLocalResources(string path)
            {
                var folderInfo = new DirectoryInfo(path);

                if (path.ToLowerInvariant().EndsWith({|#0:Localization.LocalResourceDirectory|}))
                {
                    return true;
                }

                return folderInfo.Exists;
            }
        }
        """,
        Mismatch("Localization.LocalResourceDirectory", "lower"));
}
