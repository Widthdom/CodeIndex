using CodeIndex.Changelog;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class ChangelogToolTests
{
    [Fact]
    public void ProgramMainCheckUsesAssemblyFallbackFromUnrelatedDirectory()
    {
        lock (TestConsoleLock.Gate)
        {
            var unrelatedDirectory = Path.Combine(Path.GetTempPath(), "codeindex-changelog-main-test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unrelatedDirectory);
            var previousDirectory = Directory.GetCurrentDirectory();
            var previousOut = Console.Out;
            var previousError = Console.Error;
            using var outWriter = new StringWriter();
            using var errorWriter = new StringWriter();
            Directory.SetCurrentDirectory(unrelatedDirectory);
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
            try
            {
                var exitCode = CodeIndex.Changelog.Program.Main(["check"]);

                Assert.True(exitCode == 0, $"exitCode={exitCode}\nstdout: {outWriter}\nstderr: {errorWriter}");
            }
            finally
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                Directory.SetCurrentDirectory(previousDirectory);
                Directory.Delete(unrelatedDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void PrepareMovesFragmentsIntoReleaseAndUpdatesFooter()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);
        scope.WriteFile("changelog.d/unreleased/.gitkeep", string.Empty);

        var tool = new ChangelogTool(scope.Root);
        var result = tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true);

        Assert.Contains("Prepared changelog for v1.17.0.", result.Summary);
        Assert.Contains("Previous version: v1.16.0.", result.Summary);
        Assert.Contains("Fragments consumed: 1.", result.Summary);

        var changelog = scope.ReadFile("CHANGELOG.md");
        Assert.Equal(2, CountOccurrences(changelog, "### [1.17.0] - 2026-05-01"));
        Assert.Contains("English release note", changelog);
        Assert.Contains("Japanese release note", changelog);
        Assert.Contains("Existing English unreleased note", changelog);
        Assert.Contains("Existing Japanese unreleased note", changelog);
        Assert.Contains("### [Unreleased]\n\n- **Pending changelog fragments live under `changelog.d/unreleased/`**", changelog.Replace("\r\n", "\n"));
        Assert.Contains("### [Unreleased]\n\n- **未リリースの変更内容は `changelog.d/unreleased/` にまとまっています**", changelog.Replace("\r\n", "\n"));
        Assert.Equal(1, CountOccurrences(changelog, "Pending changelog fragments live under `changelog.d/unreleased/`"));
        Assert.Equal(1, CountOccurrences(changelog, "未リリースの変更内容は `changelog.d/unreleased/` にまとまっています"));
        Assert.Contains("[Unreleased]: https://github.com/Widthdom/CodeIndex/compare/v1.17.0...HEAD", changelog);
        Assert.Contains("[1.17.0]: https://github.com/Widthdom/CodeIndex/compare/v1.16.0...v1.17.0", changelog);
        Assert.Contains("[1.0.0]: https://github.com/Widthdom/CodeIndex/releases/tag/v1.0.0", changelog);
        Assert.Equal("""
            {
              "version": "1.17.0"
            }
            """.Replace("\r\n", "\n") + "\n", scope.ReadFile("version.json").Replace("\r\n", "\n"));
        Assert.False(scope.Exists("changelog.d/unreleased/195.fixed.md"));
    }

    [Fact]
    public void PrepareWritesThroughSymlinkedReleaseFiles()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("actual-changelog.md", SampleChangelog);
        scope.WriteFile("actual-version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);

        var changelogLinkPath = Path.Combine(scope.Root, "CHANGELOG.md");
        var versionLinkPath = Path.Combine(scope.Root, "version.json");
        try
        {
            File.CreateSymbolicLink(changelogLinkPath, "actual-changelog.md");
            File.CreateSymbolicLink(versionLinkPath, "actual-version.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var tool = new ChangelogTool(scope.Root);
        tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true);

        Assert.NotNull(new FileInfo(changelogLinkPath).LinkTarget);
        Assert.NotNull(new FileInfo(versionLinkPath).LinkTarget);
        Assert.Contains("English release note", scope.ReadFile("actual-changelog.md"));
        Assert.Contains("Japanese release note", scope.ReadFile("actual-changelog.md"));
        Assert.Equal(scope.ReadFile("actual-changelog.md"), scope.ReadFile("CHANGELOG.md"));
        Assert.Contains("\"version\": \"1.17.0\"", scope.ReadFile("actual-version.json"));
        Assert.Equal(scope.ReadFile("actual-version.json"), scope.ReadFile("version.json"));
        Assert.False(scope.Exists("changelog.d/unreleased/195.fixed.md"));
    }

    [Fact]
    public void PrepareRerunPreservesExistingReleaseAndAppendsNewFragments()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);
        scope.WriteFile("changelog.d/unreleased/.gitkeep", string.Empty);

        var tool = new ChangelogTool(scope.Root);
        tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true);

        scope.WriteFile("changelog.d/unreleased/+release-process.docs.md", """
            ---
            category: docs
            affected:
              - .codex/workflows/release-changelog.md
            ---

            ## English

            - **Release changelog workflow documented** — release preparation now has a dedicated workflow.

            ## 日本語

            - **Release changelog ワークフローを文書化** — release preparation 用の専用ワークフローを追加しました。
            """);

        scope.WriteFile("version.json", """
            {
              "version": "1.17.0"
            }
            """);

        tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true);

        var changelog = scope.ReadFile("CHANGELOG.md");
        Assert.Equal(2, CountOccurrences(changelog, "### [1.17.0] - 2026-05-01"));
        Assert.Contains("English release note", changelog);
        Assert.Contains("Japanese release note", changelog);
        Assert.Contains("release preparation now has a dedicated workflow", changelog);
        Assert.Contains("release preparation 用の専用ワークフロー", changelog);
        Assert.Contains("[Unreleased]: https://github.com/Widthdom/CodeIndex/compare/v1.17.0...HEAD", changelog);
        Assert.Equal(0, scope.ListFiles("changelog.d/unreleased").Count(path => Path.GetFileName(path) is "195.fixed.md" or "+release-process.docs.md"));
    }

    [Fact]
    public void PrepareFailureAfterStagingLeavesReleaseFilesAndFragmentsUntouched()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);

        var tool = new ChangelogTool(scope.Root);
        ChangelogException? ex = null;
        ChangelogTool.PrepareWritePhaseForTesting = phase =>
        {
            if (phase == PrepareWritePhase.StagedFilesWritten)
                throw new ChangelogException("injected staging failure");
        };
        try
        {
            ex = Assert.Throws<ChangelogException>(() => tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true));
        }
        finally
        {
            ChangelogTool.PrepareWritePhaseForTesting = null;
        }

        Assert.NotNull(ex);
        Assert.Contains("injected staging failure", ex.Message);
        Assert.DoesNotContain("English release note", scope.ReadFile("CHANGELOG.md"));
        Assert.Equal("""
            {
              "version": "1.16.0"
            }
            """.Replace("\r\n", "\n"), scope.ReadFile("version.json").Replace("\r\n", "\n"));
        Assert.True(scope.Exists("changelog.d/unreleased/195.fixed.md"));
        Assert.DoesNotContain(scope.ListFiles("."), path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void PrepareFailureDuringStagedWriteDeletesPartialTempFile()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);

        var tool = new ChangelogTool(scope.Root);
        ChangelogException? ex = null;
        ChangelogTool.PrepareWritePhaseForTesting = phase =>
        {
            if (phase == PrepareWritePhase.StagedTempCreated)
                throw new ChangelogException("injected staged write failure");
        };
        try
        {
            ex = Assert.Throws<ChangelogException>(() => tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true));
        }
        finally
        {
            ChangelogTool.PrepareWritePhaseForTesting = null;
        }

        Assert.NotNull(ex);
        Assert.Contains("injected staged write failure", ex.Message);
        Assert.DoesNotContain("English release note", scope.ReadFile("CHANGELOG.md"));
        Assert.Equal("""
            {
              "version": "1.16.0"
            }
            """.Replace("\r\n", "\n"), scope.ReadFile("version.json").Replace("\r\n", "\n"));
        Assert.True(scope.Exists("changelog.d/unreleased/195.fixed.md"));
        Assert.DoesNotContain(scope.ListFiles("."), path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void PrepareFailureBeforeFragmentDeletionRollsBackReleaseFiles()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);

        var tool = new ChangelogTool(scope.Root);
        ChangelogException? ex = null;
        ChangelogTool.PrepareWritePhaseForTesting = phase =>
        {
            if (phase == PrepareWritePhase.BeforeFragmentsDeleted)
                throw new ChangelogException("injected fragment deletion failure");
        };
        try
        {
            ex = Assert.Throws<ChangelogException>(() => tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true));
        }
        finally
        {
            ChangelogTool.PrepareWritePhaseForTesting = null;
        }

        Assert.NotNull(ex);
        Assert.Contains("injected fragment deletion failure", ex.Message);
        Assert.DoesNotContain("English release note", scope.ReadFile("CHANGELOG.md"));
        Assert.Equal("""
            {
              "version": "1.16.0"
            }
            """.Replace("\r\n", "\n"), scope.ReadFile("version.json").Replace("\r\n", "\n"));
        Assert.True(scope.Exists("changelog.d/unreleased/195.fixed.md"));
        Assert.DoesNotContain(scope.ListFiles("."), path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderReleaseNotesExtractsMatchingEnglishAndJapaneseSections()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);

        var tool = new ChangelogTool(scope.Root);
        tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true);

        var notes = tool.RenderReleaseNotes(new Version(1, 17, 0));

        Assert.StartsWith("## CodeIndex v1.17.0", notes, StringComparison.Ordinal);
        Assert.Contains("### English", notes);
        Assert.Contains("English release note", notes);
        Assert.Contains("Existing English unreleased note", notes);
        Assert.Contains("### 日本語", notes);
        Assert.Contains("Japanese release note", notes);
        Assert.Contains("Existing Japanese unreleased note", notes);
        Assert.DoesNotContain("[Unreleased]:", notes);
    }

    [Fact]
    public void CheckFragmentsRejectsCategoryMismatch()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/+bad.fixed.md", """
            ---
            category: changed
            issues:
              - 195
            ---

            ## English

            - **Bad fragment** — invalid category.

            ## 日本語

            - **Bad fragment** — invalid category.
            """);

        var tool = new ChangelogTool(scope.Root);
        var ex = Assert.Throws<ChangelogException>(() => tool.CheckFragments());
        Assert.Contains("file name category 'fixed' does not match front matter category 'changed'", ex.Message);
    }

    [Fact]
    public void CheckFragmentsRejectsNonIssueFragmentWithNullIssues()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/+bad.docs.md", """
            ---
            category: docs
            issues: null
            ---

            ## English

            - **Bad fragment** — invalid issues field.

            ## 日本語

            - **Bad fragment** — invalid issues field.
            """);

        var tool = new ChangelogTool(scope.Root);
        var ex = Assert.Throws<ChangelogException>(() => tool.CheckFragments());
        Assert.Contains("invalid issue number 'null'", ex.Message);
    }

    [Fact]
    public void CheckFragmentsRejectsMissingJapaneseSection()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/+missing-jp.fixed.md", """
            ---
            category: fixed
            ---

            ## English

            - **Bad fragment** — missing Japanese section.
            """);

        var tool = new ChangelogTool(scope.Root);
        var ex = Assert.Throws<ChangelogException>(() => tool.CheckFragments());
        Assert.Contains("missing '## 日本語' heading", ex.Message);
    }

    [Fact]
    public void CheckFragmentsRejectsTooManyFragmentsBeforeParsing()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);

        for (var i = 0; i <= ChangelogTool.MaxFragmentCount; i++)
            scope.WriteFile($"changelog.d/unreleased/{1000 + i}.fixed.md", string.Empty);

        var tool = new ChangelogTool(scope.Root);
        var ex = Assert.Throws<ChangelogException>(() => tool.CheckFragments());
        Assert.Contains("too many changelog fragments", ex.Message);
        Assert.Contains($"maximum supported count is {ChangelogTool.MaxFragmentCount}", ex.Message);
    }

    [Fact]
    public void CheckFragmentsRejectsOversizedFragmentBeforeParsing()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/+large.fixed.md", OversizedContent(ChangelogTool.MaxFragmentBytes));

        var tool = new ChangelogTool(scope.Root);
        var ex = Assert.Throws<ChangelogException>(() => tool.CheckFragments());
        Assert.Contains("changelog.d/unreleased/+large.fixed.md: file is", ex.Message);
        Assert.Contains($"maximum supported size is {ChangelogTool.MaxFragmentBytes} bytes", ex.Message);
    }

    [Fact]
    public void CheckFragmentsRejectsOversizedSymlinkTargetBeforeParsing()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("large-fragment-target.md", OversizedContent(ChangelogTool.MaxFragmentBytes));

        var linkPath = Path.Combine(scope.Root, "changelog.d", "unreleased", "+large-link.fixed.md");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            File.CreateSymbolicLink(linkPath, Path.Combine(scope.Root, "large-fragment-target.md"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var tool = new ChangelogTool(scope.Root);
        var thrown = Assert.Throws<ChangelogException>(() => tool.CheckFragments());
        Assert.Contains("changelog.d/unreleased/+large-link.fixed.md: file is larger than", thrown.Message);
        Assert.Contains($"maximum supported size is {ChangelogTool.MaxFragmentBytes} bytes", thrown.Message);
    }

    [Fact]
    public void PrepareRejectsOversizedChangelogBeforeParsing()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", OversizedContent(ChangelogTool.MaxChangelogBytes));
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);

        var tool = new ChangelogTool(scope.Root);
        var ex = Assert.Throws<ChangelogException>(() => tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true));
        Assert.Contains("CHANGELOG.md: file is", ex.Message);
        Assert.Contains($"maximum supported size is {ChangelogTool.MaxChangelogBytes} bytes", ex.Message);
    }

    [Fact]
    public void ConfiguredChangelogLimitFitsRepositoryChangelog()
    {
        var repositoryRoot = FindRepositoryRootForTest();
        var changelogLength = new FileInfo(Path.Combine(repositoryRoot, "CHANGELOG.md")).Length;

        Assert.True(
            changelogLength <= ChangelogTool.MaxChangelogBytes,
            $"CHANGELOG.md is {changelogLength} bytes, but MaxChangelogBytes is {ChangelogTool.MaxChangelogBytes}.");
    }

    [Fact]
    public void PrepareRejectsOversizedVersionBeforeParsing()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", OversizedContent(ChangelogTool.MaxVersionJsonBytes));

        var tool = new ChangelogTool(scope.Root);
        var ex = Assert.Throws<ChangelogException>(() => tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true));
        Assert.Contains("version.json: file is", ex.Message);
        Assert.Contains($"maximum supported size is {ChangelogTool.MaxVersionJsonBytes} bytes", ex.Message);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string OversizedContent(long maxBytes) => new('x', checked((int)maxBytes + 1));

    private static string FindRepositoryRootForTest()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CHANGELOG.md")) &&
                File.Exists(Path.Combine(current.FullName, "CodeIndex.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class TestRepositoryScope : IDisposable
    {
        private readonly string _previousDirectory;
        public string Root { get; }

        public TestRepositoryScope()
        {
            Root = Path.Combine(Path.GetTempPath(), "codeindex-changelog-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            _previousDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(Root);
        }

        public void WriteFile(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal));
        }

        public string ReadFile(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

        public bool Exists(string relativePath) => File.Exists(Path.Combine(Root, relativePath));

        public IReadOnlyList<string> ListFiles(string relativePath) => Directory.Exists(Path.Combine(Root, relativePath))
            ? Directory.GetFiles(Path.Combine(Root, relativePath), "*", SearchOption.AllDirectories)
            : [];

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_previousDirectory);
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private const string SampleChangelog = """
        # Changelog

        All notable changes to this project will be documented in this file.

        ## English

        ### [Unreleased]

        - **Pending changelog fragments live under `changelog.d/unreleased/`** — this section stays empty during ordinary work; see `changelog.d/unreleased/` for the release notes that are waiting to be aggregated.

        #### Fixed
        - Existing English unreleased note.

        ## 日本語

        ### [Unreleased]

        - **未リリースの変更内容は `changelog.d/unreleased/` にまとまっています** — 通常の作業ではこのセクションは空のままにし、リリース待ちの変更は `changelog.d/unreleased/` を参照してください。

        #### 修正
        - Existing Japanese unreleased note.

        [Unreleased]: https://github.com/Widthdom/CodeIndex/compare/v1.16.0...HEAD
        [1.16.0]: https://github.com/Widthdom/CodeIndex/compare/v1.15.3...v1.16.0
        [1.0.0]: https://github.com/Widthdom/CodeIndex/releases/tag/v1.0.0
        """;

    private const string SampleFragment = """
        ---
        category: fixed
        issues:
          - 195
        affected:
          - src/CodeIndex/Cli/QueryCommandRunner.cs
        ---

        ## English

        - **English release note (#195)** — fragment content.

        ## 日本語

        - **Japanese release note (#195)** — fragment content.
        """;
}
