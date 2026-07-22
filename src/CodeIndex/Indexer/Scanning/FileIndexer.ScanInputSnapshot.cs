using System.Security.Cryptography;
using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private const int MaxConfigurationInputSnapshots = 8192;
    private const long MaxConfigurationInputSnapshotBytes = 32L * 1024 * 1024;
    private const int MaxConfigurationInputFileBytes = 512 * 1024;
    private const int MaxDirectoryListingSnapshots = 1_000_000;

    private static readonly ScanInputSnapshot EmptyScanInputSnapshot = new(
        Array.Empty<DirectoryListingSnapshot>(),
        Array.Empty<ConfigurationInputSnapshot>(),
        IsComplete: true,
        IncompletePath: null,
        IncompleteReason: null,
        ConfigurationGeneration: 0);

    internal static int? MaxConfigurationInputSnapshotsForTesting { get; set; }
    internal static long? MaxConfigurationInputSnapshotBytesForTesting { get; set; }
    internal static int? MaxDirectoryListingSnapshotsForTesting { get; set; }

    private readonly object _configurationInputSnapshotGate = new();
    private readonly Dictionary<string, ConfigurationInputSnapshot> _configurationInputSnapshots =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(string Path, bool IsUserConfiguration), bool>
        _patternConfigurationDirectoryExistenceCache = [];
    private long _configurationInputSnapshotBytes;
    private bool _configurationInputSnapshotsComplete = true;
    private string? _configurationInputSnapshotIncompletePath;
    private string? _configurationInputSnapshotIncompleteReason;
    private long _configurationInputSnapshotGeneration;
    private string? _configurationInputSnapshotLastChangedPath;
    private bool _suppressConfigurationInputObservation;
    private long _configurationInputContentHashCountForTesting;
    private int _patternConfigurationDirectoryProbeCountForTesting;

    internal long ConfigurationInputContentHashCountForTesting
        => Interlocked.Read(ref _configurationInputContentHashCountForTesting);

    internal int PatternConfigurationDirectoryProbeCountForTesting
        => Volatile.Read(ref _patternConfigurationDirectoryProbeCountForTesting);

    internal long ConfigurationInputSnapshotGenerationForTesting
    {
        get
        {
            lock (_configurationInputSnapshotGate)
                return _configurationInputSnapshotGeneration;
        }
    }

    private static int ConfigurationInputSnapshotLimit
        => MaxConfigurationInputSnapshotsForTesting ?? MaxConfigurationInputSnapshots;

    private static long ConfigurationInputSnapshotByteLimit
        => MaxConfigurationInputSnapshotBytesForTesting ?? MaxConfigurationInputSnapshotBytes;

    private static int DirectoryListingSnapshotLimit
        => MaxDirectoryListingSnapshotsForTesting ?? MaxDirectoryListingSnapshots;

    private Stream OpenConfigurationFileForRead(string path)
    {
        if (_suppressConfigurationInputObservation)
            return _openReadForIndexContent(path);

        var normalizedPath = NormalizeConfigurationInputPath(path);
        FileStream? stream = null;
        try
        {
            stream = _openReadForIndexContent(path);
            if (!stream.CanSeek)
            {
                MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
                return stream;
            }

            var length = stream.Length;
            if (length < 0 || length > MaxConfigurationInputFileBytes)
            {
                RecordConfigurationFileMetadataSnapshot(normalizedPath, length);
                return stream;
            }

            if (!TryReserveConfigurationInputSnapshot(normalizedPath, length))
                return stream;

            var content = new byte[checked((int)length)];
            var offset = 0;
            while (offset < content.Length)
            {
                var read = stream.Read(content, offset, content.Length - offset);
                if (read == 0)
                    break;
                offset += read;
            }

            if (offset != content.Length || stream.ReadByte() != -1)
            {
                MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
                stream.Position = 0;
                return stream;
            }

            var contentHash = SHA256.HashData(content);
            Interlocked.Increment(ref _configurationInputContentHashCountForTesting);
            var snapshot = CreateConfigurationFileSnapshot(
                normalizedPath,
                length,
                contentHash);
            RecordConfigurationInputSnapshot(snapshot);

            stream.Dispose();
            stream = null;
            return new MemoryStream(content, writable: false);
        }
        catch (Exception ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            stream?.Dispose();
            MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            var opened = stream != null;
            stream?.Dispose();
            // A failed initial open consumes absence and is bound by the parent directory
            // listing. Disappearance after a successful open cannot be represented that way.
            // 初回open失敗は親listingでabsenceを固定できるが、open後の消失はpartial扱いにする。
            if (opened)
                MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
            throw;
        }
    }

    private Stream OpenObservedConfigurationFileForRead(string path)
    {
        if (_suppressConfigurationInputObservation)
            return _openReadForIndexContent(path);

        try
        {
            return OpenConfigurationFileForRead(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            MarkConfigurationInputSnapshotsIncomplete(NormalizeConfigurationInputPath(path));
            throw;
        }
    }

    private Stream OpenObservedPatternConfigurationFileForRead(string path)
    {
        if (_suppressConfigurationInputObservation)
            return _openReadForIndexContent(path);

        var normalizedPath = NormalizeConfigurationInputPath(path);
        try
        {
            return _openReadForIndexContent(path);
        }
        catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
        {
            MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
            throw;
        }
    }

    private void ObservePatternConfigurationInput(
        string path,
        ReadOnlyMemory<byte>? content,
        long? observedLength)
    {
        if (_suppressConfigurationInputObservation)
            return;

        var normalizedPath = NormalizeConfigurationInputPath(path);
        try
        {
            if (!content.HasValue)
            {
                if (!observedLength.HasValue)
                {
                    MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
                    return;
                }

                RecordRejectedOversizePatternConfiguration(
                    normalizedPath,
                    observedLength.Value);
                return;
            }

            var bytes = content.Value;
            if (!TryReserveConfigurationInputSnapshot(normalizedPath, bytes.Length))
                return;

            var contentHash = SHA256.HashData(bytes.Span);
            Interlocked.Increment(ref _configurationInputContentHashCountForTesting);
            RecordConfigurationInputSnapshot(CreateConfigurationFileSnapshot(
                normalizedPath,
                bytes.Length,
                contentHash));
        }
        catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
        {
            MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
        }
    }

    private void RecordRejectedOversizePatternConfiguration(string path, long length)
    {
        var info = new FileInfo(LongPath.EnsureWindowsPrefix(path));
        info.Refresh();
        if (!info.Exists || info.Length != length)
        {
            MarkConfigurationInputSnapshotsIncomplete(path);
            return;
        }

        FileIdentity? identity = TryGetFileIdentity(path, out var observedIdentity)
            ? observedIdentity
            : null;
        RecordConfigurationInputSnapshot(new ConfigurationInputSnapshot(
            path,
            ConfigurationInputKind.RejectedOversizeFile,
            length,
            info.LastWriteTimeUtc,
            identity,
            ContentHash: null));
    }

    private bool TryReserveConfigurationInputSnapshot(
        string path,
        long length,
        bool reserveContentBytes = true)
    {
        lock (_configurationInputSnapshotGate)
        {
            if (_configurationInputSnapshots.TryGetValue(path, out var existing))
            {
                if (existing.Kind != ConfigurationInputKind.File || existing.Length != length)
                {
                    MarkConfigurationInputSnapshotsIncompleteUnderLock(path);
                    return false;
                }
                if (!reserveContentBytes
                    || existing.ContentHash != null
                    || length > MaxConfigurationInputFileBytes)
                {
                    return true;
                }
                if (length > ConfigurationInputSnapshotByteLimit - _configurationInputSnapshotBytes)
                {
                    MarkConfigurationInputSnapshotsIncompleteUnderLock(
                        path,
                        $"Configuration input snapshot byte limit ({ConfigurationInputSnapshotByteLimit} bytes) was exceeded.");
                    return false;
                }

                _configurationInputSnapshotBytes += length;
                return true;
            }

            if (_configurationInputSnapshots.Count >= ConfigurationInputSnapshotLimit)
            {
                MarkConfigurationInputSnapshotsIncompleteUnderLock(
                    path,
                    $"Configuration input snapshot count limit ({ConfigurationInputSnapshotLimit}) was exceeded.");
                return false;
            }

            if (reserveContentBytes
                && length > ConfigurationInputSnapshotByteLimit - _configurationInputSnapshotBytes)
            {
                MarkConfigurationInputSnapshotsIncompleteUnderLock(
                    path,
                    $"Configuration input snapshot byte limit ({ConfigurationInputSnapshotByteLimit} bytes) was exceeded.");
                return false;
            }

            if (reserveContentBytes)
                _configurationInputSnapshotBytes += length;
            return true;
        }
    }

    private void RecordConfigurationFileMetadataSnapshot(string path, long length)
    {
        if (length <= MaxConfigurationInputFileBytes)
        {
            MarkConfigurationInputSnapshotsIncomplete(path);
            return;
        }

        if (!TryReserveConfigurationInputSnapshot(path, length, reserveContentBytes: false))
            return;

        RecordConfigurationInputSnapshot(CreateConfigurationFileSnapshot(path, length, contentHash: null));
    }

    private ConfigurationInputSnapshot CreateConfigurationFileSnapshot(
        string path,
        long length,
        byte[]? contentHash)
    {
        var info = new FileInfo(LongPath.EnsureWindowsPrefix(path));
        info.Refresh();
        if (!info.Exists || info.Length != length)
        {
            MarkConfigurationInputSnapshotsIncomplete(path);
            return new ConfigurationInputSnapshot(
                path,
                ConfigurationInputKind.File,
                length,
                DateTime.MinValue,
                Identity: null,
                contentHash);
        }

        FileIdentity? identity = TryGetFileIdentity(path, out var observedIdentity)
            ? observedIdentity
            : null;
        return new ConfigurationInputSnapshot(
            path,
            ConfigurationInputKind.File,
            length,
            info.LastWriteTimeUtc,
            identity,
            contentHash);
    }

    private void RecordConfigurationFileProbe(string path)
    {
        if (_suppressConfigurationInputObservation)
            return;

        var normalizedPath = NormalizeConfigurationInputPath(path);
        try
        {
            var info = new FileInfo(LongPath.EnsureWindowsPrefix(normalizedPath));
            info.Refresh();
            if (!info.Exists)
            {
                RecordConfigurationInputSnapshot(new ConfigurationInputSnapshot(
                    normalizedPath,
                    ConfigurationInputKind.MissingFile,
                    0,
                    DateTime.MinValue,
                    Identity: null,
                    ContentHash: null));
                return;
            }

            RecordConfigurationInputSnapshot(CreateConfigurationFileSnapshot(
                normalizedPath,
                info.Length,
                contentHash: null));
        }
        catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
        {
            MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
        }
    }

    private void RecordConfigurationDirectoryProbe(
        string path,
        bool recordMissingWhenParentExists = false)
    {
        if (_suppressConfigurationInputObservation)
            return;

        var normalizedPath = NormalizeConfigurationInputPath(path);
        try
        {
            var info = new DirectoryInfo(LongPath.EnsureWindowsPrefix(normalizedPath));
            info.Refresh();
            if (!info.Exists)
            {
                var parentDirectory = Path.GetDirectoryName(normalizedPath);
                if (recordMissingWhenParentExists
                    && !string.IsNullOrEmpty(parentDirectory)
                    && Directory.Exists(LongPath.EnsureWindowsPrefix(parentDirectory)))
                {
                    RecordConfigurationInputSnapshot(new ConfigurationInputSnapshot(
                        normalizedPath,
                        ConfigurationInputKind.MissingDirectory,
                        0,
                        DateTime.MinValue,
                        Identity: null,
                        ContentHash: null));
                }
                return;
            }

            FileIdentity? identity = TryGetFileIdentity(normalizedPath, out var observedIdentity)
                ? observedIdentity
                : null;
            RecordConfigurationInputSnapshot(new ConfigurationInputSnapshot(
                normalizedPath,
                ConfigurationInputKind.Directory,
                0,
                info.LastWriteTimeUtc,
                identity,
                ContentHash: null));
        }
        catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
        {
            MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
        }
    }

    private void RecordObservedNestedRepositoryMarker(string path)
    {
        if (_suppressConfigurationInputObservation)
            return;

        var normalizedPath = NormalizeConfigurationInputPath(path);
        try
        {
            var directory = new DirectoryInfo(LongPath.EnsureWindowsPrefix(normalizedPath));
            directory.Refresh();
            if (directory.Exists)
            {
                FileIdentity? identity = TryGetFileIdentity(normalizedPath, out var observedIdentity)
                    ? observedIdentity
                    : null;
                RecordConfigurationInputSnapshot(new ConfigurationInputSnapshot(
                    normalizedPath,
                    ConfigurationInputKind.MarkerDirectory,
                    0,
                    directory.LastWriteTimeUtc,
                    identity,
                    ContentHash: null));
                return;
            }

            var file = new FileInfo(LongPath.EnsureWindowsPrefix(normalizedPath));
            file.Refresh();
            if (!file.Exists)
            {
                MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
                return;
            }

            FileIdentity? fileIdentity = TryGetFileIdentity(normalizedPath, out var observedFileIdentity)
                ? observedFileIdentity
                : null;
            RecordConfigurationInputSnapshot(new ConfigurationInputSnapshot(
                normalizedPath,
                ConfigurationInputKind.MarkerFile,
                file.Length,
                file.LastWriteTimeUtc,
                fileIdentity,
                ContentHash: null));
        }
        catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
        {
            MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
        }
    }

    private bool ObservePatternConfigurationDirectoryExists(
        string path,
        bool isUserConfiguration)
    {
        if (_suppressConfigurationInputObservation)
            return Directory.Exists(LongPath.EnsureWindowsPrefix(path));

        var normalizedPath = NormalizeConfigurationInputPath(path);
        var cacheKey = (normalizedPath, isUserConfiguration);
        lock (_configurationInputSnapshotGate)
        {
            if (_patternConfigurationDirectoryExistenceCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        Interlocked.Increment(ref _patternConfigurationDirectoryProbeCountForTesting);
        try
        {
            // Project-local pattern discovery happens for every scanned directory. Probe
            // the .cdidx parent first so the common missing case remains one metadata
            // lookup and does not retain one missing snapshot per source directory. The
            // scan's parent-listing snapshot binds creation of a previously absent .cdidx.
            // project内pattern探索の通常missingは親.cdidxの1回probeに抑え、全source
            // directory分のmissing snapshotを保持しない。後発.cdidxはscan listingで検出する。
            var isProjectLocalWorkspaceConfiguration = !isUserConfiguration
                && PathCasing.IsFullPathEqualOrParent(_projectRoot, normalizedPath);
            if (isProjectLocalWorkspaceConfiguration)
            {
                var parentPath = Path.GetDirectoryName(normalizedPath);
                if (string.IsNullOrEmpty(parentPath))
                    return false;

                var parent = new DirectoryInfo(LongPath.EnsureWindowsPrefix(parentPath));
                parent.Refresh();
                if (!parent.Exists)
                {
                    CachePatternConfigurationDirectoryExistence(cacheKey, exists: false);
                    return false;
                }
            }

            var info = new DirectoryInfo(LongPath.EnsureWindowsPrefix(normalizedPath));
            info.Refresh();
            if (!info.Exists)
            {
                // Out-of-project user configuration has no project listing snapshot, and
                // a project-local existing .cdidx needs its absent patterns child bound.
                RecordConfigurationInputSnapshot(new ConfigurationInputSnapshot(
                    normalizedPath,
                    ConfigurationInputKind.MissingDirectory,
                    0,
                    DateTime.MinValue,
                    Identity: null,
                    ContentHash: null));
                CachePatternConfigurationDirectoryExistence(cacheKey, exists: false);
                return false;
            }

            FileIdentity? identity = TryGetFileIdentity(normalizedPath, out var observedIdentity)
                ? observedIdentity
                : null;
            RecordConfigurationInputSnapshot(new ConfigurationInputSnapshot(
                normalizedPath,
                ConfigurationInputKind.Directory,
                0,
                info.LastWriteTimeUtc,
                identity,
                ContentHash: null));
            CachePatternConfigurationDirectoryExistence(cacheKey, exists: true);
            return true;
        }
        catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
        {
            MarkConfigurationInputSnapshotsIncomplete(normalizedPath);
            // The snapshot is already non-authoritative, so retrying the same failing
            // directory probe for every unknown-extension file cannot recover this scan.
            // Keep the failure scan-local and avoid amplifying transient/permission I/O.
            CachePatternConfigurationDirectoryExistence(cacheKey, exists: false);
            return false;
        }
    }

    private void CachePatternConfigurationDirectoryExistence(
        (string Path, bool IsUserConfiguration) cacheKey,
        bool exists)
    {
        lock (_configurationInputSnapshotGate)
            _patternConfigurationDirectoryExistenceCache[cacheKey] = exists;
    }

    private void ResetPatternConfigurationDirectoryExistenceCache()
    {
        lock (_configurationInputSnapshotGate)
            _patternConfigurationDirectoryExistenceCache.Clear();
    }

    private void RecordConfigurationInputSnapshot(ConfigurationInputSnapshot snapshot)
    {
        lock (_configurationInputSnapshotGate)
        {
            if (_configurationInputSnapshots.TryGetValue(snapshot.Path, out var existing))
            {
                if (CanUpgradeConfigurationInputSnapshot(existing, snapshot))
                {
                    _configurationInputSnapshots[snapshot.Path] = snapshot;
                    AdvanceConfigurationInputSnapshotGenerationUnderLock(snapshot.Path);
                    return;
                }
                if (IsMatchingConfigurationMetadataProbe(existing, snapshot))
                    return;
                if (!ConfigurationInputSnapshotsEqual(existing, snapshot))
                    MarkConfigurationInputSnapshotsIncompleteUnderLock(snapshot.Path);
                return;
            }

            if (_configurationInputSnapshots.Count >= ConfigurationInputSnapshotLimit)
            {
                MarkConfigurationInputSnapshotsIncompleteUnderLock(
                    snapshot.Path,
                    $"Configuration input snapshot count limit ({ConfigurationInputSnapshotLimit}) was exceeded.");
                return;
            }

            _configurationInputSnapshots.Add(snapshot.Path, snapshot);
            AdvanceConfigurationInputSnapshotGenerationUnderLock(snapshot.Path);
        }
    }

    private static bool CanUpgradeConfigurationInputSnapshot(
        ConfigurationInputSnapshot existing,
        ConfigurationInputSnapshot snapshot)
        => existing.Kind == ConfigurationInputKind.File
            && snapshot.Kind == ConfigurationInputKind.File
            && existing.ContentHash == null
            && snapshot.ContentHash != null
            && existing.Path == snapshot.Path
            && existing.Length == snapshot.Length
            && existing.ModifiedUtc == snapshot.ModifiedUtc
            && existing.Identity == snapshot.Identity;

    private static bool IsMatchingConfigurationMetadataProbe(
        ConfigurationInputSnapshot existing,
        ConfigurationInputSnapshot snapshot)
        => existing.Kind == ConfigurationInputKind.File
            && snapshot.Kind == ConfigurationInputKind.File
            && existing.ContentHash != null
            && snapshot.ContentHash == null
            && existing.Path == snapshot.Path
            && existing.Length == snapshot.Length
            && existing.ModifiedUtc == snapshot.ModifiedUtc
            && existing.Identity == snapshot.Identity;

    private static bool ConfigurationInputSnapshotsEqual(
        ConfigurationInputSnapshot left,
        ConfigurationInputSnapshot right)
    {
        if (left.Path != right.Path
            || left.Kind != right.Kind
            || left.Length != right.Length
            || left.ModifiedUtc != right.ModifiedUtc
            || left.Identity != right.Identity)
        {
            return false;
        }

        if (left.ContentHash == null || right.ContentHash == null)
            return left.ContentHash == right.ContentHash;
        return left.ContentHash.AsSpan().SequenceEqual(right.ContentHash);
    }

    private void MarkConfigurationInputSnapshotsIncomplete(string path, string? reason = null)
    {
        if (_suppressConfigurationInputObservation)
            return;

        lock (_configurationInputSnapshotGate)
            MarkConfigurationInputSnapshotsIncompleteUnderLock(path, reason);
    }

    private void MarkConfigurationInputSnapshotsIncompleteUnderLock(string path, string? reason = null)
    {
        if (!_configurationInputSnapshotsComplete)
            return;

        _configurationInputSnapshotsComplete = false;
        _configurationInputSnapshotIncompletePath ??= path;
        _configurationInputSnapshotIncompleteReason ??= reason;
        AdvanceConfigurationInputSnapshotGenerationUnderLock(path);
    }

    private void AdvanceConfigurationInputSnapshotGenerationUnderLock(string path)
    {
        _configurationInputSnapshotGeneration = unchecked(_configurationInputSnapshotGeneration + 1);
        _configurationInputSnapshotLastChangedPath = path;
    }

    private ScanInputSnapshot MaterializeScanInputSnapshot(
        List<DirectoryListingSnapshot>? directoryListings,
        bool directoryListingsComplete,
        string? directoryListingsIncompletePath,
        string? directoryListingsIncompleteReason)
    {
        lock (_configurationInputSnapshotGate)
        {
            var configurationInputs = _configurationInputSnapshots.Count == 0
                ? Array.Empty<ConfigurationInputSnapshot>()
                : _configurationInputSnapshots.Values.ToArray();
            var complete = directoryListingsComplete && _configurationInputSnapshotsComplete;
            return new ScanInputSnapshot(
                directoryListings is { Count: > 0 }
                    ? directoryListings
                    : Array.Empty<DirectoryListingSnapshot>(),
                configurationInputs,
                complete,
                directoryListingsIncompletePath ?? _configurationInputSnapshotIncompletePath,
                !directoryListingsComplete
                    ? directoryListingsIncompleteReason
                    : _configurationInputSnapshotIncompleteReason,
                _configurationInputSnapshotGeneration);
        }
    }

    internal bool TryValidateScanInputSnapshot(
        ScanInputSnapshot snapshot,
        out string changedPath,
        CancellationToken cancellationToken = default)
    {
        changedPath = snapshot.IncompletePath ?? _projectRoot;
        if (!TryValidateConfigurationCollectorState(snapshot, out var collectorChangedPath))
        {
            changedPath = collectorChangedPath;
            return false;
        }

        foreach (var directory in snapshot.DirectoryListings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _pathAccessValidator?.Invoke(directory.Path);
                if (ReadDirectoryModifiedUtc(directory.Path) != directory.ModifiedUtc)
                {
                    changedPath = directory.Path;
                    return false;
                }
            }
            catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
            {
                changedPath = directory.Path;
                return false;
            }
        }

        var validationBuffer = snapshot.ConfigurationInputs.Count == 0
            ? Array.Empty<byte>()
            : new byte[8192];
        foreach (var input in snapshot.ConfigurationInputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateConfigurationInput(input, validationBuffer, out changedPath))
                return false;
        }

        if (!TryValidateConfigurationCollectorState(snapshot, out collectorChangedPath))
        {
            changedPath = collectorChangedPath;
            return false;
        }

        changedPath = string.Empty;
        return true;
    }

    private bool TryValidateConfigurationCollectorState(
        ScanInputSnapshot snapshot,
        out string changedPath)
    {
        lock (_configurationInputSnapshotGate)
        {
            if (!snapshot.IsComplete)
            {
                changedPath = snapshot.IncompletePath ?? _projectRoot;
                return false;
            }

            if (!_configurationInputSnapshotsComplete)
            {
                changedPath = _configurationInputSnapshotIncompletePath ?? _projectRoot;
                return false;
            }

            changedPath = _configurationInputSnapshotLastChangedPath ?? _projectRoot;
            return snapshot.ConfigurationGeneration == _configurationInputSnapshotGeneration;
        }
    }

    private bool TryValidateConfigurationInput(
        ConfigurationInputSnapshot input,
        byte[] validationBuffer,
        out string changedPath)
    {
        changedPath = input.Path;
        try
        {
            _pathAccessValidator?.Invoke(input.Path);
            if (input.Kind == ConfigurationInputKind.MissingFile)
                return ConfigurationInputPathIsAbsent(input.Path);
            if (input.Kind == ConfigurationInputKind.MissingDirectory)
                return ConfigurationInputPathIsAbsent(input.Path);

            if (input.Kind == ConfigurationInputKind.Directory)
            {
                var directory = new DirectoryInfo(LongPath.EnsureWindowsPrefix(input.Path));
                directory.Refresh();
                return directory.Exists
                    && directory.LastWriteTimeUtc == input.ModifiedUtc
                    && ConfigurationInputIdentityMatches(input);
            }

            if (input.Kind == ConfigurationInputKind.MarkerDirectory)
            {
                var directory = new DirectoryInfo(LongPath.EnsureWindowsPrefix(input.Path));
                directory.Refresh();
                return directory.Exists
                    && directory.LastWriteTimeUtc == input.ModifiedUtc
                    && ConfigurationInputIdentityMatches(input);
            }

            if (input.Kind == ConfigurationInputKind.MarkerFile)
            {
                var marker = new FileInfo(LongPath.EnsureWindowsPrefix(input.Path));
                marker.Refresh();
                return marker.Exists
                    && marker.Length == input.Length
                    && marker.LastWriteTimeUtc == input.ModifiedUtc
                    && ConfigurationInputIdentityMatches(input);
            }

            if (input.Kind == ConfigurationInputKind.RejectedOversizeFile)
            {
                var rejected = new FileInfo(LongPath.EnsureWindowsPrefix(input.Path));
                rejected.Refresh();
                return rejected.Exists
                    && rejected.Length == input.Length
                    && rejected.LastWriteTimeUtc == input.ModifiedUtc
                    && ConfigurationInputIdentityMatches(input);
            }

            var before = new FileInfo(LongPath.EnsureWindowsPrefix(input.Path));
            before.Refresh();
            if (!before.Exists
                || before.Length != input.Length
                || before.LastWriteTimeUtc != input.ModifiedUtc
                || !ConfigurationInputIdentityMatches(input))
            {
                return false;
            }

            if (input.ContentHash == null)
                return input.Length > MaxConfigurationInputFileBytes;

            using var stream = _openReadForIndexContent(input.Path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            int read;
            while ((read = stream.Read(validationBuffer, 0, validationBuffer.Length)) > 0)
            {
                total += read;
                if (total > input.Length)
                    return false;
                hash.AppendData(validationBuffer, 0, read);
            }

            if (total != input.Length
                || !hash.GetHashAndReset().AsSpan().SequenceEqual(input.ContentHash))
            {
                return false;
            }

            var after = new FileInfo(LongPath.EnsureWindowsPrefix(input.Path));
            after.Refresh();
            return after.Exists
                && after.Length == input.Length
                && after.LastWriteTimeUtc == input.ModifiedUtc
                && ConfigurationInputIdentityMatches(input);
        }
        catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
        {
            return false;
        }
    }

    private static bool ConfigurationInputIdentityMatches(ConfigurationInputSnapshot input)
        => !input.Identity.HasValue
            || (TryGetFileIdentity(input.Path, out var identity) && identity == input.Identity.Value);

    private static bool ConfigurationInputPathIsAbsent(string path)
    {
        if (FileSystemBoundary.TryGetAttributes(path, out _) != FileSystemBoundaryProbeStatus.Missing)
            return false;

        try
        {
            return string.IsNullOrEmpty(new FileInfo(path).LinkTarget)
                && string.IsNullOrEmpty(new DirectoryInfo(path).LinkTarget);
        }
        catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
        {
            return false;
        }
    }

    private static string NormalizeConfigurationInputPath(string path)
        => Path.GetFullPath(LongPath.RemoveWindowsPrefix(path));
}
