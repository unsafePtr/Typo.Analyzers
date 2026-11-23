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
