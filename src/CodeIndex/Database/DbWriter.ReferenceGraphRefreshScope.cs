using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private const string ReferenceGraphDirtyFilesTable = "reference_graph_dirty_files";
    private const string ReferenceGraphDirtyNamesTable = "reference_graph_dirty_names";
    private const string ReferenceGraphLookupNamesTable = "reference_graph_lookup_names";
    private const string ReferenceGraphRemovedReferencesTable = "reference_graph_removed_references";
    private const string ReferenceGraphDirtyReferencesTable = "reference_graph_dirty_references";
    private const int ReferenceGraphScopedMinimumFullFallbackCount = 4_096;
    private const int ReferenceGraphScopedFullFallbackPercent = 50;

    private static readonly AsyncLocal<Action<ReferenceGraphRefreshScopeStats>?>
        ScopedReferenceGraphRefreshScopeForTesting = new();
    private static readonly AsyncLocal<Action<string>?>
        ReferenceGraphRowCountForTestingScope = new();
    private ReferenceGraphRefreshScope? _referenceGraphRefreshScope;

    internal sealed record ReferenceGraphRefreshScopeStats(
        bool UsedFullRefresh,
        int DirtyFileCount,
        int DirtyNameCount,
        int DirtyReferenceCount,
        int TotalReferenceCount);

    internal static Action<ReferenceGraphRefreshScopeStats>? ReferenceGraphRefreshScopeForTesting
    {
        get => ScopedReferenceGraphRefreshScopeForTesting.Value;
        set => ScopedReferenceGraphRefreshScopeForTesting.Value = value;
    }

    internal static Action<string>? ReferenceGraphRowCountForTesting
    {
        get => ReferenceGraphRowCountForTestingScope.Value;
        set => ReferenceGraphRowCountForTestingScope.Value = value;
    }

    private static readonly string RefreshScopedReferenceSourceSymbolsSql = $"""
        UPDATE symbol_references AS r
        SET source_symbol_id = {BuildReferenceSourceSymbolValueSql("r")}
        WHERE r.id IN (
                  SELECT reference_id
                  FROM temp.{ReferenceGraphDirtyReferencesTable}
              )
          AND r.source_symbol_id IS NOT {BuildReferenceSourceSymbolValueSql("r")};
        """;

    private static string RefreshCSharpReferenceFactsScopedSql =>
        BuildRefreshCSharpReferenceFactsSql(
            $"r.id IN (SELECT reference_id FROM temp.{ReferenceGraphDirtyReferencesTable})");

    private static string RefreshCSharpSymbolFactsScopedSql =>
        BuildRefreshCSharpSymbolFactsSql(
            $"""
            JOIN temp.{ReferenceGraphLookupNamesTable} AS symbol_lookup
              ON symbol_lookup.lang = 'csharp'
             AND symbol_lookup.name_folded = symbol.name_folded
            """);

    private static string RefreshCSharpPropertyTargetFactsScopedSql =>
        BuildRefreshCSharpPropertyTargetFactsSql(
            $"""
            FROM temp.{ReferenceGraphLookupNamesTable} AS property_lookup
            CROSS JOIN symbols AS target INDEXED BY idx_symbols_name_folded
            """,
            "property_lookup.lang = 'csharp' " +
            "AND target.name_folded = property_lookup.name_folded");

    private static string NormalizeCSharpPropertyReceiverReferencesScopedSql =>
        BuildCSharpPropertyReceiverNormalizationSql(
            $"r.id IN (SELECT reference_id FROM temp.{ReferenceGraphDirtyReferencesTable})");

    private const string RefreshScopedReferenceUniqueFamiliesSql = $"""
        DELETE FROM temp.reference_unique_symbol_families;

        INSERT INTO temp.reference_unique_symbol_families(lang, name_folded, family_key)
        SELECT target_file.lang,
               s.name_folded,
               MIN(target_file.path || char(31) ||
                   COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                   COALESCE(s.name, '')) AS family_key
        FROM temp.{ReferenceGraphLookupNamesTable} AS dirty_name
        CROSS JOIN symbols AS s INDEXED BY idx_symbols_name_folded
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE s.name_folded = dirty_name.name_folded
          AND target_file.lang = dirty_name.lang
          AND target_file.lang <> 'ambiguous_m'
        GROUP BY target_file.lang, s.name_folded
        HAVING COUNT(target_file.path || char(31) ||
                     COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                     COALESCE(s.name, '')) > 0
           AND MIN(target_file.path || char(31) ||
                   COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                   COALESCE(s.name, ''))
               IS MAX(target_file.path || char(31) ||
                      COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                      COALESCE(s.name, ''));

        -- Keep the scoped projection aligned with the full-refresh union-wide
        -- uniqueness contract for callers whose .m dialect is unresolved.
        -- .m 方言が未確定な呼出し元について、差分更新でも全件更新と同じ
        -- 言語横断の一意性契約を維持する。
        INSERT INTO temp.reference_unique_symbol_families(lang, name_folded, family_key)
        SELECT 'ambiguous_m',
               s.name_folded,
               MIN(target_file.path || char(31) ||
                   COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                   COALESCE(s.name, '')) AS family_key
        FROM temp.{ReferenceGraphLookupNamesTable} AS dirty_name
        CROSS JOIN symbols AS s INDEXED BY idx_symbols_name_folded
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE dirty_name.lang = 'ambiguous_m'
          AND s.name_folded = dirty_name.name_folded
          AND target_file.lang IN ('matlab', 'objc')
        GROUP BY s.name_folded
        HAVING COUNT(target_file.path || char(31) ||
                     COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                     COALESCE(s.name, '')) > 0
           AND MIN(target_file.path || char(31) ||
                   COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                   COALESCE(s.name, ''))
               IS MAX(target_file.path || char(31) ||
                      COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                      COALESCE(s.name, ''));
        """;

    private static readonly string RefreshScopedReferenceCandidatesSql = BuildScopedReferenceCandidatesSql();

    private static readonly string RefreshScopedReferenceResolutionSymbolFactsSql = $"""
        DELETE FROM temp.{ReferenceResolutionSymbolFactsTable};

        WITH dirty_target_symbols(symbol_id) AS MATERIALIZED (
            SELECT candidate.symbol_id
            FROM temp.{ReferenceGraphDirtyReferencesTable} AS dirty_target_reference
            CROSS JOIN symbol_reference_candidates AS candidate
              ON candidate.reference_id = dirty_target_reference.reference_id
            GROUP BY candidate.symbol_id
        )
        INSERT INTO temp.{ReferenceResolutionSymbolFactsTable}(symbol_id, target_key)
        SELECT target.id,
               {BuildReferenceResolutionTargetKeySql()}
        FROM dirty_target_symbols AS dirty_target
        JOIN symbols AS target ON target.id = dirty_target.symbol_id
        JOIN files AS target_file ON target_file.id = target.file_id;
        """;

    private static readonly string RefreshScopedReferenceResolutionValuesSql = $"""
        UPDATE symbol_references AS r
        SET (target_symbol_id, target_symbol_key, resolution_candidate_count, resolution_state) = {ReferenceResolutionValueSql}
        WHERE r.id IN (
                  SELECT reference_id
                  FROM temp.{ReferenceGraphDirtyReferencesTable}
              )
          AND (r.target_symbol_id, r.target_symbol_key, r.resolution_candidate_count, r.resolution_state)
              IS NOT {ReferenceResolutionValueSql};
        """;

    private static readonly string RefreshScopedSelfReferenceSql = $"""
        UPDATE symbol_references AS r
        SET is_self_reference = {SelfReferenceValueSql}
        WHERE r.id IN (
                  SELECT reference_id
                  FROM temp.{ReferenceGraphDirtyReferencesTable}
              )
          AND r.is_self_reference IS NOT ({SelfReferenceValueSql});
        """;

    private static readonly string RefreshScopedReferenceResolutionSql =
        RefreshScopedReferenceResolutionValuesSql + "\n" + RefreshScopedSelfReferenceSql;

    private static readonly string RefreshScopedMutualRecursionFlagsSql = $"""
        UPDATE symbol_references AS r
        SET is_mutual_recursion = {MutualRecursionValueSql}
        WHERE r.id IN (
                  SELECT reference_id
                  FROM temp.{ReferenceGraphDirtyReferencesTable}
              )
          AND r.is_mutual_recursion IS NOT ({MutualRecursionValueSql});
        """;

    internal static string RefreshScopedReferenceCandidatesSqlForTesting
        => RefreshScopedReferenceCandidatesSql;

    internal static IReadOnlyList<(
        string Scope,
        string MaterializationSql,
        string ResolutionSql)> ReferenceResolutionFactSqlForTesting
        =>
        [
            ("fresh", RefreshReferenceResolutionSymbolFactsFullSql, RefreshReferenceResolutionFreshSparseSql),
            ("full", RefreshReferenceResolutionSymbolFactsFullSql, RefreshReferenceResolutionFullSql),
            ("differential", RefreshReferenceResolutionSymbolFactsFullSql, RefreshReferenceResolutionDifferentialSql),
            ("scoped", RefreshScopedReferenceResolutionSymbolFactsSql, RefreshScopedReferenceResolutionSql),
            ("retained", RefreshReferenceResolutionSymbolFactsFullSql, RefreshReferenceResolutionFullSql),
        ];

    internal static IReadOnlyList<(string Scope, string Sql)>
        CSharpGraphFactEvaluationSqlForTesting
        =>
        [
            (
                "full",
                RefreshCSharpReferenceFactsFullSql + "\n"
                + RefreshCSharpSymbolFactsFullSql + "\n"
                + RefreshCSharpTypeIdentityFactsSql + "\n"
                + RefreshCSharpConstructorIdentityFactsSql + "\n"
                + RefreshCSharpInstantiationFamilyFactsSql + "\n"
                + RefreshCSharpPropertyTargetFactsFullSql + "\n"
                + NormalizeCSharpPropertyReceiverReferencesFullSql + "\n"
                + RefreshReferenceCandidatesSql),
            (
                "scoped",
                RefreshCSharpReferenceFactsScopedSql + "\n"
                + RefreshCSharpSymbolFactsScopedSql + "\n"
                + RefreshCSharpTypeIdentityFactsSql + "\n"
                + RefreshCSharpConstructorIdentityFactsSql + "\n"
                + RefreshCSharpInstantiationFamilyFactsSql + "\n"
                + RefreshCSharpPropertyTargetFactsScopedSql + "\n"
                + NormalizeCSharpPropertyReceiverReferencesScopedSql + "\n"
                + RefreshScopedReferenceCandidatesSql),
            (
                "retained",
                RefreshCSharpReferenceFactsFullSql + "\n"
                + RefreshCSharpSymbolFactsFullSql + "\n"
                + RefreshCSharpTypeIdentityFactsSql + "\n"
                + RefreshCSharpConstructorIdentityFactsSql + "\n"
                + RefreshCSharpInstantiationFamilyFactsSql + "\n"
                + RefreshCSharpPropertyTargetFactsFullSql + "\n"
                + NormalizeCSharpPropertyReceiverReferencesFullSql + "\n"
                + RefreshReferenceCandidatesSql),
        ];

    internal static IReadOnlyList<(string Scope, string Sql)>
        CSharpGraphCandidateSqlForTesting
        =>
        [
            ("full", RefreshReferenceCandidatesSql),
            ("scoped", RefreshScopedReferenceCandidatesSql),
            ("retained", RefreshReferenceCandidatesSql),
        ];

    internal static IReadOnlyList<(string Scope, string Sql)>
        CSharpInstantiationFamilyFactSqlForTesting
        =>
        [
            ("full", RefreshCSharpInstantiationFamilyFactsSql),
            ("scoped", RefreshCSharpInstantiationFamilyFactsSql),
            ("retained", RefreshCSharpInstantiationFamilyFactsSql),
        ];

    internal static IReadOnlyList<(
        string Scope,
        string MaterializationSql,
        string NormalizationSql)> CSharpPropertyReceiverFactSqlForTesting
        =>
        [
            (
                "full",
                RefreshCSharpPropertyTargetFactsFullSql,
                NormalizeCSharpPropertyReceiverReferencesFullSql),
            (
                "scoped",
                RefreshCSharpPropertyTargetFactsScopedSql,
                NormalizeCSharpPropertyReceiverReferencesScopedSql),
            (
                "retained",
                RefreshCSharpPropertyTargetFactsFullSql,
                NormalizeCSharpPropertyReceiverReferencesFullSql),
        ];

    internal static IReadOnlyList<string> ScopedReferenceGraphUpdateStatementsForTesting
        =>
        [
            RefreshScopedReferenceSourceSymbolsSql,
            .. NormalizeCSharpPropertyReceiverReferencesScopedSql
                .Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Where(static statement => statement.StartsWith(
                    "UPDATE symbol_references",
                    StringComparison.Ordinal)),
            RefreshScopedReferenceResolutionValuesSql,
            RefreshScopedSelfReferenceSql,
            RefreshScopedMutualRecursionFlagsSql,
        ];

    internal static IReadOnlyList<(string Scope, string Sql)>
        ScopedUnresolvedMutualLookupStatementsForTesting
        =>
        [
            .. ExtractUnresolvedMutualLookupStatements("old", ExpandReferenceGraphOldMutualScopeSql),
            .. ExtractUnresolvedMutualLookupStatements("new", ExpandReferenceGraphNewMutualScopeSql),
        ];

    private static IEnumerable<(string Scope, string Sql)> ExtractUnresolvedMutualLookupStatements(
        string scope,
        string sql)
        => sql.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static statement => statement.Contains(
                "idx_symbol_refs_unresolved_mutual_folded",
                StringComparison.Ordinal))
            .Select(statement => (scope, statement));

    private static string BuildScopedReferenceCandidatesSql()
    {
        const string fullDeleteSql = "DELETE FROM symbol_reference_candidates;";
        const string fullReferenceSourceSql = "FROM symbol_references AS r";
        const string fullCSharpTypeSymbolSourceSql = "FROM symbols AS type_symbol";
        const string fullCSharpTypeNamePredicateSql = "AND type_symbol.name_folded IS NOT NULL";
        const string fullLowerRankCandidateSourceSql =
            "FROM symbol_reference_candidates AS lower_rank_candidate";
        const int expectedReferenceSourceCount = 12;

        if (CountOrdinalOccurrences(RefreshReferenceCandidatesSql, fullDeleteSql) != 1
            || CountOrdinalOccurrences(RefreshReferenceCandidatesSql, fullReferenceSourceSql)
                != expectedReferenceSourceCount
            || CountOrdinalOccurrences(RefreshReferenceCandidatesSql, fullCSharpTypeSymbolSourceSql) != 1
            || CountOrdinalOccurrences(RefreshReferenceCandidatesSql, fullCSharpTypeNamePredicateSql) != 1
            || CountOrdinalOccurrences(RefreshReferenceCandidatesSql, fullLowerRankCandidateSourceSql) != 1)
        {
            throw new InvalidOperationException(
                "The reference-candidate SQL shape changed without updating the dirty-scope projection.");
        }

        return RefreshReferenceCandidatesSql
            .Replace(
                fullDeleteSql,
                $"DELETE FROM symbol_reference_candidates WHERE reference_id IN (SELECT reference_id FROM temp.{ReferenceGraphDirtyReferencesTable});",
                StringComparison.Ordinal)
            .Replace(
                fullReferenceSourceSql,
                $"FROM temp.{ReferenceGraphDirtyReferencesTable} AS dirty_reference\n        CROSS JOIN symbol_references AS r ON r.id = dirty_reference.reference_id",
                StringComparison.Ordinal)
            .Replace(
                fullCSharpTypeSymbolSourceSql,
                $"FROM temp.{ReferenceGraphLookupNamesTable} AS type_lookup_name\n            CROSS JOIN symbols AS type_symbol INDEXED BY idx_symbols_name_folded",
                StringComparison.Ordinal)
            .Replace(
                fullCSharpTypeNamePredicateSql,
                "AND type_lookup_name.lang = 'csharp'\n              AND type_symbol.name_folded = type_lookup_name.name_folded",
                StringComparison.Ordinal)
            .Replace(
                fullLowerRankCandidateSourceSql,
                $"FROM temp.{ReferenceGraphDirtyReferencesTable} AS dirty_lower_rank\n        CROSS JOIN symbol_reference_candidates AS lower_rank_candidate\n          ON lower_rank_candidate.reference_id = dirty_lower_rank.reference_id",
                StringComparison.Ordinal);
    }

    private static int CountOrdinalOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private const string MaterializeReferenceGraphDirtyScopeSql = $"""
        DELETE FROM temp.{ReferenceGraphDirtyReferencesTable};
        DELETE FROM temp.{ReferenceGraphLookupNamesTable};

        INSERT OR IGNORE INTO temp.{ReferenceGraphDirtyNamesTable}(lang, name_folded)
        SELECT f.lang, s.name_folded
        FROM temp.{ReferenceGraphDirtyFilesTable} AS dirty_file
        JOIN files AS f ON f.id = dirty_file.file_id
        JOIN symbols AS s ON s.file_id = f.id
        WHERE f.lang IS NOT NULL
          AND s.name_folded IS NOT NULL;

        -- A changed C# FooAttribute definition also changes attribute references spelled Foo.
        -- C# FooAttribute定義の変更はFoo表記のattribute参照にも影響する。
        INSERT OR IGNORE INTO temp.{ReferenceGraphDirtyNamesTable}(lang, name_folded)
        SELECT lang, substr(name_folded, 1, length(name_folded) - 9)
        FROM temp.{ReferenceGraphDirtyNamesTable}
        WHERE lang = 'csharp'
          AND length(name_folded) > 9
          AND substr(name_folded, -9) = 'attribute';

        INSERT OR IGNORE INTO temp.{ReferenceGraphDirtyReferencesTable}(reference_id)
        SELECT r.id
        FROM temp.{ReferenceGraphDirtyFilesTable} AS dirty_file
        CROSS JOIN symbol_references AS r INDEXED BY idx_symbol_refs_file
        WHERE r.file_id = dirty_file.file_id;

        INSERT OR IGNORE INTO temp.{ReferenceGraphDirtyReferencesTable}(reference_id)
        SELECT r.id
        FROM temp.{ReferenceGraphDirtyNamesTable} AS dirty_name
        CROSS JOIN symbol_references AS r INDEXED BY idx_symbol_refs_symbol_name_folded_file
        JOIN files AS source_file ON source_file.id = r.file_id
        WHERE r.symbol_name_folded = dirty_name.name_folded
          AND (
              source_file.lang = dirty_name.lang
              OR (source_file.lang = 'ambiguous_m' AND dirty_name.lang IN ('matlab', 'objc'))
          );

        -- Explicit Markdown anchor identities preserve case and punctuation, while the
        -- indexed reference fold also carries the equivalent heading slug. Use that slug
        -- for the indexed seek, then require the exact raw identity.
        -- Markdown の明示 anchor は大文字小文字と句読点を保持するため、heading slug を
        -- index seek に使った後で raw identity の完全一致を要求する。
        INSERT OR IGNORE INTO temp.{ReferenceGraphDirtyReferencesTable}(reference_id)
        SELECT r.id
        FROM temp.{ReferenceGraphDirtyNamesTable} AS dirty_name
        CROSS JOIN symbol_references AS r INDEXED BY idx_symbol_refs_symbol_name_folded_file
        JOIN files AS source_file ON source_file.id = r.file_id
        WHERE dirty_name.lang = 'markdown'
          AND source_file.lang = 'markdown'
          AND r.reference_kind = 'reference'
          AND r.symbol_name_folded = markdown_normalize_fragment(dirty_name.name_folded)
          AND r.symbol_name = dirty_name.name_folded COLLATE BINARY;
        """;

    private const string MaterializeReferenceGraphLookupNamesSql = $"""
        INSERT OR IGNORE INTO temp.{ReferenceGraphLookupNamesTable}(lang, name_folded)
        SELECT source_file.lang, r.symbol_name_folded
        FROM temp.{ReferenceGraphDirtyReferencesTable} AS dirty
        JOIN symbol_references AS r ON r.id = dirty.reference_id
        JOIN files AS source_file ON source_file.id = r.file_id
        WHERE source_file.lang IS NOT NULL
          AND r.symbol_name_folded IS NOT NULL;

        INSERT OR IGNORE INTO temp.{ReferenceGraphLookupNamesTable}(lang, name_folded)
        SELECT target_lang.lang, r.symbol_name_folded
        FROM temp.{ReferenceGraphDirtyReferencesTable} AS dirty
        JOIN symbol_references AS r ON r.id = dirty.reference_id
        JOIN files AS source_file ON source_file.id = r.file_id
        CROSS JOIN (
            SELECT 'matlab' AS lang
            UNION ALL
            SELECT 'objc'
        ) AS target_lang
        WHERE source_file.lang = 'ambiguous_m'
          AND r.symbol_name_folded IS NOT NULL;

        INSERT OR IGNORE INTO temp.{ReferenceGraphLookupNamesTable}(lang, name_folded)
        SELECT 'csharp', r.symbol_name_folded || 'attribute'
        FROM temp.{ReferenceGraphDirtyReferencesTable} AS dirty
        JOIN symbol_references AS r ON r.id = dirty.reference_id
        JOIN files AS source_file ON source_file.id = r.file_id
        WHERE source_file.lang = 'csharp'
          AND r.reference_kind = 'attribute'
          AND r.symbol_name_folded IS NOT NULL;
        """;

    private const string ExpandReferenceGraphOldMutualScopeSql = $"""
        INSERT OR IGNORE INTO temp.{ReferenceGraphDirtyReferencesTable}(reference_id)
        SELECT reverse.id
        FROM temp.{ReferenceGraphDirtyReferencesTable} AS dirty
        JOIN symbol_references AS changed ON changed.id = dirty.reference_id
        JOIN symbol_references AS reverse INDEXED BY idx_symbol_refs_resolved_source_target_kind
          ON reverse.source_symbol_id = changed.target_symbol_id
         AND reverse.target_symbol_id = changed.source_symbol_id
        WHERE changed.source_symbol_id IS NOT NULL
          AND changed.target_symbol_id IS NOT NULL
          AND changed.source_symbol_id <> changed.target_symbol_id
          AND changed.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
          AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding');

        INSERT OR IGNORE INTO temp.{ReferenceGraphDirtyReferencesTable}(reference_id)
        SELECT reverse.id
        FROM temp.{ReferenceGraphRemovedReferencesTable} AS removed
        JOIN symbol_references AS reverse INDEXED BY idx_symbol_refs_resolved_source_target_kind
          ON reverse.source_symbol_id = removed.target_symbol_id
         AND reverse.target_symbol_id = removed.source_symbol_id
        WHERE removed.source_symbol_id IS NOT NULL
          AND removed.target_symbol_id IS NOT NULL
          AND removed.source_symbol_id <> removed.target_symbol_id
          AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding');

        INSERT OR IGNORE INTO temp.{ReferenceGraphDirtyReferencesTable}(reference_id)
        SELECT reverse.id
        FROM temp.{ReferenceGraphDirtyReferencesTable} AS dirty
        JOIN symbol_references AS changed ON changed.id = dirty.reference_id
        JOIN symbol_references AS reverse INDEXED BY idx_symbol_refs_unresolved_mutual_folded
          ON reverse.container_name_folded = changed.symbol_name_folded
         AND reverse.symbol_name_folded = changed.container_name_folded
        WHERE changed.source_symbol_id IS NULL
          AND changed.target_symbol_id IS NULL
          AND changed.is_self_reference = 0
          AND changed.container_name_folded IS NOT NULL
          AND changed.container_name_folded <> ''
          AND changed.symbol_name_folded IS NOT NULL
          AND changed.symbol_name_folded <> ''
          AND changed.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
          AND reverse.source_symbol_id IS NULL
          AND reverse.target_symbol_id IS NULL
          AND reverse.is_self_reference = 0
          AND reverse.container_name_folded IS NOT NULL
          AND reverse.container_name_folded <> ''
          AND reverse.symbol_name_folded IS NOT NULL
          AND reverse.symbol_name_folded <> ''
          AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding');

        INSERT OR IGNORE INTO temp.{ReferenceGraphDirtyReferencesTable}(reference_id)
        SELECT reverse.id
        FROM temp.{ReferenceGraphRemovedReferencesTable} AS removed
        JOIN symbol_references AS reverse INDEXED BY idx_symbol_refs_unresolved_mutual_folded
          ON reverse.container_name_folded = removed.symbol_name_folded
         AND reverse.symbol_name_folded = removed.container_name_folded
        WHERE removed.source_symbol_id IS NULL
          AND removed.target_symbol_id IS NULL
          AND removed.container_name_folded IS NOT NULL
          AND removed.container_name_folded <> ''
          AND removed.symbol_name_folded IS NOT NULL
          AND removed.symbol_name_folded <> ''
          AND reverse.source_symbol_id IS NULL
          AND reverse.target_symbol_id IS NULL
          AND reverse.is_self_reference = 0
          AND reverse.container_name_folded IS NOT NULL
          AND reverse.container_name_folded <> ''
          AND reverse.symbol_name_folded IS NOT NULL
          AND reverse.symbol_name_folded <> ''
          AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding');
        """;

    private const string ExpandReferenceGraphNewMutualScopeSql = $"""
        INSERT OR IGNORE INTO temp.{ReferenceGraphDirtyReferencesTable}(reference_id)
        SELECT reverse.id
        FROM temp.{ReferenceGraphDirtyReferencesTable} AS dirty
        JOIN symbol_references AS changed ON changed.id = dirty.reference_id
        JOIN symbol_references AS reverse INDEXED BY idx_symbol_refs_resolved_source_target_kind
          ON reverse.source_symbol_id = changed.target_symbol_id
         AND reverse.target_symbol_id = changed.source_symbol_id
        WHERE changed.source_symbol_id IS NOT NULL
          AND changed.target_symbol_id IS NOT NULL
          AND changed.source_symbol_id <> changed.target_symbol_id
          AND changed.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
          AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding');

        INSERT OR IGNORE INTO temp.{ReferenceGraphDirtyReferencesTable}(reference_id)
        SELECT reverse.id
        FROM temp.{ReferenceGraphDirtyReferencesTable} AS dirty
        JOIN symbol_references AS changed ON changed.id = dirty.reference_id
        JOIN symbol_references AS reverse INDEXED BY idx_symbol_refs_unresolved_mutual_folded
          ON reverse.container_name_folded = changed.symbol_name_folded
         AND reverse.symbol_name_folded = changed.container_name_folded
        WHERE changed.source_symbol_id IS NULL
          AND changed.target_symbol_id IS NULL
          AND changed.is_self_reference = 0
          AND changed.container_name_folded IS NOT NULL
          AND changed.container_name_folded <> ''
          AND changed.symbol_name_folded IS NOT NULL
          AND changed.symbol_name_folded <> ''
          AND changed.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
          AND reverse.source_symbol_id IS NULL
          AND reverse.target_symbol_id IS NULL
          AND reverse.is_self_reference = 0
          AND reverse.container_name_folded IS NOT NULL
          AND reverse.container_name_folded <> ''
          AND reverse.symbol_name_folded IS NOT NULL
          AND reverse.symbol_name_folded <> ''
          AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding');
        """;

    private const string ClearReferenceGraphDirtyScopeSql = $"""
        DELETE FROM temp.{ReferenceGraphDirtyFilesTable};
        DELETE FROM temp.{ReferenceGraphDirtyNamesTable};
        DELETE FROM temp.{ReferenceGraphLookupNamesTable};
        DELETE FROM temp.{ReferenceGraphRemovedReferencesTable};
        DELETE FROM temp.{ReferenceGraphDirtyReferencesTable};
        """;

    /// <summary>
    /// Track the graph identities touched by one indexing run. The TEMP rows participate in
    /// the caller's SQLite transactions, so rolled-back file batches never leak dirty state.
    /// Indexing from an empty/stale contract, or a broad dirty set, retains the full refresh.
    /// 1回のindex runで変更されたgraph identityを追跡する。TEMP行も呼出元transactionに
    /// 参加するため、rollbackされたfile batchのdirty状態は残らない。空・stale契約・広範な
    /// dirty集合では従来のfull refreshを維持する。
    /// </summary>
    internal ReferenceGraphRefreshScope BeginReferenceGraphRefreshScope(
        bool forceFullRefresh = false,
        bool useFreshReferenceResolutionDefaults = false)
    {
        if (_referenceGraphRefreshScope != null)
            throw new InvalidOperationException("A reference graph refresh scope is already active for this writer.");
        if (useFreshReferenceResolutionDefaults && !forceFullRefresh)
        {
            throw new ArgumentException(
                "Fresh reference resolution defaults require a forced full graph refresh.",
                nameof(useFreshReferenceResolutionDefaults));
        }

        using (var command = _conn.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TEMP TABLE IF NOT EXISTS {ReferenceGraphDirtyFilesTable} (
                    file_id INTEGER PRIMARY KEY
                ) WITHOUT ROWID;
                CREATE TEMP TABLE IF NOT EXISTS {ReferenceGraphDirtyNamesTable} (
                    lang TEXT NOT NULL,
                    name_folded TEXT NOT NULL,
                    PRIMARY KEY(lang, name_folded)
                ) WITHOUT ROWID;
                CREATE TEMP TABLE IF NOT EXISTS {ReferenceGraphLookupNamesTable} (
                    lang TEXT NOT NULL,
                    name_folded TEXT NOT NULL,
                    PRIMARY KEY(lang, name_folded)
                ) WITHOUT ROWID;
                CREATE TEMP TABLE IF NOT EXISTS {ReferenceGraphRemovedReferencesTable} (
                    reference_id INTEGER PRIMARY KEY,
                    source_symbol_id INTEGER,
                    target_symbol_id INTEGER,
                    container_name_folded TEXT,
                    symbol_name_folded TEXT
                ) WITHOUT ROWID;
                CREATE TEMP TABLE IF NOT EXISTS {ReferenceGraphDirtyReferencesTable} (
                    reference_id INTEGER PRIMARY KEY
                ) WITHOUT ROWID;
                DELETE FROM {ReferenceGraphDirtyFilesTable};
                DELETE FROM {ReferenceGraphDirtyNamesTable};
                DELETE FROM {ReferenceGraphLookupNamesTable};
                DELETE FROM {ReferenceGraphRemovedReferencesTable};
                DELETE FROM {ReferenceGraphDirtyReferencesTable};
                """;
            command.ExecuteNonQuery();
        }

        var scope = new ReferenceGraphRefreshScope(
            this,
            forceFullRefresh || !ReferenceIdentityContractMatchesCurrent(),
            useFreshReferenceResolutionDefaults);
        _referenceGraphRefreshScope = scope;
        return scope;
    }

    private bool IsTrackingReferenceGraphRefresh
        // A forced full refresh never consumes the dirty TEMP tables. Fresh indexes and
        // rebuilds can therefore skip the per-batch Distinct/materialization work across
        // every language while preserving the scoped path for existing databases.
        // forced full refreshはdirty TEMP tableを参照しないため、全言語のbatchごとの
        // Distinct/materializeを省き、既存DBのscoped pathだけ追跡する。
        => _referenceGraphRefreshScope is
        {
            IsCompleting: false,
            IsDisposed: false,
            ForceFullRefresh: false,
        };

    private void RequireFullReferenceGraphRefresh()
    {
        if (_referenceGraphRefreshScope is { IsDisposed: false } scope)
            scope.RequireFullRefresh();
    }

    internal void RequireFullReferenceGraphRefreshForSecondaryIndexDeferral()
        => RequireFullReferenceGraphRefresh();

    private void TrackReferenceGraphFileAtPathBeforeMutation(string path)
    {
        if (!IsTrackingReferenceGraphRefresh)
            return;

        using var command = _conn.CreateCommand();
        command.Transaction = _activeTransaction;
        command.CommandText = "SELECT id FROM files WHERE path = @path";
        command.Parameters.Add("@path", SqliteType.Text).Value = path;
        var value = command.ExecuteScalar();
        if (value != null && value != DBNull.Value)
            TrackReferenceGraphFilesBeforeMutation([Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private void TrackReferenceGraphFileIds(IEnumerable<long> fileIds)
    {
        if (!IsTrackingReferenceGraphRefresh)
            return;

        var uniqueIds = fileIds.Distinct().ToArray();
        for (var offset = 0; offset < uniqueIds.Length; offset += DeleteFilesBatchSize)
        {
            var count = Math.Min(DeleteFilesBatchSize, uniqueIds.Length - offset);
            using var command = _conn.CreateCommand();
            command.Transaction = _activeTransaction;
            var values = new string[count];
            for (var parameterIndex = 0; parameterIndex < count; parameterIndex++)
            {
                var parameterName = SqliteDynamicSql.BuildParameterName("file_id", parameterIndex);
                values[parameterIndex] = $"({parameterName})";
                command.Parameters.Add(parameterName, SqliteType.Integer).Value = uniqueIds[offset + parameterIndex];
            }
            command.CommandText = $"INSERT OR IGNORE INTO {ReferenceGraphDirtyFilesTable}(file_id) VALUES {string.Join(", ", values)}";
            command.ExecuteNonQuery();
        }
    }

    private void TrackReferenceGraphFilesBeforeMutation(IEnumerable<long> fileIds)
    {
        if (!IsTrackingReferenceGraphRefresh)
            return;

        var uniqueIds = fileIds.Distinct().ToArray();
        TrackReferenceGraphFileIds(uniqueIds);
        for (var offset = 0; offset < uniqueIds.Length; offset += DeleteFilesBatchSize)
        {
            var count = Math.Min(DeleteFilesBatchSize, uniqueIds.Length - offset);
            using var command = _conn.CreateCommand();
            command.Transaction = _activeTransaction;
            var parameters = new string[count];
            for (var parameterIndex = 0; parameterIndex < count; parameterIndex++)
            {
                var parameterName = SqliteDynamicSql.BuildParameterName("old_file_id", parameterIndex);
                parameters[parameterIndex] = parameterName;
                command.Parameters.Add(parameterName, SqliteType.Integer).Value = uniqueIds[offset + parameterIndex];
            }
            var idList = string.Join(", ", parameters);
            command.CommandText = $"""
                INSERT OR IGNORE INTO {ReferenceGraphDirtyNamesTable}(lang, name_folded)
                SELECT f.lang, s.name_folded
                FROM symbols AS s
                JOIN files AS f ON f.id = s.file_id
                WHERE s.file_id IN ({idList})
                  AND f.lang IS NOT NULL
                  AND s.name_folded IS NOT NULL;

                INSERT OR IGNORE INTO {ReferenceGraphRemovedReferencesTable}(
                    reference_id,
                    source_symbol_id,
                    target_symbol_id,
                    container_name_folded,
                    symbol_name_folded)
                SELECT r.id,
                       r.source_symbol_id,
                       r.target_symbol_id,
                       r.container_name_folded,
                       r.symbol_name_folded
                FROM symbol_references AS r
                WHERE r.file_id IN ({idList});
                """;
            command.ExecuteNonQuery();
        }
    }

    private void TrackReferenceGraphInsertedSymbols(IReadOnlyList<CodeIndex.Models.SymbolRecord> symbols)
    {
        if (symbols.Count > 0)
            TrackReferenceGraphFileIds(symbols.Select(static symbol => symbol.FileId));
    }

    private void TrackReferenceGraphInsertedReferences(IReadOnlyList<CodeIndex.Models.ReferenceRecord> references)
    {
        if (references.Count > 0)
            TrackReferenceGraphFileIds(references.Select(static reference => reference.FileId));
    }

    private void TrackReferenceGraphDeletedReferences(
        IReadOnlyList<(
            long Id,
            long FileId,
            long? SourceId,
            long? TargetId,
            string? ContainerNameFolded,
            string? SymbolNameFolded)> references)
    {
        if (!IsTrackingReferenceGraphRefresh || references.Count == 0)
            return;

        TrackReferenceGraphFileIds(references.Select(static reference => reference.FileId));
        var rowsPerStatement = GetRowsPerInsertStatement(columnCount: 5);
        for (var offset = 0; offset < references.Count; offset += rowsPerStatement)
        {
            var count = Math.Min(rowsPerStatement, references.Count - offset);
            using var command = _conn.CreateCommand();
            command.Transaction = _activeTransaction;
            var values = new string[count];
            for (var parameterIndex = 0; parameterIndex < count; parameterIndex++)
            {
                var reference = references[offset + parameterIndex];
                var idName = SqliteDynamicSql.BuildParameterName("removed_reference_id", parameterIndex);
                var sourceName = SqliteDynamicSql.BuildParameterName("removed_source_id", parameterIndex);
                var targetName = SqliteDynamicSql.BuildParameterName("removed_target_id", parameterIndex);
                var containerName = SqliteDynamicSql.BuildParameterName("removed_container_name", parameterIndex);
                var symbolName = SqliteDynamicSql.BuildParameterName("removed_symbol_name", parameterIndex);
                values[parameterIndex] = $"({idName}, {sourceName}, {targetName}, {containerName}, {symbolName})";
                command.Parameters.Add(idName, SqliteType.Integer).Value = reference.Id;
                command.Parameters.Add(sourceName, SqliteType.Integer).Value = (object?)reference.SourceId ?? DBNull.Value;
                command.Parameters.Add(targetName, SqliteType.Integer).Value = (object?)reference.TargetId ?? DBNull.Value;
                command.Parameters.Add(containerName, SqliteType.Text).Value = (object?)reference.ContainerNameFolded ?? DBNull.Value;
                command.Parameters.Add(symbolName, SqliteType.Text).Value = (object?)reference.SymbolNameFolded ?? DBNull.Value;
            }
            command.CommandText = $"""
                INSERT OR IGNORE INTO {ReferenceGraphRemovedReferencesTable}(
                    reference_id,
                    source_symbol_id,
                    target_symbol_id,
                    container_name_folded,
                    symbol_name_folded)
                VALUES {string.Join(", ", values)}
                """;
            command.ExecuteNonQuery();
        }
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private ReferenceGraphRefreshPlan BuildReferenceGraphRefreshPlan(
        ReferenceGraphRefreshScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scope.ForceFullRefresh)
        {
            if (ReferenceGraphRefreshScopeForTesting == null)
                return new ReferenceGraphRefreshPlan(true, 0, 0, 0, 0);

            var fullReferenceCount = CountReferenceGraphRows("symbol_references", cancellationToken);
            return new ReferenceGraphRefreshPlan(
                UseFullRefresh: true,
                DirtyFileCount: CountReferenceGraphRows($"temp.{ReferenceGraphDirtyFilesTable}", cancellationToken),
                DirtyNameCount: CountReferenceGraphRows($"temp.{ReferenceGraphDirtyNamesTable}", cancellationToken),
                DirtyReferenceCount: fullReferenceCount,
                TotalReferenceCount: fullReferenceCount);
        }

        ExecuteReferenceGraphScopeSql(MaterializeReferenceGraphDirtyScopeSql, cancellationToken);
        ExecuteReferenceGraphScopeSql(ExpandReferenceGraphOldMutualScopeSql, cancellationToken);
        ExecuteReferenceGraphScopeSql(MaterializeReferenceGraphLookupNamesSql, cancellationToken);
        var dirtyFileCount = CountReferenceGraphRows($"temp.{ReferenceGraphDirtyFilesTable}", cancellationToken);
        var dirtyNameCount = CountReferenceGraphRows($"temp.{ReferenceGraphDirtyNamesTable}", cancellationToken);
        var dirtyReferenceCount = CountReferenceGraphRows($"temp.{ReferenceGraphDirtyReferencesTable}", cancellationToken);
        var needsTotalReferenceCount = dirtyReferenceCount >= ReferenceGraphScopedMinimumFullFallbackCount
            || ReferenceGraphRefreshScopeForTesting != null;
        var totalReferenceCount = needsTotalReferenceCount
            ? CountReferenceGraphRows("symbol_references", cancellationToken)
            : 0;
        var broadDirtyScope = dirtyReferenceCount >= ReferenceGraphScopedMinimumFullFallbackCount
            && (long)dirtyReferenceCount * 100
                >= (long)Math.Max(1, totalReferenceCount) * ReferenceGraphScopedFullFallbackPercent;
        return new ReferenceGraphRefreshPlan(
            broadDirtyScope,
            dirtyFileCount,
            dirtyNameCount,
            dirtyReferenceCount,
            totalReferenceCount);
    }

    private int CountReferenceGraphRows(string tableName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReferenceGraphRowCountForTesting?.Invoke(tableName);
        using var command = _conn.CreateCommand();
        command.Transaction = _activeTransaction;
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
        return checked((int)Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    private HashSet<long> GetReferenceGraphRefreshFileIds(
        bool useFullRefresh,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = _conn.CreateCommand();
        command.Transaction = _activeTransaction;
        command.CommandText = useFullRefresh
            ? "SELECT DISTINCT file_id FROM symbol_references"
            : $"""
               SELECT DISTINCT reference.file_id
               FROM temp.{ReferenceGraphDirtyReferencesTable} AS dirty
               JOIN symbol_references AS reference ON reference.id = dirty.reference_id
               """;
        using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
        var fileIds = new HashSet<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            fileIds.Add(reader.GetInt64(0));
        }
        return fileIds;
    }

    private void ExecuteReferenceGraphScopeSql(string sql, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = _conn.CreateCommand();
        command.Transaction = _activeTransaction;
        command.CommandText = sql;
        using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void DeleteRemovedReferenceCandidates(CancellationToken cancellationToken)
        => ExecuteReferenceGraphScopeSql(
            $"""
            DELETE FROM symbol_reference_candidates
            WHERE reference_id IN (
                SELECT reference_id
                FROM temp.{ReferenceGraphRemovedReferencesTable}
            );
            """,
            cancellationToken);

    private readonly record struct ReferenceGraphRefreshPlan(
        bool UseFullRefresh,
        int DirtyFileCount,
        int DirtyNameCount,
        int DirtyReferenceCount,
        int TotalReferenceCount);

    internal sealed class ReferenceGraphRefreshScope : IDisposable
    {
        private readonly DbWriter _writer;
        private bool _forceFullRefresh;
        private bool _freshReferenceResolutionDefaultsPending;

        internal ReferenceGraphRefreshScope(
            DbWriter writer,
            bool forceFullRefresh,
            bool useFreshReferenceResolutionDefaults)
        {
            _writer = writer;
            _forceFullRefresh = forceFullRefresh;
            _freshReferenceResolutionDefaultsPending = useFreshReferenceResolutionDefaults;
        }

        internal bool IsCompleting { get; set; }
        internal bool IsDisposed { get; private set; }
        internal bool ForceFullRefresh => _forceFullRefresh;
        internal bool FreshReferenceResolutionDefaultsPending
            => _freshReferenceResolutionDefaultsPending;

        internal void RequireFullRefresh() => _forceFullRefresh = true;

        internal void DisableFreshReferenceResolutionDefaults()
            => _freshReferenceResolutionDefaultsPending = false;

        internal void MarkRefreshCompleted()
        {
            _forceFullRefresh = false;
            _freshReferenceResolutionDefaultsPending = false;
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;
            IsDisposed = true;
            if (ReferenceEquals(_writer._referenceGraphRefreshScope, this))
                _writer._referenceGraphRefreshScope = null;
        }
    }
}
