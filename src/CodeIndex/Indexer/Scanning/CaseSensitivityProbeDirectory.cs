using System.Runtime.InteropServices;
using CodeIndex.Cli;

namespace CodeIndex.Indexer;

internal static class CaseSensitivityProbeDirectory
{
    internal const string DataDirectoryName = ".cdidx";
    internal const string ProbeDirectoryName = "probes";
    internal const string IsolatedProbeDirectoryPrefix = ".cdidx-case-probe-";

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static Action<string>? DeleteCreatedEmptyDirectoryForTesting { get; set; }
    internal static Action<CaseSensitivityProbeCleanupDiagnostic>? CleanupDiagnosticSinkForTesting { get; set; }

    internal static bool ProbeIgnoreCase(string projectRoot, string prefix)
    {
        var normalizedRoot = Path.GetFullPath(projectRoot);
        using var probe = CreateIsolatedProbePathScope(normalizedRoot, prefix);
        var probePath = probe.Path;
        FileWriteProbe.WriteEmptyFile(probePath);
        try
        {
            if (TryCreateLeafNameCaseVariant(probePath, out var probeVariant))
                return File.Exists(LongPath.EnsureWindowsPrefix(probeVariant));
        }
        finally
        {
            FileWriteProbe.DeleteFileIfExists(probePath);
        }

        throw new CaseSensitivityProbeException(
            "Failed to create a case-variant path for filesystem case-sensitivity probing.",
            normalizedRoot,
            probePath: probePath);
    }

