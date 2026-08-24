using CodeIndex.Indexer;
using System.Text;

namespace CodeIndex.Cli;

internal static class ConsoleCompletionRenderer
{
    private static readonly string[] ShellCommandNames = [.. CliCommandMetadata.PublicCommandNames];

    internal static string GetCompletionScript(string shell) =>
        shell.ToLowerInvariant() switch
        {
            "bash" => GetBashCompletions(),
            "zsh" => GetZshCompletions(),
            "fish" => GetFishCompletions(),
            "powershell" or "pwsh" => GetPowerShellCompletions(),
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unknown shell"),
        };

    /// <summary>
    /// Get sorted unique language names from FileIndexer for completion values.
    /// 補完値用にFileIndexerからソート済みのユニークな言語名を取得する。
    /// </summary>
    internal static string GetCompletionLangs() =>
        string.Join(" ", FileIndexer.GetDetectedLanguageNames()
            .Append("bat")
            .Append("cmd")
            .Concat(QueryCommandRunner.GetCompletionLanguageAliases().Where(IsShellCompletionSafeLanguageValue))
            .Distinct()
            .OrderBy(l => l));

    private static bool IsShellCompletionSafeLanguageValue(string value)
        => !value.Any(char.IsWhiteSpace);

    private static string GetCompletionKinds() =>
        string.Join(" ", new[]
        {
            "accessor", "annotation", "associatedtype", "attribute", "augmentation",
            "call", "class", "class_hook", "consumes_hook", "constant", "constructor",
            "delegate", "enum", "event", "field", "friend", "function", "heading",
            "hook", "impl", "import", "instantiate", "interface", "label", "lambda",
            "method", "module", "namespace", "object", "operator", "procedure",
            "property", "razor_event_binding", "record", "reference", "specialization",
            "struct", "subscribe", "test.method", "trait", "type", "type_reference",
            "type_parameter", "typealias", "union", "unsubscribe", "variable",
        }.OrderBy(k => k, StringComparer.Ordinal));

    // Commands that get their own per-command completion branch (bash/zsh). Order matters: the
    // `else` generic branch is the catch-all, and `search` must remain the last `elif` so the
    // tests `CompletionRenderer_BashAndZshScopeMaxLineWidthToSearchBranch` can isolate it.
    // bash / zsh の専用ブランチを持つコマンド。順序は意図的で、`search` が最終 elif、`else` が
    // generic catch-all となるよう揃える。テストもこの並びを前提にしている。
    private static readonly string[] EnumeratedCompletionCommands =
    [
        "find", "excerpt", "references", "inspect", "hotspots", "status", "validate-config", "db", "report", "suggestions",
        .. CliCommandMetadata.PublicCommandNames.Except(["find", "excerpt", "references", "inspect", "hotspots", "status", "validate-config", "db", "report", "suggestions", "search"]),
        "search",
    ];

    // Generic-branch representative set: union of completion flags from these commands populates
    // the bash/zsh `else` branch. Excludes find/excerpt/etc. which have their own branches, and
    // intentionally omits commands whose flags would surface in their own branches.
    // generic ブランチを構成する代表コマンド集合。専用ブランチを持つコマンドは除外。
    private static readonly string[] GenericBranchRepresentativeCommands =
    [
        "definition", "callers", "callees", "symbols", "files", "map", "impact", "deps", "unused",
    ];

