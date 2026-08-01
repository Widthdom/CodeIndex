using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Database;

public partial class DbReader
{
    /// <summary>
    /// Return a structured outline of symbols in a single file, ordered deterministically.
    /// 1ファイルのシンボルを決定的な順序の構造化アウトラインとして返す。
    /// </summary>
    public OutlineResult? GetOutline(string filePath, bool includeReferenceCounts = false)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, lang, lines FROM files WHERE path = @path";
        SqliteCommandPolicy.Add(cmd, "@path", filePath);

        string? lang = null;
        int totalLines = 0;
        long fileId = 0;
        using (var reader = cmd.ExecuteTrackedReader())
        {
            if (!reader.TrackedRead())
                return null;
            fileId = reader.GetInt64(0);
            lang = GetNullableString(reader, 2);
            totalLines = reader.GetInt32(3);
        }

        var startColumnOrderSql = GetSymbolColumnSql("start_column", "CAST(2147483647 AS INTEGER)");
        var structuredHierarchyOrderSql = lang is "json" or "jsonl"
            ? "LENGTH(CAST(s.name AS BLOB)) ASC,"
            : string.Empty;
        var includeReferenceCountSql = includeReferenceCounts && _hasReferencesTable;
        var referenceCountSql = includeReferenceCountSql
            ? "CASE WHEN COALESCE(symbol_defs.definition_sites, 0) = 1 THEN COALESCE(symbol_rank.reference_count, 0) ELSE 0 END"
            : "CAST(NULL AS INTEGER)";
        var symbolRankCteSql = includeReferenceCountSql
            ? $@"
            WITH candidate_outline_names AS (
                SELECT DISTINCT name COLLATE NOCASE AS symbol_name
                FROM symbols
                WHERE file_id = @fileId
                  AND name IS NOT NULL
                  AND name <> ''
            ),
            symbol_rank AS (
                SELECT rf.lang AS lang,
                       sr.symbol_name COLLATE NOCASE AS symbol_name,
                       COUNT(*) AS reference_count
                FROM symbol_references sr
                JOIN files rf ON rf.id = sr.file_id
                JOIN candidate_outline_names cn ON cn.symbol_name = sr.symbol_name COLLATE NOCASE
                WHERE sr.reference_kind IN {CallGraphReferenceKindsSql}
                  AND sr.symbol_name IS NOT NULL
                  AND sr.symbol_name <> ''
                GROUP BY rf.lang, sr.symbol_name COLLATE NOCASE
            ),
            symbol_defs AS (
                SELECT df.lang AS lang,
                       ds.name COLLATE NOCASE AS symbol_name,
                       COUNT(*) AS definition_sites
                FROM symbols ds
                JOIN files df ON df.id = ds.file_id
                JOIN candidate_outline_names cn ON cn.symbol_name = ds.name COLLATE NOCASE
                WHERE ds.name IS NOT NULL
                  AND ds.name <> ''
                GROUP BY df.lang, ds.name COLLATE NOCASE
            )"
            : string.Empty;
        var symbolRankJoin = includeReferenceCountSql
            ? @"
            LEFT JOIN symbol_rank
              ON symbol_rank.lang IS @lang
             AND symbol_rank.symbol_name = s.name COLLATE NOCASE
            LEFT JOIN symbol_defs
              ON symbol_defs.lang IS @lang
             AND symbol_defs.symbol_name = s.name COLLATE NOCASE"
            : string.Empty;
        using var symCmd = _conn.CreateCommand();
        symCmd.CommandText = $@"
            {symbolRankCteSql}
            SELECT s.kind, s.name, s.line,
                   {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                   {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                   {GetSymbolColumnSql("body_start_line")} AS body_start_line,
                   {GetSymbolColumnSql("body_end_line")} AS body_end_line,
                   {GetSymbolColumnSql("signature")} AS signature,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("container_qualified_name")} AS container_qualified_name,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type,
                   {referenceCountSql} AS reference_count
            FROM symbols s
            {symbolRankJoin}
            WHERE s.file_id = @fileId
            ORDER BY s.line ASC,
                     {startColumnOrderSql} ASC,
                     {structuredHierarchyOrderSql}
                     s.kind COLLATE BINARY ASC,
                     s.name COLLATE BINARY ASC,
                     s.id ASC";
        SqliteCommandPolicy.Add(symCmd, "@fileId", fileId);
        if (includeReferenceCountSql)
            SqliteCommandPolicy.AddNullableText(symCmd, "@lang", lang);

        var symbols = new List<OutlineSymbol>();
        var isJsonStructuredData = lang is "json" or "jsonl";
        using (var reader = symCmd.ExecuteTrackedReader())
        {
            while (reader.TrackedRead())
            {
                var name = reader.GetString(1);
                var containerName = GetNullableString(reader, 9);
                var containerQualifiedName = GetNullableString(reader, 10);
                symbols.Add(new OutlineSymbol
                {
                    Kind = reader.GetString(0),
                    Name = name,
                    Line = reader.GetInt32(2),
                    StartLine = GetInt32OrFallback(reader, 3, 2),
                    EndLine = GetInt32OrFallback(reader, 4, 2),
                    BodyStartLine = GetNullableInt32(reader, 5),
                    BodyEndLine = GetNullableInt32(reader, 6),
                    Signature = GetNullableString(reader, 7),
                    ContainerKind = GetNullableString(reader, 8),
                    ContainerName = containerName,
                    Path = BuildOutlineSymbolPath(
                        containerQualifiedName ?? containerName,
                        name,
                        isJsonStructuredData),
                    Visibility = GetNullableString(reader, 11),
                    ReturnType = GetNullableString(reader, 12),
                    ReferenceCount = GetNullableInt32(reader, 13),
                });
            }
        }

        PopulateOutlineDepths(symbols, isJsonStructuredData);
        ApplyQueryOutputSignatureLimits(symbols);
        PopulateOutlineDisplayNames(symbols, lang);

        return new OutlineResult
        {
            Path = filePath,
            Lang = lang,
            TotalLines = totalLines,
            SymbolCount = symbols.Count,
            Symbols = symbols,
        };
    }

