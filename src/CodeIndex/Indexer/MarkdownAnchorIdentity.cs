using System.Net;
using System.Text;

namespace CodeIndex.Indexer;

internal static class MarkdownAnchorIdentity
{
    internal static string NormalizeHeadingFragment(string value) =>
        Slugify(DecodeFragment(value));

    internal static string NormalizeExplicitAnchorDefinition(string value) =>
        WebUtility.HtmlDecode(value.Trim());

    internal static string DecodeExplicitAnchorFragment(string value) =>
        DecodeFragment(value);

    private static string DecodeFragment(string value)
    {
        var anchor = value.Trim();
        if (anchor.Length >= 2 && anchor[0] == '<' && anchor[^1] == '>')
            anchor = anchor[1..^1].Trim();
        if (anchor.Length == 0)
            return string.Empty;

        anchor = WebUtility.HtmlDecode(anchor);
        try
        {
            anchor = Uri.UnescapeDataString(anchor);
        }
        catch (UriFormatException)
        {
            // Preserve malformed fragments as unresolved evidence instead of dropping them.
            // 不正な fragment も削除せず、未解決の参照根拠として保持する。
        }

        return anchor;
    }

    private static string Slugify(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var previousDash = false;
        foreach (var originalRune in normalized.EnumerateRunes())
        {
            var rune = Rune.ToLowerInvariant(originalRune);
            if (Rune.IsLetterOrDigit(rune) || rune.Value is '_' or '-')
            {
                builder.Append(rune.ToString());
                previousDash = rune.Value == '-';
            }
            else if (Rune.IsWhiteSpace(rune) && builder.Length > 0 && !previousDash)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        while (builder.Length > 0 && builder[^1] == '-')
            builder.Length--;

        return builder.ToString();
    }

    internal static string CreateUniqueHeadingIdentity(string headingText, HashSet<string> usedIdentities)
    {
        var baseIdentity = Slugify(FlattenHeadingInlineMarkup(headingText));
        if (baseIdentity.Length == 0)
            return string.Empty;
        if (usedIdentities.Add(baseIdentity))
            return baseIdentity;

        for (var suffix = 1; ; suffix++)
        {
            var candidate = $"{baseIdentity}-{suffix}";
            if (usedIdentities.Add(candidate))
                return candidate;
        }
    }

    private static string FlattenHeadingInlineMarkup(string value)
    {
        var builder = new StringBuilder(value.Length);
        AppendHeadingInlineText(value, builder);
        return WebUtility.HtmlDecode(builder.ToString());
    }

    private static void AppendHeadingInlineText(string value, StringBuilder builder)
    {
        for (var index = 0; index < value.Length;)
        {
            if (value[index] == '\\' && index + 1 < value.Length)
            {
                builder.Append(value[index + 1]);
                index += 2;
                continue;
            }

            if (value[index] == '<' && TryFindHtmlTagEnd(value, index, out var tagEnd))
            {
                index = tagEnd + 1;
                continue;
            }

            if (value[index] == '`')
            {
                var delimiterLength = 1;
                while (index + delimiterLength < value.Length
                       && value[index + delimiterLength] == '`')
                {
                    delimiterLength++;
                }

                var closing = value.IndexOf(
                    new string('`', delimiterLength),
                    index + delimiterLength,
                    StringComparison.Ordinal);
                if (closing >= 0)
                {
                    builder.Append(value, index + delimiterLength, closing - index - delimiterLength);
                    index = closing + delimiterLength;
                    continue;
                }
            }

            if (value[index] == '_'
                && TryFindClosingEmphasisDelimiter(value, index, out var emphasisEnd, out var emphasisLength))
            {
                AppendHeadingInlineText(
                    value[(index + emphasisLength)..emphasisEnd],
                    builder);
                index = emphasisEnd + emphasisLength;
                continue;
            }

            var labelStart = value[index] == '!'
                && index + 1 < value.Length
                && value[index + 1] == '['
                    ? index + 1
                    : index;
            if (value[labelStart] == '['
                && TryFindClosingDelimiter(value, labelStart, '[', ']', out var labelEnd))
            {
                AppendHeadingInlineText(value[(labelStart + 1)..labelEnd], builder);
                index = labelEnd + 1;
                if (index < value.Length
                    && value[index] == '('
                    && TryFindClosingDelimiter(value, index, '(', ')', out var destinationEnd))
                {
                    index = destinationEnd + 1;
                }
                else if (index < value.Length
                         && value[index] == '['
                         && TryFindClosingDelimiter(value, index, '[', ']', out var referenceEnd))
                {
                    index = referenceEnd + 1;
                }
                continue;
            }

            builder.Append(value[index]);
            index++;
        }
    }

    private static bool TryFindClosingEmphasisDelimiter(
        string value,
        int openingIndex,
        out int closingIndex,
        out int delimiterLength)
    {
        delimiterLength = 1;
        while (openingIndex + delimiterLength < value.Length
               && value[openingIndex + delimiterLength] == '_')
        {
            delimiterLength++;
        }

        closingIndex = -1;
        var contentStart = openingIndex + delimiterLength;
        if (contentStart >= value.Length
            || char.IsWhiteSpace(value[contentStart])
            || IsIntrawordUnderscore(value, openingIndex))
        {
            return false;
        }

        for (var index = contentStart; index < value.Length;)
        {
            if (value[index] != '_')
            {
                index++;
                continue;
            }

            var runLength = 1;
            while (index + runLength < value.Length
                   && value[index + runLength] == '_')
            {
                runLength++;
            }

            if (runLength == delimiterLength
                && index > contentStart
                && !char.IsWhiteSpace(value[index - 1])
                && !IsIntrawordUnderscore(value, index))
            {
                closingIndex = index;
                return true;
            }

            index += runLength;
        }

        return false;
    }

    private static bool IsIntrawordUnderscore(string value, int index) =>
        index > 0
        && index + 1 < value.Length
        && char.IsLetterOrDigit(value[index - 1])
        && char.IsLetterOrDigit(value[index + 1]);

    private static bool TryFindClosingDelimiter(
        string value,
        int openingIndex,
        char opening,
        char closing,
        out int closingIndex)
    {
        var depth = 1;
        for (var index = openingIndex + 1; index < value.Length; index++)
        {
            if (value[index] == '\\')
            {
                index++;
                continue;
            }
            if (value[index] == opening)
            {
                depth++;
                continue;
            }
            if (value[index] != closing || --depth != 0)
                continue;

            closingIndex = index;
            return true;
        }

        closingIndex = -1;
        return false;
    }

    private static bool TryFindHtmlTagEnd(string value, int openingIndex, out int tagEnd)
    {
        tagEnd = -1;
        if (IsMarkdownAutolink(value, openingIndex))
            return false;

        var index = openingIndex + 1;
        if (index >= value.Length)
            return false;

        if (value.AsSpan(index).StartsWith("!--", StringComparison.Ordinal))
        {
            var commentEnd = value.IndexOf("-->", index + 3, StringComparison.Ordinal);
            if (commentEnd < 0)
                return false;
            tagEnd = commentEnd + 2;
            return true;
        }

        if (value[index] is '!' or '?')
            index++;
        else
        {
            if (value[index] == '/')
                index++;
            var nameStart = index;
            while (index < value.Length
                   && (char.IsAsciiLetterOrDigit(value[index]) || value[index] is '-' or ':'))
            {
                index++;
            }
            if (index == nameStart
                || (index < value.Length
                    && !char.IsWhiteSpace(value[index])
                    && value[index] is not ('/' or '>')))
            {
                return false;
            }
        }

        var quote = '\0';
        for (; index < value.Length; index++)
        {
            var current = value[index];
            if (quote != '\0')
            {
                if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }
            if (current == '>')
            {
                tagEnd = index;
                return true;
            }
        }

        return false;
    }

    private static bool IsMarkdownAutolink(string value, int openingIndex)
    {
        var closing = value.IndexOf('>', openingIndex + 1);
        if (closing < 0)
            return false;

        var target = value.AsSpan(openingIndex + 1, closing - openingIndex - 1);
        if (target.IsEmpty || target.IndexOfAny(" \t\r\n") >= 0)
            return false;

        return target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
               || target.IndexOf('@') > 0;
    }
}
