using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

internal static class TestProjectHelper
{
    internal static string CreateTempProject(string prefix)
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        return projectRoot;
    }

    internal static void InitializeGitRepo(string projectRoot)
    {
        RunGit(projectRoot, "init");
        RunGit(projectRoot, "config", "user.name", "CodeIndex Tests");
        RunGit(projectRoot, "config", "user.email", "tests@codeindex.local");
        RunGit(projectRoot, "config", "commit.gpgsign", "false");
        RunGit(projectRoot, "config", "tag.gpgsign", "false");

        var excludePath = Path.Combine(projectRoot, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
        File.AppendAllText(excludePath, ".cdidx/\n");
    }

    internal static string CreateProjectDb(string projectRoot)
    {
        var dbDir = Path.Combine(projectRoot, ".cdidx");
        Directory.CreateDirectory(dbDir);

        var dbPath = Path.Combine(dbDir, "codeindex.db");
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        writer.SetMeta(DbContext.IndexedProjectRootMetaKey, Path.GetFullPath(projectRoot));
        return dbPath;
    }

    internal static void InsertIndexedFile(string dbPath, string path, string lang, string content, DateTime? modified = null)
    {
        var normalized = content.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var lineCount = FileIndexer.CountPhysicalLines(content);

        using (var db = new DbContext(dbPath))
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
            writer.InsertReferences(ReferenceExtractor.Extract(fileId, lang, normalized, symbols));
        }

        SqlitePoolCleanup.ClearPoolsForWindowsFileRelease();
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
                Thread.Sleep(100);
                ClearAttributes(path);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                SqlitePoolCleanup.ClearPoolsForWindowsFileRelease();
                Thread.Sleep(100);
                ClearAttributes(path);
            }
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
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                SqlitePoolCleanup.ClearPoolsForWindowsFileRelease();
                Thread.Sleep(100);
            }
        }
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
}
