using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CodeIndex.Tests;

public class DbPathResolverPureTests
{
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
    public void ResolveDataDirForQuery_UsesInjectedActiveWorkspaceBeforeAncestorCdidx()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_query_active_workspace_project");
        using var active = TestProjectHelper.CreateTempProjectScope("cdidx_query_active_workspace_state");
        var projectRoot = project.Root;
        var activeRoot = active.Root;
        var activeDb = Path.Combine(activeRoot, ".cdidx", "codeindex.db");
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
    public void TryNormalizeDbPath_MalformedFileUri_ReturnsParseErrorWithoutChangingValue()
    {
        const string malformedUri = "file:///tmp/codeindex%ZZ.db?immutable=1";

        var resolved = DbPathResolver.TryNormalizeDbPath(malformedUri, out var normalized, out var parseError);

        Assert.False(resolved);
        Assert.Equal(malformedUri, normalized);
        Assert.NotNull(parseError);
    }

    [Theory]
    [InlineData("file:sub%2fdir/codeindex.db")]
    [InlineData("file:sub%5cdir/codeindex.db")]
    [InlineData("file:%2e%2e/codeindex.db")]
    public void TryNormalizeDbPath_RejectsEncodedPathBoundaries_Issue3789(string dbUri)
    {
        var resolved = DbPathResolver.TryNormalizeDbPath(dbUri, out var normalized, out var parseError);

        Assert.False(resolved);
        Assert.Equal(dbUri, normalized);
        Assert.NotNull(parseError);
    }

    [Fact]
    public void TryNormalizeDbPath_FileUriWithDecodedSpace_NormalizesOnce_Issue3789()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx decoded space {Guid.NewGuid():N}.db");
        var decodedSpaceUri = new Uri(dbPath).AbsoluteUri.Replace("%20", " ", StringComparison.Ordinal);

        var resolved = DbPathResolver.TryNormalizeDbPath(decodedSpaceUri, out var normalized, out var parseError);

