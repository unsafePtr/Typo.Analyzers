using System.Runtime.CompilerServices;
using Xunit;

namespace Typo.Tests;

/// <summary>
/// A rule's help link must land on an anchor the README defines, and PACKAGE.md must link to the same anchor.
/// </summary>
public sealed class HelpLinkTests
{
    private const string Repository = "https://github.com/unsafePtr/TypoAnalyzer";

    [Fact]
    public void EveryRule_LinksToItsReadmeAnchor()
    {
        var readme = File.ReadAllText(RepositoryFile("README.md"));
        var package = File.ReadAllText(RepositoryFile("PACKAGE.md"));
        foreach (var descriptor in AllAnalyzers.Instances.SelectMany(static analyzer => analyzer.SupportedDiagnostics).Distinct())
        {
            var anchor = descriptor.Id.ToLowerInvariant();
            Assert.Equal($"{Repository}#{anchor}", descriptor.HelpLinkUri);
            Assert.Contains($"<a id=\"{anchor}\"></a>{descriptor.Id}", readme, StringComparison.Ordinal);
            Assert.Contains($"[{descriptor.Id}]({Repository}#{anchor})", package, StringComparison.Ordinal);
        }
    }

    private static string RepositoryFile(string name, [CallerFilePath] string testFile = "") =>
        Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", name);
}
