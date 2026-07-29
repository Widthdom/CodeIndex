namespace CodeIndex.Database;

/// <summary>
/// Normalizes escaped source identifiers for exact search across C#, Java, and Kotlin.
/// C# / Java / Kotlin の exact 検索向けに、source 側の escaped identifier を正規化する。
/// </summary>
internal static class ExactSourceSearchNormalizer
{
    internal static string Normalize(string text, string? lang)
    {
        var kind = GetLanguageKind(lang);
        return kind switch
        {
            SourceLanguageKind.CSharp => NormalizeCSharp(text),
            SourceLanguageKind.Java => NormalizeJavaUnicodeEscapes(text),
            SourceLanguageKind.Kotlin => NormalizeKotlin(text),
            _ => text,
        };
    }

    internal static string Normalize(string text, string? lang, out int[] rawIndexMap)
    {
        var kind = GetLanguageKind(lang);
        return kind switch
        {
            SourceLanguageKind.CSharp => NormalizeCSharp(text, out rawIndexMap),
            SourceLanguageKind.Java => NormalizeJavaUnicodeEscapes(text, out rawIndexMap),
            SourceLanguageKind.Kotlin => NormalizeKotlin(text, out rawIndexMap),
            _ => Identity(text, out rawIndexMap),
        };
    }

    private static SourceLanguageKind GetLanguageKind(string? lang)
        => string.IsNullOrWhiteSpace(lang) ? SourceLanguageKind.Other : lang.Trim().ToLowerInvariant() switch
        {
            "csharp" or "cs" => SourceLanguageKind.CSharp,
            "java" => SourceLanguageKind.Java,
            "kotlin" or "kt" or "kts" => SourceLanguageKind.Kotlin,
            _ => SourceLanguageKind.Other,
        };

    private static string NormalizeCSharp(string text)
    {
        if (text.Length == 0
            || (text.IndexOf('@') < 0
                && text.IndexOf("global::", StringComparison.Ordinal) < 0
                && text.IndexOf('\\') < 0))
            return text;

        var decoded = NormalizeCSharpUnicodeEscapes(text, out _);
        return CSharpVerbatimNameNormalizer.Normalize(decoded);
    }

    private static string NormalizeCSharp(string text, out int[] rawIndexMap)
    {
        if (text.Length == 0)
            return Identity(text, out rawIndexMap);

        if (text.IndexOf('@') < 0
            && text.IndexOf("global::", StringComparison.Ordinal) < 0
            && text.IndexOf('\\') < 0)
            return Identity(text, out rawIndexMap);

        var decoded = NormalizeCSharpUnicodeEscapes(text, out var decodedMap);
        var normalized = CSharpVerbatimNameNormalizer.Normalize(decoded, out var normalizedMap);
        rawIndexMap = RemapIndexMap(normalizedMap, decodedMap);
        return normalized;
    }

    internal static string NormalizeCSharpUnicodeEscapes(string text, out int[] rawIndexMap)
    {
        if (text.Length == 0 || text.IndexOf('\\') < 0)
            return Identity(text, out rawIndexMap);

        var sb = new System.Text.StringBuilder(text.Length);
        var map = new System.Collections.Generic.List<int>(text.Length);
        bool changed = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (TryConsumeCSharpUnicodeEscape(text, i, out var decoded, out var consumed))
            {
                sb.Append(decoded);
                for (int j = 0; j < decoded.Length; j++)
                    map.Add(i);
                i += consumed - 1;
                changed = true;
                continue;
            }

            sb.Append(text[i]);
            map.Add(i);
        }

        if (!changed)
            return Identity(text, out rawIndexMap);

