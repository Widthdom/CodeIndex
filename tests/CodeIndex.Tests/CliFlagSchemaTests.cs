using System.Reflection;
using System.Text.RegularExpressions;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Guards the single-source-of-truth contract introduced by #1570: <see cref="CliFlagSchema"/>
/// drives the per-command parser allowlists (`TryWriteUnsupportedOptionError` /
/// `ValidateFindArgs`), command help inventory, next-step tokens, and the bash / zsh / fish
/// completion generators in <see cref="ConsoleCompletionRenderer"/>.
/// These tests fail fast when the schema, help, and generated completion scripts drift apart,
/// or when a flag's <c>Commands</c> / <c>AlsoAcceptedBy</c> sets reference unknown subcommands.
/// #1570 で導入した「フラグ単一情報源」の契約を守るためのテスト群。スキーマと
/// 補完スクリプト、コマンド一覧、parser-vs-completion の許容差分がずれた瞬間に失敗する。
/// </summary>
[Collection("Console sensitive")]
public class CliFlagSchemaTests
{
    private static readonly IReadOnlySet<string> CompletionGlobalFlags =
        new HashSet<string>(["--quiet", "--silent", "--no-progress"], StringComparer.Ordinal);

    [Fact]
    public void AllCommands_MatchesCliCommandCatalog()
    {
        Assert.Equal(CliCommandCatalog.PublicCommandNames, CliFlagSchema.AllCommands);
    }

    [Fact]
    public void BatchReadOnlyCommands_AreCatalogedAndFailClosed_Issue4582()
    {
        var known = CliFlagSchema.AllCommands.ToHashSet(StringComparer.Ordinal);
        Assert.All(CliCommandCatalog.BatchReadOnlyCommands, command => Assert.Contains(command, known));
        Assert.Contains("goto", CliCommandCatalog.BatchReadOnlyCommands);
        Assert.Contains("audit", CliCommandCatalog.BatchReadOnlyCommands);
        Assert.DoesNotContain("index", CliCommandCatalog.BatchReadOnlyCommands);
        Assert.DoesNotContain("import", CliCommandCatalog.BatchReadOnlyCommands);
        Assert.DoesNotContain("hooks", CliCommandCatalog.BatchReadOnlyCommands);
    }

    [Fact]
    public void EveryFlagCommandsSet_OnlyReferencesKnownSubcommands()
    {
        var known = CliFlagSchema.AllCommands.ToHashSet(StringComparer.Ordinal);
        foreach (var flag in CliFlagSchema.All)
        {
            foreach (var command in flag.PrimaryCommands)
                Assert.True(known.Contains(command), $"{flag.Name} Commands references unknown subcommand '{command}'");
            foreach (var command in flag.AlsoAcceptedBy)
                Assert.True(known.Contains(command), $"{flag.Name} AlsoAcceptedBy references unknown subcommand '{command}'");
            foreach (var command in flag.CompletionSubcommands.Keys)
                Assert.True(known.Contains(command), $"{flag.Name} CompletionSubcommands references unknown command '{command}'");
            foreach (var command in flag.ParentCompletionCommands)
            {
                Assert.True(known.Contains(command), $"{flag.Name} ParentCompletionCommands references unknown command '{command}'");
                Assert.True(flag.CompletionSubcommands.ContainsKey(command), $"{flag.Name} keeps parent completion for '{command}' without a nested applicability entry");
            }
            if (flag.ShortNameCommands is not null)
            {
                Assert.NotNull(flag.ShortName);
                foreach (var command in flag.ShortNameCommands)
                {
                    Assert.True(known.Contains(command), $"{flag.Name} ShortNameCommands references unknown command '{command}'");
                    Assert.True(flag.PrimaryCommands.Contains(command), $"{flag.Name} exposes its short name for non-primary command '{command}'");
                }
            }
        }
    }

    [Fact]
    public void FlagPrimaryAndAlsoAcceptedSets_DoNotOverlap()
    {
        foreach (var flag in CliFlagSchema.All)
        {
            foreach (var command in flag.AlsoAcceptedBy)
                Assert.False(flag.PrimaryCommands.Contains(command),
                    $"{flag.Name}: '{command}' appears in both PrimaryCommands and AlsoAcceptedBy");
        }
    }

    [Fact]
    public void EveryFlag_HasCommandOrTopLevelScope()
    {
        foreach (var flag in CliFlagSchema.All)
            Assert.True(flag.PrimaryCommands.Count > 0 || flag.TopLevel, $"{flag.Name} must apply to a command or top-level scope.");
    }

