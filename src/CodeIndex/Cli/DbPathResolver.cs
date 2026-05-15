using System.Security.Cryptography;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Resolves database paths for CLI commands.
/// CLIコマンドのデータベースパスを解決する。
/// </summary>
public static class DbPathResolver
{
    /// <summary>
    /// Resolve the DB path for indexing. When no explicit path is provided,
    /// store the DB under the indexed project's .cdidx directory.
    /// インデックス時のDBパスを解決する。明示指定がない場合は、
    /// 対象プロジェクトの .cdidx ディレクトリ配下に保存する。
    /// </summary>
    public static string ResolveForIndex(string projectPath, string? explicitDbPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitDbPath))
            return explicitDbPath;

        return Path.Combine(Path.GetFullPath(projectPath), ".cdidx", "codeindex.db");
    }

    /// <summary>
    /// Resolve the most likely project root for query commands from the DB path.
    /// クエリ系コマンドのDBパスから、もっとも可能性が高いプロジェクトルートを解決する。
    /// </summary>
    public static string? ResolveProjectRootForQuery(string dbPath, bool dbPathExplicit = false)
    {
        var fullDbPath = Path.GetFullPath(NormalizeDbPath(dbPath));
        var dbDir = Path.GetDirectoryName(fullDbPath);
        if (dbDir == null)
            return null;

        var indexedProjectRoot = TryReadIndexedProjectRoot(dbPath);
        if (indexedProjectRoot == null && !string.Equals(dbPath, fullDbPath, StringComparison.Ordinal))
            indexedProjectRoot = TryReadIndexedProjectRoot(fullDbPath);

        var projectLocalRoot = TryResolveProjectLocalRoot(fullDbPath, dbPath, dbPathExplicit, indexedProjectRoot);
        if (projectLocalRoot != null)
            return projectLocalRoot;

        if (!string.IsNullOrWhiteSpace(indexedProjectRoot))
            return Path.GetFullPath(indexedProjectRoot);

        return null;
    }

    /// <summary>
    /// Normalize a file query path against the indexed project root when the DB exposes one.
    /// If the caller supplied an absolute path under the indexed root, convert it to the
    /// stored index-relative path before lookup. Relative paths pass through unchanged.
    /// DB が indexed project root を持つとき、ファイル query path をその root に合わせて正規化する。
    /// 絶対パスが indexed root 配下なら、lookup 前に保存済みの相対パスへ変換する。
    /// </summary>
    public static string ResolveQueryFilePath(string dbPath, string filePath, bool dbPathExplicit = false)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return filePath;

        var normalizedFilePath = FileIndexer.NormalizePathSeparators(filePath);
        if (!Path.IsPathRooted(filePath))
            return normalizedFilePath;

        var projectRoot = ResolveProjectRootForQuery(dbPath, dbPathExplicit);
        if (string.IsNullOrWhiteSpace(projectRoot))
            return normalizedFilePath;

        var fullProjectRoot = Path.GetFullPath(projectRoot);
        var fullFilePath = Path.GetFullPath(filePath);
        if (!IsUnderDirectory(fullProjectRoot, fullFilePath))
            return normalizedFilePath;

        return FileIndexer.NormalizePathSeparators(Path.GetRelativePath(fullProjectRoot, fullFilePath));
    }

    /// <summary>
    /// Normalize a SQLite `file:///...?immutable=1`-style URI to a plain filesystem path.
    /// Plain paths pass through unchanged. Used by metadata resolution so CLI `--db` URIs
    /// (the read-only escape hatch) don't poison workspace / git lookups that depend on a
    /// real path — feeding `file:///…` into `Path.GetFullPath` otherwise produces paths
    /// like `/cwd/file:/abs/...` and breaks `project_root`, `git_head`, `git_is_dirty`.
    /// SQLite の file: URI をローカルパスに正規化する。平文パスは素通し。
    /// </summary>
    public static string NormalizeDbPath(string dbPath)
    {
        if (!dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return dbPath;
        try
        {
            // Strip query params (?immutable=1 etc.) before URI parsing so LocalPath is clean.
            var qIdx = dbPath.IndexOf('?');
            var trimmed = qIdx >= 0 ? dbPath[..qIdx] : dbPath;
            if (!trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = Uri.UnescapeDataString(trimmed["file:".Length..]);
                return string.IsNullOrWhiteSpace(relativePath)
                    ? dbPath
                    : Path.GetFullPath(relativePath);
            }

            var uri = new Uri(trimmed);
            if (!uri.IsFile)
                return dbPath;

            var localPath = uri.LocalPath;
            return string.IsNullOrWhiteSpace(localPath)
                ? dbPath
                : Path.IsPathRooted(localPath)
                    ? localPath
                    : Path.GetFullPath(localPath);
        }
        catch
        {
            return dbPath;
        }
    }

    /// <summary>
    /// Best-effort: resolve a DB reference used for write commands to a writable filesystem path.
    /// Plain paths pass through; `file:///...?...` URIs are normalized to their local path when possible.
    /// Returns false when a writable local path cannot be derived safely.
    /// 書き込み系コマンド向けに DB 参照をローカルの writable path へ解決する。安全に解決できない場合は false。
    /// </summary>
    public static bool TryResolveWritableMutationDbPath(string dbPath, out string writableDbPath)
    {
        var normalized = NormalizeDbPath(dbPath);
        if (normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            writableDbPath = string.Empty;
            return false;
        }

        writableDbPath = Path.GetFullPath(normalized);
        return true;
    }

    private static string? TryReadIndexedProjectRoot(string dbPath)
        => TryReadMetaString(dbPath, CodeIndex.Database.DbContext.IndexedProjectRootMetaKey);

    /// <summary>
    /// Best-effort read of the persisted git HEAD commit stamped at the end of the
    /// most recent successful full-scan index run. Returns null when the DB does not
    /// expose `indexed_head_commit` (legacy DBs or projects indexed outside a git
    /// checkout). Used at `status` time to surface a worktree branch / HEAD switch
    /// via `worktree_head_changed`. Issues #1508 / #1512.
    /// 直近 full-scan 成功時点で保存された git HEAD を best-effort で読む。legacy DB や
    /// git 外プロジェクトでは null。`status` で worktree HEAD の切替検知に使う。
    /// </summary>
    public static string? TryReadIndexedHeadCommit(string dbPath)
        => TryReadMetaString(dbPath, CodeIndex.Database.DbContext.IndexedHeadCommitMetaKey);

    private static string? TryReadMetaString(string dbPath, string key)
    {
        try
        {
            using var connection = OpenMetadataConnection(dbPath);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            var raw = cmd.ExecuteScalar();
            return raw is string value && !string.IsNullOrWhiteSpace(value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveProjectLocalRoot(string fullDbPath, string dbPath, bool dbPathExplicit, string? indexedProjectRoot)
    {
        var dbDir = Path.GetDirectoryName(fullDbPath);
        if (dbDir == null)
            return null;

        if (!string.Equals(Path.GetFileName(dbDir), ".cdidx", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(fullDbPath), "codeindex.db", StringComparison.OrdinalIgnoreCase))
            return null;

        var siblingRoot = Path.GetDirectoryName(dbDir);
        if (string.IsNullOrWhiteSpace(siblingRoot))
            return null;

        // Implicit default queries always trust the sibling `.cdidx/codeindex.db` layout.
        // 明示指定なしのデフォルト query は `.cdidx/codeindex.db` の sibling layout を常に信頼する。
        if (!dbPathExplicit)
            return siblingRoot;

        // Explicit `--db .../.cdidx/codeindex.db` is ambiguous: it may be a genuine
        // project-local DB, or an external/shared DB whose container happens to use the
        // same filename. Only prefer the sibling path when persisted metadata exists and
        // the indexed contents clearly match that sibling more strongly than the stored
        // root; legacy explicit DBs without stored metadata must not guess.
        // 明示指定の `--db .../.cdidx/codeindex.db` は曖昧なので、保存済み metadata があり、
        // かつ DB内容がその sibling と stored root より強く整合するときだけ sibling を採用する。
        // 保存済み metadata のない legacy explicit DB は推測してはいけない。
        if (SiblingRootMatchesIndexedContents(dbPath, fullDbPath, siblingRoot, indexedProjectRoot))
            return siblingRoot;

        return null;
    }

    private static bool SiblingRootMatchesIndexedContents(string dbPath, string fullDbPath, string siblingRoot, string? indexedProjectRoot)
    {
        if (!string.IsNullOrWhiteSpace(indexedProjectRoot))
        {
            var storedDbPath = Path.Combine(Path.GetFullPath(indexedProjectRoot), ".cdidx", "codeindex.db");
            if (PathsEqual(storedDbPath, fullDbPath))
                return true;
        }

        var samples = TryReadIndexedFileSamples(dbPath);
        if (samples.Count == 0)
            return false;

        var siblingMatches = CountMatchingSamples(siblingRoot, samples);
        if (string.IsNullOrWhiteSpace(indexedProjectRoot))
            return false;

        var storedRootMatches = CountMatchingSamples(Path.GetFullPath(indexedProjectRoot), samples);
        if (siblingMatches.ChecksumMatches != storedRootMatches.ChecksumMatches)
            return siblingMatches.ChecksumMatches > storedRootMatches.ChecksumMatches;

        if (siblingMatches.PathExistsMatches != storedRootMatches.PathExistsMatches)
            return siblingMatches.PathExistsMatches > storedRootMatches.PathExistsMatches;

        return false;
    }

    private static SqliteConnection OpenMetadataConnection(string dbPath)
    {
        if (dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && UriRequestsReadOnly(dbPath))
            return new SqliteConnection($"Data Source={dbPath}");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    public static bool UriRequestsReadOnly(string uriText)
    {
        if (!uriText.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return false;

        var qIdx = uriText.IndexOf('?');
        if (qIdx < 0) return false;
        var query = uriText[(qIdx + 1)..];
        foreach (var raw in query.Split('&'))
        {
            var seg = raw.Trim();
            if (seg.Equals("immutable=1", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("mode=ro", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }

    private static bool IsUnderDirectory(string parentDirectory, string candidatePath)
    {
        if (PathsEqual(parentDirectory, candidatePath))
            return true;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var trimmedParent = parentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentWithSeparator = trimmedParent
            + Path.DirectorySeparatorChar;
        var parentWithAltSeparator = trimmedParent
            + Path.AltDirectorySeparatorChar;
        return candidatePath.StartsWith(parentWithSeparator, comparison)
            || candidatePath.StartsWith(parentWithAltSeparator, comparison);
    }

    private static List<IndexedFileSample> TryReadIndexedFileSamples(string dbPath)
    {
        try
        {
            using var connection = OpenMetadataConnection(dbPath);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT path, checksum
                FROM files
                WHERE path IS NOT NULL
                  AND checksum IS NOT NULL
                  AND checksum != ''
                ORDER BY id
                LIMIT 5
                """;

            using var reader = cmd.ExecuteReader();
            var samples = new List<IndexedFileSample>();
            while (reader.Read())
            {
                samples.Add(new IndexedFileSample(
                    reader.GetString(0),
                    reader.GetString(1)));
            }

            return samples;
        }
        catch
        {
            // Fall back to persisted metadata / 永続化 metadata 側へフォールバック
            return [];
        }
    }

    private static SampleMatchResult CountMatchingSamples(string candidateRoot, IReadOnlyList<IndexedFileSample> samples)
    {
        var checksumMatches = 0;
        var pathExistsMatches = 0;
        foreach (var sample in samples)
        {
            try
            {
                var absolutePath = Path.Combine(candidateRoot, sample.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absolutePath))
                    continue;

                pathExistsMatches++;
                var checksum = ComputeChecksum(File.ReadAllBytes(absolutePath));
                if (string.Equals(checksum, sample.Checksum, StringComparison.OrdinalIgnoreCase))
                    checksumMatches++;
            }
            catch
            {
                // Ignore unreadable samples and keep comparing the rest.
                // 読み込めないサンプルは無視して残りを比較する。
            }
        }

        return new SampleMatchResult(checksumMatches, pathExistsMatches);
    }

    private static string ComputeChecksum(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private readonly record struct SampleMatchResult(int ChecksumMatches, int PathExistsMatches);
    private sealed record IndexedFileSample(string RelativePath, string Checksum);
}
