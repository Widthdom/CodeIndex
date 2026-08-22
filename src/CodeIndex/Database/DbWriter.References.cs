using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    internal static Action? HotspotAggregateRefreshExecutingForTesting { get; set; }
    private const string NonTypeReceiverQualifierPrefix = "\u001freceiver:";
    private const string NonIdentifierReceiverQualifier =
        NonTypeReceiverQualifierPrefix + "\u001fqualified";
    private const int MaxReferenceLineWindowBatchCount = 32;
    private const string ReferenceLowerRankCandidateMatchesTable =
        "reference_lower_rank_candidate_matches";
    private const string ReferenceResolutionSymbolFactsTable =
        "reference_resolution_symbol_facts";

    private const string ReferenceResolutionTargetKeySql = """
        target_file.lang || char(31) || target_file.path || char(31) ||
        COALESCE(target.container_qualified_name, target.container_name, '') || char(31) ||
        COALESCE(target.name, '')
        """;

    private static readonly string CreateReferenceResolutionSymbolFactsTableSql = $"""
        CREATE TEMP TABLE IF NOT EXISTS {ReferenceResolutionSymbolFactsTable} (
            symbol_id  INTEGER NOT NULL PRIMARY KEY,
            target_key TEXT COLLATE BINARY
        ) WITHOUT ROWID
        """;

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
                        FROM symbol_references AS reverse INDEXED BY idx_symbol_refs_resolved_source_target_kind
                        WHERE reverse.source_symbol_id IS NOT NULL
                          AND reverse.target_symbol_id IS NOT NULL
                          AND reverse.source_symbol_id = r.target_symbol_id
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
                                FROM symbol_references AS reverse INDEXED BY idx_symbol_refs_unresolved_mutual_folded
                                WHERE reverse.source_symbol_id IS NULL
                                  AND reverse.target_symbol_id IS NULL
                                  AND reverse.is_self_reference = 0
                                  AND reverse.container_name_folded IS NOT NULL
                                  AND reverse.container_name_folded <> ''
                                  AND reverse.symbol_name_folded IS NOT NULL
                                  AND reverse.symbol_name_folded <> ''
                                  AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
                                  AND reverse.container_name_folded = r.symbol_name_folded
                                  AND reverse.symbol_name_folded = r.container_name_folded
                            )
                        )
                        OR (
                            (r.container_name_folded IS NULL OR r.symbol_name_folded IS NULL)
                            AND EXISTS (
                                SELECT 1
                                FROM symbol_references AS reverse INDEXED BY idx_symbol_refs_container_nocase_kind
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

    private static string BuildReferenceSourceSymbolValueSql(string referenceAlias)
        => $"""
        (
            SELECT s.id
            FROM symbols AS s
            WHERE s.file_id = {referenceAlias}.file_id
              AND {referenceAlias}.container_name IS NOT NULL
              AND {referenceAlias}.container_name <> ''
              AND (s.name_folded = {referenceAlias}.container_name_folded
                   OR s.display_name_folded = {referenceAlias}.container_name_folded
                   OR (s.name_folded IS NULL AND s.name = {referenceAlias}.container_name COLLATE NOCASE))
              AND {referenceAlias}.line BETWEEN COALESCE(s.start_line, s.line) AND COALESCE(s.end_line, s.line)
            ORDER BY (COALESCE(s.end_line, s.line) - COALESCE(s.start_line, s.line)),
                     COALESCE(s.start_line, s.line) DESC,
                     s.id
            LIMIT 1
        )
        """;

    private static readonly string RefreshReferenceSourceSymbolsFullSql = $"""
        UPDATE symbol_references AS r
        SET source_symbol_id = {BuildReferenceSourceSymbolValueSql("r")}
        """;

    private static readonly string RefreshReferenceSourceSymbolsDifferentialSql = $"""
        UPDATE symbol_references AS r
        SET source_symbol_id = {BuildReferenceSourceSymbolValueSql("r")}
        -- IS NOT is null-safe: stable NULL identities must not be rewritten either.
        -- IS NOTはNULL-safeであり、安定したNULL identityも再書込みしない。
        WHERE r.source_symbol_id IS NOT {BuildReferenceSourceSymbolValueSql("r")}
        """;

    private static string? SelectReferenceSourceRefreshSql(
        bool useFreshReferenceResolutionDefaults,
        bool hasPersistedReferenceResolutionState)
        => useFreshReferenceResolutionDefaults
            ? null
            : hasPersistedReferenceResolutionState
                ? RefreshReferenceSourceSymbolsDifferentialSql
                : RefreshReferenceSourceSymbolsFullSql;

    internal static string? SelectReferenceSourceRefreshSqlForTesting(
        bool useFreshReferenceResolutionDefaults,
        bool hasPersistedReferenceResolutionState)
        => SelectReferenceSourceRefreshSql(
            useFreshReferenceResolutionDefaults,
            hasPersistedReferenceResolutionState);

    private static readonly string CreateReferenceUniqueFamiliesSql = $"""
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
        ) WITHOUT ROWID;

        CREATE TEMP TABLE IF NOT EXISTS csharp_reference_facts (
            reference_id                  INTEGER NOT NULL PRIMARY KEY,
            type_arity                    INTEGER,
            argument_count                INTEGER,
            is_member_receiver            INTEGER NOT NULL,
            is_property_receiver_reference INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TEMP TABLE IF NOT EXISTS csharp_property_target_facts (
            name_folded              TEXT NOT NULL,
            name                     TEXT NOT NULL COLLATE BINARY,
            container_qualified_name TEXT NOT NULL COLLATE BINARY,
            symbol_id                INTEGER NOT NULL,
            PRIMARY KEY(name_folded, name, container_qualified_name, symbol_id)
        ) WITHOUT ROWID;

        CREATE TEMP TABLE IF NOT EXISTS csharp_symbol_facts (
            symbol_id                   INTEGER NOT NULL PRIMARY KEY,
            definition_type_arity       INTEGER,
            constructor_parameter_count INTEGER,
            is_value_type               INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TEMP TABLE IF NOT EXISTS csharp_type_identity_facts (
            symbol_id                    INTEGER NOT NULL PRIMARY KEY,
            unprefixed_type_identity     TEXT COLLATE BINARY,
            type_identity                TEXT COLLATE BINARY
        ) WITHOUT ROWID;

        CREATE TEMP TABLE IF NOT EXISTS csharp_constructor_identity_facts (
            symbol_id     INTEGER NOT NULL PRIMARY KEY,
            type_identity TEXT COLLATE BINARY,
            type_arity    INTEGER
        ) WITHOUT ROWID;

        {CreateReferenceResolutionSymbolFactsTableSql};

        CREATE TEMP TABLE IF NOT EXISTS {ReferenceLowerRankCandidateMatchesTable} (
            reference_id INTEGER NOT NULL PRIMARY KEY
        ) WITHOUT ROWID
        """;

    private const string CreateCSharpReferenceFactIndexesSql = """
        CREATE INDEX IF NOT EXISTS temp.idx_csharp_reference_facts_property_receiver
        ON csharp_reference_facts(reference_id)
        WHERE is_property_receiver_reference = 1
        """;

    private static readonly string RefreshReferenceResolutionSymbolFactsFullSql = $"""
        DELETE FROM temp.{ReferenceResolutionSymbolFactsTable};

        INSERT INTO temp.{ReferenceResolutionSymbolFactsTable}(symbol_id, target_key)
        SELECT target.id,
               {ReferenceResolutionTargetKeySql}
        FROM symbols AS target
        JOIN files AS target_file ON target_file.id = target.file_id;
        """;

    private static string BuildRefreshCSharpReferenceFactsSql(string scopePredicate)
        => $"""
            DELETE FROM temp.csharp_reference_facts;

            INSERT INTO temp.csharp_reference_facts(
                reference_id,
                type_arity,
                argument_count,
                is_member_receiver,
                is_property_receiver_reference)
            SELECT r.id,
                   CASE
                       WHEN r.reference_kind IN ('instantiate', 'type_reference')
                         OR (
                             r.reference_kind = 'reference'
                             AND r.target_qualifier LIKE
                                 char(31) || 'property_receiver:%'
                         )
                       THEN csharp_reference_type_arity(
                           COALESCE(r.context, reference_line.context),
                           r.symbol_name,
                           r.column_number)
                   END,
                   CASE
                       WHEN r.reference_kind = 'instantiate'
                       THEN csharp_invocation_argument_count(
                           COALESCE(r.context, reference_line.context),
                           r.symbol_name,
                           r.column_number)
                   END,
                   CASE
                       WHEN (
                           r.reference_kind = 'type_reference'
                           AND r.target_qualifier IS NULL
                       ) OR (
                           r.reference_kind = 'reference'
                           AND r.target_qualifier LIKE
                               char(31) || 'property_receiver:%'
                       )
                       THEN csharp_reference_is_member_receiver(
                           COALESCE(r.context, reference_line.context),
                           r.symbol_name,
                           r.column_number)
                       ELSE 0
                   END,
                   CASE
                       WHEN r.reference_kind = 'reference'
                        AND r.target_qualifier LIKE
                            char(31) || 'property_receiver:%'
                       THEN 1
                       ELSE 0
                   END
            FROM symbol_references AS r
            JOIN files AS source_file
              ON source_file.id = r.file_id
             AND source_file.lang = 'csharp'
            LEFT JOIN reference_lines AS reference_line
              ON reference_line.id = r.reference_line_id
            WHERE {scopePredicate}
              AND (
                  r.reference_kind IN ('instantiate', 'type_reference')
                  OR (
                      r.reference_kind = 'reference'
                      AND r.target_qualifier LIKE
                          char(31) || 'property_receiver:%'
                  )
              );
            """;

    private static string RefreshCSharpReferenceFactsFullSql =>
        BuildRefreshCSharpReferenceFactsSql("1 = 1");

    private static string BuildRefreshCSharpPropertyTargetFactsSql(
        string symbolSource,
        string scopePredicate)
        => $"""
            DELETE FROM temp.csharp_property_target_facts;

            INSERT INTO temp.csharp_property_target_facts(
                name_folded,
                name,
                container_qualified_name,
                symbol_id)
            SELECT target.name_folded,
                   target.name,
                   target.container_qualified_name,
                   target.id
            {symbolSource}
            JOIN files AS target_file
              ON target_file.id = target.file_id
             AND target_file.lang = 'csharp'
            WHERE {scopePredicate}
              AND target.kind IN ('field', 'property')
              AND target.name_folded IS NOT NULL
              AND target.name IS NOT NULL
              AND target.container_qualified_name IS NOT NULL;
            """;

    private static string RefreshCSharpPropertyTargetFactsFullSql =>
        BuildRefreshCSharpPropertyTargetFactsSql(
            "FROM symbols AS target",
            "1 = 1");

    private static string BuildRefreshCSharpSymbolFactsSql(string scopeJoin)
        => $"""
            DELETE FROM temp.csharp_symbol_facts;

            INSERT INTO temp.csharp_symbol_facts(
                symbol_id,
                definition_type_arity,
                constructor_parameter_count,
                is_value_type)
            SELECT symbol.id,
                   csharp_definition_type_arity(
                       symbol.signature,
                       symbol.name,
                       symbol.kind),
                   csharp_constructor_parameter_count(
                       symbol.signature,
                       symbol.name,
                       symbol.kind),
                   csharp_definition_is_value_type(
                       symbol.signature,
                       symbol.kind)
            FROM symbols AS symbol
            {scopeJoin}
            JOIN files AS symbol_file
              ON symbol_file.id = symbol.file_id
             AND symbol_file.lang = 'csharp'
            WHERE symbol.kind IN (
                'class',
                'struct',
                'interface',
                'record',
                'enum',
                'delegate',
                'function');
            """;

    private static string RefreshCSharpSymbolFactsFullSql =>
        BuildRefreshCSharpSymbolFactsSql(string.Empty);

    private static string BuildCSharpDefinitionTypeAritySql(string symbolAlias)
        => $"""
            (
                SELECT symbol_fact.definition_type_arity
                FROM temp.csharp_symbol_facts AS symbol_fact
                WHERE symbol_fact.symbol_id = {symbolAlias}.id
            )
            """;

    private static string BuildCSharpConstructorParameterCountSql(string symbolAlias)
        => $"""
            (
                SELECT symbol_fact.constructor_parameter_count
                FROM temp.csharp_symbol_facts AS symbol_fact
                WHERE symbol_fact.symbol_id = {symbolAlias}.id
            )
            """;

    private static string BuildCSharpIsValueTypeSql(string symbolAlias)
        => $"""
            (
                SELECT symbol_fact.is_value_type
                FROM temp.csharp_symbol_facts AS symbol_fact
                WHERE symbol_fact.symbol_id = {symbolAlias}.id
            )
            """;

    private static string BuildCSharpProjectPrefixSql(string symbolAlias)
        => $"""
            CASE
                WHEN INSTR(COALESCE({symbolAlias}.family_key, ''), CHAR(31)) > 0
                     AND (
                         SUBSTR(COALESCE({symbolAlias}.family_key, ''), 1, 11) = 'file-local:'
                         OR SUBSTR(
                                COALESCE({symbolAlias}.family_key, ''),
                                INSTR(COALESCE({symbolAlias}.family_key, ''), '|') + 1,
                                11) = 'file-local:'
                     )
                    THEN SUBSTR(
                        {symbolAlias}.family_key,
                        1,
                        INSTR({symbolAlias}.family_key, CHAR(31)))
                WHEN INSTR(COALESCE({symbolAlias}.family_key, ''), '|') > 0
                    THEN SUBSTR(
                        {symbolAlias}.family_key,
                        1,
                        INSTR({symbolAlias}.family_key, '|'))
                ELSE NULL
            END
            """;

    private static string BuildCSharpUnprefixedTypeIdentitySql(string symbolAlias)
        => $"""
            CASE
                WHEN COALESCE({symbolAlias}.container_qualified_name, '') = ''
                    THEN {symbolAlias}.name
                WHEN {symbolAlias}.container_qualified_name = {symbolAlias}.name COLLATE BINARY
                     OR substr(
                            {symbolAlias}.container_qualified_name,
                            -length({symbolAlias}.name) - 1
                        ) = ('.' || {symbolAlias}.name) COLLATE BINARY
                    THEN {symbolAlias}.container_qualified_name
                ELSE {symbolAlias}.container_qualified_name || '.' || {symbolAlias}.name
            END
            """;

    private static string BuildCSharpTypeIdentityPrefixSql(string symbolAlias, string fileAlias)
        => $"""
            COALESCE(
                {BuildCSharpProjectPrefixSql(symbolAlias)},
                CASE
                    WHEN INSTR(
                             ' ' || LOWER(
                                 REPLACE(
                                     REPLACE(
                                         REPLACE(
                                             REPLACE(COALESCE({symbolAlias}.signature, ''), '(', ' '),
                                             ')',
                                             ' '),
                                         ':',
                                         ' '),
                                     CHAR(9),
                                     ' ')) || ' ',
                             ' partial ') > 0
                        THEN ''
                    ELSE {fileAlias}.path || char(31)
                END
            )
            """;

    private static readonly string RefreshCSharpTypeIdentityFactsSql = $"""
        DELETE FROM temp.csharp_type_identity_facts;

        WITH type_identity_parts(
            symbol_id,
            identity_prefix,
            unprefixed_type_identity,
            definition_type_arity) AS MATERIALIZED (
            SELECT symbol.id,
                   {BuildCSharpTypeIdentityPrefixSql("symbol", "symbol_file")},
                   {BuildCSharpUnprefixedTypeIdentitySql("symbol")},
                   symbol_fact.definition_type_arity
            FROM temp.csharp_symbol_facts AS symbol_fact
            JOIN symbols AS symbol
              ON symbol.id = symbol_fact.symbol_id
            JOIN files AS symbol_file
              ON symbol_file.id = symbol.file_id
             AND symbol_file.lang = 'csharp'
            WHERE symbol.kind IN (
                'class',
                'struct',
                'interface',
                'record',
                'enum',
                'delegate')
        )
        INSERT INTO temp.csharp_type_identity_facts(
            symbol_id,
            unprefixed_type_identity,
            type_identity)
        SELECT symbol_id,
               unprefixed_type_identity,
               identity_prefix ||
                   unprefixed_type_identity ||
                   char(31) ||
                   COALESCE(definition_type_arity, -1)
        FROM type_identity_parts;
        """;

    private static readonly string RefreshCSharpConstructorIdentityFactsSql = $"""
        DELETE FROM temp.csharp_constructor_identity_facts;

        WITH ranked_constructor_owners(
            constructor_symbol_id,
            type_identity,
            type_arity,
            owner_rank) AS MATERIALIZED (
            SELECT constructor.id,
                   constructor_type_identity.type_identity,
                   constructor_type_fact.definition_type_arity,
                   ROW_NUMBER() OVER (
                       PARTITION BY constructor.id
                       ORDER BY
                           COALESCE(constructor_type.end_line, constructor_type.line)
                               - COALESCE(constructor_type.start_line, constructor_type.line),
                           COALESCE(constructor_type.start_line, constructor_type.line) DESC,
                           constructor_type.id)
            FROM temp.csharp_symbol_facts AS constructor_fact
            JOIN symbols AS constructor
              ON constructor.id = constructor_fact.symbol_id
            JOIN files AS constructor_file
              ON constructor_file.id = constructor.file_id
             AND constructor_file.lang = 'csharp'
            JOIN symbols AS constructor_type
              ON constructor_type.file_id = constructor.file_id
             AND constructor_type.kind IN ('class', 'struct', 'record')
             AND COALESCE(constructor.start_line, constructor.line)
                 BETWEEN COALESCE(constructor_type.start_line, constructor_type.line)
                     AND COALESCE(constructor_type.end_line, constructor_type.line)
            JOIN temp.csharp_type_identity_facts AS constructor_type_identity
              ON constructor_type_identity.symbol_id = constructor_type.id
             AND constructor_type_identity.unprefixed_type_identity =
                 COALESCE(
                     NULLIF(constructor.container_qualified_name, ''),
                     NULLIF(constructor.container_name, ''),
                     constructor.name) COLLATE BINARY
            JOIN temp.csharp_symbol_facts AS constructor_type_fact
              ON constructor_type_fact.symbol_id = constructor_type.id
            WHERE constructor.kind = 'function'
              AND constructor_fact.constructor_parameter_count IS NOT NULL
        )
        INSERT INTO temp.csharp_constructor_identity_facts(
            symbol_id,
            type_identity,
            type_arity)
        SELECT constructor_symbol_id,
               type_identity,
               type_arity
        FROM ranked_constructor_owners
        WHERE owner_rank = 1;

        INSERT OR IGNORE INTO temp.csharp_constructor_identity_facts(
            symbol_id,
            type_identity,
            type_arity)
        SELECT constructor.id,
               COALESCE(
                   {BuildCSharpProjectPrefixSql("constructor")},
                   constructor_file.path || char(31)) ||
               COALESCE(
                   NULLIF(constructor.container_qualified_name, ''),
                   NULLIF(constructor.container_name, ''),
                   constructor.name) ||
               char(31) || '-1',
               NULL
        FROM temp.csharp_symbol_facts AS constructor_fact
        JOIN symbols AS constructor
          ON constructor.id = constructor_fact.symbol_id
        JOIN files AS constructor_file
          ON constructor_file.id = constructor.file_id
         AND constructor_file.lang = 'csharp'
        WHERE constructor.kind = 'function'
          AND constructor_fact.constructor_parameter_count IS NOT NULL
          AND NOT EXISTS (
              SELECT 1
              FROM temp.csharp_constructor_identity_facts AS existing
              WHERE existing.symbol_id = constructor.id
          );
        """;

    private static string BuildCSharpTypeIdentitySql(string symbolAlias)
        => $"""
            (
                SELECT type_identity_fact.type_identity
                FROM temp.csharp_type_identity_facts AS type_identity_fact
                WHERE type_identity_fact.symbol_id = {symbolAlias}.id
            )
            """;

    private static string BuildCSharpConstructorIdentitySql(string symbolAlias)
        => $"""
            (
                SELECT constructor_identity_fact.type_identity
                FROM temp.csharp_constructor_identity_facts AS constructor_identity_fact
                WHERE constructor_identity_fact.symbol_id = {symbolAlias}.id
            )
            """;

    private static string BuildCSharpConstructorTypeAritySql(string symbolAlias)
        => $"""
            (
                SELECT constructor_identity_fact.type_arity
                FROM temp.csharp_constructor_identity_facts AS constructor_identity_fact
                WHERE constructor_identity_fact.symbol_id = {symbolAlias}.id
            )
            """;

    private static string CSharpReferenceTypeAritySql => """
        (
            SELECT reference_fact.type_arity
            FROM temp.csharp_reference_facts AS reference_fact
            WHERE reference_fact.reference_id = r.id
        )
        """;

    private static string CSharpReferenceArgumentCountSql => """
        (
            SELECT reference_fact.argument_count
            FROM temp.csharp_reference_facts AS reference_fact
            WHERE reference_fact.reference_id = r.id
        )
        """;

    private static string CSharpTypeReferenceCandidatePredicateSql => $"""
        (
            source_file.lang <> 'csharp'
            OR r.reference_kind NOT IN ('instantiate', 'type_reference')
            OR CASE
                WHEN r.reference_kind = 'instantiate'
                     AND s.name <> r.symbol_name COLLATE BINARY THEN 0
                WHEN r.reference_kind = 'instantiate'
                     AND s.kind = 'function'
                     AND s.container_name = s.name COLLATE BINARY
                     AND {BuildCSharpConstructorParameterCountSql("s")} IS NOT NULL
                     AND (
                         {CSharpReferenceArgumentCountSql} IS NULL
                         OR {BuildCSharpConstructorParameterCountSql("s")}
                            = {CSharpReferenceArgumentCountSql}
                     )
                     AND (
                         {CSharpReferenceTypeAritySql} IS NULL
                         OR {BuildCSharpConstructorTypeAritySql("s")}
                            = {CSharpReferenceTypeAritySql}
                     ) THEN 1
                WHEN r.reference_kind = 'instantiate'
                     AND s.kind IN ('class', 'struct', 'record', 'enum', 'delegate')
                     AND (
                         {CSharpReferenceTypeAritySql} IS NULL
                         OR {BuildCSharpDefinitionTypeAritySql("s")}
                            = {CSharpReferenceTypeAritySql}
                     )
                     AND s.id = (
                         SELECT representative.id
                         FROM symbols AS representative
                         JOIN files AS representative_file
                           ON representative_file.id = representative.file_id
                          AND representative_file.lang = 'csharp'
                         WHERE representative.name_folded = s.name_folded
                           AND representative.name = s.name COLLATE BINARY
                           AND representative.kind IN (
                               'class',
                               'struct',
                               'record',
                               'enum',
                               'delegate')
                           AND {BuildCSharpTypeIdentitySql("representative")}
                               = {BuildCSharpTypeIdentitySql("s")} COLLATE BINARY
                         ORDER BY representative_file.path,
                                  COALESCE(representative.start_line, representative.line),
                                  representative.id
                         LIMIT 1
                     )
                     AND (
                         (
                             {BuildCSharpConstructorParameterCountSql("s")} IS NOT NULL
                             AND (
                                 {CSharpReferenceArgumentCountSql} IS NULL
                                 OR {BuildCSharpConstructorParameterCountSql("s")}
                                    = {CSharpReferenceArgumentCountSql}
                             )
                         )
                         OR s.kind = 'delegate'
                         OR (
                             s.kind = 'enum'
                             AND (
                                 {CSharpReferenceArgumentCountSql} IS NULL
                                 OR {CSharpReferenceArgumentCountSql} = 0
                             )
                         )
                         OR (
                             s.kind IN ('class', 'record')
                             AND {BuildCSharpIsValueTypeSql("s")} = 0
                             AND {BuildCSharpConstructorParameterCountSql("s")} IS NULL
                             AND (
                                 {CSharpReferenceArgumentCountSql} IS NULL
                                 OR {CSharpReferenceArgumentCountSql} = 0
                             )
                             AND NOT EXISTS (
                                 SELECT 1
                                 FROM symbols AS explicit_constructor
                                 JOIN files AS constructor_file
                                   ON constructor_file.id = explicit_constructor.file_id
                                  AND constructor_file.lang = 'csharp'
                                 WHERE explicit_constructor.name_folded = s.name_folded
                                   AND explicit_constructor.name = s.name COLLATE BINARY
                                   AND explicit_constructor.kind = 'function'
                                   AND explicit_constructor.container_name =
                                       explicit_constructor.name COLLATE BINARY
                                   AND {BuildCSharpConstructorParameterCountSql("explicit_constructor")}
                                       IS NOT NULL
                                   AND {BuildCSharpConstructorIdentitySql("explicit_constructor")}
                                       = {BuildCSharpTypeIdentitySql("s")} COLLATE BINARY
                             )
                         )
                         OR (
                             {BuildCSharpIsValueTypeSql("s")} = 1
                             AND (
                                 {CSharpReferenceArgumentCountSql} IS NULL
                                 OR {CSharpReferenceArgumentCountSql} = 0
                             )
                             AND NOT EXISTS (
                                 SELECT 1
                                 FROM symbols AS explicit_zero_constructor
                                 JOIN files AS zero_constructor_file
                                   ON zero_constructor_file.id = explicit_zero_constructor.file_id
                                  AND zero_constructor_file.lang = 'csharp'
                                 WHERE explicit_zero_constructor.name_folded = s.name_folded
                                   AND explicit_zero_constructor.name = s.name COLLATE BINARY
                                   AND explicit_zero_constructor.kind = 'function'
                                   AND explicit_zero_constructor.container_name =
                                       explicit_zero_constructor.name COLLATE BINARY
                                   AND {BuildCSharpConstructorParameterCountSql("explicit_zero_constructor")}
                                       = 0
                                   AND {BuildCSharpConstructorIdentitySql("explicit_zero_constructor")}
                                       = {BuildCSharpTypeIdentitySql("s")} COLLATE BINARY
                             )
                         )
                     ) THEN 1
                WHEN r.reference_kind = 'instantiate' THEN 0
                WHEN s.kind NOT IN ('class', 'struct', 'record', 'interface', 'enum', 'delegate') THEN 0
                WHEN s.name <> r.symbol_name COLLATE BINARY THEN 0
                WHEN {CSharpReferenceTypeAritySql} IS NULL THEN 1
                WHEN {BuildCSharpDefinitionTypeAritySql("s")}
                     = {CSharpReferenceTypeAritySql} THEN 1
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
          AND r.id IN (
              SELECT reference_fact.reference_id
              FROM temp.csharp_reference_facts AS reference_fact
              WHERE reference_fact.is_property_receiver_reference = 1
          )
          AND r.reference_kind = 'reference'
          AND r.target_qualifier LIKE char(31) || 'property_receiver:%'
          AND NOT EXISTS (
              SELECT 1
              FROM symbols AS source
              JOIN files AS source_file ON source_file.id = source.file_id
              JOIN temp.csharp_property_target_facts AS target
                ON target.name_folded = r.symbol_name_folded
               AND target.name = r.symbol_name COLLATE BINARY
              WHERE source.id = r.source_symbol_id
                AND source_file.lang = 'csharp'
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
                JOIN temp.csharp_property_target_facts AS target
                  ON target.name_folded = r.symbol_name_folded
                 AND target.name = r.symbol_name COLLATE BINARY
                WHERE source.id = r.source_symbol_id
                  AND source_file.lang = 'csharp'
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
                         target.symbol_id
                LIMIT 1
            )
        WHERE {scopePredicate}
          AND r.id IN (
              SELECT reference_fact.reference_id
              FROM temp.csharp_reference_facts AS reference_fact
              WHERE reference_fact.is_member_receiver = 1
          )
          AND r.reference_kind = 'type_reference'
          AND r.target_qualifier IS NULL
          AND EXISTS (
              SELECT 1
              FROM symbols AS source
              JOIN files AS source_file ON source_file.id = source.file_id
              JOIN temp.csharp_property_target_facts AS target
                ON target.name_folded = r.symbol_name_folded
               AND target.name = r.symbol_name COLLATE BINARY
              WHERE source.id = r.source_symbol_id
                AND source_file.lang = 'csharp'
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
        SELECT r.id, target.id, 0
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS target
          ON target.file_id = source_file.id
         AND (
             (target.kind = 'heading' AND target.name_folded = r.symbol_name_folded)
             OR (target.kind = 'anchor' AND target.name_folded = r.symbol_name COLLATE BINARY)
         )
        WHERE source_file.lang = 'markdown'
          AND r.reference_kind = 'reference'
          AND r.target_qualifier IS NULL;

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, target.id, 0
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN files AS target_file
          ON target_file.lang = 'markdown'
         AND target_file.path =
             markdown_resolve_path(source_file.path, r.target_qualifier)
        JOIN symbols AS target
          ON target.file_id = target_file.id
         AND (
             (target.kind = 'heading' AND target.name_folded = r.symbol_name_folded)
             OR (target.kind = 'anchor' AND target.name_folded = r.symbol_name COLLATE BINARY)
         )
        WHERE source_file.lang = 'markdown'
          AND r.reference_kind = 'reference'
          AND r.target_qualifier IS NOT NULL;

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
          AND s.kind IN ('field', 'property');

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
          AND source_file.lang <> 'markdown'
          AND r.target_qualifier IS NOT NULL
          AND r.target_qualifier NOT LIKE char(31) || 'receiver:%'
          AND (
               s.container_name = r.target_qualifier COLLATE NOCASE
               OR s.container_qualified_name = r.target_qualifier COLLATE NOCASE
               OR s.container_qualified_name LIKE '%.' || r.target_qualifier COLLATE NOCASE
               OR (
                   source_file.lang = 'csharp'
                   AND r.reference_kind = 'instantiate'
                   AND s.kind = 'function'
                   AND (
                       s.container_qualified_name =
                           (r.target_qualifier || '.' || r.symbol_name) COLLATE NOCASE
                       OR s.container_qualified_name LIKE
                           ('%.' || r.target_qualifier || '.' || r.symbol_name) COLLATE NOCASE
                   )
               )
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
          AND source_file.lang <> 'markdown'
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
          AND source_file.lang <> 'markdown'
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
          AND source_file.lang <> 'markdown'
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
          AND source_file.lang <> 'markdown'
          AND r.target_qualifier IS NULL
          AND (source_file.lang <> 'dependency_lock' OR s.file_id = r.file_id)
          AND source.container_name IS NOT NULL
          AND source.container_name <> ''
          AND s.container_name = source.container_name COLLATE NOCASE
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        -- Rank-5 fallbacks only need to know whether a lower rank matched. Keep that
        -- one-row-per-reference fact compact instead of probing the much larger
        -- physical-candidate table once for every fallback candidate.
        -- rank 5 fallbackが必要とするのは下位rankの一致有無だけであるため、各fallback
        -- candidateから巨大な物理candidate表を参照せず、referenceごと1行の集合に縮約する。
        DELETE FROM temp.{ReferenceLowerRankCandidateMatchesTable};

        INSERT INTO temp.{ReferenceLowerRankCandidateMatchesTable}(reference_id)
        SELECT lower_rank_candidate.reference_id
        FROM symbol_reference_candidates AS lower_rank_candidate
        WHERE lower_rank_candidate.scope_rank < 5
        GROUP BY lower_rank_candidate.reference_id;

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        WITH csharp_type_reference_members(
            symbol_id,
            name_folded,
            name,
            type_arity,
            type_identity) AS MATERIALIZED (
            SELECT type_symbol.id,
                   type_symbol.name_folded,
                   type_symbol.name,
                   type_symbol_fact.definition_type_arity,
                   type_identity_fact.type_identity
            FROM symbols AS type_symbol
            JOIN files AS target_file
              ON target_file.id = type_symbol.file_id
            CROSS JOIN temp.csharp_symbol_facts AS type_symbol_fact
              ON type_symbol_fact.symbol_id = type_symbol.id
            CROSS JOIN temp.csharp_type_identity_facts AS type_identity_fact
              ON type_identity_fact.symbol_id = type_symbol.id
            WHERE target_file.lang = 'csharp'
              AND type_symbol.name_folded IS NOT NULL
              AND type_symbol.kind IN ('class', 'struct', 'record', 'interface', 'enum', 'delegate')
              AND type_symbol_fact.definition_type_arity IS NOT NULL
        ),
        csharp_unique_type_reference_families(
            name_folded,
            name,
            type_arity,
            type_identity) AS MATERIALIZED (
            SELECT type_member.name_folded,
                   type_member.name,
                   type_member.type_arity,
                   MIN(type_member.type_identity COLLATE BINARY)
            FROM csharp_type_reference_members AS type_member
            GROUP BY type_member.name_folded,
                     type_member.name,
                     type_member.type_arity
            HAVING COUNT(DISTINCT type_member.type_identity COLLATE BINARY) = 1
        ),
        matched_csharp_type_reference_families(
            reference_id,
            name_folded,
            name,
            type_arity,
            type_identity) AS MATERIALIZED (
            SELECT r.id,
                   unique_family.name_folded,
                   unique_family.name,
                   unique_family.type_arity,
                   unique_family.type_identity
            FROM symbol_references AS r
            JOIN files AS source_file ON source_file.id = r.file_id
            JOIN csharp_unique_type_reference_families AS unique_family
              ON unique_family.name_folded = r.symbol_name_folded
             AND unique_family.name = r.symbol_name COLLATE BINARY
            LEFT JOIN temp.csharp_reference_facts AS reference_fact
              ON reference_fact.reference_id = r.id
            WHERE source_file.lang = 'csharp'
              AND r.target_qualifier IS NULL
              AND r.reference_kind = 'type_reference'
              AND (
                  reference_fact.type_arity IS NULL
                  OR unique_family.type_arity = reference_fact.type_arity
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM temp.{ReferenceLowerRankCandidateMatchesTable} AS lower_rank_match
                  WHERE lower_rank_match.reference_id = r.id
              )
        )
        SELECT matched_family.reference_id,
               type_member.symbol_id,
               5
        FROM matched_csharp_type_reference_families AS matched_family
        JOIN csharp_type_reference_members AS type_member
          ON type_member.name_folded = matched_family.name_folded
         AND type_member.name = matched_family.name COLLATE BINARY
         AND type_member.type_arity = matched_family.type_arity
         AND type_member.type_identity = matched_family.type_identity COLLATE BINARY;

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
        WHERE source_file.lang NOT IN ('csharp', 'markdown')
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
              SELECT 1
              FROM temp.{ReferenceLowerRankCandidateMatchesTable} AS lower_rank_match
              WHERE lower_rank_match.reference_id = r.id
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
              SELECT 1
              FROM temp.{ReferenceLowerRankCandidateMatchesTable} AS lower_rank_match
              WHERE lower_rank_match.reference_id = r.id
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
              SELECT 1
              FROM temp.{ReferenceLowerRankCandidateMatchesTable} AS lower_rank_match
              WHERE lower_rank_match.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, unique_target.symbol_id, 5
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN (
            SELECT candidate.id AS symbol_id,
                   unique_type.name_folded,
                   unique_type.name,
                   unique_type.type_arity,
                   unique_type.type_identity,
                   candidate.kind AS candidate_kind,
                   {BuildCSharpConstructorParameterCountSql("candidate")}
                       AS constructor_parameter_count,
                   {BuildCSharpIsValueTypeSql("candidate")} AS is_value_type
            FROM (
                SELECT s.name_folded,
                       s.name,
                       {BuildCSharpDefinitionTypeAritySql("s")} AS type_arity,
                       MIN({BuildCSharpTypeIdentitySql("s")}) AS type_identity
                FROM symbols AS s
                JOIN files AS target_file ON target_file.id = s.file_id
                WHERE target_file.lang = 'csharp'
                  AND s.name_folded IS NOT NULL
                  AND s.kind IN ('class', 'struct', 'record', 'enum', 'delegate')
                GROUP BY s.name_folded,
                         s.name,
                         {BuildCSharpDefinitionTypeAritySql("s")}
                HAVING COUNT(DISTINCT {BuildCSharpTypeIdentitySql("s")}) = 1
            ) AS unique_type
            JOIN symbols AS candidate
              ON candidate.name_folded = unique_type.name_folded
             AND candidate.name = unique_type.name COLLATE BINARY
            JOIN files AS candidate_file
              ON candidate_file.id = candidate.file_id
             AND candidate_file.lang = 'csharp'
            WHERE (
                candidate.kind = 'function'
                    AND candidate.container_name = candidate.name COLLATE BINARY
                    AND {BuildCSharpConstructorParameterCountSql("candidate")} IS NOT NULL
                    AND {BuildCSharpConstructorIdentitySql("candidate")}
                        = unique_type.type_identity COLLATE BINARY
                )
               OR (
                    candidate.kind IN ('class', 'struct', 'record', 'enum', 'delegate')
                    AND {BuildCSharpTypeIdentitySql("candidate")}
                        = unique_type.type_identity COLLATE BINARY
                    AND candidate.id = (
                        SELECT representative.id
                        FROM symbols AS representative
                        JOIN files AS representative_file
                          ON representative_file.id = representative.file_id
                         AND representative_file.lang = 'csharp'
                        WHERE representative.name_folded = unique_type.name_folded
                          AND representative.name = unique_type.name COLLATE BINARY
                          AND representative.kind IN (
                              'class',
                              'struct',
                              'record',
                              'enum',
                              'delegate')
                          AND {BuildCSharpTypeIdentitySql("representative")}
                              = unique_type.type_identity COLLATE BINARY
                        ORDER BY representative_file.path,
                                 COALESCE(representative.start_line, representative.line),
                                 representative.id
                        LIMIT 1
                    )
                )
        ) AS unique_target ON unique_target.name_folded = r.symbol_name_folded
                           AND unique_target.name = r.symbol_name COLLATE BINARY
        WHERE source_file.lang = 'csharp'
          AND r.target_qualifier IS NULL
          AND r.reference_kind = 'instantiate'
          AND (
              {CSharpReferenceTypeAritySql} IS NULL
              OR unique_target.type_arity = {CSharpReferenceTypeAritySql}
          )
          AND (
              (
                  unique_target.candidate_kind = 'function'
                  AND (
                      {CSharpReferenceArgumentCountSql} IS NULL
                      OR unique_target.constructor_parameter_count
                         = {CSharpReferenceArgumentCountSql}
                  )
              )
              OR (
                  unique_target.candidate_kind IN ('class', 'struct', 'record')
                  AND unique_target.constructor_parameter_count IS NOT NULL
                  AND (
                      {CSharpReferenceArgumentCountSql} IS NULL
                      OR unique_target.constructor_parameter_count
                         = {CSharpReferenceArgumentCountSql}
                  )
              )
              OR unique_target.candidate_kind = 'delegate'
              OR (
                  unique_target.candidate_kind = 'enum'
                  AND (
                      {CSharpReferenceArgumentCountSql} IS NULL
                      OR {CSharpReferenceArgumentCountSql} = 0
                  )
              )
              OR (
                  unique_target.candidate_kind IN ('class', 'record')
                  AND unique_target.is_value_type = 0
                  AND unique_target.constructor_parameter_count IS NULL
                  AND (
                      {CSharpReferenceArgumentCountSql} IS NULL
                      OR {CSharpReferenceArgumentCountSql} = 0
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM symbols AS explicit_constructor
                      JOIN files AS constructor_file
                        ON constructor_file.id = explicit_constructor.file_id
                       AND constructor_file.lang = 'csharp'
                      WHERE explicit_constructor.name_folded = unique_target.name_folded
                        AND explicit_constructor.name = unique_target.name COLLATE BINARY
                        AND explicit_constructor.kind = 'function'
                        AND explicit_constructor.container_name =
                            explicit_constructor.name COLLATE BINARY
                        AND {BuildCSharpConstructorParameterCountSql("explicit_constructor")}
                            IS NOT NULL
                        AND {BuildCSharpConstructorIdentitySql("explicit_constructor")}
                            = unique_target.type_identity COLLATE BINARY
                  )
              )
              OR (
                  unique_target.is_value_type = 1
                  AND (
                      {CSharpReferenceArgumentCountSql} IS NULL
                      OR {CSharpReferenceArgumentCountSql} = 0
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM symbols AS explicit_zero_constructor
                      JOIN files AS zero_constructor_file
                        ON zero_constructor_file.id = explicit_zero_constructor.file_id
                       AND zero_constructor_file.lang = 'csharp'
                      WHERE explicit_zero_constructor.name_folded = unique_target.name_folded
                        AND explicit_zero_constructor.name = unique_target.name COLLATE BINARY
                        AND explicit_zero_constructor.kind = 'function'
                        AND explicit_zero_constructor.container_name =
                            explicit_zero_constructor.name COLLATE BINARY
                        AND {BuildCSharpConstructorParameterCountSql("explicit_zero_constructor")}
                            = 0
                        AND {BuildCSharpConstructorIdentitySql("explicit_zero_constructor")}
                            = unique_target.type_identity COLLATE BINARY
                  )
              )
          )
          AND NOT EXISTS (
              SELECT 1
              FROM temp.{ReferenceLowerRankCandidateMatchesTable} AS lower_rank_match
              WHERE lower_rank_match.reference_id = r.id
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
                       COUNT(DISTINCT target_fact.target_key) AS target_family_count,
                       MIN(target_fact.target_key) AS minimum_target_key
                    FROM symbol_reference_candidates AS c
                    JOIN temp.reference_resolution_symbol_facts AS target_fact
                      ON target_fact.symbol_id = c.symbol_id
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

    internal static string RefreshReferenceResolutionFullSqlForTesting
        => CreateReferenceResolutionSymbolFactsTableSql + ";\n"
           + RefreshReferenceResolutionSymbolFactsFullSql + "\n"
           + RefreshReferenceResolutionFullSql;

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

    private static readonly string RefreshReferenceResolutionFreshSparseSql = $"""
        WITH resolution_facts AS MATERIALIZED (
            SELECT candidate.reference_id,
                   COUNT(*) AS candidate_count,
                   MIN(candidate.symbol_id) AS minimum_symbol_id,
                   COUNT(DISTINCT target_fact.target_key) AS target_family_count,
                   MIN(target_fact.target_key) AS minimum_target_key
            FROM symbol_reference_candidates AS candidate
            JOIN temp.{ReferenceResolutionSymbolFactsTable} AS target_fact
              ON target_fact.symbol_id = candidate.symbol_id
            GROUP BY candidate.reference_id
        )
        UPDATE symbol_references AS r
        SET target_symbol_id = CASE
                WHEN resolution.candidate_count = 1 THEN resolution.minimum_symbol_id
            END,
            target_symbol_key = CASE
                WHEN resolution.target_family_count = 1 THEN resolution.minimum_target_key
            END,
            resolution_candidate_count = resolution.candidate_count,
            resolution_state = CASE
                WHEN resolution.candidate_count = 1 THEN 'resolved'
                WHEN resolution.target_family_count = 1 THEN 'resolved_group'
                ELSE 'ambiguous'
            END,
            is_self_reference = CASE
                WHEN r.source_symbol_id IS NOT NULL
                 AND resolution.candidate_count = 1
                 AND r.source_symbol_id = resolution.minimum_symbol_id THEN 1
                ELSE 0
            END
        FROM resolution_facts AS resolution
        -- Fresh inserts already carry canonical candidate-free values. Aggregate and write
        -- only rows that gained a candidate during this graph build.
        -- fresh insertはcandidate-freeのcanonical値を保持するため、このgraph buildで
        -- candidateを得たrowだけを集約・更新する。
        WHERE r.id = resolution.reference_id;
        """;

    internal static string RefreshReferenceResolutionFreshSparseSqlForTesting
        => CreateReferenceResolutionSymbolFactsTableSql + ";\n"
           + RefreshReferenceResolutionSymbolFactsFullSql + "\n"
           + RefreshReferenceResolutionFreshSparseSql;

    private static readonly string RefreshMutualRecursionFlagsSql = $"""
        WITH desired_mutual_recursion(id, desired_value) AS MATERIALIZED (
            SELECT r.id,
                   {MutualRecursionValueSql}
            FROM symbol_references AS r
            -- Ordinary non-call rows are already persisted with the canonical zero value.
            -- Keep them out of the materialized working set while still repairing legacy or
            -- externally modified non-boolean values.
            -- 通常の非call rowはcanonicalな0で永続化済みなのでwork setから除外しつつ、
            -- legacyまたは外部変更による非boolean値は引き続き修復する。
            WHERE r.reference_kind IN (
                      'call',
                      'instantiate',
                      'subscribe',
                      'unsubscribe',
                      'razor_event_binding')
               OR r.is_mutual_recursion IS NOT 0
        )
        UPDATE symbol_references AS r
        SET is_mutual_recursion = desired.desired_value
        FROM desired_mutual_recursion AS desired
        WHERE r.id = desired.id
          -- IS NOT is null-safe and also normalizes legacy non-boolean values. Materializing
          -- the desired value keeps each correlated reverse-edge lookup to one evaluation
          -- per candidate instead of repeating it in both SET and WHERE.
          -- IS NOTによりNULLとlegacyの非boolean値も安全に正規化する。desired valueを
          -- materializeし、相関reverse-edge lookupをSETとWHEREで二重評価しない。
          AND r.is_mutual_recursion IS NOT desired.desired_value
        """;

    internal static string RefreshMutualRecursionFlagsSqlForTesting
        => RefreshMutualRecursionFlagsSql;

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
            CreateCSharpReferenceFactIndexesSql + ";\n" +
            RefreshReferenceSourceSymbolsFullSql + ";\n" +
            RefreshCSharpReferenceFactsFullSql + "\n" +
            RefreshCSharpSymbolFactsFullSql + "\n" +
            RefreshCSharpTypeIdentityFactsSql + "\n" +
            RefreshCSharpConstructorIdentityFactsSql + "\n" +
            RefreshCSharpPropertyTargetFactsFullSql + "\n" +
            NormalizeCSharpPropertyReceiverReferencesFullSql + "\n" +
            RefreshReferenceUniqueFamiliesSql + "\n" +
            RefreshReferenceCandidatesSql + "\n" +
            RefreshReferenceResolutionSymbolFactsFullSql + "\n" +
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

    internal void InsertReferencesInAtomicFileScope(
        IReadOnlyList<ReferenceRecord> references,
        bool refreshMutualRecursionFlags,
        CancellationToken cancellationToken,
        ReferenceSecondaryIndexBulkLoadGuard? referenceSecondaryIndexBulkLoad)
        => InsertReferencesInAtomicFileScopeCore(
            references,
            refreshMutualRecursionFlags,
            cancellationToken,
            referenceLinesAreNew: false,
            operation: nameof(InsertReferencesInAtomicFileScope),
            referenceSecondaryIndexBulkLoad: referenceSecondaryIndexBulkLoad);

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
        string operation,
        ReferenceSecondaryIndexBulkLoadGuard? referenceSecondaryIndexBulkLoad = null)
    {
        RequireCallerOwnedTransaction(operation);
        AtomicFileReferenceInsertForTesting?.Invoke(referenceLinesAreNew);
        InsertReferencesCore(
            references,
            refreshMutualRecursionFlags,
            cancellationToken,
            referenceLinesAreNew,
            batchesAreAtomicInCaller: true,
            referenceSecondaryIndexBulkLoad: referenceSecondaryIndexBulkLoad);
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
        bool batchesAreAtomicInCaller,
        ReferenceSecondaryIndexBulkLoadGuard? referenceSecondaryIndexBulkLoad = null)
    {
        if (references.Count == 0) return;
        TrackReferenceGraphInsertedReferences(references);
        InvalidateReferenceIdentityContractForMutation();

        // If a chunk commits but aggregate refresh is cancelled, readers must fall back to
        // raw references until InitializeSchema performs a complete backfill.
        // aggregate refresh 前に中断した場合は trust bit を残さず raw fallback に降格する。
        var aggregateWasReady = ClearHotspotReferenceAggregateReady();

        int rowsPerStatement = batchesAreAtomicInCaller
            ? GetRowsPerCallerTransactionInsertStatement(
                columnCount: ReferenceInsertParameterCountPerRow)
            : GetRowsPerInsertStatement(
                columnCount: ReferenceInsertParameterCountPerRow);
        var foldedNameCache = CreateFoldedNameCache(
            Math.Min(references.Count, rowsPerStatement),
            namesPerRow: 2);
        var newReferenceLineIds = referenceLinesAreNew
            ? new Dictionary<(long FileId, int Line, string Context), long>()
            : null;
        int referenceBatchCount = GetReferenceBatchCount(references.Count, rowsPerStatement);
        var useAuthoritativeFreshRawInsert = batchesAreAtomicInCaller
            && referenceLinesAreNew
            && _authoritativeFreshBulkInsertScope != null;
        if (batchesAreAtomicInCaller)
        {
            InsertAtomicReferenceBatches(
                references,
                referenceLinesAreNew,
                newReferenceLineIds,
                useCallerTransactionParameterBudget: true,
                foldedNameCache,
                rowsPerStatement,
                referenceBatchCount,
                useAuthoritativeFreshRawInsert,
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
                    rowsPerStatement,
                    cancellationToken);
                using var transaction = BeginReferenceBatchTransaction(cancellationToken);
                var referenceLineIds = MaterializeReferenceLines(
                    references,
                    start,
                    end,
                    referenceLinesAreNew,
                    newReferenceLineIds,
                    useCallerTransactionParameterBudget: false,
                    useAuthoritativeFreshRawInsert: false,
                    cancellationToken);
                InsertReferenceBatch(
                    references,
                    start,
                    end,
                    referenceLineIds,
                    foldedNameCache,
                    useAuthoritativeFreshRawInsert: false);
                transaction.Commit();
            }
        }

        CheckBatchCancellationAndReportProgress(
            "insert_references",
            references.Count,
            references.Count,
            rowsPerStatement,
            cancellationToken);
        RefreshHotspotReferenceCounts(references, cancellationToken);
        RestoreHotspotReferenceAggregateReady(aggregateWasReady);
        if (refreshMutualRecursionFlags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshMutualRecursionFlags(
                cancellationToken,
                referenceSecondaryIndexBulkLoad: referenceSecondaryIndexBulkLoad);
        }
    }

    private void InsertAtomicReferenceBatches(
        IReadOnlyList<ReferenceRecord> references,
        bool referenceLinesAreNew,
        Dictionary<(long FileId, int Line, string Context), long>? newReferenceLineIds,
        bool useCallerTransactionParameterBudget,
        Dictionary<string, string?> foldedNameCache,
        int rowsPerStatement,
        int referenceBatchCount,
        bool useAuthoritativeFreshRawInsert,
        CancellationToken cancellationToken)
    {
        for (int windowStartBatch = 0; windowStartBatch < referenceBatchCount;)
        {
            int windowStart = windowStartBatch * rowsPerStatement;
            CheckBatchCancellationAndReportProgress(
                "insert_references",
                windowStart,
                references.Count,
                rowsPerStatement,
                cancellationToken);
            int windowEndBatch = GetAtomicReferenceLineWindowEndBatch(
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
                useCallerTransactionParameterBudget,
                useAuthoritativeFreshRawInsert,
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
                        rowsPerStatement,
                        cancellationToken);
                }
                int end = Math.Min(start + rowsPerStatement, references.Count);
                InsertReferenceBatch(
                    references,
                    start,
                    end,
                    referenceLineIds,
                    foldedNameCache,
                    useAuthoritativeFreshRawInsert);
            }

            windowStartBatch = windowEndBatch;
        }
    }

    private static int GetAtomicReferenceLineWindowEndBatch(
        int windowStartBatch,
        int referenceBatchCount,
        int rowsPerStatement)
    {
        int maxReferenceLines = GetRowsPerInsertStatement(columnCount: 3);
        int worstCaseBatches = Math.Max(1, maxReferenceLines / rowsPerStatement);
        int windowBatchCount = Math.Min(
            MaxReferenceLineWindowBatchCount,
            worstCaseBatches);
        return Math.Min(
            referenceBatchCount,
            windowStartBatch + windowBatchCount);
    }

    internal static int GetAtomicReferenceLineWindowEndBatchForTesting(
        int windowStartBatch,
        int referenceBatchCount,
        int rowsPerStatement)
        => GetAtomicReferenceLineWindowEndBatch(
            windowStartBatch,
            referenceBatchCount,
            rowsPerStatement);

    private ReferenceLineBatchMap MaterializeReferenceLines(
        IReadOnlyList<ReferenceRecord> references,
        int start,
        int end,
        bool referenceLinesAreNew,
        Dictionary<(long FileId, int Line, string Context), long>? newReferenceLineIds,
        bool useCallerTransactionParameterBudget,
        bool useAuthoritativeFreshRawInsert,
        CancellationToken cancellationToken)
        => referenceLinesAreNew
            ? InsertNewReferenceLines(
                references,
                start,
                end,
                newReferenceLineIds!,
                useCallerTransactionParameterBudget,
                useAuthoritativeFreshRawInsert,
                cancellationToken)
            : UpsertReferenceLines(
                references,
                start,
                end,
                useCallerTransactionParameterBudget,
                cancellationToken);

    private void InsertReferenceBatch(
        IReadOnlyList<ReferenceRecord> references,
        int start,
        int end,
        ReferenceLineBatchMap referenceLineIds,
        Dictionary<string, string?> foldedNameCache,
        bool useAuthoritativeFreshRawInsert)
    {
        if (useAuthoritativeFreshRawInsert)
        {
            (_authoritativeFreshBulkInsertScope
                ?? throw new InvalidOperationException(
                    "The authoritative fresh raw insert scope ended before a reference batch."))
                .InsertReferences(
                    references,
                    start,
                    end,
                    referenceLineIds,
                    foldedNameCache);
            return;
        }

        var rowsInBatch = end - start;
        var useFreshReferenceResolutionDefaults = _referenceGraphRefreshScope is
        {
            IsDisposed: false,
            FreshReferenceResolutionDefaultsPending: true,
        };
        var cacheKey = (
            Rows: rowsInBatch,
            FreshResolutionDefaults: useFreshReferenceResolutionDefaults);
        var sql = ReferenceInsertSqlCache.GetOrAdd(
            cacheKey,
            static key => BuildReferenceInsertSql(key.Rows, key.FreshResolutionDefaults));
        var cmd = RentCommand(sql, c => AddReferenceInsertParameters(c, rowsInBatch));
        try
        {
            var parameterIndex = 0;
            for (int index = start; index < end; index++)
            {
                var reference = references[index];
                ValidateReferenceKinds(reference);

                cmd.Parameters[parameterIndex++].Value = reference.FileId;
                cmd.Parameters[parameterIndex++].Value = reference.SymbolName;
                cmd.Parameters[parameterIndex++].Value = reference.ReferenceKind;
                cmd.Parameters[parameterIndex++].Value = reference.Line;
                cmd.Parameters[parameterIndex++].Value = reference.Column;
                cmd.Parameters[parameterIndex++].Value =
                    (object?)(reference.SpanLength > 0 ? reference.SpanLength : null)
                    ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value =
                    referenceLineIds.GetReferenceLineId(index);
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
                cmd.Parameters[parameterIndex++].Value =
                    !useFreshReferenceResolutionDefaults && reference.IsSelfReference ? 1 : 0;
                cmd.Parameters[parameterIndex++].Value =
                    !useFreshReferenceResolutionDefaults && reference.IsMutualRecursion ? 1 : 0;
                cmd.Parameters[parameterIndex++].Value = (object?)ExtractTargetQualifier(reference) ?? DBNull.Value;
            }

            ReferenceInsertBindingWorkForTesting?.Invoke(
                new ReferenceInsertBindingWork(
                    rowsInBatch,
                    cmd.Parameters.Count,
                    referenceLineIds.ReferenceCount,
                    referenceLineIds.ReferenceLineCount,
                    useFreshReferenceResolutionDefaults));
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

        int rowsPerStatement = GetRowsPerInsertStatement(
            columnCount: ReferenceInsertParameterCountPerRow);
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
                    rowsAdvancedSincePreviousCheckpoint: 1,
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
            CheckBatchCancellationAndReportProgress(
                "refresh_hotspot_reference_counts",
                completed,
                fileIds.Count,
                rowsAdvancedSincePreviousCheckpoint: 1,
                cancellationToken);
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        transaction.Commit();
    }

    internal void RefreshHotspotReferenceCountsForTesting(
        IReadOnlyCollection<long> fileIds,
        CancellationToken cancellationToken)
        => RefreshHotspotReferenceCounts(fileIds, cancellationToken);

    private ReferenceLineBatchMap UpsertReferenceLines(
        IReadOnlyList<ReferenceRecord> references,
        int start,
        int end,
        bool useCallerTransactionParameterBudget,
        CancellationToken cancellationToken)
    {
        var lineIds = ReferenceLineBatchMap.Create(
            references,
            start,
            end,
            cancellationToken);
        var rows = lineIds.Keys;
        int rowsPerStatement = useCallerTransactionParameterBudget
            ? GetRowsPerCallerTransactionInsertStatement(columnCount: 3)
            : GetRowsPerInsertStatement(columnCount: 3);
        for (int i = 0; i < rows.Length; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress(
                "upsert_reference_lines",
                i,
                rows.Length,
                rowsPerStatement,
                cancellationToken);
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

        int keysPerStatement = rowsPerStatement;
        for (int i = 0; i < rows.Length; i += keysPerStatement)
        {
            CheckBatchCancellationAndReportProgress(
                "lookup_reference_lines",
                i,
                rows.Length,
                keysPerStatement,
                cancellationToken);
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
                    var rowIndex = ResolveReferenceLineInputRowIndex(
                        i,
                        statementRowCount,
                        reader.IsDBNull(1) ? null : reader.GetInt32(1));
                    lineIds.SetReferenceLineId(rowIndex, id);
                }
            }
            finally
            {
                ReleaseCommand(cmd);
            }
        }

        lineIds.CompleteMaterialization();
        return lineIds;
    }

    private ReferenceLineBatchMap InsertNewReferenceLines(
        IReadOnlyList<ReferenceRecord> references,
        int start,
        int end,
        Dictionary<(long FileId, int Line, string Context), long> knownLineIds,
        bool useCallerTransactionParameterBudget,
        bool useAuthoritativeFreshRawInsert,
        CancellationToken cancellationToken)
    {
        var lineIds = ReferenceLineBatchMap.Create(
            references,
            start,
            end,
            cancellationToken);
        var rows = new List<(long FileId, int Line, string Context)>(lineIds.ReferenceLineCount);
        var rowOrdinals = new List<int>(lineIds.ReferenceLineCount);
        for (var ordinal = 0; ordinal < lineIds.Keys.Length; ordinal++)
        {
            var key = lineIds.Keys[ordinal];
            if (knownLineIds.TryGetValue(key, out var knownId))
                lineIds.SetReferenceLineId(ordinal, knownId);
            else
            {
                rows.Add(key);
                rowOrdinals.Add(ordinal);
            }
        }

        int rowsPerStatement = useCallerTransactionParameterBudget
            ? GetRowsPerCallerTransactionInsertStatement(columnCount: 3)
            : GetRowsPerInsertStatement(columnCount: 3);
        for (int i = 0; i < rows.Count; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress(
                "insert_reference_lines",
                i,
                rows.Count,
                rowsPerStatement,
                cancellationToken);
            int batchEnd = Math.Min(i + rowsPerStatement, rows.Count);
            var statementRowCount = batchEnd - i;
            if (useAuthoritativeFreshRawInsert)
            {
                (_authoritativeFreshBulkInsertScope
                    ?? throw new InvalidOperationException(
                        "The authoritative fresh raw insert scope ended before a reference-line batch."))
                    .InsertReferenceLines(
                        rows,
                        i,
                        batchEnd,
                        rowOrdinals,
                        lineIds,
                        knownLineIds);
            }
            else
            {
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
                        var rowIndex = ResolveReferenceLineInputRowIndex(
                            i,
                            statementRowCount,
                            reader.IsDBNull(1) ? null : reader.GetInt32(1));
                        var lineOrdinal = rowOrdinals[rowIndex];
                        var key = rows[rowIndex];
                        lineIds.SetReferenceLineId(lineOrdinal, id);
                        knownLineIds[key] = id;
                    }
                }
                finally
                {
                    ReleaseCommand(cmd);
                }
            }
        }

        lineIds.CompleteMaterialization();
        return lineIds;
    }

    private static int ResolveReferenceLineInputRowIndex(
        int statementStart,
        int statementRowCount,
        int? inputOrdinal)
    {
        if (inputOrdinal is not { } ordinal
            || (uint)ordinal >= (uint)statementRowCount)
        {
            throw new InvalidDataException(
                $"Reference-line materialization returned invalid input ordinal {inputOrdinal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"} for {statementRowCount} rows.");
        }

        return checked(statementStart + ordinal);
    }

    internal static void ValidateReferenceLineMaterializedOrdinalsForTesting(
        int expectedCount,
        IReadOnlyList<int?> inputOrdinals)
    {
        ArgumentNullException.ThrowIfNull(inputOrdinals);
        if (expectedCount < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedCount));

        var keys = new (long FileId, int Line, string Context)[expectedCount];
        var referenceLineOrdinals = new int[expectedCount];
        for (var ordinal = 0; ordinal < expectedCount; ordinal++)
            referenceLineOrdinals[ordinal] = ordinal;
        var lineIds = new ReferenceLineBatchMap(
            referenceStart: 0,
            keys,
            referenceLineOrdinals);
        for (var resultIndex = 0; resultIndex < inputOrdinals.Count; resultIndex++)
        {
            var ordinal = ResolveReferenceLineInputRowIndex(
                statementStart: 0,
                statementRowCount: expectedCount,
                inputOrdinals[resultIndex]);
            lineIds.SetReferenceLineId(ordinal, resultIndex + 1L);
        }
        lineIds.CompleteMaterialization();
    }

    internal sealed class ReferenceLineBatchMap
    {
        private readonly int _referenceStart;
        private readonly int[] _referenceLineOrdinals;
        private readonly long[] _referenceLineIds;
        private readonly bool[] _hasReferenceLineIds;
        private bool _materializationComplete;

        internal ReferenceLineBatchMap(
            int referenceStart,
            (long FileId, int Line, string Context)[] keys,
            int[] referenceLineOrdinals)
        {
            _referenceStart = referenceStart;
            Keys = keys;
            _referenceLineOrdinals = referenceLineOrdinals;
            _referenceLineIds = new long[keys.Length];
            _hasReferenceLineIds = new bool[keys.Length];
        }

        internal (long FileId, int Line, string Context)[] Keys { get; }
        internal int ReferenceCount => _referenceLineOrdinals.Length;
        internal int ReferenceLineCount => Keys.Length;

        internal static ReferenceLineBatchMap Create(
            IReadOnlyList<ReferenceRecord> references,
            int start,
            int end,
            CancellationToken cancellationToken)
        {
            var referenceCount = end - start;
            var keys = new List<(long FileId, int Line, string Context)>(referenceCount);
            var keyOrdinals = new Dictionary<(long FileId, int Line, string Context), int>(
                referenceCount);
            var referenceLineOrdinals = new int[referenceCount];
            (long FileId, int Line, string Context)? previousKey = null;
            var previousOrdinal = 0;
            for (var index = start; index < end; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = references[index];
                var key = (reference.FileId, reference.Line, reference.Context);
                int ordinal;
                if (previousKey is { } prior && ReferenceLineKeysEqual(prior, key))
                {
                    ordinal = previousOrdinal;
                }
                else if (!keyOrdinals.TryGetValue(key, out ordinal))
                {
                    ordinal = keys.Count;
                    keys.Add(key);
                    keyOrdinals.Add(key, ordinal);
                }

                referenceLineOrdinals[index - start] = ordinal;
                previousKey = key;
                previousOrdinal = ordinal;
            }

            return new ReferenceLineBatchMap(
                start,
                keys.ToArray(),
                referenceLineOrdinals);
        }

        internal void SetReferenceLineId(int ordinal, long id)
        {
            if (_materializationComplete)
                throw new InvalidOperationException("Reference-line materialization is already complete.");
            if ((uint)ordinal >= (uint)_referenceLineIds.Length)
            {
                throw new InvalidDataException(
                    $"Reference-line materialization returned out-of-range ordinal {ordinal} for {_referenceLineIds.Length} rows.");
            }
            if (id <= 0)
                throw new InvalidDataException("Reference-line materialization returned a non-positive ID.");
            if (_hasReferenceLineIds[ordinal])
            {
                throw new InvalidDataException(
                    $"Reference-line materialization returned duplicate ordinal {ordinal}.");
            }

            _referenceLineIds[ordinal] = id;
            _hasReferenceLineIds[ordinal] = true;
        }

        internal void CompleteMaterialization()
        {
            for (var ordinal = 0; ordinal < _referenceLineIds.Length; ordinal++)
            {
                if (_hasReferenceLineIds[ordinal])
                    continue;

                throw new InvalidDataException(
                    $"Reference-line ID was not materialized for input ordinal {ordinal}.");
            }

            _materializationComplete = true;
        }

        internal long GetReferenceLineId(int referenceIndex)
        {
            if (!_materializationComplete)
                throw new InvalidOperationException("Reference-line materialization is incomplete.");

            var offset = referenceIndex - _referenceStart;
            if ((uint)offset >= (uint)_referenceLineOrdinals.Length)
                throw new ArgumentOutOfRangeException(nameof(referenceIndex));

            return _referenceLineIds[_referenceLineOrdinals[offset]];
        }
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

    internal void RefreshMutualRecursionFlags(
        CancellationToken cancellationToken = default,
        bool? stampReferenceIdentityContractReady = null,
        ReferenceSecondaryIndexBulkLoadGuard? referenceSecondaryIndexBulkLoad = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MutualRecursionRefreshForTesting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var stampReferenceIdentityContract = stampReferenceIdentityContractReady
            ?? CSharpFamilyTrustAllowsReferenceIdentityReady(validatePersistedRows: true);
        var graphScope = _referenceGraphRefreshScope;
        using var transaction = BeginTransaction(cancellationToken, "refresh reference identities");
        if (graphScope != null)
            graphScope.IsCompleting = true;
        SqliteCommand? createUniqueFamiliesCommand = null;
        SqliteCommand? createCSharpReferenceFactIndexesCommand = null;
        SqliteCommand? refreshIdentityCommand = null;
        SqliteCommand? refreshMutualCommand = null;
        try
        {
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            referenceSecondaryIndexBulkLoad?.PrepareForCandidatePopulation(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            createUniqueFamiliesCommand = RentCommand(CreateReferenceUniqueFamiliesSql, static _ => { });
            createUniqueFamiliesCommand.ExecuteNonQuery();
            createCSharpReferenceFactIndexesCommand = RentCommand(
                CreateCSharpReferenceFactIndexesSql,
                static _ => { });
            createCSharpReferenceFactIndexesCommand.ExecuteNonQuery();
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
            var useFreshReferenceResolutionDefaults = graphScope?.FreshReferenceResolutionDefaultsPending == true;
            if (useFreshReferenceResolutionDefaults && !refreshPlan.UseFullRefresh)
            {
                throw new InvalidOperationException(
                    "Fresh reference resolution defaults require a full graph refresh.");
            }
            // A true empty-database graph aggregates candidate-side resolution facts once and
            // seeks only candidate-bearing references. Other persisted resolutions retain the
            // differential path so stable rows are not rewritten.
            // 真に空のdatabaseではcandidate側resolution factsを1回集約し、candidateを持つ
            // referenceだけをseekする。その他の既存resolutionはstable rowを書き換えない
            // differential pathを維持する。
            string refreshIdentitySql;
            if (refreshPlan.UseFullRefresh)
            {
                var hasPersistedReferenceResolutionState = !useFreshReferenceResolutionDefaults
                    && HasPersistedReferenceResolutionState(cancellationToken);
                var refreshReferenceSourcesSql = SelectReferenceSourceRefreshSql(
                    useFreshReferenceResolutionDefaults,
                    hasPersistedReferenceResolutionState);
                var refreshReferenceResolutionSql = useFreshReferenceResolutionDefaults
                    ? RefreshReferenceResolutionFreshSparseSql
                    : hasPersistedReferenceResolutionState
                        ? RefreshReferenceResolutionDifferentialSql
                        : RefreshReferenceResolutionFullSql;
                refreshIdentitySql =
                    (refreshReferenceSourcesSql == null
                        ? string.Empty
                        : refreshReferenceSourcesSql + ";\n") +
                                     RefreshCSharpReferenceFactsFullSql + "\n" +
                                     RefreshCSharpSymbolFactsFullSql + "\n" +
                                     RefreshCSharpTypeIdentityFactsSql + "\n" +
                                     RefreshCSharpConstructorIdentityFactsSql + "\n" +
                                     RefreshCSharpPropertyTargetFactsFullSql + "\n" +
                                     NormalizeCSharpPropertyReceiverReferencesFullSql + "\n" +
                                     RefreshReferenceUniqueFamiliesSql + "\n" +
                                     RefreshReferenceCandidatesSql + "\n" +
                                     RefreshReferenceResolutionSymbolFactsFullSql + "\n" +
                                     refreshReferenceResolutionSql + "\n";
            }
            else
            {
                DeleteRemovedReferenceCandidates(cancellationToken);
                refreshIdentitySql = RefreshScopedReferenceSourceSymbolsSql + "\n" +
                                     RefreshCSharpReferenceFactsScopedSql + "\n" +
                                     RefreshCSharpSymbolFactsScopedSql + "\n" +
                                     RefreshCSharpTypeIdentityFactsSql + "\n" +
                                     RefreshCSharpConstructorIdentityFactsSql + "\n" +
                                     RefreshCSharpPropertyTargetFactsScopedSql + "\n" +
                                     NormalizeCSharpPropertyReceiverReferencesScopedSql + "\n" +
                                     RefreshScopedReferenceUniqueFamiliesSql + "\n" +
                                     RefreshScopedReferenceCandidatesSql + "\n" +
                                     RefreshScopedReferenceResolutionSymbolFactsSql + "\n" +
                                     RefreshScopedReferenceResolutionSql + "\n" +
                                     ExpandReferenceGraphNewMutualScopeSql + "\n";
            }
            var hotspotReferenceFileIds = GetReferenceGraphRefreshFileIds(
                refreshPlan.UseFullRefresh,
                cancellationToken);
            refreshIdentityCommand = RentCommand(refreshIdentitySql, static _ => { });
            // Reconcile the marker inside the same transaction, but before the graph refresh
            // so the public SQLite changes() result continues to describe recursion updates.
            // High-level indexing defers v7 while untouched legacy C# family rows remain.
            // 同一 transaction 内で先に marker を調整して公開 changes() を維持する。
            // high-level index は未更新の旧 C# family row が残る間 v7 を保留する。
            if (stampReferenceIdentityContract)
                MarkReferenceIdentityContractReady();
            else
                ClearReferenceIdentityContractReady();
            cancellationToken.ThrowIfCancellationRequested();
            referenceSecondaryIndexBulkLoad?.ReportIdentityRefreshStarted();
            refreshIdentityCommand.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            // Resolution changes alter the default C# common-call hotspot projection even
            // when the caller file itself was skipped. Refresh those source-file aggregates
            // before the recursion statement, which preserves the public changes() contract.
            // resolution の変更は caller file 自体が skip されても C# common-call の既定
            // hotspot projection を変えるため、公開 changes() 契約を保つ recursion 文の前に
            // 対象 source file の aggregate を再集計する。
            RefreshHotspotReferenceCounts(hotspotReferenceFileIds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            referenceSecondaryIndexBulkLoad?.PrepareForMutualRecursion(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            referenceSecondaryIndexBulkLoad?.ReportMutualRecursionStarted();
            refreshMutualCommand = RentCommand(
                refreshPlan.UseFullRefresh
                    ? RefreshMutualRecursionFlagsSql
                    : RefreshScopedMutualRecursionFlagsSql,
                static _ => { });
            refreshMutualCommand.ExecuteNonQuery();
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
            if (refreshMutualCommand != null)
                ReleaseCommand(refreshMutualCommand);
            if (refreshIdentityCommand != null)
                ReleaseCommand(refreshIdentityCommand);
            if (createCSharpReferenceFactIndexesCommand != null)
                ReleaseCommand(createCSharpReferenceFactIndexesCommand);
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
        // Preserve a useful simple receiver through null-conditional/null-forgiving
        // punctuation (`json?.Read()` / `json!.Read()`). More complex receivers receive a
        // conservative non-null marker below so they can never enter the global fallback.
        // null conditional / null forgiving の句読点（`json?.Read()` / `json!.Read()`）を
        // 越えて単純 receiver を保持する。複雑な receiver は下で保守的な non-null marker
        // を付け、global fallback に入らないようにする。
        while (end >= 0 && context[end] is '?' or '!')
        {
            end--;
            while (end >= 0 && char.IsWhiteSpace(context[end]))
                end--;
        }
        var start = end;
        while (start >= 0 && (char.IsLetterOrDigit(context[start]) || context[start] is '_' or '@'))
            start--;
        var qualifier = context[(start + 1)..(end + 1)].TrimStart('@');
        if (qualifier.Length == 0)
            return NonIdentifierReceiverQualifier;
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
