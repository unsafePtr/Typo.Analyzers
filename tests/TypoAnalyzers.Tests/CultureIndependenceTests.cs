using System.Globalization;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace TypoAnalyzers.Tests;

/// <summary>
/// The compiler and IDE run analyzers under the user's culture, so verdicts must not change with it.
/// </summary>
/// <remarks>
/// Turkish is the classic trap: 'i' upper-cases to 'İ' and 'I' lower-cases to 'ı', so culture-sensitive case handling breaks name matching and suggestions.
/// </remarks>
public sealed class CultureIndependenceTests
{
    [Fact]
    public Task SimilarLinesUnderTurkishCulture() => InTurkishAsync(() =>
        Verifier<Analyzers.SimilarRunAnalyzer>.VerifyAsync(
            """
            public sealed class Order
            {
                public int Count;
                public int Index;
                public int Items;

                public Order(int count, int index, int items)
                {
                    Count = count;
                    Index = index;
                    Items = {|#0:index|};
                }
            }
            """,
            Verifier<Analyzers.SimilarRunAnalyzer>.Diagnostic(Descriptors.SimilarLineAnomaly).WithLocation(0).WithArguments("index", "items")));

    // Culture-sensitive upper-casing of the 'i' in 'items' would suggest 's.İtems'.
    [Fact]
    public Task SimilarPairUnderTurkishCulture() => InTurkishAsync(() =>
        Verifier<Analyzers.SimilarRunAnalyzer>.VerifyAsync(
            """
            public sealed class Store
            {
                public int Index;
                public int Items;
            }

            public static class C
            {
                public static int Read(Store s)
                {
                    var index = s.Index;
                    var items = {|#0:s.Index|};
                    return index + items;
                }
            }
            """,
            Verifier<Analyzers.SimilarRunAnalyzer>.Diagnostic(Descriptors.SimilarPairAnomaly).WithLocation(0).WithArguments("s.Index", "s.Items")));

    // Culture-sensitive case-insensitive matching turns the 'I' in 'DECLARATIONS' into 'ı', so the name would be unknown and the statement pair reported too.
    [Fact]
    public Task PairScanOptionUnderTurkishCulture() => InTurkishAsync(() =>
        Verifier<Analyzers.SimilarRunAnalyzer>.VerifyWithOptionsAsync(
            """
            public sealed class Store
            {
                public int Index;
                public int Items;

                public static int Read(Store s)
                {
                    var index = s.Index;
                    var items = {|#0:s.Index|};
                    return index + items;
                }

                public void Copy(Store s)
                {
                    Index = s.Index;
                    Items = s.Index;
                }
            }
            """,
            "TYPO0012.scan = DECLARATIONS",
            Verifier<Analyzers.SimilarRunAnalyzer>.Diagnostic(Descriptors.SimilarPairAnomaly).WithLocation(0).WithArguments("s.Index", "s.Items")));

    [Fact]
    public Task StringCaseUnderTurkishCulture() => InTurkishAsync(() =>
        Verifier<Analyzers.StringCaseAnalyzer>.VerifyAsync(
            """
            public static class C
            {
                public static bool Lower(string title) => title.ToLower() == {|#0:"TITLE"|};

                public static bool Upper(string title) => title.ToUpper() == {|#1:"title"|};
            }
            """,
            Verifier<Analyzers.StringCaseAnalyzer>.Diagnostic(Descriptors.StringCaseMismatch).WithLocation(0).WithArguments("\"TITLE\"", "lower"),
            Verifier<Analyzers.StringCaseAnalyzer>.Diagnostic(Descriptors.StringCaseMismatch).WithLocation(1).WithArguments("\"title\"", "upper")));

    [Fact]
    public Task DuplicateOperandsUnderTurkishCulture() => InTurkishAsync(() =>
        Verifier<Analyzers.IdenticalOperandsAnalyzer>.VerifyAsync(
            """
            public static class C
            {
                public static bool M(string id) => {|#1:id == "I"|} || id == "i" || {|#0:id == "I"|};
            }
            """,
            Verifier<Analyzers.IdenticalOperandsAnalyzer>.Diagnostic(Descriptors.IdenticalSubExpressions)
                .WithLocation(0)
                .WithLocation(1)
                .WithArguments("id == \"I\"", "id == \"I\"", "||")));

    private static async Task InTurkishAsync(Func<Task> test)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var turkish = new CultureInfo("tr-TR");
        CultureInfo.CurrentCulture = turkish;
        CultureInfo.CurrentUICulture = turkish;
        try
        {
            // Under invariant globalization every culture behaves like the invariant one and these tests would prove nothing.
            Assert.Equal("İ", "i".ToUpper(CultureInfo.CurrentCulture));
            await test();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}
