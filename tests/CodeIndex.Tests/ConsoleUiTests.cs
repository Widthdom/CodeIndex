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
        Assert.Contains("cdidx index <projectPath> [--db <path>] [--rebuild] [--verbose] [--dry-run] [--json]", output);
        Assert.Contains("cdidx index <projectPath> --commits <id> [id ...] [--db <path>] [--verbose] [--dry-run] [--json]", output);
        Assert.Contains("cdidx index <projectPath> --files <path> [path ...] [--db <path>] [--verbose] [--dry-run] [--json]", output);
        Assert.Contains("cdidx backfill-fold [--db <path>] [--json]", output);
        Assert.Contains("cdidx license", output);
        Assert.Contains("cdidx references <query>|--query <query>|-- <query>", output);
        Assert.Contains("cdidx callers <query>|--query <query>|-- <query>", output);
        Assert.Contains("cdidx callees <query>|--query <query>|-- <query>", output);
        Assert.Contains("cdidx search <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--snippet-lines <n>] [--max-line-width <n>] [--fts] [--exact|--exact-substring] [--count] [--since <datetime>] [--no-dedup]", output);
        Assert.Contains("cdidx definition <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--exact|--exact-name] [--count] [--since <datetime>]", output);
        Assert.Contains("cdidx references <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--max-line-width <n>] [--exact|--exact-name] [--count]", output);
        Assert.Contains("cdidx inspect <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--max-line-width <n>] [--exact|--exact-name]", output);
        Assert.Contains("--snippet-lines <n>        Search snippet length (1-20, default: 8)", output);
        Assert.Contains("--max-line-width <n>       search/references/find/excerpt/inspect only: clamp very long single-line snippet/context/excerpt payloads (`0` disables clamping; default: 512)", output);
        Assert.Contains("cdidx find <query> --path <glob>", output);
        Assert.Contains("--exact-substring          Search only: case-sensitive exact substring (no FTS5)", output);
        Assert.Contains("--exact-name               symbols/definition/references/callers/callees/inspect: NFKC + Unicode CaseFold exact name match", output);
        Assert.Contains("--kind <kind>              definition/symbols/hotspots/unused: symbol kind; references: reference kind (call/instantiate/subscribe/attribute/annotation); callers/callees: call-graph kinds only (call/instantiate/subscribe — metadata kinds rejected, use references instead); validate: issue kind", output);
        Assert.Contains("--count                    Count only; search/definition/references/callers/callees/symbols/files/find/unused ignore --limit, impact/hotspots still use visible page counts", output);
        Assert.Contains("--commits <id> [id ...]    Update only files changed in the specified git commits (preferred after commits)", output);
        Assert.Contains("--files <path> [path ...]  Update only the specified files; old rename/delete paths are not purged unless also listed", output);
        Assert.Contains("cdidx excerpt <path> --start <line> [--end <line>] [--before <n>] [--after <n>] [--max-line-width <n>] [--focus-line <line>] [--focus-column <n>] [--focus-length <n>] [--db <path>] [--json]", output);
        Assert.Contains("--focus-column <n>         excerpt: column to keep centered when clamping (must be within the focused line)", output);
        Assert.Contains("--focus-line <line>        excerpt: line whose focused column should stay visible", output);
        Assert.Contains("cdidx map [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests]", output);
        Assert.Contains("cdidx symbols [query|--query <query>|-- <query>] [--name <name>] [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--count] [--since <datetime>]", output);
        Assert.Contains("cdidx files [query|--query <query>|-- <query>] [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--since <datetime>]", output);
        Assert.Contains("cdidx validate [--db <path>] [--json] [--kind <kind>] [--path <glob>]", output);
        Assert.Contains("Note: if a query itself starts with '-', pass it with --query <query> or -- <query>", output);
        Assert.DoesNotContain("cdidx validate [--db <path>] [--json] [--limit <n>] [--lang <lang>]", output);
        Assert.Contains("cdidx unused [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count]", output);
        Assert.Contains("cdidx hotspots [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--group-by-name]", output);
        Assert.Contains("--json                     Output as JSON (streaming hits use JSON lines; counts/summaries use one object)", output);
        Assert.Contains("--lang <lang>              Filter by language (aliases: bat, cmd)", output);
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
        Assert.Contains("Use --files only for known in-place edits or new files", output);
        Assert.Contains("cdidx index ./myproject --commits abc123", output);
    }

    [Fact]
    public void PrintUsage_QueryLinesMatchImplementedOptions()
    {
        var output = CaptureUsageOutput(showBanner: false);

        Assert.Contains("cdidx search <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--snippet-lines <n>] [--max-line-width <n>] [--fts] [--exact|--exact-substring] [--count] [--since <datetime>] [--no-dedup]", output);
        Assert.Contains("cdidx symbols [query|--query <query>|-- <query>] [--name <name>] [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--count] [--since <datetime>]", output);
        Assert.Contains("cdidx files [query|--query <query>|-- <query>] [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--since <datetime>]", output);
        Assert.Contains("cdidx hotspots [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count]", output);
        Assert.Contains("cdidx unused [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count]", output);
        Assert.Contains("cdidx license", output);
    }

    [Fact]
    public void GetSpinnerFrames_Default_UsesRotatingBrailleSequence()
    {
        var frames = ConsoleUi.GetSpinnerFrames(null);

        Assert.Equal(["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"], frames);
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
                var maxLineWidthToken = shell == "fish"
                    ? "Clamp long single-line payloads (0 disables clamping)"
                    : shell == "bash"
                        ? "--max-line-width"
                        : "--max-line-width[Clamp long single-line snippets (0 disables clamping)]:number";
                Assert.Contains(exactSubstringToken, output);
                Assert.Contains(exactNameToken, output);
                Assert.Contains(groupByNameToken, output);
                Assert.Contains(licenseToken, output);
                Assert.Contains(maxLineWidthToken, output);
                if (shell == "zsh")
                    Assert.Contains("--max-line-width[Clamp long single-line contexts (0 disables clamping)]:number", output);
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
        var output = ConsoleUi.GetCompletionScript("fish");
        Assert.Contains("__fish_seen_subcommand_from search definition references callers callees symbols files find", output);
        Assert.Contains("__fish_seen_subcommand_from find excerpt", output);
        Assert.Contains("__fish_seen_subcommand_from search find", output);
        Assert.Contains("-l query -r -d 'Literal query'", output);
        Assert.Contains("-l before -r -d 'Context lines before'", output);
        Assert.Contains("-l after -r -d 'Context lines after'", output);
        Assert.Contains("-l exact -d 'Exact match'", output);
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
    public void GetCompletionLangs_IncludesWindowsBatchAliases()
    {
        var method = typeof(ConsoleUi).GetMethod("GetCompletionLangs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        var value = method!.Invoke(null, []);

        var langs = value?.ToString() ?? string.Empty;
        var tokens = langs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("bat", tokens);
        Assert.Contains("batch", tokens);
        Assert.Contains("cmd", tokens);
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
