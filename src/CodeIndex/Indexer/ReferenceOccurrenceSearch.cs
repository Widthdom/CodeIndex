namespace CodeIndex.Indexer;

internal static class ReferenceOccurrenceSearch
{
    // The old forward searches consume non-overlapping matches. A match at the
    // recorded column is safe to use directly only when no earlier match can
    // overlap its start. A preceding character absent from the needle proves
    // that without scanning the prefix (including for plugin-supplied names).
    // 従来の検索は非重複 match を消費する。直前の文字が検索語に含まれなければ、
    // 先行 match が記録位置をまたぐことはなく、prefix の走査を省略できる。
    internal static bool IsExactNonOverlappingOccurrence(
        string text,
        string value,
        int index)
        => value.Length > 0
           && index >= 0
           && index <= text.Length - value.Length
           && text.AsSpan(index, value.Length).SequenceEqual(value)
           && (index == 0 || !value.AsSpan().Contains(text[index - 1]));

    internal static int FindClosest(
        string text,
        string value,
        int column,
        out int examinedOccurrences)
    {
        examinedOccurrences = 0;
        if (value.Length == 0)
            return -1;
        if (column > 0 && IsExactNonOverlappingOccurrence(text, value, column - 1))
        {
            examinedOccurrences = 1;
            return column - 1;
        }

        var bestIndex = -1;
        var bestDistance = int.MaxValue;
        for (var searchAt = 0; searchAt <= text.Length - value.Length;)
        {
            var found = text.IndexOf(value, searchAt, StringComparison.Ordinal);
            if (found < 0)
                break;
            examinedOccurrences++;
            var distance = Math.Abs((found + 1) - column);
            if (distance < bestDistance)
            {
                bestIndex = found;
                bestDistance = distance;
            }

            // At or beyond a usable column, later matches cannot be closer.
            // Strict comparison above preserves the earlier occurrence on ties.
            if (column > 0 && found + 1 >= column)
                break;
            searchAt = found + value.Length;
        }

        return bestIndex;
    }
}