        rawIndexMap = map.ToArray();
        return sb.ToString();
    }

    private static string NormalizeJavaUnicodeEscapes(string text)
    {
        if (text.Length == 0 || text.IndexOf('\\') < 0)
            return text;

        var sb = new System.Text.StringBuilder(text.Length);
        bool changed = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (TryConsumeJavaUnicodeEscape(text, i, out var decoded, out var consumed))
            {
                sb.Append(decoded);
                i += consumed - 1;
                changed = true;
                continue;
            }

            sb.Append(text[i]);
        }

        return changed ? sb.ToString() : text;
    }

    private static string NormalizeJavaUnicodeEscapes(string text, out int[] rawIndexMap)
    {
        if (text.Length == 0 || text.IndexOf('\\') < 0)
            return Identity(text, out rawIndexMap);

        var sb = new System.Text.StringBuilder(text.Length);
        var map = new System.Collections.Generic.List<int>(text.Length);
        bool changed = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (TryConsumeJavaUnicodeEscape(text, i, out var decoded, out var consumed))
            {
                sb.Append(decoded);
                map.Add(i);
                i += consumed - 1;
                changed = true;
                continue;
            }

            sb.Append(text[i]);
            map.Add(i);
        }

        if (!changed)
            return Identity(text, out rawIndexMap);

        rawIndexMap = map.ToArray();
        return sb.ToString();
    }

    private static string NormalizeKotlin(string text)
    {
        if (text.Length == 0 || (text.IndexOf('`') < 0 && text.IndexOf('\\') < 0))
            return text;

        var decoded = NormalizeJavaUnicodeEscapes(text, out var decodedMap);
        return StripKotlinBackticks(decoded, decodedMap, out _);
    }

    private static string NormalizeKotlin(string text, out int[] rawIndexMap)
    {
        if (text.Length == 0 || (text.IndexOf('`') < 0 && text.IndexOf('\\') < 0))
            return Identity(text, out rawIndexMap);

        var decoded = NormalizeJavaUnicodeEscapes(text, out var decodedMap);
        if (decoded.IndexOf('`') < 0)
        {
            rawIndexMap = decodedMap;
            return decoded;
        }

        return StripKotlinBackticks(decoded, decodedMap, out rawIndexMap);
    }

    private static string StripKotlinBackticks(string text, int[] sourceIndexMap, out int[] rawIndexMap)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var map = new System.Collections.Generic.List<int>(text.Length);
        bool atBoundary = true;
        bool inBackticks = false;
        bool changed = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!inBackticks && atBoundary && c == '`')
            {
                inBackticks = true;
                atBoundary = false;
                changed = true;
                continue;
            }

            if (inBackticks && c == '`')
            {
                inBackticks = false;
                atBoundary = true;
                changed = true;
                continue;
            }

            sb.Append(c);
            map.Add(sourceIndexMap[i]);
            if (c == '.')
            {
                atBoundary = true;
            }
            else if (c == ':' && i + 1 < text.Length && text[i + 1] == ':')
            {
                sb.Append(':');
                map.Add(sourceIndexMap[++i]);
                atBoundary = true;
            }
            else
            {
                atBoundary = !IsIdentifierPartChar(c);
            }
        }

        if (!changed)
        {
            rawIndexMap = sourceIndexMap;
            return text;
        }

        rawIndexMap = map.ToArray();
        return sb.ToString();
    }

    private static bool TryConsumeCSharpUnicodeEscape(string text, int index, out string decoded, out int consumed)
    {
        decoded = string.Empty;
        consumed = 0;
        if (index + 2 > text.Length || text[index] != '\\')
            return false;

        var escapeMarker = text[index + 1];
        if (escapeMarker == 'u')
        {
            if (!TryReadHexScalar(text, index + 2, 4, out var value))
                return false;

            decoded = ((char)value).ToString();
            consumed = 6;
            return true;
        }

        if (escapeMarker == 'U')
        {
            if (!TryReadHexScalar(text, index + 2, 8, out var value)
                || value > 0x10FFFF
                || (value >= 0xD800 && value <= 0xDFFF))
                return false;

            decoded = char.ConvertFromUtf32(value);
            consumed = 10;
            return true;
        }

        return false;
    }

    private static bool TryConsumeJavaUnicodeEscape(string text, int index, out char decoded, out int consumed)
    {
        decoded = default;
        consumed = 0;
        if (index >= text.Length || text[index] != '\\')
            return false;

        int i = index + 1;
        int uCount = 0;
        while (i < text.Length && text[i] == 'u')
        {
            uCount++;
            i++;
        }

        if (uCount == 0 || i + 4 > text.Length)
            return false;

        int value = 0;
        for (int j = 0; j < 4; j++)
        {
            char hex = text[i + j];
            int nibble = hex switch
            {
                >= '0' and <= '9' => hex - '0',
                >= 'a' and <= 'f' => hex - 'a' + 10,
                >= 'A' and <= 'F' => hex - 'A' + 10,
                _ => -1,
            };
            if (nibble < 0)
                return false;
            value = (value << 4) | nibble;
        }

        decoded = (char)value;
        consumed = 1 + uCount + 4;
        return true;
    }

    private static bool TryReadHexScalar(string text, int start, int length, out int value)
    {
        value = 0;
        if (start + length > text.Length)
            return false;

        long scalar = 0;
        for (int j = 0; j < length; j++)
        {
            char hex = text[start + j];
            int nibble = hex switch
            {
                >= '0' and <= '9' => hex - '0',
                >= 'a' and <= 'f' => hex - 'a' + 10,
                >= 'A' and <= 'F' => hex - 'A' + 10,
                _ => -1,
            };
            if (nibble < 0)
                return false;

            scalar = (scalar << 4) | (long)nibble;
            if (scalar > int.MaxValue)
                return false;
        }

        value = (int)scalar;
        return true;
    }

    private static bool IsIdentifierStartChar(char c) =>
        c == '_' || char.IsLetter(c);

    private static bool IsIdentifierPartChar(char c) =>
        IsIdentifierStartChar(c) || char.IsDigit(c);

    private static string Identity(string text, out int[] rawIndexMap)
    {
        rawIndexMap = IdentityMap(text.Length);
        return text;
    }

    private static int[] RemapIndexMap(int[] normalizedMap, int[] sourceMap)
    {
        if (normalizedMap.Length == sourceMap.Length)
        {
            bool identity = true;
            for (int i = 0; i < normalizedMap.Length; i++)
            {
                if (normalizedMap[i] != i)
                {
                    identity = false;
                    break;
                }
            }

            if (identity)
                return sourceMap;
        }

        var map = new int[normalizedMap.Length];
        for (int i = 0; i < normalizedMap.Length; i++)
            map[i] = sourceMap[normalizedMap[i]];
        return map;
    }

    private static int[] IdentityMap(int length)
    {
        var map = new int[length];
        for (int i = 0; i < length; i++)
            map[i] = i;
        return map;
    }

    private enum SourceLanguageKind
    {
        Other,
        CSharp,
        Java,
        Kotlin,
    }
}
