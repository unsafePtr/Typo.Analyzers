using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Typo.Tests;

/// <summary>
/// Runs every analyzer in the package over one source file and records analyzer exceptions, which the single-analyzer verifier cannot do for broken code.
/// </summary>
internal static class AllAnalyzers
{
    public static ImmutableArray<DiagnosticAnalyzer> Instances { get; } =
    [
        .. typeof(Descriptors).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
            .Select(type => (DiagnosticAnalyzer)Activator.CreateInstance(type)!),
    ];

    public static async Task<Compilation> CompileAsync(string source, CancellationToken cancellationToken)
    {
        var references = await ReferenceAssemblies.Net.Net100.ResolveAsync(LanguageNames.CSharp, cancellationToken);
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), cancellationToken: cancellationToken);
        return CSharpCompilation.Create("Probe", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    public static async Task<AnalysisRun> RunAsync(string source, CancellationToken cancellationToken)
    {
        var compilation = await CompileAsync(source, cancellationToken);
        var exceptions = new ConcurrentQueue<Exception>();
        var options = new CompilationWithAnalyzersOptions(
            new AnalyzerOptions([]),
            (exception, _, _) => exceptions.Enqueue(exception),
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false);
        var diagnostics = await compilation.WithAnalyzers(Instances, options).GetAnalyzerDiagnosticsAsync(cancellationToken);
        return new AnalysisRun(diagnostics, [.. exceptions]);
    }
}

internal sealed record AnalysisRun(ImmutableArray<Diagnostic> Diagnostics, IReadOnlyList<Exception> Exceptions);