    private static string GetBashCompletions()
    {
        var cmds = string.Join(" ", ShellCommandNames);
        var topLevelFlags = string.Join(" ", BuildTopLevelFlagList());
        var langs = GetCompletionLangs();
        var kinds = GetCompletionKinds();
        var version = ConsoleUi.LoadVersion();
        var sb = new StringBuilder();
        sb.Append($"# cdidx bash completions generated for version {version}\n");
        sb.Append("# Regenerate this script after upgrading cdidx.\n");
        sb.Append("_cdidx() {\n");
        sb.Append("    local cur prev commands\n");
        sb.Append("    local cmd nested i skip_next cmd_index\n");
        sb.Append("    cur=\"${COMP_WORDS[COMP_CWORD]}\"\n");
        sb.Append("    prev=\"${COMP_WORDS[COMP_CWORD-1]}\"\n");
        sb.Append("    cmd=\"\"\n");
        sb.Append("    cmd_index=0\n");
        sb.Append("    skip_next=0\n");
        sb.Append("    for ((i=1; i<COMP_CWORD; i++)); do\n");
        sb.Append("        if [ $skip_next -eq 1 ]; then skip_next=0; continue; fi\n");
        sb.Append("        case \"${COMP_WORDS[i]}\" in\n");
        sb.Append($"            {string.Join('|', GetTopLevelValueTakingFlagNames())}) skip_next=1 ;;\n");
        sb.Append($"            {string.Join('|', GetTopLevelValueTakingFlagNames().Select(name => $"{name}=*"))}) ;;\n");
        sb.Append($"            {string.Join('|', GetTopLevelFlagOnlyNames())}) ;;\n");
        sb.Append("            -*) ;;\n");
        sb.Append("            *) cmd=\"${COMP_WORDS[i]}\"; cmd_index=$i; break ;;\n");
        sb.Append("        esac\n");
        sb.Append("    done\n");
        sb.Append("    nested=\"\"\n");
        sb.Append("    case \"$cmd\" in\n");
        foreach (var (command, _) in CliCommandMetadata.CommandSubcommands)
        {
            sb.Append($"        {command})\n");
            sb.Append("            skip_next=0\n");
            sb.Append("            for ((i=cmd_index+1; i<COMP_CWORD; i++)); do\n");
            sb.Append("                if [ $skip_next -eq 1 ]; then skip_next=0; continue; fi\n");
            sb.Append("                case \"${COMP_WORDS[i]}\" in\n");
            foreach (var flagName in GetValueTakingFlagNamesForNestedCommand(command))
            {
                sb.Append($"                    {flagName}) skip_next=1 ;;\n");
                sb.Append($"                    {flagName}=*) ;;\n");
            }
            sb.Append($"                    {string.Join('|', GetNestedSubcommandNames(command))}) nested=\"${{COMP_WORDS[i]}}\"; break ;;\n");
            sb.Append("                esac\n");
            sb.Append("            done\n");
            sb.Append("            ;;\n");
        }
        sb.Append("    esac\n");
        sb.Append($"    commands=\"{cmds}\"\n");
        sb.Append("\n");
        sb.Append("    if [ $cmd_index -eq 0 ]; then\n");
        sb.Append("        case \"$prev\" in\n");
        sb.Append($"            {string.Join('|', GetTopLevelValueTakingFlagNames())}) ;;\n");
        sb.Append("            *)\n");
        sb.Append($"                COMPREPLY=($(compgen -W \"$commands --help --version --license {topLevelFlags}\" -- \"$cur\"))\n");
        sb.Append("                return\n");
        sb.Append("                ;;\n");
        sb.Append("        esac\n");
        sb.Append("    fi\n");
        sb.Append("\n");
        sb.Append("    case \"$prev\" in\n");
        foreach (var (command, subcommands) in CliCommandMetadata.CommandSubcommands)
        {
            var candidates = string.Join(' ', subcommands);
            if (CliCommandMetadata.OptionalSubcommandCommands.Contains(command))
                candidates += $" {BuildBashFlagList(command)}";
            sb.Append($"        {command}) COMPREPLY=($(compgen -W \"{candidates}\" -- \"$cur\")); return ;;\n");
        }
        foreach (var flag in GetValueFlags(IsPathCompletionKind).Where(flag => flag.SupplementalCompletionValues.Count > 0))
        {
            sb.Append($"        {BuildBashFlagPattern(flag)}) COMPREPLY=($(compgen -W \"{string.Join(' ', flag.SupplementalCompletionValues)}\" -- \"$cur\") $(compgen -f -- \"$cur\")) ;;\n");
        }
        sb.Append($"        {BuildBashValueFlagPattern(IsPathCompletionKind, flag => flag.SupplementalCompletionValues.Count == 0)}) COMPREPLY=($(compgen -f -- \"$cur\")) ;;\n");
        sb.Append($"        {BuildBashValueFlagPattern(kind => kind == CliOptionValueKind.Language)}) COMPREPLY=($(compgen -W \"{langs}\" -- \"$cur\")) ;;\n");
        sb.Append($"        {BuildBashValueFlagPattern(kind => kind == CliOptionValueKind.SymbolKind)}) COMPREPLY=($(compgen -W \"{kinds}\" -- \"$cur\")) ;;\n");
        foreach (var (flag, values) in GetEnumValueCompletions().Where(item => item.Flag != "--format"))
            sb.Append($"        {flag}) COMPREPLY=($(compgen -W \"{string.Join(' ', values)}\" -- \"$cur\")) ;;\n");
        foreach (var flagGroup in GetContextEnumValueCompletions().GroupBy(item => item.Flag, StringComparer.Ordinal))
        {
            sb.Append($"        {flagGroup.Key})\n");
            sb.Append("            case \"$cmd|$nested\" in\n");
            foreach (var (command, subcommand, _, values) in flagGroup.OrderByDescending(item => item.Subcommand is not null))
            {
                var contextPattern = subcommand is null ? $"{command}\\|*" : $"{command}\\|{subcommand}";
                sb.Append($"                {contextPattern}) COMPREPLY=($(compgen -W \"{string.Join(' ', values)}\" -- \"$cur\")) ;;\n");
            }
            sb.Append("            esac ;;\n");
        }
        sb.Append("        --format)\n");
        sb.Append("            case \"$cmd\" in\n");
        foreach (var (command, values) in GetFormatValueCompletions())
            sb.Append($"                {command}) COMPREPLY=($(compgen -W \"{string.Join(' ', values)}\" -- \"$cur\")) ;;\n");
        sb.Append("            esac ;;\n");
        sb.Append("        *)\n");
        sb.Append("            if [ \"$cmd\" = \"hooks\" ] && [ -z \"$nested\" ] && [[ \"$cur\" != -* ]]; then\n");
        sb.Append($"                COMPREPLY=($(compgen -W \"{string.Join(' ', GetNestedSubcommandNames("hooks"))}\" -- \"$cur\"))\n");
        sb.Append("                return\n");
        sb.Append("            fi\n");
        for (var i = 0; i < EnumeratedCompletionCommands.Length; i++)
        {
            var command = EnumeratedCompletionCommands[i];
            var keyword = i == 0 ? "if" : "elif";
            if (command == "hooks")
            {
                sb.Append($"            {keyword} [ \"$cmd\" = \"hooks\" ] && [ \"$nested\" = \"install\" ]; then\n");
                sb.Append($"                COMPREPLY=($(compgen -W \"{BuildBashFlagList("hooks", "install")}\" -- \"$cur\"))\n");
                sb.Append("            elif [ \"$cmd\" = \"hooks\" ] && [ \"$nested\" = \"uninstall\" ]; then\n");
                sb.Append($"                COMPREPLY=($(compgen -W \"{BuildBashFlagList("hooks", "uninstall")}\" -- \"$cur\"))\n");
                sb.Append("            elif [ \"$cmd\" = \"hooks\" ]; then\n");
                sb.Append($"                COMPREPLY=($(compgen -W \"{BuildBashFlagList("hooks", "status")}\" -- \"$cur\"))\n");
            }
            else
            {
                sb.Append($"            {keyword} [ \"$cmd\" = \"{command}\" ]; then\n");
                sb.Append($"                COMPREPLY=($(compgen -W \"{BuildBashFlagList(command)}\" -- \"$cur\"))\n");
            }
        }
        sb.Append("            else\n");
        sb.Append($"                COMPREPLY=($(compgen -W \"{BuildBashGenericFlagList()}\" -- \"$cur\"))\n");
        sb.Append("            fi\n");
        sb.Append("            ;;\n");
        sb.Append("    esac\n");
        sb.Append("}\n");
        sb.Append("complete -F _cdidx cdidx");
        return sb.ToString();
    }

