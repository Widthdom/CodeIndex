using System.Runtime.InteropServices;

namespace CodeIndex.Indexer;

internal static class CaseSensitivityProbeDirectory
{
    internal const string DataDirectoryName = ".cdidx";
    internal const string ProbeDirectoryName = "probes";

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static Action<string>? DeleteCreatedEmptyDirectoryForTesting { get; set; }
    internal static Action<CaseSensitivityProbeCleanupDiagnostic>? CleanupDiagnosticSinkForTesting { get; set; }

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
        private readonly List<CaseSensitivityProbeCleanupDiagnostic> _cleanupDiagnostics = [];
        private bool _disposed;

        internal ProbePathScope(
            string projectRoot,
            string path,
            string probeDirectory,
            string dataDirectory,
            bool createdProbeDirectory,
            bool createdDataDirectory)
        {
            ProjectRoot = projectRoot;
            Path = path;
            _probeDirectory = probeDirectory;
            _dataDirectory = dataDirectory;
            _createdProbeDirectory = createdProbeDirectory;
            _createdDataDirectory = createdDataDirectory;
        }

        internal string ProjectRoot { get; }
        internal string Path { get; }
        internal IReadOnlyList<CaseSensitivityProbeCleanupDiagnostic> CleanupDiagnostics => _cleanupDiagnostics;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DeleteCreatedEmptyDirectory(_probeDirectory, _createdProbeDirectory);
            DeleteCreatedEmptyDirectory(_dataDirectory, _createdDataDirectory);
        }

        private void DeleteCreatedEmptyDirectory(string path, bool createdForProbe)
        {
            if (!createdForProbe)
                return;

            try
            {
                if (DeleteCreatedEmptyDirectoryForTesting is { } delete)
                    delete(path);
                else
                    Directory.Delete(LongPath.EnsureWindowsPrefix(path));
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecordCleanupDiagnostic(path, ex);
            }
        }

        private void RecordCleanupDiagnostic(string path, Exception ex)
        {
            var diagnostic = new CaseSensitivityProbeCleanupDiagnostic(
                RelativePath: FormatCleanupRelativePath(path),
                ExceptionType: ex.GetType().Name,
                Message: "Failed to clean up filesystem case-sensitivity probe directory.");
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
            => path.Replace(System.IO.Path.DirectorySeparatorChar, '/')
                .Replace(System.IO.Path.AltDirectorySeparatorChar, '/');
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
