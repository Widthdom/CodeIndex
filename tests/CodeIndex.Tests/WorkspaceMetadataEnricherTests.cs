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
    public void Enrich_StatusResult_FlagsWorktreeHeadChangedWhenPersistedHeadDiffersFromRuntimeHead()
    {
        var (projectRoot, dbPath, originalHead) = CreateDirtyGitProject("cdidx_workspace_head_changed");
        try
        {
            // Persist a fake "old" HEAD distinct from the runtime HEAD to simulate a per-worktree branch
            // switch after the index was built.
            // index 構築後のブランチ切替を模擬するため、現在の HEAD と異なる値を persisted HEAD として保存。
            var staleHead = new string('a', 40);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, staleHead);
            }

            var status = new StatusResult();

            WorkspaceMetadataEnricher.Enrich(status, dbPath);

            Assert.Equal(projectRoot, status.ProjectRoot);
            Assert.Equal(originalHead, status.GitHead);
            Assert.Equal(staleHead, status.IndexedHeadCommit);
            Assert.True(status.WorktreeHeadChanged);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Enrich_StatusResult_DoesNotFlagWorktreeHeadChangedWhenPersistedHeadMatchesRuntimeHead()
    {
        var (projectRoot, dbPath, originalHead) = CreateDirtyGitProject("cdidx_workspace_head_match");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, originalHead);
            }

            var status = new StatusResult();

            WorkspaceMetadataEnricher.Enrich(status, dbPath);

            Assert.Equal(originalHead, status.IndexedHeadCommit);
            Assert.False(status.WorktreeHeadChanged);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Enrich_StatusResult_LeavesWorktreeHeadChangedNullWhenPersistedHeadMissing()
    {
        // Legacy DBs (indexed before #1512) and non-git projects must not produce a false-positive
        // HEAD-switch warning when there is no persisted HEAD to compare against.
        // #1512 以前の DB や非 git プロジェクトでは persisted HEAD が無いため、誤検出を回避する。
        var (projectRoot, dbPath, _) = CreateDirtyGitProject("cdidx_workspace_head_missing");
        try
        {
            var status = new StatusResult();

            WorkspaceMetadataEnricher.Enrich(status, dbPath);

            Assert.Null(status.IndexedHeadCommit);
            Assert.Null(status.WorktreeHeadChanged);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
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
