using System.Globalization;
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
            var unrelatedDirectory = TestProjectHelper.CreateTempProject("codeindex-changelog-main-test");
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
                TestProjectHelper.DeleteDirectory(unrelatedDirectory);
            }
        }
    }

    [Fact]
    public void ProgramMainPrepareBadVersionReturnsFriendlyError_Issue3444()
    {
        var (exitCode, stdout, stderr) = CaptureChangelogMain(
            ["prepare", "--version", "not-a-version", "--date", "2026-05-01"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--version must be a version like X.Y.Z.", stderr);
    }

    [Fact]
    public void ProgramMainPrepareBadDateReturnsFriendlyError_Issue3444()
    {
        var (exitCode, stdout, stderr) = CaptureChangelogMain(
            ["prepare", "--version", "1.17.0", "--date", "05/01/2026"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--date must use yyyy-MM-dd.", stderr);
    }

    [Fact]
    public void ProgramMainReleaseNotesBadPreviousVersionReturnsFriendlyError_Issue3444()
    {
        var (exitCode, stdout, stderr) = CaptureChangelogMain(
            ["release-notes", "--version", "1.17.0", "--previous-version", "bad"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--previous-version must be a version like X.Y.Z.", stderr);
    }

    [Fact]
    public void ProgramMainRenderUsesInvariantInputsUnderNonInvariantCulture_Issue3444()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CodeIndex.sln", string.Empty);
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var (exitCode, stdout, stderr) = CaptureChangelogMain(
                ["render", "--version", "1.17.0", "--date", "2026-05-01"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("### [1.17.0] - 2026-05-01", stdout);
            Assert.Contains("English release note", stdout);
            Assert.Contains("Japanese release note", stdout);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void ProgramMainPrepareWithoutFragmentsReturnsFriendlyError()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CodeIndex.sln", string.Empty);
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/.gitkeep", string.Empty);

        var (exitCode, stdout, stderr) = CaptureChangelogMain(
            ["prepare", "--version", "1.17.0", "--date", "2026-05-01"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("changelog.d/unreleased: no changelog fragments found.", stderr);
        Assert.DoesNotContain("### [1.17.0] - 2026-05-01", scope.ReadFile("CHANGELOG.md"));
        Assert.Contains("\"version\": \"1.16.0\"", scope.ReadFile("version.json"));
    }

    [Fact]
    public void CheckFragmentsAllowsEmptyFragmentSet()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/.gitkeep", string.Empty);

        var tool = new ChangelogTool(scope.Root);
        var summary = tool.CheckFragments();

        Assert.Equal("Validated 0 changelog fragment(s).", summary);
    }

    [Theory]
    [InlineData("lf")]
    [InlineData("crlf")]
    [InlineData("mixed")]
    public void PrepareMovesFragmentsIntoReleaseAndUpdatesFooter(string newlineStyle)
    {
        using var scope = new TestRepositoryScope();
        var input = SampleChangelog.Replace("\r\n", "\n", StringComparison.Ordinal);
        input = newlineStyle switch
        {
            "crlf" => input.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n",
            "mixed" => input + "\r\n",
            _ => input + "\n"
        };
        File.WriteAllText(Path.Combine(scope.Root, "CHANGELOG.md"), input);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);
        scope.WriteFile("changelog.d/unreleased/.gitkeep", string.Empty);

        var tool = new ChangelogTool(scope.Root);
        var preview = tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: false);
        var result = tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true);

        Assert.Contains("Prepared changelog for v1.17.0.", result.Summary);
        Assert.Contains("Previous version: v1.16.0.", result.Summary);
        Assert.Contains("Fragments consumed: 1.", result.Summary);

        var changelog = scope.ReadFile("CHANGELOG.md");
        Assert.Equal(preview.RenderedChangelog, changelog);
        Assert.DoesNotContain("\r", changelog);
        Assert.EndsWith("\n", changelog);
        Assert.Contains("Validated 0", tool.CheckFragments());
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
    public void PrepareConsumedFragmentDeleteFailureReportsSanitizedDiagnostic_Issue3457()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);
        var fragmentPath = Path.Combine(scope.Root, "changelog.d/unreleased/195.fixed.md");
        var fullFragmentPath = Path.GetFullPath(fragmentPath);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var tool = new ChangelogTool(scope.Root);
        ChangelogException? ex = null;
        ChangelogTool.DeleteFileForTesting = path =>
        {
            if (string.Equals(Path.GetFullPath(path), fullFragmentPath, pathComparison))
                throw new IOException($"raw filesystem detail {path}");
            File.Delete(path);
        };
        try
        {
            ex = Assert.Throws<ChangelogException>(() => tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true));
        }
        finally
        {
            ChangelogTool.DeleteFileForTesting = File.Delete;
        }

        Assert.NotNull(ex);
        Assert.Contains("fragment_delete_failed", ex.Message);
        Assert.Contains("affected_paths=changelog.d/unreleased/195.fixed.md", ex.Message);
        Assert.Contains("reason=io_error", ex.Message);
        Assert.Contains("Hint: Delete the listed fragment manually before retrying prepare.", ex.Message);
        Assert.DoesNotContain("raw filesystem detail", ex.Message);
        Assert.DoesNotContain(scope.Root, ex.Message);
    }

    [Fact]
    public void PrepareRollbackFailureReportsSanitizedDiagnostic_Issue3457()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", """
            {
              "version": "1.16.0"
            }
            """);
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);
        var versionPath = Path.Combine(scope.Root, "version.json");

        var tool = new ChangelogTool(scope.Root);
        ChangelogException? ex = null;
        ChangelogTool.PrepareWritePhaseForTesting = phase =>
        {
            if (phase == PrepareWritePhase.BeforeFragmentsDeleted)
                throw new ChangelogException("injected fragment deletion failure");
        };
        ChangelogTool.BeforeRestoreTextForTesting = path =>
        {
            if (string.Equals(path, versionPath, StringComparison.Ordinal))
                throw new IOException($"raw rollback detail {path}");
        };
        try
        {
            ex = Assert.Throws<ChangelogException>(() => tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true));
        }
        finally
        {
            ChangelogTool.PrepareWritePhaseForTesting = null;
            ChangelogTool.BeforeRestoreTextForTesting = null;
        }

        Assert.NotNull(ex);
        Assert.Contains("rollback_failed", ex.Message);
        Assert.Contains("affected_paths=version.json,CHANGELOG.md", ex.Message);
        Assert.Contains("reason=io_error", ex.Message);
        Assert.Contains("Hint: Restore the listed release files from version control or backup before retrying prepare.", ex.Message);
        Assert.DoesNotContain("raw rollback detail", ex.Message);
        Assert.DoesNotContain(scope.Root, ex.Message);
    }

    [Fact]
    public void RenderReleaseNotesUsesProvidedPreviousVersion()
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

        var notes = tool.RenderReleaseNotes(new Version(1, 17, 0), new Version(1, 16, 0));

        Assert.Equal("""
            ## What's Changed

            Full Changelog: https://github.com/Widthdom/CodeIndex/compare/v1.16.0...v1.17.0

            ## Install or update

            Homebrew:

            ```bash
            brew install widthdom/tap/codeindex
            brew upgrade widthdom/tap/codeindex
            ```

            NuGet:

            ```bash
            dotnet tool install -g cdidx
            dotnet tool update -g cdidx
            ```
            """.Replace("\r\n", "\n") + "\n", notes.Replace("\r\n", "\n"));
        Assert.DoesNotContain("English release note", notes);
        Assert.DoesNotContain("Japanese release note", notes);
        Assert.DoesNotContain("[Unreleased]:", notes);
    }

    [Fact]
    public void RenderReleaseNotesRejectsMissingTargetCompareFooter_Issue5294()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", """
            # Changelog

            All notable changes to this project will be documented in this file.

            ## English

            ### [1.17.0] - 2026-05-01

            #### Fixed
            - English release note.

            ## 日本語

            ### [1.17.0] - 2026-05-01

            #### 修正
            - Japanese release note.

            [Unreleased]: https://github.com/Widthdom/CodeIndex/compare/v1.17.0...HEAD
            [1.16.0]: https://github.com/Widthdom/CodeIndex/compare/v1.15.3...v1.16.0
            """);

        var tool = new ChangelogTool(scope.Root);
        var error = Assert.Throws<ChangelogException>(() => tool.RenderReleaseNotes(new Version(1, 17, 0), new Version(1, 16, 0)));
        Assert.Contains("missing compare link [1.17.0]", error.Message);
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
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);

        var tool = new ChangelogTool(scope.Root);
        var ex = Assert.Throws<ChangelogException>(() => tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), writeChanges: true));
        Assert.Contains("CHANGELOG.md: file is", ex.Message);
        Assert.Contains($"maximum supported size is {ChangelogTool.MaxChangelogBytes} bytes", ex.Message);
    }

    [Fact]
    public void ConfiguredChangelogLimitFitsRepositoryChangelog()
    {
        var changelogLength = new FileInfo(RepositoryTestPaths.Combine("CHANGELOG.md")).Length;

        Assert.True(
            changelogLength <= ChangelogTool.MaxChangelogBytes,
            $"CHANGELOG.md is {changelogLength} bytes, but MaxChangelogBytes is {ChangelogTool.MaxChangelogBytes}.");
        Assert.True(ChangelogTool.MaxChangelogBytes <= 3 * 1024 * 1024);
        new ChangelogTool(Path.GetDirectoryName(RepositoryTestPaths.Combine("CHANGELOG.md"))!).CheckFragments();
    }

    [Fact]
    public void ArchivedHistorySupportsCheckPrepareAndReleaseNotes_Issue5294()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", RootWithArchive);
        scope.WriteFile(ArchivePath, SampleArchive);
        scope.WriteFile("version.json", "{\"version\":\"1.16.0\"}");
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);
        var tool = new ChangelogTool(scope.Root);

        Assert.Contains("Validated 1", tool.CheckFragments());
        Assert.Contains("compare/v0.9.0...v1.0.0", tool.RenderReleaseNotes(new Version(1, 0, 0), new Version(0, 9, 0)));
        Assert.Contains("compare/v1.15.3...v1.16.0", tool.RenderReleaseNotes(new Version(1, 16, 0), new Version(1, 15, 3)));
        Assert.Contains("archived", Assert.Throws<ChangelogException>(() => tool.Prepare(new Version(1, 0, 0), new DateOnly(2026, 5, 1), true)).Message);
        var preview = tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), false);
        Assert.Equal(RootWithArchive, scope.ReadFile("CHANGELOG.md"));
        Assert.True(scope.Exists("changelog.d/unreleased/195.fixed.md"));
        tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), true);
        Assert.Equal(preview.RenderedChangelog, scope.ReadFile("CHANGELOG.md"));
        Assert.Equal(SampleArchive, scope.ReadFile(ArchivePath));
        Assert.Contains($"]({ArchivePath})", scope.ReadFile("CHANGELOG.md"));
        Assert.Contains("Validated 0", tool.CheckFragments());
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);
        tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), true);
        var prepared = scope.ReadFile("CHANGELOG.md");
        var japaneseStart = prepared.IndexOf("## 日本語", StringComparison.Ordinal);
        var archiveStart = prepared.IndexOf("### アーカイブ", StringComparison.Ordinal);
        Assert.True(japaneseStart < archiveStart);
        Assert.True(archiveStart < prepared.IndexOf("### [Unreleased]", japaneseStart, StringComparison.Ordinal));
        Assert.Equal(1, CountOccurrences(prepared, "### アーカイブ"));
        Assert.DoesNotContain("過去の履歴", prepared[..japaneseStart]);
        Assert.Equal(SampleArchive, scope.ReadFile(ArchivePath));
        tool.CheckFragments();
    }

    [Theory]
    [InlineData("missing-japanese", "English/日本語")]
    [InlineData("duplicate-japanese", "English/日本語")]
    [InlineData("mismatched-date", "English/日本語")]
    [InlineData("missing-compare", "missing compare")]
    [InlineData("duplicate-compare", "duplicate compare")]
    [InlineData("wrong-compare-target", "mismatched target")]
    [InlineData("wrong-compare-base", "older version")]
    [InlineData("wrong-unreleased-base", "newest root release")]
    [InlineData("duplicate-release", "duplicate release")]
    [InlineData("missing-backlink", "link back")]
    [InlineData("unlisted-archive", "archive index")]
    [InlineData("wrong-japanese-index", "archive index")]
    [InlineData("missing-archive", "archive index")]
    [InlineData("wrong-filename", "filename")]
    [InlineData("overlapping-range", "overlaps")]
    [InlineData("archive-unreleased", "Unreleased")]
    [InlineData("reversed-order", "descending order")]
    public void HistoryValidationRejectsBrokenArchivesBeforeMutation_Issue5294(string scenario, string expected)
    {
        using var scope = new TestRepositoryScope();
        var root = RootWithArchive;
        var archive = SampleArchive;
        var path = ArchivePath;
        switch (scenario)
        {
            case "missing-japanese": archive = archive.Replace("  ### [1.0.0] - 2026-04-08\n\n### 修正\n\n- 旧リリース。\n\n", string.Empty); break;
            case "duplicate-japanese": archive = archive.Replace("  ### [1.0.0] - 2026-04-08", "  ### [1.0.0] - 2026-04-08\n\n  ### [1.0.0] - 2026-04-08"); break;
            case "mismatched-date": archive = archive.Replace("  ### [1.0.0] - 2026-04-08", "  ### [1.0.0] - 2026-04-09"); break;
            case "missing-compare": archive = archive.Replace("[1.0.0]:", "[0.9.0]:").Replace("tag/v1.0.0", "tag/v0.9.0"); break;
            case "duplicate-compare": archive += "\n[1.0.0]: https://github.com/Widthdom/CodeIndex/releases/tag/v1.0.0\n"; break;
            case "wrong-compare-target": archive = archive.Replace("tag/v1.0.0", "tag/v0.9.0"); break;
            case "wrong-compare-base": archive = archive.Replace("releases/tag/v1.0.0", "compare/v1.0.0...v1.0.0"); break;
            case "wrong-unreleased-base": root = root.Replace("v1.16.0...HEAD", "v1.0.0...HEAD"); break;
            case "duplicate-release": archive = archive.Replace("1.0.0", "1.16.0"); break;
            case "missing-backlink": archive = archive.Replace("../../CHANGELOG.md", "missing.md"); break;
            case "unlisted-archive": root = root.Replace($"[History]({ArchivePath})", string.Empty); break;
            case "wrong-japanese-index": root = root.Replace($"{ArchivePath}#日本語", "docs/changelog/v0.9.0-v0.9.0.md#日本語"); break;
            case "missing-archive": break;
            case "wrong-filename": path = "docs/changelog/v0.9.0-v1.0.0.md"; root = root.Replace(ArchivePath, path); break;
            case "overlapping-range": archive = archive.Replace("1.0.0", "1.18.0"); path = "docs/changelog/v1.18.0-v1.18.0.md"; root = root.Replace(ArchivePath, path); break;
            case "archive-unreleased": archive = archive.Replace("### [1.0.0] - 2026-04-08", "### [Unreleased]").Replace("[1.0.0]: https://github.com/Widthdom/CodeIndex/releases/tag/v1.0.0", "[Unreleased]: https://github.com/Widthdom/CodeIndex/compare/v1.0.0...HEAD"); break;
            case "reversed-order": archive = archive.Replace("## 日本語", "### [1.1.0] - 2026-04-10\n\n## 日本語").Replace("[1.0.0]:", "### [1.1.0] - 2026-04-10\n\n[1.1.0]: https://github.com/Widthdom/CodeIndex/compare/v1.0.0...v1.1.0\n[1.0.0]:"); break;
        }
        scope.WriteFile("CHANGELOG.md", root);
        if (scenario != "missing-archive")
            scope.WriteFile(path, archive);
        scope.WriteFile("version.json", "{\"version\":\"1.16.0\"}");
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);
        var tool = new ChangelogTool(scope.Root);
        Assert.Contains(expected, Assert.Throws<ChangelogException>(() => tool.CheckFragments()).Message);
        Assert.Contains(expected, Assert.Throws<ChangelogException>(() => tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), true)).Message);
        Assert.Contains(expected, Assert.Throws<ChangelogException>(() => tool.RenderReleaseNotes(new Version(1, 16, 0), new Version(1, 15, 3))).Message);
        Assert.Equal(root, scope.ReadFile("CHANGELOG.md"));
        Assert.Equal("{\"version\":\"1.16.0\"}", scope.ReadFile("version.json"));
        Assert.Equal(SampleFragment, scope.ReadFile("changelog.d/unreleased/195.fixed.md"));
        if (scenario != "missing-archive")
            Assert.Equal(archive, scope.ReadFile(path));
    }

    [Fact]
    public void HistoryByteLimitsCoverArchivesAndPreparedOutput_Issue5294()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", RootWithArchive);
        scope.WriteFile(ArchivePath, SampleArchive);
        scope.WriteFile("version.json", "{\"version\":\"1.16.0\"}");
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);
        var tool = new ChangelogTool(scope.Root);
        var paddingBytes = checked((int)ChangelogTool.MaxChangelogBytes - System.Text.Encoding.UTF8.GetByteCount(SampleArchive));
        var padding = new string('界', paddingBytes / 3) + new string('x', paddingBytes % 3);
        var archive = SampleArchive.Replace("Legacy English.", "Legacy English." + padding);
        Assert.Equal(ChangelogTool.MaxChangelogBytes, System.Text.Encoding.UTF8.GetByteCount(archive));
        scope.WriteFile(ArchivePath, archive);
        tool.CheckFragments();
        scope.WriteFile(ArchivePath, archive + "x");
        Assert.Contains("maximum supported size", Assert.Throws<ChangelogException>(() => tool.CheckFragments()).Message);
        scope.WriteFile(ArchivePath, SampleArchive);
        var root = RootWithArchive.Replace("# Changelog", "# Changelog" + new string('x', checked((int)ChangelogTool.MaxChangelogBytes - System.Text.Encoding.UTF8.GetByteCount(RootWithArchive))));
        scope.WriteFile("CHANGELOG.md", root);
        tool.CheckFragments();
        foreach (var write in new[] { false, true })
            Assert.Contains("prepared output", Assert.Throws<ChangelogException>(() => tool.Prepare(new Version(1, 17, 0), new DateOnly(2026, 5, 1), write)).Message);
        Assert.Equal(root, scope.ReadFile("CHANGELOG.md"));
        Assert.Equal("{\"version\":\"1.16.0\"}", scope.ReadFile("version.json"));
        Assert.Equal(SampleFragment, scope.ReadFile("changelog.d/unreleased/195.fixed.md"));
    }

    private const string ArchivePath = "docs/changelog/v1.0.0-v1.0.0.md";
    private static string RootWithArchive => SampleChangelog
        .Replace("# Changelog", $"# Changelog\n\n[History]({ArchivePath})")
        .Replace("## 日本語", $"### [1.16.0] - 2026-04-30\n\n- Current English.\n\n## 日本語\n\n### アーカイブ\n\n[過去の履歴]({ArchivePath}#日本語)")
        .Replace("[Unreleased]:", "### [1.16.0] - 2026-04-30\n\n- 現行リリース。\n\n[Unreleased]:");
    private const string SampleArchive = """
        # Changelog archive

        [Current](../../CHANGELOG.md)

        ## English

        ### [1.0.0] - 2026-04-08

        ### Fixed

        - Legacy English.

        ## 日本語

        [最新の変更履歴](../../CHANGELOG.md#日本語)

          ### [1.0.0] - 2026-04-08

        ### 修正

        - 旧リリース。

        [1.0.0]: https://github.com/Widthdom/CodeIndex/releases/tag/v1.0.0
        """;

    [Fact]
    public void PrepareRejectsOversizedVersionBeforeParsing()
    {
        using var scope = new TestRepositoryScope();
        scope.WriteFile("CHANGELOG.md", SampleChangelog);
        scope.WriteFile("version.json", OversizedContent(ChangelogTool.MaxVersionJsonBytes));
        scope.WriteFile("changelog.d/unreleased/195.fixed.md", SampleFragment);

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

    private static (int ExitCode, string Stdout, string Stderr) CaptureChangelogMain(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            using var outWriter = new StringWriter();
            using var errorWriter = new StringWriter();
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
            try
            {
                var exitCode = CodeIndex.Changelog.Program.Main(args);
                return (exitCode, outWriter.ToString(), errorWriter.ToString());
            }
            finally
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
            }
        }
    }

    private static string OversizedContent(long maxBytes) => new('x', checked((int)maxBytes + 1));

    private sealed class TestRepositoryScope : IDisposable
    {
        private readonly string _previousDirectory;
        public string Root { get; }

        public TestRepositoryScope()
        {
            Root = TestProjectHelper.CreateTempProject("codeindex-changelog-tests");
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
            TestProjectHelper.DeleteDirectory(Root);
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
