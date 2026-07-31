using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    internal readonly record struct CSharpContractWorkspaceReadStats(
        int MemberCandidateRowsRead,
        int ExactMembersRetained,
        int InterfaceDeclarationRowsRead,
        int InterfaceDeclarationBatchQueries);

    private sealed record CSharpFilesInPathsLookup(
        HashSet<string> Paths,
        FilePurgePlan PurgePlan);

    // The workspace prepass needs both interface declarations and their static contract members.
    // workspace prepassではinterface宣言とstatic contract memberの両方が必要。
    private const string CSharpStaticInterfaceContractPredicateSql = @"
              (
                    s.kind = 'interface'
                    OR (
                        s.container_kind = 'interface'
                        AND s.kind IN ('function', 'operator', 'property')
                        AND s.signature LIKE '%static%'
                        AND (s.signature LIKE '%abstract%' OR s.signature LIKE '%virtual%')
                    )
              )";

    private const string CSharpStaticInterfaceContractMemberPredicateSql = @"
              s.container_kind = 'interface'
              AND s.kind IN ('function', 'operator', 'property')
              AND s.signature LIKE '%static%'
              AND (s.signature LIKE '%abstract%' OR s.signature LIKE '%virtual%')";

    private const string CSharpStaticInterfaceContractWhereSql = @"
            WHERE f.lang = 'csharp'
              AND " + CSharpStaticInterfaceContractPredicateSql;

    private const string CSharpStaticInterfaceContractMemberWhereSql = @"
            WHERE f.lang = 'csharp'
              AND " + CSharpStaticInterfaceContractMemberPredicateSql;

    private const string CSharpContractWorkspaceProjectionSql = @"
                f.path,
                s.file_id, s.kind, s.name, s.line,
                COALESCE(s.start_line, s.line) AS start_line,
                s.start_column,
                COALESCE(s.end_line, COALESCE(s.start_line, s.line)) AS end_line,
                s.body_start_line, s.body_end_line,
                s.signature,
                s.container_kind, s.container_name, s.container_qualified_name,
                s.family_key, s.visibility, s.return_type,
                s.is_metadata_target";

    // Start with files(lang), then probe only member-capable kinds for each C# file.
    // The old OR query mixed interface declarations with members and made SQLite use
    // symbols(file_id), physically visiting every symbol in every C# file.
    // files(lang) を起点に member 候補 kind だけを symbols(file_id, kind) で probe する。
    internal const string CSharpStaticInterfaceContractMemberWorkspaceSql = @"
            SELECT " + CSharpContractWorkspaceProjectionSql + @"
            FROM files f INDEXED BY idx_files_lang
            CROSS JOIN symbols s INDEXED BY idx_symbols_file_kind
              ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND " + CSharpStaticInterfaceContractMemberPredicateSql;

    private const string CSharpMemberReadTargetPredicateSql = @"
              (
                    (s.kind = 'enum' AND s.container_kind = 'enum')
                    OR (
                        s.kind IN ('field', 'property')
                        AND s.container_kind IN ('class', 'struct', 'interface')
                        AND (s.signature LIKE '%static%' OR s.signature LIKE '%const%')
                    )
              )";

    internal const string CSharpMemberReadTargetWorkspaceSql = @"
            SELECT " + CSharpContractWorkspaceProjectionSql + @"
            FROM files f INDEXED BY idx_files_lang
            CROSS JOIN symbols s INDEXED BY idx_symbols_file_kind
              ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND " + CSharpMemberReadTargetPredicateSql;

    internal bool? GetCSharpStaticInterfaceSourceEvidence()
    {
        var raw = GetMetaString(DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey);
        return bool.TryParse(raw, out var value) ? value : null;
    }

    internal void SetCSharpStaticInterfaceSourceEvidence(bool? hasContracts)
        => SetMeta(
            DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey,
            hasContracts?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public List<SymbolRecord> LoadCSharpStaticInterfaceContractSymbols(IReadOnlySet<string>? excludedPaths = null)
        => LoadCSharpStaticInterfaceContractSymbols(excludedPaths, out _);

    internal List<SymbolRecord> LoadCSharpStaticInterfaceContractSymbols(
        IReadOnlySet<string>? excludedPaths,
        out bool excludedPathsHaveContracts)
        => LoadCSharpStaticInterfaceContractSymbols(
            excludedPaths,
            excludedExistingFileIds: null,
            isExistingSymbolPathExcluded: null,
            out excludedPathsHaveContracts,
            out _);

    internal List<SymbolRecord> LoadCSharpStaticInterfaceContractSymbols(
        IReadOnlySet<string>? excludedPaths,
        IReadOnlyList<long>? excludedExistingFileIds,
        out bool excludedPathsHaveContracts)
        => LoadCSharpStaticInterfaceContractSymbols(
            excludedPaths,
            excludedExistingFileIds,
            isExistingSymbolPathExcluded: null,
            out excludedPathsHaveContracts,
            out _);

    internal List<SymbolRecord> LoadCSharpStaticInterfaceContractSymbols(
        IReadOnlySet<string>? excludedPaths,
        IReadOnlyList<long>? excludedExistingFileIds,
        Func<string, bool>? isExistingSymbolPathExcluded,
        out bool excludedPathsHaveContracts,
        CancellationToken cancellationToken = default)
        => LoadCSharpStaticInterfaceContractSymbols(
            excludedPaths,
            excludedExistingFileIds,
            isExistingSymbolPathExcluded,
            out excludedPathsHaveContracts,
            out _,
            cancellationToken);

    internal List<SymbolRecord> LoadCSharpStaticInterfaceContractSymbols(
        IReadOnlySet<string>? excludedPaths,
        IReadOnlyList<long>? excludedExistingFileIds,
        Func<string, bool>? isExistingSymbolPathExcluded,
        out bool excludedPathsHaveContracts,
        out bool excludedPathsHaveMemberReadTargets,
        CancellationToken cancellationToken = default)
    {
        excludedPathsHaveContracts = false;
        excludedPathsHaveMemberReadTargets = false;
        var symbols = new List<SymbolRecord>();
        var retainedContractContainerNames = new HashSet<string>(StringComparer.Ordinal);
        var memberCandidateRowsRead = 0;
        var exactMembersRetained = 0;
        var interfaceDeclarationRowsRead = 0;
        var interfaceDeclarationBatchQueries = 0;

        cancellationToken.ThrowIfCancellationRequested();
        CSharpContractWorkspaceReadForTesting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var cmd = RentCommand(CSharpStaticInterfaceContractMemberWorkspaceSql, static _ => { });
        try
        {
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                memberCandidateRowsRead++;
                var fileId = reader.GetInt64(1);
                // Purge plans guarantee ascending IDs. Skip those rows before path-based
                // bookkeeping: deleted contracts must not make pending paths look changed.
                // purge plan は ID 昇順を保証する。削除対象 contract を pending path の
                // 変更扱いにしないよう、path based bookkeeping より先に除外する。
                if (FilePurgePlan.ContainsSortedFileId(excludedExistingFileIds, fileId))
                    continue;

                var path = reader.GetString(0);
                var signature = reader.IsDBNull(10) ? null : reader.GetString(10);
                // SQLite LIKE is only a cheap ASCII-case-insensitive candidate filter.
                // Keep the exact C# token-boundary check authoritative.
                // LIKE は候補絞り込み専用で、C# token 境界は managed 側で確定する。
                if (!CSharpStaticInterfacePrepass.IsCSharpStaticInterfaceContractSignature(signature))
                    continue;

                if (excludedPaths?.Contains(path) == true
                    || isExistingSymbolPathExcluded?.Invoke(path) == true)
                {
                    excludedPathsHaveContracts = true;
                    continue;
                }

                var symbol = ReadCSharpContractWorkspaceSymbol(reader);
                symbols.Add(symbol);
                exactMembersRetained++;
                if (!string.IsNullOrWhiteSpace(symbol.ContainerName))
                    retainedContractContainerNames.Add(symbol.ContainerName);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException(
                "C# static-interface contract workspace member read was interrupted.",
                ex,
                cancellationToken);
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        // Generic interface parameter names are needed only for types that actually own a
        // retained static contract. Resolve those declarations by bounded name batches and
        // keep the dynamic SQL shapes out of PreparedCommandCache.
        // retained contract を持つ型の interface 宣言だけを name batch で取得する。
        try
        {
            if (retainedContractContainerNames.Count > 0)
            {
                var containerNames = retainedContractContainerNames.ToArray();
                for (var offset = 0; offset < containerNames.Length; offset += DeleteFilesBatchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batchCount = Math.Min(DeleteFilesBatchSize, containerNames.Length - offset);
                    var sql = BuildCSharpStaticInterfaceDeclarationWorkspaceSql(batchCount);
                    using var interfaceCommand = _conn.CreateCommand();
                    interfaceCommand.Transaction = _activeTransaction;
                    interfaceCommand.CommandText = sql;
                    for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
                    {
                        interfaceCommand.Parameters.Add(
                            SqliteDynamicSql.BuildParameterName("containerName", parameterIndex),
                            SqliteType.Text).Value = containerNames[offset + parameterIndex];
                    }

                    interfaceDeclarationBatchQueries++;
                    using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
                    using var reader = interfaceCommand.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        interfaceDeclarationRowsRead++;
                        var fileId = reader.GetInt64(1);
                        if (FilePurgePlan.ContainsSortedFileId(excludedExistingFileIds, fileId))
                            continue;

                        var path = reader.GetString(0);
                        if (excludedPaths?.Contains(path) == true
                            || isExistingSymbolPathExcluded?.Invoke(path) == true)
                        {
                            continue;
                        }

                        symbols.Add(ReadCSharpContractWorkspaceSymbol(reader));
                    }
                }
            }
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException(
                "C# static-interface contract workspace interface read was interrupted.",
                ex,
                cancellationToken);
        }

        AppendCSharpMemberReadTargetSymbols(
            symbols,
            excludedPaths,
            excludedExistingFileIds,
            isExistingSymbolPathExcluded,
            out excludedPathsHaveMemberReadTargets,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CSharpContractWorkspaceReadStatsForTesting?.Invoke(
            new CSharpContractWorkspaceReadStats(
                memberCandidateRowsRead,
                exactMembersRetained,
                interfaceDeclarationRowsRead,
                interfaceDeclarationBatchQueries));

        return symbols;
    }

    private void AppendCSharpMemberReadTargetSymbols(
        List<SymbolRecord> symbols,
        IReadOnlySet<string>? excludedPaths,
        IReadOnlyList<long>? excludedExistingFileIds,
        Func<string, bool>? isExistingSymbolPathExcluded,
        out bool excludedPathsHaveMemberReadTargets,
        CancellationToken cancellationToken)
    {
        excludedPathsHaveMemberReadTargets = false;
        cancellationToken.ThrowIfCancellationRequested();
        var cmd = RentCommand(CSharpMemberReadTargetWorkspaceSql, static _ => { });
        try
        {
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var symbol = ReadCSharpContractWorkspaceSymbol(reader);
                if (!ReferenceExtractor.IsCSharpQualifiedMemberReadTargetSymbol(symbol))
                    continue;

                var fileId = reader.GetInt64(1);
                if (FilePurgePlan.ContainsSortedFileId(excludedExistingFileIds, fileId))
                {
                    excludedPathsHaveMemberReadTargets = true;
                    continue;
                }

                var path = reader.GetString(0);
                if (excludedPaths?.Contains(path) == true
                    || isExistingSymbolPathExcluded?.Invoke(path) == true)
                {
                    excludedPathsHaveMemberReadTargets = true;
                    continue;
                }

                symbols.Add(symbol);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException(
                "C# member-read target workspace read was interrupted.",
                ex,
                cancellationToken);
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    internal bool HasCSharpMemberReadTargetSymbolsInFileIds(
        IReadOnlyList<long> sortedFileIds,
        CancellationToken cancellationToken = default)
    {
        if (sortedFileIds.Count == 0)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        for (var offset = 0; offset < sortedFileIds.Count; offset += DeleteFilesBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchCount = Math.Min(DeleteFilesBatchSize, sortedFileIds.Count - offset);
            SqliteDynamicSql.EnsureParameterBudget(batchCount, "C# member-read target file-id preflight batch");
            var parameterList = SqliteDynamicSql.BuildParameterList("fileId", batchCount);
            var sql = @"
                SELECT " + CSharpContractWorkspaceProjectionSql + @"
                FROM files f
                CROSS JOIN symbols s INDEXED BY idx_symbols_file_kind
                  ON s.file_id = f.id
                WHERE f.lang = 'csharp'
                  AND s.file_id IN (" + parameterList + @")
                  AND " + CSharpMemberReadTargetPredicateSql;
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = _activeTransaction;
            cmd.CommandText = sql;
            for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
            {
                cmd.Parameters.Add(
                    SqliteDynamicSql.BuildParameterName("fileId", parameterIndex),
                    SqliteType.Integer).Value = sortedFileIds[offset + parameterIndex];
            }

            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReferenceExtractor.IsCSharpQualifiedMemberReadTargetSymbol(
                        ReadCSharpContractWorkspaceSymbol(reader)))
                {
                    return true;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    internal bool HasCSharpMemberReadTargetSymbolsInPaths(
        IReadOnlySet<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        var pathArray = paths.ToArray();
        for (var offset = 0; offset < pathArray.Length; offset += DeleteFilesBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchCount = Math.Min(DeleteFilesBatchSize, pathArray.Length - offset);
            SqliteDynamicSql.EnsureParameterBudget(batchCount, "C# member-read target path preflight batch");
            var parameterList = SqliteDynamicSql.BuildParameterList("path", batchCount);
            var sql = @"
                SELECT " + CSharpContractWorkspaceProjectionSql + @"
                FROM files f
                CROSS JOIN symbols s INDEXED BY idx_symbols_file_kind
                  ON s.file_id = f.id
                WHERE f.path IN (" + parameterList + @")
                  AND f.lang = 'csharp'
                  AND " + CSharpMemberReadTargetPredicateSql;
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = _activeTransaction;
            cmd.CommandText = sql;
            for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
            {
                cmd.Parameters.Add(
                    SqliteDynamicSql.BuildParameterName("path", parameterIndex),
                    SqliteType.Text).Value = pathArray[offset + parameterIndex];
            }

            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReferenceExtractor.IsCSharpQualifiedMemberReadTargetSymbol(
                        ReadCSharpContractWorkspaceSymbol(reader)))
                {
                    return true;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    internal static string BuildCSharpStaticInterfaceDeclarationWorkspaceSql(int batchCount)
    {
        SqliteDynamicSql.EnsureParameterBudget(
            batchCount,
            "C# contract interface-declaration workspace batch");
        var parameterList = SqliteDynamicSql.BuildParameterList("containerName", batchCount);
        return @"
            SELECT " + CSharpContractWorkspaceProjectionSql + @"
            FROM symbols s INDEXED BY idx_symbols_name
            CROSS JOIN files f ON f.id = s.file_id
            WHERE s.name IN (" + parameterList + @")
              AND s.kind = 'interface'
              AND f.lang = 'csharp'";
    }

    private static SymbolRecord ReadCSharpContractWorkspaceSymbol(SqliteDataReader reader)
        => new()
        {
            FileId = reader.GetInt64(1),
            Kind = reader.GetString(2),
            Name = reader.GetString(3),
            Line = reader.GetInt32(4),
            StartLine = reader.GetInt32(5),
            StartColumn = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            EndLine = reader.GetInt32(7),
            BodyStartLine = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            BodyEndLine = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            Signature = reader.IsDBNull(10) ? null : reader.GetString(10),
            ContainerKind = reader.IsDBNull(11) ? null : reader.GetString(11),
            ContainerName = reader.IsDBNull(12) ? null : reader.GetString(12),
            ContainerQualifiedName = reader.IsDBNull(13) ? null : reader.GetString(13),
            FamilyKey = reader.IsDBNull(14) ? null : reader.GetString(14),
            Visibility = reader.IsDBNull(15) ? null : reader.GetString(15),
            ReturnType = reader.IsDBNull(16) ? null : reader.GetString(16),
            IsMetadataTarget = reader.IsDBNull(17) ? null : reader.GetInt32(17) != 0,
        };

    public bool HasCSharpStaticInterfaceContractSymbols(CancellationToken cancellationToken = default)
        => HasCSharpStaticInterfaceContracts(
            CSharpStaticInterfaceContractWhereSql,
            cancellationToken);

    internal bool HasCSharpStaticInterfaceContractMembers(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CSharpContractPreflightForTesting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        // SQLite performs a broad candidate prefilter and managed code applies the
        // exact C# token-boundary predicate. SQLite LIKE is ASCII case-insensitive.
        // SQLiteで広く候補を絞り、managed codeで正確なC# token境界を判定する。
        var sql = @"
            SELECT s.signature
            FROM symbols s
            JOIN files f ON f.id = s.file_id" + CSharpStaticInterfaceContractMemberWhereSql;

        try
        {
            return HasExactCSharpStaticInterfaceContractMember(
                sql,
                configureParameterSchema: static _ => { },
                bindParameterValues: null,
                cancellationToken);
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("C# static-interface contract preflight was interrupted.", ex, cancellationToken);
        }
    }

    internal bool HasCSharpStaticInterfaceContractMembersInFileIds(
        IReadOnlyList<long> sortedFileIds,
        CancellationToken cancellationToken = default)
        => HasCSharpStaticInterfaceContractMembersInFileIds(
            sortedFileIds,
            includeInterfaceDeclarationsAsConservativeEvidence: false,
            cancellationToken);

    internal bool HasCSharpStaticInterfaceContractMembersInFileIds(
        IReadOnlyList<long> sortedFileIds,
        bool includeInterfaceDeclarationsAsConservativeEvidence,
        CancellationToken cancellationToken = default)
    {
        if (sortedFileIds.Count == 0)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        CSharpContractPreflightForTesting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            for (var offset = 0; offset < sortedFileIds.Count; offset += DeleteFilesBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchCount = Math.Min(DeleteFilesBatchSize, sortedFileIds.Count - offset);
                SqliteDynamicSql.EnsureParameterBudget(batchCount, "C# contract file-id preflight batch");
                var parameterList = SqliteDynamicSql.BuildParameterList("fileId", batchCount);
                var sql = @"
                    SELECT s.kind, s.signature
                    FROM symbols s
                    JOIN files f ON f.id = s.file_id"
                    + (includeInterfaceDeclarationsAsConservativeEvidence
                        ? CSharpStaticInterfaceContractWhereSql
                        : CSharpStaticInterfaceContractMemberWhereSql)
                    + $" AND s.file_id IN ({parameterList})";

                // Batch size changes the SQL shape. Keep these short-lived commands out of
                // PreparedCommandCache so a large purge cannot retain one command per tail size.
                // batch sizeでSQL shapeが変わるため、一時commandをprepared cacheへ残さない。
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = _activeTransaction;
                cmd.CommandText = sql;
                for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
                {
                    cmd.Parameters.Add(
                        SqliteDynamicSql.BuildParameterName("fileId", parameterIndex),
                        SqliteType.Integer).Value = sortedFileIds[offset + parameterIndex];
                }

                using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var kind = reader.GetString(0);
                    if (includeInterfaceDeclarationsAsConservativeEvidence && kind == "interface")
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return true;
                    }

                    var signature = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (CSharpStaticInterfacePrepass.IsCSharpStaticInterfaceContractSignature(signature))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return true;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("C# static-interface contract file-id preflight was interrupted.", ex, cancellationToken);
        }
    }

    private bool HasExactCSharpStaticInterfaceContractMember(
        string sql,
        Action<SqliteCommand> configureParameterSchema,
        Action<SqliteCommand>? bindParameterValues,
        CancellationToken cancellationToken)
    {
        var cmd = RentCommand(sql, configureParameterSchema);
        try
        {
            bindParameterValues?.Invoke(cmd);
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var signature = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (CSharpStaticInterfacePrepass.IsCSharpStaticInterfaceContractSignature(signature))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return true;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private bool HasCSharpStaticInterfaceContracts(
        string whereSql,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CSharpContractPreflightForTesting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        // Preflight callers need only a presence bit, so let SQLite stop at the first row.
        // preflight callerは存在判定だけでよいため、SQLite側で最初の1行で打ち切る。
        var sql = @"
            SELECT EXISTS(
                SELECT 1
                FROM symbols s
                JOIN files f ON f.id = s.file_id" + whereSql + @"
                LIMIT 1)";

        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var raw = cmd.ExecuteScalar();
            cancellationToken.ThrowIfCancellationRequested();
            return raw is long l ? l != 0 : raw is int i && i != 0;
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("C# static-interface contract preflight was interrupted.", ex, cancellationToken);
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public bool HasCSharpStaticInterfaceContractSymbolsInPaths(IReadOnlySet<string> paths)
        => HasCSharpStaticInterfaceContractSymbolsInPaths(
            paths,
            includeInterfaceDeclarationsAsConservativeEvidence: false,
            cancellationToken: default);

    internal bool HasCSharpStaticInterfaceContractSymbolsInPaths(
        IReadOnlySet<string> paths,
        bool includeInterfaceDeclarationsAsConservativeEvidence,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        CSharpContractWorkspaceReadForTesting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var pathArray = paths.ToArray();
        try
        {
            for (var offset = 0; offset < pathArray.Length; offset += DeleteFilesBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchCount = Math.Min(DeleteFilesBatchSize, pathArray.Length - offset);
                var sql = BuildCSharpStaticInterfaceContractPathPreflightSql(
                    batchCount,
                    includeInterfaceDeclarationsAsConservativeEvidence);
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = _activeTransaction;
                cmd.CommandText = sql;
                for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
                {
                    cmd.Parameters.Add(
                        SqliteDynamicSql.BuildParameterName("path", parameterIndex),
                        SqliteType.Text).Value = pathArray[offset + parameterIndex];
                }

                // Batch-size SQL shapes are deliberately short-lived so a large path set does
                // not populate PreparedCommandCache with one entry per tail size. CROSS JOIN
                // pins the bounded files(path) lookup before symbols(file_id, kind).
                // batch size ごとの SQL shape は prepared cache に残さず、CROSS JOIN で
                // bounded な files(path) lookup を symbols(file_id, kind) より先に固定する。
                using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var kind = reader.GetString(0);
                    if ((includeInterfaceDeclarationsAsConservativeEvidence && kind == "interface")
                        || CSharpStaticInterfacePrepass.IsCSharpStaticInterfaceContractSignature(
                            reader.IsDBNull(1) ? null : reader.GetString(1)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return true;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("C# static-interface contract path preflight was interrupted.", ex, cancellationToken);
        }
        return false;
    }

    internal static string BuildCSharpStaticInterfaceContractPathPreflightSql(
        int batchCount,
        bool includeInterfaceDeclarationsAsConservativeEvidence)
    {
        SqliteDynamicSql.EnsureParameterBudget(
            batchCount,
            "C# contract path preflight batch");
        var parameterList = SqliteDynamicSql.BuildParameterList("path", batchCount);
        var predicate = includeInterfaceDeclarationsAsConservativeEvidence
            ? CSharpStaticInterfaceContractPredicateSql
            : CSharpStaticInterfaceContractMemberPredicateSql;
        return $@"
            SELECT s.kind, s.signature
            FROM files f
            CROSS JOIN symbols s INDEXED BY idx_symbols_file_kind
              ON s.file_id = f.id
            WHERE f.path IN ({parameterList})
              AND f.lang = 'csharp'
              AND {predicate}";
    }

    internal HashSet<string> ResolveCSharpFilePaths(
        IReadOnlySet<string> candidatePaths,
        CancellationToken cancellationToken = default)
        => LookupCSharpFilesInPaths(candidatePaths, cancellationToken).Paths;

    internal FilePurgePlan PlanCSharpFilesInPaths(
        IReadOnlySet<string> candidatePaths,
        CancellationToken cancellationToken = default)
        => LookupCSharpFilesInPaths(candidatePaths, cancellationToken).PurgePlan;

    private CSharpFilesInPathsLookup LookupCSharpFilesInPaths(
        IReadOnlySet<string> candidatePaths,
        CancellationToken cancellationToken)
    {
        var csharpPaths = new HashSet<string>(StringComparer.Ordinal);
        if (candidatePaths.Count == 0)
            return new CSharpFilesInPathsLookup(csharpPaths, FilePurgePlan.Empty);

        cancellationToken.ThrowIfCancellationRequested();
        var pathArray = candidatePaths.ToArray();
        var fileIds = new List<long>(Math.Min(candidatePaths.Count, DeleteFilesBatchSize));
        long deletedBytes = 0;
        var byteEstimateComplete = true;
        try
        {
            for (var offset = 0; offset < pathArray.Length; offset += DeleteFilesBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchCount = Math.Min(DeleteFilesBatchSize, pathArray.Length - offset);

                // The IN-list shape changes with the tail batch. Keep this bounded point
                // lookup out of PreparedCommandCache and return every matched path at once,
                // so callers do not issue one SQLite command per candidate.
                // IN-list の tail ごとに SQL shape が変わるため prepared cache へ残さず、
                // candidate ごとの SQLite query を避けて一致 path をまとめて返す。
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = _activeTransaction;
                cmd.CommandText = BuildCSharpFilePathLookupSql(batchCount);
                for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
                {
                    cmd.Parameters.Add(
                        SqliteDynamicSql.BuildParameterName("path", parameterIndex),
                        SqliteType.Text).Value = pathArray[offset + parameterIndex];
                }

                using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fileIds.Add(reader.GetInt64(0));
                    csharpPaths.Add(reader.GetString(1));
                    var size = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                    if (!size.HasValue
                        || size.Value < 0
                        || deletedBytes > long.MaxValue - size.Value)
                    {
                        byteEstimateComplete = false;
                    }
                    else
                    {
                        deletedBytes += size.Value;
                    }
                }

                CSharpFilePathLookupBatchCompletedForTesting?.Invoke(offset + batchCount);
                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
            fileIds.Sort();
            return new CSharpFilesInPathsLookup(
                csharpPaths,
                new FilePurgePlan(
                    fileIds.AsReadOnly(),
                    deletedBytes,
                    byteEstimateComplete,
                    RemainingFileCount: 0));
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException(
                "C# file-language path lookup was interrupted.",
                ex,
                cancellationToken);
        }
    }

    internal static string BuildCSharpFilePathLookupSql(int batchCount)
    {
        SqliteDynamicSql.EnsureParameterBudget(
            batchCount,
            "C# file-language path lookup batch");
        var parameterList = SqliteDynamicSql.BuildParameterList("path", batchCount);
        return $@"
            SELECT id, path, size
            FROM files
            WHERE path IN ({parameterList})
              AND lang = 'csharp'";
    }

    internal bool HasCSharpFilesInPaths(
        IReadOnlySet<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        var pathArray = paths.ToArray();
        try
        {
            for (var offset = 0; offset < pathArray.Length; offset += DeleteFilesBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchCount = Math.Min(DeleteFilesBatchSize, pathArray.Length - offset);
                SqliteDynamicSql.EnsureParameterBudget(batchCount, "C# file-language path preflight batch");
                var parameterList = SqliteDynamicSql.BuildParameterList("path", batchCount);

                // Tail batch sizes change the SQL shape. Keep these point lookups out of
                // PreparedCommandCache so a large scoped update cannot retain every shape.
                // tail batch sizeでSQL shapeが変わるため、一時commandをcacheへ残さない。
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = _activeTransaction;
                cmd.CommandText = $@"
                    SELECT 1
                    FROM files
                    WHERE lang = 'csharp'
                      AND path IN ({parameterList})
                    LIMIT 1";
                for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
                {
                    cmd.Parameters.Add(
                        SqliteDynamicSql.BuildParameterName("path", parameterIndex),
                        SqliteType.Text).Value = pathArray[offset + parameterIndex];
                }

                using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (cmd.ExecuteScalar() != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return true;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("C# file-language path preflight was interrupted.", ex, cancellationToken);
        }
    }

    internal bool HasCSharpFilesInFileIds(
        IReadOnlyList<long> sortedFileIds,
        CancellationToken cancellationToken = default)
    {
        if (sortedFileIds.Count == 0)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            for (var offset = 0; offset < sortedFileIds.Count; offset += DeleteFilesBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchCount = Math.Min(DeleteFilesBatchSize, sortedFileIds.Count - offset);
                SqliteDynamicSql.EnsureParameterBudget(batchCount, "C# file-language ID preflight batch");
                var parameterList = SqliteDynamicSql.BuildParameterList("fileId", batchCount);

                // Tail batch sizes change the SQL shape. Keep these point lookups out of
                // PreparedCommandCache so a large plan cannot retain every shape.
                // tail batch sizeでSQL shapeが変わるため、一時commandをcacheへ残さない。
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = _activeTransaction;
                cmd.CommandText = $@"
                    SELECT 1
                    FROM files
                    WHERE lang = 'csharp'
                      AND id IN ({parameterList})
                    LIMIT 1";
                for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
                {
                    cmd.Parameters.Add(
                        SqliteDynamicSql.BuildParameterName("fileId", parameterIndex),
                        SqliteType.Integer).Value = sortedFileIds[offset + parameterIndex];
                }

                using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (cmd.ExecuteScalar() != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return true;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException(
                "C# file-language ID preflight was interrupted.",
                ex,
                cancellationToken);
        }
    }

    internal bool HasCSharpFileMatchingPath(
        Func<string, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        cancellationToken.ThrowIfCancellationRequested();

        const string sql = "SELECT path FROM files WHERE lang = 'csharp'";
        var command = RentCommand(sql, static _ => { });
        try
        {
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (predicate(reader.GetString(0)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return true;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException(
                "C# file-language transition preflight was interrupted.",
                ex,
                cancellationToken);
        }
        finally
        {
            ReleaseCommand(command);
        }
    }
}
