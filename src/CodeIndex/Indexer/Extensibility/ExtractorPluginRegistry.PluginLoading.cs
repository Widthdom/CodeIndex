using CodeIndex.Cli;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    internal static Action? WorkspacePluginLoadedBeforeCommitForTesting { get; set; }

    private static void LoadPluginAssemblies(
        IEnumerable<string> directories,
        PatternWorkspaceState? workspaceState = null)
    {
        foreach (var pluginPath in EnumeratePluginAssemblyPaths(directories, workspaceState))
            TryLoadPlugin(pluginPath, workspaceState);
    }

    private static void TryLoadPlugin(string pluginPath, PatternWorkspaceState? workspaceState = null)
    {
        var fullPath = pluginPath;
        ExecutableExtensionStagingHandle? staging = null;
        ExtractorPluginWorkerClient? worker = null;
        try
        {
            fullPath = Path.GetFullPath(pluginPath);
            if (WorkspacePluginIsAlreadyLoaded(workspaceState, fullPath)
                || !PluginAssemblyCandidateIsWithinBudget(workspaceState, fullPath))
            {
                return;
            }

            var pluginDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            if (!ExecutableExtensionBoundary.TryStageFile(
                    pluginDirectory,
                    fullPath,
                    MaxPluginAssemblyBytes,
                    out staging,
                    out var boundaryFailure))
            {
                RecordPluginDiagnostic(
                    workspaceState,
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "error",
                    boundaryFailure.Message,
                    countsAsSkippedFile: true,
                    category: boundaryFailure.Category);
                return;
            }

            if (!PluginMetadataInspector.TryInspect(staging!.StagedPath, out var metadata, out var metadataError))
            {
                if (PluginLoadAttemptIsCached(workspaceState, fullPath, staging.Fingerprint))
                    return;
                RecordPluginLoadAttempt(workspaceState, fullPath, staging.Fingerprint, succeeded: false);
                RecordPluginDiagnostic(
                    workspaceState,
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "error",
                    metadataError,
                    countsAsSkippedFile: true,
                    category: "plugin_metadata_invalid");
                return;
            }

            if (!metadata.HasMarker)
            {
                if (PluginLoadAttemptIsCached(workspaceState, fullPath, staging.Fingerprint))
                    return;
                RecordPluginLoadAttempt(workspaceState, fullPath, staging.Fingerprint, succeeded: false);
                RecordPluginDiagnostic(
                    workspaceState,
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "skipped",
                    "Plugin assembly skipped: missing CdidxPluginAttribute.",
                    countsAsSkippedFile: true,
                    category: "missing_plugin_attribute");
                return;
            }

            if (metadata.MinApiVersion > CurrentApiVersion
                || metadata.MaxApiVersion < CurrentApiVersion)
            {
                if (PluginLoadAttemptIsCached(workspaceState, fullPath, staging.Fingerprint))
                    return;
                RecordPluginLoadAttempt(workspaceState, fullPath, staging.Fingerprint, succeeded: false);
                RecordPluginDiagnostic(
                    workspaceState,
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "skipped",
                    $"Plugin assembly skipped: API range {metadata.MinApiVersion}-{metadata.MaxApiVersion} does not include {CurrentApiVersion}.",
                    countsAsSkippedFile: true,
                    category: "incompatible_plugin_api");
                return;
            }

            if (!PluginDependencyStager.TryStageManagedDependencies(
                    pluginDirectory,
                    staging,
                    MaxPluginAssemblyBytes,
                    out var stagedFingerprint,
                    out var dependencyFailure))
            {
                RecordPluginDiagnostic(
                    workspaceState,
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "error",
                    dependencyFailure.Message,
                    countsAsSkippedFile: true,
                    category: dependencyFailure.Category);
                return;
            }

            if (PluginLoadAttemptIsCached(workspaceState, fullPath, stagedFingerprint))
                return;

            worker = new ExtractorPluginWorkerClient(
                staging.StagedPath,
                ResolveTypeInspectionLimit(),
                WorkerOperationBudgetForTesting);
            var manifestResult = worker.LoadManifest();
            if (!manifestResult.Success || manifestResult.Response?.Manifest == null)
            {
                RecordPluginLoadAttempt(workspaceState, fullPath, stagedFingerprint, succeeded: false);
                var typeLimitExceeded = StringComparer.Ordinal.Equals(
                    manifestResult.ErrorCategory,
                    "plugin_type_limit_exceeded");
                RecordPluginDiagnostic(
                    workspaceState,
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: typeLimitExceeded ? "skipped" : "error",
                    manifestResult.Error ?? "Plugin worker did not return a manifest.",
                    countsAsSkippedFile: true,
                    category: manifestResult.ErrorCategory ?? "plugin_worker_manifest_failed");
                return;
            }

            foreach (var diagnostic in manifestResult.Response.Diagnostics ?? [])
            {
                RecordPluginDiagnostic(
                    workspaceState,
                    "plugin_type",
                    fullPath,
                    diagnostic.TypeName,
                    severity: "error",
                    diagnostic.Message,
                    countsAsSkippedFile: false,
                    category: diagnostic.Category);
            }

            if (workspaceState != null)
                WorkspacePluginLoadedBeforeCommitForTesting?.Invoke();

            LoadedPluginState? replacedState;
            if (workspaceState == null)
            {
                lock (Gate)
                {
                    replacedState = LoadedPluginStates.FirstOrDefault(
                        state => string.Equals(state.Path, fullPath, PathCasing.ComparisonFor(fullPath)));
                    if (replacedState != null)
                        RemoveLoadedPluginStateUnderLock(replacedState, workspaceState: null);

                    var registrations = RegisterPluginManifest(
                        manifestResult.Response.Manifest,
                        worker,
                        fullPath,
                        workspaceState: null);
                    pluginAssemblyCount++;
                    LoadedPluginWorkers.Add(worker);
                    LoadedPluginStagingHandles.Add(staging!);
                    LoadedPluginStates.Add(new(
                        fullPath,
                        stagedFingerprint,
                        worker,
                        staging!,
                        registrations));
                    RecordPluginLoadAttemptUnderLock(
                        PluginLoadAttempts,
                        fullPath,
                        stagedFingerprint,
                        succeeded: true);
                    PublishUserExtractorSnapshot();
                    worker = null;
                    staging = null;
                }

                lock (DefaultPatternWorkspace.Gate)
                    DefaultPatternWorkspace.PublishSnapshot();
            }
            else
            {
                lock (workspaceState.Gate)
                {
                    if (workspaceState.Retired)
                        return;

                    replacedState = workspaceState.PluginStates.FirstOrDefault(
                        state => string.Equals(state.Path, fullPath, PathCasing.ComparisonFor(fullPath)));
                    if (replacedState != null)
                        RemoveLoadedPluginStateUnderLock(replacedState, workspaceState);

                    var registrations = RegisterPluginManifest(
                        manifestResult.Response.Manifest,
                        worker,
                        fullPath,
                        workspaceState);
                    workspaceState.PluginStates.Add(new(
                        fullPath,
                        stagedFingerprint,
                        worker,
                        staging!,
                        registrations));
                    workspaceState.PluginAssemblyCount = workspaceState.PluginStates.Count;
                    RecordPluginLoadAttemptUnderLock(
                        workspaceState.PluginLoadAttempts,
                        fullPath,
                        stagedFingerprint,
                        succeeded: true);
                    workspaceState.PublishSnapshot();
                    worker = null;
                    staging = null;
                }
            }

            replacedState?.Worker.Dispose();
            replacedState?.Staging.Dispose();
        }
        catch (Exception ex)
        {
            var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyAssemblyLoad("Plugin assembly load", ex);
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                diagnostic.Message,
                countsAsSkippedFile: true,
                category: diagnostic.Category);
        }
        finally
        {
            worker?.Dispose();
            staging?.Dispose();
        }
    }

    private static bool WorkspacePluginIsAlreadyLoaded(PatternWorkspaceState? workspaceState, string fullPath)
    {
        if (workspaceState == null)
            return false;

        lock (workspaceState.Gate)
        {
            return workspaceState.Retired
                   || workspaceState.PluginStates.Any(
                       state => string.Equals(state.Path, fullPath, PathCasing.ComparisonFor(fullPath)));
        }
    }

    private static void DisposePluginWorkers()
    {
        foreach (var worker in LoadedPluginWorkers)
            worker.Dispose();

        LoadedPluginWorkers.Clear();
    }

    private static void DisposePluginStagingHandles()
    {
        foreach (var staging in LoadedPluginStagingHandles)
            staging.Dispose();

        LoadedPluginStagingHandles.Clear();
    }

    private static void DisposePluginStates(IEnumerable<LoadedPluginState> states)
    {
        foreach (var state in states)
        {
            state.Worker.Dispose();
            state.Staging.Dispose();
        }
    }

    private static bool PluginAssemblyCandidateIsWithinBudget(PatternWorkspaceState? workspaceState, string fullPath)
    {
        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: could not inspect file.",
                countsAsSkippedFile: true,
                category: "plugin_file_inspection_failed");
            return false;
        }

        if (!fileInfo.Exists)
        {
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: file does not exist.",
                countsAsSkippedFile: true,
                category: "plugin_file_missing");
            return false;
        }

        if ((fileInfo.Attributes & FileAttributes.Directory) != 0)
        {
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: path is a directory.",
                countsAsSkippedFile: true,
                category: "plugin_path_is_directory");
            return false;
        }

        if (FileSystemBoundary.IsSymlinkOrReparsePoint(fileInfo))
        {
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                "Plugin assembly skipped: symbolic links and reparse points are not supported.",
                countsAsSkippedFile: true,
                category: "plugin_reparse_point");
            return false;
        }

        if (fileInfo.Length > MaxPluginAssemblyBytes)
        {
            RecordPluginDiagnostic(
                workspaceState,
                "plugin",
                fullPath,
                typeName: null,
                severity: "skipped",
                $"Plugin assembly skipped: file is too large ({fileInfo.Length} bytes; maximum {MaxPluginAssemblyBytes}).",
                countsAsSkippedFile: true,
                category: "plugin_file_too_large");
            return false;
        }

        return true;
    }

    private static IReadOnlyList<PluginRegistration> RegisterPluginManifest(
        IReadOnlyList<ExtractorPluginWorkerManifestEntry> manifest,
        ExtractorPluginWorkerClient worker,
        string pluginPath,
        PatternWorkspaceState? workspaceState)
    {
        var registrations = new List<PluginRegistration>();
        foreach (var entry in manifest)
        {
            try
            {
                var proxy = new IsolatedPluginExtractorProxy(
                    entry,
                    worker,
                    (typeName, category, message) => RecordPluginDiagnostic(
                        workspaceState,
                        "plugin_type",
                        pluginPath,
                        typeName,
                        severity: "error",
                        message,
                        countsAsSkippedFile: false,
                        category));
                var symbolLanguage = entry.SupportsSymbols
                    ? NormalizePluginLanguage(((ISymbolExtractor)proxy).Language)
                    : string.Empty;
                var referenceLanguage = entry.SupportsReferences
                    ? NormalizePluginLanguage(((IReferenceExtractor)proxy).Language)
                    : string.Empty;
                var symbolExtractors = workspaceState?.WorkspaceSymbolExtractors ?? SymbolExtractors;
                var referenceExtractors = workspaceState?.WorkspaceReferenceExtractors ?? ReferenceExtractors;
                if (entry.SupportsSymbols)
                    symbolExtractors[symbolLanguage] = proxy;
                if (entry.SupportsReferences)
                    referenceExtractors[referenceLanguage] = proxy;
                registrations.Add(new(
                    entry.SupportsSymbols ? symbolLanguage : null,
                    entry.SupportsReferences ? referenceLanguage : null,
                    proxy,
                    entry.SupportsSymbols,
                    entry.SupportsReferences));
            }
            catch (Exception ex)
            {
                RecordPluginDiagnostic(
                    workspaceState,
                    "plugin_type",
                    pluginPath,
                    entry.TypeName,
                    severity: "error",
                    SafeDiagnosticFormatter.FormatExceptionCategory("plugin_manifest_invalid", ex),
                    countsAsSkippedFile: false,
                    category: "plugin_manifest_invalid");
            }
        }

        return registrations;
    }

    private static void RemoveLoadedPluginStateUnderLock(
        LoadedPluginState state,
        PatternWorkspaceState? workspaceState)
    {
        var symbolExtractors = workspaceState?.WorkspaceSymbolExtractors ?? SymbolExtractors;
        var referenceExtractors = workspaceState?.WorkspaceReferenceExtractors ?? ReferenceExtractors;
        foreach (var registration in state.Registrations)
        {
            if (registration.SupportsSymbols
                && registration.SymbolLanguage != null
                && symbolExtractors.TryGetValue(registration.SymbolLanguage, out var symbolExtractor)
                && ReferenceEquals(symbolExtractor, registration.Proxy))
            {
                symbolExtractors.Remove(registration.SymbolLanguage);
            }

            if (registration.SupportsReferences
                && registration.ReferenceLanguage != null
                && referenceExtractors.TryGetValue(registration.ReferenceLanguage, out var referenceExtractor)
                && ReferenceEquals(referenceExtractor, registration.Proxy))
            {
                referenceExtractors.Remove(registration.ReferenceLanguage);
            }
        }

        if (workspaceState == null)
        {
            LoadedPluginStates.Remove(state);
            LoadedPluginWorkers.Remove(state.Worker);
            LoadedPluginStagingHandles.Remove(state.Staging);
            pluginAssemblyCount--;
        }
        else
        {
            workspaceState.PluginStates.Remove(state);
            workspaceState.PluginAssemblyCount = workspaceState.PluginStates.Count;
        }
    }

    private static void RecordPluginLoadAttempt(
        PatternWorkspaceState? workspaceState,
        string path,
        string fingerprint,
        bool succeeded)
    {
        if (workspaceState == null)
        {
            lock (Gate)
                RecordPluginLoadAttemptUnderLock(PluginLoadAttempts, path, fingerprint, succeeded);
            return;
        }

        lock (workspaceState.Gate)
            RecordPluginLoadAttemptUnderLock(workspaceState.PluginLoadAttempts, path, fingerprint, succeeded);
    }

    private static bool PluginLoadAttemptIsCached(
        PatternWorkspaceState? workspaceState,
        string path,
        string fingerprint)
    {
        if (workspaceState == null)
        {
            lock (Gate)
                return PluginLoadAttemptIsCachedUnderLock(PluginLoadAttempts, path, fingerprint);
        }

        lock (workspaceState.Gate)
            return PluginLoadAttemptIsCachedUnderLock(workspaceState.PluginLoadAttempts, path, fingerprint);
    }

    private static bool PluginLoadAttemptIsCachedUnderLock(
        IReadOnlyCollection<PluginLoadAttempt> attempts,
        string path,
        string fingerprint)
        => attempts.Any(
            attempt => string.Equals(attempt.Path, path, PathCasing.ComparisonFor(path))
                       && StringComparer.Ordinal.Equals(attempt.Fingerprint, fingerprint));

    private static void RecordPluginLoadAttemptUnderLock(
        List<PluginLoadAttempt> attempts,
        string path,
        string fingerprint,
        bool succeeded)
    {
        attempts.RemoveAll(
            attempt => string.Equals(attempt.Path, path, PathCasing.ComparisonFor(path)));
        attempts.Add(new(path, fingerprint, succeeded));
    }

    private static void RecordPluginDiagnostic(
        PatternWorkspaceState? workspaceState,
        string kind,
        string path,
        string? typeName,
        string severity,
        string message,
        bool countsAsSkippedFile,
        string category)
    {
        if (workspaceState == null)
        {
            RecordDiagnostic(kind, path, typeName, severity, message, countsAsSkippedFile, category);
            return;
        }

        RecordPatternDiagnostic(workspaceState, kind, path, typeName, severity, message, countsAsSkippedFile, category);
    }
}
