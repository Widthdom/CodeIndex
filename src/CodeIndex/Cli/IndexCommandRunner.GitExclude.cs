using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static void AddToGitExclude(
        string projectPath,
        string dbPath,
        List<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var projectRoot = Path.GetFullPath(projectPath);
            var gitDir = GitHelper.ResolveGitCommonDir(projectRoot, cancellationToken);
            if (gitDir == null) return;

            if (!GitHelper.TryResolveGitMetadataChildPath(
                    gitDir,
                    "info",
                    expectDirectory: true,
                    allowMissing: true,
                    out var infoDirectory))
            {
                throw new IOException("Unsafe Git metadata info directory.");
            }

            Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(infoDirectory));
            if (!GitHelper.TryResolveGitMetadataChildPath(
                    gitDir,
                    "info",
                    expectDirectory: true,
                    allowMissing: false,
                    out infoDirectory)
                || !GitHelper.TryResolveGitMetadataChildPath(
                    infoDirectory,
                    "exclude",
                    expectDirectory: false,
                    allowMissing: true,
                    out var excludeFile))
            {
                throw new IOException("Unsafe Git metadata exclude path.");
            }

            var dbAbsolutePath = Path.IsPathRooted(dbPath)
                ? Path.GetFullPath(dbPath)
                : Path.GetFullPath(Path.Combine(projectRoot, dbPath));
            var dbDirAbsolute = Path.GetDirectoryName(dbAbsolutePath);
            if (string.IsNullOrEmpty(dbDirAbsolute)) return;

            var dbDirRelative = FileIndexer.NormalizePathSeparators(FileIndexer.GetRelativePathFromDirectory(projectRoot, dbDirAbsolute));
            if (IsOutsideProjectRoot(dbDirRelative)) return;

            string[] patterns;
            if (dbDirRelative == ".")
            {
                var dbFileName = Path.GetFileName(dbAbsolutePath);
                patterns = [dbFileName, $"{dbFileName}-*"];
            }
            else
            {
                patterns = [$"{dbDirRelative.TrimEnd('/')}/"];
            }

            var ioExcludeFile = LongPath.EnsureWindowsPrefix(excludeFile);
            var existingContent = File.Exists(ioExcludeFile)
                ? DataDirectorySecurity.ReadTextWithinLimit(ioExcludeFile, MaxGitExcludeBytes, FileShare.ReadWrite)
                : "";
            if (existingContent is null)
                return;

            var existingLines = existingContent.Split('\n').Select(l => l.TrimEnd('\r')).ToHashSet();

            var missing = patterns.Where(p => !existingLines.Contains(p)).ToList();
            if (missing.Count == 0) return;

            if (!GitHelper.TryResolveGitMetadataChildPath(
                    gitDir,
                    "info",
                    expectDirectory: true,
                    allowMissing: false,
                    out infoDirectory)
                || !GitHelper.TryResolveGitMetadataChildPath(
                    infoDirectory,
                    "exclude",
                    expectDirectory: false,
                    allowMissing: true,
                    out excludeFile))
            {
                throw new IOException("Git metadata exclude path became unsafe before write.");
            }

            var updatedContent = new System.Text.StringBuilder(existingContent);
            if (existingContent.Length > 0 && !existingContent.EndsWith('\n'))
                updatedContent.AppendLine();
            updatedContent.AppendLine("# cdidx (CodeIndex) — auto-generated");
            foreach (var pattern in missing)
                updatedContent.AppendLine(pattern);

            AtomicFileWriter.WriteText(
                excludeFile,
                updatedContent.ToString(),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordIndexRunDiagnostic(diagnostics, "git_exclude_metadata_write_failed", ex);
        }
    }
}
