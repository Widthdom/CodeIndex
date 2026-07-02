using System.Text;
using CodeIndex.Database;

namespace CodeIndex.Indexer;

internal static class SqlNameResolver
{
    private static readonly IReadOnlyList<bool> SingleCaseInsensitiveSegment = new[] { false };

    private readonly record struct SqlNameParts(
        string NormalizedName,
        string LeafName,
        int SegmentCount,
        IReadOnlyList<string> Segments,
        IReadOnlyList<bool> CaseSensitiveSegments);
    private readonly record struct QualifiedNameMatch(
        string NormalizedName,
        int SegmentCount,
        IReadOnlyList<string> Segments,
        IReadOnlyList<bool> CaseSensitiveSegments,
        int StartIndex,
        int EndIndexExclusive,
        int LeafStartIndex,
        int LeafEndIndexExclusive);

    public static string NormalizeQualifiedName(string? qualifiedName)
        => ParseParts(qualifiedName).NormalizedName;

    public static string GetLeafName(string? qualifiedName)
        => ParseParts(qualifiedName).LeafName;

    public static int GetSegmentCount(string? qualifiedName)
        => ParseParts(qualifiedName).SegmentCount;

    public static bool HasQualifier(string? qualifiedName)
        => ParseParts(qualifiedName).SegmentCount > 1;

    public static bool ContextContainsQualifiedNameAtColumn(string? context, string? query, int? columnNumber)
    {
        var queryParts = ParseParts(query);
        if (queryParts.NormalizedName.Length == 0 || queryParts.SegmentCount <= 1 || string.IsNullOrWhiteSpace(context))
            return false;

        return TryGetQualifiedNameAtColumn(context, columnNumber, out var match)
            && match.SegmentCount == queryParts.SegmentCount
            && QualifiedNamesEqual(match.Segments, match.CaseSensitiveSegments, queryParts.Segments, queryParts.CaseSensitiveSegments);
    }

    public static bool ContextContainsQualifiedNameLikeAtColumn(string? context, string? query, int? columnNumber)
    {
        var queryParts = ParseParts(query);
        if (queryParts.NormalizedName.Length == 0 || queryParts.SegmentCount <= 1 || string.IsNullOrWhiteSpace(context))
            return false;

        return TryGetQualifiedNameAtColumn(context, columnNumber, out var match)
            && match.SegmentCount == queryParts.SegmentCount
            && QualifiedNamesEqual(match.Segments, match.CaseSensitiveSegments, queryParts.Segments, queryParts.CaseSensitiveSegments);
    }

    public static bool ContextContainsQualifiedName(string? context, string? query)
    {
        var queryParts = ParseParts(query);
        if (queryParts.NormalizedName.Length == 0 || queryParts.SegmentCount <= 1 || string.IsNullOrWhiteSpace(context))
            return false;

        foreach (var candidate in EnumerateQualifiedNameMatches(context))
        {
            if (candidate.SegmentCount == queryParts.SegmentCount
                && QualifiedNamesEqual(candidate.Segments, candidate.CaseSensitiveSegments, queryParts.Segments, queryParts.CaseSensitiveSegments))
            {
                return true;
            }
        }

        return false;
    }

    public static string ResolveReferenceName(string? symbolName, string? context, string? containerName)
    {
        var normalizedSymbolName = NormalizeQualifiedName(symbolName);
        if (normalizedSymbolName.Length == 0)
            return string.Empty;

        if (string.IsNullOrWhiteSpace(context))
            return normalizedSymbolName;

        var leafName = GetLeafName(symbolName);
        if (leafName.Length == 0)
            return normalizedSymbolName;

        List<string>? candidates = null;
        HashSet<string>? seenCandidates = null;
        foreach (var candidate in EnumerateQualifiedNames(context))
        {
            if (!string.Equals(GetLeafName(candidate), leafName, StringComparison.OrdinalIgnoreCase))
                continue;

            seenCandidates ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!seenCandidates.Add(candidate))
                continue;

            candidates ??= [];
            candidates.Add(candidate);
        }

        if (candidates == null || candidates.Count == 0)
            return normalizedSymbolName;
        if (candidates.Count == 1)
            return candidates[0];

