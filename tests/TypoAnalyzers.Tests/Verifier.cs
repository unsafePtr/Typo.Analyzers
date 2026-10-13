using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace TypoAnalyzers.Tests;

internal static class Verifier<TAnalyzer>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    public static DiagnosticResult Diagnostic(DiagnosticDescriptor descriptor) => new(descriptor);

    public static Task VerifyAsync(string source, params DiagnosticResult[] expected) => RunAsync(source, string.Empty, ReferenceAssemblies.Net.Net100, expected);

    // The options go into the [*.cs] section of an .editorconfig above the test files.
    public static Task VerifyWithOptionsAsync(string source, string options, params DiagnosticResult[] expected) =>
        RunAsync(source, options, ReferenceAssemblies.Net.Net100, expected);

    public static Task VerifyWithReferencesAsync(ReferenceAssemblies references, string source, params DiagnosticResult[] expected) =>
        RunAsync(source, string.Empty, references, expected);

    private static Task RunAsync(string source, string options, ReferenceAssemblies references, DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = references,
        };
        if (options.Length > 0)
        {
            test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", $"root = true\n\n[*.cs]\n{options}\n"));
        }

        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync(TestContext.Current.CancellationToken);
    }
}