    internal static bool TryCreateLeafNameCaseVariant(string path, out string variant)
    {
        var leafName = Path.GetFileName(path);
        var chars = leafName.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            var ch = chars[i];
            if (!char.IsLetter(ch))
                continue;

            chars[i] = char.IsUpper(ch)
                ? char.ToLowerInvariant(ch)
                : char.ToUpperInvariant(ch);
            variant = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, new string(chars));
            return true;
        }

        variant = path;
        return false;
    }

    internal static bool? ProbeExistingChildIgnoreCase(string directory)
    {
        var normalizedDirectory = Path.GetFullPath(directory);
        var entries = Directory.EnumerateFileSystemEntries(LongPath.EnsureWindowsPrefix(normalizedDirectory))
            .Select(LongPath.RemoveWindowsPrefix)
            .ToArray();
        return ProbeExistingChildIgnoreCase(normalizedDirectory, entries);
    }

    internal static bool? ProbeExistingChildIgnoreCase(string directory, IReadOnlyList<string> entries)
    {
        _ = Path.GetFullPath(directory);
        var exactNames = entries
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var normalizedEntry in entries)
        {
            if (!TryCreateLeafNameCaseVariant(normalizedEntry, out var variant))
                continue;

            // A case-sensitive directory may legitimately contain both spellings. Do not
            // mistake that sibling for case-insensitive resolution of the original entry.
            // case-sensitive directory では大小文字だけが異なる sibling が共存できるため、
            // その sibling を元 entry の case-insensitive 解決と誤認しない。
            var variantName = Path.GetFileName(variant);
            if (exactNames.Contains(variantName))
                continue;

            var probeStatus = FileSystemBoundary.TryGetAttributes(
                LongPath.EnsureWindowsPrefix(variant),
                out _);
            if (probeStatus == FileSystemBoundaryProbeStatus.Found)
                return true;
            if (probeStatus == FileSystemBoundaryProbeStatus.Missing)
                return false;
        }

        return null;
    }

    internal static ProbePathScope CreateProbePathScope(string projectRoot, string prefix)
    {
        var normalizedRoot = Path.GetFullPath(projectRoot);
        var directory = CreateProbeDirectory(normalizedRoot);
        return new ProbePathScope(
            normalizedRoot,
            Path.Combine(directory.ProbeDirectory, $"{prefix}{Guid.NewGuid():N}"),
            directory.ProbeDirectory,
            directory.DataDirectory,
            directory.CreatedProbeDirectory,
            directory.CreatedDataDirectory);
    }

    private static ProbePathScope CreateIsolatedProbePathScope(string projectRoot, string prefix)
    {
        var normalizedRoot = Path.GetFullPath(projectRoot);
        var probeDirectory = Path.Combine(
            normalizedRoot,
            $"{IsolatedProbeDirectoryPrefix}{Guid.NewGuid():N}");
        CreatePrivateDirectory(probeDirectory);
        return new ProbePathScope(
            normalizedRoot,
            Path.Combine(probeDirectory, $"{prefix}{Guid.NewGuid():N}"),
            probeDirectory,
            normalizedRoot,
            createdProbeDirectory: true,
            createdDataDirectory: false,
            probeDirectoryNamePrefix: IsolatedProbeDirectoryPrefix,
            cleanupPathComparison: StringComparison.Ordinal);
    }

    internal static ProbeDirectoryScope CreateProbeDirectory(string projectRoot)
    {
        var normalizedRoot = Path.GetFullPath(projectRoot);
        var cdidxDirectory = Path.Combine(normalizedRoot, DataDirectoryName);
        var createdDataDirectory = !Directory.Exists(LongPath.EnsureWindowsPrefix(cdidxDirectory));
        CreatePrivateDirectory(cdidxDirectory);

        var probeDirectory = Path.Combine(cdidxDirectory, ProbeDirectoryName);
        var createdProbeDirectory = !Directory.Exists(LongPath.EnsureWindowsPrefix(probeDirectory));
        CreatePrivateDirectory(probeDirectory);
        return new ProbeDirectoryScope(cdidxDirectory, probeDirectory, createdDataDirectory, createdProbeDirectory);
    }

    internal sealed class ProbePathScope : IDisposable
    {
        private readonly string _probeDirectory;
        private readonly string _dataDirectory;
        private readonly bool _createdProbeDirectory;
        private readonly bool _createdDataDirectory;
        private readonly string _probeDirectoryNamePrefix;
        private readonly StringComparison? _cleanupPathComparison;
        private readonly List<CaseSensitivityProbeCleanupDiagnostic> _cleanupDiagnostics = [];
        private bool _disposed;

        internal ProbePathScope(
            string projectRoot,
            string path,
            string probeDirectory,
            string dataDirectory,
            bool createdProbeDirectory,
            bool createdDataDirectory,
            string probeDirectoryNamePrefix = ProbeDirectoryName,
            StringComparison? cleanupPathComparison = null)
        {
            ProjectRoot = projectRoot;
            Path = path;
            _probeDirectory = probeDirectory;
            _dataDirectory = dataDirectory;
            _createdProbeDirectory = createdProbeDirectory;
            _createdDataDirectory = createdDataDirectory;
            _probeDirectoryNamePrefix = probeDirectoryNamePrefix;
            _cleanupPathComparison = cleanupPathComparison;
        }

        internal string ProjectRoot { get; }
        internal string Path { get; }
        internal IReadOnlyList<CaseSensitivityProbeCleanupDiagnostic> CleanupDiagnostics => _cleanupDiagnostics;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DeleteCreatedEmptyDirectory(_probeDirectory, _createdProbeDirectory, _dataDirectory, _probeDirectoryNamePrefix);
            DeleteCreatedEmptyDirectory(_dataDirectory, _createdDataDirectory, ProjectRoot, DataDirectoryName);
        }

        private void DeleteCreatedEmptyDirectory(
            string path,
            bool createdForProbe,
            string safeRoot,
            string expectedNamePrefix)
        {
            if (!createdForProbe)
                return;

            try
            {
                if (!TryValidateCleanupTarget(path, safeRoot, expectedNamePrefix, out var fullPath, out var validationFailure))
                {
                    RecordCleanupDiagnostic(
                        path,
                        "CleanupTargetRejected",
                        $"Skipped filesystem case-sensitivity probe directory cleanup: {validationFailure}.");
                    return;
                }

                if (!Directory.Exists(LongPath.EnsureWindowsPrefix(fullPath)))
                    return;

                if (!TryValidateCleanupTarget(fullPath, safeRoot, expectedNamePrefix, out fullPath, out validationFailure))
                {
                    RecordCleanupDiagnostic(
                        path,
                        "CleanupTargetRejected",
                        $"Skipped filesystem case-sensitivity probe directory cleanup: {validationFailure}.");
                    return;
                }

                if (DeleteCreatedEmptyDirectoryForTesting is { } delete)
                    delete(fullPath);
                else
                    Directory.Delete(LongPath.EnsureWindowsPrefix(fullPath));
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                RecordCleanupDiagnostic(path, ex);
            }
        }

        private void RecordCleanupDiagnostic(string path, Exception ex)
            => RecordCleanupDiagnostic(
                path,
                ex.GetType().Name,
                "Failed to clean up filesystem case-sensitivity probe directory.");

        private void RecordCleanupDiagnostic(string path, string exceptionType, string message)
        {
            var diagnostic = new CaseSensitivityProbeCleanupDiagnostic(
                RelativePath: FormatCleanupRelativePath(path),
                ExceptionType: exceptionType,
                Message: message);
            _cleanupDiagnostics.Add(diagnostic);

            if (CleanupDiagnosticSinkForTesting is { } sink)
            {
                sink(diagnostic);
                return;
            }

            Console.Error.WriteLine(
                $"Warning: {diagnostic.Message} path={diagnostic.RelativePath} exception={diagnostic.ExceptionType}. " +
                $"Remove stale {DataDirectoryName}/{ProbeDirectoryName} entries when no cdidx process is running.");
        }

        private bool TryValidateCleanupTarget(
            string path,
            string safeRoot,
            string expectedNamePrefix,
            out string fullPath,
            out string failureReason)
        {
            var options = new DirectoryCleanupBoundaryOptions(
                expectedNamePrefix,
                "target is outside the expected probe cleanup root",
                "target name does not match the expected probe cleanup prefix",
                "target is a symbolic link, reparse point, or device");
            return FileSystemBoundary.TryValidateDirectoryCleanupTarget(
                path,
                safeRoot,
                options,
                out fullPath,
                out failureReason,
                _cleanupPathComparison);
        }

        private string FormatCleanupRelativePath(string path)
        {
            try
            {
                var relative = System.IO.Path.GetRelativePath(ProjectRoot, path);
                if (!relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative))
                    return NormalizeRelativePath(relative);
            }
            catch
            {
            }

            return DataDirectoryName + "/" + ProbeDirectoryName;
        }

        private static string NormalizeRelativePath(string path)
            => FileIndexer.NormalizePathSeparators(path);
    }

    internal readonly record struct ProbeDirectoryScope(
        string DataDirectory,
        string ProbeDirectory,
        bool CreatedDataDirectory,
        bool CreatedProbeDirectory);

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(path));
        ApplyPrivateDirectoryMode(path);
    }

    private static void ApplyPrivateDirectoryMode(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        File.SetUnixFileMode(LongPath.EnsureWindowsPrefix(path), PrivateDirectoryMode);
    }

}

internal readonly record struct CaseSensitivityProbeCleanupDiagnostic(
    string RelativePath,
    string ExceptionType,
    string Message);
