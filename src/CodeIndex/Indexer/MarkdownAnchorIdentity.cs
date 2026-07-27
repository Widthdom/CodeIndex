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
        var decoded = WebUtility.HtmlDecode(value);
        var builder = new StringBuilder(decoded.Length);
        for (var index = 0; index < decoded.Length;)
        {
            if (decoded[index] == '\\' && index + 1 < decoded.Length)
            {
                builder.Append(decoded[index + 1]);
                index += 2;
                continue;
            }

            if (decoded[index] == '<' && TryFindHtmlTagEnd(decoded, index, out var tagEnd))
            {
                index = tagEnd + 1;
                continue;
            }

            if (decoded[index] == '`')
            {
                var delimiterLength = 1;
                while (index + delimiterLength < decoded.Length
                       && decoded[index + delimiterLength] == '`')
                {
                    delimiterLength++;
                }

                var closing = decoded.IndexOf(
                    new string('`', delimiterLength),
                    index + delimiterLength,
                    StringComparison.Ordinal);
                if (closing >= 0)
                {
                    builder.Append(decoded, index + delimiterLength, closing - index - delimiterLength);
                    index = closing + delimiterLength;
                    continue;
                }
            }

            var labelStart = decoded[index] == '!'
                && index + 1 < decoded.Length
                && decoded[index + 1] == '['
                    ? index + 1
                    : index;
            if (decoded[labelStart] == '['
                && TryFindClosingDelimiter(decoded, labelStart, '[', ']', out var labelEnd))
            {
                builder.Append(FlattenHeadingInlineMarkup(decoded[(labelStart + 1)..labelEnd]));
                index = labelEnd + 1;
                if (index < decoded.Length
                    && decoded[index] == '('
                    && TryFindClosingDelimiter(decoded, index, '(', ')', out var destinationEnd))
                {
                    index = destinationEnd + 1;
                }
                else if (index < decoded.Length
                         && decoded[index] == '['
                         && TryFindClosingDelimiter(decoded, index, '[', ']', out var referenceEnd))
                {
                    index = referenceEnd + 1;
                }
                continue;
            }

            builder.Append(decoded[index]);
            index++;
        }

        return builder.ToString();
    }

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
}
