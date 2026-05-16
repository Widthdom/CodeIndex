using System.Reflection;
using System.Text.RegularExpressions;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Guards the single-source-of-truth contract introduced by #1570: <see cref="CliFlagSchema"/>
/// drives both the per-command parser allowlists (`TryWriteUnsupportedOptionError` /
/// `ValidateFindArgs`) and the bash / zsh / fish completion generators in <see cref="ConsoleUi"/>.
/// These tests fail fast when the schema and the generated completion scripts drift apart,
/// or when a flag's <c>Commands</c> / <c>AlsoAcceptedBy</c> sets reference unknown subcommands.
/// #1570 で導入した「フラグ単一情報源」の契約を守るためのテスト群。スキーマと
/// 補完スクリプト、コマンド一覧、parser-vs-completion の許容差分がずれた瞬間に失敗する。
/// </summary>
public class CliFlagSchemaTests
{
    [Fact]
    public void AllCommands_MatchesConsoleUiCommandsList()
    {
        var consoleUiCommands = GetConsoleUiCommands();
        Assert.Equal(consoleUiCommands, CliFlagSchema.AllCommands);
    }

    [Fact]
    public void EveryFlagCommandsSet_OnlyReferencesKnownSubcommands()
    {
        var known = CliFlagSchema.AllCommands.ToHashSet(StringComparer.Ordinal);
        foreach (var flag in CliFlagSchema.All)
        {
            foreach (var command in flag.Commands)
                Assert.True(known.Contains(command), $"{flag.Name} Commands references unknown subcommand '{command}'");
            foreach (var command in flag.AlsoAcceptedBy)
                Assert.True(known.Contains(command), $"{flag.Name} AlsoAcceptedBy references unknown subcommand '{command}'");
        }
    }

    [Fact]
    public void FlagPrimaryAndAlsoAcceptedSets_DoNotOverlap()
    {
        foreach (var flag in CliFlagSchema.All)
        {
            foreach (var command in flag.AlsoAcceptedBy)
                Assert.False(flag.Commands.Contains(command),
                    $"{flag.Name}: '{command}' appears in both Commands and AlsoAcceptedBy");
        }
    }

    [Fact]
    public void EveryFlag_AppliesToAtLeastOneCommand()
    {
        foreach (var flag in CliFlagSchema.All)
            Assert.NotEmpty(flag.Commands);
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
    public void EveryNonHelpFlagInBashCompletion_IsBackedBySchemaEntry()
    {
        // Walks every per-subcommand bash branch and asserts each `--foo` token corresponds
        // to a schema flag that lists this subcommand in its `Commands` set. Anything that
        // surfaces in the completion script but isn't in the schema would be a zombie flag.
        // bash 補完の各 subcommand ブランチに現れるフラグが、schema 上もそのコマンドの
        // 補完対象として登録されていることを確認する（補完だけ生き残った ghost フラグの検出）。
        var script = ConsoleUi.GetCompletionScript("bash");
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
    public void EveryFlagInSchemaForEnumeratedBranch_AppearsInBashCompletionForThatBranch()
    {
        // Inverse direction: every flag the schema declares for a per-command branch must
        // surface in that branch's bash completion list. Otherwise users can't tab-complete
        // a parser-accepted flag from the SSoT.
        // schema が宣言した補完対象フラグが bash 補完にも必ず出ること（逆方向）。
        var script = ConsoleUi.GetCompletionScript("bash");
        foreach (var command in EnumeratedBashBranches)
        {
            var flags = ExtractBashSubcommandFlags(script, command);
            foreach (var schemaFlag in CliFlagSchema.GetCompletionFlagsForCommand(command))
                Assert.True(flags.Contains(schemaFlag.Name),
                    $"bash completion for {command} is missing schema flag {schemaFlag.Name}.");
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
        var script = ConsoleUi.GetCompletionScript("fish");
        var emitted = ExtractFishFlagCommandPairs(script);

        var expected = new HashSet<(string Flag, string Command)>();
        foreach (var flag in CliFlagSchema.All)
        {
            var name = flag.Name.TrimStart('-');
            foreach (var command in flag.Commands)
                expected.Add((name, command));
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

    // Mirrors the EnumeratedCompletionCommands list inside ConsoleUi — the only commands
    // that get their own bash/zsh branch (everything else falls into the generic else branch).
    // ConsoleUi 側の EnumeratedCompletionCommands に対応する一覧。
    private static readonly string[] EnumeratedBashBranches =
    [
        "find", "excerpt", "references", "inspect", "hotspots", "status", "db", "report", "search",
    ];

    private static IReadOnlyList<string> GetConsoleUiCommands()
    {
        var field = typeof(ConsoleUi).GetField("Commands", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = (string[]?)field!.GetValue(null);
        Assert.NotNull(value);
        return value!;
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
        var pattern = new Regex(@"__fish_seen_subcommand_from\s+(?<list>[^']+)'(?<rest>.+?)-l\s+(?<flag>[a-z][a-z0-9-]*)\b");
        foreach (var line in script.Split('\n'))
        {
            var match = pattern.Match(line);
            if (!match.Success)
                continue;
            var flag = match.Groups["flag"].Value;
            foreach (var subcmd in match.Groups["list"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result.Add((flag, subcmd));
        }
        return result;
    }
}
