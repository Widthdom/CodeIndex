using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

internal static class TestProjectHelper
{
    internal static string RepeatCsvEntry(string value, int count)
        => RepeatJoinedEntry(value, count, ",");

    internal static string RepeatJoinedEntry(string value, int count, string separator)
        => string.Join(separator, Enumerable.Repeat(value, count));

    internal static string CreateTempProject(string prefix)
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        return projectRoot;
    }

    internal static TempProjectScope CreateTempProjectScope(string prefix)
        => new(CreateTempProject(prefix));

    internal static string CreateTrustedWindowsGitDirectory(string prefix)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Trusted Windows Git fixtures require Windows.");

        return CreateTrustedWindowsGitDirectoryCore(prefix);
    }

    [SupportedOSPlatform("windows")]
    private static string CreateTrustedWindowsGitDirectoryCore(string prefix)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            throw new InvalidOperationException("The current Windows user profile is unavailable.");

        var directoryPath = Path.Combine(userProfile, $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var trustedPrincipals = new SecurityIdentifier[]
        {
            currentUser,
            new(WellKnownSidType.LocalSystemSid, domainSid: null),
            new(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null),
        };
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var principal in trustedPrincipals)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                principal,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(directoryPath), security);
        return directoryPath;
    }

    internal static string CreateTempDbPath(string prefix)
        => Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}.db");

    internal static string CreateTempFilePath(string prefix, string extension)
        => Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}{extension}");

    internal static string ProjectPath(string projectRoot, params string[] relativeSegments)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("Project root is required.", nameof(projectRoot));

        var fullRoot = Path.GetFullPath(projectRoot);
        var combined = fullRoot;
        foreach (var segment in relativeSegments)
        {
            if (string.IsNullOrEmpty(segment))
                continue;
            if (Path.IsPathRooted(segment))
                throw new ArgumentException("Fixture paths must be relative to the project root.", nameof(relativeSegments));

            combined = Path.Combine(combined, segment);
        }

        var fullPath = Path.GetFullPath(combined);
        if (!IsSameOrChildPath(fullRoot, fullPath))
            throw new ArgumentException("Fixture path escapes the project root.", nameof(relativeSegments));

        return fullPath;
    }

    internal static string CreateDirectory(string projectRoot, params string[] relativeSegments)
    {
        var path = ProjectPath(projectRoot, relativeSegments);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string WriteTextFile(string projectRoot, string relativePath, string content)
    {
        var path = ProjectPath(projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    internal static string WriteTextFile(string projectRoot, string relativePath, string content, Encoding encoding)
    {
        var path = ProjectPath(projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding);
        return path;
    }

    internal static void WriteTextFiles(string projectRoot, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (relativePath, content) in files)
            WriteTextFile(projectRoot, relativePath, content);
    }

    internal static string WriteBinaryFile(string projectRoot, string relativePath, byte[] content)
    {
        var path = ProjectPath(projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    internal static string WriteSparseFile(string projectRoot, string relativePath, long length)
    {
        var path = ProjectPath(projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(length);
        return path;
    }

    internal static string AppendTextFile(string projectRoot, string relativePath, string content)
    {
        var path = ProjectPath(projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, content);
        return path;
    }

    internal static string ReadTextFile(string projectRoot, string relativePath)
    {
        return File.ReadAllText(ProjectPath(projectRoot, relativePath));
    }

    internal static void InitializeGitRepo(string projectRoot)
    {
        RunGit(projectRoot, "init");
        RunGit(projectRoot, "config", "user.name", "CodeIndex Tests");
        RunGit(projectRoot, "config", "user.email", "tests@codeindex.local");
        RunGit(projectRoot, "config", "commit.gpgsign", "false");
        RunGit(projectRoot, "config", "tag.gpgsign", "false");

        AppendTextFile(projectRoot, Path.Combine(".git", "info", "exclude"), ".cdidx/\n");
    }

    internal static string CreateProjectDb(string projectRoot)
    {
        var dbDir = Path.Combine(projectRoot, ".cdidx");
        Directory.CreateDirectory(dbDir);

        var dbPath = Path.Combine(dbDir, "codeindex.db");
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        writer.SetMeta(DbContext.IndexedProjectRootMetaKey, Path.GetFullPath(projectRoot));
        return dbPath;
    }

    internal static void InsertIndexedFile(string dbPath, string path, string lang, string content, DateTime? modified = null, bool isGenerated = false)
    {
        var normalized = content.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var lineCount = FileIndexer.CountPhysicalLines(content);

        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.InitializeSchema();

            var writer = new DbWriter(db.Connection);
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = path,
                Lang = lang,
                Size = normalized.Length,
                Lines = lineCount,
                Modified = modified ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant(),
                Generated = isGenerated,
            });

            writer.InsertChunks([
                new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = 0,
                    StartLine = 1,
                    EndLine = lines.Length,
                    Content = normalized,
                }
            ]);

            var symbols = SymbolExtractor.Extract(fileId, lang, normalized, path);
            writer.InsertSymbols(symbols);
            writer.InsertReferences(ReferenceExtractor.Extract(fileId, lang, normalized, symbols, path));
        }

        SqlitePoolCleanup.ClearPoolsForWindowsFileRelease();
    }

    internal static void DeleteSqliteDatabaseFiles(string dbPath)
    {
        SqlitePoolCleanup.ClearPoolsAtCollectionBoundary();
        DeleteFile(dbPath);
        DeleteFile(dbPath + "-wal");
        DeleteFile(dbPath + "-shm");
    }

    internal static string RunGit(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process / gitプロセスの起動に失敗");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Trim()}");

        return stdout;
    }

    internal static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        ClearAttributes(path);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                // Avoid clearing SQLite pools on every temp-project cleanup: that is a
                // process-global operation and can interfere with unrelated tests running in
                // parallel. On Windows, a failed recursive delete is the signal that pooled
                // handles may still need releasing, so escalate only on retry.
                // 毎回の cleanup で SQLite pool を落とすと並列テスト全体へ波及するため、
                // 通常経路では触らない。Windows で削除失敗したときだけ最終手段として解放する。
                SqlitePoolCleanup.ClearPoolsForWindowsFileRelease();
                WaitForFileSystemReleaseRetry();
                ClearAttributes(path);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                SqlitePoolCleanup.ClearPoolsForWindowsFileRelease();
                WaitForFileSystemReleaseRetry();
                ClearAttributes(path);
            }
        }
    }

    internal sealed class TempProjectScope : IDisposable
    {
        internal TempProjectScope(string root)
        {
            Root = root;
        }

        internal string Root { get; }

        public void Dispose()
        {
            DeleteDirectory(Root);
        }
    }

    internal static void DeleteFile(string path)
    {
        if (!File.Exists(path))
            return;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (attempt > 0)
                    SqlitePoolCleanup.ClearPoolsForWindowsFileRelease();

                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                SqlitePoolCleanup.ClearPoolsForWindowsFileRelease();
                WaitForFileSystemReleaseRetry();
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                SqlitePoolCleanup.ClearPoolsForWindowsFileRelease();
                WaitForFileSystemReleaseRetry();
            }
        }
    }

    internal static void WaitForFileSystemReleaseRetry()
    {
        // Windows and SQLite can release file handles just after a failed cleanup attempt.
        // Keep that bounded blocking delay in one helper instead of scattering fixed sleeps.
        Thread.Sleep(100);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void CaptureAssemblyLoadContextWeakReferences(
        IEnumerable<AssemblyLoadContext> loadContexts,
        ICollection<WeakReference> weakReferences)
    {
        foreach (var loadContext in loadContexts)
            weakReferences.Add(new WeakReference(loadContext, trackResurrection: false));
    }

    internal static void AssertReleasedAssemblyLoadContexts(IReadOnlyCollection<WeakReference> weakReferences)
    {
        const int maxCollectionAttempts = 10;
        for (var attempt = 0; attempt < maxCollectionAttempts; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (weakReferences.All(reference => !reference.IsAlive))
                return;

            Thread.Sleep(10);
        }

        Assert.All(
            weakReferences,
            reference => Assert.False(reference.IsAlive, "Collectible AssemblyLoadContext remained alive after bounded collection."));
    }

    private static void ClearAttributes(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(dir, FileAttributes.Normal);

        File.SetAttributes(path, FileAttributes.Normal);
    }

    private static bool IsSameOrChildPath(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);

        return string.Equals(normalizedRoot, normalizedCandidate, comparison) ||
               normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }
}
