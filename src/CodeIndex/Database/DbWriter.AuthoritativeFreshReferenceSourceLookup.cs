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
        ON {AuthoritativeFreshReferenceSourceSymbolsTableName}(file_id, name_folded)
        WHERE name_folded IS NOT NULL;

        CREATE INDEX IF NOT EXISTS temp.idx_authoritative_fresh_source_display_name_folded
        ON {AuthoritativeFreshReferenceSourceSymbolsTableName}(file_id, display_name_folded)
        WHERE display_name_folded IS NOT NULL;

        CREATE INDEX IF NOT EXISTS temp.idx_authoritative_fresh_source_name_nocase
        ON {AuthoritativeFreshReferenceSourceSymbolsTableName}(file_id, name COLLATE NOCASE)
        WHERE name_folded IS NULL;

        DELETE FROM temp.{AuthoritativeFreshReferenceSourceSymbolsTableName};
        """;

    private static readonly string ClearAuthoritativeFreshReferenceSourceLookupSql = $"""
        DELETE FROM temp.{AuthoritativeFreshReferenceSourceSymbolsTableName}
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
        string referenceAlias)
        // Only the first ranked symbol ID is observed. A symbol matching multiple
        // name indexes has the same range and ID in every arm, so duplicates cannot
        // change that winner. UNION ALL avoids a distinct temporary B-tree per reference.
        // 同一symbolの重複は順位もIDも同じ。先頭1件の選択に不要な参照ごとの重複除去を省く。
        => $"""
        (
            SELECT candidate.symbol_id
            FROM (
                SELECT source.symbol_id,
                       source.line,
                       source.start_line,
                       source.end_line
                FROM temp.{AuthoritativeFreshReferenceSourceSymbolsTableName} AS source
                WHERE {referenceAlias}.container_name IS NOT NULL
                  AND {referenceAlias}.container_name <> ''
                  AND source.file_id = {referenceAlias}.file_id
                  AND source.name_folded = {referenceAlias}.container_name_folded

                UNION ALL

                SELECT source.symbol_id,
                       source.line,
                       source.start_line,
                       source.end_line
                FROM temp.{AuthoritativeFreshReferenceSourceSymbolsTableName} AS source
                WHERE {referenceAlias}.container_name IS NOT NULL
                  AND {referenceAlias}.container_name <> ''
                  AND source.file_id = {referenceAlias}.file_id
                  AND source.display_name_folded = {referenceAlias}.container_name_folded

                UNION ALL

                SELECT source.symbol_id,
                       source.line,
                       source.start_line,
                       source.end_line
                FROM temp.{AuthoritativeFreshReferenceSourceSymbolsTableName} AS source
                WHERE {referenceAlias}.container_name IS NOT NULL
                  AND {referenceAlias}.container_name <> ''
                  AND source.file_id = {referenceAlias}.file_id
                  AND source.name_folded IS NULL
                  AND source.name = {referenceAlias}.container_name COLLATE NOCASE
            ) AS candidate
            WHERE {referenceAlias}.line BETWEEN COALESCE(candidate.start_line, candidate.line)
                                             AND COALESCE(candidate.end_line, candidate.line)
            ORDER BY (COALESCE(candidate.end_line, candidate.line) -
                      COALESCE(candidate.start_line, candidate.line)),
                     COALESCE(candidate.start_line, candidate.line) DESC,
                     candidate.symbol_id
            LIMIT 1
        )
        """;

    internal static string BuildMaterializedFreshReferenceSourceSymbolValueSqlForTesting(
        string referenceAlias)
        => BuildMaterializedFreshReferenceSourceSymbolValueSql(referenceAlias);

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

    private void MaterializeAuthoritativeFreshReferenceSourceLookup(
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
    }
}
