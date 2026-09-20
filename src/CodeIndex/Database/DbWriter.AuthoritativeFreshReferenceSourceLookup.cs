using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    internal const string AuthoritativeFreshReferenceSourceSymbolsTableName =
        "authoritative_fresh_reference_source_symbols";

    private static readonly string InitializeAuthoritativeFreshReferenceSourceLookupSql = $"""
        CREATE TEMP TABLE IF NOT EXISTS {AuthoritativeFreshReferenceSourceSymbolsTableName} (
            symbol_id           INTEGER NOT NULL PRIMARY KEY,
            file_id             INTEGER NOT NULL,
            name                TEXT,
            name_folded         TEXT,
            display_name_folded TEXT,
            line                INTEGER,
            start_line          INTEGER,
            end_line            INTEGER
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS temp.idx_authoritative_fresh_source_name_folded
        ON {AuthoritativeFreshReferenceSourceSymbolsTableName}(
            file_id, name_folded,
            COALESCE(end_line, line) - COALESCE(start_line, line),
            COALESCE(start_line, line) DESC, symbol_id, COALESCE(end_line, line))
        WHERE name_folded IS NOT NULL;

        CREATE INDEX IF NOT EXISTS temp.idx_authoritative_fresh_source_display_name_folded
        ON {AuthoritativeFreshReferenceSourceSymbolsTableName}(
            file_id, display_name_folded,
            COALESCE(end_line, line) - COALESCE(start_line, line),
            COALESCE(start_line, line) DESC, symbol_id, COALESCE(end_line, line))
        WHERE display_name_folded IS NOT NULL;

        CREATE INDEX IF NOT EXISTS temp.idx_authoritative_fresh_source_name_nocase
        ON {AuthoritativeFreshReferenceSourceSymbolsTableName}(
            file_id, name COLLATE NOCASE,
            COALESCE(end_line, line) - COALESCE(start_line, line),
            COALESCE(start_line, line) DESC, symbol_id, COALESCE(end_line, line))
        WHERE name_folded IS NULL;

        DELETE FROM temp.{AuthoritativeFreshReferenceSourceSymbolsTableName};
        """;

    private static readonly string ClearAuthoritativeFreshReferenceSourceLookupSql = $"""
        DELETE FROM temp.{AuthoritativeFreshReferenceSourceSymbolsTableName}
        """;

    // Empty partial indexes prove these channels absent without scanning all symbols.
    // 部分indexの空判定で全symbolの走査を避ける。
    private static readonly string HasOnlyCanonicalFreshReferenceSourcesSql = $"""
        SELECT NOT EXISTS (
            SELECT 1 FROM temp.{AuthoritativeFreshReferenceSourceSymbolsTableName}
                INDEXED BY idx_authoritative_fresh_source_display_name_folded
            WHERE display_name_folded IS NOT NULL
        ) AND NOT EXISTS (
            SELECT 1 FROM temp.{AuthoritativeFreshReferenceSourceSymbolsTableName}
                INDEXED BY idx_authoritative_fresh_source_name_nocase
            WHERE name_folded IS NULL
        )
        """;

    private static readonly string PopulateAuthoritativeFreshReferenceSourceLookupSql = $"""
        INSERT INTO temp.{AuthoritativeFreshReferenceSourceSymbolsTableName} (
            symbol_id,
            file_id,
            name,
            name_folded,
            display_name_folded,
            line,
            start_line,
            end_line)
        SELECT persisted.id,
               persisted.file_id,
               persisted.name,
               persisted.name_folded,
               persisted.display_name_folded,
               persisted.line,
               persisted.start_line,
               persisted.end_line
        FROM main.symbols AS persisted INDEXED BY idx_symbols_file
        WHERE persisted.file_id = $file_id
        """;

    internal static string PopulateAuthoritativeFreshReferenceSourceLookupSqlForTesting
        => PopulateAuthoritativeFreshReferenceSourceLookupSql;

    private static string BuildMaterializedFreshReferenceSourceSymbolValueSql(
        string referenceAlias,
        bool canonicalNamesOnly = false)
    {
        var canonicalProbe = BuildRankedFreshReferenceSourceProbeSql(
            referenceAlias,
            $"source.name_folded = {referenceAlias}.container_name_folded");
        // Most freshly written files have neither display aliases nor legacy NULL
        // keys. Their sole ranked probe needs no union or final comparison sort.
        // display alias/旧NULL keyがないfileは、単一の順位付きprobeだけで解決する。
        if (canonicalNamesOnly)
            return $"(SELECT symbol_id FROM ({canonicalProbe}))";

        // Each name index supplies its best containing symbol in rank order. The
        // final sort compares at most three rows, even for a large overload family.
        // Duplicate matches retain the same rank and ID and cannot change the winner.
        // 各名前indexから包含順位の先頭だけを取り、最後のsortを最大3行に制限する。
        return $"""
        (
            SELECT candidate.symbol_id
            FROM (
                {canonicalProbe}

                UNION ALL

                {BuildRankedFreshReferenceSourceProbeSql(referenceAlias,
                    $"source.display_name_folded = {referenceAlias}.container_name_folded")}

                UNION ALL

                {BuildRankedFreshReferenceSourceProbeSql(referenceAlias,
                    $"source.name_folded IS NULL AND source.name = {referenceAlias}.container_name COLLATE NOCASE")}
            ) AS candidate
            ORDER BY candidate.range_width,
                     candidate.start_line DESC,
                     candidate.symbol_id
            LIMIT 1
        )
        """;
    }

    private static string BuildRankedFreshReferenceSourceProbeSql(
        string referenceAlias,
        string namePredicate)
        => $"""
        SELECT * FROM (
            SELECT source.symbol_id,
                   COALESCE(source.end_line, source.line) -
                       COALESCE(source.start_line, source.line) AS range_width,
                   COALESCE(source.start_line, source.line) AS start_line
            FROM temp.{AuthoritativeFreshReferenceSourceSymbolsTableName} AS source
            WHERE {referenceAlias}.container_name IS NOT NULL
              AND {referenceAlias}.container_name <> ''
              AND source.file_id = {referenceAlias}.file_id
              AND {namePredicate}
              AND {referenceAlias}.line BETWEEN COALESCE(source.start_line, source.line)
                                           AND COALESCE(source.end_line, source.line)
            ORDER BY COALESCE(source.end_line, source.line) - COALESCE(source.start_line, source.line),
                     COALESCE(source.start_line, source.line) DESC,
                     source.symbol_id
            LIMIT 1
        )
        """;

    internal static string BuildMaterializedFreshReferenceSourceSymbolValueSqlForTesting(
        string referenceAlias,
        bool canonicalNamesOnly = false)
        => BuildMaterializedFreshReferenceSourceSymbolValueSql(referenceAlias, canonicalNamesOnly);

    private void InitializeAuthoritativeFreshReferenceSourceLookup(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
        try
        {
            Execute(
                InitializeAuthoritativeFreshReferenceSourceLookupSql,
                _activeTransaction);
        }
        catch (SqliteException exception) when (
            IsSqliteInterruptCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(
                "Authoritative fresh source lookup initialization was interrupted.",
                exception,
                cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private bool MaterializeAuthoritativeFreshReferenceSourceLookup(
        IReadOnlyList<ReferenceRecord> references,
        CancellationToken cancellationToken)
    {
        RequireCallerOwnedTransaction(
            nameof(MaterializeAuthoritativeFreshReferenceSourceLookup));
        cancellationToken.ThrowIfCancellationRequested();

        var fileIds = new List<long>();
        var seenFileIds = new HashSet<long>();
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.ContainerName is not { Length: > 0 }
                || !seenFileIds.Add(reference.FileId))
            {
                continue;
            }
            fileIds.Add(reference.FileId);
        }

        using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
        bool canonicalNamesOnly;
        try
        {
            using (var clear = _conn.CreateCommand())
            {
                clear.Transaction = _activeTransaction;
                clear.CommandText = ClearAuthoritativeFreshReferenceSourceLookupSql;
                clear.ExecuteNonQuery();
            }

            if (fileIds.Count > 0)
            {
                using var populate = _conn.CreateCommand();
                populate.Transaction = _activeTransaction;
                populate.CommandText = PopulateAuthoritativeFreshReferenceSourceLookupSql;
                var fileIdParameter = populate.Parameters.Add("$file_id", SqliteType.Integer);
                foreach (var fileId in fileIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fileIdParameter.Value = fileId;
                    populate.ExecuteNonQuery();
                }
            }

            using var shape = _conn.CreateCommand();
            shape.Transaction = _activeTransaction;
            shape.CommandText = HasOnlyCanonicalFreshReferenceSourcesSql;
            canonicalNamesOnly = Convert.ToInt64(shape.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        catch (SqliteException exception) when (
            IsSqliteInterruptCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(
                "Authoritative fresh source lookup materialization was interrupted.",
                exception,
                cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return canonicalNamesOnly;
    }
}
