using System.Globalization;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    /// <summary>
    /// Get nearby symbols in the same file ordered by proximity to a focus line.
    /// 同一ファイル内の近傍シンボルを、注目行からの近さ順で取得する。
    /// </summary>
    public List<SymbolResult> GetNearbySymbols(string path, int focusLine, int limit = 10, string? excludeName = null, int? excludeStartLine = null)
    {
        using var cmd = _conn.CreateCommand();

        var sql = $@"
            SELECT f.path, f.lang, s.kind, s.name, s.line,
                   {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                   {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                   {GetSymbolColumnSql("body_start_line")} AS body_start_line,
                   {GetSymbolColumnSql("body_end_line")} AS body_end_line,
                   {GetSymbolColumnSql("signature")} AS signature,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path";

        if (excludeName != null && excludeStartLine != null)
            sql += " AND NOT (s.name = @excludeName AND " + GetSymbolColumnSql("start_line", "s.line") + " = @excludeStartLine)";

        sql += " ORDER BY CASE WHEN @focusLine BETWEEN " + GetSymbolColumnSql("start_line", "s.line") + " AND " + GetSymbolColumnSql("end_line", "s.line") + " THEN 0 ELSE abs(" + GetSymbolColumnSql("start_line", "s.line") + " - @focusLine) END, " + GetSymbolColumnSql("start_line", "s.line") + " LIMIT @limit";

        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@path", path);
        SqliteCommandPolicy.Add(cmd, "@focusLine", focusLine);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);
        if (excludeName != null && excludeStartLine != null)
        {
            SqliteCommandPolicy.Add(cmd, "@excludeName", excludeName);
            SqliteCommandPolicy.Add(cmd, "@excludeStartLine", excludeStartLine.Value);
        }

        var results = new List<SymbolResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new SymbolResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                Kind = reader.GetString(2),
                Name = reader.GetString(3),
                Line = reader.GetInt32(4),
                StartLine = GetInt32OrFallback(reader, 5, 4),
                EndLine = GetInt32OrFallback(reader, 6, 4),
                BodyStartLine = GetNullableInt32(reader, 7),
                BodyEndLine = GetNullableInt32(reader, 8),
                Signature = GetNullableString(reader, 9),
                ContainerKind = GetNullableString(reader, 10),
                ContainerName = GetNullableString(reader, 11),
                Visibility = GetNullableString(reader, 12),
                ReturnType = GetNullableString(reader, 13),
            });
        }

        return results;
    }

    public SymbolAnalysisResult AnalyzeFileLine(
        string path,
        int line,
        int limit = 10,
        string? lang = null,
        bool includeBody = false,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth,
        int? bodyStartLine = null,
        int? bodyLineCount = null,
        string? kind = null)
    {
        lang = DbReader.NormalizeQueryLanguage(lang);
        using var txn = _conn.BeginTransaction(deferred: true);

        var query = $"{path}:{line.ToString(CultureInfo.InvariantCulture)}";
        var file = GetFileByPath(path);
        var freshness = GetWorkspaceFreshness();
        var graphLanguage = lang ?? file?.Lang;
        List<SymbolResult> symbolsAtLine = file == null
            ? []
            : GetSymbolsAtLine(path, line, Math.Max(limit, 1), kind, lang);
        var primarySymbol = symbolsAtLine.FirstOrDefault();
        var primaryLineDefinition = primarySymbol == null
            ? null
            : BuildDefinitionResult(primarySymbol, includeBody, bodyStartLine, bodyLineCount);
        List<DefinitionResult> definitions = primaryLineDefinition == null ? [] : [primaryLineDefinition];
        var primaryDefinition = definitions.FirstOrDefault();
        var hasSupportedGraphDefinition = primaryDefinition != null
            && SupportsSymbolGraph(primaryDefinition.Lang, primaryDefinition.Kind, primaryDefinition.ContainerKind) == true;
        var baseGraphSupported = graphLanguage == null
            ? (bool?)null
            : SupportsReferenceLanguage(graphLanguage);
        var graphSupportReason = ReferenceExtractor.BuildGraphSupportReasonWithUnsupportedEnumMemberGap(
            graphLanguage,
            baseGraphSupported,
            hasUnsupportedEnumMember: false,
            hasSupportedGraphDefinition);
        var references = primaryDefinition != null
            ? SearchReferences(primaryDefinition.Name, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true, maxLineWidth)
            : [];
        var callers = primaryDefinition != null
            ? GetCallers(primaryDefinition.Name, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true)
            : [];
        var callees = primaryDefinition != null
            ? GetCallees(primaryDefinition.Name, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true)
            : [];
        var nearbySymbols = file != null
            ? GetNearbySymbols(
                path,
                line,
                Math.Min(limit, 10),
                primaryDefinition?.Name,
                primaryDefinition?.StartLine)
            : [];
        ApplyQueryOutputSignatureLimits(definitions);
        ApplyQueryOutputSignatureLimits(nearbySymbols);

        var result = new SymbolAnalysisResult
        {
            Query = query,
            File = file,
            WorkspaceIndexedAt = freshness.IndexedAt,
            WorkspaceLatestModified = freshness.LatestModified,
            GraphLanguage = graphLanguage,
            GraphSupported = baseGraphSupported,
            GraphSupportReason = graphSupportReason,
            Definitions = definitions,
            NearbySymbols = [.. nearbySymbols],
            References = [.. references],
            Callers = [.. callers],
            Callees = [.. callees],
            GraphTableAvailable = _hasReferencesTable,
        };
        txn.Commit();
        return result;
    }

    private List<SymbolResult> GetSymbolsAtLine(string path, int line, int limit, string? kind, string? lang)
    {
        using var cmd = _conn.CreateCommand();

        var startLineSql = GetSymbolColumnSql("start_line", "s.line");
        var endLineSql = GetSymbolColumnSql("end_line", "s.line");
        var bodyStartLineSql = GetSymbolColumnSql("body_start_line");
        var bodyEndLineSql = GetSymbolColumnSql("body_end_line");
        var sql = $@"
            SELECT f.path, f.lang, s.kind, s.name, s.line,
                   {startLineSql} AS start_line,
                   {endLineSql} AS end_line,
                   {bodyStartLineSql} AS body_start_line,
                   {bodyEndLineSql} AS body_end_line,
                   {GetSymbolColumnSql("signature")} AS signature,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND @line BETWEEN {startLineSql} AND {endLineSql}";
        if (kind != null)
            sql += " AND s.kind = @kind";
        if (lang != null)
            sql += " AND f.lang = @lang";
        sql += $@"
            ORDER BY
                CASE WHEN {startLineSql} = @line THEN 0 ELSE 1 END,
                ({endLineSql} - {startLineSql}),
                CASE WHEN {bodyStartLineSql} IS NOT NULL
                       AND {bodyEndLineSql} IS NOT NULL
                       AND @line BETWEEN {bodyStartLineSql} AND {bodyEndLineSql}
                     THEN 0 ELSE 1 END,
                {startLineSql} DESC
            LIMIT @limit";

        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@path", path);
        SqliteCommandPolicy.Add(cmd, "@line", line);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);

        var results = new List<SymbolResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            results.Add(ReadSymbolResult(reader));

        return results;
    }

    private static SymbolResult ReadSymbolResult(SqliteDataReader reader)
        => new()
        {
            Path = reader.GetString(0),
            Lang = GetNullableString(reader, 1),
            Kind = reader.GetString(2),
            Name = reader.GetString(3),
            Line = reader.GetInt32(4),
            StartLine = GetInt32OrFallback(reader, 5, 4),
            EndLine = GetInt32OrFallback(reader, 6, 4),
            BodyStartLine = GetNullableInt32(reader, 7),
            BodyEndLine = GetNullableInt32(reader, 8),
            Signature = GetNullableString(reader, 9),
            ContainerKind = GetNullableString(reader, 10),
            ContainerName = GetNullableString(reader, 11),
            Visibility = GetNullableString(reader, 12),
            ReturnType = GetNullableString(reader, 13),
        };

    /// <summary>
    /// Bundle definition, graph, and local file context for one symbol query.
    /// 単一シンボルクエリ向けに、定義・グラフ・ローカル文脈をまとめて返す。
    /// </summary>
    public SymbolAnalysisResult AnalyzeSymbol(string query, int limit = 10, string? lang = null, bool includeBody = false, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, int? bodyStartLine = null, int? bodyLineCount = null, string? kind = null, bool groupPartials = false)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
        {
            var workspaceFreshness = GetWorkspaceFreshness();
            return new SymbolAnalysisResult
            {
                Query = query,
                WorkspaceIndexedAt = workspaceFreshness.IndexedAt,
                WorkspaceLatestModified = workspaceFreshness.LatestModified,
                GraphTableAvailable = _hasReferencesTable,
            };
        }

        lang = DbReader.NormalizeQueryLanguage(lang);
        var normalizedQuery = NormalizeSymbolSearchQuery(query, lang) ?? query;
        // Propagate `exact` to every bundled sub-query so the one-round-trip AI workflow
        // (`inspect` / MCP `analyze_symbol`) keeps the same precision contract as the leaf
        // commands. Without this, `inspect Run --exact` would still pull RunAsync/RunImpact
        // into references / callers / callees. See codex review of #83.
        // `exact` は bundle 内のすべての sub-query に伝播させ、leaf コマンドと precision を揃える。
        //
        // Issue #180: wrap the multi-statement bundle in one DEFERRED transaction so every
        // sub-query (definitions / file metadata / freshness / references / callers /
        // callees / nearby symbols) resolves against the same WAL snapshot. Without this,
        // a concurrent writer mid-indexing can make the bundle report callers for an old
        // symbol layout alongside a file row that already reflects the new one.
        // Issue #180: bundle 内の全 sub-query を 1 つの DEFERRED transaction でまとめ、
        // definitions / file / freshness / references / callers / callees / nearby symbols
        // が同じ WAL snapshot を参照するようにする。
        using var txn = _conn.BeginTransaction(deferred: true);
        var definitionLimit = Math.Min(limit, 5);
        var definitions = PrioritizeSourceDefinitions(GetDefinitions(normalizedQuery, definitionLimit, kind: kind, lang, includeBody, pathPatterns, excludePathPatterns, excludeTests, since: null, exact, bodyStartLine: bodyStartLine, bodyLineCount: bodyLineCount, groupPartials: groupPartials));
        DefinitionResult? primaryDefinition = definitions
            .FirstOrDefault(definition => SupportsReferenceLanguage(definition.Lang) && !IsCSharpEnumMemberDefinition(definition))
            ?? definitions.FirstOrDefault(definition => SupportsReferenceLanguage(definition.Lang))
            ?? definitions.FirstOrDefault();
        definitions = BuildAnalysisDefinitions(primaryDefinition, definitions, definitionLimit);
        var freshness = GetWorkspaceFreshness();
        var hasGraphApplicableFiles = HasGraphApplicableFiles(lang, pathPatterns, excludePathPatterns, excludeTests);
        var graphLanguage = lang ?? primaryDefinition?.Lang;
        const bool hasUnsupportedEnumMember = false;
        var hasSupportedGraphDefinition = exact
            ? HasExactGraphSupportedDefinition(normalizedQuery, lang, pathPatterns, excludePathPatterns, excludeTests)
            : definitions.Any(definition => SupportsSymbolGraph(definition.Lang, definition.Kind, definition.ContainerKind) == true);
        var baseGraphSupported = graphLanguage == null
            ? (bool?)null
            : SupportsReferenceLanguage(graphLanguage);
        bool? graphSupported = baseGraphSupported;
        var graphSupportReason = ReferenceExtractor.BuildGraphSupportReasonWithUnsupportedEnumMemberGap(
            graphLanguage,
            graphSupported,
            hasUnsupportedEnumMember,
            hasSupportedGraphDefinition);
        var unsupportedSymbolKind = hasUnsupportedEnumMember ? "enum_member" : null;
        var candidateBundles = definitions
            .Select((definition, index) => BuildSymbolCandidateBundle(
                definition,
                limit,
                includeNameFallback: index == 0,
                pathPatterns,
                excludePathPatterns,
                excludeTests,
                maxLineWidth))
            .ToList();
        var selectedBundle = candidateBundles.FirstOrDefault();
        var file = selectedBundle?.File;
        var references = selectedBundle?.References
            ?? (definitions.Count == 0
                ? SearchReferences(normalizedQuery, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact, maxLineWidth)
                : []);
        var callers = selectedBundle?.Callers
            ?? (definitions.Count == 0
                ? GetCallers(normalizedQuery, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact)
                : []);
        var callees = selectedBundle?.Callees
            ?? (definitions.Count == 0
                ? GetCallees(normalizedQuery, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact)
                : []);
        var sqlGraphRelevant = IsSqlLanguage(lang)
            || IsSqlLanguage(graphLanguage)
            || ContainsSqlLanguage(definitions.Select(definition => definition.Lang))
            || ContainsSqlLanguage(references.Select(reference => reference.Lang))
            || ContainsSqlLanguage(callers.Select(caller => caller.Lang))
            || ContainsSqlLanguage(callees.Select(callee => callee.Lang))
            || candidateBundles.Any(bundle =>
                ContainsSqlLanguage(bundle.References.Select(reference => reference.Lang))
                || ContainsSqlLanguage(bundle.Callers.Select(caller => caller.Lang))
                || ContainsSqlLanguage(bundle.Callees.Select(callee => callee.Lang)));
        var exactSignal = exact
            ? GetAnalyzeSymbolExactQuerySignal(
                includeGraphSignal: hasGraphApplicableFiles,
                includeSqlGraphContractSignal: sqlGraphRelevant,
                lang: lang,
                pathPatterns: pathPatterns,
                excludePathPatterns: excludePathPatterns,
                excludeTests: excludeTests)
            : (ExactQuerySignal?)null;
        var relaxedSymbols = exact && definitions.Count == 0 && references.Count == 0 && callers.Count == 0 && callees.Count == 0
            ? SearchSymbols(normalizedQuery, Math.Max(limit, 5), kind: null, lang, pathPatterns, excludePathPatterns, excludeTests, since: null, exact: false)
            : null;
        var exactZeroHint = exact && definitions.Count == 0 && references.Count == 0 && callers.Count == 0 && callees.Count == 0
            ? ExactZeroHintResult.FromRelaxedMatches(
                relaxedSymbols!.Count,
                relaxedSymbols.Select(result => result.Name))
            : null;
        var nearbySymbols = selectedBundle?.NearbySymbols ?? [];
        ApplyQueryOutputSignatureLimits(definitions);
        ApplyQueryOutputSignatureLimits(nearbySymbols);
        foreach (var bundle in candidateBundles)
            ApplyQueryOutputSignatureLimits(bundle.NearbySymbols);

        var result = new SymbolAnalysisResult
        {
            Query = query,
            File = file,
            WorkspaceIndexedAt = freshness.IndexedAt,
            WorkspaceLatestModified = freshness.LatestModified,
            GraphLanguage = graphLanguage,
            GraphSupported = graphSupported,
            GraphSupportReason = graphSupportReason,
            GraphDegraded = hasUnsupportedEnumMember ? true : null,
            UnsupportedSymbolKind = unsupportedSymbolKind,
            Definitions = definitions,
            NearbySymbols = [.. nearbySymbols],
            References = [.. references],
            Callers = [.. callers],
            Callees = [.. callees],
            CandidateCount = candidateBundles.Count,
            GraphScope = candidateBundles.Count switch
            {
                > 1 => "primary_candidate",
                1 => "single_candidate",
                _ => "query_fallback",
            },
            SelectionRequired = candidateBundles.Count > 1 && candidateBundles.Any(bundle => !bundle.IdentityScoped),
            CandidateBundles = candidateBundles.Count > 0 ? candidateBundles : null,
            GraphTableAvailable = _hasReferencesTable,
            ExactZeroHint = exactZeroHint,
            ExactIndexAvailable = exactSignal?.ExactIndexAvailable,
            ExactHasMissingIndex = exactSignal?.HasMissingIndex,
            ExactHasMissingTable = exactSignal?.HasMissingTable,
            DegradedReason = exactSignal?.DegradedReason,
        };
        txn.Commit();
        return result;
    }

    /// <summary>
    /// Resolve references for one already-selected definition through the same
    /// identity-scoped candidate path used by <see cref="AnalyzeSymbol"/>.
    /// 選択済みの単一定義について、<see cref="AnalyzeSymbol"/> と同じ identity-scoped
    /// candidate 経路で参照を解決する。
    /// </summary>
    internal IReadOnlyList<ReferenceResult> GetReferencesForDefinition(DefinitionResult definition, int limit)
    {
        using var txn = _conn.BeginTransaction(deferred: true);
        var references = CanScopeCandidateByIdentity(definition)
            ? SearchReferencesForCandidate(
                definition,
                limit,
                pathPatterns: null,
                excludePathPatterns: null,
                excludeTests: false,
                LineWidthFormatter.DefaultMaxLineWidth)
            : SearchReferences(
                definition.Name,
                limit,
                definition.Lang,
                referenceKind: null,
                pathPatterns: null,
                excludePathPatterns: null,
                excludeTests: false,
                exact: true,
                LineWidthFormatter.DefaultMaxLineWidth);
        txn.Commit();
        return references;
    }

    private SymbolCandidateBundle BuildSymbolCandidateBundle(
        DefinitionResult definition,
        int limit,
        bool includeNameFallback,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        int maxLineWidth)
    {
        var identityScoped = CanScopeCandidateByIdentity(definition);
        var references = identityScoped
            ? SearchReferencesForCandidate(definition, limit, pathPatterns, excludePathPatterns, excludeTests, maxLineWidth)
            : includeNameFallback
                ? SearchReferences(definition.Name, limit, definition.Lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true, maxLineWidth)
                : [];
        var callers = identityScoped
            ? GetCallersForCandidate(definition, limit, pathPatterns, excludePathPatterns, excludeTests)
            : includeNameFallback
                ? GetCallers(definition.Name, limit, definition.Lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true)
                : [];
        var callees = identityScoped
            ? GetCalleesForCandidate(definition, limit, pathPatterns, excludePathPatterns, excludeTests)
            : includeNameFallback
                ? GetCallees(definition.Name, limit, definition.Lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true)
                : [];
        var nearbySymbols = GetNearbySymbols(
            definition.Path,
            definition.StartLine,
            Math.Min(limit, 10),
            definition.Name,
            definition.StartLine);
        var graphSupported = SupportsSymbolGraph(
            definition.Lang,
            definition.Kind,
            definition.ContainerKind);
        var graphSupportReason = ReferenceExtractor.BuildGraphSupportReason(
            definition.Lang,
            graphSupported,
            definition.Kind,
            definition.ContainerKind);

        return new SymbolCandidateBundle
        {
            Selector = BuildSymbolCandidateSelector(definition),
            Definition = definition,
            File = GetFileByPath(definition.Path),
            GraphSupported = graphSupported,
            GraphSupportReason = graphSupportReason,
            IdentityScoped = identityScoped,
            NearbySymbols = nearbySymbols,
            References = references,
            Callers = callers,
            Callees = callees,
        };
    }

    private bool CanScopeCandidateByIdentity(DefinitionResult definition) =>
        _hasReferencesTable
        && _referenceIdentityContractCurrent
        && definition.SymbolId != null
        && !string.Equals(definition.Lang, "sql", StringComparison.Ordinal)
        && _referenceColumns.Contains("source_symbol_id")
        && HasTable("symbol_reference_candidates");

    private static SymbolCandidateSelector BuildSymbolCandidateSelector(DefinitionResult definition)
    {
        var container = definition.ContainerQualifiedName ?? definition.ContainerName;
        var qualifiedName = string.IsNullOrWhiteSpace(container)
            ? definition.Name
            : $"{container}.{definition.Name}";
        var selector = definition.SymbolId is long symbolId
            ? $"id:{symbolId.ToString(CultureInfo.InvariantCulture)}"
            : $"{definition.Lang}:{definition.Path}:{definition.StartLine.ToString(CultureInfo.InvariantCulture)}:{qualifiedName}";

        return new SymbolCandidateSelector
        {
            Selector = selector,
            SymbolId = definition.SymbolId,
            QualifiedName = qualifiedName,
            Container = container,
            Signature = definition.Signature,
            Path = definition.Path,
            Line = definition.StartLine,
            Lang = definition.Lang,
            Kind = definition.Kind,
        };
    }

    private static List<DefinitionResult> PrioritizeSourceDefinitions(List<DefinitionResult> definitions)
    {
        if (definitions.Count <= 1)
            return definitions;

        return definitions
            .Select((definition, index) => (definition, index))
            .OrderBy(item => SearchMatchClassifier.IsLikelyTestPath(item.definition.Path) ? 1 : 0)
            .ThenBy(item => item.index)
            .Select(item => item.definition)
            .ToList();
    }

    public HashSet<string> GetUnsupportedExactGraphSymbolKinds(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var normalizedQuery = NormalizeCSharpVerbatimQuery(query, lang) ?? query;
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        if (HasExactUnsupportedCSharpEnumMember(normalizedQuery, lang, pathPatterns, excludePathPatterns, excludeTests))
            kinds.Add("enum_member");
        return kinds;
    }

    public bool HasExactUnsupportedCSharpEnumMember(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var normalizedQuery = NormalizeCSharpVerbatimQuery(query, lang) ?? query;
        return false;
    }

    public bool HasExactGraphSupportedDefinition(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var normalizedQuery = NormalizeCSharpVerbatimQuery(query, lang) ?? query;
        return GetExactGraphSupportedDefinitionLanguage(normalizedQuery, lang, pathPatterns, excludePathPatterns, excludeTests) != null;
    }

    public string? GetExactGraphSupportedDefinitionLanguage(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var normalizedQuery = NormalizeCSharpVerbatimQuery(query, lang) ?? query;
        return TryGetExactGraphSupportedDefinitionLanguage(normalizedQuery, lang, pathPatterns, excludePathPatterns, excludeTests, preferNonEnumMember: true)
            ?? TryGetExactGraphSupportedDefinitionLanguage(normalizedQuery, lang, pathPatterns, excludePathPatterns, excludeTests, preferNonEnumMember: false);
    }

    private string? TryGetExactGraphSupportedDefinitionLanguage(
        string query,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        bool preferNonEnumMember)
    {
        var normalizedQuery = NormalizeCSharpVerbatimQuery(query, lang) ?? query;
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "supportedGraphLang");
        var allowLeafFallback = !SqlNameResolver.HasQualifier(normalizedQuery);
        var nameCondition = _foldReady
            ? allowLeafFallback
                ? "(s.name_folded = @queryFolded OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded) OR sql_leaf_name_folded(s.name) = @queryLeafFolded)))"
                : "(s.name_folded = @queryFolded OR (f.lang = 'sql' AND sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded))"
            : allowLeafFallback
                ? "(s.name = @queryRaw COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name(s.name) = @queryNormalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @queryLeaf COLLATE NOCASE)))"
                : "(s.name = @queryRaw COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name(s.name) = @queryNormalized COLLATE NOCASE))";

        var sql = @"
            SELECT f.lang
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE " + nameCondition + @"
              AND " + supportedLangFilter;
        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        if (preferNonEnumMember)
            sql += " AND NOT (f.lang = 'csharp' AND s.kind = 'enum' AND "
                + GetSymbolColumnSql("container_kind", "''")
                + " = 'enum')";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " LIMIT 1";

        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@queryRaw", query);
        SqliteCommandPolicy.Add(cmd, "@queryFolded", NameFold.Fold(query) ?? query);
        SqliteCommandPolicy.Add(cmd, "@queryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        SqliteCommandPolicy.Add(cmd, "@queryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        SqliteCommandPolicy.Add(cmd, "@queryLeaf", SqlNameResolver.GetLeafName(query));
        SqliteCommandPolicy.Add(cmd, "@queryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        SqliteCommandPolicy.Add(cmd, "@querySegmentCount", SqlNameResolver.GetSegmentCount(query));
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? null : (string?)value;
    }

    public bool HasFilteredCSharpEnumSymbols(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        if (lang != null && !string.Equals(lang, "csharp", StringComparison.Ordinal))
            return false;
        if (kind != null && !string.Equals(kind, "enum", StringComparison.Ordinal))
            return false;

        using var cmd = _conn.CreateCommand();
        var sql = @"
            SELECT 1
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.kind = 'enum'";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " LIMIT 1";
        cmd.CommandText = sql;
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var value = cmd.ExecuteScalar();
        return value != null && value != DBNull.Value;
    }

    private static bool IsCSharpEnumMemberDefinition(DefinitionResult definition)
    {
        return string.Equals(definition.Lang, "csharp", StringComparison.Ordinal)
            && string.Equals(definition.Kind, "enum", StringComparison.Ordinal)
            && string.Equals(definition.ContainerKind, "enum", StringComparison.Ordinal);
    }

    private static List<DefinitionResult> BuildAnalysisDefinitions(DefinitionResult? primaryDefinition, List<DefinitionResult> definitions, int limit)
    {
        if (primaryDefinition == null || limit <= 0)
            return definitions;

        var ordered = definitions
            .Where(definition => !IsSameDefinition(definition, primaryDefinition))
            .Prepend(primaryDefinition)
            .Take(limit)
            .ToList();
        return ordered;
    }

    private static bool IsSameDefinition(DefinitionResult left, DefinitionResult right)
    {
        return string.Equals(left.Path, right.Path, StringComparison.Ordinal)
            && left.StartLine == right.StartLine
            && left.EndLine == right.EndLine
            && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal);
    }

}
