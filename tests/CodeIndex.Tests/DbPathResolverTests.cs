using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for default DB path resolution.
/// 既定DBパス解決のテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class DbPathResolverTests
{
    [Fact]
    public void ResolveForIndex_UsesProjectLocalCdidxByDefault()
    {
        var projectPath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "tmp", "sample-project");
        var expectedProjectPath = Path.GetFullPath(projectPath);

        var dbPath = DbPathResolver.ResolveForIndex(projectPath, null);

        Assert.Equal(
            Path.Combine(expectedProjectPath, ".cdidx", "codeindex.db"),
            dbPath);
    }

    [Fact]
    public void ResolveForIndex_PrefersExplicitPath()
    {
        var projectPath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "tmp", "sample-project");
        var explicitPath = Path.Combine("custom", "index.db");

        var dbPath = DbPathResolver.ResolveForIndex(projectPath, explicitPath);

        Assert.Equal(explicitPath, dbPath);
    }

    [Fact]
    public void BuildSqliteConnectionString_FileUriKeepsSemicolonPayloadInDataSource_Issue3220()
    {
        const string uri = "file:///tmp/codeindex.db?immutable=1;Mode=ReadWriteCreate;Cache=Shared";

        var connectionString = DbPathResolver.BuildSqliteConnectionString(uri, SqliteOpenMode.ReadOnly);
        var parsed = new SqliteConnectionStringBuilder(connectionString);

        Assert.Equal(uri, parsed.DataSource);
        Assert.Equal(SqliteOpenMode.ReadOnly, parsed.Mode);
        Assert.NotEqual(SqliteOpenMode.ReadWriteCreate, parsed.Mode);
    }

    [Fact]
    public void ResolveForIndex_PrefersExplicitDataDirWhenDbPathMissing()
    {
        var projectPath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "tmp", "sample-project");
        var dataDir = Path.Combine(Path.GetTempPath(), $"cdidx_data_dir_{Guid.NewGuid():N}");

        var resolved = DbPathResolver.ResolveForIndex(projectPath, explicitDbPath: null, explicitDataDir: dataDir);

        Assert.Equal(Path.Combine(Path.GetFullPath(dataDir), "codeindex.db"), resolved.DbPath);
        Assert.Equal(Path.GetFullPath(dataDir), resolved.DataDir);
        Assert.Equal(DbPathResolver.DataDirSourceFlag, resolved.DataDirSource);
    }

    [Fact]
    public void ResolveDataDir_PrefersEnvironmentBeforeXdgAndWorkspace()
    {
        var projectPath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "tmp", "sample-project");
        var envDir = Path.Combine(Path.GetTempPath(), $"cdidx_env_dir_{Guid.NewGuid():N}");
        var xdgDir = Path.Combine(Path.GetTempPath(), $"cdidx_xdg_dir_{Guid.NewGuid():N}");

        var resolved = DbPathResolver.ResolveDataDir(projectPath, explicitDataDir: null, environmentDataDir: envDir, xdgDataHome: xdgDir);

        Assert.Equal(Path.Combine(Path.GetFullPath(envDir), "codeindex.db"), resolved.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceEnv, resolved.DataDirSource);
    }

    [Fact]
    public void ResolveDataDir_UsesStableXdgWorkspaceHashBeforeWorkspaceDefault()
    {
        var projectPath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "tmp", "sample-project");
        var xdgDir = Path.Combine(Path.GetTempPath(), $"cdidx_xdg_dir_{Guid.NewGuid():N}");

        var first = DbPathResolver.ResolveDataDir(projectPath, explicitDataDir: null, environmentDataDir: null, xdgDataHome: xdgDir);
        var second = DbPathResolver.ResolveDataDir(projectPath, explicitDataDir: null, environmentDataDir: null, xdgDataHome: xdgDir);

        Assert.Equal(first.DbPath, second.DbPath);
        Assert.StartsWith(Path.Combine(Path.GetFullPath(xdgDir), "cdidx"), first.DbPath, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("codeindex.db"), first.DbPath, StringComparison.Ordinal);
        Assert.Equal(DbPathResolver.DataDirSourceXdg, first.DataDirSource);
    }

    [Fact]
    public void ResolveDataDirForQuery_WithXdgPrefersAncestorWorkspaceDataDir()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_xdg_root_db");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_query_xdg_config");
        var xdgDir = Path.Combine(Path.GetTempPath(), $"cdidx_xdg_dir_{Guid.NewGuid():N}");
        try
        {
            using var env = IsolateActiveWorkspace(configHome);
            var child = Path.Combine(projectRoot, "src", "App");
            Directory.CreateDirectory(child);
            var indexedRootResolution = DbPathResolver.ResolveDataDir(projectRoot, explicitDataDir: null, environmentDataDir: null, xdgDataHome: xdgDir);
            Directory.CreateDirectory(indexedRootResolution.DataDir!);

            var resolved = DbPathResolver.ResolveDataDirForQuery(
                child,
                explicitDataDir: null,
                environmentDataDir: null,
                xdgDataHome: xdgDir,
                activeWorkspaceLoader: () => null);

            Assert.Equal(indexedRootResolution.DbPath, resolved.DbPath);
            Assert.Equal(indexedRootResolution.DataDir, resolved.DataDir);
            Assert.Equal(DbPathResolver.DataDirSourceXdg, resolved.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(configHome);
            TestProjectHelper.DeleteDirectory(xdgDir);
        }
    }

    [Fact]
    public void ResolveDataDirForQuery_PrefersOutermostAncestorCdidx()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_root_db");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_query_root_config");
        try
        {
            using var env = IsolateActiveWorkspace(configHome);
            var child = Path.Combine(projectRoot, "src", "App");
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(Path.Combine(projectRoot, ".cdidx"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", ".cdidx"));

            var resolved = DbPathResolver.ResolveDataDirForQuery(
                child,
                explicitDataDir: null,
                environmentDataDir: null,
                xdgDataHome: null,
                activeWorkspaceLoader: () => null);

            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), resolved.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, resolved.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ResolveDataDirForQuery_UsesInjectedActiveWorkspaceBeforeAncestorCdidx()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_active_workspace_project");
        var activeRoot = TestProjectHelper.CreateTempProject("cdidx_query_active_workspace_state");
        var activeDb = Path.Combine(activeRoot, ".cdidx", "codeindex.db");
        try
        {
            var child = Path.Combine(projectRoot, "src", "App");
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(Path.Combine(projectRoot, ".cdidx"));

            var resolved = DbPathResolver.ResolveDataDirForQuery(
                child,
                explicitDataDir: null,
                environmentDataDir: null,
                xdgDataHome: null,
                activeWorkspaceLoader: () => new ActiveWorkspaceState("test", activeRoot, activeDb));

            Assert.Equal(Path.GetFullPath(activeDb), resolved.DbPath);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(activeDb)), resolved.DataDir);
            Assert.Equal(DbPathResolver.DataDirSourceActiveWorkspace, resolved.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(activeRoot);
        }
    }

    [Fact]
    public void ResolveDataDirForQuery_FallsBackToCurrentDirectoryWhenNoAncestorCdidxExists()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_no_root_db");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_query_no_root_config");
        try
        {
            using var env = IsolateActiveWorkspace(configHome);
            var child = Path.Combine(projectRoot, "src", "App");
            Directory.CreateDirectory(child);

            var resolved = DbPathResolver.ResolveDataDirForQuery(
                child,
                explicitDataDir: null,
                environmentDataDir: null,
                xdgDataHome: null,
                activeWorkspaceLoader: () => null);

            Assert.Equal(Path.Combine(child, ".cdidx", "codeindex.db"), resolved.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, resolved.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_UsesParentOfCdidxDirectory()
    {
        var projectPath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "tmp", "sample-project");
        var dbPath = Path.Combine(projectPath, ".cdidx", "codeindex.db");

        var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath);

        Assert.Equal(Path.GetFullPath(projectPath), resolved);
    }

    [Fact]
    public void TryResolveWritableMutationDbPath_ReadOnlyUri_ReturnsLocalPath()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_path_resolver_{Guid.NewGuid():N}.db");
        var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";

        var resolved = DbPathResolver.TryResolveWritableMutationDbPath(readOnlyUri, out var writableDbPath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(dbPath), writableDbPath);
    }

    [Fact]
    public void UriRequestsReadOnly_PlainPathWithQuestionMarkSuffix_IsFalse()
    {
        var plainPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_path_resolver_{Guid.NewGuid():N}?immutable=1");

        Assert.False(DbPathResolver.UriRequestsReadOnly(plainPath));
    }

    [Fact]
    public void UriRequestsReadOnly_FileUriWithReadOnlyMode_IsTrue()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_path_resolver_{Guid.NewGuid():N}.db");
        var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?mode=ro";

        Assert.True(DbPathResolver.UriRequestsReadOnly(readOnlyUri));
    }

    [Fact]
    public void UriRequestsReadOnly_OversizedFileUriQuery_ReturnsFalseWithoutScanningQuery()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_path_resolver_{Guid.NewGuid():N}.db");
        var readOnlyUri = new Uri(dbPath).AbsoluteUri +
            "?" +
            new string('a', SqliteFileUri.MaxQueryLength + 1) +
            "&immutable=1";

        Assert.False(DbPathResolver.UriRequestsReadOnly(readOnlyUri));
    }

    [Fact]
    public void TryResolveWritableMutationDbPath_RelativeReadOnlyUri_ReturnsWorkingDirectoryPath()
    {
        var fileName = $"cdidx_db_path_resolver_{Guid.NewGuid():N}.db";
        var readOnlyUri = $"file:{fileName}?mode=ro";

        var resolved = DbPathResolver.TryResolveWritableMutationDbPath(readOnlyUri, out var writableDbPath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(fileName), writableDbPath);
    }

    [Fact]
    public void TryNormalizeDbPath_MalformedFileUri_ReturnsParseErrorWithoutChangingValue()
    {
        const string malformedUri = "file:///tmp/codeindex%ZZ.db?immutable=1";

        var resolved = DbPathResolver.TryNormalizeDbPath(malformedUri, out var normalized, out var parseError);

        Assert.False(resolved);
        Assert.Equal(malformedUri, normalized);
        Assert.NotNull(parseError);
    }

    [Fact]
    public void TryNormalizeDbPath_OversizedFileUri_ReturnsParseErrorWithoutChangingValue()
    {
        var oversizedUri = "file:///" + new string('a', SqliteFileUri.MaxUriLength);

        var resolved = DbPathResolver.TryNormalizeDbPath(oversizedUri, out var normalized, out var parseError);

        Assert.False(resolved);
        Assert.Equal(oversizedUri, normalized);
        Assert.NotNull(parseError);
        Assert.Contains(SqliteFileUri.MaxUriLength.ToString(CultureInfo.InvariantCulture), parseError.Message);
        Assert.DoesNotContain(new string('a', 32), parseError.Message);
    }

    [Fact]
    public void TryNormalizeDbPath_OversizedFileUriQuery_ReturnsParseErrorWithoutChangingValue()
    {
        var oversizedQueryUri = "file:///tmp/codeindex.db?" + new string('a', SqliteFileUri.MaxQueryLength + 1);

        var resolved = DbPathResolver.TryNormalizeDbPath(oversizedQueryUri, out var normalized, out var parseError);

        Assert.False(resolved);
        Assert.Equal(oversizedQueryUri, normalized);
        Assert.NotNull(parseError);
        Assert.Contains(SqliteFileUri.MaxQueryLength.ToString(CultureInfo.InvariantCulture), parseError.Message);
        Assert.DoesNotContain(new string('a', 32), parseError.Message);
    }

    [Fact]
    public void TryValidateExistingCodeIndexDb_OversizedFileUriQuery_ReturnsBoundedErrorWithoutOpening()
    {
        var opened = false;
        var oversizedQueryUri = "file:///tmp/codeindex.db?" + new string('a', SqliteFileUri.MaxQueryLength + 1);

        var resolved = DbContext.TryValidateExistingCodeIndexDb(
            oversizedQueryUri,
            _ =>
            {
                opened = true;
                throw new InvalidOperationException("Unexpected open.");
            },
            _ => throw new InvalidOperationException("Unexpected open."),
            sleep: null,
            out var message,
            out var isNotFound);

        Assert.False(resolved);
        Assert.False(opened);
        Assert.False(isNotFound);
        Assert.Contains(SqliteFileUri.MaxQueryLength.ToString(CultureInfo.InvariantCulture), message);
        Assert.DoesNotContain(new string('a', 32), message);
    }

    [Fact]
    public void DbContext_OversizedFileUriQuery_ThrowsBeforeOpeningSqlite()
    {
        var oversizedQueryUri = "file:///tmp/codeindex.db?" + new string('a', SqliteFileUri.MaxQueryLength + 1);

        var ex = Assert.Throws<FormatException>(() => new DbContext(oversizedQueryUri));

        Assert.Contains(SqliteFileUri.MaxQueryLength.ToString(CultureInfo.InvariantCulture), ex.Message);
        Assert.DoesNotContain(new string('a', 32), ex.Message);
    }

    [Fact]
    public void ToReadOnlyUri_OversizedFileUriQuery_ThrowsBeforeAppendingReadOnlyFlags()
    {
        var oversizedQueryUri = "file:///tmp/codeindex.db?" + new string('a', SqliteFileUri.MaxQueryLength + 1);

        var ex = Assert.Throws<FormatException>(() => DbContext.ToReadOnlyUri(oversizedQueryUri));

        Assert.Contains(SqliteFileUri.MaxQueryLength.ToString(CultureInfo.InvariantCulture), ex.Message);
        Assert.DoesNotContain("immutable=1", ex.Message);
        Assert.DoesNotContain(new string('a', 32), ex.Message);
    }

    [Fact]
    public void TruncateDiagnosticValue_OversizedInput_ReturnsBoundedValueWithLength()
    {
        var oversizedUri = "file:" + new string('x', SqliteFileUri.MaxDiagnosticValueLength + 1);

        var diagnostic = SqliteFileUri.TruncateDiagnosticValue(oversizedUri);

        Assert.True(diagnostic.Length < SqliteFileUri.MaxDiagnosticValueLength + 64);
        Assert.Contains("truncated", diagnostic, StringComparison.Ordinal);
        Assert.Contains(oversizedUri.Length.ToString(CultureInfo.InvariantCulture), diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', SqliteFileUri.MaxDiagnosticValueLength), diagnostic);
    }

    [Fact]
    public void ResolveProjectRootForQuery_PrefersStoredIndexedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_meta_root");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_path_resolver_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

            Assert.Equal(projectRoot, resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteFile(dbPath);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ReadOnlyUri_PrefersStoredIndexedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_meta_uri");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_path_resolver_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var resolved = DbPathResolver.ResolveProjectRootForQuery(readOnlyUri, dbPathExplicit: true);

            Assert.Equal(projectRoot, resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteFile(dbPath);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ProjectLocalDbPrefersCdidxSiblingOverStoredMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_local");
        var staleRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_stale");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, staleRoot);
            }

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath);

            Assert.Equal(projectRoot, resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(staleRoot);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitProjectLocalDbPrefersCdidxSiblingOverStoredMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_local_explicit");
        var staleRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_stale_explicit");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, staleRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

            Assert.Equal(projectRoot, resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(staleRoot);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitProjectLocalDbDoesNotCaseFoldPersistedChecksums()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_upper_checksum");
        var staleRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_upper_checksum_stale");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            Directory.CreateDirectory(Path.Combine(staleRoot, "src"));

            const string indexedContent = "class App {}\n";
            const string staleContent = "class App { void Different() {} }\n";
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), indexedContent);
            File.WriteAllText(Path.Combine(staleRoot, "src", "app.cs"), staleContent);

            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, staleRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", indexedContent);
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "UPDATE files SET checksum = upper(checksum) WHERE path = @path";
                cmd.Parameters.AddWithValue("@path", "src/app.cs");
                cmd.ExecuteNonQuery();
            }

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

            Assert.Equal(staleRoot, resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(staleRoot);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitProjectLocalDbIgnoresEscapingSampleMatches()
    {
        var projectParent = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_escape_parent");
        var staleParent = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_escape_stale_parent");
        var projectRoot = Path.Combine(projectParent, "project");
        var staleRoot = Path.Combine(staleParent, "stale");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(staleRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            Directory.CreateDirectory(Path.Combine(projectParent, "outside"));

            const string outsideContent = "class Outside {}\n";
            File.WriteAllText(Path.Combine(projectParent, "outside", "outside.cs"), outsideContent);

            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, staleRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "../outside/outside.cs", "csharp", outsideContent);

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

            Assert.Equal(staleRoot, resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectParent);
            TestProjectHelper.DeleteDirectory(staleParent);
        }
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    public void TryResolveIndexedFileSampleIoPath_RejectsEscapingRelativeSamples(string samplePath)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_escape_sample");
        try
        {
            var resolved = DbPathResolver.TryResolveIndexedFileSampleIoPath(projectRoot, samplePath, out var ioPath);

            Assert.False(resolved);
            Assert.Equal(string.Empty, ioPath);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("/outside.cs")]
    public void TryResolveIndexedFileSampleIoPath_RejectsRootedSamples(string samplePath)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_rooted_sample");
        try
        {
            var resolved = DbPathResolver.TryResolveIndexedFileSampleIoPath(projectRoot, samplePath, out var ioPath);

            Assert.False(resolved);
            Assert.Equal(string.Empty, ioPath);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void TryResolveIndexedFileSampleIoPath_OnWindowsRejectsDriveAndUncSamples()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_windows_absolute_sample");
        try
        {
            foreach (var samplePath in new[] { "\\outside.cs", "C:/outside.cs", @"C:\outside.cs", @"\\server\share\outside.cs" })
            {
                var resolved = DbPathResolver.TryResolveIndexedFileSampleIoPath(projectRoot, samplePath, out var ioPath);

                Assert.False(resolved);
                Assert.Equal(string.Empty, ioPath);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void TryResolveIndexedFileSampleIoPath_OnPosixPreservesBackslashInFilename()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_posix_backslash_sample");
        try
        {
            const string samplePath = "back\\slash.py";
            var resolved = DbPathResolver.TryResolveIndexedFileSampleIoPath(projectRoot, samplePath, out var ioPath);

            Assert.True(resolved);
            Assert.Equal(Path.GetFullPath(Path.Combine(projectRoot, samplePath)), ioPath);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitProjectLocalReadOnlyUriWithoutMetadataReturnsNull()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_local_uri");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
            }
            using (var db = new DbContext(dbPath))
            {
                using var deleteCmd = db.Connection.CreateCommand();
                deleteCmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                deleteCmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                deleteCmd.ExecuteNonQuery();

                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var resolved = DbPathResolver.ResolveProjectRootForQuery(readOnlyUri, dbPathExplicit: true);

            Assert.Null(resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_CustomDbUnderCdidxPrefersStoredIndexedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_custom_root");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_custom_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "shared.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

            Assert.Equal(projectRoot, resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitExternalCodeIndexDbPrefersStoredIndexedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_explicit_codeindex_root");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_explicit_codeindex_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

            Assert.Equal(projectRoot, resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitExternalCodeIndexDbIgnoresSingleSiblingPathCollision()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_collision_root");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_collision_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            Directory.CreateDirectory(Path.Combine(dbContainerRoot, "src"));

            const string content = "class App {}\n";
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), content);
            File.WriteAllText(Path.Combine(dbContainerRoot, "src", "app.cs"), content);

            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", content);

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

            Assert.Equal(projectRoot, resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitExternalCodeIndexDbWithoutMetadataReturnsNullEvenWhenSiblingPathExists()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_collision_missing_meta_root");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_collision_missing_meta_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            Directory.CreateDirectory(Path.Combine(dbContainerRoot, "src"));

            const string indexedContent = "class App {}\n";
            const string siblingContent = "class App { void Different() {} }\n";
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), indexedContent);
            File.WriteAllText(Path.Combine(dbContainerRoot, "src", "app.cs"), siblingContent);

            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", indexedContent);
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

            Assert.Null(resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitExternalCodeIndexDbSkipsOversizedSiblingChecksumSample()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_oversized_root");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_db_path_resolver_oversized_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            Directory.CreateDirectory(Path.Combine(dbContainerRoot, "src"));

            const string indexedContent = "class App {}\n";
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), indexedContent);
            using (var stream = File.Create(Path.Combine(dbContainerRoot, "src", "app.cs")))
                stream.SetLength(FileIndexer.DefaultMaxFileSizeBytes + 1);

            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", indexedContent);

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

            Assert.Equal(projectRoot, resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ReturnsNullForExplicitDbWithoutMetadata()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_path_resolver_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
            }

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

            Assert.Null(resolved);
        }
        finally
        {
            TestProjectHelper.DeleteFile(dbPath);
        }
    }

    private static EnvironmentVariableScope IsolateActiveWorkspace(string configHome)
    {
        var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        env.Set(ActiveWorkspace.EnvironmentVariable, null);
        env.Set("XDG_CONFIG_HOME", configHome);
        return env;
    }
}
