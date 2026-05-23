using System.Globalization;
using System.Text;

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
        if (DisplayWidth(line) <= maxLineWidth)
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
        var truncatedCharCount = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var clamped = i == focusLineIndex
                ? ClampLine(lines[i], maxLineWidth, focusColumn, focusLength)
                : ClampLine(lines[i], maxLineWidth);
            output[i] = clamped.Text;
            anyTruncated |= clamped.Truncated;
            truncatedCharCount += clamped.TruncatedCharCount;
        }

        return new ClampedTextResult(string.Join('\n', output), anyTruncated, truncatedCharCount);
    }

    private static ClampedTextResult ClampFromHead(string line, int maxLineWidth)
    {
        var visibleEnd = SliceEndByDisplayWidth(line, maxLineWidth);
        var suffix = BuildMarker(line.Length - visibleEnd);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var nextVisibleWidth = maxLineWidth - DisplayWidth(suffix);
            if (nextVisibleWidth <= 0)
                break;
            var nextVisibleEnd = SliceEndByDisplayWidth(line, nextVisibleWidth);
            if (nextVisibleEnd == visibleEnd)
                break;

            visibleEnd = nextVisibleEnd;
            suffix = BuildMarker(line.Length - visibleEnd);
        }

        var visibleWidth = maxLineWidth - DisplayWidth(suffix);
        if (visibleWidth <= 0)
        {
            var safeEnd = SliceEndByDisplayWidth(line, maxLineWidth);
            return new ClampedTextResult(line[..safeEnd], true, line.Length - safeEnd);
        }

        var safeVisibleEnd = SliceEndByDisplayWidth(line, visibleWidth);
        return new ClampedTextResult(line[..safeVisibleEnd] + BuildMarker(line.Length - safeVisibleEnd), true, line.Length - safeVisibleEnd);
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
            var visibleWidth = maxLineWidth - DisplayWidth(prefix) - DisplayWidth(suffix);
            if (visibleWidth <= 0)
            {
                var safeFocusStart = SafeSliceStart(line, focusStart);
                var safeFallbackEnd = SliceEndByDisplayWidth(line, maxLineWidth, safeFocusStart);
                return new ClampedTextResult(line[safeFocusStart..safeFallbackEnd], true, line.Length - (safeFallbackEnd - safeFocusStart));
            }

            var desiredCenter = focusStart + ((focusEnd - focusStart) / 2);
            start = Math.Max(0, desiredCenter - (visibleWidth / 2));
            end = SliceEndByDisplayWidth(line, visibleWidth, start);

            if (end < focusEnd)
            {
                end = focusEnd;
                start = SliceStartByDisplayWidth(line, visibleWidth, end);
            }

            if (start > focusStart)
            {
                start = focusStart;
                end = SliceEndByDisplayWidth(line, visibleWidth, start);
            }
        }

        var safeStart = SafeSliceStart(line, start);
        var safeEnd = SafeSliceEnd(line, end);
        var finalPrefix = safeStart > 0 ? BuildMarker(safeStart) : string.Empty;
        var finalSuffix = safeEnd < line.Length ? BuildMarker(line.Length - safeEnd) : string.Empty;
        var visibleChars = safeEnd - safeStart;
        return new ClampedTextResult(finalPrefix + line[safeStart..safeEnd] + finalSuffix, true, line.Length - visibleChars);
    }

    // Avoid slicing in the middle of a grapheme cluster so combining marks, ZWJ emoji,
    // and non-BMP characters survive line clamping without becoming detached or invalid.
    private static int SafeSliceEnd(string line, int desiredEnd)
    {
        if (desiredEnd <= 0)
            return 0;
        if (desiredEnd >= line.Length)
            return line.Length;
        return PreviousTextElementBoundary(line, desiredEnd);
    }

    private static int SafeSliceStart(string line, int desiredStart)
    {
        if (desiredStart <= 0)
            return 0;
        if (desiredStart >= line.Length)
            return line.Length;
        return PreviousTextElementBoundary(line, desiredStart);
    }

    private static string BuildMarker(int elidedChars) =>
        $"...(+{elidedChars})...";

    private static int PreviousTextElementBoundary(string line, int index)
    {
        var previous = 0;
        foreach (var boundary in StringInfo.ParseCombiningCharacters(line))
        {
            if (boundary == index)
                return index;
            if (boundary > index)
                return previous;
            previous = boundary;
        }

        return previous;
    }

    private static int SliceEndByDisplayWidth(string line, int maxDisplayWidth, int start = 0)
    {
        if (maxDisplayWidth <= 0 || start >= line.Length)
            return Math.Clamp(start, 0, line.Length);

        start = SafeSliceStart(line, start);
        var width = 0;
        var end = start;
        var enumerator = StringInfo.GetTextElementEnumerator(line);
        while (enumerator.MoveNext())
        {
            if (enumerator.ElementIndex < start)
                continue;

            var element = enumerator.GetTextElement();
            var elementWidth = TextElementWidth(element);
            if (width + elementWidth > maxDisplayWidth)
                break;

            width += elementWidth;
            end = enumerator.ElementIndex + element.Length;
        }

        return end;
    }

    private static int SliceStartByDisplayWidth(string line, int maxDisplayWidth, int end)
    {
        if (maxDisplayWidth <= 0 || end <= 0)
            return Math.Clamp(end, 0, line.Length);

        end = SafeSliceEnd(line, end);
        var elements = new List<(int Start, int End, int Width)>();
        var enumerator = StringInfo.GetTextElementEnumerator(line);
        while (enumerator.MoveNext())
        {
            var elementStart = enumerator.ElementIndex;
            var element = enumerator.GetTextElement();
            var elementEnd = elementStart + element.Length;
            if (elementEnd > end)
                break;
            elements.Add((elementStart, elementEnd, TextElementWidth(element)));
        }

        var start = end;
        var width = 0;
        for (var i = elements.Count - 1; i >= 0; i--)
        {
            var element = elements[i];
            if (width + element.Width > maxDisplayWidth)
                break;

            width += element.Width;
            start = element.Start;
        }

        return start;
    }

    private static int DisplayWidth(string text)
    {
        var width = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            width += TextElementWidth(enumerator.GetTextElement());
        return width;
    }

    private static int TextElementWidth(string element)
    {
        if (element.Length == 0)
            return 0;

        var rune = Rune.GetRuneAt(element, 0);
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.Control or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            return 0;

        return IsWideRune(rune.Value) || (IsAmbiguousRune(rune.Value) && UsesWideAmbiguousWidth()) ? 2 : 1;
    }

    private static bool UsesWideAmbiguousWidth()
    {
        var lang = Environment.GetEnvironmentVariable("LC_ALL");
        if (string.IsNullOrEmpty(lang))
            lang = Environment.GetEnvironmentVariable("LC_CTYPE");
        if (string.IsNullOrEmpty(lang))
            lang = Environment.GetEnvironmentVariable("LANG");

        return lang is not null
            && (lang.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                || lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                || lang.StartsWith("ko", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWideRune(int value) =>
        value is >= 0x1100 and <= 0x115F
            or >= 0x231A and <= 0x231B
            or >= 0x2329 and <= 0x232A
            or >= 0x23E9 and <= 0x23EC
            or >= 0x23F0 and <= 0x23F0
            or >= 0x23F3 and <= 0x23F3
            or >= 0x25FD and <= 0x25FE
            or >= 0x2614 and <= 0x2615
            or >= 0x2648 and <= 0x2653
            or >= 0x267F and <= 0x267F
            or >= 0x2693 and <= 0x2693
            or >= 0x26A1 and <= 0x26A1
            or >= 0x26AA and <= 0x26AB
            or >= 0x26BD and <= 0x26BE
            or >= 0x26C4 and <= 0x26C5
            or >= 0x26CE and <= 0x26CE
            or >= 0x26D4 and <= 0x26D4
            or >= 0x26EA and <= 0x26EA
            or >= 0x26F2 and <= 0x26F3
            or >= 0x26F5 and <= 0x26F5
            or >= 0x26FA and <= 0x26FA
            or >= 0x26FD and <= 0x26FD
            or >= 0x2705 and <= 0x2705
            or >= 0x270A and <= 0x270B
            or >= 0x2728 and <= 0x2728
            or >= 0x274C and <= 0x274C
            or >= 0x274E and <= 0x274E
            or >= 0x2753 and <= 0x2755
            or >= 0x2757 and <= 0x2757
            or >= 0x2795 and <= 0x2797
            or >= 0x27B0 and <= 0x27B0
            or >= 0x27BF and <= 0x27BF
            or >= 0x2B1B and <= 0x2B1C
            or >= 0x2B50 and <= 0x2B50
            or >= 0x2B55 and <= 0x2B55
            or >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F004 and <= 0x1F004
            or >= 0x1F0CF and <= 0x1F0CF
            or >= 0x1F18E and <= 0x1F18E
            or >= 0x1F191 and <= 0x1F19A
            or >= 0x1F200 and <= 0x1F64F
            or >= 0x1F680 and <= 0x1F6FF
            or >= 0x1F900 and <= 0x1F9FF
            or >= 0x20000 and <= 0x3FFFD;

    private static bool IsAmbiguousRune(int value) =>
        value is >= 0x00A1 and <= 0x00A1
            or >= 0x00A4 and <= 0x00A4
            or >= 0x00A7 and <= 0x00A8
            or >= 0x00AA and <= 0x00AA
            or >= 0x00AD and <= 0x00AE
            or >= 0x00B0 and <= 0x00B4
            or >= 0x00B6 and <= 0x00BA
            or >= 0x00BC and <= 0x00BF
            or >= 0x00C6 and <= 0x00C6
            or >= 0x00D0 and <= 0x00D0
            or >= 0x00D7 and <= 0x00D8
            or >= 0x00DE and <= 0x00E1
            or >= 0x00E6 and <= 0x00E6
            or >= 0x00E8 and <= 0x00EA
            or >= 0x00EC and <= 0x00ED
            or >= 0x00F0 and <= 0x00F0
            or >= 0x00F2 and <= 0x00F3
            or >= 0x00F7 and <= 0x00FA
            or >= 0x00FC and <= 0x00FC
            or >= 0x00FE and <= 0x00FE
            or >= 0x0101 and <= 0x0101
            or >= 0x0111 and <= 0x0111
            or >= 0x0113 and <= 0x0113
            or >= 0x011B and <= 0x011B
            or >= 0x0126 and <= 0x0127
            or >= 0x012B and <= 0x012B
            or >= 0x0131 and <= 0x0133
            or >= 0x0138 and <= 0x0138
            or >= 0x013F and <= 0x0142
            or >= 0x0144 and <= 0x0144
            or >= 0x0148 and <= 0x014B
            or >= 0x014D and <= 0x014D
            or >= 0x0152 and <= 0x0153
            or >= 0x0166 and <= 0x0167
            or >= 0x016B and <= 0x016B
            or >= 0x01CE and <= 0x01CE
            or >= 0x01D0 and <= 0x01D0
            or >= 0x01D2 and <= 0x01D2
            or >= 0x01D4 and <= 0x01D4
            or >= 0x01D6 and <= 0x01D6
            or >= 0x01D8 and <= 0x01D8
            or >= 0x01DA and <= 0x01DA
            or >= 0x01DC and <= 0x01DC
            or >= 0x0251 and <= 0x0251
            or >= 0x0261 and <= 0x0261
            or >= 0x02C4 and <= 0x02C4
            or >= 0x02C7 and <= 0x02C7
            or >= 0x02C9 and <= 0x02CB
            or >= 0x02CD and <= 0x02CD
            or >= 0x02D0 and <= 0x02D0
            or >= 0x02D8 and <= 0x02DB
            or >= 0x02DD and <= 0x02DD
            or >= 0x02DF and <= 0x02DF
            or >= 0x0391 and <= 0x03A9
            or >= 0x03B1 and <= 0x03C1
            or >= 0x03C3 and <= 0x03C9
            or >= 0x0401 and <= 0x0401
            or >= 0x0410 and <= 0x044F
            or >= 0x0451 and <= 0x0451
            or >= 0x2010 and <= 0x2010
            or >= 0x2013 and <= 0x2016
            or >= 0x2018 and <= 0x2019
            or >= 0x201C and <= 0x201D
            or >= 0x2020 and <= 0x2022
            or >= 0x2024 and <= 0x2027
            or >= 0x2030 and <= 0x2030
            or >= 0x2032 and <= 0x2033
            or >= 0x2035 and <= 0x2035
            or >= 0x203B and <= 0x203B
            or >= 0x203E and <= 0x203E
            or >= 0x2074 and <= 0x2074
            or >= 0x207F and <= 0x207F
            or >= 0x2081 and <= 0x2084
            or >= 0x20AC and <= 0x20AC
            or >= 0x2103 and <= 0x2103
            or >= 0x2105 and <= 0x2105
            or >= 0x2109 and <= 0x2109
            or >= 0x2113 and <= 0x2113
            or >= 0x2116 and <= 0x2116
            or >= 0x2121 and <= 0x2122
            or >= 0x2126 and <= 0x2126
            or >= 0x212B and <= 0x212B
            or >= 0x2153 and <= 0x2154
            or >= 0x215B and <= 0x215E
            or >= 0x2160 and <= 0x216B
            or >= 0x2170 and <= 0x2179
            or >= 0x2189 and <= 0x2189
            or >= 0x2190 and <= 0x2199
            or >= 0x21B8 and <= 0x21B9
            or >= 0x21D2 and <= 0x21D2
            or >= 0x21D4 and <= 0x21D4
            or >= 0x21E7 and <= 0x21E7
            or >= 0x2200 and <= 0x2200
            or >= 0x2202 and <= 0x2203
            or >= 0x2207 and <= 0x2208
            or >= 0x220B and <= 0x220B
            or >= 0x220F and <= 0x220F
            or >= 0x2211 and <= 0x2211
            or >= 0x2215 and <= 0x2215
            or >= 0x221A and <= 0x221A
            or >= 0x221D and <= 0x2220
            or >= 0x2223 and <= 0x2223
            or >= 0x2225 and <= 0x2225
            or >= 0x2227 and <= 0x222C
            or >= 0x222E and <= 0x222E
            or >= 0x2234 and <= 0x2237
            or >= 0x223C and <= 0x223D
            or >= 0x2248 and <= 0x2248
            or >= 0x224C and <= 0x224C
            or >= 0x2252 and <= 0x2252
            or >= 0x2260 and <= 0x2261
            or >= 0x2264 and <= 0x2267
            or >= 0x226A and <= 0x226B
            or >= 0x226E and <= 0x226F
            or >= 0x2282 and <= 0x2283
            or >= 0x2286 and <= 0x2287
            or >= 0x2295 and <= 0x2295
            or >= 0x2299 and <= 0x2299
            or >= 0x22A5 and <= 0x22A5
            or >= 0x22BF and <= 0x22BF
            or >= 0x2312 and <= 0x2312
            or >= 0x2460 and <= 0x24E9
            or >= 0x24EB and <= 0x254B
            or >= 0x2550 and <= 0x2573
            or >= 0x2580 and <= 0x258F
            or >= 0x2592 and <= 0x2595
            or >= 0x25A0 and <= 0x25A1
            or >= 0x25A3 and <= 0x25A9
            or >= 0x25B2 and <= 0x25B3
            or >= 0x25B6 and <= 0x25B7
            or >= 0x25BC and <= 0x25BD
            or >= 0x25C0 and <= 0x25C1
            or >= 0x25C6 and <= 0x25C8
            or >= 0x25CB and <= 0x25CB
            or >= 0x25CE and <= 0x25D1
            or >= 0x25E2 and <= 0x25E5
            or >= 0x25EF and <= 0x25EF
            or >= 0x2605 and <= 0x2606
            or >= 0x2609 and <= 0x2609
            or >= 0x260E and <= 0x260F
            or >= 0x261C and <= 0x261C
            or >= 0x261E and <= 0x261E
            or >= 0x2640 and <= 0x2640
            or >= 0x2642 and <= 0x2642
            or >= 0x2660 and <= 0x2661
            or >= 0x2663 and <= 0x2665
            or >= 0x2667 and <= 0x266A
            or >= 0x266C and <= 0x266D
            or >= 0x266F and <= 0x266F
            or >= 0x269E and <= 0x269F
            or >= 0x26BF and <= 0x26BF
            or >= 0x26C6 and <= 0x26CD
            or >= 0x26CF and <= 0x26D3
            or >= 0x26D5 and <= 0x26E1
            or >= 0x26E3 and <= 0x26E3
            or >= 0x26E8 and <= 0x26E9
            or >= 0x26EB and <= 0x26F1
            or >= 0x26F4 and <= 0x26F4
            or >= 0x26F6 and <= 0x26F9
            or >= 0x26FB and <= 0x26FC
            or >= 0x26FE and <= 0x26FF
            or >= 0x273D and <= 0x273D
            or >= 0x2776 and <= 0x277F
            or >= 0x2B56 and <= 0x2B59
            or >= 0x3248 and <= 0x324F
            or >= 0xE000 and <= 0xF8FF
            or >= 0xFE00 and <= 0xFE0F
            or >= 0xFFFD and <= 0xFFFD
            or >= 0x1F100 and <= 0x1F10A
            or >= 0x1F110 and <= 0x1F12D
            or >= 0x1F130 and <= 0x1F169
            or >= 0x1F170 and <= 0x1F18D
            or >= 0x1F18F and <= 0x1F190
            or >= 0x1F19B and <= 0x1F1AC;
}

public readonly record struct ClampedTextResult(string Text, bool Truncated, int TruncatedCharCount = 0);
