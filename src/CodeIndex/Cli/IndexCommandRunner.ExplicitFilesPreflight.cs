using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    internal const int ExplicitFilesRejectedPathLimit = 50;
    private const int ExplicitFilesIndexedPathQueryBatchSize = 256;
    private const int ExplicitFilesDiagnosticPathCharLimit = 240;
    private const string OutsideProjectRootPathPlaceholder = "<outside-project-root>";
    private const string SymlinkOutsideProjectRootPathPlaceholder = "<symlink-outside-project-root>";

    private sealed record ExplicitFileRejection(int InputIndex, string Path, string Reason);

    private sealed record ExplicitFilesIndexedPathSnapshot(
        IReadOnlySet<string> Paths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> CaseFoldedPaths,
        bool ReadFailed)
    {
        internal long? FileSizeLimit { get; init; }

        internal static ExplicitFilesIndexedPathSnapshot Empty()
            => CreateExplicitFilesIndexedPathSnapshot([], readFailed: false);

        internal static ExplicitFilesIndexedPathSnapshot ReadFailure()
            => CreateExplicitFilesIndexedPathSnapshot([], readFailed: true);
    }

    internal readonly record struct ExplicitFilesIndexedPathLookupMetrics(
        int ExactQueryCount,
        int CaseFoldScanCount,
        long CaseFoldScannedRowCount);

    internal sealed record ExplicitFilesIndexedPathLookupResult(
        IReadOnlySet<string> Paths,
        ExplicitFilesIndexedPathLookupMetrics Metrics);

    private static int? RunExplicitFilesPreflight(
        IndexCommandOptions options,
        string resolvedDbPath,
        bool ignoreCase,
        string ignoreRuleRoot,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        bool writerLockHeld,
        ExplicitFilesIndexedPathSnapshot? providedIndexedPaths = null)
    {
        var explicitFileInputs = options.ExplicitFileInputs;
        if (explicitFileInputs.Count == 0)
            return null;

        var projectRoot = Path.GetFullPath(options.ProjectPath!);
        var canonicalProjectRoot = FileIndexer.NormalizePathForIdentityComparison(projectRoot);
        var indexedPathCandidates = CollectExplicitFilesIndexedPathCandidates(
            projectRoot,
            explicitFileInputs);
        var indexedPaths = providedIndexedPaths;
        if (indexedPaths == null)
        {
            indexedPaths = ReadExplicitFilesIndexedPathSnapshot(
                resolvedDbPath,
                    indexedPathCandidates,
                    cancellationToken,
                    writerLockHeld,
                    options.MaxFileSizeBytes);
        }
        if (indexedPaths.ReadFailed && indexedPathCandidates.Count > 0)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                "the existing index could not be read during --files validation; no database writes were performed",
                CommandExitCodes.DatabaseError,
                "Verify that the index database is readable and not locked, then retry.",
                errorCode: CommandErrorCodes.DbError);
        }

        options = options.WithResolvedFileSizeLimit(
            indexedPaths.FileSizeLimit ?? IndexedFileSizePolicy.Resolve(null, options.MaxFileSizeBytes));
        var indexer = new FileIndexer(
            projectRoot,
            ignoreCase,
            ignoreRuleRoot,
            options.MaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: options.SymlinkPolicy,
            generatedCodePatterns: options.GeneratedCodePatterns,
            internalIndexDatabasePath: resolvedDbPath);
        // Native canonicalization preserves distinct paths on case-sensitive targets while
        // collapsing ordinary case aliases on case-insensitive targets. Keep a small
        // case-insensitive candidate bucket, then ask the target filesystem only when two
        // canonical spellings differ by case. A workspace-wide comparer is incorrect for
        // nested mounts and --follow-symlinks=all targets on another filesystem.
        var canonicalSelections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var normalizedExecutionPaths = new string?[explicitFileInputs.Count];
        var rejectionSamples = new List<ExplicitFileRejection>(ExplicitFilesRejectedPathLimit);
        var rejectionCount = 0;

        void Reject(int inputIndex, string path, string reason)
        {
            rejectionCount++;
            if (rejectionSamples.Count < ExplicitFilesRejectedPathLimit)
                rejectionSamples.Add(new ExplicitFileRejection(inputIndex, path, reason));
        }

        for (var inputIndex = 0; inputIndex < explicitFileInputs.Count; inputIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputPath = explicitFileInputs[inputIndex];
            string absolutePath;
            string relativePath;
            try
            {
                var selectedAbsolutePath = Path.GetFullPath(
                    Path.IsPathRooted(inputPath)
                        ? inputPath
                        : Path.Combine(projectRoot, inputPath));
                var isLexicallyWithinProjectRoot =
                    FileIndexer.TryGetNativeEquivalentProjectRelativePath(
                        projectRoot,
                        selectedAbsolutePath,
                        out FileIndexer.NativeProjectPathMatch pathMatch);
                if (isLexicallyWithinProjectRoot)
                {
                    relativePath = pathMatch.RelativePath;
                    absolutePath = pathMatch.CanonicalLexicalPath;
                }
                else
                {
                    relativePath = FileIndexer.NormalizePathSeparators(
                        FileIndexer.GetRelativePathFromProjectRoot(projectRoot, selectedAbsolutePath));
                    absolutePath = selectedAbsolutePath;
                }
            }
            catch (Exception ex) when (IsExplicitFilePathException(ex))
            {
                var isRooted = false;
                try
                {
                    isRooted = Path.IsPathRooted(inputPath);
                }
                catch (Exception pathEx) when (IsExplicitFilePathException(pathEx))
                {
                }

                Reject(
                    inputIndex,
                    isRooted
                        ? OutsideProjectRootPathPlaceholder
                        : FormatExplicitFileDiagnosticPath(inputPath),
                    "invalid_path");
                continue;
            }

            var isLexicallyOutsideProjectRoot = IsOutsideProjectRoot(relativePath);
            var isExistingRelevantIgnoreControl = isLexicallyOutsideProjectRoot
                && File.Exists(LongPath.EnsureWindowsPrefix(absolutePath))
                && FileIndexer.IsIgnoreFilePath(absolutePath)
                && IsRelevantIgnoreFileForProjectRoot(projectRoot, absolutePath);
            if (isLexicallyOutsideProjectRoot && !isExistingRelevantIgnoreControl)
            {
                Reject(inputIndex, OutsideProjectRootPathPlaceholder, "outside_project_root");
                continue;
            }

            if (!IsExplicitFilePathComponentSyntaxValid(
                    relativePath,
                    useWindowsRules: OperatingSystem.IsWindows()))
            {
                Reject(
                    inputIndex,
                    isLexicallyOutsideProjectRoot
                        ? OutsideProjectRootPathPlaceholder
                        : FormatExplicitFileDiagnosticPath(relativePath),
                    "invalid_path");
                continue;
            }

            var indexPath = FileIndexer.NormalizeIndexPath(relativePath);

            string canonicalAbsolutePath;
            try
            {
                canonicalAbsolutePath = FileIndexer.NormalizePathForIdentityComparison(absolutePath);
            }
            catch (Exception ex) when (IsExplicitFilePathException(ex))
            {
                Reject(
                    inputIndex,
                    isLexicallyOutsideProjectRoot
                        ? OutsideProjectRootPathPlaceholder
                        : FormatExplicitFileDiagnosticPath(relativePath),
                    "probe_failed");
                continue;
            }

            var isIndexed = TryGetExplicitFileIndexedPath(
                indexedPaths,
                projectRoot,
                indexPath,
                canonicalAbsolutePath,
                out var matchedIndexedPath);
            normalizedExecutionPaths[inputIndex] = isExistingRelevantIgnoreControl
                ? inputPath
                : relativePath;

            void UseMatchedIndexedCleanupPath()
            {
                if (matchedIndexedPath != null)
                    normalizedExecutionPaths[inputIndex] = matchedIndexedPath;
            }

            if (!isExistingRelevantIgnoreControl
                && options.SymlinkPolicy != FileIndexer.SymlinkPolicy.All
                && !PathCasing.IsPathEqualOrParentByDirectoryNamespace(
                    canonicalProjectRoot,
                    canonicalAbsolutePath)
                && !isIndexed)
            {
                Reject(inputIndex, SymlinkOutsideProjectRootPathPlaceholder, "symlink_escape");
                continue;
            }

            var canonicalIdentity = FileIndexer.NormalizeIndexPath(canonicalAbsolutePath);
            if (!TryAddExplicitFileCanonicalSelection(canonicalSelections, canonicalIdentity))
            {
                Reject(
                    inputIndex,
                    isLexicallyOutsideProjectRoot
                        ? OutsideProjectRootPathPlaceholder
                        : FormatExplicitFileDiagnosticPath(relativePath),
                    "duplicate");
                continue;
            }

            var isControlInput = isExistingRelevantIgnoreControl
                || IsExplicitFilesControlInput(projectRoot, absolutePath);
            var indexability = indexer.GetFileIndexabilityForIndexing(absolutePath);
            if (indexability == FileIndexer.FileProbeStatus.Missing)
            {
                // Deleted extractor/configuration inputs are reconciliation signals even
                // though they are never rows in files. Missing ignore paths retain the
                // stricter #4471 contract unless an indexed row proves cleanup intent.
                var isMissingReconciliationControl = isControlInput
                    && !FileIndexer.IsIgnoreFilePath(absolutePath);
                if (!isIndexed && !isMissingReconciliationControl && !indexedPaths.ReadFailed)
                    Reject(inputIndex, FormatExplicitFileDiagnosticPath(relativePath), "not_found");
                else if (isIndexed)
                    UseMatchedIndexedCleanupPath();
                continue;
            }

            // Do not load ignore rules for physically unsupported inputs: an ignore/control
            // filename may itself be a FIFO or device. Attribute probing is bounded and lets
            // us preserve the dedicated reason for a leaf symlink rejected by policy None.
            if (indexability == FileIndexer.FileProbeStatus.Unsupported)
            {
                if (!isIndexed)
                {
                    var reason = options.SymlinkPolicy == FileIndexer.SymlinkPolicy.None
                        && FileIndexer.IsSymlinkOrReparsePointPath(absolutePath)
                            ? "symlink_disallowed"
                            : "unsupported_file";
                    Reject(
                        inputIndex,
                        isLexicallyOutsideProjectRoot
                            ? OutsideProjectRootPathPlaceholder
                            : FormatExplicitFileDiagnosticPath(relativePath),
                        reason);
                }
                else
                {
                    UseMatchedIndexedCleanupPath();
                }

                continue;
            }

            // Relevant ancestor ignore files are the one intentional lexical-scope exception.
            // They still must be ordinary supported files before they may trigger a refresh.
            if (isExistingRelevantIgnoreControl)
            {
                continue;
            }

            FileIndexer.PathFilterResult pathFilter;
            try
            {
                pathFilter = indexer.EvaluatePathFilter(absolutePath);
            }
            catch (Exception ex) when (IsExplicitFilePathException(ex))
            {
                Reject(inputIndex, FormatExplicitFileDiagnosticPath(relativePath), "ignore_rules_unavailable");
                continue;
            }

            // Enforce scope and symlink policy before control-input bypass. Other filters
            // remain bypassable because control inputs intentionally trigger full refreshes.
            if (pathFilter.FilterKind is FileIndexer.PathFilterKind.OutsideProjectRoot
                or FileIndexer.PathFilterKind.SymlinkDisallowed)
            {
                if (isIndexed && pathFilter.ShouldDeleteExisting)
                {
                    UseMatchedIndexedCleanupPath();
                    continue;
                }

                Reject(
                    inputIndex,
                    pathFilter.FilterKind == FileIndexer.PathFilterKind.OutsideProjectRoot
                        ? SymlinkOutsideProjectRootPathPlaceholder
                        : FormatExplicitFileDiagnosticPath(relativePath),
                    GetExplicitFilePathFilterReason(pathFilter.FilterKind));
                continue;
            }

            if (isControlInput)
            {
                continue;
            }

            if (pathFilter.ShouldSkip)
            {
                if (isIndexed && pathFilter.ShouldDeleteExisting)
                {
                    UseMatchedIndexedCleanupPath();
                    continue;
                }

                Reject(
                    inputIndex,
                    pathFilter.FilterKind == FileIndexer.PathFilterKind.OutsideProjectRoot
                        ? SymlinkOutsideProjectRootPathPlaceholder
                        : FormatExplicitFileDiagnosticPath(relativePath),
                    GetExplicitFilePathFilterReason(pathFilter.FilterKind));
                continue;
            }

            // Preserve the existing structured indexing-error path for races and unreadable
            // files instead of converting an inconclusive probe into a usage error.
            if (indexability == FileIndexer.FileProbeStatus.ProbeFailed)
                continue;

            var language = indexer.TryDetectLanguageForIndexing(
                absolutePath,
                knownIndexability: indexability);
            if (language.Status == FileIndexer.FileProbeStatus.Unsupported)
            {
                if (!isIndexed)
                    Reject(inputIndex, FormatExplicitFileDiagnosticPath(relativePath), "unsupported_language");
                else
                    UseMatchedIndexedCleanupPath();
            }
            else if (language.Status == FileIndexer.FileProbeStatus.Missing)
            {
                if (!isIndexed && !indexedPaths.ReadFailed)
                    Reject(inputIndex, FormatExplicitFileDiagnosticPath(relativePath), "not_found");
                else if (isIndexed)
                    UseMatchedIndexedCleanupPath();
            }
            // ProbeFailed intentionally continues to the normal indexing path so existing
            // file-error/partial-result reporting remains authoritative.
        }

        if (rejectionCount == 0)
        {
            ApplyExplicitFileExecutionPaths(options, normalizedExecutionPaths);
            return null;
        }

        return WriteExplicitFilesPreflightError(
            options.Json,
            jsonOptions,
            rejectionSamples,
            rejectionCount);
    }

    private static bool TryAddExplicitFileCanonicalSelection(
        Dictionary<string, List<string>> selections,
        string canonicalIdentity)
    {
        if (!selections.TryGetValue(canonicalIdentity, out var candidateSpellings))
        {
            selections.Add(canonicalIdentity, [canonicalIdentity]);
            return true;
        }

        foreach (var existing in candidateSpellings)
        {
            // A case-folded bucket may span differently configured nested mounts. Compare
            // each differing component in its parent namespace rather than applying the
            // leaf filesystem's case policy to the whole path.
            if (PathCasing.PathsEqualByDirectoryNamespace(existing, canonicalIdentity))
                return false;
        }

        candidateSpellings.Add(canonicalIdentity);
        return true;
    }

    private static bool TryGetExplicitFileIndexedPath(
        ExplicitFilesIndexedPathSnapshot indexedPaths,
        string projectRoot,
        string indexPath,
        string canonicalAbsolutePath,
        out string? matchedIndexedPath)
    {
        // Preserve exact indexed cleanup targets without a filesystem probe. A case-folded
        // candidate, however, is authoritative only when both the selected target namespace
        // and the stored path's current target namespace agree that the spellings are equal.
        // This prevents workspace-wide core.ignorecase from collapsing a case-sensitive
        // nested mount while retaining deleted-path cleanup on case-insensitive targets.
        if (indexedPaths.Paths.Contains(indexPath))
        {
            matchedIndexedPath = indexPath;
            return true;
        }
        if (!indexedPaths.CaseFoldedPaths.TryGetValue(indexPath, out var caseFoldedCandidates))
        {
            matchedIndexedPath = null;
            return false;
        }

        foreach (var indexedPath in caseFoldedCandidates)
        {
            try
            {
                if (Path.IsPathRooted(indexedPath))
                    continue;

                var indexedAbsolutePath = Path.GetFullPath(Path.Combine(
                    projectRoot,
                    FileIndexer.NormalizeRelativePathForCurrentPlatform(indexedPath)));
                var canonicalIndexedPath = FileIndexer.NormalizePathForIdentityComparison(
                    indexedAbsolutePath);
                if (PathCasing.PathsEqualByDirectoryNamespace(
                        canonicalIndexedPath,
                        canonicalAbsolutePath))
                {
                    matchedIndexedPath = indexedPath;
                    return true;
                }
            }
            catch (Exception ex) when (IsExplicitFilePathException(ex))
            {
                // An unusable case-fold candidate cannot prove indexed cleanup intent.
            }
        }

        matchedIndexedPath = null;
        return false;
    }

    private static void ApplyExplicitFileExecutionPaths(
        IndexCommandOptions options,
        IReadOnlyList<string?> normalizedExecutionPaths)
    {
        for (var index = 0; index < normalizedExecutionPaths.Count; index++)
        {
            var normalizedPath = normalizedExecutionPaths[index];
            if (normalizedPath == null)
                continue;

            if (index < options.UpdateFiles.Count)
                options.UpdateFiles[index] = normalizedPath;
            if (options.ExplicitFiles != null && index < options.ExplicitFiles.Count)
                options.ExplicitFiles[index] = normalizedPath;
        }
    }

    private static ExplicitFilesIndexedPathSnapshot CreateExplicitFilesIndexedPathSnapshot(
        IEnumerable<string> paths,
        bool readFailed)
    {
        var exactPaths = new HashSet<string>(StringComparer.Ordinal);
        var mutableCaseFoldedPaths = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var normalizedPath = FileIndexer.NormalizeIndexPath(path);
            if (!exactPaths.Add(normalizedPath))
                continue;

            if (!mutableCaseFoldedPaths.TryGetValue(normalizedPath, out var bucket))
            {
                bucket = [];
                mutableCaseFoldedPaths.Add(normalizedPath, bucket);
            }

            bucket.Add(normalizedPath);
        }

        var caseFoldedPaths = mutableCaseFoldedPaths.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
        return new ExplicitFilesIndexedPathSnapshot(
            exactPaths,
            caseFoldedPaths,
            readFailed);
    }

    private static ExplicitFilesIndexedPathSnapshot ReadExplicitFilesIndexedPathSnapshot(
        string dbPath,
        IReadOnlyCollection<string> candidates,
        CancellationToken cancellationToken,
        bool writerLockHeld,
        long? explicitFileSizeLimit)
    {
        try
        {
            if (candidates.Count == 0)
                return ExplicitFilesIndexedPathSnapshot.Empty();

            if (!dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(LongPath.EnsureWindowsPrefix(dbPath)))
            {
                return ExplicitFilesIndexedPathSnapshot.Empty();
            }

            using var connection = writerLockHeld
                ? DbConnectionFactory.CreateLockedIndexPreflightQueryOnlyConnection(
                    dbPath,
                    pooling: false,
                    cancellationToken: cancellationToken)
                : DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
                    dbPath,
                    pooling: false,
                    out _,
                    out _,
                    out _,
                    out _,
                    cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            connection.Open();
            cancellationToken.ThrowIfCancellationRequested();
            if (!DryRunTableExists(connection, "files"))
                return ExplicitFilesIndexedPathSnapshot.Empty();

            var lookup = ReadExplicitFilesIndexedPathMatches(
                connection,
                candidates,
                cancellationToken);
            var metadata = DryRunReadMetadata(connection);
            metadata.TryGetValue(IndexedFileSizePolicy.MetaKey, out var storedLimit);
            var largestFile = IndexedFileSizePolicy.ReadLargestFileSize(
                connection, DryRunColumnExists(connection, "files", "size"));
            return CreateExplicitFilesIndexedPathSnapshot(lookup.Paths, readFailed: false) with
            {
                FileSizeLimit = IndexedFileSizePolicy.ResolveStored(storedLimit, largestFile, explicitFileSizeLimit),
            };
        }
        catch (SqliteException)
        {
            return ExplicitFilesIndexedPathSnapshot.ReadFailure();
        }
        catch (global::CodeIndex.CodeIndexException)
        {
            return ExplicitFilesIndexedPathSnapshot.ReadFailure();
        }
        catch (IOException)
        {
            return ExplicitFilesIndexedPathSnapshot.ReadFailure();
        }
        catch (UnauthorizedAccessException)
        {
            return ExplicitFilesIndexedPathSnapshot.ReadFailure();
        }
        catch (ArgumentException)
        {
            return ExplicitFilesIndexedPathSnapshot.ReadFailure();
        }
        catch (NotSupportedException)
        {
            return ExplicitFilesIndexedPathSnapshot.ReadFailure();
        }
        catch (System.Security.SecurityException)
        {
            return ExplicitFilesIndexedPathSnapshot.ReadFailure();
        }
    }

    private static IReadOnlyCollection<string> CollectExplicitFilesIndexedPathCandidates(
        string projectRoot,
        IEnumerable<string> inputPaths)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inputPath in inputPaths)
        {
            try
            {
                var absolutePath = Path.GetFullPath(
                    Path.IsPathRooted(inputPath)
                        ? inputPath
                        : Path.Combine(projectRoot, inputPath));
                if (FileIndexer.TryGetNativeEquivalentProjectRelativePath(
                        projectRoot,
                        absolutePath,
                        out FileIndexer.NativeProjectPathMatch pathMatch)
                    && pathMatch.RelativePath != ".")
                {
                    candidates.Add(FileIndexer.NormalizeIndexPath(pathMatch.RelativePath));
                }
            }
            catch (Exception ex) when (IsExplicitFilePathException(ex))
            {
            }
        }

        return candidates;
    }

    internal static ExplicitFilesIndexedPathLookupResult
        ReadExplicitFilesIndexedPathMatchesForTesting(
            SqliteConnection connection,
            IReadOnlyCollection<string> candidates,
            CancellationToken cancellationToken = default)
        => ReadExplicitFilesIndexedPathMatches(connection, candidates, cancellationToken);

    private static ExplicitFilesIndexedPathLookupResult ReadExplicitFilesIndexedPathMatches(
        SqliteConnection connection,
        IReadOnlyCollection<string> candidates,
        CancellationToken cancellationToken)
    {
        var matches = new HashSet<string>(StringComparer.Ordinal);
        var exactQueryCount = 0;
        foreach (var batch in candidates.Chunk(ExplicitFilesIndexedPathQueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            var parameterNames = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                var parameterName = $"@path{index}";
                parameterNames[index] = parameterName;
                SqliteCommandPolicy.Add(command, parameterName, batch[index]);
            }

            command.CommandText = $"SELECT path FROM files WHERE path IN ({string.Join(", ", parameterNames)})";
            exactQueryCount++;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.IsDBNull(0))
                    matches.Add(FileIndexer.NormalizeIndexPath(reader.GetString(0)));
            }
        }

        var unmatched = candidates
            .Where(path => !matches.Contains(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var caseFoldScanCount = 0;
        long caseFoldScannedRowCount = 0;
        if (unmatched.Count > 0)
        {
            // A custom collation on files.path prevents SQLite from using the ordinary
            // binary path index. Batching that expression therefore rescanned the entire
            // files table once per 256 candidates. Scan it at most once, retain only the
            // case-fold candidates, and let TryGetExplicitFileIndexedPath apply the target
            // directory namespace policy before treating a spelling as indexed.
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path FROM files";
            caseFoldScanCount = 1;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                caseFoldScannedRowCount++;
                if (reader.IsDBNull(0))
                    continue;

                var path = FileIndexer.NormalizeIndexPath(reader.GetString(0));
                if (unmatched.Contains(path))
                    matches.Add(path);
            }
        }

        return new ExplicitFilesIndexedPathLookupResult(
            matches,
            new ExplicitFilesIndexedPathLookupMetrics(
                exactQueryCount,
                caseFoldScanCount,
                caseFoldScannedRowCount));
    }

    internal static bool IsExplicitFilePathComponentSyntaxValid(
        string path,
        bool useWindowsRules)
    {
        if (!FileIndexer.IsFilePathSyntaxIndexable(path))
            return false;
        if (!useWindowsRules)
            return true;

        if (FileIndexer.IsWindowsDevicePath(path)
            || IsExplicitFileWindowsSuperscriptDevicePath(path))
            return false;

        // Path.GetFullPath no longer rejects wildcard and other invalid filename
        // characters on modern .NET. Check Windows filename components explicitly so a
        // bad direct --files token cannot reach the mutation pipeline as an inconclusive
        // filesystem probe. A drive designator is valid only as the first component; the
        // production caller normally supplies a project-relative spelling, but accepting
        // the rooted form here keeps the validation seam faithful to Win32 syntax.
        var span = path.AsSpan();
        var componentIndex = 0;
        for (var start = 0; start < span.Length;)
        {
            while (start < span.Length && span[start] is '/' or '\\')
                start++;
            if (start >= span.Length)
                break;

            var end = start;
            while (end < span.Length && span[end] is not ('/' or '\\'))
                end++;

            var component = span[start..end];
            var isDriveDesignator = componentIndex == 0
                && component.Length == 2
                && ((component[0] >= 'A' && component[0] <= 'Z')
                    || (component[0] >= 'a' && component[0] <= 'z'))
                && component[1] == ':';
            var hasInvalidCharacter = false;
            if (!isDriveDesignator)
            {
                foreach (var character in component)
                {
                    if (character is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                    {
                        hasInvalidCharacter = true;
                        break;
                    }
                }
            }
            if (hasInvalidCharacter)
                return false;

            if (!component.SequenceEqual(".".AsSpan())
                && !component.SequenceEqual("..".AsSpan())
                && component[^1] is ' ' or '.')
            {
                return false;
            }

            componentIndex++;
            start = end + 1;
        }

        return true;
    }

    private static bool IsExplicitFileWindowsSuperscriptDevicePath(string path)
    {
        var span = path.AsSpan();
        for (var start = 0; start < span.Length;)
        {
            while (start < span.Length && span[start] is '/' or '\\')
                start++;
            if (start >= span.Length)
                break;

            var end = start;
            while (end < span.Length && span[end] is not ('/' or '\\'))
                end++;

            var component = span[start..end];
            var extensionIndex = component.IndexOf('.');
            var name = extensionIndex >= 0 ? component[..extensionIndex] : component;
            if (name.Length == 4
                && (name.StartsWith("COM".AsSpan(), StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("LPT".AsSpan(), StringComparison.OrdinalIgnoreCase))
                && name[3] is '\u00b9' or '\u00b2' or '\u00b3')
            {
                return true;
            }

            start = end + 1;
        }

        return false;
    }

    private static int WriteExplicitFilesPreflightError(
        bool json,
        JsonSerializerOptions jsonOptions,
        IReadOnlyList<ExplicitFileRejection> rejectionSamples,
        int rejectionCount)
    {
        const string message = "paths supplied to --files were rejected; the selection was not applied and no database writes were performed";
        const string hint = "Fix or remove every rejected path, then rerun `cdidx index <projectPath> --files <path> [path ...]`.";
        if (!json)
        {
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.UsageError}]: {message}");
            foreach (var rejection in rejectionSamples)
            {
                CommandErrorWriter.WriteStderr(
                    $"  [{rejection.InputIndex}] {rejection.Path}: {rejection.Reason}");
            }

            if (rejectionCount > rejectionSamples.Count)
            {
                CommandErrorWriter.WriteStderr(
                    $"  ... {rejectionCount - rejectionSamples.Count} additional rejected path(s) omitted (limit {ExplicitFilesRejectedPathLimit}).");
            }

            CommandErrorWriter.WriteStderr($"Hint: {hint}");
            return CommandExitCodes.UsageError;
        }

        var rejectedPaths = new JsonArray();
        foreach (var rejection in rejectionSamples)
        {
            rejectedPaths.Add(new JsonObject
            {
                ["input_index"] = rejection.InputIndex,
                ["path"] = rejection.Path,
                ["reason"] = rejection.Reason,
            });
        }

        return CommandErrorWriter.WriteJsonOrHuman(
            json: true,
            jsonOptions,
            message,
            CommandExitCodes.UsageError,
            hint,
            errorCode: CommandErrorCodes.UsageError,
            additionalJsonProperties: new JsonObject
            {
                ["rejected_paths"] = rejectedPaths,
                ["rejected_path_count"] = rejectionCount,
                ["rejected_paths_truncated"] = rejectionCount > rejectionSamples.Count,
                ["rejected_path_limit"] = ExplicitFilesRejectedPathLimit,
            });
    }

    private static string GetExplicitFilePathFilterReason(FileIndexer.PathFilterKind filterKind)
        => filterKind switch
        {
            FileIndexer.PathFilterKind.IgnoredByRules => "ignored_by_rules",
            FileIndexer.PathFilterKind.ExcludedByDefaultDirectory => "excluded_by_default_directory",
            FileIndexer.PathFilterKind.ExcludedByDefaultFile => "excluded_by_default_file",
            FileIndexer.PathFilterKind.OutsideProjectRoot => "symlink_escape",
            FileIndexer.PathFilterKind.SymlinkDisallowed => "symlink_disallowed",
            FileIndexer.PathFilterKind.IgnoreRulesUnavailable => "ignore_rules_unavailable",
            _ => "invalid_path",
        };

    private static bool IsExplicitFilesControlInput(string projectRoot, string absolutePath)
        => FileIndexer.ClassifyIndexInputInvalidation(projectRoot, absolutePath)
                != FileIndexer.IndexInputInvalidationKind.None
            || IsJavaScriptTypeScriptConfigPath(absolutePath)
            || FileIndexer.IsAmbiguousLanguageProjectMarkerPath(absolutePath);

    private static string FormatExplicitFileDiagnosticPath(string path)
        => DiagnosticRedactor.BoundDiagnosticText(
            DiagnosticRedactor.RedactSensitiveText(
                FileIndexer.NormalizePathSeparators(path),
                placeholder: DiagnosticRedactor.AngleRedacted,
                redactPaths: false),
            ExplicitFilesDiagnosticPathCharLimit);

    private static bool IsExplicitFilePathException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;
}