        var normalizedContainerName = NormalizeQualifiedName(containerName);
        if (normalizedContainerName.Length > 0)
        {
            string? nonContainerCandidate = null;
            var nonContainerCandidateCount = 0;
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate, normalizedContainerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                nonContainerCandidate = candidate;
                nonContainerCandidateCount++;
                if (nonContainerCandidateCount > 1)
                    break;
            }

            if (nonContainerCandidateCount == 1)
                return nonContainerCandidate!;
        }

        return normalizedSymbolName;
    }

    public static string ResolveReferenceNameAtColumn(string? symbolName, string? context, string? containerName, int? columnNumber)
    {
        var normalizedSymbolName = NormalizeQualifiedName(symbolName);
        if (normalizedSymbolName.Length == 0)
            return string.Empty;
        if (string.IsNullOrWhiteSpace(context))
            return normalizedSymbolName;

        var leafName = GetLeafName(symbolName);
        if (leafName.Length > 0 && columnNumber.HasValue && columnNumber.Value > 0)
        {
            if (TryGetQualifiedNameAtColumn(context, columnNumber, out var match)
                && LeafNamesEqual(match.Segments, match.CaseSensitiveSegments, ParseParts(symbolName)))
            {
                return match.NormalizedName;
            }

            return QualifyLeafNameFromContainerCore(normalizedSymbolName, containerName);
        }

        return ResolveReferenceName(symbolName, context, containerName);
    }

    public static string ResolveReferenceNameFolded(string? symbolName, string? context, string? containerName)
    {
        var resolved = ResolveReferenceName(symbolName, context, containerName);
        return resolved.Length == 0 ? string.Empty : NameFold.Fold(resolved) ?? resolved;
    }

    public static string ResolveReferenceNameFoldedAtColumn(string? symbolName, string? context, string? containerName, int? columnNumber)
    {
        var resolved = ResolveReferenceNameAtColumn(symbolName, context, containerName, columnNumber);
        return resolved.Length == 0 ? string.Empty : NameFold.Fold(resolved) ?? resolved;
    }

    public static int ResolveReferenceSegmentCountAtColumn(string? symbolName, string? context, string? containerName, int? columnNumber)
    {
        var normalizedSymbolName = NormalizeQualifiedName(symbolName);
        if (normalizedSymbolName.Length == 0)
            return 0;
        if (string.IsNullOrWhiteSpace(context))
            return GetSegmentCount(normalizedSymbolName);

        var leafName = GetLeafName(symbolName);
        if (leafName.Length > 0 && columnNumber.HasValue && columnNumber.Value > 0)
        {
            if (TryGetQualifiedNameAtColumn(context, columnNumber, out var match)
                && string.Equals(GetLeafName(match.NormalizedName), leafName, StringComparison.OrdinalIgnoreCase))
            {
                return match.SegmentCount;
            }

            return GetSegmentCount(QualifyLeafNameFromContainerCore(normalizedSymbolName, containerName));
        }

        return GetSegmentCount(ResolveReferenceName(symbolName, context, containerName));
    }

    public static bool ReferenceMatchesTargetAtColumn(
        string? symbolName,
        string? context,
        string? containerName,
        int? columnNumber,
        string? targetName)
    {
        var targetParts = ParseParts(targetName);
        if (targetParts.NormalizedName.Length == 0)
            return false;

        var resolved = ResolveReferenceNameAtColumn(symbolName, context, containerName, columnNumber);
        if (resolved.Length == 0)
            return false;

        if (TryGetQualifiedNameAtColumn(context, columnNumber, out var match))
            return QualifiedNamesEqual(match.Segments, match.CaseSensitiveSegments, targetParts.Segments, targetParts.CaseSensitiveSegments);

        var resolvedParts = ParseParts(resolved);
        return QualifiedNamesEqual(resolvedParts.Segments, resolvedParts.CaseSensitiveSegments, targetParts.Segments, targetParts.CaseSensitiveSegments);
    }

    public static bool AllowLeafFallbackAtColumn(string? symbolName, string? context, string? containerName, int? columnNumber)
    {
        var normalizedSymbolName = NormalizeQualifiedName(symbolName);
        if (normalizedSymbolName.Length == 0 || HasQualifier(normalizedSymbolName))
            return false;

        var leafName = GetLeafName(symbolName);
        if (leafName.Length > 0
            && TryGetQualifiedNameAtColumn(context, columnNumber, out var match)
            && LeafNamesEqual(match.Segments, match.CaseSensitiveSegments, ParseParts(symbolName)))
        {
            return false;
        }

        if (leafName.Length > 0 && !string.IsNullOrWhiteSpace(context))
        {
            foreach (var candidate in EnumerateQualifiedNames(context))
            {
                if (HasQualifier(candidate)
                    && string.Equals(GetLeafName(candidate), leafName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return !IsQuotedSingleIdentifierWithDots(containerName);
    }

    public static bool ContextContainsQualifiedNameFoldedAtColumn(string? context, string? query, int? columnNumber)
    {
        var queryParts = ParseParts(query);
        if (queryParts.NormalizedName.Length == 0 || queryParts.SegmentCount <= 1 || string.IsNullOrWhiteSpace(context))
            return false;
        if (!TryGetQualifiedNameAtColumn(context, columnNumber, out var match))
            return false;

        return match.SegmentCount == queryParts.SegmentCount
            && QualifiedNamesEqualFolded(match.Segments, match.CaseSensitiveSegments, queryParts.Segments, queryParts.CaseSensitiveSegments);
    }

    public static bool ContextContainsQualifiedNameLikeFoldedAtColumn(string? context, string? query, int? columnNumber)
    {
        var queryParts = ParseParts(query);
        if (queryParts.NormalizedName.Length == 0 || queryParts.SegmentCount <= 1 || string.IsNullOrWhiteSpace(context))
            return false;
        if (!TryGetQualifiedNameAtColumn(context, columnNumber, out var match))
            return false;

        return match.SegmentCount == queryParts.SegmentCount
            && QualifiedNamesEqualFolded(match.Segments, match.CaseSensitiveSegments, queryParts.Segments, queryParts.CaseSensitiveSegments);
    }

    public static bool ContextContainsQualifiedNameFolded(string? context, string? query)
    {
        var queryParts = ParseParts(query);
        if (queryParts.NormalizedName.Length == 0 || queryParts.SegmentCount <= 1 || string.IsNullOrWhiteSpace(context))
            return false;

        foreach (var candidate in EnumerateQualifiedNameMatches(context))
        {
            if (candidate.SegmentCount == queryParts.SegmentCount
                && QualifiedNamesEqualFolded(candidate.Segments, candidate.CaseSensitiveSegments, queryParts.Segments, queryParts.CaseSensitiveSegments))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetQualifiedNameAtColumn(string? context, int? columnNumber, out QualifiedNameMatch match)
    {
        match = default;
        if (string.IsNullOrWhiteSpace(context) || !columnNumber.HasValue || columnNumber.Value <= 0)
            return false;

        var zeroBasedColumn = columnNumber.Value - 1;
        foreach (var candidate in EnumerateQualifiedNameMatches(context))
        {
            if (candidate.SegmentCount <= 1 && !candidate.NormalizedName.Contains('.', StringComparison.Ordinal))
                continue;

            if (zeroBasedColumn >= candidate.LeafStartIndex && zeroBasedColumn < candidate.LeafEndIndexExclusive)
            {
                match = candidate;
                return true;
            }

            if (zeroBasedColumn >= candidate.StartIndex
                && zeroBasedColumn < candidate.EndIndexExclusive
                && TryReadQualifiedNamePrefixAtColumn(context, candidate.StartIndex, zeroBasedColumn, out match))
            {
                return true;
            }
        }

        return false;
    }

    private static SqlNameParts ParseParts(string? qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return new SqlNameParts(string.Empty, string.Empty, 0, [], []);

        var trimmed = qualifiedName.Trim();
        if (TryParseSimpleUnquotedSqlName(trimmed, out var simpleParts))
            return simpleParts;

        var segments = new List<string>();
        var caseSensitiveSegments = new List<bool>();
        var current = new StringBuilder();
        char quote = '\0';
        var currentHasCaseSensitiveQuote = false;

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (quote != '\0')
            {
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < trimmed.Length && trimmed[i + 1] == ']')
                        {
                            current.Append(']');
                            i++;
                        }
                        else
                        {
                            quote = '\0';
                        }
                    }
                    else
                    {
                        current.Append(ch);
                    }

                    continue;
                }

                if (ch == quote)
                {
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == quote)
                    {
                        current.Append(quote);
                        i++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch is '[' or '"' or '`')
            {
                quote = ch;
                currentHasCaseSensitiveQuote |= ch == '"';
                continue;
            }

            if (ch == '.')
            {
                AppendNormalizedSegment(segments, caseSensitiveSegments, current, currentHasCaseSensitiveQuote);
                currentHasCaseSensitiveQuote = false;
                continue;
            }

            current.Append(ch);
        }

        AppendNormalizedSegment(segments, caseSensitiveSegments, current, currentHasCaseSensitiveQuote);
        var segmentCount = segments.Count;
        if (segmentCount == 0)
            return new SqlNameParts(string.Empty, string.Empty, 0, [], []);

        var normalized = string.Join(".", segments);
        return new SqlNameParts(normalized, segments[^1], segmentCount, segments, caseSensitiveSegments);
    }

    private static IEnumerable<string> EnumerateQualifiedNames(string text)
    {
        foreach (var match in EnumerateQualifiedNameMatches(text))
        {
            if (match.SegmentCount > 1)
                yield return match.NormalizedName;
        }
    }

    private static IEnumerable<QualifiedNameMatch> EnumerateQualifiedNameMatches(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsSqlIdentifierStartChar(text[i]))
                continue;

            if (!TryReadQualifiedName(text, i, out var match))
                continue;

            yield return match;

            i = Math.Max(i, match.EndIndexExclusive - 1);
        }
    }

    private static string QualifyLeafNameFromContainerCore(string normalizedSymbolName, string? containerName)
    {
        if (normalizedSymbolName.Length == 0 || HasQualifier(normalizedSymbolName))
            return normalizedSymbolName;

        var containerParts = ParseParts(containerName);
        if (containerParts.NormalizedName.Length == 0 || containerParts.SegmentCount <= 1)
            return normalizedSymbolName;

        var lastDot = containerParts.NormalizedName.LastIndexOf('.');
        if (lastDot <= 0)
            return normalizedSymbolName;

        return containerParts.NormalizedName[..(lastDot + 1)] + normalizedSymbolName;
    }

    private static void AppendNormalizedSegment(List<string> segments, List<bool> caseSensitiveSegments, StringBuilder current, bool hasCaseSensitiveQuote)
    {
        var value = current.ToString().Trim();
        if (value.Length > 0)
        {
            segments.Add(value);
            caseSensitiveSegments.Add(hasCaseSensitiveQuote);
        }
        current.Clear();
    }

    private static bool IsQuotedSingleIdentifierWithDots(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var trimmed = name.Trim();
        if (trimmed.Length < 2)
            return false;

        var first = trimmed[0];
        var last = trimmed[^1];
        if (!((first == '[' && last == ']') || (first == '"' && last == '"') || (first == '`' && last == '`')))
            return false;

        var parts = ParseParts(trimmed);
        return parts.SegmentCount == 1
            && parts.NormalizedName.Contains('.', StringComparison.Ordinal);
    }

    private static bool TryReadQualifiedName(string text, int startIndex, out QualifiedNameMatch match)
    {
        match = default;

        var segments = new List<string>();
        var caseSensitiveSegments = new List<bool>();
        var index = startIndex;
        var leafStartIndex = startIndex;
        var leafEndIndexExclusive = startIndex;
        while (true)
        {
            var segmentStartIndex = index;
            if (!TryReadQualifiedNameSegment(text, ref index, out var segment, out var segmentHasCaseSensitiveQuote))
                return false;

            segments.Add(segment);
            caseSensitiveSegments.Add(segmentHasCaseSensitiveQuote);
            leafStartIndex = segmentStartIndex;
            leafEndIndexExclusive = index;
            var scan = index;
            while (scan < text.Length && char.IsWhiteSpace(text[scan]))
                scan++;

            if (scan >= text.Length || text[scan] != '.')
            {
                break;
            }

            scan++;
            while (scan < text.Length && char.IsWhiteSpace(text[scan]))
                scan++;

            if (scan >= text.Length || !IsSqlIdentifierStartChar(text[scan]))
            {
                break;
            }

            index = scan;
        }

        var normalizedName = string.Join(".", segments);
        if (normalizedName.Length == 0)
            return false;

        match = new QualifiedNameMatch(normalizedName, segments.Count, segments, caseSensitiveSegments, startIndex, index, leafStartIndex, leafEndIndexExclusive);
        return true;
    }

    private static bool TryReadQualifiedNameSegment(string text, ref int index, out string segment, out bool hasCaseSensitiveQuote)
    {
        segment = string.Empty;
        hasCaseSensitiveQuote = false;
        if (index >= text.Length)
            return false;

        var quote = text[index];
        if (quote is '[' or '"' or '`')
        {
            var current = new StringBuilder();
            hasCaseSensitiveQuote = quote == '"';
            index++;
            while (index < text.Length)
            {
                var ch = text[index++];
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (index < text.Length && text[index] == ']')
                        {
                            current.Append(']');
                            index++;
                            continue;
                        }

                        segment = current.ToString().Trim();
                        return segment.Length > 0;
                    }

                    current.Append(ch);
                    continue;
                }

                if (ch == quote)
                {
                    if (index < text.Length && text[index] == quote)
                    {
                        current.Append(quote);
                        index++;
                        continue;
                    }

                    segment = current.ToString().Trim();
                    return segment.Length > 0;
                }

                current.Append(ch);
            }

            return false;
        }

        if (!IsSqlIdentifierStartChar(text[index]))
            return false;

        var segmentStart = index++;
        while (index < text.Length && IsSqlIdentifierChar(text[index]))
            index++;

        segment = text[segmentStart..index];
        return true;
    }

    private static bool TryReadQualifiedNamePrefixAtColumn(
        string text,
        int startIndex,
        int zeroBasedColumn,
        out QualifiedNameMatch match)
    {
        match = default;
        var segments = new List<(string Name, bool HasCaseSensitiveQuote, int StartIndex, int EndIndexExclusive)>();
        var index = startIndex;
        while (true)
        {
            var segmentStartIndex = index;
            if (!TryReadQualifiedNameSegment(text, ref index, out var segment, out var segmentHasCaseSensitiveQuote))
                return false;

            segments.Add((segment, segmentHasCaseSensitiveQuote, segmentStartIndex, index));
            var scan = index;
            while (scan < text.Length && char.IsWhiteSpace(text[scan]))
                scan++;

            if (scan >= text.Length || text[scan] != '.')
                break;

            scan++;
            while (scan < text.Length && char.IsWhiteSpace(text[scan]))
                scan++;

            if (scan >= text.Length || !IsSqlIdentifierStartChar(text[scan]))
                break;

            index = scan;
        }

        for (var i = 1; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (zeroBasedColumn < segment.StartIndex || zeroBasedColumn >= segment.EndIndexExclusive)
                continue;

            var segmentCount = i + 1;
            var names = new List<string>(segmentCount);
            var caseSensitiveSegments = new List<bool>(segmentCount);
            var normalizedNameLength = segmentCount - 1;
            for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
                normalizedNameLength += segments[segmentIndex].Name.Length;

            var normalizedNameBuilder = new StringBuilder(normalizedNameLength);
            for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                var part = segments[segmentIndex];
                if (normalizedNameBuilder.Length > 0)
                    normalizedNameBuilder.Append('.');

                normalizedNameBuilder.Append(part.Name);
                names.Add(part.Name);
                caseSensitiveSegments.Add(part.HasCaseSensitiveQuote);
            }

            match = new QualifiedNameMatch(
                normalizedNameBuilder.ToString(),
                segmentCount,
                names,
                caseSensitiveSegments,
                startIndex,
                segment.EndIndexExclusive,
                segment.StartIndex,
                segment.EndIndexExclusive);
            return true;
        }

        return false;
    }

    private static bool IsSqlIdentifierStartChar(char ch)
        => ch is '[' or '"' or '`' or '_' or '$' or '#'
           || char.IsLetter(ch);

    private static bool IsUnquotedSqlIdentifierStartChar(char ch)
        => ch is '_' or '$' or '#'
           || char.IsLetter(ch);

    private static bool IsSqlIdentifierChar(char ch)
        => ch is '_' or '$' or '#'
           || char.IsLetterOrDigit(ch);

    private static bool TryParseSimpleUnquotedSqlName(string value, out SqlNameParts parts)
    {
        parts = default;
        if (value.Length == 0
            || !IsUnquotedSqlIdentifierStartChar(value[0]))
            return false;

        var segmentCount = 1;
        var segmentStart = 0;
        var leafStart = 0;
        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '.')
            {
                if (i == segmentStart
                    || i + 1 >= value.Length
                    || !IsUnquotedSqlIdentifierStartChar(value[i + 1]))
                {
                    return false;
                }

                segmentCount++;
                segmentStart = i + 1;
                leafStart = segmentStart;
                continue;
            }

            if (!IsSqlIdentifierChar(ch))
                return false;
        }

        if (segmentCount == 1)
        {
            parts = new SqlNameParts(value, value, 1, [value], SingleCaseInsensitiveSegment);
            return true;
        }

        var segments = new List<string>(segmentCount);
        var caseSensitiveSegments = new List<bool>(segmentCount);
        segmentStart = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] != '.')
                continue;

            segments.Add(value[segmentStart..i]);
            caseSensitiveSegments.Add(false);
            segmentStart = i + 1;
        }

        parts = new SqlNameParts(value, value[leafStart..], segmentCount, segments, caseSensitiveSegments);
        return true;
    }

    private static bool LeafNamesEqual(
        IReadOnlyList<string> leftSegments,
        IReadOnlyList<bool> leftCaseSensitiveSegments,
        SqlNameParts rightParts)
        => rightParts.SegmentCount > 0
           && leftSegments.Count > 0
           && SegmentsEqual(
               leftSegments[^1],
               leftCaseSensitiveSegments.Count > 0 && leftCaseSensitiveSegments[^1],
               rightParts.LeafName,
               rightParts.CaseSensitiveSegments.Count > 0 && rightParts.CaseSensitiveSegments[^1]);

    private static bool QualifiedNamesEqual(
        IReadOnlyList<string> leftSegments,
        IReadOnlyList<bool> leftCaseSensitiveSegments,
        IReadOnlyList<string> rightSegments,
        IReadOnlyList<bool> rightCaseSensitiveSegments)
    {
        if (leftSegments.Count != rightSegments.Count)
            return false;

        for (var i = 0; i < leftSegments.Count; i++)
        {
            if (!SegmentsEqual(
                    leftSegments[i],
                    i < leftCaseSensitiveSegments.Count && leftCaseSensitiveSegments[i],
                    rightSegments[i],
                    i < rightCaseSensitiveSegments.Count && rightCaseSensitiveSegments[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool QualifiedNamesEqualFolded(
        IReadOnlyList<string> leftSegments,
        IReadOnlyList<bool> leftCaseSensitiveSegments,
        IReadOnlyList<string> rightSegments,
        IReadOnlyList<bool> rightCaseSensitiveSegments)
    {
        if (leftSegments.Count != rightSegments.Count)
            return false;

        for (var i = 0; i < leftSegments.Count; i++)
        {
            var preserveCase = (i < leftCaseSensitiveSegments.Count && leftCaseSensitiveSegments[i])
                || (i < rightCaseSensitiveSegments.Count && rightCaseSensitiveSegments[i]);
            var left = preserveCase ? leftSegments[i] : NameFold.Fold(leftSegments[i]) ?? leftSegments[i];
            var right = preserveCase ? rightSegments[i] : NameFold.Fold(rightSegments[i]) ?? rightSegments[i];
            if (!string.Equals(left, right, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool SegmentsEqual(string left, bool leftCaseSensitive, string right, bool rightCaseSensitive)
        => string.Equals(left, right, leftCaseSensitive || rightCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
