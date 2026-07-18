using CodeIndex.Cli;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    private static void LoadPluginAssemblies(IEnumerable<string> directories)
    {
        foreach (var pluginPath in EnumeratePluginAssemblyPaths(directories))
            TryLoadPlugin(pluginPath);
    }

    private static void TryLoadPlugin(string pluginPath)
    {
        var fullPath = pluginPath;
        ExecutableExtensionStagingHandle? staging = null;
        ExtractorPluginWorkerClient? worker = null;
        try
        {
            fullPath = Path.GetFullPath(pluginPath);
            if (!PluginAssemblyCandidateIsWithinBudget(fullPath))
                return;

            var pluginDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            if (!ExecutableExtensionBoundary.TryStageFile(
                    pluginDirectory,
                    fullPath,
                    MaxPluginAssemblyBytes,
                    out staging,
                    out var boundaryFailure))
            {
                RecordDiagnostic(
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
                if (PluginLoadAttemptIsCached(fullPath, staging.Fingerprint))
                    return;
                RecordPluginLoadAttempt(fullPath, staging.Fingerprint, succeeded: false);
                RecordDiagnostic(
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
                if (PluginLoadAttemptIsCached(fullPath, staging.Fingerprint))
                    return;
                RecordPluginLoadAttempt(fullPath, staging.Fingerprint, succeeded: false);
                RecordDiagnostic(
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
                if (PluginLoadAttemptIsCached(fullPath, staging.Fingerprint))
                    return;
                RecordPluginLoadAttempt(fullPath, staging.Fingerprint, succeeded: false);
                RecordDiagnostic(
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
                RecordDiagnostic(
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "error",
                    dependencyFailure.Message,
                    countsAsSkippedFile: true,
                    category: dependencyFailure.Category);
                return;
            }

            if (PluginLoadAttemptIsCached(fullPath, stagedFingerprint))
                return;

            worker = new ExtractorPluginWorkerClient(
                staging.StagedPath,
                ResolveTypeInspectionLimit(),
                WorkerOperationBudgetForTesting);
            var manifestResult = worker.LoadManifest();
            if (!manifestResult.Success || manifestResult.Response?.Manifest == null)
            {
                RecordPluginLoadAttempt(fullPath, stagedFingerprint, succeeded: false);
                var typeLimitExceeded = StringComparer.Ordinal.Equals(
                    manifestResult.ErrorCategory,
                    "plugin_type_limit_exceeded");
                RecordDiagnostic(
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
                RecordDiagnostic(
                    "plugin_type",
                    fullPath,
                    diagnostic.TypeName,
                    severity: "error",
                    diagnostic.Message,
                    countsAsSkippedFile: false,
                    category: diagnostic.Category);
            }

            LoadedPluginState? replacedState;
            lock (Gate)
            {
                replacedState = LoadedPluginStates.FirstOrDefault(
                    state => string.Equals(state.Path, fullPath, PathCasing.ComparisonFor(fullPath)));
                if (replacedState != null)
                    RemoveLoadedPluginStateUnderLock(replacedState);

                var registrations = RegisterPluginManifest(manifestResult.Response.Manifest, worker, fullPath);
                pluginAssemblyCount++;
                LoadedPluginWorkers.Add(worker);
                LoadedPluginStagingHandles.Add(staging!);
                LoadedPluginStates.Add(new(
                    fullPath,
                    stagedFingerprint,
                    worker,
                    staging!,
                    registrations));
                RecordPluginLoadAttemptUnderLock(fullPath, stagedFingerprint, succeeded: true);
                worker = null;
                staging = null;
            }

            replacedState?.Worker.Dispose();
            replacedState?.Staging.Dispose();
        }
        catch (Exception ex)
        {
            var diagnostic = ExtensionLoadDiagnosticClassifier.ClassifyAssemblyLoad("Plugin assembly load", ex);
            RecordDiagnostic(
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

    private static bool PluginAssemblyCandidateIsWithinBudget(string fullPath)
    {
        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            RecordDiagnostic(
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
            RecordDiagnostic(
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
            RecordDiagnostic(
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
            RecordDiagnostic(
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
            RecordDiagnostic(
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
        string pluginPath)
    {
        var registrations = new List<PluginRegistration>();
        foreach (var entry in manifest)
        {
            try
            {
                var proxy = new IsolatedPluginExtractorProxy(
                    entry,
                    worker,
                    (typeName, category, message) => RecordDiagnostic(
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
                if (entry.SupportsSymbols)
                    SymbolExtractors[symbolLanguage] = proxy;
                if (entry.SupportsReferences)
                    ReferenceExtractors[referenceLanguage] = proxy;
                registrations.Add(new(
                    entry.SupportsSymbols ? symbolLanguage : null,
                    entry.SupportsReferences ? referenceLanguage : null,
                    proxy,
                    entry.SupportsSymbols,
                    entry.SupportsReferences));
            }
            catch (Exception ex)
            {
                RecordDiagnostic(
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

    private static void RemoveLoadedPluginStateUnderLock(LoadedPluginState state)
    {
        foreach (var registration in state.Registrations)
        {
            if (registration.SupportsSymbols
                && registration.SymbolLanguage != null
                && SymbolExtractors.TryGetValue(registration.SymbolLanguage, out var symbolExtractor)
                && ReferenceEquals(symbolExtractor, registration.Proxy))
            {
                SymbolExtractors.Remove(registration.SymbolLanguage);
            }

            if (registration.SupportsReferences
                && registration.ReferenceLanguage != null
                && ReferenceExtractors.TryGetValue(registration.ReferenceLanguage, out var referenceExtractor)
                && ReferenceEquals(referenceExtractor, registration.Proxy))
            {
                ReferenceExtractors.Remove(registration.ReferenceLanguage);
            }
        }

        LoadedPluginStates.Remove(state);
        LoadedPluginWorkers.Remove(state.Worker);
        LoadedPluginStagingHandles.Remove(state.Staging);
        pluginAssemblyCount--;
    }

    private static void RecordPluginLoadAttempt(string path, string fingerprint, bool succeeded)
    {
        lock (Gate)
            RecordPluginLoadAttemptUnderLock(path, fingerprint, succeeded);
    }

    private static bool PluginLoadAttemptIsCached(string path, string fingerprint)
    {
        lock (Gate)
        {
            return PluginLoadAttempts.Any(
                attempt => string.Equals(attempt.Path, path, PathCasing.ComparisonFor(path))
                           && StringComparer.Ordinal.Equals(attempt.Fingerprint, fingerprint));
        }
    }

    private static void RecordPluginLoadAttemptUnderLock(string path, string fingerprint, bool succeeded)
    {
        PluginLoadAttempts.RemoveAll(
            attempt => string.Equals(attempt.Path, path, PathCasing.ComparisonFor(path)));
        PluginLoadAttempts.Add(new(path, fingerprint, succeeded));
    }
}
