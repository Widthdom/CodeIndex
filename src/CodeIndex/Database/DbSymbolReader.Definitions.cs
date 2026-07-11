using System.Text;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    /// <summary>
    /// Resolve symbol definitions with reconstructed excerpts.
    /// シンボル定義を抜粋付きで解決する。
    /// </summary>
    public List<DefinitionResult> GetDefinitions(string query, int limit = 20, string? kind = null, string? lang = null, bool includeBody = false, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, int? bodyStartLine = null, int? bodyLineCount = null)
    {
        lang = DbReader.NormalizeQueryLanguage(lang);
        var symbols = SearchSymbols(query, limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, since, exact, visibilityFilters, excludeVisibilityFilters);
        var results = new List<DefinitionResult>();

        foreach (var symbol in symbols)
        {
            var definition = BuildDefinitionResult(symbol, includeBody, bodyStartLine, bodyLineCount);
            if (definition != null)
                results.Add(definition);
        }

        return results;
    }

    private DefinitionResult? BuildDefinitionResult(
        SymbolResult symbol,
        bool includeBody,
        int? bodyStartLine = null,
        int? bodyLineCount = null)
    {
        var definitionExcerpt = GetExcerpt(symbol.Path, symbol.StartLine, symbol.EndLine);
        if (definitionExcerpt == null)
            return null;

        string? bodyContent = null;
        int? bodyContentStartLine = null;
        int? bodyContentEndLine = null;
        int? bodyContentNextStartLine = null;
        var bodyContentTruncated = false;
        int? bodyRequestedStartLine = null;
        int? bodyRequestedEndLine = null;
        int? bodyEffectiveStartLine = null;
        int? bodyEffectiveEndLine = null;
        var bodyContentTruncationReasons = new List<string>();
        ExcerptRecoveryHint? bodyContentRecovery = null;
        if (includeBody && symbol.BodyStartLine != null && symbol.BodyEndLine != null)
        {
            var requestedBodyLines = Math.Clamp(
                bodyLineCount ?? DefinitionBodyMaxLines,
                1,
                DefinitionBodyMaxRequestedLines);
            var effectiveBodyStartLine = Math.Clamp(
                bodyStartLine ?? symbol.BodyStartLine.Value,
                symbol.BodyStartLine.Value,
                symbol.BodyEndLine.Value);
            var cappedBodyEndLine = Math.Min(
                symbol.BodyEndLine.Value,
                effectiveBodyStartLine + requestedBodyLines - 1);
            var bodyExcerpt = GetExcerpt(symbol.Path, effectiveBodyStartLine, cappedBodyEndLine);
            if (bodyExcerpt != null)
            {
                bodyRequestedStartLine = symbol.BodyStartLine.Value;
                bodyRequestedEndLine = symbol.BodyEndLine.Value;
                bodyEffectiveStartLine = bodyExcerpt.StartLine;
                bodyEffectiveEndLine = bodyExcerpt.EndLine;
                bodyContent = bodyExcerpt.Content;
                bodyContentStartLine = bodyExcerpt.StartLine;
                bodyContentEndLine = bodyExcerpt.EndLine;
                bodyContentTruncationReasons.AddRange(bodyExcerpt.ContentTruncationReasons);
                bodyContentRecovery = bodyExcerpt.ContentRecovery;
                if (cappedBodyEndLine < symbol.BodyEndLine.Value)
                {
                    bodyContentTruncated = true;
                    AddBodyContentTruncationReason(bodyContentTruncationReasons, "body_line_cap");
                    var recoveryStartLine = cappedBodyEndLine + 1;
                    var recoveryEndLine = Math.Min(symbol.BodyEndLine.Value, recoveryStartLine + DefinitionBodyMaxLines - 1);
                    bodyContentNextStartLine = recoveryStartLine;
                    bodyContentRecovery ??= FileExcerptResult.CreateRecoveryHint(symbol.Path, recoveryStartLine, recoveryEndLine);
                }
                bodyContentTruncated |= bodyExcerpt.ContentTruncated;
                var byteClamp = ClampDefinitionBodyBytes(bodyContent);
                bodyContent = byteClamp.Content;
                if (byteClamp.Truncated)
                {
                    bodyContentTruncated = true;
                    AddBodyContentTruncationReason(bodyContentTruncationReasons, "body_byte_cap");
                    if (byteClamp.ReturnedLineCount > 0)
                    {
                        bodyContentEndLine = Math.Min(
                            bodyExcerpt.EndLine,
                            bodyExcerpt.StartLine + byteClamp.ReturnedLineCount - 1);
                        var nextStartLine = bodyContentEndLine.Value + 1;
                        if (nextStartLine <= symbol.BodyEndLine.Value)
                            bodyContentNextStartLine = nextStartLine;
                    }
                    else
                    {
                        bodyContentEndLine = bodyExcerpt.StartLine;
                        var nextStartLine = bodyExcerpt.StartLine + 1;
                        if (nextStartLine <= symbol.BodyEndLine.Value)
                            bodyContentNextStartLine = nextStartLine;
                    }
                    bodyContentRecovery = FileExcerptResult.CreateRecoveryHint(symbol.Path, bodyExcerpt.StartLine, bodyExcerpt.EndLine);
                }
            }
        }

        return new DefinitionResult
        {
            Path = symbol.Path,
            Lang = symbol.Lang,
            Kind = symbol.Kind,
            SubKind = symbol.SubKind,
            Name = symbol.Name,
            Line = symbol.Line,
            StartLine = symbol.StartLine,
            EndLine = symbol.EndLine,
            BodyStartLine = symbol.BodyStartLine,
            BodyEndLine = symbol.BodyEndLine,
            Signature = symbol.Signature,
            ContainerKind = symbol.ContainerKind,
            ContainerName = symbol.ContainerName,
            Visibility = symbol.Visibility,
            ReturnType = symbol.ReturnType,
            Disambiguator = BuildDefinitionDisambiguator(symbol),
            Content = definitionExcerpt.Content,
            BodyContent = bodyContent,
            BodyContentStartLine = bodyContentStartLine,
            BodyContentEndLine = bodyContentEndLine,
            BodyContentNextStartLine = bodyContentNextStartLine,
            BodyContentTruncated = bodyContentTruncated,
            BodyRequestedStartLine = bodyRequestedStartLine,
            BodyRequestedEndLine = bodyRequestedEndLine,
            BodyEffectiveStartLine = bodyEffectiveStartLine,
            BodyEffectiveEndLine = bodyEffectiveEndLine,
            BodyContentTruncationReasons = bodyContentTruncationReasons.Count > 0 ? bodyContentTruncationReasons : null,
            BodyContentRecovery = bodyContentRecovery,
            Complexity = bodyContent != null && !bodyContentTruncated
                ? SymbolExtractor.EstimateComplexity(bodyContent)
                : null,
        };
    }

    private static void AddBodyContentTruncationReason(List<string> reasons, string reason)
    {
        if (!reasons.Any(existing => string.Equals(existing, reason, StringComparison.Ordinal)))
            reasons.Add(reason);
    }

    private static (string Content, bool Truncated, int ReturnedLineCount) ClampDefinitionBodyBytes(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length <= DefinitionBodyMaxBytes)
            return (content, false, CountReturnedBodyLines(content));

        var byteCount = DefinitionBodyMaxBytes;
        while (byteCount > 0 && IsUtf8ContinuationByte(bytes[byteCount]))
            byteCount--;

        var clamped = Encoding.UTF8.GetString(bytes, 0, byteCount);
        return (
            clamped,
            true,
            CountReturnedBodyLines(clamped));
    }

    private static bool IsUtf8ContinuationByte(byte value) => (value & 0xC0) == 0x80;

    private static int CountReturnedBodyLines(string content)
    {
        if (content.Length == 0)
            return 0;

        var lineBreaks = 0;
        foreach (var ch in content)
        {
            if (ch == '\n')
                lineBreaks++;
        }

        return content[^1] == '\n'
            ? lineBreaks
            : lineBreaks + 1;
    }

    private static string? BuildDefinitionDisambiguator(SymbolResult symbol)
    {
        if (!string.Equals(symbol.Lang, "csharp", StringComparison.OrdinalIgnoreCase))
            return null;

        var signature = symbol.Signature;
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        if (signature.Contains(" partial ", StringComparison.Ordinal)
            || signature.Contains("partial class ", StringComparison.Ordinal)
            || signature.Contains("partial struct ", StringComparison.Ordinal)
            || signature.Contains("partial interface ", StringComparison.Ordinal))
            return "partial-" + (symbol.Kind ?? "definition");

        if (signature.Contains("(this ", StringComparison.Ordinal)
            || signature.Contains(", this ", StringComparison.Ordinal))
        {
            var receiver = ExtractExtensionReceiver(signature);
            return receiver == null ? "extension-method" : $"extension-method-on({receiver})";
        }

        if (symbol.Kind == "function")
        {
            var parameters = ExtractParameterTypeList(signature);
            if (parameters != null)
                return $"overload({parameters})";
        }

        return null;
    }

    private static string? ExtractExtensionReceiver(string signature)
    {
        var parameters = ExtractParameters(signature);
        if (parameters == null)
            return null;

        var firstParameter = parameters.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        const string ThisPrefix = "this ";
        if (!firstParameter.StartsWith(ThisPrefix, StringComparison.Ordinal))
            return null;

        var withoutThis = firstParameter[ThisPrefix.Length..].Trim();
        var parts = withoutThis.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : null;
    }

    private static string? ExtractParameterTypeList(string signature)
    {
        var parameters = ExtractParameters(signature);
        if (parameters == null)
            return null;
        if (string.IsNullOrWhiteSpace(parameters))
            return "";

        var types = parameters
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ExtractParameterType)
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .ToList();
        return types.Count > 0 ? string.Join(", ", types) : null;
    }

    private static string? ExtractParameters(string signature)
    {
        var open = signature.IndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close <= open)
            return null;
        return signature.Substring(open + 1, close - open - 1).Trim();
    }

    private static string ExtractParameterType(string parameter)
    {
        var tokens = parameter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return string.Empty;
        var start = tokens[0] is "this" or "ref" or "out" or "in" or "params" ? 1 : 0;
        if (start >= tokens.Length)
            return string.Empty;
        var end = Math.Max(start + 1, tokens.Length - 1);
        return string.Join(" ", tokens[start..end]);
    }

    public QueryCountResult CountDefinitionsTotal(string query, string? kind = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null, bool exact = false, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var normalizedQuery = NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact);
        using var cmd = _conn.CreateCommand();

        var sql = $@"
            SELECT COUNT(*), COUNT(DISTINCT path)
            FROM (
                SELECT f.path AS path
                FROM symbols s
                JOIN files f ON s.file_id = f.id
                WHERE 1=1";

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            var rustQualifiedExact = ShouldPreserveRustQualifiedExactQuery(normalizedQuery, lang, exact);
            var rustQualifiedParts = rustQualifiedExact ? NormalizeRustQualifiedExactQueryParts(normalizedQuery) : default;
            var allowLeafFallback = !SqlNameResolver.HasQualifier(normalizedQuery);
            var qualifiedSymbolClause = SqlNameResolver.HasQualifier(normalizedQuery)
                ? BuildQualifiedSymbolMatchSql("query", _foldReady)
                : null;
            sql += exact
                ? rustQualifiedParts.QualifiedPath != null
                    ? _foldReady
                        ? " AND ((s.container_qualified_name = @queryRustContainer COLLATE NOCASE OR s.container_name = @queryRustContainer COLLATE NOCASE) AND s.name_folded = @queryRustLeafFolded)"
                        : " AND ((s.container_qualified_name = @queryRustContainer COLLATE NOCASE OR s.container_name = @queryRustContainer COLLATE NOCASE) AND s.name = @queryRustLeaf COLLATE NOCASE)"
                    : _foldReady
                        ? allowLeafFallback
                            ? " AND (s.name_folded = @query OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded) OR sql_leaf_name_folded(s.name) = @queryLeafFolded)))"
                            : $" AND (s.name_folded = @query OR (f.lang = 'sql' AND sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})"
                        : allowLeafFallback
                            ? " AND (s.name = @query COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name(s.name) = @queryNormalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @queryLeaf COLLATE NOCASE)))"
                            : $" AND (s.name = @query COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name(s.name) = @queryNormalized COLLATE NOCASE){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})"
                : $" AND (s.name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND sql_normalize_name(s.name) LIKE @queryNormalizedLike ESCAPE '\\'){(qualifiedSymbolClause != null ? $" OR {qualifiedSymbolClause}" : string.Empty)})";
        }
        if (kind != null)
            sql += " AND s.kind = @kind";
        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @since";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);
        sql += $@"
                  AND EXISTS (
                      SELECT 1
                      FROM chunks c
                      WHERE c.file_id = s.file_id
                        AND c.end_line >= {GetSymbolColumnSql("start_line", "s.line")}
                        AND c.start_line <= {GetSymbolColumnSql("end_line", "s.line")}
                  )
            )";

        cmd.CommandText = sql;
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            var rustQualifiedExact = ShouldPreserveRustQualifiedExactQuery(normalizedQuery, lang, exact);
            var rustQualifiedParts = rustQualifiedExact ? NormalizeRustQualifiedExactQueryParts(normalizedQuery) : default;
            var paramValue = !exact
                ? $"%{EscapeLikeQuery(normalizedQuery)}%"
                : _foldReady
                    ? NameFold.Fold(normalizedQuery) ?? normalizedQuery
                    : normalizedQuery;
            SqliteCommandPolicy.Add(cmd, "@query", paramValue);
            SqliteCommandPolicy.Add(cmd, "@queryNormalized", SqlNameResolver.NormalizeQualifiedName(normalizedQuery));
            SqliteCommandPolicy.Add(cmd, "@queryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(normalizedQuery)) ?? SqlNameResolver.NormalizeQualifiedName(normalizedQuery));
            SqliteCommandPolicy.Add(cmd, "@queryLeaf", SqlNameResolver.GetLeafName(normalizedQuery));
            SqliteCommandPolicy.Add(cmd, "@queryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(normalizedQuery)) ?? SqlNameResolver.GetLeafName(normalizedQuery));
            SqliteCommandPolicy.Add(cmd, "@querySegmentCount", SqlNameResolver.GetSegmentCount(normalizedQuery));
            SqliteCommandPolicy.Add(cmd, "@queryNormalizedLike", $"%{EscapeLikeQuery(SqlNameResolver.NormalizeQualifiedName(normalizedQuery))}%");
            if (SqlNameResolver.HasQualifier(normalizedQuery))
                AddQualifiedSymbolQueryParameters(cmd, "query", normalizedQuery);
            if (rustQualifiedParts.QualifiedPath != null)
            {
                SqliteCommandPolicy.Add(cmd, "@queryRustContainer", rustQualifiedParts.ContainerPath ?? string.Empty);
                SqliteCommandPolicy.Add(cmd, "@queryRustLeaf", rustQualifiedParts.LeafName ?? string.Empty);
                SqliteCommandPolicy.Add(cmd, "@queryRustLeafFolded", NameFold.Fold(rustQualifiedParts.LeafName ?? string.Empty) ?? rustQualifiedParts.LeafName ?? string.Empty);
            }
        }
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            SqliteCommandPolicy.Add(cmd, "@since", since.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);

        using var reader = cmd.ExecuteTrackedReader();
        return reader.TrackedRead()
            ? new QueryCountResult(reader.GetInt32(0), reader.GetInt32(1))
            : new QueryCountResult(0, 0);
    }

}
