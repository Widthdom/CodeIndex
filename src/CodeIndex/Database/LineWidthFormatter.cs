namespace CodeIndex.Database;

/// <summary>
/// Clamp overly wide single-line payloads so line-centered query results stay bounded.
/// 極端に長い1行のペイロードを切り詰め、行中心のクエリ結果サイズを抑える。
/// </summary>
public static class LineWidthFormatter
{
    public const int DefaultMaxLineWidth = 512;
    public const int MaxAllowedLineWidth = 4096;

    public static int ClampMaxLineWidth(int maxLineWidth) =>
        Math.Clamp(maxLineWidth, 0, MaxAllowedLineWidth);

    public static ClampedTextResult ClampLine(string line, int maxLineWidth, int? focusColumn = null, int focusLength = 1)
    {
        maxLineWidth = ClampMaxLineWidth(maxLineWidth);
        if (maxLineWidth <= 0)
            return new ClampedTextResult(line, false);
        if (line.Length <= maxLineWidth)
            return new ClampedTextResult(line, false);

        if (focusColumn is > 0)
            return ClampAroundFocus(line, maxLineWidth, focusColumn.Value, Math.Max(1, focusLength));

        return ClampFromHead(line, maxLineWidth);
    }

    public static ClampedTextResult ClampLines(IReadOnlyList<string> lines, int maxLineWidth, int? focusLineIndex = null, int? focusColumn = null, int focusLength = 1)
    {
        if (lines.Count == 0)
            return new ClampedTextResult(string.Empty, false);

        maxLineWidth = ClampMaxLineWidth(maxLineWidth);
        if (maxLineWidth <= 0)
            return new ClampedTextResult(string.Join('\n', lines), false);
        var output = new string[lines.Count];
        var anyTruncated = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var clamped = i == focusLineIndex
                ? ClampLine(lines[i], maxLineWidth, focusColumn, focusLength)
                : ClampLine(lines[i], maxLineWidth);
            output[i] = clamped.Text;
            anyTruncated |= clamped.Truncated;
        }

        return new ClampedTextResult(string.Join('\n', output), anyTruncated);
    }

    private static ClampedTextResult ClampFromHead(string line, int maxLineWidth)
    {
        var suffix = BuildMarker(line.Length - maxLineWidth);
        var visibleWidth = maxLineWidth - suffix.Length;
        if (visibleWidth <= 0)
            return new ClampedTextResult(line[..SafeSliceEnd(line, maxLineWidth)], true);

        return new ClampedTextResult(line[..SafeSliceEnd(line, visibleWidth)] + suffix, true);
    }

    private static ClampedTextResult ClampAroundFocus(string line, int maxLineWidth, int focusColumn, int focusLength)
    {
        var focusStart = Math.Clamp(focusColumn - 1, 0, line.Length - 1);
        var focusEnd = Math.Min(line.Length, focusStart + focusLength);
        var start = focusStart;
        var end = focusEnd;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var prefix = start > 0 ? BuildMarker(start) : string.Empty;
            var suffix = end < line.Length ? BuildMarker(line.Length - end) : string.Empty;
            var visibleWidth = maxLineWidth - prefix.Length - suffix.Length;
            if (visibleWidth <= 0)
            {
                var safeFocusStart = SafeSliceStart(line, focusStart);
                var fallbackLength = Math.Min(maxLineWidth, line.Length - safeFocusStart);
                var safeFallbackEnd = SafeSliceEnd(line, safeFocusStart + fallbackLength);
                return new ClampedTextResult(line[safeFocusStart..safeFallbackEnd], true);
            }

            var desiredCenter = focusStart + ((focusEnd - focusStart) / 2);
            start = Math.Max(0, desiredCenter - (visibleWidth / 2));
            end = Math.Min(line.Length, start + visibleWidth);

            if (end < focusEnd)
            {
                end = focusEnd;
                start = Math.Max(0, end - visibleWidth);
            }

            if (start > focusStart)
            {
                start = focusStart;
                end = Math.Min(line.Length, start + visibleWidth);
            }
        }

        var safeStart = SafeSliceStart(line, start);
        var safeEnd = SafeSliceEnd(line, end);
        var finalPrefix = safeStart > 0 ? BuildMarker(safeStart) : string.Empty;
        var finalSuffix = safeEnd < line.Length ? BuildMarker(line.Length - safeEnd) : string.Empty;
        return new ClampedTextResult(finalPrefix + line[safeStart..safeEnd] + finalSuffix, true);
    }

    // Avoid slicing in the middle of a UTF-16 surrogate pair so output stays valid UTF-16
    // and non-BMP characters (e.g. emoji like U+1F44D) survive line clamping without becoming U+FFFD.
    private static int SafeSliceEnd(string line, int desiredEnd)
    {
        if (desiredEnd <= 0)
            return 0;
        if (desiredEnd >= line.Length)
            return line.Length;
        return char.IsLowSurrogate(line[desiredEnd]) ? desiredEnd - 1 : desiredEnd;
    }

    private static int SafeSliceStart(string line, int desiredStart)
    {
        if (desiredStart <= 0)
            return 0;
        if (desiredStart >= line.Length)
            return line.Length;
        return char.IsLowSurrogate(line[desiredStart]) ? desiredStart + 1 : desiredStart;
    }

    private static string BuildMarker(int elidedChars) =>
        $"...(+{elidedChars})...";
}

public readonly record struct ClampedTextResult(string Text, bool Truncated);
