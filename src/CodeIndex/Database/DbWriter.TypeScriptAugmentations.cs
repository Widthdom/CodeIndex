using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbWriter
{
    internal sealed record TypeScriptAugmentationGroupingStats(
        int DeclarationCount,
        int GroupCount,
        int MergedGroupCount,
        int MaterializedDeclarationIndexCount,
        int? ScopedNameCount = null);

    internal sealed class TypeScriptAugmentationDirtyNameScope : IDisposable
    {
        private readonly DbWriter _owner;
        private readonly bool _collectDirtyNames;
        private readonly HashSet<string> _dirtyNames = new(StringComparer.Ordinal);
        private readonly HashSet<long> _currentTypeScriptFileIds = [];
        private bool _augmentationReadyMayBeCurrent = true;
        private bool _disposed;

        internal TypeScriptAugmentationDirtyNameScope(DbWriter owner, bool collectDirtyNames)
        {
            _owner = owner;
            _collectDirtyNames = collectDirtyNames;
        }

        internal IReadOnlyCollection<string> DirtyNames => _dirtyNames;
        internal bool RequiresRefresh { get; private set; }

        internal bool TrackExistingFile(string path)
        {
            if (!_owner.AddTypeScriptInterfaceNamesAtPath(
                    path,
                    _collectDirtyNames ? _dirtyNames : null,
                    out var augmentationReady))
                return false;

            RequiresRefresh = true;
            if (augmentationReady)
            {
                TypeScriptAugmentationReadyClearForTesting?.Invoke();
                _owner.ClearTypeScriptAugmentationReady();
            }
            _augmentationReadyMayBeCurrent = false;
            return true;
        }

        internal void TrackDeletedFiles(IReadOnlyList<long> fileIds)
        {
            if (!_owner.AddTypeScriptInterfaceNamesForFiles(
                    fileIds,
                    _collectDirtyNames ? _dirtyNames : null,
                    out var augmentationReady))
                return;

            RequiresRefresh = true;
            if (augmentationReady)
            {
                TypeScriptAugmentationReadyClearForTesting?.Invoke();
                _owner.ClearTypeScriptAugmentationReady();
            }
            _augmentationReadyMayBeCurrent = false;
        }

        internal void TrackCurrentFile(long fileId, string? language, bool wasExistingTypeScript = false)
        {
            if (string.Equals(language, "typescript", StringComparison.Ordinal))
            {
                if (_collectDirtyNames)
                    _currentTypeScriptFileIds.Add(fileId);
                if (!wasExistingTypeScript)
                {
                    RequiresRefresh = true;
                    if (_augmentationReadyMayBeCurrent)
                    {
                        TypeScriptAugmentationReadyCheckForTesting?.Invoke();
                        if (_owner.TypeScriptAugmentationVersionMatchesCurrent())
                        {
                            TypeScriptAugmentationReadyClearForTesting?.Invoke();
                            _owner.ClearTypeScriptAugmentationReady();
                        }
                        _augmentationReadyMayBeCurrent = false;
                    }
                }
            }
            else if (_collectDirtyNames)
                _currentTypeScriptFileIds.Remove(fileId);
        }

        internal void OnTransactionRolledBack() => _augmentationReadyMayBeCurrent = true;

        internal void TrackInsertedSymbols(
            IReadOnlyList<SymbolRecord> symbols,
            CancellationToken cancellationToken)
        {
            if (!_collectDirtyNames || symbols.Count == 0 || _currentTypeScriptFileIds.Count == 0)
                return;

            for (var symbolIndex = 0; symbolIndex < symbols.Count; symbolIndex++)
            {
                if ((symbolIndex & 1_023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var symbol = symbols[symbolIndex];
                if (_currentTypeScriptFileIds.Contains(symbol.FileId)
                    && string.Equals(symbol.Kind, "interface", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(symbol.Name))
                {
                    _dirtyNames.Add(symbol.Name);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (!ReferenceEquals(_owner._typeScriptAugmentationDirtyNameScope, this))
                throw new InvalidOperationException("TypeScript augmentation dirty-name scopes must be disposed in ownership order.");
            _owner._typeScriptAugmentationDirtyNameScope = null;
        }
    }

    private TypeScriptAugmentationDirtyNameScope? _typeScriptAugmentationDirtyNameScope;

    private void NotifyTypeScriptAugmentationTransactionRolledBack() =>
        _typeScriptAugmentationDirtyNameScope?.OnTransactionRolledBack();

    internal TypeScriptAugmentationDirtyNameScope BeginTypeScriptAugmentationDirtyNameTracking(
        bool collectDirtyNames = true)
    {
        if (_typeScriptAugmentationDirtyNameScope != null)
            throw new InvalidOperationException("A TypeScript augmentation dirty-name scope is already active.");

        var scope = new TypeScriptAugmentationDirtyNameScope(this, collectDirtyNames);
        _typeScriptAugmentationDirtyNameScope = scope;
        return scope;
    }

    private static readonly AsyncLocal<Action<TypeScriptAugmentationGroupingStats>?> ScopedTypeScriptAugmentationGroupingForTesting = new();
    private static readonly AsyncLocal<Action<int>?> ScopedTypeScriptAugmentationNameBatchForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedTypeScriptAugmentationReadyClearForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedTypeScriptAugmentationReadyCheckForTesting = new();
    internal static Action<TypeScriptAugmentationGroupingStats>? TypeScriptAugmentationGroupingForTesting
    {
        get => ScopedTypeScriptAugmentationGroupingForTesting.Value;
        set => ScopedTypeScriptAugmentationGroupingForTesting.Value = value;
    }
    internal static Action<int>? TypeScriptAugmentationNameBatchForTesting
    {
        get => ScopedTypeScriptAugmentationNameBatchForTesting.Value;
        set => ScopedTypeScriptAugmentationNameBatchForTesting.Value = value;
    }
    internal static Action? TypeScriptAugmentationReadyClearForTesting
    {
        get => ScopedTypeScriptAugmentationReadyClearForTesting.Value;
        set => ScopedTypeScriptAugmentationReadyClearForTesting.Value = value;
    }
    internal static Action? TypeScriptAugmentationReadyCheckForTesting
    {
        get => ScopedTypeScriptAugmentationReadyCheckForTesting.Value;
        set => ScopedTypeScriptAugmentationReadyCheckForTesting.Value = value;
    }

    public int RebuildTypeScriptAugmentationReferences(string? projectRoot = null) =>
        RebuildTypeScriptAugmentationReferencesCore(
            projectRoot,
            dirtyNames: null,
            finalizeDeferredReferenceGraph: false,
            referenceSecondaryIndexBulkLoad: null);

    internal int RebuildTypeScriptAugmentationReferences(
        string projectRoot,
        IReadOnlyCollection<string> dirtyNames) =>
        RebuildTypeScriptAugmentationReferencesCore(
            projectRoot,
            dirtyNames,
            finalizeDeferredReferenceGraph: false,
            referenceSecondaryIndexBulkLoad: null,
            cancellationToken: CancellationToken.None);

    internal int RebuildTypeScriptAugmentationReferences(
        string projectRoot,
        IReadOnlyCollection<string>? dirtyNames,
        CancellationToken cancellationToken) =>
        RebuildTypeScriptAugmentationReferencesCore(
            projectRoot,
            dirtyNames,
            finalizeDeferredReferenceGraph: false,
            referenceSecondaryIndexBulkLoad: null,
            cancellationToken: cancellationToken);

    internal int RebuildTypeScriptAugmentationReferences(
        string projectRoot,
        IReadOnlyCollection<string>? dirtyNames,
        bool finalizeDeferredReferenceGraph,
        CancellationToken cancellationToken) =>
        RebuildTypeScriptAugmentationReferences(
            projectRoot,
            dirtyNames,
            finalizeDeferredReferenceGraph,
            referenceSecondaryIndexBulkLoad: null,
            cancellationToken: cancellationToken);

    internal int RebuildTypeScriptAugmentationReferences(
        string projectRoot,
        IReadOnlyCollection<string>? dirtyNames,
        bool finalizeDeferredReferenceGraph,
        ReferenceSecondaryIndexBulkLoadGuard? referenceSecondaryIndexBulkLoad,
        CancellationToken cancellationToken) =>
        RebuildTypeScriptAugmentationReferencesCore(
            projectRoot,
            dirtyNames,
            finalizeDeferredReferenceGraph,
            referenceSecondaryIndexBulkLoad,
            cancellationToken);

    private int RebuildTypeScriptAugmentationReferencesCore(
        string? projectRoot,
        IReadOnlyCollection<string>? dirtyNames,
        bool finalizeDeferredReferenceGraph,
        ReferenceSecondaryIndexBulkLoadGuard? referenceSecondaryIndexBulkLoad,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var ownedDeferredRefresh = _deferredHotspotReferenceRefresh == null
            ? BeginDeferredHotspotReferenceAggregateRefresh()
            : null;
        using var transaction = BeginTransaction(cancellationToken, "TypeScript augmentation rebuild");
        using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryStartDeferredHotspotReferenceMutation();
            var scopePlan = BuildTypeScriptAugmentationScopePlan(dirtyNames, cancellationToken);
            var affectedFileIds = new HashSet<long>();
            var deletedReferenceCount = DeleteAndTrackTypeScriptAugmentationReferences(
                scopePlan,
                affectedFileIds,
                cancellationToken);
            var declarations = LoadTypeScriptInterfaceDeclarations(scopePlan, cancellationToken);
            var references = ProjectTypeScriptAugmentationReferences(
                projectRoot,
                scopePlan,
                declarations,
                cancellationToken);
            ApplyTypeScriptAugmentationReferences(
                references,
                deletedReferenceCount,
                affectedFileIds,
                finalizeDeferredReferenceGraph,
                referenceSecondaryIndexBulkLoad,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            MarkTypeScriptAugmentationReady();
            ownedDeferredRefresh?.Complete(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return references.Count;
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
            when (IsSqliteInterruptCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(
                "TypeScript augmentation rebuild was interrupted.",
                exception,
                cancellationToken);
        }
    }

    private bool AddTypeScriptInterfaceNamesAtPath(
        string path,
        HashSet<string>? destination,
        out bool augmentationReady)
    {
        var cmd = RentCommand(
            destination == null
                ? @"
                    SELECT EXISTS (
                               SELECT 1
                               FROM codeindex_meta
                               WHERE key = @meta_key
                                 AND value = @meta_version)
                    FROM files
                    WHERE path = @path
                      AND lang = 'typescript'"
                : @"
                SELECT s.name,
                       EXISTS (
                           SELECT 1
                           FROM codeindex_meta
                           WHERE key = @meta_key
                             AND value = @meta_version)
                FROM files f
                LEFT JOIN symbols s
                  ON s.file_id = f.id
                 AND s.kind = 'interface'
                 AND s.name IS NOT NULL
                 AND s.name <> ''
                WHERE f.path = @path
                  AND f.lang = 'typescript'",
            static command =>
            {
                command.Parameters.Add("@path", Microsoft.Data.Sqlite.SqliteType.Text);
                command.Parameters.Add("@meta_key", Microsoft.Data.Sqlite.SqliteType.Text);
                command.Parameters.Add("@meta_version", Microsoft.Data.Sqlite.SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@path"].Value = path;
            cmd.Parameters["@meta_key"].Value = DbContext.TypeScriptAugmentationVersionMetaKey;
            cmd.Parameters["@meta_version"].Value =
                DbContext.TypeScriptAugmentationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var reader = cmd.ExecuteReader();
            var foundTypeScriptFile = false;
            augmentationReady = false;
            while (reader.Read())
            {
                foundTypeScriptFile = true;
                augmentationReady = reader.GetInt64(destination == null ? 0 : 1) != 0;
                if (destination != null && !reader.IsDBNull(0))
                    destination.Add(reader.GetString(0));
            }
            return foundTypeScriptFile;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private bool AddTypeScriptInterfaceNamesForFiles(
        IReadOnlyList<long> fileIds,
        HashSet<string>? destination,
        out bool augmentationReady)
    {
        const int fileBatchSize = 250;
        var foundTypeScriptFile = false;
        augmentationReady = false;
        for (var offset = 0; offset < fileIds.Count; offset += fileBatchSize)
        {
            var count = Math.Min(fileBatchSize, fileIds.Count - offset);
            SqliteDynamicSql.EnsureParameterBudget(count, "TypeScript augmentation deleted-file batch");
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = _activeTransaction;
            var parameterNames = new string[count];
            for (var index = 0; index < count; index++)
            {
                var parameterName = SqliteDynamicSql.BuildParameterName("typescript_file", index);
                parameterNames[index] = parameterName;
                cmd.Parameters.Add(parameterName, Microsoft.Data.Sqlite.SqliteType.Integer).Value = fileIds[offset + index];
            }
            cmd.Parameters.Add("@meta_key", Microsoft.Data.Sqlite.SqliteType.Text).Value =
                DbContext.TypeScriptAugmentationVersionMetaKey;
            cmd.Parameters.Add("@meta_version", Microsoft.Data.Sqlite.SqliteType.Text).Value =
                DbContext.TypeScriptAugmentationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            cmd.CommandText = destination == null
                ? $@"
                    SELECT f.id,
                           EXISTS (
                               SELECT 1
                               FROM codeindex_meta
                               WHERE key = @meta_key
                                 AND value = @meta_version)
                    FROM files f
                    WHERE f.id IN ({string.Join(", ", parameterNames)})
                      AND f.lang = 'typescript'"
                : $@"
                SELECT f.id, s.name,
                       EXISTS (
                           SELECT 1
                           FROM codeindex_meta
                           WHERE key = @meta_key
                             AND value = @meta_version)
                FROM files f
                LEFT JOIN symbols s
                  ON s.file_id = f.id
                 AND s.kind = 'interface'
                 AND s.name IS NOT NULL
                 AND s.name <> ''
                WHERE f.id IN ({string.Join(", ", parameterNames)})
                  AND f.lang = 'typescript'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                foundTypeScriptFile = true;
                augmentationReady = reader.GetInt64(destination == null ? 1 : 2) != 0;
                if (destination != null && !reader.IsDBNull(1))
                    destination.Add(reader.GetString(1));
            }
        }
        return foundTypeScriptFile;
    }

    public void MarkTypeScriptAugmentationReady()
    {
        SetMeta(
            DbContext.TypeScriptAugmentationVersionMetaKey,
            DbContext.TypeScriptAugmentationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private HashSet<long> FindTypeScriptModuleFileIds(
        string? projectRoot,
        IReadOnlyList<TypeScriptInterfaceDeclaration> declarations,
        bool includeIndexedInterfaceMarkers,
        CancellationToken cancellationToken)
    {
        var moduleFileIds = new HashSet<long>();
        var scopedFileIds = includeIndexedInterfaceMarkers ? new HashSet<long>() : null;
        var pathsByFileId = string.IsNullOrWhiteSpace(projectRoot)
            ? null
            : new Dictionary<long, string>(Math.Min(declarations.Count, 4_096));
        for (var declarationIndex = 0; declarationIndex < declarations.Count; declarationIndex++)
        {
            if ((declarationIndex & 1_023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var declaration = declarations[declarationIndex];
            scopedFileIds?.Add(declaration.FileId);
            pathsByFileId?.TryAdd(declaration.FileId, declaration.Path);
            if (declaration.Visibility == "export"
                || declaration.Signature.StartsWith("export ", StringComparison.Ordinal)
                || declaration.Signature.StartsWith("import ", StringComparison.Ordinal))
            {
                moduleFileIds.Add(declaration.FileId);
            }
        }

        if (scopedFileIds != null)
            AddTypeScriptModuleFileIdsFromIndexedInterfaces(scopedFileIds, moduleFileIds, cancellationToken);

        if (pathsByFileId == null)
            return moduleFileIds;

        var fileIndex = 0;
        foreach (var file in pathsByFileId)
        {
            if ((fileIndex++ & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (moduleFileIds.Contains(file.Key))
                continue;

            var absolutePath = Path.Combine(projectRoot!, file.Value.Replace('/', Path.DirectorySeparatorChar));
            if (TypeScriptFileHasModuleSyntax(absolutePath))
                moduleFileIds.Add(file.Key);
        }
        cancellationToken.ThrowIfCancellationRequested();

        return moduleFileIds;
    }

    private void AddTypeScriptModuleFileIdsFromIndexedInterfaces(
        IReadOnlyCollection<long> fileIds,
        HashSet<long> moduleFileIds,
        CancellationToken cancellationToken)
    {
        const int fileBatchSize = 900;
        var pendingFileIds = new List<long>(fileIds.Count);
        var inspectedFileCount = 0;
        foreach (var fileId in fileIds)
        {
            if (!moduleFileIds.Contains(fileId))
                pendingFileIds.Add(fileId);
            if ((++inspectedFileCount & 1_023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }
        cancellationToken.ThrowIfCancellationRequested();
        for (var offset = 0; offset < pendingFileIds.Count; offset += fileBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(fileBatchSize, pendingFileIds.Count - offset);
            SqliteDynamicSql.EnsureParameterBudget(count, "TypeScript augmentation module-marker batch");
            using var command = _conn.CreateCommand();
            command.Transaction = _activeTransaction;
            var parameterNames = new string[count];
            for (var index = 0; index < count; index++)
            {
                var parameterName = SqliteDynamicSql.BuildParameterName("typescript_module_file", index);
                parameterNames[index] = parameterName;
                command.Parameters.Add(parameterName, Microsoft.Data.Sqlite.SqliteType.Integer).Value =
                    pendingFileIds[offset + index];
            }
            command.CommandText = $@"
                SELECT DISTINCT file_id
                FROM symbols INDEXED BY idx_symbols_file_kind
                WHERE file_id IN ({string.Join(", ", parameterNames)})
                  AND kind = 'interface'
                  AND (
                      visibility = 'export'
                      OR signature LIKE 'export %'
                      OR signature LIKE 'import %')";
            using var reader = command.ExecuteReader();
            var rowCount = 0;
            while (reader.Read())
            {
                moduleFileIds.Add(reader.GetInt64(0));
                if ((++rowCount & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string BuildTypeScriptScopeKey(long fileId, string path, string signature, string containerName, HashSet<long> moduleFileIds)
    {
        if (!moduleFileIds.Contains(fileId))
            return "global:" + containerName;
        if (containerName == "global" || signature.StartsWith("declare global ", StringComparison.Ordinal))
            return "global:";
        if (IsAmbientModuleContainer(containerName))
            return "ambient-module:" + containerName;
        return "module:" + path + ":" + containerName;
    }

    private static bool IsAmbientModuleContainer(string containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            return false;
        return containerName[0] is '"' or '\'' || containerName.Contains('/', StringComparison.Ordinal);
    }

    private static bool TypeScriptFileHasModuleSyntax(string absolutePath)
    {
        try
        {
            var content = DataDirectorySecurity.ReadTextWithinLimit(
                absolutePath,
                TypeScriptModuleSyntaxFallbackMaxBytes,
                FileShare.ReadWrite | FileShare.Delete);
            if (content is null)
                return false;

            var lineCount = 0;
            foreach (var line in EnumerateLines(content))
            {
                lineCount++;
                if (lineCount > TypeScriptModuleSyntaxFallbackMaxLines)
                    return false;

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("import ", StringComparison.Ordinal)
                    || trimmed.StartsWith("export ", StringComparison.Ordinal)
                    || trimmed.StartsWith("export{", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (IsExpectedTypeScriptFallbackReadException(ex)) { }

        return false;
    }

    internal static bool TypeScriptFileHasModuleSyntaxForTests(string absolutePath) =>
        TypeScriptFileHasModuleSyntax(absolutePath);

    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                yield return text[start..].TrimEnd('\r');
                yield break;
            }

            yield return text[start..newline].TrimEnd('\r');
            start = newline + 1;
        }
    }

    private static bool IsExpectedTypeScriptFallbackReadException(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;
}