        Assert.True(resolved);
        Assert.Null(parseError);
        Assert.Equal(Path.GetFullPath(dbPath), normalized);
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
}

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
    public void ResolveDataDirForQuery_WithXdgPrefersAncestorWorkspaceDataDir()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_query_xdg_root_db");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_query_xdg_config");
        using var xdg = TestProjectHelper.CreateTempProjectScope("cdidx_xdg_dir");
        var projectRoot = project.Root;
        var configHome = config.Root;
        var xdgDir = xdg.Root;
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

    [Fact]
    public void ResolveDataDirForQuery_PrefersOutermostAncestorCdidx()
    {
        using var fixture = TestProjectHelper.CreateTempProjectScope("cdidx_query_root_db");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_query_root_config");
        var fixtureRoot = fixture.Root;
        var projectRoot = Path.Combine(fixtureRoot, "workspace");
        var configHome = config.Root;
        using var env = IsolateActiveWorkspace(configHome);
        var child = Path.Combine(projectRoot, "src", "App");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(Path.Combine(fixtureRoot, ".cdidx"));
        Directory.CreateDirectory(Path.Combine(projectRoot, ".cdidx"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", ".cdidx"));

        var resolved = DbPathResolver.ResolveDataDirForQuery(
            child,
            explicitDataDir: null,
            environmentDataDir: null,
            xdgDataHome: null,
            activeWorkspaceLoader: () => null,
            ancestorSearchRoot: projectRoot);

        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), resolved.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, resolved.DataDirSource);
    }

    [Fact]
    public void ResolveDataDirForQuery_FallsBackToCurrentDirectoryWhenNoAncestorCdidxExists()
    {
        using var fixture = TestProjectHelper.CreateTempProjectScope("cdidx_query_no_root_db");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_query_no_root_config");
        var fixtureRoot = fixture.Root;
        var projectRoot = Path.Combine(fixtureRoot, "workspace");
        var configHome = config.Root;
        using var env = IsolateActiveWorkspace(configHome);
        var child = Path.Combine(projectRoot, "src", "App");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(Path.Combine(fixtureRoot, ".cdidx"));

        var resolved = DbPathResolver.ResolveDataDirForQuery(
            child,
            explicitDataDir: null,
            environmentDataDir: null,
            xdgDataHome: null,
            activeWorkspaceLoader: () => null,
            ancestorSearchRoot: projectRoot);

        Assert.Equal(Path.Combine(child, ".cdidx", "codeindex.db"), resolved.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, resolved.DataDirSource);
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
    public void TryResolveWritableMutationDbPath_RelativeReadOnlyUri_ReturnsWorkingDirectoryPath()
    {
        var fileName = $"cdidx_db_path_resolver_{Guid.NewGuid():N}.db";
        var readOnlyUri = $"file:{fileName}?mode=ro";

        var resolved = DbPathResolver.TryResolveWritableMutationDbPath(readOnlyUri, out var writableDbPath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(fileName), writableDbPath);
    }

    [Fact]
    public void DbContext_OversizedFileUriQuery_ThrowsBeforeOpeningSqlite()
    {
        var oversizedQueryUri = "file:///tmp/codeindex.db?" + new string('a', SqliteFileUri.MaxQueryLength + 1);

        var ex = Assert.Throws<FormatException>(() => new DbContext(DbOpenIntent.WriteIndex, oversizedQueryUri));

        Assert.Contains(SqliteFileUri.MaxQueryLength.ToString(CultureInfo.InvariantCulture), ex.Message);
        Assert.DoesNotContain(new string('a', 32), ex.Message);
    }

    [Fact]
    public void ResolveProjectRootForQuery_PrefersStoredIndexedProjectRootMetadata()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_meta_root");
        var projectRoot = project.Root;
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_path_resolver_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            TestProjectHelper.DeleteFile(dbPath);
        }
    }

    [Fact]
    public void MetadataStringProbesReturnNullOnFilesystemExceptions_Issue3175()
    {
        try
        {
            DbPathResolver.OpenMetadataConnectionForTesting = _ => throw new IOException("simulated metadata probe failure");

            Assert.Null(DbPathResolver.TryReadIndexedHeadCommit("unreadable.db"));
            Assert.False(DbPathResolver.TryHasIndexedHeadCommitBranchStamp("unreadable.db"));
        }
        finally
        {
            DbPathResolver.OpenMetadataConnectionForTesting = null;
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_MetadataSampleProbeFilesystemErrorReturnsNull_Issue3175()
    {
        using var container = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_probe_failure");
        var dbContainerRoot = container.Root;
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            DbPathResolver.OpenMetadataConnectionForTesting = _ => throw new IOException("simulated metadata sample failure");

            var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

            Assert.Null(resolved);
        }
        finally
        {
            DbPathResolver.OpenMetadataConnectionForTesting = null;
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ReadOnlyUri_PrefersStoredIndexedProjectRootMetadata()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_meta_uri");
        var projectRoot = project.Root;
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_path_resolver_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            TestProjectHelper.DeleteFile(dbPath);
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_StampedProjectLocalReadOnlyUriAvoidsPathCasingProbe_Issue3828()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_case_stamp");
        var projectRoot = project.Root;
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            writer.SetMeta(DbContext.WorkspacePathCaseSensitiveMetaKey, "true");
        }

        lock (PathCasingTestLock.Gate)
        {
            var previousProbe = PathCasing.IgnoreCaseProbeForTesting;
            try
            {
                PathCasing.ResetCacheForTests();
                PathCasing.IgnoreCaseProbeForTesting = _ => throw new IOException("path casing probe should not run");

                var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
                var resolved = DbPathResolver.ResolveProjectRootForQuery(readOnlyUri, dbPathExplicit: true);

                Assert.Equal(projectRoot, resolved);
            }
            finally
            {
                PathCasing.IgnoreCaseProbeForTesting = previousProbe;
                PathCasing.ResetCacheForTests();
            }
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_ProjectLocalDbPrefersCdidxSiblingOverStoredMetadata()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_local");
        using var stale = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_stale");
        var projectRoot = project.Root;
        var staleRoot = stale.Root;
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            writer.SetMeta(DbContext.IndexedProjectRootMetaKey, staleRoot);
        }

        var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath);

        Assert.Equal(projectRoot, resolved);
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitProjectLocalDbPrefersCdidxSiblingOverStoredMetadata()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_local_explicit");
        using var stale = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_stale_explicit");
        var projectRoot = project.Root;
        var staleRoot = stale.Root;
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitProjectLocalDbDoesNotCaseFoldPersistedChecksums()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_upper_checksum");
        using var stale = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_upper_checksum_stale");
        var projectRoot = project.Root;
        var staleRoot = stale.Root;
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        Directory.CreateDirectory(Path.Combine(staleRoot, "src"));

        const string indexedContent = "class App {}\n";
        const string staleContent = "class App { void Different() {} }\n";
        File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), indexedContent);
        File.WriteAllText(Path.Combine(staleRoot, "src", "app.cs"), staleContent);

        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            writer.SetMeta(DbContext.IndexedProjectRootMetaKey, staleRoot);
        }
        TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", indexedContent);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "UPDATE files SET checksum = upper(checksum) WHERE path = @path";
            cmd.Parameters.AddWithValue("@path", "src/app.cs");
            cmd.ExecuteNonQuery();
        }

        var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

        Assert.Equal(staleRoot, resolved);
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitProjectLocalDbIgnoresEscapingSampleMatches()
    {
        using var projectParentScope = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_escape_parent");
        using var staleParentScope = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_escape_stale_parent");
        var projectParent = projectParentScope.Root;
        var staleParent = staleParentScope.Root;
        var projectRoot = Path.Combine(projectParent, "project");
        var staleRoot = Path.Combine(staleParent, "stale");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(staleRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        Directory.CreateDirectory(Path.Combine(projectParent, "outside"));

        const string outsideContent = "class Outside {}\n";
        File.WriteAllText(Path.Combine(projectParent, "outside", "outside.cs"), outsideContent);

        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            writer.SetMeta(DbContext.IndexedProjectRootMetaKey, staleRoot);
        }
        TestProjectHelper.InsertIndexedFile(dbPath, "../outside/outside.cs", "csharp", outsideContent);

        var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

        Assert.Equal(staleRoot, resolved);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    public void TryResolveIndexedFileSampleIoPath_RejectsEscapingRelativeSamples(string samplePath)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_escape_sample");
        var projectRoot = project.Root;
        var resolved = DbPathResolver.TryResolveIndexedFileSampleIoPath(projectRoot, samplePath, out var ioPath);

        Assert.False(resolved);
        Assert.Equal(string.Empty, ioPath);
    }

    [Theory]
    [InlineData("/outside.cs")]
    public void TryResolveIndexedFileSampleIoPath_RejectsRootedSamples(string samplePath)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_rooted_sample");
        var projectRoot = project.Root;
        var resolved = DbPathResolver.TryResolveIndexedFileSampleIoPath(projectRoot, samplePath, out var ioPath);

        Assert.False(resolved);
        Assert.Equal(string.Empty, ioPath);
    }

    [Fact]
    public void TryResolveIndexedFileSampleIoPath_OnWindowsRejectsDriveAndUncSamples()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_windows_absolute_sample");
        var projectRoot = project.Root;
        foreach (var samplePath in new[] { "\\outside.cs", "C:/outside.cs", @"C:\outside.cs", @"\\server\share\outside.cs" })
        {
            var resolved = DbPathResolver.TryResolveIndexedFileSampleIoPath(projectRoot, samplePath, out var ioPath);

            Assert.False(resolved);
            Assert.Equal(string.Empty, ioPath);
        }
    }

    [Fact]
    public void TryResolveIndexedFileSampleIoPath_OnPosixPreservesBackslashInFilename()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_posix_backslash_sample");
        var projectRoot = project.Root;
        const string samplePath = "back\\slash.py";
        var resolved = DbPathResolver.TryResolveIndexedFileSampleIoPath(projectRoot, samplePath, out var ioPath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(Path.Combine(projectRoot, samplePath)), ioPath);
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitProjectLocalReadOnlyUriWithoutMetadataReturnsNull()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_local_uri");
        var projectRoot = project.Root;
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
            }
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void ResolveProjectRootForQuery_CustomDbUnderCdidxPrefersStoredIndexedProjectRootMetadata()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_custom_root");
        using var container = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_custom_container");
        var projectRoot = project.Root;
        var dbContainerRoot = container.Root;
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "shared.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
        }

        var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

        Assert.Equal(projectRoot, resolved);
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitExternalCodeIndexDbPrefersStoredIndexedProjectRootMetadata()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_explicit_codeindex_root");
        using var container = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_explicit_codeindex_container");
        var projectRoot = project.Root;
        var dbContainerRoot = container.Root;
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
        }

        var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

        Assert.Equal(projectRoot, resolved);
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitExternalCodeIndexDbIgnoresSingleSiblingPathCollision()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_collision_root");
        using var container = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_collision_container");
        var projectRoot = project.Root;
        var dbContainerRoot = container.Root;
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        Directory.CreateDirectory(Path.Combine(dbContainerRoot, "src"));

        const string content = "class App {}\n";
        File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), content);
        File.WriteAllText(Path.Combine(dbContainerRoot, "src", "app.cs"), content);

        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
        }
        TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", content);

        var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

        Assert.Equal(projectRoot, resolved);
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitExternalCodeIndexDbWithoutMetadataReturnsNullEvenWhenSiblingPathExists()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_collision_missing_meta_root");
        using var container = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_collision_missing_meta_container");
        var projectRoot = project.Root;
        var dbContainerRoot = container.Root;
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        Directory.CreateDirectory(Path.Combine(dbContainerRoot, "src"));

        const string indexedContent = "class App {}\n";
        const string siblingContent = "class App { void Different() {} }\n";
        File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), indexedContent);
        File.WriteAllText(Path.Combine(dbContainerRoot, "src", "app.cs"), siblingContent);

        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.InitializeSchema();
        }
        TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", indexedContent);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
            cmd.ExecuteNonQuery();
        }

        var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveProjectRootForQuery_ExplicitExternalCodeIndexDbSkipsOversizedSiblingChecksumSample()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_oversized_root");
        using var container = TestProjectHelper.CreateTempProjectScope("cdidx_db_path_resolver_oversized_container");
        var projectRoot = project.Root;
        var dbContainerRoot = container.Root;
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        Directory.CreateDirectory(Path.Combine(dbContainerRoot, "src"));

        const string indexedContent = "class App {}\n";
        File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), indexedContent);
        using (var stream = File.Create(Path.Combine(dbContainerRoot, "src", "app.cs")))
            stream.SetLength(FileIndexer.DefaultMaxFileSizeBytes + 1);

        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
        }
        TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", indexedContent);

        var resolved = DbPathResolver.ResolveProjectRootForQuery(dbPath, dbPathExplicit: true);

        Assert.Equal(projectRoot, resolved);
    }

    [Fact]
    public void ResolveProjectRootForQuery_ReturnsNullForExplicitDbWithoutMetadata()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_path_resolver_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
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