    [Fact]
    public void FlagNames_AreUniqueAndDoubleDashed()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in CliFlagSchema.All)
        {
            Assert.StartsWith("--", flag.Name);
            Assert.True(seen.Add(flag.Name), $"Duplicate flag in schema: {flag.Name}");
            if (flag.ShortName is not null)
            {
                Assert.StartsWith("-", flag.ShortName);
                Assert.DoesNotContain("--", flag.ShortName);
            }
        }
    }

    [Fact]
    public void GetAcceptedFlagNamesForCommand_IncludesEndOfOptionsForQueryPassthroughCommands()
    {
        // Commands that accept literal queries beginning with '-' must allow `--` as the
        // end-of-options marker. The schema injects `--` only for these commands.
        // クエリ先頭が `-` で始まる場合の end-of-options マーカーが付与されるか確認。
        var passthroughCommands = new[]
        {
            "search", "definition", "references", "callers", "callees",
            "symbols", "files", "inspect", "impact",
        };
        foreach (var command in passthroughCommands)
            Assert.Contains("--", CliFlagSchema.GetAcceptedFlagNamesForCommand(command));

        // `find` accepts a literal dashed query via the `--` marker too, but that is
        // enforced separately by `ValidateFindArgs` (PrepareFindArgs consumes it before
        // validation), so the allowlist deliberately omits `--`.
        // `find` は ValidateFindArgs 側で `--` を吸収するため、allowlist には載らない。
        Assert.DoesNotContain("--", CliFlagSchema.GetAcceptedFlagNamesForCommand("find"));

        // Index-only commands (no query positional) must NOT include `--`.
        // クエリ positional を取らないコマンドには `--` が紛れ込まないこと。
        foreach (var command in new[] { "index", "status", "db", "languages", "license", "mcp", "report" })
            Assert.DoesNotContain("--", CliFlagSchema.GetAcceptedFlagNamesForCommand(command));
    }

    [Fact]
    public void GetAcceptedFlagNamesForCommand_UnionsCommandsAndAlsoAcceptedBy()
    {
        // `--exact-name` is primary on the symbol-resolution commands but is also accepted
        // by the search parser (so users mid-migration get a friendlier error than "unknown
        // option"). The allowlist must include `--exact-name` for search even though shell
        // completions deliberately do not.
        // `--exact-name` は symbol 系で primary だが search でもパーサが受理する。
        Assert.Contains("--exact-name", CliFlagSchema.GetAcceptedFlagNamesForCommand("search"));
        Assert.DoesNotContain(CliFlagSchema.GetCompletionFlagsForCommand("search"), f => f.Name == "--exact-name");

        // Conversely `--exact-substring` is primary on search and accepted (but not
        // completed) on the other name-resolution commands.
        // `--exact-substring` は search で primary、他の name コマンドではパーサ受理のみ。
        Assert.Contains("--exact-substring", CliFlagSchema.GetAcceptedFlagNamesForCommand("definition"));
        Assert.DoesNotContain(CliFlagSchema.GetCompletionFlagsForCommand("definition"), f => f.Name == "--exact-substring");
    }

    [Fact]
    public void McpPublicOptionInventoryMatchesParserAndAuthoritativeHelp_Issue5096()
    {
        string[] expectedOptions =
        [
            "--db",
            "--quiet",
            "--silent",
            "--no-progress",
            "--transport",
            "--http-listen",
            "--allow-unauthenticated-http",
            "--audit-log",
            "--audit-log-include-values",
            "--audit-log-max-bytes",
            "--audit-log-strict",
            "--suggestion-dedup-threshold",
        ];
        var inventory = CliFlagSchema.GetCompletionFlagsForCommand("mcp")
            .Select(flag => flag.Name)
            .ToArray();

        Assert.Equal(expectedOptions, inventory);
        Assert.Equal(
            expectedOptions.OrderBy(option => option, StringComparer.Ordinal),
            CliFlagSchema.GetAcceptedFlagNamesForCommand("mcp").OrderBy(option => option, StringComparer.Ordinal));

        var parserProbes = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["--db"] = ["mcp", "--db"],
            ["--quiet"] = ["--quiet", "mcp", "--audit-log-strict"],
            ["--silent"] = ["--silent", "mcp", "--audit-log-strict"],
            ["--no-progress"] = ["--no-progress", "mcp", "--audit-log-strict"],
            ["--transport"] = ["mcp", "--transport"],
            ["--http-listen"] = ["mcp", "--http-listen"],
            ["--allow-unauthenticated-http"] = ["mcp", "--allow-unauthenticated-http"],
            ["--audit-log"] = ["mcp", "--audit-log"],
            ["--audit-log-include-values"] = ["mcp", "--audit-log-include-values"],
            ["--audit-log-max-bytes"] = ["mcp", "--audit-log-max-bytes"],
            ["--audit-log-strict"] = ["mcp", "--audit-log-strict"],
            ["--suggestion-dedup-threshold"] = ["mcp", "--suggestion-dedup-threshold"],
        };
        Assert.Equal(
            expectedOptions.OrderBy(option => option, StringComparer.Ordinal),
            parserProbes.Keys.OrderBy(option => option, StringComparer.Ordinal));
        foreach (var (option, args) in parserProbes)
        {
            var (exitCode, _, stderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(args, appVersion: "1.10.0"));
            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.DoesNotContain($"{option} is not supported for mcp", stderr, StringComparison.Ordinal);
        }

        var (printed, helpOutput, helpError) = ConsoleCapture.Capture(() =>
            ConsoleUi.PrintCommandUsage("mcp") ? 1 : 0);
        Assert.Equal(1, printed);
        Assert.Empty(helpError);
        foreach (var option in expectedOptions)
            Assert.Contains(option, helpOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DedicatedCommandOptionInventoriesMatchSchemaHelpAndParsers_Issue5194()
    {
        var expectedByContext = new Dictionary<(string Command, string? Nested), string[]>
        {
            [("lsp", null)] = ["--db"],
            [("workspace", null)] = ["--json"],
            [("workspace", "list")] = ["--json"],
            [("workspace", "status")] = ["--json", "--check"],
            [("workspace", "use")] = ["--json"],
            [("workspace", "current")] = ["--json"],
            [("workspace", "clear")] = ["--json"],
            [("workspace", "deactivate")] = ["--json"],
            [("config", null)] = [],
            [("config", "show")] = ["--json", "--show-paths"],
            [("diff", null)] =
            [
                "--json", "--summary-only", "--detailed", "--data-only", "--include-telemetry",
                "--include-content", "--max-json-bytes", "--limit", "--offset", "--cursor",
            ],
            [("import", null)] =
            [
                "--db", "--prune-paths", "--no-backup", "--dry-run", "--check", "--limit", "--offset", "--json",
            ],
            [("export", null)] =
            [
                "--db", "--json", "--overwrite", "--lang", "--path", "--exclude-path", "--project", "--solution", "--exclude-tests",
            ],
            [("export", "ctags")] =
            [
                "--output", "--db", "--json", "--lang", "--path", "--exclude-path", "--exclude-tests", "--include-generated",
            ],
        };

        foreach (var ((command, nested), expected) in expectedByContext)
        {
            var actual = CliFlagSchema.GetCompletionFlagsForCommand(command, nested)
                .Select(flag => flag.Name)
                .Where(name => !CompletionGlobalFlags.Contains(name))
                .ToHashSet(StringComparer.Ordinal);
            AssertOptionSet(expected, actual, $"schema context {command}{(nested is null ? string.Empty : $" {nested}")}");
        }

        foreach (var commandGroup in expectedByContext.GroupBy(entry => entry.Key.Command, StringComparer.Ordinal))
        {
            var expected = commandGroup.SelectMany(entry => entry.Value).ToHashSet(StringComparer.Ordinal);
            var accepted = CliFlagSchema.GetAcceptedFlagNamesForCommand(commandGroup.Key)
                .Where(name => !CompletionGlobalFlags.Contains(name))
                .ToHashSet(StringComparer.Ordinal);
            AssertOptionSet(expected, accepted, $"accepted schema inventory for {commandGroup.Key}");

            var (printed, help, error) = ConsoleCapture.Capture(() =>
                ConsoleUi.PrintCommandUsage(commandGroup.Key) ? 1 : 0);
            Assert.Equal(1, printed);
            Assert.Empty(error);
            var documented = Regex.Matches(help, @"--[a-z][a-z0-9-]*")
                .Select(match => match.Value)
                .Where(name => name != "--help")
                .ToHashSet(StringComparer.Ordinal);
            AssertOptionSet(expected, documented, $"authoritative help inventory for {commandGroup.Key}");
        }

        var jsonOptions = ProgramRunner.CreateDefaultJsonOptions();
        var (_, _, lspError) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run(["lsp", "--db"], appVersion: "1.10.0"));
        Assert.DoesNotContain("--db is not supported for lsp", lspError, StringComparison.Ordinal);

        var (_, _, workspaceJsonError) = ConsoleCapture.Capture(() =>
            WorkspaceCommandRunner.Run(["list", "--json"], jsonOptions));
        Assert.Empty(workspaceJsonError);
        var (_, _, workspaceCheckError) = ConsoleCapture.Capture(() =>
            WorkspaceCommandRunner.Run(["status", "--check"], jsonOptions));
        Assert.DoesNotContain("only valid", workspaceCheckError, StringComparison.OrdinalIgnoreCase);
        var (invalidWorkspaceCheck, _, _) = ConsoleCapture.Capture(() =>
            WorkspaceCommandRunner.Run(["list", "--check"], jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, invalidWorkspaceCheck);

        var (configShowExitCode, _, configShowError) = ConsoleCapture.Capture(() =>
            CdidxConfigFile.RunShow(["--json", "--show-paths"], jsonOptions));
        Assert.Equal(CommandExitCodes.Success, configShowExitCode);
        Assert.Empty(configShowError);

        foreach (var option in expectedByContext[("diff", null)])
        {
            var parsed = DiffCommandOptionsParser.Parse(["left.db", "right.db", option], DiffCommandRunner.MaxDiffLimit);
            Assert.DoesNotContain("does not support option", parsed.ParseError ?? string.Empty, StringComparison.Ordinal);
        }
        var conflictingDiffModes = DiffCommandOptionsParser.Parse(
            ["left.db", "right.db", "--data-only", "--include-telemetry"],
            DiffCommandRunner.MaxDiffLimit);
        Assert.Contains("cannot be combined", conflictingDiffModes.ParseError, StringComparison.Ordinal);
        var conflictingDiffPaging = DiffCommandOptionsParser.Parse(
            ["left.db", "right.db", "--json", "--detailed", "--cursor", "cursor", "--offset", "0"],
            DiffCommandRunner.MaxDiffLimit);
        Assert.Contains("--cursor cannot be combined with --offset", conflictingDiffPaging.ParseError, StringComparison.Ordinal);

        foreach (var option in expectedByContext[("import", null)])
        {
            var (_, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([option], jsonOptions));
            Assert.DoesNotContain($"unknown import option `{option}`", stdout + stderr, StringComparison.Ordinal);
        }

        foreach (var option in expectedByContext[("export", null)])
        {
            var (_, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport([option], jsonOptions, "1.10.0"));
            Assert.DoesNotContain($"unknown export option `{option}`", stdout + stderr, StringComparison.Ordinal);
        }

        foreach (var option in expectedByContext[("export", "ctags")])
        {
            var args = CliFlagSchema.GetFlag("export", option)!.IsValueBearing
                ? new[] { "ctags", option }
                : new[] { "ctags", option, "--issue5194-probe" };
            var (_, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(args, jsonOptions, "1.10.0"));
            Assert.DoesNotContain($"unknown ctags export option `{option}`", stdout + stderr, StringComparison.Ordinal);
        }

        var output = CliFlagSchema.GetFlag("export", "--output");
        Assert.NotNull(output);
        Assert.Null(output!.GetShortName("export"));
        Assert.Equal("-o", output.GetShortName("report"));
        Assert.Equal(CliOptionValueKind.FilePath, output.GetValueKind("export", "ctags"));
        Assert.Equal(CliOptionValueKind.FilePath, CliFlagSchema.GetValueKindForCommand("lsp", "--db"));
        Assert.Equal(CliOptionValueKind.Language, CliFlagSchema.GetValueKindForCommand("export", "--lang"));
        Assert.False(CliFlagSchema.GetFlag("workspace", "--json")!.IsValueBearing);

        var (_, ctagsAliasOutput, ctagsAliasError) = ConsoleCapture.Capture(() =>
            ExportImportCommandRunner.RunExport(["ctags", "-o"], jsonOptions, "1.10.0"));
        Assert.Contains("unknown ctags export option `-o`", ctagsAliasOutput + ctagsAliasError, StringComparison.Ordinal);
    }

    [Fact]
    public void DedicatedCommandContextsRenderExactlyAcrossAllShells_Issue5194()
    {
        (string Command, string? Nested)[] contexts =
        [
            ("lsp", null),
            ("workspace", null),
            ("workspace", "list"),
            ("workspace", "status"),
            ("workspace", "use"),
            ("workspace", "current"),
            ("workspace", "clear"),
            ("workspace", "deactivate"),
            ("config", null),
            ("config", "show"),
            ("diff", null),
            ("import", null),
            ("export", null),
            ("export", "ctags"),
        ];

        foreach (var shell in new[] { "bash", "zsh", "fish", "powershell" })
        {
            var script = ConsoleCompletionRenderer.GetCompletionScript(shell);
            foreach (var (command, nested) in contexts)
            {
                var expected = CliFlagSchema.GetCompletionFlagsForCommand(command, nested)
                    .Select(flag => flag.Name)
                    .Where(name => !CompletionGlobalFlags.Contains(name));
                var actual = ExtractContextLongFlags(script, shell, command, nested);
                actual.ExceptWith(CompletionGlobalFlags);
                AssertOptionSet(
                    expected,
                    actual,
                    $"{shell} completion context {command}{(nested is null ? string.Empty : $" {nested}")}");
            }
        }
    }

    [Fact]
    public void Goto_AcceptsDocumentedExcludeFilters_Issue3934()
    {
        var accepted = CliFlagSchema.GetAcceptedFlagNamesForCommand("goto");
        Assert.Contains("--exclude-tests", accepted);
        Assert.Contains("--exclude-path", accepted);
    }

    [Fact]
    public void QualifiedCommonCallCompletenessFlag_IsScopedToGraphCommands_Issue4867()
    {
        const string flag = "--include-qualified-common-calls";
        foreach (var command in new[] { "references", "callers", "callees" })
        {
            Assert.Contains(flag, CliFlagSchema.GetAcceptedFlagNamesForCommand(command));
            Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand(command), option => option.Name == flag);
        }

        Assert.DoesNotContain(flag, CliFlagSchema.GetAcceptedFlagNamesForCommand("search"));
    }

    [Fact]
    public void DiffComparisonModes_AreRegisteredForCompletions_Issue4884()
    {
        var flags = CliFlagSchema.GetCompletionFlagsForCommand("diff")
            .Select(flag => flag.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("--data-only", flags);
        Assert.Contains("--include-telemetry", flags);
    }

    [Fact]
    public void AuditAggregationFlags_SurfaceDocumentedRecipeGrouping_Issues4301_4339()
    {
        var accepted = CliFlagSchema.GetAcceptedFlagNamesForCommand("audit");
        foreach (var flag in new[] { "--count", "--group-by", "--count-by", "--unique" })
            Assert.Contains(flag, accepted);

        var completionFlags = CliFlagSchema.GetCompletionFlagsForCommand("audit").ToList();
        var groupBy = Assert.Single(completionFlags, flag => flag.Name == "--group-by");
        var countBy = Assert.Single(completionFlags, flag => flag.Name == "--count-by");
        var unique = Assert.Single(completionFlags, flag => flag.Name == "--unique");

        foreach (var placeholder in new[]
                 {
                     groupBy.GetValuePlaceholder("audit"),
                     countBy.GetValuePlaceholder("audit"),
                     unique.GetValuePlaceholder("audit"),
                 })
        {
            Assert.Contains("return-type", placeholder, StringComparison.Ordinal);
            Assert.Contains("subsystem", placeholder, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MemberReadCompatibilityFlag_IsScopedToGraphTraversalCommands_Issue4894()
    {
        const string flag = "--include-member-reads";
        foreach (var command in new[] { "callers", "callees", "impact" })
        {
            Assert.Contains(flag, CliFlagSchema.GetAcceptedFlagNamesForCommand(command));
            Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand(command), option => option.Name == flag);
        }

        Assert.DoesNotContain(flag, CliFlagSchema.GetAcceptedFlagNamesForCommand("references"));
        Assert.DoesNotContain(flag, CliFlagSchema.GetAcceptedFlagNamesForCommand("search"));
    }

    [Fact]
    public void UpgradeFlags_SurfaceImplementedSelectionAndJsonOptions()
    {
        var accepted = CliFlagSchema.GetAcceptedFlagNamesForCommand("upgrade");
        Assert.Contains("--json", accepted);
        Assert.Contains("--channel", accepted);
        Assert.Contains("--prerelease", accepted);
        Assert.Contains("--version", accepted);

        var channel = Assert.Single(CliFlagSchema.GetCompletionFlagsForCommand("upgrade"), f => f.Name == "--channel");
        Assert.Equal("<stable|latest|prerelease>", channel.GetValuePlaceholder("upgrade"));
        Assert.DoesNotContain("reserved", channel.Description, StringComparison.OrdinalIgnoreCase);

        var prerelease = Assert.Single(CliFlagSchema.GetCompletionFlagsForCommand("upgrade"), f => f.Name == "--prerelease");
        Assert.DoesNotContain("reserved", prerelease.Description, StringComparison.OrdinalIgnoreCase);

        var version = Assert.Single(CliFlagSchema.GetCompletionFlagsForCommand("upgrade"), f => f.Name == "--version");
        Assert.Equal("<tag>", version.ValuePlaceholder);
    }

    [Fact]
    public void TopLevelGlobalSchema_IncludesLogFlagsAndMatchesProgramRunnerParserSets()
    {
        var topLevel = CliFlagSchema.GetTopLevelGlobalOptionNames(includeLogOptions: true);
        Assert.Contains("--log-format", topLevel);
        Assert.Contains("--log-retain-count", topLevel);
        Assert.Contains("--log-max-size-mb", topLevel);
        Assert.Contains("--quiet", topLevel);
        Assert.Contains("-q", topLevel);

        var valueNames = CliFlagSchema.GetTopLevelValueOptionNames();
        Assert.Contains("--log-format", valueNames);
        Assert.Contains("--log-retain-count", valueNames);
        Assert.Contains("--log-max-size-mb", valueNames);

        var nonLog = CliFlagSchema.GetTopLevelGlobalOptionNames(includeLogOptions: false);
        Assert.DoesNotContain("--log-format", nonLog);
        Assert.DoesNotContain("--log-retain-count", nonLog);
        Assert.DoesNotContain("--log-max-size-mb", nonLog);

        Assert.Equal(valueNames, GetProgramRunnerStringSet("TopLevelValueOptionNames"));
        Assert.Equal(nonLog, GetProgramRunnerStringSet("NonLogGlobalOptionNames"));
    }

    [Fact]
    public void VisibilityFilters_AreScopedToSymbolVisibilityCommands()
    {
        var visibilityCommands = new[] { "definition", "symbols", "unused", "hotspots" };
        foreach (var command in visibilityCommands)
        {
            Assert.Contains("--visibility", CliFlagSchema.GetAcceptedFlagNamesForCommand(command));
            Assert.Contains("--exclude-visibility", CliFlagSchema.GetAcceptedFlagNamesForCommand(command));
            Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand(command), f => f.Name == "--visibility");
            Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand(command), f => f.Name == "--exclude-visibility");
        }

        foreach (var command in CliFlagSchema.AllCommands.Except(visibilityCommands))
        {
            Assert.DoesNotContain("--visibility", CliFlagSchema.GetAcceptedFlagNamesForCommand(command));
            Assert.DoesNotContain("--exclude-visibility", CliFlagSchema.GetAcceptedFlagNamesForCommand(command));
        }
    }

    [Fact]
    public void GetParserFlagsPartitionedByValueBearing_MatchesFlagShape()
    {
        var (withValues, flagOnly) = CliFlagSchema.GetParserFlagsPartitionedByValueBearing("find");

        // `find` parser must accept `--query`/`--path`/etc. as value-bearing.
        // `--exclude-tests` / `--count` are flag-only.
        Assert.Contains("--query", withValues);
        Assert.Contains("--path", withValues);
        Assert.Contains("--limit", withValues);
        Assert.Contains("--before", withValues);
        Assert.Contains("--exclude-tests", flagOnly);
        Assert.Contains("--count", flagOnly);

        // The two sets must be disjoint and cover the same flag names that the unified
        // allowlist returns (modulo `--` which the partitioning helper deliberately drops).
        Assert.Empty(withValues.Intersect(flagOnly));
        var combined = new HashSet<string>(withValues.Concat(flagOnly), StringComparer.Ordinal);
        var unified = CliFlagSchema.GetAcceptedFlagNamesForCommand("find").Where(n => n != "--").ToHashSet(StringComparer.Ordinal);
        Assert.Equal(unified, combined);
    }

    [Fact]
    public void SuggestionsParserFlags_IncludeHiddenLangAlias_Issue4162()
    {
        var accepted = CliFlagSchema.GetAcceptedFlagNamesForCommand("suggestions");
        Assert.Contains("--language", accepted);
        Assert.Contains("--lang", accepted);
        Assert.Contains("--query", accepted);
        Assert.Contains("--max-json-bytes", accepted);

        var (withValues, flagOnly) = CliFlagSchema.GetParserFlagsPartitionedByValueBearing("suggestions");
        Assert.Contains("--lang", withValues);
        Assert.Contains("--language", withValues);
        Assert.Contains("--description", withValues);
        Assert.Contains("--evidence-path", withValues);
        Assert.Contains("--query", withValues);
        Assert.Contains("--max-json-bytes", withValues);
        Assert.Contains("--json", flagOnly);
        Assert.Contains("--count", flagOnly);
        Assert.Contains("--summary-only", flagOnly);
        Assert.Contains("--compact", flagOnly);
        Assert.DoesNotContain("--json", withValues);

        Assert.DoesNotContain(CliFlagSchema.GetCompletionFlagsForCommand("suggestions"), flag => flag.Name == "--lang");

        var parse = typeof(SuggestionsCommandRunner).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(parse);

        var parsed = parse!.Invoke(null, [new[] { "--lang=csharp", "--query=needle", "--compact", "--max-json-bytes=4096" }]);
        Assert.NotNull(parsed);
        Assert.Equal("csharp", parsed!.GetType().GetProperty("Language")!.GetValue(parsed));
        Assert.Equal("needle", parsed.GetType().GetProperty("Query")!.GetValue(parsed));
        Assert.Equal(true, parsed.GetType().GetProperty("Compact")!.GetValue(parsed));
        Assert.Equal(4096, parsed.GetType().GetProperty("MaxJsonBytes")!.GetValue(parsed));
        Assert.Equal(true, parsed.GetType().GetProperty("Json")!.GetValue(parsed));
        Assert.Null(parsed.GetType().GetProperty("Error")!.GetValue(parsed));

        var oversizedQuery = parse.Invoke(null, [new[] { $"--query={new string('q', QueryLimits.MaxQueryLength + 1)}" }]);
        Assert.NotNull(oversizedQuery);
        Assert.Equal(
            $"Error: --query must be at most {QueryLimits.MaxQueryLength} characters.",
            oversizedQuery!.GetType().GetProperty("Error")!.GetValue(oversizedQuery));

        var rejected = parse.Invoke(null, [new[] { "--json=true" }]);
        Assert.NotNull(rejected);
        Assert.Equal("Error: --json does not take a value.", rejected!.GetType().GetProperty("Error")!.GetValue(rejected));
    }

    [Fact]
    public void EveryNonHelpFlagInBashCompletion_IsBackedBySchemaEntry()
    {
        // Walks every per-subcommand bash branch and asserts each `--foo` token corresponds
        // to a schema flag that lists this subcommand in its `Commands` set. Anything that
        // surfaces in the completion script but isn't in the schema would be a zombie flag.
        // bash 補完の各 subcommand ブランチに現れるフラグが、schema 上もそのコマンドの
        // 補完対象として登録されていることを確認する（補完だけ生き残った ghost フラグの検出）。
        var script = ConsoleCompletionRenderer.GetCompletionScript("bash");
        foreach (var command in EnumeratedBashBranches)
        {
            var flags = ExtractBashSubcommandFlags(script, command);
            var allowed = CliFlagSchema.GetCompletionFlagsForCommand(command)
                .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var shortName in CliFlagSchema.GetCompletionFlagsForCommand(command).Select(f => f.ShortName).OfType<string>())
                allowed.Add(shortName);
            allowed.Add("--help");
            if (command == "find")
                allowed.Add("--");
            foreach (var token in flags)
                Assert.True(allowed.Contains(token),
                    $"bash completion for {command} surfaces {token}, but schema does not list it.");
        }
    }

    [Fact]
    public void EveryFlagInSchemaForEnumeratedBranch_AppearsInCompletionAndAuthoritativeCommandHelp_Issue4571()
    {
        // Inverse direction: every flag the schema declares for a per-command branch must
        // surface in that branch's bash completion list. Otherwise users can't tab-complete
        // a parser-accepted flag from the SSoT.
        // schema が宣言した補完対象フラグが bash 補完にも必ず出ること（逆方向）。
        var script = ConsoleCompletionRenderer.GetCompletionScript("bash");
        foreach (var command in EnumeratedBashBranches)
        {
            var flags = ExtractBashSubcommandFlags(script, command);
            var (printed, helpOutput, _) = ConsoleCapture.Capture(() =>
                ConsoleUi.PrintCommandUsage(command) ? 1 : 0);
            Assert.Equal(1, printed);
            var schemaFlags = CliFlagSchema.GetCompletionFlagsForCommand(command);
            foreach (var schemaFlag in schemaFlags)
            {
                Assert.Contains(schemaFlag.Name, CliFlagSchema.GetAcceptedFlagNamesForCommand(command));
                Assert.True(flags.Contains(schemaFlag.Name),
                    $"bash completion for {command} is missing schema flag {schemaFlag.Name}.");
                if (CliFlagSchema.HasAuthoritativeHelpOptions(command))
                    Assert.Contains(schemaFlag.Name, helpOutput, StringComparison.Ordinal);
            }

            if (!CliFlagSchema.HasAuthoritativeHelpOptions(command))
                Assert.DoesNotContain("\nOptions:\n", helpOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryFlagInFishCompletion_IsBackedBySchemaEntry()
    {
        // Fish emits one `complete` line per schema flag; verify by parsing the script
        // back. The set of (flag, subcommand) pairs in the script must equal the set of
        // (flag.Name, command) pairs implied by the schema's `Commands` field (AlsoAcceptedBy
        // is intentionally hidden from completions).
        // fish の `complete` 行から復元した (flag, command) ペアが、schema の Commands と一致すること。
        var script = ConsoleCompletionRenderer.GetCompletionScript("fish");
        var emitted = ExtractFishFlagCommandPairs(script);

        var expected = new HashSet<(string Flag, string Command)>();
        foreach (var flag in CliFlagSchema.All)
        {
            var name = flag.Name.TrimStart('-');
            foreach (var command in flag.PrimaryCommands)
            {
                if (flag.CompletionSubcommands.TryGetValue(command, out var nestedSubcommands))
                {
                    if (flag.ParentCompletionCommands.Contains(command))
                        expected.Add((name, command));
                    foreach (var nestedSubcommand in nestedSubcommands)
                        expected.Add((name, $"{command}:{nestedSubcommand}"));
                }
                else
                {
                    expected.Add((name, command));
                    if (flag.SubcommandValueDomains.TryGetValue(command, out var subcommandDomains))
                    {
                        foreach (var nestedSubcommand in subcommandDomains.Keys)
                            expected.Add((name, $"{command}:{nestedSubcommand}"));
                    }
                }
            }
        }

        foreach (var pair in expected)
            Assert.True(emitted.Contains(pair),
                $"fish completion is missing schema pair: --{pair.Flag} for {pair.Command}.");

        foreach (var pair in emitted)
        {
            // Ignore non-schema sentinel lines: `help`, `version`, `license`, command-name `-a` lines.
            // ヘルプ系の非スキーマ行は無視する。
            if (pair.Flag is "help" or "version" or "license")
                continue;
            Assert.True(expected.Contains(pair),
                $"fish completion surfaces non-schema pair: --{pair.Flag} for {pair.Command}.");
        }
    }

    [Fact]
    public void CanonicalValueRegistry_DrivesHelpValidationAndCompletions_Issue4861()
    {
        var auditFormats = CliFlagSchema.GetCanonicalValuesForCommand("audit", "--format");
        Assert.Contains("sarif", auditFormats);
        Assert.DoesNotContain("lsp", auditFormats);
        foreach (var command in CliFlagSchema.AllCommands)
        {
            foreach (var format in CliFlagSchema.GetCanonicalValuesForCommand(command, "--format"))
                Assert.True(CliOutputFormatCapabilities.TryGet(format, out _), $"{command} registers unknown format {format}.");
        }

        var origins = CliFlagSchema.GetCanonicalValuesForCommand("search", "--origin");
        Assert.Equal(
            ["code", "comment", "string_literal", "regex_literal", "help_text", "schema_description", "unknown"],
            origins);
        var resultKinds = CliFlagSchema.GetCanonicalValuesForCommand("search", "--result-kind");
        Assert.Equal(
            ["call_site", "declaration", "identifier", "code", "comment", "string_literal", "regex_literal", "help_text", "schema_description", "unknown"],
            resultKinds);
        Assert.Equal(["eof"], CliFlagSchema.GetCanonicalValuesForCommand("excerpt", "--end"));
        Assert.Equal("<line|eof>", CliFlagSchema.GetValuePlaceholderForCommand("excerpt", "--end"));
        Assert.True(CliFlagSchema.TryNormalizeOptionValue("search", "--origin", "schema", out var normalizedOrigin));
        Assert.Equal("schema_description", normalizedOrigin);
        Assert.True(CliFlagSchema.TryNormalizeOptionValue("search", "--origin", "schema-description", out normalizedOrigin));
        Assert.Equal("schema_description", normalizedOrigin);
        Assert.True(CliFlagSchema.TryNormalizeOptionValue("search", "--result-kind", "callsite", out var normalizedKind));
        Assert.Equal("call_site", normalizedKind);
        Assert.False(CliFlagSchema.TryNormalizeOptionValue("deps", "--format", "json_graph", out _));
        Assert.False(CliFlagSchema.TryNormalizeOptionValue("search", "--format", "issue_drafts", out _));

        Assert.Contains("--format <text|json|count|compact|sarif|issue-drafts>", ConsoleUi.GetUsageLine("audit"));
        var searchUsage = ConsoleUi.GetUsageLine("search");
        Assert.NotNull(searchUsage);
        Assert.Contains("--origin <code|comment|string_literal|regex_literal|help_text|schema_description|unknown>", searchUsage);
        Assert.Contains("--result-kind <call_site|declaration|identifier|code|comment|string_literal|regex_literal|help_text|schema_description|unknown>", searchUsage);

        var acceptsFormat = typeof(ProgramRunner).GetMethod(
            "CommandAcceptsOutputFormat",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(acceptsFormat);
        Assert.Equal(true, acceptsFormat!.Invoke(null, ["audit", "sarif"]));
        Assert.Equal(false, acceptsFormat.Invoke(null, ["audit", "lsp"]));
        Assert.Equal(false, acceptsFormat.Invoke(null, ["deps", "json_graph"]));
        Assert.Equal(false, acceptsFormat.Invoke(null, ["search", "issue_drafts"]));

        var bash = ConsoleCompletionRenderer.GetCompletionScript("bash");
        Assert.Contains("audit) COMPREPLY=($(compgen -W \"text json count compact sarif issue-drafts\"", bash);
        Assert.Contains("--origin) COMPREPLY=($(compgen -W \"code comment string_literal regex_literal help_text schema_description unknown\"", bash);
        Assert.Contains("--end) COMPREPLY=($(compgen -W \"eof\"", bash);
        Assert.DoesNotContain("--end) COMPREPLY=($(compgen -W \"line eof\"", bash);

        var fish = ConsoleCompletionRenderer.GetCompletionScript("fish");
        Assert.Contains("__fish_cdidx_using_command audit' -l format -r -a 'text json count compact sarif issue-drafts'", fish);
        Assert.Contains("__fish_cdidx_using_command search' -l result-kind -r -a 'call_site declaration identifier code comment string_literal regex_literal help_text schema_description unknown'", fish);
        Assert.Contains("__fish_cdidx_using_command excerpt' -l end -r -a 'eof'", fish);

        var zsh = ConsoleCompletionRenderer.GetCompletionScript("zsh");
        Assert.Contains("--end[Excerpt end line; eof reads through the indexed end of file]:value:(eof)", zsh);

        var powershell = ConsoleCompletionRenderer.GetCompletionScript("powershell");
        Assert.Contains("'--end' = @('eof')", powershell);
    }

    [Fact]
    public void NestedValueDomainsAndValidateConfigJsonMatchAcceptedCliContracts_Issue5163()
    {
        Assert.Equal(
            SuggestionsCommandRunner.StatusFilterValues,
            CliFlagSchema.GetCanonicalValuesForCommand("suggestions", "--status"));
        Assert.Equal(
            SuggestionsCommandRunner.ManualStatusTransitionValues,
            CliFlagSchema.GetCanonicalValuesForCommand("suggestions", "--status", "update"));

        foreach (var status in CliFlagSchema.GetCanonicalValuesForCommand("suggestions", "--status", "update"))
            Assert.True(SuggestionsCommandRunner.TryParseLifecycleStatus(status, out _), $"Completion advertised rejected update status: {status}");
        foreach (var rejected in new[] { "all", "submitted_pending_triage", "submitted", "unsubmitted" })
            Assert.False(SuggestionsCommandRunner.TryParseLifecycleStatus(rejected, out _), $"Update parser unexpectedly accepted {rejected}");

        var validateConfigFlags = CliFlagSchema.GetCompletionFlagsForCommand("validate-config")
            .Select(flag => flag.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("--json", validateConfigFlags);
        Assert.DoesNotContain("--pretty", validateConfigFlags);
        Assert.Contains("--json", ConsoleUi.GetUsageLine("validate-config"), StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionValueKinds_KeepMetavariablesOutOfFiniteChoicesAcrossShells_Issue4902()
    {
        Assert.Empty(CliFlagSchema.GetCanonicalValuesForCommand("search", "--project"));
        Assert.Empty(CliFlagSchema.GetCanonicalValuesForCommand("search", "--recipe"));
        Assert.Empty(CliFlagSchema.GetCanonicalValuesForCommand("search", "--open-issues"));
        Assert.Equal(CliOptionValueKind.Project, CliFlagSchema.GetValueKindForCommand("search", "--project"));
        Assert.Equal(CliOptionValueKind.DirectoryPath, CliFlagSchema.GetValueKindForCommand("hooks", "--project"));
        Assert.Equal(CliOptionValueKind.FreeText, CliFlagSchema.GetValueKindForCommand("search", "--recipe"));
        Assert.Equal(CliOptionValueKind.FilePath, CliFlagSchema.GetValueKindForCommand("search", "--open-issues"));
        Assert.Equal(["github"], CliFlagSchema.GetFlag("search", "--open-issues")!.SupplementalCompletionValues);
        Assert.Equal(CliOptionValueKind.Repository, CliFlagSchema.GetValueKindForCommand("search", "--repo"));
        Assert.Equal(CliOptionValueKind.Language, CliFlagSchema.GetValueKindForCommand("languages", "--language"));
        Assert.Equal(CliOptionValueKind.SymbolKind, CliFlagSchema.GetValueKindForCommand("symbols", "--kind"));
        Assert.Equal(CliOptionValueKind.FilePath, CliFlagSchema.GetValueKindForCommand("suggestions", "--output"));
        Assert.Equal(CliOptionValueKind.Finite, CliFlagSchema.GetValueKindForCommand("search", "--issue-state"));
        Assert.Equal(["open", "closed", "all"], CliFlagSchema.GetCanonicalValuesForCommand("search", "--issue-state"));
        Assert.False(CliFlagSchema.GetFlag("search", "--json")!.IsValueBearing);

        var displayOnlyAlternatives = CliFlagSchema.All
            .Where(flag =>
                flag.ValuePlaceholder?.Contains('|', StringComparison.Ordinal) == true
                && flag.ValueDomain is null
                && flag.CommandValueDomains.Count == 0)
            .Select(flag => flag.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["--open-issues", "--project", "--recipe"], displayOnlyAlternatives);

        var bash = ConsoleCompletionRenderer.GetCompletionScript("bash");
        Assert.Contains("--workspace-db|--data-dir|--metrics|--path|--project|--solution|--exclude-path", bash, StringComparison.Ordinal);
        Assert.Contains("--open-issues) COMPREPLY=($(compgen -W \"github\" -- \"$cur\") $(compgen -f -- \"$cur\"))", bash, StringComparison.Ordinal);
        Assert.Contains("--evidence-path|--files|--output|-o", bash, StringComparison.Ordinal);
        Assert.Contains("--lang|--language) COMPREPLY=($(compgen -W", bash, StringComparison.Ordinal);
        Assert.Contains("--issue-state) COMPREPLY=($(compgen -W \"open closed all\"", bash, StringComparison.Ordinal);
        Assert.DoesNotContain("--project) COMPREPLY=($(compgen -W \"name path\"", bash, StringComparison.Ordinal);
        Assert.DoesNotContain("--recipe) COMPREPLY=($(compgen -W \"name name/query\"", bash, StringComparison.Ordinal);
        Assert.DoesNotContain("path github github:owner/name", bash, StringComparison.Ordinal);

        var zsh = ConsoleCompletionRenderer.GetCompletionScript("zsh");
        Assert.Contains("--project[Filter to a .sln/.csproj project]:file:_files", zsh, StringComparison.Ordinal);
        Assert.Contains("--open-issues[Preflight issue drafts against issue JSON or GitHub issues]:value:_alternative \"files:file:_files\" \"values:value:(github)\"", zsh, StringComparison.Ordinal);
        Assert.Contains("--repo[Issue-drafts: GitHub repository for --open-issues github]:repository", zsh, StringComparison.Ordinal);
        Assert.Contains("--recipe[Search: run a built-in audit recipe query set, optionally selecting one child query]:value", zsh, StringComparison.Ordinal);
        Assert.Contains("--language[Suggestions: filter by language; languages: look up one language by canonical name or recognized language spelling]:language:(", zsh, StringComparison.Ordinal);
        Assert.DoesNotContain(":value:(name name/query)", zsh, StringComparison.Ordinal);
        Assert.DoesNotContain(":value:(path github github:owner/name)", zsh, StringComparison.Ordinal);

        var fish = ConsoleCompletionRenderer.GetCompletionScript("fish");
        Assert.Contains("-l project -r -d 'Filter to a .sln/.csproj project'", fish, StringComparison.Ordinal);
        Assert.Contains("-l open-issues -r -a 'github' -d 'Preflight issue drafts against issue JSON or GitHub issues'", fish, StringComparison.Ordinal);
        Assert.Contains("-l language -r -a '", fish, StringComparison.Ordinal);
        Assert.Contains("-l issue-state -r -a 'open closed all'", fish, StringComparison.Ordinal);
        Assert.Contains("__fish_cdidx_using_command search' -l group-by -r -a 'file symbol origin return-type subsystem'", fish, StringComparison.Ordinal);
        Assert.Contains("__fish_cdidx_using_command audit' -l group-by -r -a 'file symbol origin return-type subsystem'", fish, StringComparison.Ordinal);
        Assert.Contains("__fish_cdidx_using_command hotspots' -l group-by -r -a 'symbol file statement'", fish, StringComparison.Ordinal);
        Assert.DoesNotContain("-l project -r -a 'name path'", fish, StringComparison.Ordinal);
        Assert.DoesNotContain("-l recipe -r -a 'name name/query'", fish, StringComparison.Ordinal);
        Assert.DoesNotContain("-a 'path github github:owner/name'", fish, StringComparison.Ordinal);

        var powershell = ConsoleCompletionRenderer.GetCompletionScript("powershell");
        Assert.Contains("'--workspace-db', '--data-dir', '--metrics', '--path', '--project', '--solution', '--exclude-path', '--evidence-path', '--files', '--output', '-o'", powershell, StringComparison.Ordinal);
        Assert.Contains("{ $_ -in @('--open-issues') }", powershell, StringComparison.Ordinal);
        Assert.Contains("@('github') | Where-Object", powershell, StringComparison.Ordinal);
        Assert.Contains("{ $_ -in @('--lang', '--language') } { $langs", powershell, StringComparison.Ordinal);
        Assert.Contains("'--issue-state' = @('open', 'closed', 'all')", powershell, StringComparison.Ordinal);
        Assert.DoesNotContain("'--project' = @('name', 'path')", powershell, StringComparison.Ordinal);
        Assert.DoesNotContain("'--recipe' = @('name', 'name/query')", powershell, StringComparison.Ordinal);
        Assert.DoesNotContain("'--open-issues' = @('path', 'github', 'github:owner/name')", powershell, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrySurfacesSafetyAndAcceptedOptionsWithoutAdvertisingRejectedGotoExact_Issue4861()
    {
        var hookFlags = CliFlagSchema.GetAcceptedFlagNamesForCommand("hooks");
        Assert.Contains("--dry-run", hookFlags);
        Assert.DoesNotContain(CliFlagSchema.GetCompletionFlagsForCommand("hooks"), flag => flag.Name == "--dry-run");
        Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("hooks", "install"), flag => flag.Name == "--dry-run");
        Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("hooks", "uninstall"), flag => flag.Name == "--dry-run");
        Assert.DoesNotContain(CliFlagSchema.GetCompletionFlagsForCommand("hooks", "status"), flag => flag.Name == "--dry-run");
        Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("hooks", "install"), flag => flag.Name == "--force");
        Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("hooks", "uninstall"), flag => flag.Name == "--force");
        Assert.DoesNotContain(CliFlagSchema.GetCompletionFlagsForCommand("hooks", "status"), flag => flag.Name == "--force");

        var gotoFlags = CliFlagSchema.GetAcceptedFlagNamesForCommand("goto");
        Assert.DoesNotContain("--exact", gotoFlags);
        Assert.Contains("--exact-name", gotoFlags);
        Assert.DoesNotContain("--exact|--exact-name", ConsoleUi.GetUsageLine("goto"), StringComparison.Ordinal);

        var (_, hookHelp, _) = ConsoleCapture.Capture(() =>
            ConsoleUi.PrintCommandUsage("hooks") ? 1 : 0);
        Assert.Contains("--dry-run", hookHelp, StringComparison.Ordinal);
        Assert.Contains("--project <path>", hookHelp, StringComparison.Ordinal);
        Assert.Contains("Repository/worktree directory used to resolve Git metadata", hookHelp, StringComparison.Ordinal);
        Assert.Contains("Install: replace an existing chained-hook backup; uninstall: remove an unmanaged pre-commit hook", hookHelp, StringComparison.Ordinal);
        Assert.DoesNotContain("--pretty", hookHelp, StringComparison.Ordinal);

        var (_, indexHelp, _) = ConsoleCapture.Capture(() =>
            ConsoleUi.PrintCommandUsage("index") ? 1 : 0);
        Assert.Contains("Bypass the per-database index lock; only use when no other cdidx index is active", indexHelp, StringComparison.Ordinal);

        var bash = ConsoleCompletionRenderer.GetCompletionScript("bash");
        Assert.Contains("[ \"$cmd\" = \"hooks\" ] && [ \"$nested\" = \"install\" ]", bash, StringComparison.Ordinal);
        Assert.Contains("[ \"$cmd\" = \"hooks\" ] && [ \"$nested\" = \"uninstall\" ]", bash, StringComparison.Ordinal);
        Assert.Contains("for ((i=cmd_index+1; i<COMP_CWORD; i++)); do", bash, StringComparison.Ordinal);
        Assert.Contains("--project) skip_next=1", bash, StringComparison.Ordinal);
        Assert.DoesNotContain("nested=\"${COMP_WORDS[2]}\"", bash, StringComparison.Ordinal);
        Assert.Contains("--project|--solution|--exclude-path", bash, StringComparison.Ordinal);
        Assert.DoesNotContain("--project) COMPREPLY=($(compgen -W \"name path\"", bash, StringComparison.Ordinal);

        var zsh = ConsoleCompletionRenderer.GetCompletionScript("zsh");
        Assert.Contains("$subcmd == hooks && $nested == install", zsh, StringComparison.Ordinal);
        Assert.Contains("$subcmd == hooks && $nested == uninstall", zsh, StringComparison.Ordinal);
        Assert.Contains("for (( i = cmd_index + 1; i < CURRENT; i++ )); do", zsh, StringComparison.Ordinal);
        Assert.Contains("(--project) skip_next=1", zsh, StringComparison.Ordinal);
        Assert.DoesNotContain("$words[3] == install", zsh, StringComparison.Ordinal);
        Assert.Contains("--project[Repository/worktree directory used to resolve Git metadata for the managed hook]:file:_files", zsh, StringComparison.Ordinal);

        var fish = ConsoleCompletionRenderer.GetCompletionScript("fish");
        Assert.Contains("__fish_cdidx_using_context hooks install' -l dry-run", fish, StringComparison.Ordinal);
        Assert.Contains("__fish_cdidx_using_context hooks install' -l force", fish, StringComparison.Ordinal);
        Assert.Contains("__fish_cdidx_using_context hooks uninstall' -l force", fish, StringComparison.Ordinal);
        Assert.DoesNotContain("__fish_cdidx_using_command hooks' -l force", fish, StringComparison.Ordinal);
        Assert.Contains("__fish_cdidx_using_command hooks' -l project -r -d 'Repository/worktree directory used to resolve Git metadata", fish, StringComparison.Ordinal);
        Assert.DoesNotContain("__fish_cdidx_using_command hooks' -l project -r -a 'name path'", fish, StringComparison.Ordinal);

        var powershell = ConsoleCompletionRenderer.GetCompletionScript("powershell");
        Assert.Contains("$subcmd -eq 'hooks' -and $nested -eq 'install'", powershell, StringComparison.Ordinal);
        Assert.Contains("$subcmd -eq 'hooks' -and $nested -eq 'uninstall'", powershell, StringComparison.Ordinal);
        Assert.Contains("if ($skipNestedValue) { $skipNestedValue = $false; continue }", powershell, StringComparison.Ordinal);
        Assert.Contains("if ($subcommands[$subcmd] -contains $token) { $nested = $token; break }", powershell, StringComparison.Ordinal);
        Assert.Contains("'--project'", powershell, StringComparison.Ordinal);
        Assert.DoesNotContain("'--project' = @('name', 'path')", powershell, StringComparison.Ordinal);

        using var capture = ConsoleCapture.Start(captureOut: true);
        ConsoleUi.PrintFlagUsage(showBanner: false);
        var flagHelp = capture.Out!.ToString();
        Assert.Contains("Safety and scope options:", flagHelp, StringComparison.Ordinal);
        foreach (var flag in new[]
                 {
                     "--read-only", "--immutable", "--data-dir", "--trace",
                     "--strict-not-found", "--project", "--solution",
                 })
        {
            Assert.Contains(flag, flagHelp, StringComparison.Ordinal);
        }
    }

    // Mirrors the EnumeratedCompletionCommands list inside ConsoleCompletionRenderer - the only commands
    // that get their own bash/zsh branch (everything else falls into the generic else branch).
    // ConsoleCompletionRenderer 側の EnumeratedCompletionCommands に対応する一覧。
    private static readonly string[] EnumeratedBashBranches =
    [
        "find", "excerpt", "references", "inspect", "hotspots", "status", "validate-config", "db", "report", "suggestions", "search",
    ];

    private static HashSet<string> GetProgramRunnerStringSet(string fieldName)
    {
        var field = typeof(ProgramRunner).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = (HashSet<string>?)field!.GetValue(null);
        Assert.NotNull(value);
        return value!;
    }

    private static void AssertOptionSet(
        IEnumerable<string> expected,
        IReadOnlySet<string> actual,
        string label)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        Assert.True(
            expectedSet.SetEquals(actual),
            $"{label} drifted. Missing: {string.Join(", ", expectedSet.Except(actual).OrderBy(value => value, StringComparer.Ordinal))}. "
            + $"Unexpected: {string.Join(", ", actual.Except(expectedSet).OrderBy(value => value, StringComparer.Ordinal))}.");
    }

    private static HashSet<string> ExtractContextLongFlags(
        string script,
        string shell,
        string command,
        string? nested)
    {
        var flags = shell switch
        {
            "bash" => ExtractBashContextLongFlags(script, command, nested),
            "zsh" => ExtractZshContextLongFlags(script, command, nested),
            "fish" => ExtractFishContextLongFlags(script, command, nested),
            "powershell" => ExtractPowerShellContextLongFlags(script, command, nested),
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unknown shell"),
        };
        flags.Remove("--help");
        return flags;
    }

    private static HashSet<string> ExtractBashContextLongFlags(
        string script,
        string command,
        string? nested)
    {
        var condition = nested is null
            ? @"\[\s*""\$cmd""\s*=\s*""" + Regex.Escape(command) + @"""\s*\]"
            : @"\[\s*""\$cmd""\s*=\s*""" + Regex.Escape(command) + @"""\s*\]\s*&&\s*\[\s*""\$nested""\s*=\s*""" + Regex.Escape(nested) + @"""\s*\]";
        var match = Regex.Match(
            script,
            @"(?:if|elif)\s*" + condition + @"\s*;\s*then\s*\n\s*COMPREPLY=\(\$\(compgen\s+-W\s+""(?<flags>[^""]*)""");
        if (!match.Success && nested is not null)
            return ExtractBashContextLongFlags(script, command, nested: null);
        Assert.True(match.Success, $"bash completion branch not found for {command} {nested}");
        return Regex.Matches(match.Groups["flags"].Value, @"--[a-z][a-z0-9-]*")
            .Select(flag => flag.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractZshContextLongFlags(
        string script,
        string command,
        string? nested)
    {
        var condition = @"\$subcmd\s*==\s*" + Regex.Escape(command);
        if (nested is not null)
            condition += @"\s*&&\s*\$nested\s*==\s*" + Regex.Escape(nested);
        var match = Regex.Match(
            script,
            @"(?:if|elif)\s+\[\[\s*" + condition + @"\s*\]\];\s*then(?<branch>.*?)(?=\n\s*(?:elif|else|fi)\b)",
            RegexOptions.Singleline);
        if (!match.Success && nested is not null)
            return ExtractZshContextLongFlags(script, command, nested: null);
        Assert.True(match.Success, $"zsh completion branch not found for {command} {nested}");
        return Regex.Matches(match.Groups["branch"].Value, @"'(?<flag>--[a-z][a-z0-9-]*)\[")
            .Select(flag => flag.Groups["flag"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ExtractFishContextLongFlags(
        string script,
        string command,
        string? nested)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var exactContext = nested is null
            ? $"__fish_cdidx_using_context {command}'"
            : $"__fish_cdidx_using_context {command} {nested}'";
        var commandPattern = new Regex(@"__fish_cdidx_using_command\s+(?<list>[^;']+)");
        foreach (var line in script.Split('\n'))
        {
            var flag = Regex.Match(line, @"\s-l\s+(?<name>[a-z][a-z0-9-]*)\b");
            if (!flag.Success)
                continue;
            if (line.Contains(exactContext, StringComparison.Ordinal))
            {
                result.Add("--" + flag.Groups["name"].Value);
                continue;
            }

            var commandMatch = commandPattern.Match(line);
            if (!commandMatch.Success)
                continue;
            var commands = commandMatch.Groups["list"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (Array.IndexOf(commands, command) >= 0)
                result.Add("--" + flag.Groups["name"].Value);
        }
        return result;
    }

    private static HashSet<string> ExtractPowerShellContextLongFlags(
        string script,
        string command,
        string? nested)
    {
        var pattern = nested is null
            ? @"'" + Regex.Escape(command) + @"'\s*\{\s*\$flags\s*=\s*@\((?<flags>[^)]*)\)\s*\}"
            : @"(?:if|}\s*elseif)\s*\(\$subcmd\s*-eq\s*'" + Regex.Escape(command) + @"'\s*-and\s*\$nested\s*-eq\s*'" + Regex.Escape(nested) + @"'\)\s*\{\s*\$flags\s*=\s*@\((?<flags>[^)]*)\)";
        var match = Regex.Match(script, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success && nested is not null)
            return ExtractPowerShellContextLongFlags(script, command, nested: null);
        Assert.True(match.Success, $"PowerShell completion branch not found for {command} {nested}");
        return Regex.Matches(match.Groups["flags"].Value, @"--[a-z][a-z0-9-]*")
            .Select(flag => flag.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static SortedSet<string> ExtractBashSubcommandFlags(string script, string subcommand)
    {
        // Each per-command branch looks like:
        //   if|elif [ "$cmd" = "<subcommand>" ]; then
        //       COMPREPLY=($(compgen -W "--foo --bar ..." -- "$cur"))
        // We capture the compgen list belonging to this subcommand without depending on the
        // ordering of the next branch (so the test does not break when we reorder branches).
        // 次ブランチ順序に依存せずに、対象 subcommand の compgen リストだけを取り出す。
        var pattern = new Regex(
            @"(?:if|elif)\s*\[\s*""\$cmd""\s*=\s*""" + Regex.Escape(subcommand) + @"""\s*\]\s*;\s*then\s*\n\s*COMPREPLY=\(\$\(compgen\s+-W\s+""(?<flags>[^""]*)""");
        var match = pattern.Match(script);
        Assert.True(match.Success, $"bash branch not found for {subcommand}");
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var token in match.Groups["flags"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            flags.Add(token);
        return flags;
    }

    private static HashSet<(string Flag, string Command)> ExtractFishFlagCommandPairs(string script)
    {
        var result = new HashSet<(string, string)>();
        var commandPattern = new Regex(@"__fish_cdidx_using_command\s+(?<list>[^;']+)[^']*'(?<rest>.+?)-l\s+(?<flag>[a-z][a-z0-9-]*)\b");
        var contextPattern = new Regex(@"__fish_cdidx_using_context\s+(?<command>[^\s']+)(?:\s+(?<nested>[^\s']+))?'(?<rest>.+?)-l\s+(?<flag>[a-z][a-z0-9-]*)\b");
        foreach (var line in script.Split('\n'))
        {
            var contextMatch = contextPattern.Match(line);
            if (contextMatch.Success)
            {
                var command = contextMatch.Groups["command"].Value;
                var nested = contextMatch.Groups["nested"].Value;
                result.Add((
                    contextMatch.Groups["flag"].Value,
                    nested.Length == 0 ? command : $"{command}:{nested}"));
            }

            var commandMatch = commandPattern.Match(line);
            if (!commandMatch.Success)
                continue;
            var flag = commandMatch.Groups["flag"].Value;
            foreach (var command in commandMatch.Groups["list"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result.Add((flag, command));
        }
        return result;
    }
}
