using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for workspace-level metadata enrichment.
/// ワークスペース単位メタデータ補強のテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class WorkspaceMetadataEnricherTests
{
    [Fact]
    public void Enrich_StatusResult_PopulatesProjectRootHeadAndDirty()
    {
        var (projectRoot, dbPath, expectedHead) = CreateDirtyGitProject("cdidx_workspace_status");
        try
        {
            var status = new StatusResult();

            WorkspaceMetadataEnricher.Enrich(status, dbPath);

            Assert.Equal(projectRoot, status.ProjectRoot);
            Assert.Equal(expectedHead, status.GitHead);
            Assert.True(status.GitIsDirty);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Enrich_RepoMapResult_PopulatesProjectRootHeadAndDirty()
    {
        var (projectRoot, dbPath, expectedHead) = CreateDirtyGitProject("cdidx_workspace_map");
        try
        {
            var map = new RepoMapResult();

            WorkspaceMetadataEnricher.Enrich(map, dbPath);

            Assert.Equal(projectRoot, map.ProjectRoot);
            Assert.Equal(expectedHead, map.GitHead);
            Assert.True(map.GitIsDirty);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Enrich_SymbolAnalysisResult_PopulatesProjectRootHeadAndDirty()
    {
        var (projectRoot, dbPath, expectedHead) = CreateDirtyGitProject("cdidx_workspace_analysis");
        try
        {
            var analysis = new SymbolAnalysisResult();

            WorkspaceMetadataEnricher.Enrich(analysis, dbPath);

            Assert.Equal(projectRoot, analysis.ProjectRoot);
            Assert.Equal(expectedHead, analysis.GitHead);
            Assert.True(analysis.GitIsDirty);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Enrich_StatusResult_LeavesGitMetadataNullOutsideGitRepo()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_workspace_plain");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var status = new StatusResult();

            WorkspaceMetadataEnricher.Enrich(status, dbPath);

            Assert.Equal(projectRoot, status.ProjectRoot);
            Assert.Null(status.GitHead);
            Assert.Null(status.GitIsDirty);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Enrich_StatusResult_DoesNotLeakCurrentWorktreeGitMetadataIntoExplicitDbWithoutStoredRoot()
    {
        var queryCwd = TestProjectHelper.CreateTempProject("cdidx_workspace_query_cwd");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_workspace_plain_{Guid.NewGuid():N}.db");
        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            TestProjectHelper.InitializeGitRepo(queryCwd);
            File.WriteAllText(Path.Combine(queryCwd, "tracked.txt"), "tracked\n");
            TestProjectHelper.RunGit(queryCwd, "add", "tracked.txt");
            TestProjectHelper.RunGit(queryCwd, "commit", "-m", "initial");
            File.WriteAllText(Path.Combine(queryCwd, "tracked.txt"), "dirty\n");

            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
            }

            Directory.SetCurrentDirectory(queryCwd);
            var status = new StatusResult();

            WorkspaceMetadataEnricher.Enrich(status, dbPath);

            Assert.Null(status.ProjectRoot);
            Assert.Null(status.GitHead);
            Assert.Null(status.GitIsDirty);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            TestProjectHelper.DeleteDirectory(queryCwd);
            TestProjectHelper.DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Enrich_StatusResult_UsesStoredProjectRootMetadataForExplicitDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_workspace_explicit_meta");
        var queryCwd = TestProjectHelper.CreateTempProject("cdidx_workspace_other_git");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_workspace_meta_{Guid.NewGuid():N}.db");
        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void Run() {} }\n");

            TestProjectHelper.InitializeGitRepo(queryCwd);
            File.WriteAllText(Path.Combine(queryCwd, "tracked.txt"), "tracked\n");
            TestProjectHelper.RunGit(queryCwd, "add", "tracked.txt");
            TestProjectHelper.RunGit(queryCwd, "commit", "-m", "initial");

            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }

            Directory.SetCurrentDirectory(queryCwd);
            var status = new StatusResult();

            WorkspaceMetadataEnricher.Enrich(status, dbPath, dbPathExplicit: true);

            Assert.Equal(projectRoot, status.ProjectRoot);
            Assert.Equal(expectedHead, status.GitHead);
            Assert.True(status.GitIsDirty);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(queryCwd);
            TestProjectHelper.DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Enrich_StatusResult_ExplicitExternalCodeIndexDbUsesStoredProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_workspace_explicit_codeindex_root");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_workspace_explicit_codeindex_container");
        var queryCwd = TestProjectHelper.CreateTempProject("cdidx_workspace_explicit_codeindex_other_git");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void Run() {} }\n");

            TestProjectHelper.InitializeGitRepo(queryCwd);
            File.WriteAllText(Path.Combine(queryCwd, "tracked.txt"), "tracked\n");
            TestProjectHelper.RunGit(queryCwd, "add", "tracked.txt");
            TestProjectHelper.RunGit(queryCwd, "commit", "-m", "initial");

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }

            Directory.SetCurrentDirectory(queryCwd);
            var status = new StatusResult();

            WorkspaceMetadataEnricher.Enrich(status, dbPath, dbPathExplicit: true);

            Assert.Equal(projectRoot, status.ProjectRoot);
            Assert.Equal(expectedHead, status.GitHead);
            Assert.True(status.GitIsDirty);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
            TestProjectHelper.DeleteDirectory(queryCwd);
        }
    }

    [Fact]
    public void Enrich_StatusResult_ExplicitProjectLocalDbLeavesWorkspaceMetadataNullWhenMetadataIsMissing()
    {
        var (projectRoot, dbPath, _) = CreateDirtyGitProject("cdidx_workspace_project_local_explicit");
        try
        {
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }

            var status = new StatusResult();

            WorkspaceMetadataEnricher.Enrich(status, dbPath, dbPathExplicit: true);

            Assert.Null(status.ProjectRoot);
            Assert.Null(status.GitHead);
            Assert.Null(status.GitIsDirty);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Enrich_StatusResult_PopulatesCommitsAheadWhenIndexedHeadShaIsAncestor()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_workspace_ahead_count");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var indexedSha = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();

            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void A() {} }\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "second");
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void B() {} }\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "third");

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var status = new StatusResult { IndexedHeadSha = indexedSha };

            WorkspaceMetadataEnricher.Enrich(status, dbPath);

            Assert.Equal(projectRoot, status.ProjectRoot);
            Assert.Equal(2, status.CommitsAheadOfIndexedHead);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Enrich_StatusResult_LeavesCommitsAheadNullWhenIndexedHeadShaIsMissing()
    {
        var (projectRoot, dbPath, _) = CreateDirtyGitProject("cdidx_workspace_ahead_no_sha");
        try
        {
            var status = new StatusResult();

            WorkspaceMetadataEnricher.Enrich(status, dbPath);

            Assert.Equal(projectRoot, status.ProjectRoot);
            Assert.Null(status.CommitsAheadOfIndexedHead);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static (string ProjectRoot, string DbPath, string HeadCommit) CreateDirtyGitProject(string prefix)
    {
        var projectRoot = TestProjectHelper.CreateTempProject(prefix);

        TestProjectHelper.InitializeGitRepo(projectRoot);
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));

        var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
        File.WriteAllText(sourcePath, "class App {}\n");
        TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
        TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();

        File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

        return (projectRoot, dbPath, expectedHead);
    }
}