    private static void PopulateOutlineDisplayNames(List<OutlineSymbol> symbols, string? lang)
    {
        foreach (var symbol in symbols)
        {
            symbol.DisplayName = BuildOutlineDisplayName(symbol, lang);
        }
    }

    private static string BuildOutlineDisplayName(OutlineSymbol symbol, string? lang)
    {
        if (IsCallableOutlineSymbol(symbol.Kind))
        {
            if (symbol.SignatureTruncated)
                return $"{symbol.Name}@{symbol.Line}";

            var compactSignature = TryBuildCompactCallableSignature(symbol.Name, symbol.Signature, lang);
            if (!string.IsNullOrWhiteSpace(compactSignature))
                return compactSignature!;

            return $"{symbol.Name}@{symbol.Line}";
        }

        return !string.IsNullOrWhiteSpace(symbol.Path)
            ? symbol.Path
            : symbol.Name;
    }

    private static string BuildOutlineSymbolPath(
        string? containerQualifiedName,
        string name,
        bool allowArrayIndexBoundary)
    {
        if (string.IsNullOrWhiteSpace(containerQualifiedName))
            return name;

        return IsQualifiedOutlineChildPath(containerQualifiedName, name, allowArrayIndexBoundary)
            ? name
            : $"{containerQualifiedName}.{name}";
    }

    private static bool IsQualifiedOutlineChildPath(
        string containerPath,
        string childPath,
        bool allowArrayIndexBoundary)
    {
        if (!childPath.StartsWith(containerPath, StringComparison.Ordinal)
            || childPath.Length <= containerPath.Length)
        {
            return false;
        }

        var boundary = childPath[containerPath.Length];
        return boundary == '.' || (allowArrayIndexBoundary && boundary == '[');
    }

    private static bool IsCallableOutlineSymbol(string kind)
    {
        return kind is "function" or "operator" or "method" or "constructor";
    }

    private static string? TryBuildCompactCallableSignature(string name, string? signature, string? lang)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var normalizedSignature = lang == "csharp"
            ? ExactSourceSearchNormalizer.NormalizeCSharpUnicodeEscapes(signature, out _)
            : signature;
        var openParen = FindCallableParameterOpenParen(
            normalizedSignature,
            name,
            lang,
            out var csharpTypeParameters);
        if (openParen < 0)
            return null;

        var closeParen = FindMatchingParen(normalizedSignature, openParen);
        if (closeParen < 0)
            return null;

