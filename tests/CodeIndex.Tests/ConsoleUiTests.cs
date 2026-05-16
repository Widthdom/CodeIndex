using System.Text.RegularExpressions;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for console usage output.
/// コンソールの使い方出力のテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class ConsoleUiTests
{
    [Fact]
    public void PrintUsage_WithBanner_IncludesAsciiArt()
    {
        var output = CaptureUsageOutput();

        Assert.Contains("██████╗", output);
        Assert.Contains("Usage:", output);
        Assert.Contains("cdidx index <projectPath> --commits <id> [id ...]", output);
    }

    [Fact]
    public void PrintUsage_WithoutBanner_HidesAsciiArtAndEasterEggFlags()
    {
        var output = CaptureUsageOutput(showBanner: false);

        Assert.DoesNotContain("██████╗", output);
        Assert.Contains("Usage:", output);
        Assert.Contains("cdidx index <projectPath> [--db <path>] [--rebuild] [--verbose] [--dry-run] [--force] [--quiet] [--json] [--duration-format <auto|seconds|hms>]", output);
        Assert.Contains("cdidx hooks <install|uninstall|status> [--project <path>] [--force] [--json]", output);
        Assert.Contains("cdidx index <projectPath> --commits <id> [id ...] [--db <path>] [--verbose] [--dry-run] [--json] [--duration-format <auto|seconds|hms>]", output);
        Assert.Contains("cdidx index <projectPath> --files <path> [path ...] [--db <path>] [--verbose] [--dry-run] [--json] [--duration-format <auto|seconds|hms>]", output);
        Assert.Contains("cdidx backfill-fold [--db <path>] [--json]", output);
        Assert.Contains("cdidx license", output);
        Assert.Contains("cdidx references <query>|--query <query>|-- <query>", output);
        Assert.Contains("cdidx callers <query>|--query <query>|-- <query>", output);
        Assert.Contains("cdidx callees <query>|--query <query>|-- <query>", output);
        Assert.Contains("cdidx search <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--snippet-lines <n>] [--snippet-focus <leftmost|quality|proximity>] [--max-line-width <n>] [--fts] [--exact|--exact-substring] [--prefix] [--count] [--since <datetime>] [--no-dedup]", output);
        Assert.Contains("cdidx definition <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--exact|--exact-name] [--count] [--since <datetime>]", output);
        Assert.Contains("cdidx references <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--max-line-width <n>] [--exact|--exact-name] [--count]", output);
        Assert.Contains("cdidx inspect <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--max-line-width <n>] [--exact|--exact-name]", output);
        Assert.Contains("--snippet-lines <n>        Search snippet length (1-20, default: 8)", output);
        Assert.Contains("--snippet-focus <mode>     search only: long-line focus mode (leftmost|quality|proximity, default: quality)", output);
        Assert.Contains("--max-line-width <n>       search/references/find/excerpt/inspect only: clamp very long single-line snippet/context/excerpt payloads (`0` disables clamping; default: 512)", output);
        Assert.Contains("cdidx find <query> --path <glob>", output);
        Assert.Contains("--exact-substring          Search only: case-sensitive exact substring (no FTS5)", output);
        Assert.Contains("--exact-name               symbols/definition/references/callers/callees/inspect: NFKC + Unicode CaseFold exact name match", output);
        Assert.Contains("--kind <kind>              definition/symbols/hotspots/unused: symbol kind; references: reference kind (call/instantiate/subscribe/attribute/annotation); callers/callees: call-graph kinds only (call/instantiate/subscribe — metadata kinds rejected, use references instead); validate: issue kind", output);
        Assert.Contains("--count                    Count only; search/definition/references/callers/callees/symbols/files/find/unused ignore --limit, impact/hotspots still use visible page counts", output);
        Assert.Contains("--commits <id> [id ...]    Update only files changed in the specified git commits (preferred after commits)", output);
        Assert.Contains("--files <path> [path ...]  Update only the specified files; old rename/delete paths are not purged unless also listed", output);
        Assert.Contains("--duration-format <format> Index elapsed time format: `auto` (default), `seconds`, or `hms`; JSON keeps raw elapsed_ms", output);
        Assert.Contains("cdidx excerpt <path> --start <line> [--end <line>] [--before <n>] [--after <n>] [--max-line-width <n>] [--focus-line <line>] [--focus-column <n>] [--focus-length <n>] [--db <path>] [--json]", output);
        Assert.Contains("--focus-column <n>         excerpt: column to keep centered when clamping (must be within the focused line)", output);
        Assert.Contains("--focus-line <line>        excerpt: line whose focused column should stay visible", output);
        Assert.Contains("cdidx map [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--bytes]", output);
        Assert.Contains("cdidx symbols [query|--query <query>|-- <query>] [--name <name>] [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--count] [--since <datetime>]", output);
        Assert.Contains("cdidx files [query|--query <query>|-- <query>] [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--since <datetime>] [--bytes]", output);
        Assert.Contains("cdidx validate [--db <path>] [--json] [--kind <kind>] [--path <glob>]", output);
        Assert.Contains("Note: if a query itself starts with '-', pass it with --query <query> or -- <query>", output);
        Assert.DoesNotContain("cdidx validate [--db <path>] [--json] [--limit <n>] [--lang <lang>]", output);
        Assert.Contains("cdidx unused [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count]", output);
        Assert.Contains("cdidx hotspots [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--group-by <symbol|file|statement>] [--group-by-name]", output);
        Assert.Contains("--json                     Output as JSON (streaming hits use JSON lines; counts/summaries use one object)", output);
        Assert.Contains("--lang <lang>              Filter by language (aliases: bat, cmd, cshtml, razor, ts, tsx, cts, mts)", output);
        Assert.Contains("--bytes                    Show raw byte counts in human output for files/map instead of binary units; JSON always keeps raw integer bytes", output);
        Assert.Contains("--group-by-name            hotspots: collapse rows sharing (name, kind) across files into one line", output);
        Assert.Contains("cdidx search \"Run();\" --exact-substring        Case-sensitive exact substring search", output);
        Assert.Contains("cdidx search --query --path --path README.md   Search for a literal option token", output);
        Assert.Contains("cdidx hotspots --group-by-name --exclude-tests", output);
        Assert.Contains("Collapse same-name hotspots across files", output);
        Assert.Contains("cdidx symbols Run --exact-name                Exact symbol-name match", output);
        Assert.Contains("backfill-fold", output);
        Assert.Contains("find <query>               Find literal substring matches inside known indexed files", output);
        Assert.Contains("Prefer --exact-substring for search, keep --exact for find", output);
        Assert.Contains("impact <query>             Show transitive callers; type queries may return heuristic file-level dependency hints", output);
        Assert.Contains("hotspots                   Find high-impact symbols; duplicate-name families may fall back conservatively", output);
        Assert.Contains("cdidx find guard --path src/Auth.cs --after 2", output);
        Assert.Contains("cdidx references DbContext --kind instantiate Filter constructor sites by reference kind", output);
        Assert.Contains("cdidx hotspots --lang csharp --exclude-tests    Find high-impact symbols with conservative duplicate fallback", output);
        Assert.Contains("cdidx impact FolderDiffService --json           Type query may return heuristic file-level dependency hints", output);
        Assert.Contains("license                    Show licensing, trademark, and commercial-use summary", output);
        Assert.Contains("--license                  Show licensing, trademark, and commercial-use summary", output);
        Assert.Contains("cdidx license                                  Show licensing and commercial-use terms", output);
        Assert.DoesNotContain("Easter eggs", output);
        Assert.DoesNotContain("--sushi", output);
        Assert.DoesNotContain("--random-spinner", output);
    }

    [Fact]
    public void PrintUsage_ShowsCommitUpdateWorkflowClearly()
    {
        var output = CaptureUsageOutput(showBanner: false);

        Assert.Contains("Update workflows:", output);
        Assert.Contains("Use --commits with a project path after normal commits", output);
        Assert.Contains("Use --changed-between <old-ref> <new-ref> after switching branches", output);
        Assert.Contains("Use --files only for known in-place edits or new files", output);
        Assert.Contains("cdidx index ./myproject --commits abc123", output);
        Assert.Contains("cdidx index ./myproject --changed-between main feature", output);
    }

    [Fact]
    public void PrintUsage_QueryLinesMatchImplementedOptions()
    {
        var output = CaptureUsageOutput(showBanner: false);

        Assert.Contains("cdidx search <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--snippet-lines <n>] [--snippet-focus <leftmost|quality|proximity>] [--max-line-width <n>] [--fts] [--exact|--exact-substring] [--prefix] [--count] [--since <datetime>] [--no-dedup]", output);
        Assert.Contains("cdidx symbols [query|--query <query>|-- <query>] [--name <name>] [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--count] [--since <datetime>]", output);
        Assert.Contains("cdidx files [query|--query <query>|-- <query>] [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--since <datetime>] [--bytes]", output);
        Assert.Contains("cdidx hotspots [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count]", output);
        Assert.Contains("cdidx unused [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count]", output);
        Assert.Contains("cdidx license", output);
    }

    [Theory]
    [InlineData(0, "0 results", "No results found.")]
    [InlineData(1, "1 result", "Found 1 result.")]
    [InlineData(5, "5 results", "Found 5 results.")]
    public void CountedAndFoundSummary_UseNaturalPluralization(int count, string counted, string summary)
    {
        Assert.Equal(counted, ConsoleUi.Counted(count, "result"));
        Assert.Equal(summary, ConsoleUi.FoundSummary(count, "result"));
    }

    [Fact]
    public void Counted_AllowsIrregularPluralAndNumberFormat()
    {
        Assert.Equal("2 in-file matches", ConsoleUi.Counted(2, "in-file match", "in-file matches"));
        Assert.Equal("1,234 files", ConsoleUi.Counted(1234, "file", format: "N0"));
    }

    [Fact]
    public void GetSpinnerFrames_Default_UsesRotatingBrailleSequence()
    {
        var frames = ConsoleUi.GetSpinnerFrames(null);

        Assert.Equal(["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"], frames);
    }

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(1023L, "1,023 bytes")]
    [InlineData(1024L, "1.0 KiB")]
    [InlineData(1048575L, "1,024.0 KiB")]
    [InlineData(1048576L, "1.0 MiB")]
    [InlineData(5368709120L, "5.0 GiB")]
    [InlineData(1099511627776L, "1.0 TiB")]
    public void FormatBytes_UsesBinaryUnitsForHumanOutput(long bytes, string expected)
    {
        Assert.Equal(expected, ConsoleUi.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(0, "0ms")]
    [InlineData(999, "999ms")]
    [InlineData(1_000, "1.0s")]
    [InlineData(59_900, "59.9s")]
    [InlineData(60_000, "1m 0s")]
    [InlineData(3_599_000, "59m 59s")]
    [InlineData(3_600_000, "1h 0m 0s")]
    [InlineData(86_400_000, "24h 0m 0s")]
    [InlineData(360_061_000, "100h 1m 1s")]
    public void FormatDuration_Auto_UsesUnitLabels(int milliseconds, string expected)
    {
        Assert.Equal(expected, ConsoleUi.FormatDuration(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void FormatDuration_ExplicitFormats_RespectUserPreference()
    {
        var duration = TimeSpan.FromMilliseconds(65_432);

        Assert.Equal("65.4s", ConsoleUi.FormatDuration(duration, DurationOutputFormat.Seconds));
        Assert.Equal("00:01:05", ConsoleUi.FormatDuration(duration, DurationOutputFormat.Hms));
    }

    [Fact]
    public void PrintProgress_InitialRender_ShowsZeroPercent()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);

                ConsoleUi.PrintProgress(0, 10);

                var output = writer.ToString();

                Assert.Contains("0.0%", output);
                Assert.Contains("[0/10]", output);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    [Fact]
    public void PrintLicenseSummary_DescribesFslAndCommercialRestriction()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                ConsoleUi.PrintLicenseSummary();
                var output = writer.ToString();

                Assert.Contains("Functional Source License, Version 1.1, ALv2 Future License (FSL-1.1-ALv2)", output);
                Assert.Contains("non-competing purposes", output);
                Assert.Contains("Competing commercial products or services require a separate written agreement", output);
                Assert.Contains("separate written agreement", output);
                Assert.Contains("LICENSES/Apache-2.0.txt", output);
                Assert.Contains("INTEGRATION_POLICY.md", output);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    [Fact]
    public void LoadVersion_ReturnsActualVersion_NotFallback()
    {
        // In the test environment the built version.json should be present.
        // Verify the returned value is a real version, not the "0.0.0" fallback.
        // テスト環境ではビルド済みの version.json が存在するはず。
        // フォールバックの "0.0.0" ではなく実際のバージョンが返ることを検証。
        var version = ConsoleUi.LoadVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual("0.0.0", version);
        Assert.Contains('.', version);
    }

    [Fact]
    public void LoadBuildMetadata_PopulatesAllFields()
    {
        // #1550: build metadata (commit SHA, build date, dirty flag) is stamped
        // into the assembly so `cdidx --version` can distinguish dev builds
        // from tagged releases. All four fields must be non-empty — when the
        // MSBuild target cannot resolve a value it falls back to "unknown".
        // #1550 で導入したアセンブリメタデータ。MSBuild ターゲットが値を解決
        // できない場合は "unknown" フォールバックが入るため、いずれのフィールド
        // も空にはならない。
        var metadata = ConsoleUi.LoadBuildMetadata();
        Assert.False(string.IsNullOrWhiteSpace(metadata.Version));
        Assert.False(string.IsNullOrWhiteSpace(metadata.Commit));
        Assert.False(string.IsNullOrWhiteSpace(metadata.BuildDate));
        Assert.False(string.IsNullOrWhiteSpace(metadata.Dirty));
        Assert.Equal(ConsoleUi.LoadVersion(), metadata.Version);
        Assert.Contains(metadata.Dirty, new[] { "clean", "dirty", "unknown" });
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    public void PrintCompletions_KnownShell_ReturnsTrue(string shell)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                Assert.True(ConsoleUi.PrintCompletions(shell));
                var output = writer.ToString();
                var exactSubstringToken = shell == "fish" ? "exact-substring" : "--exact-substring";
                var exactNameToken = shell == "fish" ? "exact-name" : "--exact-name";
                var groupByNameToken = shell == "fish" ? "group-by-name" : "--group-by-name";
                var licenseToken = shell == "fish" ? "-l license" : shell == "bash" ? "--license" : "license:license command";
                // #1570: the schema-driven generators emit one canonical description per flag
                // across every branch — the pre-refactor per-branch wording ("snippets" /
                // "contexts" / "excerpts") collapses to a single "payloads" tooltip.
                // #1570 後はスキーマ駆動なので、ブランチごとに違う旧文言ではなく統一の "payloads" 表記。
                var maxLineWidthToken = shell == "fish"
                    ? "Clamp long single-line payloads (0 disables clamping)"
                    : shell == "bash"
                        ? "--max-line-width"
                        : "--max-line-width[Clamp long single-line payloads (0 disables clamping)]:number";
                Assert.Contains(exactSubstringToken, output);
                Assert.Contains(exactNameToken, output);
                Assert.Contains(groupByNameToken, output);
                Assert.Contains(licenseToken, output);
                Assert.Contains(maxLineWidthToken, output);
                Assert.Contains("cshtml", output);
                Assert.Contains("razor", output);
                if (shell is "bash" or "zsh")
                {
                    // Should contain dynamically generated languages, including newly added ones
                    // 動的生成の言語リストに新しく追加した言語が含まれているか検証
                    Assert.Contains("msbuild", output);
                    Assert.Contains("elixir", output);
                    Assert.Contains("graphql", output);
                    Assert.Contains("protobuf", output);
                }
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    [Theory]
    [InlineData("bash", "if [ \"$cmd\" = \"hotspots\" ]", "--group-by-name", "--exact-name")]
    [InlineData("zsh", "elif [[ $subcmd == hotspots ]]; then", "--group-by-name[Hotspots: collapse same-name rows across files]", "--exact-name[Exact symbol-name equality]")]
    public void PrintCompletions_BashAndZshScopeGroupByNameToHotspots(string shell, string hotspotsBranchMarker, string groupedFlagToken, string genericExactNameToken)
    {
        var output = ConsoleUi.GetCompletionScript(shell);
        Assert.Contains(hotspotsBranchMarker, output);
        Assert.Contains(groupedFlagToken, output);

        var hotspotsIndex = output.IndexOf(hotspotsBranchMarker, StringComparison.Ordinal);
        var genericIndex = output.LastIndexOf(genericExactNameToken, StringComparison.Ordinal);
        Assert.True(hotspotsIndex >= 0);
        Assert.True(genericIndex > hotspotsIndex);
        Assert.DoesNotContain("--group-by-name --exact-name", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintCompletions_FishIncludesFindOptions()
    {
        // #1570: fish completion is now emitted from `CliFlagSchema`. The hand-written
        // `__fish_seen_subcommand_from <list>` strings change to the canonical
        // command-ordering used by `CliFlagSchema.AllCommands`, and descriptions use the
        // schema's single source of truth (e.g. `--exact` → "Backward-compatible exact
        // shorthand"). These assertions intentionally check the schema-ordered groupings
        // (`--query` and `--before`/`--after` predicates) and key flag invariants while
        // accepting the unified wording.
        // #1570 によりスキーマ駆動。`__fish_seen_subcommand_from` の並びは `CliFlagSchema.AllCommands`
        // 順、`--exact` の説明は統一表記 (`Backward-compatible exact shorthand`)。
        var output = ConsoleUi.GetCompletionScript("fish");
        Assert.Contains("__fish_seen_subcommand_from search definition references callers callees symbols files find inspect impact", output);
        Assert.Contains("__fish_seen_subcommand_from find excerpt", output);
        // `--exact` schema membership: search + find + the name-resolution commands.
        // 旧手書きが `search find` だけだった所を、スキーマ準拠の正規列で確認する。
        Assert.Contains("__fish_seen_subcommand_from search definition references callers callees symbols find inspect' -l exact ", output);
        Assert.Contains("-l query -r -d 'Literal query'", output);
        Assert.Contains("-l before -r -d 'Context lines before'", output);
        Assert.Contains("-l after -r -d 'Context lines after'", output);
        Assert.Contains("-l exact -d 'Backward-compatible exact shorthand'", output);
        Assert.Contains("__fish_seen_subcommand_from hotspots", output);
        Assert.Contains("-l group-by-name -d 'Collapse same-name rows across files'", output);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    public void PrintCompletions_BashAndZshKeepFocusOptionsExcerptOnly(string shell)
    {
        var output = ConsoleUi.GetCompletionScript(shell);
        Assert.Contains("find", output);
        Assert.Contains("--before", output);
        Assert.Contains("--after", output);
        Assert.Contains("--max-line-width", output);
        Assert.Contains("--exact", output);
        Assert.Contains("--query", output);
        if (shell == "bash")
        {
            Assert.Contains("if [ \"$cmd\" = \"find\" ]", output);
            Assert.Contains("elif [ \"$cmd\" = \"excerpt\" ]; then", output);
            var findBranch = ExtractBetween(output, "if [ \"$cmd\" = \"find\" ]", "elif [ \"$cmd\" = \"excerpt\" ]; then");
            var excerptBranch = ExtractBetween(output, "elif [ \"$cmd\" = \"excerpt\" ]; then", "elif [ \"$cmd\" = \"references\" ]; then");
            Assert.DoesNotContain("--focus-column", findBranch);
            Assert.Contains("--focus-column", excerptBranch);
        }
        else
        {
            Assert.Contains("if [[ $subcmd == find ]]; then", output);
            Assert.Contains("elif [[ $subcmd == excerpt ]]; then", output);
            var findBranch = ExtractBetween(output, "if [[ $subcmd == find ]]; then", "elif [[ $subcmd == excerpt ]]; then");
            var excerptBranch = ExtractBetween(output, "elif [[ $subcmd == excerpt ]]; then", "elif [[ $subcmd == references ]]; then");
            Assert.DoesNotContain("focus-column", findBranch);
            Assert.Contains("focus-column", excerptBranch);
        }
    }

    [Fact]
    public void PrintCompletions_ExcerptFlagSetsMatchAcrossShells()
    {
        var bashExcerpt = ExtractBashSubcommandFlags(ConsoleUi.GetCompletionScript("bash"), "excerpt", "references");
        var zshExcerpt = ExtractZshSubcommandFlags(ConsoleUi.GetCompletionScript("zsh"), "excerpt", "references");
        var fishExcerpt = ExtractFishSubcommandFlags(ConsoleUi.GetCompletionScript("fish"), "excerpt");

        // --help is universal in bash but is not enumerated by the zsh/fish scripts;
        // exclude it from the parity comparison so the flag sets line up cleanly.
        bashExcerpt.Remove("help");

        Assert.Equal(bashExcerpt, zshExcerpt);
        Assert.Equal(bashExcerpt, fishExcerpt);
        // Sanity check: required excerpt flags are present in every shell.
        foreach (var flag in new[] { "db", "json", "start", "end", "before", "after", "max-line-width", "focus-line", "focus-column", "focus-length" })
        {
            Assert.Contains(flag, bashExcerpt);
            Assert.Contains(flag, zshExcerpt);
            Assert.Contains(flag, fishExcerpt);
        }
    }

    [Fact]
    public void PrintCompletions_ReportFlagSetsMatchAcrossShells()
    {
        var bashScript = ConsoleUi.GetCompletionScript("bash");
        var zshScript = ConsoleUi.GetCompletionScript("zsh");
        var fishScript = ConsoleUi.GetCompletionScript("fish");
        var bashReport = ExtractBashSubcommandFlags(bashScript, "report", "search");
        var zshReport = ExtractZshSubcommandFlags(zshScript, "report", "search");
        var fishReport = ExtractFishSubcommandFlags(fishScript, "report");

        // --help is universal in bash but is not enumerated by the zsh/fish scripts.
        bashReport.Remove("help");

        var expected = new SortedSet<string>(StringComparer.Ordinal)
        {
            "db", "json", "output", "log-lines", "no-log", "include-args",
        };
        Assert.Equal(expected, bashReport);
        Assert.Equal(expected, zshReport);
        Assert.Equal(expected, fishReport);

        Assert.Contains("-o", ExtractBetween(bashScript, "[ \"$cmd\" = \"report\" ]; then", "[ \"$cmd\" = \"search\" ]; then"));
        Assert.Contains("'-o[Output bundle path]:file:_files'", ExtractBetween(zshScript, "[[ $subcmd == report ]]; then", "[[ $subcmd == search ]]; then"));
        Assert.Contains("-l output -s o -r", fishScript);
    }

    private static SortedSet<string> ExtractBashSubcommandFlags(string script, string subcommand, string nextSubcommand)
    {
        var startMarker = $"[ \"$cmd\" = \"{subcommand}\" ]; then";
        var endMarker = $"[ \"$cmd\" = \"{nextSubcommand}\" ]; then";
        var branch = ExtractBetween(script, startMarker, endMarker);
        var quoted = Regex.Match(branch, "compgen -W \"(?<flags>[^\"]*)\"");
        Assert.True(quoted.Success, $"bash branch for {subcommand} did not contain a compgen list");
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var token in quoted.Groups["flags"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
                flags.Add(token[2..]);
        }
        return flags;
    }

    private static SortedSet<string> ExtractZshSubcommandFlags(string script, string subcommand, string nextSubcommand)
    {
        var startMarker = $"[[ $subcmd == {subcommand} ]]; then";
        var endMarker = $"[[ $subcmd == {nextSubcommand} ]]; then";
        var branch = ExtractBetween(script, startMarker, endMarker);
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(branch, @"'--(?<name>[a-z][a-z0-9-]*)\["))
            flags.Add(match.Groups["name"].Value);
        return flags;
    }

    private static SortedSet<string> ExtractFishSubcommandFlags(string script, string subcommand)
    {
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        var pattern = new Regex($@"__fish_seen_subcommand_from\s+(?<list>[^']+)'\s+-l\s+(?<flag>[a-z][a-z0-9-]*)\b");
        foreach (var line in script.Split('\n'))
        {
            var match = pattern.Match(line);
            if (!match.Success)
                continue;
            var subcmds = match.Groups["list"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (Array.IndexOf(subcmds, subcommand) < 0)
                continue;
            flags.Add(match.Groups["flag"].Value);
        }
        return flags;
    }

    [Fact]
    public void GetCompletionLangs_IncludesWindowsBatchAndYamlAliases()
    {
        var method = typeof(ConsoleUi).GetMethod("GetCompletionLangs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        var value = method!.Invoke(null, []);

        var langs = value?.ToString() ?? string.Empty;
        var tokens = langs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("bat", tokens);
        Assert.Contains("batch", tokens);
        Assert.Contains("cmd", tokens);
        Assert.Contains("yml", tokens);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    public void PrintCompletions_BashAndZshScopeMaxLineWidthToSearchBranch(string shell)
    {
        var output = ConsoleUi.GetCompletionScript(shell).Replace("\r\n", "\n");
        if (shell == "bash")
        {
            Assert.Contains("elif [ \"$cmd\" = \"search\" ]; then", output);
            var searchBranch = ExtractBetween(output, "elif [ \"$cmd\" = \"search\" ]; then", "else\n");
            var genericBranch = ExtractBetween(output, "else\n                COMPREPLY=($(compgen -W \"", "\" -- \"$cur\"))\n            fi");
            Assert.Contains("--max-line-width", searchBranch);
            Assert.Contains("--top", searchBranch);
            Assert.Contains("--no-dedup", searchBranch);
            Assert.DoesNotContain("--exact-name", searchBranch);
            Assert.DoesNotContain("--max-line-width", genericBranch);
        }
        else
        {
            Assert.Contains("elif [[ $subcmd == search ]]; then", output);
            var searchBranch = ExtractBetween(output, "elif [[ $subcmd == search ]]; then", "else\n");
            var genericBranch = ExtractBetween(output, "else\n                _arguments", "fi\n            ;;");
            Assert.Contains("--max-line-width", searchBranch);
            Assert.Contains("'--top", searchBranch);
            Assert.Contains("'--no-dedup", searchBranch);
            Assert.DoesNotContain("--exact-name", searchBranch);
            Assert.DoesNotContain("--max-line-width", genericBranch);
        }
    }

    [Fact]
    public void PrintUsage_ShowsWorkingFindDashedLiteralExample()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                ConsoleUi.PrintUsage();
                var output = writer.ToString();
                Assert.Contains("cdidx find --path README.md -- --path", output);
                Assert.DoesNotContain("cdidx find -- --path --path README.md", output);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    [Fact]
    public void PrintCompletions_UnknownShell_ReturnsFalse()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalErr = Console.Error;
            using var writer = new StringWriter();
            try
            {
                Console.SetError(writer);
                Assert.False(ConsoleUi.PrintCompletions("powershell"));
                Assert.Contains("Unknown shell", writer.ToString());
            }
            finally
            {
                Console.SetError(originalErr);
            }
        }
    }

    [Fact]
    public void ColorizeKind_NoColorEnvVar_DisablesAnsiEscapes()
    {
        WithColorEnvironment(noColor: "1", cliColor: null, cliColorForce: null, () =>
        {
            Assert.False(ConsoleUi.ShouldUseColor());
            var output = ConsoleUi.ColorizeKind("function");
            Assert.Equal("function", output);
            Assert.DoesNotContain('\x1b', output);
        });
    }

    [Fact]
    public void ColorizeKind_NoColorEmpty_LeavesDefaultBehavior()
    {
        // Per https://no-color.org, only a non-empty NO_COLOR disables color.
        // 規約に従い、空の NO_COLOR は色を抑制せず、TTY 判定にフォールバックする。
        WithColorEnvironment(noColor: string.Empty, cliColor: null, cliColorForce: null, () =>
        {
            Assert.Equal(ConsoleUi.ShouldUseInteractiveConsole(), ConsoleUi.ShouldUseColor());
        });
    }

    [Fact]
    public void ColorizeKind_CliColorZero_DisablesAnsiEscapes()
    {
        WithColorEnvironment(noColor: null, cliColor: "0", cliColorForce: null, () =>
        {
            Assert.False(ConsoleUi.ShouldUseColor());
            var output = ConsoleUi.ColorizeKind("class", padWidth: 6);
            Assert.Equal("class ", output);
            Assert.DoesNotContain('\x1b', output);
        });
    }

    [Fact]
    public void ColorizeKind_CliColorOne_DoesNotOverrideTtyDetection()
    {
        // CLICOLOR=1 is the conventional default and must not override TTY detection.
        // CLICOLOR=1 は慣習的なデフォルトであり、TTY 判定を上書きしない。
        WithColorEnvironment(noColor: null, cliColor: "1", cliColorForce: null, () =>
        {
            Assert.Equal(ConsoleUi.ShouldUseInteractiveConsole(), ConsoleUi.ShouldUseColor());
        });
    }

    [Fact]
    public void ColorizeKind_CliColorForce_ForcesAnsiEvenWithoutTty()
    {
        WithColorEnvironment(noColor: null, cliColor: null, cliColorForce: "1", () =>
        {
            Assert.True(ConsoleUi.ShouldUseColor());
            var output = ConsoleUi.ColorizeKind("function");
            Assert.StartsWith("\x1b[33m", output);
            Assert.EndsWith("\x1b[0m", output);
        });
    }

    [Fact]
    public void ColorizeKind_CliColorForceZero_DoesNotForceColor()
    {
        // CLICOLOR_FORCE=0 must not force color on; behavior falls back to TTY detection.
        // CLICOLOR_FORCE=0 は色を強制せず、TTY 判定にフォールバックする。
        WithColorEnvironment(noColor: null, cliColor: null, cliColorForce: "0", () =>
        {
            Assert.Equal(ConsoleUi.ShouldUseInteractiveConsole(), ConsoleUi.ShouldUseColor());
        });
    }

    [Fact]
    public void ColorizeKind_CliColorForceBeatsNoColor()
    {
        // CLICOLOR_FORCE has the highest precedence so users can override a
        // global NO_COLOR for a single command.
        // CLICOLOR_FORCE は NO_COLOR より優先され、単発で色を有効化できる。
        WithColorEnvironment(noColor: "1", cliColor: "0", cliColorForce: "1", () =>
        {
            Assert.True(ConsoleUi.ShouldUseColor());
        });
    }

    private static void WithColorEnvironment(string? noColor, string? cliColor, string? cliColorForce, Action action)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalNoColor = Environment.GetEnvironmentVariable("NO_COLOR");
            var originalCliColor = Environment.GetEnvironmentVariable("CLICOLOR");
            var originalCliColorForce = Environment.GetEnvironmentVariable("CLICOLOR_FORCE");
            var originalColorTerm = Environment.GetEnvironmentVariable("COLORTERM");
            var originalTerm = Environment.GetEnvironmentVariable("TERM");
            var originalPaletteEnv = Environment.GetEnvironmentVariable("CDIDX_COLOR_PALETTE");
            var originalPalette = ConsoleUi.GetExplicitColorPalette();
            try
            {
                Environment.SetEnvironmentVariable("NO_COLOR", noColor);
                Environment.SetEnvironmentVariable("CLICOLOR", cliColor);
                Environment.SetEnvironmentVariable("CLICOLOR_FORCE", cliColorForce);
                // These ANSI-code assertions predate the palette feature and assume
                // the 8-color basic palette; pin it explicitly so the host's
                // COLORTERM/TERM cannot upgrade the palette mid-test.
                Environment.SetEnvironmentVariable("COLORTERM", null);
                Environment.SetEnvironmentVariable("TERM", null);
                Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", null);
                ConsoleUi.SetColorPalette(ColorPalette.Basic);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable("NO_COLOR", originalNoColor);
                Environment.SetEnvironmentVariable("CLICOLOR", originalCliColor);
                Environment.SetEnvironmentVariable("CLICOLOR_FORCE", originalCliColorForce);
                Environment.SetEnvironmentVariable("COLORTERM", originalColorTerm);
                Environment.SetEnvironmentVariable("TERM", originalTerm);
                Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", originalPaletteEnv);
                ConsoleUi.SetColorPalette(originalPalette);
            }
        }
    }

    [Theory]
    [InlineData("serach", "search")]
    [InlineData("statu", "status")]
    [InlineData("defnition", "definition")]
    [InlineData("refernces", "references")]
    [InlineData("indx", "index")]
    [InlineData("mpa", "map")]
    [InlineData("backfillfold", "backfill-fold")]
    public void FindClosestCommand_Typo_ReturnsCorrectSuggestion(string input, string expected)
    {
        Assert.Equal(expected, ConsoleUi.FindClosestCommand(input));
    }

    [Theory]
    [InlineData("xyzabc")]
    [InlineData("foobarqux")]
    [InlineData("fold")]
    public void FindClosestCommand_GarbageInput_ReturnsNull(string input)
    {
        Assert.Null(ConsoleUi.FindClosestCommand(input));
    }

    [Theory]
    [InlineData("--paht", "--path")]
    [InlineData("--exclud-path", "--exclude-path")]
    [InlineData("--limti", "--limit")]
    [InlineData("--lnag", "--lang")]
    public void FindClosestMatch_FlagTypo_SuggestsClosestFlag(string input, string expected)
    {
        var flags = new[] { "--path", "--exclude-path", "--limit", "--lang", "--kind", "--json", "--query" };

        Assert.Equal(expected, ConsoleUi.FindClosestMatch(input, flags));
    }

    [Theory]
    [InlineData("pythno", "python")]
    [InlineData("csarp", "csharp")]
    [InlineData("typescritp", "typescript")]
    public void FindClosestMatch_LanguageTypo_SuggestsClosestLanguage(string input, string expected)
    {
        var languages = new[] { "python", "csharp", "typescript", "javascript", "go", "rust" };

        Assert.Equal(expected, ConsoleUi.FindClosestMatch(input, languages));
    }

    [Fact]
    public void FindClosestMatch_BlankInput_ReturnsNull()
    {
        Assert.Null(ConsoleUi.FindClosestMatch(null, new[] { "csharp" }));
        Assert.Null(ConsoleUi.FindClosestMatch("", new[] { "csharp" }));
        Assert.Null(ConsoleUi.FindClosestMatch("  ", new[] { "csharp" }));
    }

    [Fact]
    public void FindClosestMatches_ReturnsRankedSuggestions()
    {
        var candidates = new[] { "added", "changed", "fixed", "removed", "security", "docs" };

        var matches = ConsoleUi.FindClosestMatches("addd", candidates, maxResults: 2);

        Assert.Contains("added", matches);
        Assert.True(matches.Count <= 2);
    }

    [Fact]
    public void FindClosestMatches_NoMatchesWithinThreshold_ReturnsEmpty()
    {
        var candidates = new[] { "added", "changed" };

        var matches = ConsoleUi.FindClosestMatches("absolutelynotrelated", candidates);

        Assert.Empty(matches);
    }

    [Fact]
    public void FindClosestMatches_ZeroMaxResults_ReturnsEmpty()
    {
        var candidates = new[] { "added", "changed" };

        var matches = ConsoleUi.FindClosestMatches("addd", candidates, maxResults: 0);

        Assert.Empty(matches);
    }

    [Theory]
    [InlineData("auto", ColorMode.Auto)]
    [InlineData("Always", ColorMode.Always)]
    [InlineData("NEVER", ColorMode.Never)]
    [InlineData(" always ", ColorMode.Always)]
    public void TryParseColorMode_KnownValue_ReturnsTrue(string value, ColorMode expected)
    {
        Assert.True(ConsoleUi.TryParseColorMode(value, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseColorMode_UnknownValue_ReturnsFalse(string? value)
    {
        Assert.False(ConsoleUi.TryParseColorMode(value, out _));
    }

    [Fact]
    public void ShouldUseColor_Always_OverridesNoColorEnv()
    {
        using var env = new ColorEnvironmentScope();
        Environment.SetEnvironmentVariable("NO_COLOR", "1");
        ConsoleUi.SetColorMode(ColorMode.Always);

        Assert.True(ConsoleUi.ShouldUseColor());
    }

    [Fact]
    public void ShouldUseColor_Never_OverridesCliColorForceEnv()
    {
        using var env = new ColorEnvironmentScope();
        Environment.SetEnvironmentVariable("CLICOLOR_FORCE", "1");
        ConsoleUi.SetColorMode(ColorMode.Never);

        Assert.False(ConsoleUi.ShouldUseColor());
    }

    [Fact]
    public void ColorizeKind_ColorModeAlways_EmitsAnsiEvenWhenRedirected()
    {
        using var env = new ColorEnvironmentScope();
        ConsoleUi.SetColorMode(ColorMode.Always);

        var output = ConsoleUi.ColorizeKind("class");

        Assert.Contains("\x1b[36m", output);
        Assert.Contains("\x1b[0m", output);
    }

    [Fact]
    public void ColorizeKind_ColorModeNever_OmitsAnsi()
    {
        using var env = new ColorEnvironmentScope();
        ConsoleUi.SetColorMode(ColorMode.Never);

        var output = ConsoleUi.ColorizeKind("class");

        Assert.DoesNotContain("\x1b[", output);
        Assert.Equal("class", output);
    }

    [Fact]
    public void PrintUsage_DocumentsColorFlag()
    {
        var output = CaptureUsageOutput(showBanner: false);
        Assert.Contains("--color <when>", output);
        Assert.Contains("`auto`", output);
        Assert.Contains("`always`", output);
        Assert.Contains("`never`", output);
        Assert.Contains("NO_COLOR", output);
        Assert.Contains("CLICOLOR_FORCE", output);
    }

    [Fact]
    public void PrintUsage_DocumentsPaletteFlag()
    {
        var output = CaptureUsageOutput(showBanner: false);
        Assert.Contains("--palette <name>", output);
        Assert.Contains("`basic`", output);
        Assert.Contains("`256`", output);
        Assert.Contains("`truecolor`", output);
        Assert.Contains("COLORTERM", output);
        Assert.Contains("CDIDX_COLOR_PALETTE", output);
    }

    [Theory]
    [InlineData("basic", ColorPalette.Basic)]
    [InlineData("BASIC", ColorPalette.Basic)]
    [InlineData("8", ColorPalette.Basic)]
    [InlineData("16", ColorPalette.Basic)]
    [InlineData("ansi", ColorPalette.Basic)]
    [InlineData("256", ColorPalette.Color256)]
    [InlineData("color256", ColorPalette.Color256)]
    [InlineData("8bit", ColorPalette.Color256)]
    [InlineData("truecolor", ColorPalette.Truecolor)]
    [InlineData("24bit", ColorPalette.Truecolor)]
    [InlineData("rgb", ColorPalette.Truecolor)]
    [InlineData(" Truecolor ", ColorPalette.Truecolor)]
    public void TryParseColorPalette_KnownValue_ReturnsTrue(string value, ColorPalette expected)
    {
        Assert.True(ConsoleUi.TryParseColorPalette(value, out var palette));
        Assert.Equal(expected, palette);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("none")]
    [InlineData("fancy")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseColorPalette_UnknownValue_ReturnsFalse(string? value)
    {
        Assert.False(ConsoleUi.TryParseColorPalette(value, out _));
    }

    [Fact]
    public void ColorizeKind_BasicPalette_DoesNotEmitDimEscape()
    {
        // The dim escape `\x1b[90m` (bright black) is unreadable on many
        // minimal SSH / CI terminals; the basic palette must avoid it (#1569).
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        ConsoleUi.SetColorMode(ColorMode.Always);
        ConsoleUi.SetColorPalette(ColorPalette.Basic);

        foreach (var kind in new[] { "namespace", "import", "class", "function" })
        {
            var output = ConsoleUi.ColorizeKind(kind);
            Assert.DoesNotContain("\x1b[90m", output);
        }
    }

    [Fact]
    public void ColorizeKind_Color256Palette_EmitsExtendedSgrCodes()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        ConsoleUi.SetColorMode(ColorMode.Always);
        ConsoleUi.SetColorPalette(ColorPalette.Color256);

        var output = ConsoleUi.ColorizeKind("class");
        Assert.StartsWith("\x1b[38;5;", output);
        Assert.EndsWith("\x1b[0m", output);
    }

    [Fact]
    public void ColorizeKind_TruecolorPalette_EmitsRgbSgrCodes()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        ConsoleUi.SetColorMode(ColorMode.Always);
        ConsoleUi.SetColorPalette(ColorPalette.Truecolor);

        var output = ConsoleUi.ColorizeKind("class");
        Assert.StartsWith("\x1b[38;2;", output);
        Assert.EndsWith("\x1b[0m", output);
    }

    [Fact]
    public void ResolveColorPalette_ExplicitOverrideWins()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", "256");
        ConsoleUi.SetColorPalette(ColorPalette.Basic);

        Assert.Equal(ColorPalette.Basic, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ResolveColorPalette_EnvVarWinsOverDetection()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", "basic");
        ConsoleUi.SetColorPalette(null);

        Assert.Equal(ColorPalette.Basic, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ResolveColorPalette_ColorTermTruecolor_DetectedAsTruecolor()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("TERM", "xterm");
        ConsoleUi.SetColorPalette(null);

        Assert.Equal(ColorPalette.Truecolor, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ResolveColorPalette_ColorTerm24bit_DetectedAsTruecolor()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", "24bit");
        Environment.SetEnvironmentVariable("TERM", "xterm");
        ConsoleUi.SetColorPalette(null);

        Assert.Equal(ColorPalette.Truecolor, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ResolveColorPalette_Term256color_DetectedAsColor256()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", null);
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        ConsoleUi.SetColorPalette(null);

        Assert.Equal(ColorPalette.Color256, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ResolveColorPalette_MinimalTerm_FallsBackToBasic()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", null);
        Environment.SetEnvironmentVariable("TERM", "xterm");
        ConsoleUi.SetColorPalette(null);

        Assert.Equal(ColorPalette.Basic, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ColorizeKind_NoColorRequested_SuppressesAllPalettes()
    {
        // NO_COLOR must consistently suppress ANSI escapes across every palette
        // so users opting out via the env standard get clean output regardless
        // of `--palette` (#1569).
        foreach (var palette in new[] { ColorPalette.Basic, ColorPalette.Color256, ColorPalette.Truecolor })
        {
            using var env = new ColorEnvironmentScope();
            using var pal = new ColorPaletteScope();
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            ConsoleUi.SetColorPalette(palette);

            var output = ConsoleUi.ColorizeKind("class");
            Assert.DoesNotContain('\x1b', output);
            Assert.Equal("class", output);
        }
    }

    private sealed class ColorPaletteScope : IDisposable
    {
        private readonly ColorPalette? _original;
        private readonly string? _originalEnv;
        private readonly string? _originalColorTerm;
        private readonly string? _originalTerm;

        public ColorPaletteScope()
        {
            _original = ConsoleUi.GetExplicitColorPalette();
            _originalEnv = Environment.GetEnvironmentVariable("CDIDX_COLOR_PALETTE");
            _originalColorTerm = Environment.GetEnvironmentVariable("COLORTERM");
            _originalTerm = Environment.GetEnvironmentVariable("TERM");
            Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", null);
            Environment.SetEnvironmentVariable("COLORTERM", null);
            Environment.SetEnvironmentVariable("TERM", null);
            ConsoleUi.SetColorPalette(null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", _originalEnv);
            Environment.SetEnvironmentVariable("COLORTERM", _originalColorTerm);
            Environment.SetEnvironmentVariable("TERM", _originalTerm);
            ConsoleUi.SetColorPalette(_original);
        }
    }

    private sealed class ColorEnvironmentScope : IDisposable
    {
        private readonly bool _lockTaken;
        private readonly string? _originalNoColor;
        private readonly string? _originalForce;
        private readonly string? _originalCliColor;
        private readonly string? _originalColorTerm;
        private readonly string? _originalTerm;
        private readonly string? _originalPaletteEnv;
        private readonly ColorMode _originalMode;
        private readonly ColorPalette? _originalPalette;

        public ColorEnvironmentScope()
        {
            Monitor.Enter(TestConsoleLock.Gate);
            _lockTaken = true;
            _originalNoColor = Environment.GetEnvironmentVariable("NO_COLOR");
            _originalForce = Environment.GetEnvironmentVariable("CLICOLOR_FORCE");
            _originalCliColor = Environment.GetEnvironmentVariable("CLICOLOR");
            _originalColorTerm = Environment.GetEnvironmentVariable("COLORTERM");
            _originalTerm = Environment.GetEnvironmentVariable("TERM");
            _originalPaletteEnv = Environment.GetEnvironmentVariable("CDIDX_COLOR_PALETTE");
            _originalMode = ConsoleUi.GetColorMode();
            _originalPalette = ConsoleUi.GetExplicitColorPalette();
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            Environment.SetEnvironmentVariable("CLICOLOR_FORCE", null);
            Environment.SetEnvironmentVariable("CLICOLOR", null);
            Environment.SetEnvironmentVariable("COLORTERM", null);
            Environment.SetEnvironmentVariable("TERM", null);
            Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", null);
            ConsoleUi.SetColorPalette(null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("NO_COLOR", _originalNoColor);
            Environment.SetEnvironmentVariable("CLICOLOR_FORCE", _originalForce);
            Environment.SetEnvironmentVariable("CLICOLOR", _originalCliColor);
            Environment.SetEnvironmentVariable("COLORTERM", _originalColorTerm);
            Environment.SetEnvironmentVariable("TERM", _originalTerm);
            Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", _originalPaletteEnv);
            ConsoleUi.SetColorMode(_originalMode);
            ConsoleUi.SetColorPalette(_originalPalette);
            if (_lockTaken)
                Monitor.Exit(TestConsoleLock.Gate);
        }
    }

    private static string CaptureUsageOutput(bool showBanner = true)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();

            try
            {
                Console.SetOut(writer);
                ConsoleUi.PrintUsage(showBanner);
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private static string ExtractBetween(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        start += startMarker.Length;
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Missing end marker: {endMarker}");
        return text[start..end];
    }
}
