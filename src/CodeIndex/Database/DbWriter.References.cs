using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    internal static Action? HotspotAggregateRefreshExecutingForTesting { get; set; }
    private const string NonTypeReceiverQualifierPrefix = "\u001freceiver:";
    private const int MaxReferenceLineWindowBatchCount = 32;

    private const string MutualRecursionValueSql = """
        CASE
            WHEN r.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
             AND (
                (
                    r.source_symbol_id IS NOT NULL
                    AND r.target_symbol_id IS NOT NULL
                    AND r.source_symbol_id <> r.target_symbol_id
                    AND EXISTS (
                        SELECT 1
                        FROM symbol_references AS reverse
                        WHERE reverse.source_symbol_id = r.target_symbol_id
                          AND reverse.target_symbol_id = r.source_symbol_id
                          AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
                    )
                )
                OR (
                    r.source_symbol_id IS NULL
                    AND r.target_symbol_id IS NULL
                    AND r.is_self_reference = 0
                    AND r.container_name IS NOT NULL
                    AND r.container_name <> ''
                    AND r.symbol_name IS NOT NULL
                    AND r.symbol_name <> ''
                    AND (
                        (
                            r.container_name_folded IS NOT NULL
                            AND r.container_name_folded <> ''
                            AND r.symbol_name_folded IS NOT NULL
                            AND r.symbol_name_folded <> ''
                            AND EXISTS (
                                SELECT 1
                                FROM symbol_references AS reverse
                                WHERE reverse.source_symbol_id IS NULL
                                  AND reverse.target_symbol_id IS NULL
                                  AND reverse.is_self_reference = 0
                                  AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
                                  AND reverse.container_name_folded = r.symbol_name_folded
                                  AND reverse.symbol_name_folded = r.container_name_folded
                            )
                        )
                        OR (
                            (r.container_name_folded IS NULL OR r.symbol_name_folded IS NULL)
                            AND EXISTS (
                                SELECT 1
                                FROM symbol_references AS reverse
                                WHERE reverse.source_symbol_id IS NULL
                                  AND reverse.target_symbol_id IS NULL
                                  AND reverse.is_self_reference = 0
                                  AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
                                  AND reverse.container_name = r.symbol_name COLLATE NOCASE
                                  AND reverse.symbol_name = r.container_name COLLATE NOCASE
                            )
                        )
                    )
                )
             )
            THEN 1
            ELSE 0
        END
        """;

    private const string ReferenceSourceSymbolValueSql = """
        (
            SELECT s.id
            FROM symbols AS s
            WHERE s.file_id = r.file_id
              AND r.container_name IS NOT NULL
              AND r.container_name <> ''
              AND (s.name_folded = r.container_name_folded
                   OR (s.name_folded IS NULL AND s.name = r.container_name COLLATE NOCASE))
              AND r.line BETWEEN COALESCE(s.start_line, s.line) AND COALESCE(s.end_line, s.line)
            ORDER BY (COALESCE(s.end_line, s.line) - COALESCE(s.start_line, s.line)),
                     COALESCE(s.start_line, s.line) DESC,
                     s.id
            LIMIT 1
        )
        """;

    private static readonly string RefreshReferenceSourceSymbolsFullSql = $"""
        UPDATE symbol_references AS r
        SET source_symbol_id = {ReferenceSourceSymbolValueSql}
        """;

    private static readonly string RefreshReferenceSourceSymbolsDifferentialSql = $"""
        UPDATE symbol_references AS r
        SET source_symbol_id = {ReferenceSourceSymbolValueSql}
        -- IS NOT is null-safe: stable NULL identities must not be rewritten either.
        -- IS NOTはNULL-safeであり、安定したNULL identityも再書込みしない。
        WHERE r.source_symbol_id IS NOT {ReferenceSourceSymbolValueSql}
        """;

    private const string CreateReferenceUniqueFamiliesSql = """
        CREATE TEMP TABLE IF NOT EXISTS reference_unique_symbol_families (
            lang        TEXT NOT NULL,
            name_folded TEXT NOT NULL,
            family_key  TEXT NOT NULL,
            PRIMARY KEY(lang, name_folded)
        ) WITHOUT ROWID;

        CREATE TEMP TABLE IF NOT EXISTS csharp_type_direct_bases (
            derived_qualified_name TEXT NOT NULL,
            base_qualified_name    TEXT NOT NULL,
            PRIMARY KEY(derived_qualified_name, base_qualified_name)
        ) WITHOUT ROWID;

        CREATE TEMP TABLE IF NOT EXISTS csharp_type_inheritance (
            derived_qualified_name TEXT NOT NULL,
            base_qualified_name    TEXT NOT NULL,
            distance               INTEGER NOT NULL,
            PRIMARY KEY(derived_qualified_name, base_qualified_name)
        ) WITHOUT ROWID
        """;

    private const string CSharpTypeReferenceCandidatePredicateSql = """
        (
            source_file.lang <> 'csharp'
            OR r.reference_kind <> 'type_reference'
            OR CASE
                WHEN s.kind NOT IN ('class', 'struct', 'record', 'interface', 'enum', 'delegate') THEN 0
                WHEN s.name <> r.symbol_name COLLATE BINARY THEN 0
                WHEN csharp_reference_type_arity(
                         COALESCE(
                             r.context,
                             (SELECT reference_line.context
                              FROM reference_lines AS reference_line
                              WHERE reference_line.id = r.reference_line_id)),
                         r.symbol_name,
                         r.column_number) IS NULL THEN 1
                WHEN csharp_definition_type_arity(s.signature, s.name, s.kind)
                     = csharp_reference_type_arity(
                         COALESCE(
                             r.context,
                             (SELECT reference_line.context
                              FROM reference_lines AS reference_line
                              WHERE reference_line.id = r.reference_line_id)),
                         r.symbol_name,
                         r.column_number) THEN 1
                ELSE 0
            END = 1
        )
        """;

    private static string BuildCSharpPropertyReceiverNormalizationSql(string scopePredicate) =>
        $"""
        DELETE FROM temp.csharp_type_direct_bases;
        DELETE FROM temp.csharp_type_inheritance;

        INSERT OR IGNORE INTO temp.csharp_type_direct_bases(
            derived_qualified_name,
            base_qualified_name)
        SELECT
            CASE
                WHEN COALESCE(derived.container_qualified_name, '') = ''
                    THEN derived.name
                WHEN derived.container_qualified_name = derived.name COLLATE BINARY
                     OR substr(
                            derived.container_qualified_name,
                            -length(derived.name) - 1
                        ) = ('.' || derived.name) COLLATE BINARY
                    THEN derived.container_qualified_name
                ELSE derived.container_qualified_name || '.' || derived.name
            END,
            CASE
                WHEN COALESCE(base_type.container_qualified_name, '') = ''
                    THEN base_type.name
                WHEN base_type.container_qualified_name = base_type.name COLLATE BINARY
                     OR substr(
                            base_type.container_qualified_name,
                            -length(base_type.name) - 1
                        ) = ('.' || base_type.name) COLLATE BINARY
                    THEN base_type.container_qualified_name
                ELSE base_type.container_qualified_name || '.' || base_type.name
            END
        FROM symbols AS derived
        JOIN files AS derived_file ON derived_file.id = derived.file_id
        JOIN json_each(
            csharp_base_identifiers_json(derived.signature)
        ) AS base_reference
        JOIN symbols AS base_type INDEXED BY idx_symbols_name_folded
          ON base_type.name_folded =
             csharp_base_name_folded(base_reference.value)
         AND base_type.name =
             csharp_base_name(base_reference.value) COLLATE BINARY
        JOIN files AS base_file ON base_file.id = base_type.file_id
        WHERE derived_file.lang = 'csharp'
          AND base_file.lang = 'csharp'
          AND derived.kind IN ('class', 'record')
          AND base_type.kind IN ('class', 'record')
          AND csharp_base_reference_matches(
                  base_reference.value,
                  base_type.name,
                  CASE
                      WHEN COALESCE(base_type.container_qualified_name, '') = ''
                          THEN base_type.name
                      WHEN base_type.container_qualified_name =
                               base_type.name COLLATE BINARY
                           OR substr(
                                  base_type.container_qualified_name,
                                  -length(base_type.name) - 1
                              ) = ('.' || base_type.name) COLLATE BINARY
                          THEN base_type.container_qualified_name
                      ELSE base_type.container_qualified_name || '.' || base_type.name
                  END,
                  CASE
                      WHEN COALESCE(derived.container_qualified_name, '') = ''
                          THEN derived.name
                      WHEN derived.container_qualified_name =
                               derived.name COLLATE BINARY
                           OR substr(
                                  derived.container_qualified_name,
                                  -length(derived.name) - 1
                              ) = ('.' || derived.name) COLLATE BINARY
                          THEN derived.container_qualified_name
                      ELSE derived.container_qualified_name || '.' || derived.name
                  END) = 1;

        INSERT OR IGNORE INTO temp.csharp_type_inheritance(
            derived_qualified_name,
            base_qualified_name,
            distance)
        WITH RECURSIVE inheritance(
            derived_qualified_name,
            base_qualified_name,
            distance,
            path) AS (
            SELECT direct.derived_qualified_name,
                   direct.base_qualified_name,
                   1,
                   char(31) || direct.derived_qualified_name ||
                       char(31) || direct.base_qualified_name || char(31)
            FROM temp.csharp_type_direct_bases AS direct
            UNION ALL
            SELECT inheritance.derived_qualified_name,
                   direct.base_qualified_name,
                   inheritance.distance + 1,
                   inheritance.path || direct.base_qualified_name || char(31)
            FROM inheritance
            JOIN temp.csharp_type_direct_bases AS direct
              ON direct.derived_qualified_name =
                 inheritance.base_qualified_name COLLATE BINARY
            WHERE inheritance.distance < 32
              AND instr(
                      inheritance.path,
                      char(31) || direct.base_qualified_name || char(31)
                  ) = 0
        )
        SELECT derived_qualified_name,
               base_qualified_name,
               MIN(distance)
        FROM inheritance
        GROUP BY derived_qualified_name, base_qualified_name;

        UPDATE symbol_references AS r
        SET reference_kind = 'type_reference',
            target_qualifier = NULL
        WHERE {scopePredicate}
          AND r.reference_kind = 'reference'
          AND r.target_qualifier LIKE char(31) || 'property_receiver:%'
          AND NOT EXISTS (
              SELECT 1
              FROM symbols AS source
              JOIN files AS source_file ON source_file.id = source.file_id
              JOIN symbols AS target
                ON target.name_folded = r.symbol_name_folded
               AND target.name = r.symbol_name COLLATE BINARY
              JOIN files AS target_file ON target_file.id = target.file_id
              WHERE source.id = r.source_symbol_id
                AND source_file.lang = 'csharp'
                AND target_file.lang = 'csharp'
                AND target.kind = 'property'
                AND target.container_qualified_name IN (
                    SELECT source.container_qualified_name
                    UNION
                    SELECT inheritance.base_qualified_name
                    FROM temp.csharp_type_inheritance AS inheritance
                    WHERE inheritance.derived_qualified_name =
                          source.container_qualified_name COLLATE BINARY
                )
                AND r.target_qualifier =
                    char(31) || 'property_receiver:' ||
                        target.container_qualified_name COLLATE BINARY
          );

        UPDATE symbol_references AS r
        SET reference_kind = 'reference',
            target_qualifier = char(31) || 'property_receiver:' || (
                SELECT target.container_qualified_name
                FROM symbols AS source
                JOIN files AS source_file ON source_file.id = source.file_id
                JOIN symbols AS target
                  ON target.name_folded = r.symbol_name_folded
                 AND target.name = r.symbol_name COLLATE BINARY
                JOIN files AS target_file ON target_file.id = target.file_id
                WHERE source.id = r.source_symbol_id
                  AND source_file.lang = 'csharp'
                  AND target_file.lang = 'csharp'
                  AND target.kind = 'property'
                  AND target.container_qualified_name IN (
                      SELECT source.container_qualified_name
                      UNION
                      SELECT inheritance.base_qualified_name
                      FROM temp.csharp_type_inheritance AS inheritance
                      WHERE inheritance.derived_qualified_name =
                            source.container_qualified_name COLLATE BINARY
                  )
                ORDER BY CASE
                             WHEN target.container_qualified_name =
                                  source.container_qualified_name COLLATE BINARY
                                 THEN 0
                             ELSE COALESCE((
                                 SELECT inheritance.distance
                                 FROM temp.csharp_type_inheritance AS inheritance
                                 WHERE inheritance.derived_qualified_name =
                                       source.container_qualified_name COLLATE BINARY
                                   AND inheritance.base_qualified_name =
                                       target.container_qualified_name COLLATE BINARY
                             ), 33)
                         END,
                         target.id
                LIMIT 1
            )
        WHERE {scopePredicate}
          AND r.reference_kind = 'type_reference'
          AND r.target_qualifier IS NULL
          AND csharp_reference_is_member_receiver(
                  COALESCE(
                      r.context,
                      (
                          SELECT reference_line.context
                          FROM reference_lines AS reference_line
                          WHERE reference_line.id = r.reference_line_id
                      )
                  ),
                  r.symbol_name,
                  r.column_number) = 1
          AND EXISTS (
              SELECT 1
              FROM symbols AS source
              JOIN files AS source_file ON source_file.id = source.file_id
              JOIN symbols AS target
                ON target.name_folded = r.symbol_name_folded
               AND target.name = r.symbol_name COLLATE BINARY
              JOIN files AS target_file ON target_file.id = target.file_id
              WHERE source.id = r.source_symbol_id
                AND source_file.lang = 'csharp'
                AND target_file.lang = 'csharp'
                AND target.kind = 'property'
                AND target.container_qualified_name IN (
                    SELECT source.container_qualified_name
                    UNION
                    SELECT inheritance.base_qualified_name
                    FROM temp.csharp_type_inheritance AS inheritance
                    WHERE inheritance.derived_qualified_name =
                          source.container_qualified_name COLLATE BINARY
                )
          );
        """;

    private static string NormalizeCSharpPropertyReceiverReferencesFullSql =>
        BuildCSharpPropertyReceiverNormalizationSql("1 = 1");

    private const string RefreshReferenceUniqueFamiliesSql = """
        DELETE FROM temp.reference_unique_symbol_families;

        INSERT INTO temp.reference_unique_symbol_families(lang, name_folded, family_key)
        SELECT target_file.lang,
               s.name_folded,
               MIN(target_file.path || char(31) ||
                   COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                   COALESCE(s.name, '')) AS family_key
        FROM symbols AS s
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE s.name_folded IS NOT NULL
          AND target_file.lang <> 'ambiguous_m'
        GROUP BY target_file.lang, s.name_folded
        HAVING COUNT(DISTINCT target_file.path || char(31) ||
                              COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                              COALESCE(s.name, '')) = 1;

        -- An ambiguous .m caller can bind to either dialect, so uniqueness must hold
        -- across the MATLAB/Objective-C union rather than within either language alone.
        -- ambiguous .m の呼出し先は両方の方言になり得るため、一意性は各言語内ではなく
        -- MATLAB/Objective-C の和集合全体で成立させる。
        INSERT INTO temp.reference_unique_symbol_families(lang, name_folded, family_key)
        SELECT 'ambiguous_m',
               s.name_folded,
               MIN(target_file.path || char(31) ||
                   COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                   COALESCE(s.name, '')) AS family_key
        FROM symbols AS s
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE s.name_folded IS NOT NULL
          AND target_file.lang IN ('matlab', 'objc')
        GROUP BY s.name_folded
        HAVING COUNT(DISTINCT target_file.path || char(31) ||
                              COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                              COALESCE(s.name, '')) = 1;
        """;

    private static string RefreshReferenceCandidatesSql => $"""
        DELETE FROM symbol_reference_candidates;

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 0
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS s ON s.name_folded = r.symbol_name_folded
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE source_file.lang = 'csharp'
          AND target_file.lang = 'csharp'
          AND r.reference_kind = 'reference'
          AND s.name = r.symbol_name COLLATE BINARY
          AND r.target_qualifier =
              char(31) || 'property_receiver:' || s.container_qualified_name
                  COLLATE BINARY
          AND s.kind = 'property';

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 0
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE (
              (source_file.lang = target_file.lang
               AND (source_file.lang <> 'ambiguous_m' OR source_file.id = target_file.id))
              OR (source_file.lang = 'ambiguous_m' AND target_file.lang IN ('matlab', 'objc'))
          )
          AND (source_file.lang <> 'dependency_lock' OR s.file_id = r.file_id)
          AND {CSharpTypeReferenceCandidatePredicateSql}
          AND r.target_qualifier IS NOT NULL
          AND r.target_qualifier NOT LIKE char(31) || 'receiver:%'
          AND (
              s.container_name = r.target_qualifier COLLATE NOCASE
              OR s.container_qualified_name = r.target_qualifier COLLATE NOCASE
              OR s.container_qualified_name LIKE '%.' || r.target_qualifier COLLATE NOCASE
              OR (
                  source_file.lang IN (
                      'ada',
                      'ambiguous_m',
                      'cython',
                      'd',
                      'julia',
                      'matlab',
                      'nim',
                      'objc'
                  )
                  AND COALESCE(s.container_name, '') = ''
                  AND COALESCE(s.container_qualified_name, '') = ''
                  AND EXISTS (
                      SELECT 1
                      FROM symbols AS target_scope
                      WHERE target_scope.file_id = s.file_id
                        AND target_scope.kind IN ('namespace', 'module', 'package')
                        AND (
                            target_scope.name = r.target_qualifier COLLATE NOCASE
                            OR target_scope.container_qualified_name = r.target_qualifier COLLATE NOCASE
                            OR substr(
                                   target_scope.name,
                                   1,
                                   length(r.target_qualifier) + 1
                               ) = (r.target_qualifier || '.') COLLATE NOCASE
                        )
                  )
              )
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 0
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS source ON source.id = r.source_symbol_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE source_file.lang = 'csharp'
          AND target_file.lang = 'csharp'
          AND {CSharpTypeReferenceCandidatePredicateSql}
          AND r.target_qualifier LIKE char(31) || 'receiver:%'
          AND source.signature IS NOT NULL
          AND source.signature <> ''
          AND (
              source.signature LIKE '%(' || COALESCE(s.container_qualified_name, s.container_name, '') || ' ' ||
                  substr(r.target_qualifier, length(char(31) || 'receiver:') + 1) || '%'
              OR source.signature LIKE '%, ' || COALESCE(s.container_qualified_name, s.container_name, '') || ' ' ||
                  substr(r.target_qualifier, length(char(31) || 'receiver:') + 1) || '%'
              OR source.signature LIKE '%(' || COALESCE(s.container_name, '') || ' ' ||
                  substr(r.target_qualifier, length(char(31) || 'receiver:') + 1) || '%'
              OR source.signature LIKE '%, ' || COALESCE(s.container_name, '') || ' ' ||
                  substr(r.target_qualifier, length(char(31) || 'receiver:') + 1) || '%'
          )
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 1
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        JOIN symbols AS source ON source.id = r.source_symbol_id
        WHERE (
              (source_file.lang = target_file.lang
               AND (source_file.lang <> 'ambiguous_m' OR source_file.id = target_file.id))
              OR (source_file.lang = 'ambiguous_m' AND target_file.lang IN ('matlab', 'objc'))
          )
          AND {CSharpTypeReferenceCandidatePredicateSql}
          AND r.target_qualifier IS NULL
          AND s.file_id = r.file_id
          AND source.container_name IS NOT NULL
          AND source.container_name <> ''
          AND (
              s.container_name = source.container_name COLLATE NOCASE
              OR s.container_qualified_name = source.container_qualified_name COLLATE NOCASE
          )
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 2
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        JOIN symbols AS source ON source.id = r.source_symbol_id
        WHERE (
              (source_file.lang = target_file.lang
               AND (source_file.lang <> 'ambiguous_m' OR source_file.id = target_file.id))
              OR (source_file.lang = 'ambiguous_m' AND target_file.lang IN ('matlab', 'objc'))
          )
          AND {CSharpTypeReferenceCandidatePredicateSql}
          AND r.target_qualifier IS NULL
          AND (source_file.lang <> 'dependency_lock' OR s.file_id = r.file_id)
          AND source.container_qualified_name IS NOT NULL
          AND source.container_qualified_name <> ''
          AND s.container_qualified_name = source.container_qualified_name COLLATE NOCASE
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 3
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE (
              (source_file.lang = target_file.lang
               AND (source_file.lang <> 'ambiguous_m' OR source_file.id = target_file.id))
              OR (source_file.lang = 'ambiguous_m' AND target_file.lang IN ('matlab', 'objc'))
          )
          AND {CSharpTypeReferenceCandidatePredicateSql}
          AND r.target_qualifier IS NULL
          AND s.file_id = r.file_id
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 4
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        JOIN symbols AS source ON source.id = r.source_symbol_id
        WHERE (
              (source_file.lang = target_file.lang
               AND (source_file.lang <> 'ambiguous_m' OR source_file.id = target_file.id))
              OR (source_file.lang = 'ambiguous_m' AND target_file.lang IN ('matlab', 'objc'))
          )
          AND {CSharpTypeReferenceCandidatePredicateSql}
          AND r.target_qualifier IS NULL
          AND (source_file.lang <> 'dependency_lock' OR s.file_id = r.file_id)
          AND source.container_name IS NOT NULL
          AND source.container_name <> ''
          AND s.container_name = source.container_name COLLATE NOCASE
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, unique_target.symbol_id, 5
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN (
            SELECT MIN(type_symbol.id) AS symbol_id,
                   type_symbol.name_folded,
                   type_symbol.name,
                   csharp_definition_type_arity(
                       type_symbol.signature,
                       type_symbol.name,
                       type_symbol.kind) AS type_arity
            FROM symbols AS type_symbol
            JOIN files AS target_file ON target_file.id = type_symbol.file_id
            WHERE target_file.lang = 'csharp'
              AND type_symbol.name_folded IS NOT NULL
              AND type_symbol.kind IN ('class', 'struct', 'record', 'interface', 'enum', 'delegate')
            GROUP BY type_symbol.name_folded, type_symbol.name, type_arity
            HAVING type_arity IS NOT NULL
               AND COUNT(DISTINCT target_file.path || char(31) ||
                                  COALESCE(
                                      type_symbol.container_qualified_name,
                                      type_symbol.container_name,
                                      '') || char(31) ||
                                  COALESCE(type_symbol.name, '')) = 1
        ) AS unique_target ON unique_target.name_folded = r.symbol_name_folded
                          AND unique_target.name = r.symbol_name COLLATE BINARY
        WHERE source_file.lang = 'csharp'
          AND r.target_qualifier IS NULL
          AND r.reference_kind = 'type_reference'
          AND (
              csharp_reference_type_arity(
                  COALESCE(
                      r.context,
                      (SELECT reference_line.context
                       FROM reference_lines AS reference_line
                       WHERE reference_line.id = r.reference_line_id)),
                  r.symbol_name,
                  r.column_number) IS NULL
              OR unique_target.type_arity
                 = csharp_reference_type_arity(
                     COALESCE(
                         r.context,
                         (SELECT reference_line.context
                          FROM reference_lines AS reference_line
                          WHERE reference_line.id = r.reference_line_id)),
                     r.symbol_name,
                     r.column_number)
          )
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, target.id, 5
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN temp.reference_unique_symbol_families AS unique_family
          ON unique_family.lang = source_file.lang
         AND unique_family.name_folded = r.symbol_name_folded
        JOIN symbols AS target ON target.name_folded = unique_family.name_folded
        JOIN files AS target_file
         ON target_file.id = target.file_id
         AND (
             (
                 unique_family.lang <> 'ambiguous_m'
                 AND target_file.lang = unique_family.lang
             )
             OR (
                 unique_family.lang = 'ambiguous_m'
                 AND target_file.lang IN ('matlab', 'objc')
             )
         )
         AND target_file.path || char(31) ||
             COALESCE(target.container_qualified_name, target.container_name, '') || char(31) ||
             COALESCE(target.name, '') = unique_family.family_key
        WHERE source_file.lang <> 'csharp'
          AND (
              r.target_qualifier IS NULL
              OR source_file.lang IN (
                  'ada',
                  'ambiguous_m',
                  'cython',
                  'd',
                  'julia',
                  'matlab',
                  'nim',
                  'objc'
              )
          )
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          )
          AND (source_file.lang <> 'dependency_lock' OR target.file_id = r.file_id);

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, target.id, 5
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN temp.reference_unique_symbol_families AS unique_family
          ON unique_family.lang = 'csharp'
         AND unique_family.name_folded = r.symbol_name_folded
        JOIN symbols AS target ON target.name_folded = unique_family.name_folded
        JOIN files AS target_file
          ON target_file.id = target.file_id
         AND target_file.lang = 'csharp'
         AND target_file.path || char(31) ||
             COALESCE(target.container_qualified_name, target.container_name, '') || char(31) ||
             COALESCE(target.name, '') = unique_family.family_key
        WHERE source_file.lang = 'csharp'
          AND r.target_qualifier IS NULL
          AND r.reference_kind NOT IN ('instantiate', 'type_reference')
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, target.id, 5
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN temp.reference_unique_symbol_families AS unique_family
          ON unique_family.lang = 'csharp'
         AND unique_family.name_folded = r.symbol_name_folded || 'attribute'
        JOIN symbols AS target ON target.name_folded = unique_family.name_folded
        JOIN files AS target_file
          ON target_file.id = target.file_id
         AND target_file.lang = 'csharp'
         AND target_file.path || char(31) ||
             COALESCE(target.container_qualified_name, target.container_name, '') || char(31) ||
             COALESCE(target.name, '') = unique_family.family_key
        WHERE source_file.lang = 'csharp'
          AND r.target_qualifier IS NULL
          AND r.reference_kind = 'attribute'
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
                AND existing.scope_rank < 5
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, unique_target.symbol_id, 5
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN (
            SELECT MIN(s.id) AS symbol_id, s.name_folded
            FROM symbols AS s
            JOIN files AS target_file ON target_file.id = s.file_id
            WHERE target_file.lang = 'csharp'
              AND s.name_folded IS NOT NULL
              AND s.kind IN ('class', 'struct', 'record')
            GROUP BY s.name_folded
            HAVING COUNT(*) = 1
        ) AS unique_target ON unique_target.name_folded = r.symbol_name_folded
        WHERE source_file.lang = 'csharp'
          AND r.target_qualifier IS NULL
          AND r.reference_kind = 'instantiate'
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        """;

    private const string ReferenceResolutionValueSql = """
        (
            SELECT CASE WHEN candidate_count = 1 THEN minimum_symbol_id END,
                   CASE WHEN target_family_count = 1 THEN minimum_target_key END,
                   candidate_count,
                   CASE
                       WHEN candidate_count = 0 THEN 'unresolved'
                       WHEN candidate_count = 1 THEN 'resolved'
                       WHEN target_family_count = 1 THEN 'resolved_group'
                       ELSE 'ambiguous'
                   END
            FROM (
                SELECT COUNT(*) AS candidate_count,
                       MIN(c.symbol_id) AS minimum_symbol_id,
                       COUNT(DISTINCT target_file.lang || char(31) || target_file.path || char(31) ||
                                              COALESCE(target.container_qualified_name, target.container_name, '') || char(31) ||
                                              COALESCE(target.name, '')) AS target_family_count,
                       MIN(target_file.lang || char(31) || target_file.path || char(31) ||
                           COALESCE(target.container_qualified_name, target.container_name, '') || char(31) ||
                           COALESCE(target.name, '')) AS minimum_target_key
                    FROM symbol_reference_candidates AS c
                    JOIN symbols AS target ON target.id = c.symbol_id
                    JOIN files AS target_file ON target_file.id = target.file_id
                    WHERE c.reference_id = r.id
            ) AS resolution
        )
        """;

    private const string SelfReferenceValueSql = """
        CASE
            WHEN source_symbol_id IS NOT NULL
             AND target_symbol_id IS NOT NULL
             AND source_symbol_id = target_symbol_id THEN 1
            ELSE 0
        END
        """;

    private static readonly string RefreshReferenceResolutionFullSql = $"""
        UPDATE symbol_references AS r
        SET (target_symbol_id, target_symbol_key, resolution_candidate_count, resolution_state) = {ReferenceResolutionValueSql};

        UPDATE symbol_references
        SET is_self_reference = {SelfReferenceValueSql};
        """;

    private static readonly string RefreshReferenceResolutionDifferentialSql = $"""
        UPDATE symbol_references AS r
        SET (target_symbol_id, target_symbol_key, resolution_candidate_count, resolution_state) = {ReferenceResolutionValueSql}
        -- A row-value IS NOT comparison preserves NULL semantics while avoiding four
        -- index-maintaining writes for every already-current reference.
        -- row-value IS NOTでNULL semanticsを保ち、最新referenceへの4列再書込みを避ける。
        WHERE (r.target_symbol_id, r.target_symbol_key, r.resolution_candidate_count, r.resolution_state)
              IS NOT {ReferenceResolutionValueSql};

        UPDATE symbol_references
        SET is_self_reference = {SelfReferenceValueSql}
        WHERE is_self_reference IS NOT ({SelfReferenceValueSql});
        """;

    private static readonly string RefreshMutualRecursionFlagsSql = $"""
        UPDATE symbol_references AS r
        SET is_mutual_recursion = {MutualRecursionValueSql}
        -- IS NOT is null-safe and also normalizes legacy non-boolean values.
        -- IS NOT により NULL と legacy の非boolean値も安全に正規化する。
        WHERE r.is_mutual_recursion IS NOT ({MutualRecursionValueSql})
        """;

    internal static void RebuildRetainedReferenceGraph(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            CreateReferenceUniqueFamiliesSql + ";\n" +
            RefreshReferenceSourceSymbolsFullSql + ";\n" +
            NormalizeCSharpPropertyReceiverReferencesFullSql + "\n" +
            RefreshReferenceUniqueFamiliesSql + "\n" +
            RefreshReferenceCandidatesSql + "\n" +
            RefreshReferenceResolutionFullSql + "\n" +
            RefreshMutualRecursionFlagsSql;
        using var cancellationRegistration = cancellationToken.Register(command.Cancel);
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Insert indexed references in batches.
    /// インデックス済み参照をバッチ挿入する。
    /// </summary>
    public void InsertReferences(IReadOnlyList<ReferenceRecord> references, bool refreshMutualRecursionFlags = true)
        => InsertReferences(references, refreshMutualRecursionFlags, CancellationToken.None);

    public void InsertReferences(IReadOnlyList<ReferenceRecord> references, CancellationToken cancellationToken)
        => InsertReferences(references, refreshMutualRecursionFlags: true, cancellationToken);

    public void InsertReferences(IReadOnlyList<ReferenceRecord> references, bool refreshMutualRecursionFlags, CancellationToken cancellationToken)
        => InsertReferencesCore(
            references,
            refreshMutualRecursionFlags,
            cancellationToken,
            referenceLinesAreNew: false,
            batchesAreAtomicInCaller: false);

    public void InsertReferencesForNewFiles(IReadOnlyList<ReferenceRecord> references, bool refreshMutualRecursionFlags, CancellationToken cancellationToken)
        => InsertReferencesCore(
            references,
            refreshMutualRecursionFlags,
            cancellationToken,
            referenceLinesAreNew: true,
            batchesAreAtomicInCaller: false);

    /// <summary>
    /// Insert references while explicitly delegating all-batch atomicity to an active
    /// file transaction owned by this writer and execution context.
    /// このwriterと実行contextが所有するfile transactionへ全batchの原子性を明示委譲して参照を挿入する。
    /// </summary>
    internal void InsertReferencesInAtomicFileScope(
        IReadOnlyList<ReferenceRecord> references,
        CancellationToken cancellationToken)
        => InsertReferencesInAtomicFileScope(
            references,
            refreshMutualRecursionFlags: true,
            cancellationToken);

    internal void InsertReferencesInAtomicFileScope(
        IReadOnlyList<ReferenceRecord> references,
        bool refreshMutualRecursionFlags,
        CancellationToken cancellationToken)
        => InsertReferencesInAtomicFileScopeCore(
            references,
            refreshMutualRecursionFlags,
            cancellationToken,
            referenceLinesAreNew: false,
            operation: nameof(InsertReferencesInAtomicFileScope));

    internal void InsertReferencesForNewFilesInAtomicFileScope(
        IReadOnlyList<ReferenceRecord> references,
        bool refreshMutualRecursionFlags,
        CancellationToken cancellationToken)
        => InsertReferencesInAtomicFileScopeCore(
            references,
            refreshMutualRecursionFlags,
            cancellationToken,
            referenceLinesAreNew: true,
            operation: nameof(InsertReferencesForNewFilesInAtomicFileScope));

    private void InsertReferencesInAtomicFileScopeCore(
        IReadOnlyList<ReferenceRecord> references,
        bool refreshMutualRecursionFlags,
        CancellationToken cancellationToken,
        bool referenceLinesAreNew,
        string operation)
    {
        RequireCallerOwnedTransaction(operation);
        AtomicFileReferenceInsertForTesting?.Invoke(referenceLinesAreNew);
        InsertReferencesCore(
            references,
            refreshMutualRecursionFlags,
            cancellationToken,
            referenceLinesAreNew,
            batchesAreAtomicInCaller: true);
    }

    private void RequireCallerOwnedTransaction(string operation)
    {
        lock (_transactionStateLock)
        {
            if (_transactionDepth > 0
                && _activeTransaction != null
                && _transactionOwnerThreadId == Environment.CurrentManagedThreadId
                && _transactionOwnerToken != Guid.Empty
                && _currentTransactionGateToken.Value == _transactionOwnerToken)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"{operation} requires an active transaction owned by this DbWriter on the current execution context.");
    }

    private void InsertReferencesCore(
        IReadOnlyList<ReferenceRecord> references,
        bool refreshMutualRecursionFlags,
        CancellationToken cancellationToken,
        bool referenceLinesAreNew,
        bool batchesAreAtomicInCaller)
    {
        if (references.Count == 0) return;
        TrackReferenceGraphInsertedReferences(references);
        InvalidateReferenceIdentityContractForMutation();

        // If a chunk commits but aggregate refresh is cancelled, readers must fall back to
        // raw references until InitializeSchema performs a complete backfill.
        // aggregate refresh 前に中断した場合は trust bit を残さず raw fallback に降格する。
        var aggregateWasReady = ClearHotspotReferenceAggregateReady();

        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 14);
        var foldedNameCache = CreateFoldedNameCache(
            Math.Min(references.Count, rowsPerStatement),
            namesPerRow: 2);
        var newReferenceLineIds = referenceLinesAreNew
            ? new Dictionary<(long FileId, int Line, string Context), long>()
            : null;
        int referenceBatchCount = GetReferenceBatchCount(references.Count, rowsPerStatement);
        if (batchesAreAtomicInCaller)
        {
            InsertAtomicReferenceBatches(
                references,
                referenceLinesAreNew,
                newReferenceLineIds,
                foldedNameCache,
                rowsPerStatement,
                referenceBatchCount,
                cancellationToken);
        }
        else
        {
            // Public APIs always retain the #1518 chunk transaction/SAVEPOINT contract.
            // The explicit atomic-file APIs alone aggregate reference-line work under
            // their required caller-owned file transaction.
            // public APIは#1518のchunk transaction/SAVEPOINT契約を常に維持する。
            // 明示atomic-file APIだけが必須の呼出元file transaction配下で参照行処理を集約する。
            for (int batchIndex = 0; batchIndex < referenceBatchCount; batchIndex++)
            {
                int start = batchIndex * rowsPerStatement;
                int end = Math.Min(start + rowsPerStatement, references.Count);
                CheckBatchCancellationAndReportProgress(
                    "insert_references",
                    start,
                    references.Count,
                    cancellationToken);
                using var transaction = BeginReferenceBatchTransaction(cancellationToken);
                var referenceLineIds = MaterializeReferenceLines(
                    references,
                    start,
                    end,
                    referenceLinesAreNew,
                    newReferenceLineIds,
                    cancellationToken);
                InsertReferenceBatch(references, start, end, referenceLineIds, foldedNameCache);
                transaction.Commit();
            }
        }

        CheckBatchCancellationAndReportProgress("insert_references", references.Count, references.Count, cancellationToken);
        RefreshHotspotReferenceCounts(references, cancellationToken);
        RestoreHotspotReferenceAggregateReady(aggregateWasReady);
        if (refreshMutualRecursionFlags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshMutualRecursionFlags(cancellationToken);
        }
    }

    private void InsertAtomicReferenceBatches(
        IReadOnlyList<ReferenceRecord> references,
        bool referenceLinesAreNew,
        Dictionary<(long FileId, int Line, string Context), long>? newReferenceLineIds,
        Dictionary<string, string?> foldedNameCache,
        int rowsPerStatement,
        int referenceBatchCount,
        CancellationToken cancellationToken)
    {
        for (int windowStartBatch = 0; windowStartBatch < referenceBatchCount;)
        {
            int windowStart = windowStartBatch * rowsPerStatement;
            CheckBatchCancellationAndReportProgress(
                "insert_references",
                windowStart,
                references.Count,
                cancellationToken);
            int windowEndBatch = GetAtomicReferenceLineWindowEndBatch(
                references,
                windowStartBatch,
                referenceBatchCount,
                rowsPerStatement);
            int windowEnd = Math.Min(windowEndBatch * rowsPerStatement, references.Count);
            var referenceLineIds = MaterializeReferenceLines(
                references,
                windowStart,
                windowEnd,
                referenceLinesAreNew,
                newReferenceLineIds,
                cancellationToken);

            for (int batchIndex = windowStartBatch; batchIndex < windowEndBatch; batchIndex++)
            {
                int start = batchIndex * rowsPerStatement;
                if (batchIndex != windowStartBatch)
                {
                    CheckBatchCancellationAndReportProgress(
                        "insert_references",
                        start,
                        references.Count,
                        cancellationToken);
                }
                int end = Math.Min(start + rowsPerStatement, references.Count);
                InsertReferenceBatch(references, start, end, referenceLineIds, foldedNameCache);
            }

            windowStartBatch = windowEndBatch;
        }
    }

    private static int GetAtomicReferenceLineWindowEndBatch(
        IReadOnlyList<ReferenceRecord> references,
        int windowStartBatch,
        int referenceBatchCount,
        int rowsPerStatement)
    {
        int maxReferenceLines = GetRowsPerInsertStatement(columnCount: 3);
        var windowKeys = new HashSet<(long FileId, int Line, string Context)>(maxReferenceLines);
        int windowEndBatch = windowStartBatch;
        while (windowEndBatch < referenceBatchCount
               && windowEndBatch - windowStartBatch < MaxReferenceLineWindowBatchCount)
        {
            int batchStart = windowEndBatch * rowsPerStatement;
            int batchEnd = Math.Min(batchStart + rowsPerStatement, references.Count);
            for (int index = batchStart; index < batchEnd; index++)
            {
                var reference = references[index];
                var key = (reference.FileId, reference.Line, reference.Context);
                windowKeys.Add(key);
            }

            if (windowKeys.Count > maxReferenceLines && windowEndBatch > windowStartBatch)
                break;

            windowEndBatch++;
        }

        return windowEndBatch;
    }

    private Dictionary<(long FileId, int Line, string Context), long> MaterializeReferenceLines(
        IReadOnlyList<ReferenceRecord> references,
        int start,
        int end,
        bool referenceLinesAreNew,
        Dictionary<(long FileId, int Line, string Context), long>? newReferenceLineIds,
        CancellationToken cancellationToken)
        => referenceLinesAreNew
            ? InsertNewReferenceLines(references, start, end, newReferenceLineIds!, cancellationToken)
            : UpsertReferenceLines(references, start, end, cancellationToken);

    private void InsertReferenceBatch(
        IReadOnlyList<ReferenceRecord> references,
        int start,
        int end,
        Dictionary<(long FileId, int Line, string Context), long> referenceLineIds,
        Dictionary<string, string?> foldedNameCache)
    {
        var rowsInBatch = end - start;
        var sql = ReferenceInsertSqlCache.GetOrAdd(rowsInBatch, static count => BuildReferenceInsertSql(count));
        var cmd = RentCommand(sql, c => AddReferenceInsertParameters(c, rowsInBatch));
        try
        {
            var parameterIndex = 0;
            (long FileId, int Line, string Context)? previousReferenceLineKey = null;
            var previousReferenceLineId = 0L;
            for (int index = start; index < end; index++)
            {
                var reference = references[index];
                ValidateReferenceKinds(reference);
                var referenceLineKey = (reference.FileId, reference.Line, reference.Context);
                if (previousReferenceLineKey is not { } previousKey
                    || !ReferenceLineKeysEqual(previousKey, referenceLineKey))
                {
                    previousReferenceLineId = referenceLineIds[referenceLineKey];
                    previousReferenceLineKey = referenceLineKey;
                }

                cmd.Parameters[parameterIndex++].Value = reference.FileId;
                cmd.Parameters[parameterIndex++].Value = reference.SymbolName;
                cmd.Parameters[parameterIndex++].Value = reference.ReferenceKind;
                cmd.Parameters[parameterIndex++].Value = reference.Line;
                cmd.Parameters[parameterIndex++].Value = reference.Column;
                cmd.Parameters[parameterIndex++].Value = DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = previousReferenceLineId;
                cmd.Parameters[parameterIndex++].Value = (object?)reference.ContainerKind ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = (object?)reference.ContainerName ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = FoldedNameDbValue(
                    reference.SymbolName,
                    reference.IdentitySymbolNameFolded,
                    foldedNameCache);
                cmd.Parameters[parameterIndex++].Value = FoldedNameDbValue(
                    reference.ContainerName,
                    reference.IdentityContainerNameFolded,
                    foldedNameCache);
                cmd.Parameters[parameterIndex++].Value = reference.IsSelfReference ? 1 : 0;
                cmd.Parameters[parameterIndex++].Value = reference.IsMutualRecursion ? 1 : 0;
                cmd.Parameters[parameterIndex++].Value = (object?)ExtractTargetQualifier(reference) ?? DBNull.Value;
            }

            ReportBatchStatementForTesting("insert_references", rowsInBatch, rowsInBatch);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private TransactionScope BeginReferenceBatchTransaction(CancellationToken cancellationToken)
    {
        ReferenceBatchTransactionOpeningForTesting?.Invoke();
        return BeginTransaction(cancellationToken, "insert references");
    }

    private static int GetReferenceBatchCount(int referenceCount, int rowsPerStatement)
    {
        if (referenceCount < 0)
            throw new ArgumentOutOfRangeException(nameof(referenceCount));
        if (rowsPerStatement <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowsPerStatement));

        return referenceCount == 0
            ? 0
            : ((referenceCount - 1) / rowsPerStatement) + 1;
    }

    internal static long CountReferenceBatchTransactionScopesForTesting(
        IReadOnlyList<int> referenceCountsByFile,
        bool atomicFileScope)
    {
        ArgumentNullException.ThrowIfNull(referenceCountsByFile);
        if (atomicFileScope)
            return 0;

        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 14);
        long transactionCount = 0;
        foreach (var referenceCount in referenceCountsByFile)
            transactionCount += GetReferenceBatchCount(referenceCount, rowsPerStatement);
        return transactionCount;
    }

    private void RefreshHotspotReferenceCounts(
        IReadOnlyList<ReferenceRecord> references,
        CancellationToken cancellationToken)
    {
        var fileIds = new HashSet<long>();
        foreach (var reference in references)
            fileIds.Add(reference.FileId);

        RefreshHotspotReferenceCounts(fileIds, cancellationToken);
    }

    private void RefreshHotspotReferenceCounts(
        IReadOnlyCollection<long> fileIds,
        CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
            return;
        if (TryDeferHotspotReferenceRefresh(fileIds, requireDirtyFileIds: true))
            return;

        using var transaction = BeginTransaction(cancellationToken, "refresh hotspot reference counts");
        var refreshCheckpoint = HotspotAggregateRefreshExecutingForTesting;
        if (refreshCheckpoint != null)
        {
            var invoked = false;
            _conn.CreateFunction("hotspot_refresh_test_checkpoint", () =>
            {
                if (!invoked)
                {
                    invoked = true;
                    refreshCheckpoint();
                }
                return 0;
            });
        }
        var cmd = RentCommand(
            HotspotReferenceAggregateSql.BuildRefreshSql(singleFile: true, includeTestCheckpoint: refreshCheckpoint != null),
            static command => command.Parameters.Add("@file_id", SqliteType.Integer));
        try
        {
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            var completed = 0;
            foreach (var fileId in fileIds)
            {
                CheckBatchCancellationAndReportProgress(
                    "refresh_hotspot_reference_counts",
                    completed,
                    fileIds.Count,
                    cancellationToken);
                cmd.Parameters["@file_id"].Value = fileId;
                try
                {
                    HotspotAggregateRefreshStatementExecutingForTesting?.Invoke();
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
                {
                    throw new OperationCanceledException(
                        "Hotspot reference aggregate refresh was interrupted.",
                        ex,
                        cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                completed++;
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        transaction.Commit();
    }

    private Dictionary<(long FileId, int Line, string Context), long> UpsertReferenceLines(IReadOnlyList<ReferenceRecord> references, int start, int end, CancellationToken cancellationToken)
    {
        var batchCount = end - start;
        var referenceLineKeys = new HashSet<(long FileId, int Line, string Context)>(batchCount);
        (long FileId, int Line, string Context)? previousReferenceLineKey = null;
        for (int i = start; i < end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = references[i];
            var referenceLineKey = (reference.FileId, reference.Line, reference.Context);
            if (previousReferenceLineKey is not { } previousKey
                || !ReferenceLineKeysEqual(previousKey, referenceLineKey))
            {
                referenceLineKeys.Add(referenceLineKey);
                previousReferenceLineKey = referenceLineKey;
            }
        }

        var rows = referenceLineKeys.ToArray();
        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 3);
        for (int i = 0; i < rows.Length; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress("upsert_reference_lines", i, rows.Length, cancellationToken);
            int batchEnd = Math.Min(i + rowsPerStatement, rows.Length);
            var statementRowCount = batchEnd - i;
            var sql = ReferenceLineUpsertSqlCache.GetOrAdd(statementRowCount, static count => BuildReferenceLineUpsertSql(count));
            var cmd = RentCommand(sql, c => AddReferenceLineParameters(c, statementRowCount));
            try
            {
                AssignReferenceLineParameterValues(cmd, rows, i, batchEnd);
                ReportBatchStatementForTesting("upsert_reference_lines", statementRowCount, statementRowCount);
                cmd.ExecuteNonQuery();
            }
            finally
            {
                ReleaseCommand(cmd);
            }
        }

        var lineIds = new Dictionary<(long FileId, int Line, string Context), long>(rows.Length);
        int keysPerStatement = GetRowsPerInsertStatement(columnCount: 3);
        for (int i = 0; i < rows.Length; i += keysPerStatement)
        {
            CheckBatchCancellationAndReportProgress("lookup_reference_lines", i, rows.Length, cancellationToken);
            int keyEnd = Math.Min(i + keysPerStatement, rows.Length);
            var statementRowCount = keyEnd - i;
            var sql = ReferenceLineLookupSqlCache.GetOrAdd(statementRowCount, static count => BuildReferenceLineLookupSql(count));
            var cmd = RentCommand(
                sql,
                c => AddReferenceLineParameters(c, statementRowCount));
            try
            {
                AssignReferenceLineParameterValues(cmd, rows, i, keyEnd);
                ReportBatchStatementForTesting("lookup_reference_lines", statementRowCount, statementRowCount);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var fileId = reader.GetInt64(1);
                    var line = reader.GetInt32(2);
                    var context = reader.GetString(3);
                    var key = (fileId, line, context);
                    lineIds[key] = id;
                }
            }
            finally
            {
                ReleaseCommand(cmd);
            }
        }

        return lineIds;
    }

    private Dictionary<(long FileId, int Line, string Context), long> InsertNewReferenceLines(
        IReadOnlyList<ReferenceRecord> references,
        int start,
        int end,
        Dictionary<(long FileId, int Line, string Context), long> knownLineIds,
        CancellationToken cancellationToken)
    {
        var batchCount = end - start;
        var referenceLineKeys = new HashSet<(long FileId, int Line, string Context)>(batchCount);
        (long FileId, int Line, string Context)? previousReferenceLineKey = null;
        for (int i = start; i < end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = references[i];
            var referenceLineKey = (reference.FileId, reference.Line, reference.Context);
            if (previousReferenceLineKey is not { } previousKey
                || !ReferenceLineKeysEqual(previousKey, referenceLineKey))
            {
                referenceLineKeys.Add(referenceLineKey);
                previousReferenceLineKey = referenceLineKey;
            }
        }

        var lineIds = new Dictionary<(long FileId, int Line, string Context), long>(referenceLineKeys.Count);
        var rows = new List<(long FileId, int Line, string Context)>(referenceLineKeys.Count);
        foreach (var key in referenceLineKeys)
        {
            if (knownLineIds.TryGetValue(key, out var knownId))
                lineIds[key] = knownId;
            else
                rows.Add(key);
        }

        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 3);
        for (int i = 0; i < rows.Count; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress("insert_reference_lines", i, rows.Count, cancellationToken);
            int batchEnd = Math.Min(i + rowsPerStatement, rows.Count);
            var statementRowCount = batchEnd - i;
            var sql = ReferenceLineInsertSqlCache.GetOrAdd(statementRowCount, static count => BuildReferenceLineInsertSql(count));
            var cmd = RentCommand(sql, c => AddReferenceLineParameters(c, statementRowCount));
            try
            {
                AssignReferenceLineParameterValues(cmd, rows, i, batchEnd);
                ReportBatchStatementForTesting("insert_reference_lines", statementRowCount, statementRowCount);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var fileId = reader.GetInt64(1);
                    var line = reader.GetInt32(2);
                    var context = reader.GetString(3);
                    var key = (fileId, line, context);
                    lineIds[key] = id;
                    knownLineIds[key] = id;
                }
            }
            finally
            {
                ReleaseCommand(cmd);
            }
        }

        return lineIds;
    }

    private static bool ReferenceLineKeysEqual(
        (long FileId, int Line, string Context) left,
        (long FileId, int Line, string Context) right)
        => left.FileId == right.FileId
           && left.Line == right.Line
           && string.Equals(left.Context, right.Context, StringComparison.Ordinal);

    private bool HasPersistedReferenceResolutionState(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string sql = "SELECT EXISTS(SELECT 1 FROM symbol_references WHERE resolution_state IS NOT NULL LIMIT 1)";
        var command = RentCommand(sql, static _ => { });
        try
        {
            return Convert.ToInt64(command.ExecuteScalar()) != 0;
        }
        finally
        {
            ReleaseCommand(command);
        }
    }

    internal void RefreshMutualRecursionFlags(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MutualRecursionRefreshForTesting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var graphScope = _referenceGraphRefreshScope;
        using var transaction = BeginTransaction(cancellationToken, "refresh reference identities");
        if (graphScope != null)
            graphScope.IsCompleting = true;
        SqliteCommand? createUniqueFamiliesCommand = null;
        SqliteCommand? refreshCommand = null;
        try
        {
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            createUniqueFamiliesCommand = RentCommand(CreateReferenceUniqueFamiliesSql, static _ => { });
            createUniqueFamiliesCommand.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            var refreshPlan = graphScope == null
                ? new ReferenceGraphRefreshPlan(true, 0, 0, 0, 0)
                : BuildReferenceGraphRefreshPlan(graphScope, cancellationToken);
            ReferenceGraphRefreshScopeForTesting?.Invoke(new ReferenceGraphRefreshScopeStats(
                refreshPlan.UseFullRefresh,
                refreshPlan.DirtyFileCount,
                refreshPlan.DirtyNameCount,
                refreshPlan.DirtyReferenceCount,
                refreshPlan.TotalReferenceCount));
            cancellationToken.ThrowIfCancellationRequested();
            // A fresh graph evaluates each correlated identity expression once. Once any
            // persisted resolution exists, differential SQL avoids rewriting the stable
            // majority while newly inserted or invalidated rows still repair normally.
            // fresh graphでは相関identity式を1回だけ評価し、既存resolutionがあれば
            // differential SQLで安定多数の再書込みを避けつつ新規/無効rowを修復する。
            string refreshIdentitySql;
            if (refreshPlan.UseFullRefresh)
            {
                refreshIdentitySql = HasPersistedReferenceResolutionState(cancellationToken)
                    ? RefreshReferenceSourceSymbolsDifferentialSql + ";\n" +
                      NormalizeCSharpPropertyReceiverReferencesFullSql + "\n" +
                      RefreshReferenceUniqueFamiliesSql + "\n" +
                      RefreshReferenceCandidatesSql + "\n" +
                      RefreshReferenceResolutionDifferentialSql + "\n"
                    : RefreshReferenceSourceSymbolsFullSql + ";\n" +
                      NormalizeCSharpPropertyReceiverReferencesFullSql + "\n" +
                      RefreshReferenceUniqueFamiliesSql + "\n" +
                      RefreshReferenceCandidatesSql + "\n" +
                      RefreshReferenceResolutionFullSql + "\n";
            }
            else
            {
                DeleteRemovedReferenceCandidates(cancellationToken);
                refreshIdentitySql = RefreshScopedReferenceSourceSymbolsSql + "\n" +
                                     NormalizeCSharpPropertyReceiverReferencesScopedSql + "\n" +
                                     RefreshScopedReferenceUniqueFamiliesSql + "\n" +
                                     RefreshScopedReferenceCandidatesSql + "\n" +
                                     RefreshScopedReferenceResolutionSql + "\n" +
                                     ExpandReferenceGraphNewMutualScopeSql + "\n";
            }
            refreshCommand = RentCommand(
                refreshIdentitySql + (refreshPlan.UseFullRefresh
                    ? RefreshMutualRecursionFlagsSql
                    : RefreshScopedMutualRecursionFlagsSql),
                static _ => { });
            // Stamp inside the same transaction, but before the graph refresh so the
            // public SQLite changes() result continues to describe recursion updates.
            // 同一トランザクション内で先に marker を設定し、公開 changes() は再帰更新件数を維持する。
            MarkReferenceIdentityContractReady();
            cancellationToken.ThrowIfCancellationRequested();
            refreshCommand.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            if (graphScope != null)
                ExecuteReferenceGraphScopeSql(ClearReferenceGraphDirtyScopeSql, cancellationToken);
            transaction.Commit();
            graphScope?.MarkRefreshCompleted();
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("Mutual recursion refresh was interrupted.", ex, cancellationToken);
        }
        finally
        {
            if (refreshCommand != null)
                ReleaseCommand(refreshCommand);
            if (createUniqueFamiliesCommand != null)
                ReleaseCommand(createUniqueFamiliesCommand);
            if (graphScope != null)
                graphScope.IsCompleting = false;
        }
    }

    private static string? ExtractTargetQualifier(ReferenceRecord reference)
    {
        if (reference.SuppressInferredTargetQualifier)
            return null;

        if (!string.IsNullOrWhiteSpace(reference.TargetQualifier))
        {
            var explicitQualifier = reference.TargetQualifier.Trim();
            return explicitQualifier.StartsWith("global::", StringComparison.Ordinal)
                ? explicitQualifier["global::".Length..]
                : explicitQualifier;
        }
        if (string.IsNullOrWhiteSpace(reference.Context) || string.IsNullOrWhiteSpace(reference.SymbolName))
            return null;

        var context = reference.Context;
        var occurrence = -1;
        var bestDistance = int.MaxValue;
        for (var searchAt = 0; searchAt <= context.Length - reference.SymbolName.Length;)
        {
            var found = context.IndexOf(reference.SymbolName, searchAt, StringComparison.Ordinal);
            if (found < 0)
                break;
            var distance = Math.Abs((found + 1) - reference.Column);
            if (distance < bestDistance)
            {
                occurrence = found;
                bestDistance = distance;
            }
            searchAt = found + Math.Max(1, reference.SymbolName.Length);
        }

        if (occurrence <= 0)
            return null;
        var dot = occurrence - 1;
        while (dot >= 0 && char.IsWhiteSpace(context[dot]))
            dot--;
        if (dot < 0 || context[dot] != '.')
            return null;
        var end = dot - 1;
        while (end >= 0 && char.IsWhiteSpace(context[end]))
            end--;
        var start = end;
        while (start >= 0 && (char.IsLetterOrDigit(context[start]) || context[start] is '_' or '@'))
            start--;
        var qualifier = context[(start + 1)..(end + 1)].TrimStart('@');
        if (qualifier.Length == 0)
            return null;
        // `this.Member()` is genuinely unqualified with respect to the current container.
        // Other lowercase receivers (for example `service.Process()`) need a non-null marker
        // so the global fallback stays disabled. The resolver may recover a target container
        // only from an explicit `Type receiver` pair in the enclosing symbol signature; the
        // receiver text alone must not participate in type matching because a variable named
        // `worker` is not evidence for type `Worker`.
        // `this.Member()` は現在の container に対して実質 unqualified として扱える。
        // それ以外の小文字 receiver（例: `service.Process()`）は global fallback を無効化する
        // non-null marker として保持する。enclosing symbol signature に明示的な `Type receiver`
        // がある場合だけ container を復元し、変数 `worker` 自体を型 `Worker` の根拠にはしない。
        if (string.Equals(qualifier, "this", StringComparison.Ordinal))
            return null;
        return char.IsUpper(qualifier[0])
            ? qualifier
            : NonTypeReceiverQualifierPrefix + qualifier;
    }
}
