using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace TypoAnalyzers.Core;

/// <summary>
/// Source text for a diagnostic message, on one line and without comments, however the code is wrapped.
/// </summary>
/// <remarks>
/// MSBuild logs compiler output line by line, so a line break in a message cuts the warning down to its first line.
/// It follows Roslyn's internal SingleLineRewriter, which the IDE uses for display text: https://github.com/dotnet/roslyn/blob/e3c31f15b79e226656e3b082a921d4d60127b1fa/src/Workspaces/SharedUtilitiesAndExtensions/Compiler/CSharp/Extensions/SyntaxNodeExtensions.SingleLineRewriter.cs
/// Trivia between tokens becomes one space, and line breaks inside a token, which only a multi-line string can hold, become a space too.
/// Messages are built only for reported diagnostics, so the allocation is off the hot path.
/// </remarks>
internal static class Quote
{
    private static readonly Regex s_lineBreaks = new(@"[\r\n]+");

    public static string Of(SyntaxNode node)
    {
        var builder = new StringBuilder();
        var spaceAfter = false;
        foreach (var token in node.DescendantTokens())
        {
            if (builder.Length > 0 && (spaceAfter || token.HasLeadingTrivia))
            {
                builder.Append(' ');
            }

            builder.Append(s_lineBreaks.Replace(token.Text, " "));
            spaceAfter = token.HasTrailingTrivia;
        }

        return builder.ToString();
    }
}
