using System.Globalization;
using CodeIndex.Indexer;
using CodeIndex.Models;
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
        string? kind = null,
        SymbolGraphPageRequest? graphPage = null)
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
        var candidateBundle = primaryDefinition == null
            ? null
            : BuildSymbolCandidateBundle(
                primaryDefinition,
                limit,
                includeNameFallback: true,
                pathPatterns,
                excludePathPatterns,
                excludeTests,
                maxLineWidth,
                graphPage);
        var references = candidateBundle?.References ?? [];
        var callers = candidateBundle?.Callers ?? [];
        var callees = candidateBundle?.Callees ?? [];
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
            GraphLanguageSource = lang != null
                ? "language_filter"
                : primaryDefinition?.Lang != null
                    ? "definition"
                    : null,
            GraphLanguageConfidence = lang != null || primaryDefinition?.Lang != null
                ? "authoritative"
                : null,
            GraphSupported = baseGraphSupported,
            GraphSupportReason = graphSupportReason,
            Definitions = definitions,
            NearbySymbols = [.. nearbySymbols],
            References = [.. references],
            Callers = [.. callers],
            Callees = [.. callees],
            GraphSections = candidateBundle?.GraphSections ?? new SymbolGraphSections(),
            CandidateCount = candidateBundle == null ? 0 : 1,
            GraphScope = candidateBundle == null ? null : "single_candidate",
            CandidateBundles = candidateBundle == null ? null : [candidateBundle],
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
                   {GetSymbolColumnSql("return_type")} AS return_type,
                   s.id AS symbol_id,
                   {GetSymbolColumnSql("container_qualified_name")} AS container_qualified_name,
                   {GetSymbolColumnSql("sub_kind")} AS sub_kind,
                   {GetSymbolColumnSql("start_column")} AS declaration_start_column
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
            StartColumn = ResolveSymbolIdentifierStartColumn(
                GetNullableInt32(reader, 17),
                GetNullableString(reader, 9),
                reader.GetString(3),
                reader.GetString(2)),
            DeclarationStartColumn = GetNullableInt32(reader, 17),
            EndLine = GetInt32OrFallback(reader, 6, 4),
            BodyStartLine = GetNullableInt32(reader, 7),
            BodyEndLine = GetNullableInt32(reader, 8),
            Signature = GetNullableString(reader, 9),
            ContainerKind = GetNullableString(reader, 10),
            ContainerName = GetNullableString(reader, 11),
            Visibility = GetNullableString(reader, 12),
            ReturnType = GetNullableString(reader, 13),
            SymbolId = reader.GetInt64(14),
            ContainerQualifiedName = GetNullableString(reader, 15),
            SubKind = GetNullableString(reader, 16),
        };

    private DefinitionResult? GetDefinitionBySymbolId(
        long symbolId,
        bool includeBody,
        int? bodyStartLine,
        int? bodyLineCount,
        string? lang,
        string? kind)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT f.path, f.lang, s.kind, s.name, s.line,
                   {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                   {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                   {GetSymbolColumnSql("body_start_line")} AS body_start_line,
                   {GetSymbolColumnSql("body_end_line")} AS body_end_line,
                   {GetSymbolColumnSql("signature")} AS signature,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type,
                   s.id AS symbol_id,
                   {GetSymbolColumnSql("container_qualified_name")} AS container_qualified_name,
                   {GetSymbolColumnSql("sub_kind")} AS sub_kind,
                   {GetSymbolColumnSql("start_column")} AS declaration_start_column
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE s.id = @symbol_id";
        if (lang != null)
            cmd.CommandText += " AND f.lang = @lang";
        if (kind != null)
            cmd.CommandText += " AND s.kind = @kind";
        cmd.CommandText += " LIMIT 1";
        SqliteCommandPolicy.Add(cmd, "@symbol_id", symbolId);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        using var reader = cmd.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return null;

        return BuildDefinitionResult(
            ReadSymbolResult(reader),
            includeBody,
            bodyStartLine,
            bodyLineCount);
    }

    /// <summary>
    /// Bundle definition, graph, and local file context for one symbol query.
    /// 単一シンボルクエリ向けに、定義・グラフ・ローカル文脈をまとめて返す。
    /// </summary>
    public SymbolAnalysisResult AnalyzeSymbol(string query, int limit = 10, string? lang = null, bool includeBody = false, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, int? bodyStartLine = null, int? bodyLineCount = null, string? kind = null, bool groupPartials = false, SymbolGraphPageRequest? graphPage = null, long? selectedSymbolId = null, string? selectedSymbolGenerationFingerprint = null)
    {
        using var txn = _conn.BeginTransaction(deferred: true);
        if (selectedSymbolId == null
            && (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query)))
        {
            var workspaceFreshness = GetWorkspaceFreshness();
            var emptyResult = new SymbolAnalysisResult
            {
                Query = query,
                WorkspaceIndexedAt = workspaceFreshness.IndexedAt,
                WorkspaceLatestModified = workspaceFreshness.LatestModified,
                GraphTableAvailable = _hasReferencesTable,
            };
            CaptureAnalysisHeadMetadataSnapshot(emptyResult);
            txn.Commit();
            return emptyResult;
        }

        lang = DbReader.NormalizeQueryLanguage(lang);
        var normalizedQuery = selectedSymbolId != null
            ? query
            : NormalizeSymbolSearchQueryForSymbolSearch(query, lang, exact) ?? query;
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
        var definitionLimit = Math.Min(limit, 5);
        var selectedGenerationMatches = selectedSymbolGenerationFingerprint == null
            || string.Equals(
                selectedSymbolGenerationFingerprint,
                SymbolSelector.BuildGenerationFingerprint(GetSymbolSelectorGenerationIdentity()),
                StringComparison.Ordinal);
        var definitions = selectedSymbolId is long symbolId
            ? selectedGenerationMatches
                ? GetDefinitionBySymbolId(symbolId, includeBody, bodyStartLine, bodyLineCount, lang, kind) is { } selectedDefinition
                    ? new List<DefinitionResult> { selectedDefinition }
                    : []
                : []
            : PrioritizeSourceDefinitions(GetDefinitions(normalizedQuery, definitionLimit, kind: kind, lang, includeBody, pathPatterns, excludePathPatterns, excludeTests, since: null, exact, bodyStartLine: bodyStartLine, bodyLineCount: bodyLineCount, groupPartials: groupPartials));
        DefinitionResult? primaryDefinition = definitions
            .FirstOrDefault(definition => SupportsReferenceLanguage(definition.Lang) && !IsCSharpEnumMemberDefinition(definition))
            ?? definitions.FirstOrDefault(definition => SupportsReferenceLanguage(definition.Lang))
            ?? definitions.FirstOrDefault();
        definitions = BuildAnalysisDefinitions(primaryDefinition, definitions, definitionLimit);
        var freshness = GetWorkspaceFreshness();
        var hasGraphApplicableFiles = HasGraphApplicableFiles(lang, pathPatterns, excludePathPatterns, excludeTests);
        var graphLanguage = lang ?? primaryDefinition?.Lang;
        var graphLanguageSource = lang != null
            ? "language_filter"
            : primaryDefinition?.Lang != null
                ? "definition"
                : null;
        var graphLanguageConfidence = graphLanguageSource != null ? "authoritative" : null;
        var graphLanguageCandidates = new List<string>();
        var graphLanguageConflict = false;
        const bool hasUnsupportedEnumMember = false;
        var hasSupportedGraphDefinition = selectedSymbolId != null
            ? definitions.Any(definition => SupportsSymbolGraph(definition.Lang, definition.Kind, definition.ContainerKind) == true)
            : exact
            ? HasExactGraphSupportedDefinition(normalizedQuery, lang, pathPatterns, excludePathPatterns, excludeTests)
            : definitions.Any(definition => SupportsSymbolGraph(definition.Lang, definition.Kind, definition.ContainerKind) == true);
        var unsupportedSymbolKind = hasUnsupportedEnumMember ? "enum_member" : null;
        var candidateBundles = definitions
            .Select((definition, index) => BuildSymbolCandidateBundle(
                definition,
                limit,
                includeNameFallback: selectedSymbolId == null && index == 0,
                pathPatterns,
                excludePathPatterns,
                excludeTests,
                maxLineWidth,
                graphPage))
            .ToList();
        var selectedBundle = candidateBundles.FirstOrDefault();
        var file = selectedBundle?.File;
        var fallbackReferenceOffset = GetGraphSectionOffset(graphPage, "references", candidateSelector: null);
        var fallbackCallerOffset = GetGraphSectionOffset(graphPage, "callers", candidateSelector: null);
        var fallbackCalleeOffset = GetGraphSectionOffset(graphPage, "callees", candidateSelector: null);
        var allowNameGraphFallback = selectedSymbolId == null && definitions.Count == 0;
        var references = selectedBundle?.References
            ?? (allowNameGraphFallback
                ? SearchReferences(normalizedQuery, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact, maxLineWidth, offset: fallbackReferenceOffset)
                : []);
        var callers = selectedBundle?.Callers
            ?? (allowNameGraphFallback
                ? GetCallers(normalizedQuery, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact, offset: fallbackCallerOffset)
                : []);
        var callees = selectedBundle?.Callees
            ?? (allowNameGraphFallback
                ? GetCallees(normalizedQuery, limit, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact, offset: fallbackCalleeOffset)
                : []);
        var graphSections = selectedBundle?.GraphSections
            ?? (allowNameGraphFallback
                ? BuildGraphSections(
                    CountSearchReferencesTotal(normalizedQuery, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact).Count,
                    references.Count,
                    fallbackReferenceOffset,
                    CountCallersTotal(normalizedQuery, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact).Count,
                    callers.Count,
                    fallbackCallerOffset,
                    CountCalleesTotal(normalizedQuery, lang, null, pathPatterns, excludePathPatterns, excludeTests, exact).Count,
                    callees.Count,
                    fallbackCalleeOffset)
                : new SymbolGraphSections());
        if (graphLanguage == null)
        {
            graphLanguageCandidates = references.Select(reference => reference.Lang)
                .Concat(callers.Select(caller => caller.Lang))
                .Concat(callees.Select(callee => callee.Lang))
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Select(candidate => NormalizeGraphEvidenceLanguage(candidate!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(candidate => candidate, StringComparer.Ordinal)
                .ToList();
            if (graphLanguageCandidates.Count == 1)
            {
                graphLanguage = graphLanguageCandidates[0];
                graphLanguageSource = "graph_evidence";
                graphLanguageConfidence = "inferred_consistent";
            }
            else if (graphLanguageCandidates.Count > 1)
            {
                graphLanguageSource = "graph_evidence";
                graphLanguageConfidence = "conflicted";
                graphLanguageConflict = true;
            }
        }
        var graphSupported = graphLanguage == null
            ? (bool?)null
            : SupportsReferenceLanguage(graphLanguage);
        var graphSupportReason = ReferenceExtractor.BuildGraphSupportReasonWithUnsupportedEnumMemberGap(
            graphLanguage,
            graphSupported,
            hasUnsupportedEnumMember,
            hasSupportedGraphDefinition);
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
        var relaxedSymbols = selectedSymbolId == null && exact && definitions.Count == 0 && references.Count == 0 && callers.Count == 0 && callees.Count == 0
            ? SearchSymbols(normalizedQuery, Math.Max(limit, 5), kind: null, lang, pathPatterns, excludePathPatterns, excludeTests, since: null, exact: false)
            : null;
        var exactZeroHint = selectedSymbolId == null && exact && definitions.Count == 0 && references.Count == 0 && callers.Count == 0 && callees.Count == 0
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
            GraphLanguageSource = graphLanguageSource,
            GraphLanguageConfidence = graphLanguageConfidence,
            GraphLanguageCandidates = graphLanguageCandidates,
            GraphLanguageConflict = graphLanguageConflict,
            GraphSupported = graphSupported,
            GraphSupportReason = graphSupportReason,
            GraphDegraded = hasUnsupportedEnumMember ? true : null,
            UnsupportedSymbolKind = unsupportedSymbolKind,
            Definitions = definitions,
            NearbySymbols = [.. nearbySymbols],
            References = [.. references],
            Callers = [.. callers],
            Callees = [.. callees],
            GraphSections = graphSections,
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
        CaptureAnalysisHeadMetadataSnapshot(result);
        txn.Commit();
        return result;
    }

    private void CaptureAnalysisHeadMetadataSnapshot(SymbolAnalysisResult result)
    {
        result.ProjectRoot = TryGetMetaStringInternal(DbContext.IndexedProjectRootMetaKey);
        result.IndexedHeadCommit = TryGetMetaStringInternal(DbContext.IndexedHeadCommitMetaKey);
        result.WorkspaceVerifiedHeadSha = TryGetMetaStringInternal(DbContext.WorkspaceVerifiedHeadShaMetaKey);
        result.IndexedHeadSha = TryGetMetaStringInternal(DbContext.IndexedHeadShaMetaKey);
        result.IndexedHeadCommitBranchSnapshot = TryGetMetaStringInternal(DbContext.IndexedHeadCommitBranchMetaKey);
        result.IndexedHeadCommitBranchStampPresentSnapshot = HasMetaKeyInternal(DbContext.IndexedHeadCommitBranchMetaKey);
        result.IndexedHeadBranchSnapshot = TryGetMetaStringInternal(DbContext.IndexedHeadBranchMetaKey);
        result.IndexedHeadBranchStampPresentSnapshot = HasMetaKeyInternal(DbContext.IndexedHeadBranchMetaKey);
        result.HeadMetadataSnapshotCaptured = true;
    }

    private string NormalizeGraphEvidenceLanguage(string language)
    {
        var storedLanguage = language.Trim().ToLowerInvariant();
        return GetWorkspaceSupportedReferenceLanguages().Contains(storedLanguage, StringComparer.Ordinal)
            ? storedLanguage
            : NormalizeQueryLanguage(storedLanguage) ?? storedLanguage;
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
        int maxLineWidth,
        SymbolGraphPageRequest? graphPage = null)
    {
        var identityAvailable = CanScopeCandidateByIdentity(definition);
        var hasAmbiguousInboundEvidence = identityAvailable
            && HasAmbiguousInboundEvidence(definition.SymbolId!.Value);
        var identityScoped = identityAvailable && !hasAmbiguousInboundEvidence;
        var selector = BuildSymbolCandidateSelector(definition);
        var referenceOffset = GetGraphSectionOffset(graphPage, "references", selector.Selector);
        var callerOffset = GetGraphSectionOffset(graphPage, "callers", selector.Selector);
        var calleeOffset = GetGraphSectionOffset(graphPage, "callees", selector.Selector);
        var references = identityAvailable
            ? SearchReferencesForCandidate(definition, limit, pathPatterns, excludePathPatterns, excludeTests, maxLineWidth, referenceOffset)
            : includeNameFallback
                ? SearchReferences(definition.Name, limit, definition.Lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true, maxLineWidth, offset: referenceOffset)
                : [];
        var callers = identityAvailable
            ? GetCallersForCandidate(definition, limit, pathPatterns, excludePathPatterns, excludeTests, callerOffset)
            : includeNameFallback
                ? GetCallers(definition.Name, limit, definition.Lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true, offset: callerOffset)
                : [];
        var callees = identityAvailable
            ? GetCalleesForCandidate(definition, limit, pathPatterns, excludePathPatterns, excludeTests, calleeOffset)
            : includeNameFallback
                ? GetCallees(definition.Name, limit, definition.Lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true, offset: calleeOffset)
                : [];
        var referenceTotal = identityAvailable
            ? CountSearchReferencesForCandidate(definition, pathPatterns, excludePathPatterns, excludeTests).Count
            : includeNameFallback
                ? CountSearchReferencesTotal(definition.Name, definition.Lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true).Count
                : 0;
        var callerTotal = identityAvailable
            ? CountCallersForCandidate(definition, pathPatterns, excludePathPatterns, excludeTests).Count
            : includeNameFallback
                ? CountCallersTotal(definition.Name, definition.Lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true).Count
                : 0;
        var calleeTotal = identityAvailable
            ? CountCalleesForCandidate(definition, pathPatterns, excludePathPatterns, excludeTests).Count
            : includeNameFallback
                ? CountCalleesTotal(definition.Name, definition.Lang, null, pathPatterns, excludePathPatterns, excludeTests, exact: true).Count
                : 0;
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
            Selector = selector,
            Definition = definition,
            File = GetFileByPath(definition.Path),
            GraphSupported = graphSupported,
            GraphSupportReason = graphSupportReason,
            IdentityScoped = identityScoped,
            IdentityScopeReason = identityScoped
                ? "exact_identity"
                : hasAmbiguousInboundEvidence
                    ? "ambiguous_reference_candidates"
                    : "identity_contract_unavailable",
            NearbySymbols = nearbySymbols,
            References = references,
            Callers = callers,
            Callees = callees,
            GraphSections = BuildGraphSections(
                referenceTotal,
                references.Count,
                referenceOffset,
                callerTotal,
                callers.Count,
                callerOffset,
                calleeTotal,
                callees.Count,
                calleeOffset),
        };
    }

    private static int GetGraphSectionOffset(
        SymbolGraphPageRequest? page,
        string section,
        string? candidateSelector)
        => page != null
           && string.Equals(page.Section, section, StringComparison.Ordinal)
           && string.Equals(page.CandidateSelector, candidateSelector, StringComparison.Ordinal)
            ? Math.Max(0, page.Offset)
            : 0;

    private static SymbolGraphSections BuildGraphSections(
        int referenceTotal,
        int referenceReturned,
        int referenceOffset,
        int callerTotal,
        int callerReturned,
        int callerOffset,
        int calleeTotal,
        int calleeReturned,
        int calleeOffset)
        => new()
        {
            References = BuildGraphSection(referenceTotal, referenceReturned, referenceOffset),
            Callers = BuildGraphSection(callerTotal, callerReturned, callerOffset),
            Callees = BuildGraphSection(calleeTotal, calleeReturned, calleeOffset),
        };

    private static SymbolGraphSection BuildGraphSection(int total, int returned, int offset)
        => new()
        {
            Total = total,
            Returned = returned,
            Offset = offset,
            Truncated = offset + returned < total,
        };

    private bool CanScopeCandidateByIdentity(DefinitionResult definition) =>
        _hasReferencesTable
        && _referenceIdentityContractCurrent
        && definition.SymbolId != null
        && !string.Equals(definition.Lang, "sql", StringComparison.Ordinal)
        && _referenceColumns.Contains("source_symbol_id")
        && HasTable("symbol_reference_candidates");

    private bool HasAmbiguousInboundEvidence(long symbolId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM symbol_reference_candidates AS candidate
                JOIN symbol_references AS reference
                  ON reference.id = candidate.reference_id
                WHERE candidate.symbol_id = @symbol_id
                  AND reference.resolution_candidate_count > 1
                LIMIT 1
            )
            """;
        SqliteCommandPolicy.Add(cmd, "@symbol_id", symbolId);
        return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
    }

    internal SymbolCandidateSelector BuildSymbolCandidateSelector(SymbolResult definition)
    {
        var container = definition.ContainerQualifiedName ?? definition.ContainerName;
        var qualifiedName = SyntheticSymbolIdentity.IsSyntheticSubKind(definition.SubKind)
            ? SyntheticSymbolIdentity.BuildFileQualifiedName(definition.Path, definition.Name)
            : string.IsNullOrWhiteSpace(container)
                ? definition.Name
                : $"{container}.{definition.Name}";
        var generationFingerprint = SymbolSelector.BuildGenerationFingerprint(
            GetSymbolSelectorGenerationIdentity());
        var selector = definition.SymbolId is long symbolId
            ? new SymbolSelector(symbolId, generationFingerprint).ToString()
            : $"{definition.Lang}:{definition.Path}:{definition.StartLine.ToString(CultureInfo.InvariantCulture)}:{qualifiedName}";

        return new SymbolCandidateSelector
        {
            Selector = selector,
            SymbolId = definition.SymbolId,
            GenerationFingerprint = definition.SymbolId != null ? generationFingerprint : null,
            QualifiedName = qualifiedName,
            Container = container,
            Signature = definition.Signature,
            Path = definition.Path,
            Line = definition.StartLine,
            Lang = definition.Lang,
            Kind = definition.Kind,
            SubKind = definition.SubKind,
        };
    }

    internal bool IsCurrentSymbolSelector(SymbolSelector selector)
        => selector.GenerationFingerprint == null
           || string.Equals(
               selector.GenerationFingerprint,
               SymbolSelector.BuildGenerationFingerprint(GetSymbolSelectorGenerationIdentity()),
               StringComparison.Ordinal);

    internal DefinitionResult? GetDefinitionBySelector(SymbolSelector selector, string? lang = null)
        => IsCurrentSymbolSelector(selector)
            ? GetDefinitionBySymbolId(
                selector.SymbolId,
                includeBody: false,
                bodyStartLine: null,
                bodyLineCount: null,
                lang,
                kind: null)
            : null;

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
                ? $"({BuildPersistedFoldedNameMatchSql("s.name_folded", "@queryFolded")} OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded) OR sql_leaf_name_folded(s.name) = @queryLeafFolded)))"
                : $"({BuildPersistedFoldedNameMatchSql("s.name_folded", "@queryFolded")} OR (f.lang = 'sql' AND sql_segment_count(s.name) = @querySegmentCount AND sql_normalize_name_folded(s.name) = @queryNormalizedFolded))"
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
        AddPersistedFoldedNameQueryParameters(cmd, "@queryFolded", query, lang);
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
