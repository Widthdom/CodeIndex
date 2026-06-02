using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace CodeIndex.Cli;

/// <summary>
/// Resolves database paths for CLI commands.
/// CLIコマンドのデータベースパスを解決する。
/// </summary>
public static class DbPathResolver
{
    public const string DataDirEnvironmentVariable = "CDIDX_DATA_DIR";
    public const string DataDirSourceFlag = "flag";
    public const string DataDirSourceEnv = "env";
    public const string DataDirSourceActiveWorkspace = "active_workspace";
    public const string DataDirSourceXdg = "xdg";
    public const string DataDirSourceWorkspace = "workspace";

    /// <summary>
    /// Resolve the DB path for indexing. When no explicit path is provided,
    /// store the DB under the indexed project's .cdidx directory.
    /// インデックス時のDBパスを解決する。明示指定がない場合は、
    /// 対象プロジェクトの .cdidx ディレクトリ配下に保存する。
    /// </summary>
    public static string ResolveForIndex(string projectPath, string? explicitDbPath)
        => ResolveForIndex(projectPath, explicitDbPath, explicitDataDir: null).DbPath;

    public static DbPathResolution ResolveForIndex(string projectPath, string? explicitDbPath, string? explicitDataDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDbPath))
            return new DbPathResolution(explicitDbPath, null, null);

        return ResolveDataDir(projectPath, explicitDataDir, Environment.GetEnvironmentVariable(DataDirEnvironmentVariable), Environment.GetEnvironmentVariable("XDG_DATA_HOME"));
    }

    public static DbPathResolution ResolveForQuery(string workspacePath, string? explicitDbPath, string? explicitDataDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDbPath))
            return new DbPathResolution(explicitDbPath, null, null);

        return ResolveDataDirForQuery(workspacePath, explicitDataDir, Environment.GetEnvironmentVariable(DataDirEnvironmentVariable), Environment.GetEnvironmentVariable("XDG_DATA_HOME"));
    }

    internal static DbPathResolution ResolveDataDir(string workspacePath, string? explicitDataDir, string? environmentDataDir, string? xdgDataHome)
    {
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        if (!string.IsNullOrWhiteSpace(explicitDataDir))
            return BuildDataDirResolution(explicitDataDir, DataDirSourceFlag);

        if (!string.IsNullOrWhiteSpace(environmentDataDir))
            return BuildDataDirResolution(environmentDataDir, DataDirSourceEnv);

        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            return BuildDataDirResolution(BuildXdgDataDir(xdgDataHome, fullWorkspacePath), DataDirSourceXdg);
        }

        return BuildDataDirResolution(Path.Combine(fullWorkspacePath, ".cdidx"), DataDirSourceWorkspace);
    }

    internal static DbPathResolution ResolveDataDirForQuery(
        string workspacePath,
        string? explicitDataDir,
        string? environmentDataDir,
        string? xdgDataHome,
        Func<ActiveWorkspaceState?>? activeWorkspaceLoader = null)
    {
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        if (!string.IsNullOrWhiteSpace(explicitDataDir))
            return BuildDataDirResolution(explicitDataDir, DataDirSourceFlag);

        if (!string.IsNullOrWhiteSpace(environmentDataDir))
            return BuildDataDirResolution(environmentDataDir, DataDirSourceEnv);

        var active = (activeWorkspaceLoader ?? ActiveWorkspace.Load)();
        if (active != null)
            return new DbPathResolution(active.DbPath, Path.GetDirectoryName(active.DbPath), DataDirSourceActiveWorkspace);

        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            var ancestorXdgDataDir = TryResolveOutermostAncestorXdgDataDir(fullWorkspacePath, xdgDataHome);
            if (ancestorXdgDataDir != null)
                return BuildDataDirResolution(ancestorXdgDataDir, DataDirSourceXdg);
            return BuildDataDirResolution(BuildXdgDataDir(xdgDataHome, fullWorkspacePath), DataDirSourceXdg);
        }

        var workspaceRootDataDir = TryResolveOutermostAncestorDataDir(fullWorkspacePath);
        if (workspaceRootDataDir != null)
            return BuildDataDirResolution(workspaceRootDataDir, DataDirSourceWorkspace);

        return BuildDataDirResolution(Path.Combine(fullWorkspacePath, ".cdidx"), DataDirSourceWorkspace);
    }

    private static DbPathResolution BuildDataDirResolution(string dataDir, string source)
    {
        var fullDataDir = Path.GetFullPath(dataDir);
        return new DbPathResolution(Path.Combine(fullDataDir, "codeindex.db"), fullDataDir, source);
    }

    private static string BuildXdgDataDir(string xdgDataHome, string workspacePath)
        => Path.Combine(xdgDataHome, "cdidx", ComputeWorkspaceHash(workspacePath));

    private static string ComputeWorkspaceHash(string workspacePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspacePath)));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private static string? TryResolveOutermostAncestorDataDir(string workspacePath)
    {
        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(workspacePath));
        }
        catch
        {
            return null;
        }

        string? selected = null;
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".cdidx");
            if (Directory.Exists(LongPath.EnsureWindowsPrefix(candidate)))
                selected = candidate;
            current = current.Parent;
        }

        return selected;
    }

    private static string? TryResolveOutermostAncestorXdgDataDir(string workspacePath, string xdgDataHome)
    {
        string? current;
        try
        {
            current = Path.GetFullPath(workspacePath);
        }
        catch
        {
            return null;
        }

        string? selected = null;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = BuildXdgDataDir(xdgDataHome, current);
            if (Directory.Exists(LongPath.EnsureWindowsPrefix(candidate)) ||
                File.Exists(LongPath.EnsureWindowsPrefix(Path.Combine(candidate, "codeindex.db"))))
                selected = candidate;
            current = Path.GetDirectoryName(current);
        }

        return selected;
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

        var normalizedFilePath = FileIndexer.NormalizeIndexPath(filePath);
        if (!Path.IsPathRooted(filePath))
            return normalizedFilePath;

        var projectRoot = ResolveProjectRootForQuery(dbPath, dbPathExplicit);
        if (string.IsNullOrWhiteSpace(projectRoot))
            return normalizedFilePath;

        var fullProjectRoot = Path.GetFullPath(projectRoot);
        var fullFilePath = Path.GetFullPath(filePath);
        if (!IsUnderDirectory(fullProjectRoot, fullFilePath))
            return normalizedFilePath;

        return FileIndexer.NormalizeIndexPath(Path.GetRelativePath(fullProjectRoot, fullFilePath));
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
        if (TryNormalizeDbPath(dbPath, out var normalized, out var parseError))
            return normalized;

        if (parseError != null)
            GlobalToolLog.Error($"db_path_uri_parse_failed db={QuoteLogValue(dbPath)} exception={QuoteLogValue(parseError.Message)}");

        return dbPath;
    }

    internal static bool TryNormalizeDbPath(string dbPath, out string normalizedDbPath, out Exception? parseError)
    {
        normalizedDbPath = dbPath;
        parseError = null;
        if (!dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            // Strip query params (?immutable=1 etc.) before URI parsing so LocalPath is clean.
            var qIdx = dbPath.IndexOf('?');
            var trimmed = qIdx >= 0 ? dbPath[..qIdx] : dbPath;
            if (ContainsInvalidPercentEscape(trimmed))
                throw new FormatException("Invalid percent escape in SQLite file URI.");

            if (!trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = Uri.UnescapeDataString(trimmed["file:".Length..]);
                normalizedDbPath = string.IsNullOrWhiteSpace(relativePath)
                    ? dbPath
                    : Path.GetFullPath(relativePath);
                return true;
            }

            var uri = new Uri(trimmed);
            if (!uri.IsFile)
                return true;

            var localPath = uri.LocalPath;
            normalizedDbPath = string.IsNullOrWhiteSpace(localPath)
                ? dbPath
                : Path.IsPathRooted(localPath)
                    ? localPath
                    : Path.GetFullPath(localPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException)
        {
            parseError = ex;
            return false;
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

    public static string? TryReadIndexedHeadBranch(string dbPath)
        => TryReadMetaString(dbPath, CodeIndex.Database.DbContext.IndexedHeadBranchMetaKey);

    public static string? TryReadIndexedHeadCommitBranch(string dbPath)
        => TryReadMetaString(dbPath, CodeIndex.Database.DbContext.IndexedHeadCommitBranchMetaKey);

    public static bool TryHasIndexedHeadCommitBranchStamp(string dbPath)
        => TryMetaKeyExists(dbPath, CodeIndex.Database.DbContext.IndexedHeadCommitBranchMetaKey);

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
        catch (SqliteException)
        {
            return null;
        }
    }

    private static bool TryMetaKeyExists(string dbPath, string key)
    {
        try
        {
            using var connection = OpenMetadataConnection(dbPath);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM codeindex_meta WHERE key = @key LIMIT 1";
            cmd.Parameters.AddWithValue("@key", key);
            return cmd.ExecuteScalar() != null;
        }
        catch (SqliteException)
        {
            return false;
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
        => PathCasing.PathsEqual(left, right);

    private static string QuoteLogValue(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static bool IsUnderDirectory(string parentDirectory, string candidatePath)
        => PathCasing.IsPathEqualOrParent(parentDirectory, candidatePath);

    private static bool ContainsInvalidPercentEscape(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
                continue;

            if (i + 2 >= value.Length || !IsHexDigit(value[i + 1]) || !IsHexDigit(value[i + 2]))
                return true;

            i += 2;
        }

        return false;
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

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
        catch (SqliteException)
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
                if (!TryResolveIndexedFileSampleIoPath(candidateRoot, sample.RelativePath, out var ioPath))
                    continue;

                if (!File.Exists(ioPath))
                    continue;

                pathExistsMatches++;
                // Use the FileIndexer helper so cross-OS clones (CRLF vs LF) still match
                // checksums recorded by an indexer running on a different OS.
                // FileIndexer のヘルパを使い、OS をまたいだ clone (CRLF と LF) でも、
                // 他 OS で生成された checksum と引き続き一致するようにする。
                if (FileIndexer.TryComputeChecksum(ioPath, FileIndexer.DefaultMaxFileSizeBytes, out var checksum) &&
                    string.Equals(checksum, sample.Checksum, StringComparison.Ordinal))
                {
                    checksumMatches++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore unreadable samples and keep comparing the rest.
                // 読み込めないサンプルは無視して残りを比較する。
            }
        }

        return new SampleMatchResult(checksumMatches, pathExistsMatches);
    }

    internal static bool TryResolveIndexedFileSampleIoPath(string candidateRoot, string sampleRelativePath, out string ioPath)
    {
        ioPath = string.Empty;
        if (string.IsNullOrWhiteSpace(sampleRelativePath) || IsRootedOrAbsoluteLikeSamplePath(sampleRelativePath))
            return false;

        try
        {
            var normalizedRoot = Path.GetFullPath(candidateRoot);
            var relativePath = NormalizeSampleRelativePath(sampleRelativePath);
            var absolutePath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
            if (!IsUnderDirectory(normalizedRoot, absolutePath))
                return false;

            ioPath = LongPath.EnsureWindowsPrefix(absolutePath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsRootedOrAbsoluteLikeSamplePath(string samplePath)
        => Path.IsPathRooted(samplePath);

    private static string NormalizeSampleRelativePath(string sampleRelativePath)
        => Path.DirectorySeparatorChar == '\\'
            ? sampleRelativePath.Replace('/', Path.DirectorySeparatorChar)
            : sampleRelativePath;

    private readonly record struct SampleMatchResult(int ChecksumMatches, int PathExistsMatches);
    private sealed record IndexedFileSample(string RelativePath, string Checksum);
}

public sealed record DbPathResolution(string DbPath, string? DataDir, string? DataDirSource);
