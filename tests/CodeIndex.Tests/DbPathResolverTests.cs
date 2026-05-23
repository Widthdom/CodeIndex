using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

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
}
