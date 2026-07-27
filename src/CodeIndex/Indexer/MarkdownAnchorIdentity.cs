using System.Text;

namespace CodeIndex.Indexer;

internal static class MarkdownAnchorIdentity
{
    internal static string Normalize(string value)
    {
        var anchor = value.Trim();
        if (anchor.Length >= 2 && anchor[0] == '<' && anchor[^1] == '>')
            anchor = anchor[1..^1].Trim();
        anchor = anchor.TrimStart('#').Trim();
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

        anchor = anchor.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(anchor.Length);
        var previousDash = false;
        foreach (var originalRune in anchor.EnumerateRunes())
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
        var baseIdentity = Normalize(headingText);
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
}