    private static string BuildBashFlagList(string command, string? subcommand = null)
    {
        // Per-command branch: schema flags + universal --help. `find` additionally surfaces
        // `--` as the end-of-options marker so users can pass literal queries starting with `-`.
        // schema のフラグに `--help` を加え、`find` のみ `--` end-of-options マーカーも露出させる。
        var tokens = new List<string>();
        foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command, subcommand))
        {
            tokens.Add(flag.Name);
            if (flag.ShortName is not null)
                tokens.Add(flag.ShortName);
        }
        tokens.Add("--help");
        if (command == "find")
            tokens.Add("--");
        return string.Join(" ", tokens);
    }

    private static string BuildBashGenericFlagList()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>();
        foreach (var command in GenericBranchRepresentativeCommands)
        {
            foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
            {
                // Skip flags that are scoped to the enumerated per-command branches; the generic
                // branch is the catch-all for "everything else".
                if (IsEnumeratedBranchScopedFlag(flag.Name))
                    continue;
                if (seen.Add(flag.Name))
                {
                    tokens.Add(flag.Name);
                    if (flag.ShortName is not null)
                        tokens.Add(flag.ShortName);
                }
            }
        }
        tokens.Add("--help");
        return string.Join(" ", tokens);
    }

    private static bool IsEnumeratedBranchScopedFlag(string flagName) =>
        flagName is "--max-line-width" or "--snippet-lines" or "--snippet-focus" or "--fts" or "--no-dedup" or "--no-visibility-rank"
            or "--prefix" or "--exact-substring" or "--token-boundary" or "--integrity-check" or "--check" or "--stale-after"
            or "--start" or "--end" or "--focus-line" or "--focus-column" or "--focus-length"
            or "--before" or "--after" or "--group-by-name";

    private static string BuildBashValueFlagPattern(
        Func<CliOptionValueKind, bool> predicate,
        Func<CliFlag, bool>? flagPredicate = null) =>
        string.Join('|', GetValueFlagNames(predicate, flagPredicate));

    private static string BuildBashFlagPattern(CliFlag flag) =>
        flag.ShortName is null
            ? flag.Name
            : $"{flag.Name}|{flag.ShortName}";

    private static string GetZshCompletions()
    {
        var cmds = string.Join(" ", ShellCommandNames.Select(c => $"'{c}:{c} command'"));
        var langs = GetCompletionLangs();
        var kinds = GetCompletionKinds();
        var version = ConsoleUi.LoadVersion();
        var sb = new StringBuilder();
        sb.Append("#compdef cdidx\n");
        sb.Append($"# cdidx zsh completions generated for version {version}\n");
        sb.Append("# Regenerate this script after upgrading cdidx.\n");
        sb.Append("_cdidx() {\n");
        sb.Append("    local -a commands\n");
        sb.Append("    commands=(\n");
        sb.Append($"        {cmds}\n");
        sb.Append("    )\n");
        sb.Append("\n");
        sb.Append("    _arguments -C \\\n");
        foreach (var arg in BuildZshTopLevelArgs(langs, kinds))
            sb.Append($"        {arg} \\\n");
        sb.Append("        '1:command:->cmds' \\\n");
        sb.Append("        '*::arg:->args'\n");
        sb.Append("\n");
        sb.Append("    case $state in\n");
        sb.Append("        cmds) _describe 'command' commands ;;\n");
        sb.Append("        args)\n");
        sb.Append("            local subcmd nested i skip_next cmd_index\n");
        sb.Append("            subcmd=''\n");
        sb.Append("            cmd_index=0\n");
        sb.Append("            skip_next=0\n");
        sb.Append("            for (( i = 2; i < CURRENT; i++ )); do\n");
        sb.Append("                if (( skip_next )); then skip_next=0; continue; fi\n");
        sb.Append("                case $words[i] in\n");
        sb.Append($"                    ({string.Join('|', GetTopLevelValueTakingFlagNames())}) skip_next=1 ;;\n");
        sb.Append($"                    ({string.Join('|', GetTopLevelValueTakingFlagNames().Select(name => $"{name}=*"))}) ;;\n");
        sb.Append($"                    ({string.Join('|', GetTopLevelFlagOnlyNames())}) ;;\n");
        sb.Append("                    (-*) ;;\n");
        sb.Append("                    (*) subcmd=$words[i]; cmd_index=$i; break ;;\n");
        sb.Append("                esac\n");
        sb.Append("            done\n");
        sb.Append("            if [[ -z $subcmd ]]; then\n");
        sb.Append("                _describe 'command' commands\n");
        sb.Append("                return\n");
        sb.Append("            fi\n");
        sb.Append("            nested=''\n");
        sb.Append("            case $subcmd in\n");
        foreach (var (command, _) in CliCommandMetadata.CommandSubcommands)
        {
            sb.Append($"                ({command})\n");
            sb.Append("                    skip_next=0\n");
            sb.Append("                    for (( i = cmd_index + 1; i < CURRENT; i++ )); do\n");
            sb.Append("                        if (( skip_next )); then skip_next=0; continue; fi\n");
            sb.Append("                        case $words[i] in\n");
            foreach (var flagName in GetValueTakingFlagNamesForNestedCommand(command))
            {
                sb.Append($"                            ({flagName}) skip_next=1 ;;\n");
                sb.Append($"                            ({flagName}=*) ;;\n");
            }
            sb.Append($"                            ({string.Join('|', GetNestedSubcommandNames(command))}) nested=$words[i]; break ;;\n");
            sb.Append("                        esac\n");
            sb.Append("                    done\n");
            sb.Append("                    ;;\n");
        }
        sb.Append("            esac\n");
        foreach (var (command, subcommands) in CliCommandMetadata.CommandSubcommands)
        {
            var needsSubcommand = command == "hooks"
                ? "$subcmd == hooks && -z $nested && $PREFIX != -*"
                : $"$subcmd == {command} && -z $nested && $CURRENT -le $(( cmd_index + 2 ))";
            sb.Append($"            if [[ {needsSubcommand} ]]; then\n");
            sb.Append("                local -a subcommands\n");
            sb.Append("                subcommands=(\n");
            sb.Append($"                    {string.Join(' ', subcommands.Select(subcommand => $"'{subcommand}:{subcommand} subcommand'"))}\n");
            sb.Append("                )\n");
            sb.Append("                _describe 'subcommand' subcommands\n");
            if (CliCommandMetadata.OptionalSubcommandCommands.Contains(command))
                AppendZshArguments(sb, BuildZshArgsForCommand(command, langs, kinds));
            sb.Append("                return\n");
            sb.Append("            fi\n");
        }
        var zshBranches = new List<(string Condition, string Command, string? Subcommand)>();
        foreach (var command in EnumeratedCompletionCommands)
        {
            foreach (var subcommand in GetCompletionContextSubcommands(command))
            {
                zshBranches.Add(($"$subcmd == {command} && $nested == {subcommand}", command, subcommand));
            }
            zshBranches.Add(($"$subcmd == {command}", command, command == "hooks" ? "status" : null));
        }
        for (var i = 0; i < zshBranches.Count; i++)
        {
            var (condition, command, subcommand) = zshBranches[i];
            sb.Append($"            {(i == 0 ? "if" : "elif")} [[ {condition} ]]; then\n");
            AppendZshArguments(sb, BuildZshArgsForCommand(command, langs, kinds, subcommand));
        }
        sb.Append("            else\n");
        AppendZshArguments(sb, BuildZshGenericArgs(langs, kinds));
        sb.Append("            fi\n");
        sb.Append("            ;;\n");
        sb.Append("    esac\n");
        sb.Append("}\n");
        sb.Append("_cdidx");
        return sb.ToString();
    }

    private static List<string> BuildZshArgsForCommand(
        string command,
        string langs,
        string kinds,
        string? subcommand = null)
    {
        var args = new List<string>();
        foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command, subcommand))
            args.AddRange(FormatZshArguments(flag, langs, kinds, command, subcommand));
        // Append a trailing positional placeholder so zsh suggests path/query completion after
        // the flags - but only for commands that actually accept a positional argument. `status`,
        // `db`, `hotspots`, etc. would reject anything typed there, so emitting no placeholder
        // matches the original hand-written script's behavior.
        // 末尾 positional は path / query を受け付けるコマンドにのみ付ける。
        var positional = command switch
        {
            "excerpt" => "'*:path'",
            "find" or "search" or "references" or "inspect" => "'*:query'",
            _ => null,
        };
        if (positional is not null)
            args.Add(positional);
        return args;
    }

    private static List<string> BuildZshTopLevelArgs(string langs, string kinds)
    {
        var args = new List<string>();
        foreach (var flag in CliFlagSchema.GetTopLevelCompletionFlags())
            args.AddRange(FormatZshArguments(flag, langs, kinds));
        args.Add("'--help[Show help]'");
        args.Add("'--version[Show version]'");
        args.Add("'--license[Show license summary]'");
        return args;
    }

    private static List<string> BuildZshGenericArgs(string langs, string kinds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var args = new List<string>();
        foreach (var command in GenericBranchRepresentativeCommands)
        {
            foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
            {
                if (IsEnumeratedBranchScopedFlag(flag.Name))
                    continue;
                if (seen.Add(flag.Name))
                    args.AddRange(FormatZshArguments(flag, langs, kinds));
            }
        }
        args.Add("'*:query'");
        return args;
    }

    private static IEnumerable<string> FormatZshArguments(
        CliFlag flag,
        string langs,
        string kinds,
        string? command = null,
        string? subcommand = null)
    {
        yield return FormatZshArgument(flag.Name, flag, langs, kinds, command, subcommand);
        if (flag.ShortName is not null)
            yield return FormatZshArgument(flag.ShortName, flag, langs, kinds, command, subcommand);
    }

    private static string FormatZshArgument(
        string name,
        CliFlag flag,
        string langs,
        string kinds,
        string? command,
        string? subcommand)
    {
        var desc = flag.GetDescription(command ?? string.Empty).Replace("'", "''");
        if (!flag.IsValueBearing)
            return $"'{name}[{desc}]'";

        var valueKind = flag.GetValueKind(command ?? string.Empty, subcommand);
        var valuePlaceholder = flag.GetValuePlaceholder(command ?? string.Empty, subcommand);
        var valueSpec = valueKind switch
        {
            _ when IsPathCompletionKind(valueKind) && flag.SupplementalCompletionValues.Count > 0 =>
                $"value:_alternative \"files:file:_files\" \"values:value:({string.Join(' ', flag.SupplementalCompletionValues)})\"",
            _ when IsPathCompletionKind(valueKind) => "file:_files",
            CliOptionValueKind.Language => $"language:({langs})",
            CliOptionValueKind.SymbolKind => $"kind:({kinds})",
            _ when flag.Name == "--format" && command is not null && GetFormatValues(command) is { } formats => $"value:({string.Join(' ', formats)})",
            CliOptionValueKind.Finite when GetEnumValues(flag, command, subcommand) is { } values =>
                $"{flag.GetValueDomain(command ?? string.Empty, subcommand)?.CompletionLabel ?? "value"}:({string.Join(' ', values)})",
            CliOptionValueKind.Repository => "repository",
            _ when valuePlaceholder is "<n>" or "<line>" => "number",
            _ when valuePlaceholder == "<id>" => "id",
            _ when valuePlaceholder == "<datetime>" => "datetime",
            _ when valuePlaceholder == "<query>" => "query",
            _ when valuePlaceholder == "<name>" => "name",
            _ when valuePlaceholder == "<host:port>" => "address",
            _ => "value",
        };
        return $"'{name}[{desc}]:{valueSpec}'";
    }

    private static void AppendZshArguments(StringBuilder sb, IReadOnlyList<string> args)
    {
        sb.Append("                _arguments");
        for (var i = 0; i < args.Count; i++)
        {
            sb.Append(" \\\n                    ");
            sb.Append(args[i]);
        }
        sb.Append('\n');
    }

    private static string GetFishCompletions()
    {
        var langs = GetCompletionLangs();
        var kinds = GetCompletionKinds();
        var lines = new List<string>
        {
            $"# cdidx fish completions generated for version {ConsoleUi.LoadVersion()}",
            "# Regenerate this script after upgrading cdidx.",
            "function __fish_cdidx_context",
            "    set -l tokens (commandline -opc)",
            "    set -e tokens[1]",
            "    set -l cmd ''",
            "    set -l nested ''",
            "    set -l skip_next 0",
            "    for token in $tokens",
            "        if test $skip_next -eq 1",
            "            set skip_next 0",
            "            continue",
            "        end",
            "        if test -z \"$cmd\"",
            "            switch $token",
            $"                case {FormatFishPatterns(GetTopLevelValueTakingFlagNames())}",
            "                    set skip_next 1",
            $"                case {FormatFishPatterns(GetTopLevelValueTakingFlagNames().Select(name => $"{name}=*"))}",
            $"                case {FormatFishPatterns(GetTopLevelFlagOnlyNames())}",
            "                case '-*'",
            "                case '*'",
            "                    set cmd $token",
            "            end",
            "            continue",
            "        end",
            "        switch $cmd",
        };
        foreach (var (command, _) in CliCommandMetadata.CommandSubcommands)
        {
            lines.Add($"            case '{command}'");
            lines.Add("                switch $token");
            lines.Add($"                    case {FormatFishPatterns(GetValueTakingFlagNamesForNestedCommand(command))}");
            lines.Add("                        set skip_next 1");
            lines.Add($"                    case {FormatFishPatterns(GetValueTakingFlagNamesForNestedCommand(command).Select(name => $"{name}=*"))}");
            lines.Add($"                    case {FormatFishPatterns(GetNestedSubcommandNames(command))}");
            lines.Add("                        set nested $token");
            lines.Add("                end");
        }
        lines.AddRange(
        [
            "        end",
            "    end",
            "    echo \"$cmd|$nested\"",
            "end",
            "function __fish_cdidx_needs_command",
            "    set -l context (string split -m 1 '|' (__fish_cdidx_context))",
            "    test -z \"$context[1]\"",
            "end",
            "function __fish_cdidx_using_command",
            "    set -l context (string split -m 1 '|' (__fish_cdidx_context))",
            "    contains -- $context[1] $argv",
            "end",
            "function __fish_cdidx_using_context --argument-names expected_command expected_nested",
            "    set -l context (string split -m 1 '|' (__fish_cdidx_context))",
            "    test \"$context[1]\" = \"$expected_command\"; and test \"$context[2]\" = \"$expected_nested\"",
            "end",
        ]);
        foreach (var cmd in ShellCommandNames)
            lines.Add($"complete -c cdidx -n '__fish_cdidx_needs_command' -a '{cmd}' -d '{cmd} command'");
        foreach (var (command, subcommands) in CliCommandMetadata.CommandSubcommands)
            lines.Add($"complete -c cdidx -n '__fish_cdidx_using_context {command}' -a '{string.Join(' ', subcommands)}' -d '{command} subcommand'");
        lines.Add("complete -c cdidx -n '__fish_cdidx_needs_command' -l help -d 'Show help'");
        lines.Add("complete -c cdidx -n '__fish_cdidx_needs_command' -l version -d 'Show version'");
        lines.Add("complete -c cdidx -n '__fish_cdidx_needs_command' -l license -d 'Show license summary'");
        foreach (var flag in CliFlagSchema.GetTopLevelCompletionFlags())
        {
            var name = flag.Name.TrimStart('-');
            var shortName = flag.ShortName is null ? "" : $" -s {flag.ShortName.TrimStart('-')}";
            var requiresArg = flag.IsValueBearing ? " -r" : "";
            var valueKind = flag.GetValueKind(string.Empty);
            var argSpec = valueKind switch
            {
                CliOptionValueKind.Language => $" -a '{langs}'",
                CliOptionValueKind.SymbolKind => $" -a '{kinds}'",
                CliOptionValueKind.Finite when GetEnumValues(flag) is { } values => $" -a '{string.Join(' ', values)}'",
                _ when flag.SupplementalCompletionValues.Count > 0 => $" -a '{string.Join(' ', flag.SupplementalCompletionValues)}'",
                _ => "",
            };
            var description = flag.Description.Replace("'", "\\'");
            lines.Add($"complete -c cdidx -n '__fish_cdidx_needs_command' -l {name}{shortName}{requiresArg}{argSpec} -d '{description}'");
        }

        // Emit one `complete` line per schema flag, joining the applicable command list into the
        // fish `__fish_seen_subcommand_from` predicate. Hotspots' `--group-by-name` description is
        // shortened to match the legacy "Collapse same-name rows across files" tooltip that the
        // existing test pins (the schema description is fuller and still appears in zsh).
        // schema 1 行 = fish の 1 行 (`complete -c cdidx -n '__fish_seen_subcommand_from <cmds>' -l <name> ...`)
        // という対応で生成する。`--group-by-name` のみ既存テストが期待する短い tooltip を維持。
        foreach (var flag in CliFlagSchema.All)
        {
            if (flag.PrimaryCommands.Count == 0)
                continue;
            if (flag.Name == "--format")
            {
                foreach (var command in flag.PrimaryCommands.OrderBy(c => Array.IndexOf(ShellCommandNames, c)))
                {
                    var values = GetFormatValues(command) ?? [];
                    lines.Add($"complete -c cdidx -n '__fish_cdidx_using_command {command}' -l format -r -a '{string.Join(' ', values)}' -d '{flag.Description.Replace("'", "\\'")}'");
                }
                continue;
            }
            var contextualCommands = flag.CompletionSubcommands.Keys
                .Concat(flag.CommandDescriptions.Keys)
                .Concat(flag.CommandValuePlaceholders.Keys)
                .Concat(flag.CommandValueKinds.Keys)
                .Concat(flag.CommandValueDomains.Keys)
                .Concat(flag.SubcommandValueDomains.Keys)
                .ToHashSet(StringComparer.Ordinal);
            var sharedCommands = flag.PrimaryCommands
                .Where(command => !contextualCommands.Contains(command))
                .OrderBy(command => Array.IndexOf(ShellCommandNames, command))
                .ToArray();
            if (sharedCommands.Length > 0)
            {
                lines.Add(BuildFishFlagCompletion(
                    flag,
                    $"__fish_cdidx_using_command {string.Join(' ', sharedCommands)}",
                    command: string.Empty,
                    subcommand: null,
                    langs,
                    kinds));
            }

            foreach (var command in flag.PrimaryCommands
                         .Where(contextualCommands.Contains)
                         .OrderBy(command => Array.IndexOf(ShellCommandNames, command)))
            {
                if (flag.CompletionSubcommands.TryGetValue(command, out var nestedSubcommands))
                {
                    foreach (var nestedSubcommand in nestedSubcommands.OrderBy(value => value, StringComparer.Ordinal))
                    {
                        lines.Add(BuildFishFlagCompletion(
                            flag,
                            $"__fish_cdidx_using_context {command} {nestedSubcommand}",
                            command,
                            nestedSubcommand,
                            langs,
                            kinds));
                    }
                }
                else if (flag.SubcommandValueDomains.TryGetValue(command, out var subcommandDomains))
                {
                    var exclusions = string.Join(
                        "; and ",
                        subcommandDomains.Keys
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .Select(nestedSubcommand => $"not __fish_cdidx_using_context {command} {nestedSubcommand}"));
                    lines.Add(BuildFishFlagCompletion(
                        flag,
                        $"__fish_cdidx_using_command {command}; and {exclusions}",
                        command,
                        subcommand: null,
                        langs,
                        kinds));
                    foreach (var nestedSubcommand in subcommandDomains.Keys.OrderBy(value => value, StringComparer.Ordinal))
                    {
                        lines.Add(BuildFishFlagCompletion(
                            flag,
                            $"__fish_cdidx_using_context {command} {nestedSubcommand}",
                            command,
                            nestedSubcommand,
                            langs,
                            kinds));
                    }
                }
                else
                {
                    lines.Add(BuildFishFlagCompletion(
                        flag,
                        $"__fish_cdidx_using_command {command}",
                        command,
                        subcommand: null,
                        langs,
                        kinds));
                }
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildFishFlagCompletion(
        CliFlag flag,
        string predicate,
        string command,
        string? subcommand,
        string langs,
        string kinds)
    {
        var name = flag.Name.TrimStart('-');
        var requiresArg = flag.IsValueBearing ? " -r" : "";
        var shortName = flag.ShortName is null ? "" : $" -s {flag.ShortName.TrimStart('-')}";
        var description = name switch
        {
            "group-by-name" => "Collapse same-name rows across files",
            _ => flag.GetDescription(command),
        };
        var valueKind = flag.GetValueKind(command, subcommand);
        var argSpec = valueKind switch
        {
            CliOptionValueKind.Language => $" -a '{langs}'",
            CliOptionValueKind.SymbolKind => $" -a '{kinds}'",
            CliOptionValueKind.Finite when GetEnumValues(flag, command, subcommand) is { } values => $" -a '{string.Join(' ', values)}'",
            _ when flag.SupplementalCompletionValues.Count > 0 => $" -a '{string.Join(' ', flag.SupplementalCompletionValues)}'",
            _ => "",
        };
        description = description.Replace("'", "\\'");
        return $"complete -c cdidx -n '{predicate}' -l {name}{shortName}{requiresArg}{argSpec} -d '{description}'";
    }

    private static string GetPowerShellCompletions()
    {
        var cmds = FormatPowerShellArray(ShellCommandNames);
        var langs = FormatPowerShellArray(GetCompletionLangs().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var kinds = FormatPowerShellArray(GetCompletionKinds().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var topLevelFlags = FormatPowerShellArray(BuildTopLevelFlagList());
        var topLevelValueFlags = FormatPowerShellArray(GetTopLevelValueTakingFlagNames());
        var topLevelFlagOnly = FormatPowerShellArray(GetTopLevelFlagOnlyNames());
        var sb = new StringBuilder();
        sb.AppendLine($"# cdidx PowerShell completions generated for version {ConsoleUi.LoadVersion()}");
        sb.AppendLine("# Regenerate this script after upgrading cdidx.");
        sb.AppendLine("Register-ArgumentCompleter -Native -CommandName cdidx -ScriptBlock {");
        sb.AppendLine("    param($wordToComplete, $commandAst, $cursorPosition)");
        sb.AppendLine($"    $commands = @({cmds})");
        sb.AppendLine($"    $langs = @({langs})");
        sb.AppendLine($"    $kinds = @({kinds})");
        sb.AppendLine("    $enumValues = @{");
        foreach (var (flag, values) in GetEnumValueCompletions().Where(item => item.Flag != "--format"))
            sb.AppendLine($"        '{EscapePowerShellSingleQuoted(flag)}' = @({FormatPowerShellArray(values)})");
        sb.AppendLine("    }");
        sb.AppendLine("    $formatValues = @{");
        foreach (var (command, values) in GetFormatValueCompletions())
            sb.AppendLine($"        '{EscapePowerShellSingleQuoted(command)}' = @({FormatPowerShellArray(values)})");
        sb.AppendLine("    }");
        sb.AppendLine("    $contextEnumValues = @{");
        foreach (var (command, subcommand, flag, values) in GetContextEnumValueCompletions())
            sb.AppendLine($"        '{EscapePowerShellSingleQuoted($"{command}|{subcommand}|{flag}")}' = @({FormatPowerShellArray(values)})");
        sb.AppendLine("    }");
        sb.AppendLine($"    $topLevelFlags = @({topLevelFlags})");
        sb.AppendLine($"    $topLevelValueFlags = @({topLevelValueFlags})");
        sb.AppendLine($"    $topLevelFlagOnly = @({topLevelFlagOnly})");
        sb.AppendLine("    $subcommands = @{");
        foreach (var (command, subcommands) in CliCommandMetadata.CommandSubcommands)
            sb.AppendLine($"        '{EscapePowerShellSingleQuoted(command)}' = @({FormatPowerShellArray(subcommands)})");
        sb.AppendLine("    }");
        sb.AppendLine("    $nestedValueFlags = @{");
        foreach (var (command, _) in CliCommandMetadata.CommandSubcommands)
            sb.AppendLine($"        '{EscapePowerShellSingleQuoted(command)}' = @({FormatPowerShellArray(GetValueTakingFlagNamesForNestedCommand(command))})");
        sb.AppendLine("    }");
        sb.AppendLine("    $optionalSubcommandFlags = @{");
        foreach (var command in CliCommandMetadata.OptionalSubcommandCommands)
            sb.AppendLine($"        '{EscapePowerShellSingleQuoted(command)}' = @({FormatPowerShellArray(BuildPowerShellFlagList(command))})");
        sb.AppendLine("    }");
        sb.AppendLine("    $elements = @($commandAst.CommandElements)");
        sb.AppendLine("    $tokens = @($elements | ForEach-Object { $_.Extent.Text })");
        sb.AppendLine("    $lastElement = if ($elements.Count -ge 1) { $elements[$elements.Count - 1] } else { $null }");
        sb.AppendLine("    $afterLastToken = $lastElement -and $cursorPosition -gt $lastElement.Extent.EndOffset");
        sb.AppendLine("    $scanLimit = if ($afterLastToken) { $tokens.Count } else { [Math]::Max(1, $tokens.Count - 1) }");
        sb.AppendLine("    $subcmd = $null");
        sb.AppendLine("    $subcmdIndex = -1");
        sb.AppendLine("    $skipCommandValue = $false");
        sb.AppendLine("    for ($i = 1; $i -lt $scanLimit; $i++) {");
        sb.AppendLine("        $token = $tokens[$i]");
        sb.AppendLine("        if ($skipCommandValue) { $skipCommandValue = $false; continue }");
        sb.AppendLine("        $valueFlag = $topLevelValueFlags | Where-Object { $token -eq $_ -or $token.StartsWith(($_ + '='), [System.StringComparison]::Ordinal) } | Select-Object -First 1");
        sb.AppendLine("        if ($valueFlag) {");
        sb.AppendLine("            if ($token -eq $valueFlag) { $skipCommandValue = $true }");
        sb.AppendLine("            continue");
        sb.AppendLine("        }");
        sb.AppendLine("        if ($topLevelFlagOnly -contains $token -or $token.StartsWith('-')) { continue }");
        sb.AppendLine("        $subcmd = $token");
        sb.AppendLine("        $subcmdIndex = $i");
        sb.AppendLine("        break");
        sb.AppendLine("    }");
        sb.AppendLine("    $nested = $null");
        sb.AppendLine("    if ($subcmd -and $subcommands.ContainsKey($subcmd)) {");
        sb.AppendLine("        $skipNestedValue = $false");
        sb.AppendLine("        for ($i = $subcmdIndex + 1; $i -lt $scanLimit; $i++) {");
        sb.AppendLine("            $token = $tokens[$i]");
        sb.AppendLine("            if ($skipNestedValue) { $skipNestedValue = $false; continue }");
        sb.AppendLine("            $valueFlag = $nestedValueFlags[$subcmd] | Where-Object { $token -eq $_ -or $token.StartsWith(($_ + '='), [System.StringComparison]::Ordinal) } | Select-Object -First 1");
        sb.AppendLine("            if ($valueFlag) {");
        sb.AppendLine("                if ($token -eq $valueFlag) { $skipNestedValue = $true }");
        sb.AppendLine("                continue");
        sb.AppendLine("            }");
        sb.AppendLine("            if ($subcommands[$subcmd] -contains $token) { $nested = $token; break }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    $prev = if ([string]::IsNullOrEmpty($wordToComplete) -and $tokens.Count -ge 1) { $tokens[$tokens.Count - 1] } elseif ($tokens.Count -ge 2) { $tokens[$tokens.Count - 2] } else { '' }");
        sb.AppendLine("    function New-CdidxCompletion($value, $kind = 'ParameterValue') {");
        sb.AppendLine("        [System.Management.Automation.CompletionResult]::new($value, $value, $kind, $value)");
        sb.AppendLine("    }");
        sb.AppendLine("    switch ($prev) {");
        foreach (var flag in GetValueFlags(IsPathCompletionKind).Where(flag => flag.SupplementalCompletionValues.Count > 0))
        {
            sb.AppendLine($"        {{ $_ -in @({FormatPowerShellArray(GetFlagNames(flag))}) }} {{");
            sb.AppendLine($"            @({FormatPowerShellArray(flag.SupplementalCompletionValues)}) | Where-Object {{ $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) }} | ForEach-Object {{ New-CdidxCompletion $_ }}");
            sb.AppendLine("            [System.Management.Automation.CompletionCompleters]::CompleteFilename($wordToComplete)");
            sb.AppendLine("            return");
            sb.AppendLine("        }");
        }
        sb.AppendLine($"        {{ $_ -in @({FormatPowerShellArray(GetValueFlagNames(IsPathCompletionKind, flag => flag.SupplementalCompletionValues.Count == 0))}) }} {{");
        sb.AppendLine("            [System.Management.Automation.CompletionCompleters]::CompleteFilename($wordToComplete)");
        sb.AppendLine("            return");
        sb.AppendLine("        }");
        sb.AppendLine($"        {{ $_ -in @({FormatPowerShellArray(GetValueFlagNames(kind => kind == CliOptionValueKind.Language))}) }} {{ $langs | Where-Object {{ $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) }} | ForEach-Object {{ New-CdidxCompletion $_ }}; return }}");
        sb.AppendLine($"        {{ $_ -in @({FormatPowerShellArray(GetValueFlagNames(kind => kind == CliOptionValueKind.SymbolKind))}) }} {{ $kinds | Where-Object {{ $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) }} | ForEach-Object {{ New-CdidxCompletion $_ }}; return }}");
        sb.AppendLine("        '--format' { if ($formatValues.ContainsKey($subcmd)) { $formatValues[$subcmd] | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ } }; return }");
        sb.AppendLine("        { $contextEnumValues.ContainsKey(\"$subcmd|$nested|$_\") } { $contextEnumValues[\"$subcmd|$nested|$_\"] | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ }; return }");
        sb.AppendLine("        { $contextEnumValues.ContainsKey(\"$subcmd||$_\") } { $contextEnumValues[\"$subcmd||$_\"] | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ }; return }");
        sb.AppendLine("        { $enumValues.ContainsKey($_) } { $enumValues[$_] | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ }; return }");
        sb.AppendLine("    }");
        sb.AppendLine("    if (-not $subcmd) {");
        sb.AppendLine("        $commands + @('--help', '--version', '--license') + $topLevelFlags | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ 'ParameterName' }");
        sb.AppendLine("        return");
        sb.AppendLine("    }");
        sb.AppendLine("    if ($subcommands.ContainsKey($subcmd)) {");
        sb.AppendLine("        if (-not $nested) {");
        sb.AppendLine("            $candidates = @($subcommands[$subcmd])");
        sb.AppendLine("            if ($optionalSubcommandFlags.ContainsKey($subcmd)) { $candidates += $optionalSubcommandFlags[$subcmd] }");
        sb.AppendLine("            $candidates | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ $(if ($_.StartsWith('-')) { 'ParameterName' } else { 'ParameterValue' }) }");
        sb.AppendLine("            return");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    if ($subcmd -eq 'hooks' -and $nested -eq 'install') {");
        sb.AppendLine($"        $flags = @({FormatPowerShellArray(BuildPowerShellFlagList("hooks", "install"))})");
        sb.AppendLine("    } elseif ($subcmd -eq 'hooks' -and $nested -eq 'uninstall') {");
        sb.AppendLine($"        $flags = @({FormatPowerShellArray(BuildPowerShellFlagList("hooks", "uninstall"))})");
        sb.AppendLine("    } elseif ($subcmd -eq 'hooks') {");
        sb.AppendLine($"        $flags = @({FormatPowerShellArray(BuildPowerShellFlagList("hooks", "status"))})");
        sb.AppendLine("    } else {");
        sb.AppendLine("        switch ($subcmd) {");
        foreach (var command in EnumeratedCompletionCommands)
        {
            if (command != "hooks")
                sb.AppendLine($"            '{EscapePowerShellSingleQuoted(command)}' {{ $flags = @({FormatPowerShellArray(BuildPowerShellFlagList(command))}) }}");
        }
        sb.AppendLine($"            default {{ $flags = @({FormatPowerShellArray(BuildPowerShellGenericFlagList())}) }}");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    $flags | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ 'ParameterName' }");
        sb.Append("}");
        return sb.ToString();
    }

    private static List<string> BuildPowerShellFlagList(string command, string? subcommand = null)
    {
        var tokens = new List<string>();
        foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command, subcommand))
        {
            tokens.Add(flag.Name);
            if (flag.ShortName is not null)
                tokens.Add(flag.ShortName);
        }
        tokens.Add("--help");
        if (command == "find")
            tokens.Add("--");
        return tokens;
    }

    private static IReadOnlyList<string> GetValueTakingFlagNamesForNestedCommand(string command)
    {
        var names = new List<string>();
        foreach (var flag in CliFlagSchema.GetHelpFlagsForCommand(command).Where(flag => flag.IsValueBearing))
        {
            names.Add(flag.Name);
            if (flag.ShortName is not null)
                names.Add(flag.ShortName);
        }
        return names;
    }

    private static IReadOnlyList<string> GetTopLevelValueTakingFlagNames() =>
        CliFlagSchema.GetTopLevelCompletionFlags()
            .Where(flag => flag.IsValueBearing)
            .SelectMany(GetFlagNames)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> GetTopLevelFlagOnlyNames() =>
        CliFlagSchema.GetTopLevelCompletionFlags()
            .Where(flag => !flag.IsValueBearing)
            .SelectMany(GetFlagNames)
            .Concat(["--help", "--version", "--license"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> GetCompletionContextSubcommands(string command) =>
        CliFlagSchema.All
            .SelectMany(flag =>
            {
                var subcommands = new List<string>();
                if (flag.CompletionSubcommands.TryGetValue(command, out var completionSubcommands))
                    subcommands.AddRange(completionSubcommands);
                if (flag.SubcommandValueDomains.TryGetValue(command, out var valueDomains))
                    subcommands.AddRange(valueDomains.Keys);
                return subcommands;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> GetNestedSubcommandNames(string command) =>
        CliCommandMetadata.CommandSubcommands
            .First(entry => string.Equals(entry.Command, command, StringComparison.Ordinal))
            .Subcommands;

    private static string FormatFishPatterns(IEnumerable<string> patterns)
    {
        var values = patterns.Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 0
            ? "'__cdidx_no_match__'"
            : string.Join(' ', values.Select(value => $"'{value.Replace("'", "\\'", StringComparison.Ordinal)}'"));
    }

    private static List<string> BuildTopLevelFlagList()
    {
        var tokens = new List<string>();
        foreach (var flag in CliFlagSchema.GetTopLevelCompletionFlags())
        {
            tokens.Add(flag.Name);
            if (flag.ShortName is not null)
                tokens.Add(flag.ShortName);
        }
        return tokens;
    }

    private static List<string> BuildPowerShellGenericFlagList()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>();
        foreach (var command in GenericBranchRepresentativeCommands)
        {
            foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
            {
                if (IsEnumeratedBranchScopedFlag(flag.Name))
                    continue;
                if (seen.Add(flag.Name))
                {
                    tokens.Add(flag.Name);
                    if (flag.ShortName is not null)
                        tokens.Add(flag.ShortName);
                }
            }
        }
        tokens.Add("--help");
        return tokens;
    }

    private static IReadOnlyList<CliFlag> GetValueFlags(Func<CliOptionValueKind, bool> predicate)
    {
        var flags = new List<CliFlag>();
        foreach (var flag in CliFlagSchema.All)
        {
            if (!flag.IsValueBearing)
                continue;

            var kinds = flag.PrimaryCommands
                .Select(command => flag.GetValueKind(command))
                .Append(flag.GetValueKind(string.Empty))
                .Concat(flag.CommandValueKinds.Values);
            if (!kinds.Any(predicate))
                continue;

            flags.Add(flag);
        }
        return flags;
    }

    private static IReadOnlyList<string> GetValueFlagNames(
        Func<CliOptionValueKind, bool> predicate,
        Func<CliFlag, bool>? flagPredicate = null) =>
        GetValueFlags(predicate)
            .Where(flag => flagPredicate?.Invoke(flag) ?? true)
            .SelectMany(GetFlagNames)
            .ToArray();

    private static IEnumerable<string> GetFlagNames(CliFlag flag)
    {
        yield return flag.Name;
        if (flag.ShortName is not null)
            yield return flag.ShortName;
    }

    private static bool IsPathCompletionKind(CliOptionValueKind kind) =>
        kind is CliOptionValueKind.FilePath
            or CliOptionValueKind.DirectoryPath
            or CliOptionValueKind.PathPattern
            or CliOptionValueKind.Project;

    private static string FormatPowerShellArray(IEnumerable<string> values) =>
        string.Join(", ", values.Select(value => $"'{EscapePowerShellSingleQuoted(value)}'"));

    private static string EscapePowerShellSingleQuoted(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string[]? GetEnumValues(
        CliFlag flag,
        string? command = null,
        string? subcommand = null)
    {
        var domain = flag.GetValueDomain(command ?? string.Empty, subcommand);
        return domain?.CanonicalValues.ToArray();
    }

    private static IEnumerable<(string Flag, string[] Values)> GetEnumValueCompletions() =>
        CliFlagSchema.All
            .Where(flag =>
                flag.Name != "--format"
                && flag.CommandValueDomains.Count == 0
                && flag.SubcommandValueDomains.Count == 0)
            .Select(flag => (
                Flag: flag.Name,
                Values: GetEnumValues(flag)))
            .Where(item => item.Values is not null)
            .GroupBy(item => item.Flag, StringComparer.Ordinal)
            .Select(group => (group.Key, group.SelectMany(item => item.Values!).Distinct(StringComparer.Ordinal).ToArray()));

    private static IEnumerable<(string Command, string? Subcommand, string Flag, string[] Values)> GetContextEnumValueCompletions()
    {
        foreach (var flag in CliFlagSchema.All.Where(flag => flag.Name != "--format"))
        {
            var commands = flag.CommandValueDomains.Keys
                .Concat(flag.SubcommandValueDomains.Keys)
                .Distinct(StringComparer.Ordinal);
            foreach (var command in commands)
            {
                if (flag.SubcommandValueDomains.TryGetValue(command, out var subcommandDomains))
                {
                    foreach (var (subcommand, domain) in subcommandDomains)
                        yield return (command, subcommand, flag.Name, domain.CanonicalValues.ToArray());
                }

                if (GetEnumValues(flag, command) is { } values)
                    yield return (command, null, flag.Name, values);
            }
        }
    }

    private static IEnumerable<(string Command, string[] Values)> GetFormatValueCompletions()
    {
        foreach (var command in CliFlagSchema.AllCommands)
        {
            var values = CliFlagSchema.GetCanonicalValuesForCommand(command, "--format");
            if (values.Count > 0)
                yield return (command, values.ToArray());
        }
    }

    private static string[]? GetFormatValues(string command) =>
        CliFlagSchema.GetCanonicalValuesForCommand(command, "--format") is { Count: > 0 } values
            ? values.ToArray()
            : null;
}
