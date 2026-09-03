using CodeIndex.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    internal const int DatabaseSizeAttributionTopObjectLimit = 20;
    internal const int DatabaseSizeAttributionObjectNameLimit = 128;
    internal const long DatabaseSizeAttributionPageLimit = 1_000_000;
    internal const long DatabaseSizeAttributionObjectLimit = 100_000;

    private StatusDatabaseSizeAttribution ReadDatabaseSizeAttribution(
        StatusDbPragmaSettings pragmaSettings,
        long? mainFileBytes,
        long? walFileBytes,
        long? shmFileBytes)
    {
        var physicalFileSetBytes = TryAddNonNegative(mainFileBytes, walFileBytes, shmFileBytes);
        if (!TryMultiplyNonNegative(
                pragmaSettings.PageCount,
                pragmaSettings.PageSize,
                out var logicalDatabaseBytes)
            || !TryMultiplyNonNegative(
                pragmaSettings.FreelistCount,
                pragmaSettings.PageSize,
                out var freelistBytes))
        {
            return BuildUnavailableDatabaseSizeAttribution(
                "pragma_values_unavailable",
                pragmaSettings,
                logicalDatabaseBytes: null,
                mainFileBytes,
                walFileBytes,
                shmFileBytes,
                physicalFileSetBytes,
                freelistBytes: null);
        }
        if (pragmaSettings.PageCount > DatabaseSizeAttributionPageLimit)
        {
            return BuildUnavailableDatabaseSizeAttribution(
                "page_attribution_limit_exceeded",
                pragmaSettings,
                logicalDatabaseBytes,
                mainFileBytes,
                walFileBytes,
                shmFileBytes,
                physicalFileSetBytes,
                freelistBytes);
        }

        try
        {
            Cancellation.ThrowIfCancellationRequested();
            using var command = _conn.CreateCommand();
            command.CommandText = """
                WITH object_pages AS (
                    SELECT
                        d.name AS object_name,
                        CASE
                            WHEN d.name = 'sqlite_schema' THEN 'table'
                            WHEN s.type = 'table' THEN 'table'
                            WHEN s.type = 'index' THEN 'index'
                            ELSE 'other'
                        END AS object_type,
                        SUM(d.pgsize) AS page_bytes,
                        SUM(d.payload) AS payload_bytes,
                        SUM(d.unused) AS unused_bytes,
                        SUM(CASE WHEN d.pagetype = 'internal' THEN d.pgsize ELSE 0 END) AS internal_page_bytes,
                        SUM(CASE WHEN d.pagetype = 'leaf' THEN d.pgsize ELSE 0 END) AS leaf_page_bytes,
                        SUM(CASE WHEN d.pagetype = 'overflow' THEN d.pgsize ELSE 0 END) AS overflow_page_bytes,
                        SUM(CASE
                            WHEN d.pagetype IN ('internal', 'leaf', 'overflow') THEN 0
                            ELSE d.pgsize
                        END) AS other_page_bytes
                    FROM dbstat AS d
                    LEFT JOIN sqlite_schema AS s
                        ON s.name = d.name
                       AND s.type IN ('table', 'index')
                    GROUP BY
                        d.name,
                        CASE
                            WHEN d.name = 'sqlite_schema' THEN 'table'
                            WHEN s.type = 'table' THEN 'table'
                            WHEN s.type = 'index' THEN 'index'
                            ELSE 'other'
                        END
                ),
                ranked AS (
                    SELECT
                        object_name,
                        object_type,
                        page_bytes,
                        payload_bytes,
                        unused_bytes,
                        internal_page_bytes,
                        leaf_page_bytes,
                        overflow_page_bytes,
                        other_page_bytes,
                        COUNT(*) OVER () AS object_count,
                        SUM(page_bytes) OVER () AS allocated_object_bytes,
                        SUM(payload_bytes) OVER () AS total_payload_bytes,
                        SUM(unused_bytes) OVER () AS total_unused_bytes,
                        SUM(internal_page_bytes) OVER () AS total_internal_page_bytes,
                        SUM(leaf_page_bytes) OVER () AS total_leaf_page_bytes,
                        SUM(overflow_page_bytes) OVER () AS total_overflow_page_bytes,
                        SUM(other_page_bytes) OVER () AS total_other_page_bytes,
                        SUM(CASE WHEN object_type = 'table' THEN page_bytes ELSE 0 END) OVER () AS table_bytes,
                        SUM(CASE WHEN object_type = 'index' THEN page_bytes ELSE 0 END) OVER () AS index_bytes,
                        SUM(CASE WHEN object_type = 'other' THEN page_bytes ELSE 0 END) OVER () AS other_object_bytes
                    FROM object_pages
                )
                SELECT
                    object_name,
                    object_type,
                    page_bytes,
                    payload_bytes,
                    unused_bytes,
                    internal_page_bytes,
                    leaf_page_bytes,
                    overflow_page_bytes,
                    other_page_bytes,
                    object_count,
                    allocated_object_bytes,
                    total_payload_bytes,
                    total_unused_bytes,
                    total_internal_page_bytes,
                    total_leaf_page_bytes,
                    total_overflow_page_bytes,
                    total_other_page_bytes,
                    table_bytes,
                    index_bytes,
                    other_object_bytes
                FROM ranked
                ORDER BY page_bytes DESC, object_name COLLATE BINARY
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", DatabaseSizeAttributionTopObjectLimit);

            using var cancellationRegistration = RegisterSqliteInterruptForCancellation();
            using var reader = command.ExecuteReader();
            var topObjects = new List<StatusDatabaseObjectSize>(DatabaseSizeAttributionTopObjectLimit);
            long objectCount = 0;
            long allocatedObjectBytes = 0;
            long payloadBytes = 0;
            long unusedBytes = 0;
            long internalPageBytes = 0;
            long leafPageBytes = 0;
            long overflowPageBytes = 0;
            long otherPageBytes = 0;
            long tableBytes = 0;
            long indexBytes = 0;
            long otherObjectBytes = 0;
            var first = true;

            while (reader.Read())
            {
                Cancellation.ThrowIfCancellationRequested();
                var rawName = reader.GetString(0);
                var safeName = DiagnosticSanitizer.ForMessage(
                    rawName,
                    DatabaseSizeAttributionObjectNameLimit - 3);
                topObjects.Add(new StatusDatabaseObjectSize
                {
                    Name = safeName,
                    ObjectType = reader.GetString(1),
                    NameRedactedOrTruncated = !string.Equals(rawName, safeName, StringComparison.Ordinal),
                    PageBytes = reader.GetInt64(2),
                    PayloadBytes = reader.GetInt64(3),
                    UnusedBytes = reader.GetInt64(4),
                });

                if (!first)
                    continue;

                objectCount = reader.GetInt64(9);
                allocatedObjectBytes = reader.GetInt64(10);
                payloadBytes = reader.GetInt64(11);
                unusedBytes = reader.GetInt64(12);
                internalPageBytes = reader.GetInt64(13);
                leafPageBytes = reader.GetInt64(14);
                overflowPageBytes = reader.GetInt64(15);
                otherPageBytes = reader.GetInt64(16);
                tableBytes = reader.GetInt64(17);
                indexBytes = reader.GetInt64(18);
                otherObjectBytes = reader.GetInt64(19);
                first = false;
            }

            if (objectCount > DatabaseSizeAttributionObjectLimit)
            {
                return BuildUnavailableDatabaseSizeAttribution(
                    "page_attribution_limit_exceeded",
                    pragmaSettings,
                    logicalDatabaseBytes,
                    mainFileBytes,
                    walFileBytes,
                    shmFileBytes,
                    physicalFileSetBytes,
                    freelistBytes);
            }

            if (!TrySubtractNonNegative(
                    allocatedObjectBytes,
                    payloadBytes,
                    unusedBytes,
                    out var structuralOverheadBytes)
                || !TrySubtractNonNegative(
                    logicalDatabaseBytes,
                    allocatedObjectBytes,
                    freelistBytes,
                    out var unexplainedResidualBytes)
                || !Reconciles(
                    allocatedObjectBytes,
                    tableBytes,
                    indexBytes,
                    otherObjectBytes)
                || !Reconciles(
                    allocatedObjectBytes,
                    internalPageBytes,
                    leafPageBytes,
                    overflowPageBytes,
                    otherPageBytes))
            {
                return BuildUnavailableDatabaseSizeAttribution(
                    "page_attribution_inconsistent",
                    pragmaSettings,
                    logicalDatabaseBytes,
                    mainFileBytes,
                    walFileBytes,
                    shmFileBytes,
                    physicalFileSetBytes,
                    freelistBytes);
            }

            return new StatusDatabaseSizeAttribution
            {
                Available = true,
                Measurement = "dbstat_page_bytes",
                PageSizeBytes = pragmaSettings.PageSize,
                PageCount = pragmaSettings.PageCount,
                LogicalDatabaseBytes = logicalDatabaseBytes,
                MainFileBytes = mainFileBytes,
                WalFileBytes = walFileBytes,
                ShmFileBytes = shmFileBytes,
                PhysicalFileSetBytes = physicalFileSetBytes,
                AllocatedObjectBytes = allocatedObjectBytes,
                TableBytes = tableBytes,
                IndexBytes = indexBytes,
                OtherObjectBytes = otherObjectBytes,
                InternalPageBytes = internalPageBytes,
                LeafPageBytes = leafPageBytes,
                OverflowPageBytes = overflowPageBytes,
                OtherPageBytes = otherPageBytes,
                PayloadBytes = payloadBytes,
                UnusedBytes = unusedBytes,
                StructuralOverheadBytes = structuralOverheadBytes,
                FreelistBytes = freelistBytes,
                UnexplainedResidualBytes = unexplainedResidualBytes,
                ObjectCount = objectCount,
                TopObjectLimit = DatabaseSizeAttributionTopObjectLimit,
                TopObjectsTruncated = objectCount > topObjects.Count,
                TopObjects = topObjects,
            };
        }
        catch (SqliteException exception) when (IsSqliteInterruptCancellation(exception))
        {
            throw new OperationCanceledException(
                "The SQLite database-attribution scan was interrupted by cancellation.",
                exception,
                Cancellation);
        }
        catch (SqliteException)
        {
            return ReadFilePageAttribution(
                pragmaSettings,
                logicalDatabaseBytes,
                mainFileBytes,
                walFileBytes,
                shmFileBytes,
                physicalFileSetBytes,
                freelistBytes);
        }
    }

    private StatusDatabaseSizeAttribution ReadFilePageAttribution(
        StatusDbPragmaSettings pragmaSettings,
        long logicalDatabaseBytes,
        long? mainFileBytes,
        long? walFileBytes,
        long? shmFileBytes,
        long? physicalFileSetBytes,
        long freelistBytes)
    {
        var databasePath = TryGetLocalDatabasePath();
        if (databasePath == null
            || pragmaSettings.PageCount is not { } pageCount
            || pragmaSettings.PageSize is not { } pageSize)
        {
            return BuildUnavailableDatabaseSizeAttribution(
                "database_file_unavailable",
                pragmaSettings,
                logicalDatabaseBytes,
                mainFileBytes,
                walFileBytes,
                shmFileBytes,
                physicalFileSetBytes,
                freelistBytes);
        }

        try
        {
            var attribution =
                string.Equals(pragmaSettings.JournalMode, "wal", StringComparison.OrdinalIgnoreCase)
                && !_databaseFileSnapshotStable
                    ? SqlitePageAttributionReader.ReadConnectionSnapshot(
                        _conn,
                        pageCount,
                        pageSize,
                        Cancellation)
                    : SqlitePageAttributionReader.Read(
                        _conn,
                        databasePath,
                        pageCount,
                        pageSize,
                        Cancellation);
            if (!TrySubtractNonNegative(
                    attribution.AllocatedObjectBytes,
                    attribution.PayloadBytes,
                    attribution.UnusedBytes,
                    out var structuralOverheadBytes)
                || !TrySubtractNonNegative(
                    logicalDatabaseBytes,
                    attribution.AllocatedObjectBytes,
                    freelistBytes,
                    out var unexplainedResidualBytes)
                || !Reconciles(
                    attribution.AllocatedObjectBytes,
                    attribution.TableBytes,
                    attribution.IndexBytes,
                    attribution.OtherObjectBytes)
                || !Reconciles(
                    attribution.AllocatedObjectBytes,
                    attribution.InternalPageBytes,
                    attribution.LeafPageBytes,
                    attribution.OverflowPageBytes,
                    attribution.OtherPageBytes))
            {
                return BuildUnavailableDatabaseSizeAttribution(
                    "page_attribution_inconsistent",
                    pragmaSettings,
                    logicalDatabaseBytes,
                    mainFileBytes,
                    walFileBytes,
                    shmFileBytes,
                    physicalFileSetBytes,
                    freelistBytes);
            }

            return new StatusDatabaseSizeAttribution
            {
                Available = true,
                Measurement = "sqlite_file_btree_pages",
                PageSizeBytes = pageSize,
                PageCount = pageCount,
                LogicalDatabaseBytes = logicalDatabaseBytes,
                MainFileBytes = mainFileBytes,
                WalFileBytes = walFileBytes,
                ShmFileBytes = shmFileBytes,
                PhysicalFileSetBytes = physicalFileSetBytes,
                AllocatedObjectBytes = attribution.AllocatedObjectBytes,
                TableBytes = attribution.TableBytes,
                IndexBytes = attribution.IndexBytes,
                OtherObjectBytes = attribution.OtherObjectBytes,
                InternalPageBytes = attribution.InternalPageBytes,
                LeafPageBytes = attribution.LeafPageBytes,
                OverflowPageBytes = attribution.OverflowPageBytes,
                OtherPageBytes = attribution.OtherPageBytes,
                PayloadBytes = attribution.PayloadBytes,
                UnusedBytes = attribution.UnusedBytes,
                StructuralOverheadBytes = structuralOverheadBytes,
                FreelistBytes = freelistBytes,
                UnexplainedResidualBytes = unexplainedResidualBytes,
                ObjectCount = attribution.ObjectCount,
                TopObjectLimit = DatabaseSizeAttributionTopObjectLimit,
                TopObjectsTruncated = attribution.ObjectCount > attribution.TopObjects.Count,
                TopObjects = attribution.TopObjects
                    .Select(item => new StatusDatabaseObjectSize
                    {
                        Name = item.Name,
                        ObjectType = item.ObjectType,
                        NameRedactedOrTruncated = item.NameRedactedOrTruncated,
                        PageBytes = item.PageBytes,
                        PayloadBytes = item.PayloadBytes,
                        UnusedBytes = item.UnusedBytes,
                    })
                    .ToList(),
            };
        }
        catch (Exception ex) when (ex is
            SqliteException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or OverflowException
            or ArgumentException
            or NotSupportedException)
        {
            return BuildUnavailableDatabaseSizeAttribution(
                "file_page_attribution_unavailable",
                pragmaSettings,
                logicalDatabaseBytes,
                mainFileBytes,
                walFileBytes,
                shmFileBytes,
                physicalFileSetBytes,
                freelistBytes);
        }
    }

    internal static StatusDatabaseSizeAttribution BuildUnavailableDatabaseSizeAttribution(
        string reason,
        StatusDbPragmaSettings pragmaSettings,
        long? logicalDatabaseBytes,
        long? mainFileBytes,
        long? walFileBytes,
        long? shmFileBytes,
        long? physicalFileSetBytes,
        long? freelistBytes)
        => new()
        {
            Available = false,
            Measurement = "unavailable",
            UnavailableReason = reason,
            PageSizeBytes = pragmaSettings.PageSize,
            PageCount = pragmaSettings.PageCount,
            LogicalDatabaseBytes = logicalDatabaseBytes,
            MainFileBytes = mainFileBytes,
            WalFileBytes = walFileBytes,
            ShmFileBytes = shmFileBytes,
            PhysicalFileSetBytes = physicalFileSetBytes,
            FreelistBytes = freelistBytes,
            TopObjectLimit = DatabaseSizeAttributionTopObjectLimit,
        };

    private static bool TryMultiplyNonNegative(long? left, long? right, out long result)
    {
        result = 0;
        if (left is null or < 0 || right is null or <= 0)
            return false;

        try
        {
            result = checked(left.Value * right.Value);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long? TryAddNonNegative(params long?[] values)
    {
        try
        {
            long total = 0;
            foreach (var value in values)
            {
                if (value is null or < 0)
                    return null;
                total = checked(total + value.Value);
            }

            return total;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool TrySubtractNonNegative(
        long total,
        long first,
        long second,
        out long residual)
    {
        residual = 0;
        if (total < 0 || first < 0 || second < 0)
            return false;

        try
        {
            residual = checked(total - first - second);
            return residual >= 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool Reconciles(long expected, params long[] values)
    {
        try
        {
            long actual = 0;
            foreach (var value in values)
            {
                if (value < 0)
                    return false;
                actual = checked(actual + value);
            }

            return actual == expected;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