        var parameters = normalizedSignature.Substring(openParen + 1, closeParen - openParen - 1);
        var normalizedTypeParameters = BuildNormalizedCSharpTypeParameterMap(csharpTypeParameters);
        var parameterLabels = SplitTopLevelParameters(parameters)
            .Select(parameter => normalizedTypeParameters == null
                ? SimplifyParameterForOutline(parameter, lang)
                : SimplifyCSharpGenericParameterForOutline(parameter, normalizedTypeParameters))
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter))
            .ToList();

        var displayName = normalizedTypeParameters == null
            ? name
            : name + BuildNormalizedCSharpTypeParameterSuffix(csharpTypeParameters!.Count);
        return $"{displayName}({string.Join(", ", parameterLabels)})";
    }

    private static int FindCallableParameterOpenParen(
        string signature,
        string name,
        string? lang,
        out IReadOnlyList<string>? csharpTypeParameters)
    {
        csharpTypeParameters = null;
        var searchStart = 0;
        while (searchStart < signature.Length)
        {
            var nameIndex = signature.IndexOf(name, searchStart, StringComparison.Ordinal);
            if (nameIndex < 0)
                return -1;

            var afterName = nameIndex + name.Length;
            var tokenStart = nameIndex > 0 && signature[nameIndex - 1] == '@'
                ? nameIndex - 1
                : nameIndex;
            var hasIdentifierBoundary =
                (tokenStart == 0 || !IsCSharpIdentifierPart(signature[tokenStart - 1]))
                && (afterName >= signature.Length || !IsCSharpIdentifierPart(signature[afterName]));
            if (!hasIdentifierBoundary)
            {
                searchStart = afterName;
                continue;
            }

            var cursor = afterName;
            while (cursor < signature.Length && char.IsWhiteSpace(signature[cursor]))
                cursor++;

            if (cursor < signature.Length && signature[cursor] == '(')
                return cursor;

            if (lang == "csharp" && cursor < signature.Length && signature[cursor] == '<')
            {
                var closeAngle = FindMatchingAngleBracket(signature, cursor);
                if (closeAngle > cursor
                    && TryReadCSharpTypeParameterNames(
                        signature[(cursor + 1)..closeAngle],
                        out var typeParameters))
                {
                    var parameterOpen = closeAngle + 1;
                    while (parameterOpen < signature.Length && char.IsWhiteSpace(signature[parameterOpen]))
                        parameterOpen++;

                    if (parameterOpen < signature.Length && signature[parameterOpen] == '(')
                    {
                        csharpTypeParameters = typeParameters;
                        return parameterOpen;
                    }
                }
            }

            searchStart = afterName;
        }

        return -1;
    }

    private static int FindMatchingAngleBracket(string value, int openAngle)
    {
        var depth = 0;
        for (var i = openAngle; i < value.Length; i++)
        {
            if (value[i] == '<')
            {
                depth++;
            }
            else if (value[i] == '>')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static bool TryReadCSharpTypeParameterNames(
        string typeParameterList,
        out IReadOnlyList<string> typeParameters)
    {
        var parsed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in SplitTopLevelParameters(typeParameterList))
        {
            var name = ReadTrailingCSharpIdentifier(parameter);
            if (name == null || !seen.Add(name))
            {
                typeParameters = Array.Empty<string>();
                return false;
            }

            parsed.Add(name);
        }

        typeParameters = parsed;
        return parsed.Count > 0;
    }

    private static string? ReadTrailingCSharpIdentifier(string value)
    {
        var end = value.Length;
        while (end > 0 && char.IsWhiteSpace(value[end - 1]))
            end--;

        var start = end;
        while (start > 0 && IsCSharpIdentifierPart(value[start - 1]))
            start--;
        if (start > 0 && value[start - 1] == '@')
            start--;
        if (start == end)
            return null;

        var identifier = value[start..end];
        if (identifier[0] == '@')
            identifier = identifier[1..];
        return identifier.Length > 0 && IsCSharpIdentifierStart(identifier[0])
            ? identifier
            : null;
    }

    private static Dictionary<string, string>? BuildNormalizedCSharpTypeParameterMap(
        IReadOnlyList<string>? typeParameters)
    {
        if (typeParameters == null || typeParameters.Count == 0)
            return null;

        var normalized = new Dictionary<string, string>(typeParameters.Count, StringComparer.Ordinal);
        for (var i = 0; i < typeParameters.Count; i++)
        {
            normalized[typeParameters[i]] = typeParameters.Count == 1
                ? "T"
                : $"T{i + 1}";
        }

        return normalized;
    }

    private static string BuildNormalizedCSharpTypeParameterSuffix(int arity)
    {
        if (arity == 1)
            return "<T>";

        return $"<{string.Join(", ", Enumerable.Range(1, arity).Select(index => $"T{index}"))}>";
    }

    private static string SimplifyCSharpGenericParameterForOutline(
        string parameter,
        IReadOnlyDictionary<string, string> typeParameters)
    {
        var cleaned = Regex.Replace(parameter, @"\s*=\s*.*$", "").Trim();
        cleaned = Regex.Replace(cleaned, @"^\[[^\]]+\]\s*", "").Trim();
        if (cleaned.Length == 0)
            return string.Empty;

        var overloadModifiers = new List<string>(2);
        while (TryReadLeadingWord(cleaned, out var modifier, out var remainder))
        {
            if (modifier is "ref" or "out" or "in")
            {
                overloadModifiers.Add(modifier);
            }
            else if (modifier == "readonly"
                     && overloadModifiers.Count > 0
                     && overloadModifiers[^1] == "ref")
            {
                overloadModifiers.Add(modifier);
            }
            else if (modifier is not ("this" or "params" or "scoped"))
            {
                break;
            }

            cleaned = remainder.TrimStart();
        }

        var typeName = SimplifyParameterForOutline(cleaned, "csharp");
        if (typeName.Length == 0)
            return string.Empty;

        typeName = ReplaceCSharpTypeParameterTokens(typeName, typeParameters);
        return overloadModifiers.Count == 0
            ? typeName
            : $"{string.Join(" ", overloadModifiers)} {typeName}";
    }

    private static bool TryReadLeadingWord(string value, out string word, out string remainder)
    {
        var end = 0;
        while (end < value.Length && IsCSharpIdentifierPart(value[end]))
            end++;

        if (end == 0 || (end < value.Length && !char.IsWhiteSpace(value[end])))
        {
            word = string.Empty;
            remainder = value;
            return false;
        }

        word = value[..end];
        remainder = value[end..];
        return true;
    }

    private static string ReplaceCSharpTypeParameterTokens(
        string typeName,
        IReadOnlyDictionary<string, string> typeParameters)
    {
        var builder = new StringBuilder(typeName.Length);
        for (var i = 0; i < typeName.Length;)
        {
            var tokenStart = i;
            if (typeName[i] == '@'
                && i + 1 < typeName.Length
                && IsCSharpIdentifierStart(typeName[i + 1]))
            {
                i++;
            }

            if (!IsCSharpIdentifierStart(typeName[i]))
            {
                builder.Append(typeName[tokenStart]);
                i = tokenStart + 1;
                continue;
            }

            i++;
            while (i < typeName.Length && IsCSharpIdentifierPart(typeName[i]))
                i++;

            var token = typeName[tokenStart..i];
            var lookup = token[0] == '@' ? token[1..] : token;
            builder.Append(typeParameters.TryGetValue(lookup, out var normalized)
                ? normalized
                : token);
        }

        return builder.ToString();
    }

    private static bool IsCSharpIdentifierStart(char value)
    {
        return value == '_' || char.IsLetter(value);
    }

    private static bool IsCSharpIdentifierPart(char value)
    {
        return IsCSharpIdentifierStart(value) || char.IsDigit(value);
    }

    private static int FindMatchingParen(string value, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < value.Length; i++)
        {
            if (value[i] == '(')
            {
                depth++;
            }
            else if (value[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static IEnumerable<string> SplitTopLevelParameters(string parameters)
    {
        var start = 0;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            var ch = parameters[i];
            switch (ch)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0) bracketDepth--;
                    break;
                case ',' when angleDepth == 0 && parenDepth == 0 && bracketDepth == 0:
                    yield return parameters[start..i].Trim();
                    start = i + 1;
                    break;
            }
        }

        var last = parameters[start..].Trim();
        if (last.Length > 0)
            yield return last;
    }

    private static string SimplifyParameterForOutline(string parameter, string? lang)
    {
        var cleaned = Regex.Replace(parameter, @"\s*=\s*.*$", "").Trim();
        cleaned = Regex.Replace(cleaned, @"^\[[^\]]+\]\s*", "").Trim();
        if (cleaned.Length == 0)
            return string.Empty;

        if (lang is "python")
        {
            if (cleaned is "self" or "cls" or "*" or "/")
                return string.Empty;

            var colonIndex = cleaned.IndexOf(':');
            if (colonIndex >= 0 && colonIndex + 1 < cleaned.Length)
                return cleaned[(colonIndex + 1)..].Trim();

            return cleaned.TrimStart('*');
        }

        var colon = cleaned.LastIndexOf(':');
        if (colon >= 0 && colon + 1 < cleaned.Length)
            return cleaned[(colon + 1)..].Trim();

        cleaned = Regex.Replace(cleaned, @"^(params|ref|out|in|this|readonly)\s+", "", RegexOptions.IgnoreCase).Trim();
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
            return cleaned;

        if (lang is "go")
            return parts[^1];

        return string.Join(" ", parts.Take(parts.Length - 1));
    }

    private static void PopulateOutlineDepths(
        List<OutlineSymbol> symbols,
        bool isJsonStructuredData)
    {
        var depthCache = new Dictionary<int, int>();
        var activeStack = new HashSet<int>();
        for (var i = 0; i < symbols.Count; i++)
            symbols[i].Depth = GetOutlineDepth(
                symbols,
                i,
                depthCache,
                activeStack,
                isJsonStructuredData);
    }

    private static int GetOutlineDepth(
        List<OutlineSymbol> symbols,
        int index,
        Dictionary<int, int> depthCache,
        HashSet<int> activeStack,
        bool isJsonStructuredData)
    {
        if (depthCache.TryGetValue(index, out var cachedDepth))
            return cachedDepth;

        if (!activeStack.Add(index))
            return 0;

        var symbol = symbols[index];
        var depth = 0;
        if (!string.IsNullOrEmpty(symbol.ContainerName))
        {
            var parentIndex = FindOutlineContainerIndex(
                symbols,
                index,
                symbol.ContainerName,
                symbol.ContainerKind,
                isJsonStructuredData);
            if (parentIndex >= 0)
                depth = GetOutlineDepth(
                    symbols,
                    parentIndex,
                    depthCache,
                    activeStack,
                    isJsonStructuredData) + 1;
        }

        activeStack.Remove(index);
        depthCache[index] = depth;
        return depth;
    }

    private static int FindOutlineContainerIndex(
        List<OutlineSymbol> symbols,
        int childIndex,
        string containerName,
        string? containerKind,
        bool isJsonStructuredData)
    {
        var child = symbols[childIndex];
        var expectedContainerPath = GetOutlineContainerPath(child, isJsonStructuredData);
        for (var i = childIndex - 1; i >= 0; i--)
        {
            var candidate = symbols[i];
            if (!string.Equals(candidate.Name, containerName, StringComparison.Ordinal))
                continue;
            if (containerKind != null && !string.Equals(candidate.Kind, containerKind, StringComparison.Ordinal))
                continue;
            if (expectedContainerPath != null
                && !string.Equals(candidate.Path, expectedContainerPath, StringComparison.Ordinal))
            {
                continue;
            }
            if (candidate.Line > child.Line)
                continue;
            if (IsOutlineContainerMatch(candidate, child.Line, isJsonStructuredData))
                return i;
        }

        return -1;
    }

    private static string? GetOutlineContainerPath(
        OutlineSymbol symbol,
        bool allowArrayIndexBoundary)
    {
        if (string.IsNullOrWhiteSpace(symbol.Path) || string.IsNullOrWhiteSpace(symbol.Name))
            return null;

        var suffix = "." + symbol.Name;
        if (symbol.Path.EndsWith(suffix, StringComparison.Ordinal))
            return symbol.Path[..^suffix.Length];

        return !string.IsNullOrWhiteSpace(symbol.ContainerName)
            && string.Equals(symbol.Path, symbol.Name, StringComparison.Ordinal)
            && IsQualifiedOutlineChildPath(
                symbol.ContainerName,
                symbol.Name,
                allowArrayIndexBoundary)
                ? symbol.ContainerName
                : null;
    }

    private static bool IsOutlineContainerMatch(
        OutlineSymbol candidate,
        int childLine,
        bool isJsonStructuredData)
    {
        if (candidate.StartLine <= childLine && candidate.EndLine >= childLine)
            return true;

        // File-scoped namespaces and structured-data containers have no body range, so they
        // do not enclose children by lines even though their exact paths identify the parent.
        return (candidate.Kind == "namespace"
                || (isJsonStructuredData && candidate.Kind is "object" or "array" or "record"))
            && candidate.BodyStartLine == null
            && candidate.BodyEndLine == null
            && candidate.Line <= childLine;
    }

}
