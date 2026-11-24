using System.Collections.Immutable;

namespace Typo.Core;

/// <summary>
/// The words an identifier is made of, such as 'max', 'Row' and 'Count' in maxRowCount.
/// </summary>
/// <remarks>
/// Words are separated by any character that is not a letter or digit, by a lower-case letter followed by an upper-case one, by a letter next to a digit, and before the last capital of an acronym followed by a lower-case letter (XML|Http).
/// All-lower-case compounds such as 'minx' have no boundary inside.
/// </remarks>
internal static class Words
{
    // Names are lowered on the stack up to this length, so each word costs one string.
    private const int StackLength = 256;

    // The words of a name in lower case and without its field prefix, so m_rowCount, _rowCount and RowCount all give 'row' and 'count'.
    // Invariant casing keeps 'Index' from becoming 'ındex' under a Turkish culture.
    public static ImmutableArray<string> Split(string name)
    {
        var span = name.AsSpan();
        var first = span.Length > 2 && span[1] == '_' && span[0] is 'm' or 's' ? 2 : 0;
        var count = 0;
        for (var at = first; NextWord(span, ref at, out _);)
        {
            count++;
        }

        if (count == 0)
        {
            return [];
        }

        var words = ImmutableArray.CreateBuilder<string>(count);
        var lower = span.Length <= StackLength ? stackalloc char[StackLength] : new char[span.Length];
        for (var at = first; NextWord(span, ref at, out var start);)
        {
            var length = span.Slice(start, at - start).ToLowerInvariant(lower);
            words.Add(lower.Slice(0, length).ToString());
        }

        return words.MoveToImmutable();
    }

    // Moves past the next word at or after 'at', whose first character is then at 'start'; false when no word is left.
    private static bool NextWord(ReadOnlySpan<char> span, ref int at, out int start)
    {
        while (at < span.Length && !char.IsLetterOrDigit(span[at]))
        {
            at++;
        }

        start = at;
        if (at == span.Length)
        {
            return false;
        }

        at++;
        while (at < span.Length && !IsBoundary(span, at))
        {
            at++;
        }

        return true;
    }

    public static bool IsBoundary(ReadOnlySpan<char> name, int index)
    {
        if (index <= 0 || index >= name.Length)
        {
            return true;
        }

        var before = name[index - 1];
        var after = name[index];
        if (!char.IsLetterOrDigit(before) || !char.IsLetterOrDigit(after) || char.IsDigit(before) != char.IsDigit(after))
        {
            return true;
        }

        if (char.IsLower(before) && char.IsUpper(after))
        {
            return true;
        }

        return char.IsUpper(before) && char.IsUpper(after) && index + 1 < name.Length && char.IsLower(name[index + 1]);
    }

    public static int IndexOf(ReadOnlySpan<char> text, ReadOnlySpan<char> word, int start)
    {
        for (var i = start; i + word.Length <= text.Length; i++)
        {
            if (IsBoundary(text, i)
                && IsBoundary(text, i + word.Length)
                && text.Slice(i, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
