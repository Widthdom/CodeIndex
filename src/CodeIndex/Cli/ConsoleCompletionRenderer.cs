using CodeIndex.Indexer;
using System.Text;

namespace CodeIndex.Cli;

internal static class ConsoleCompletionRenderer
{
    private static string[] Commands => CliCommandCatalog.Commands;

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
        string.Join(" ", FileIndexer.GetLanguageExtensions().Values
            .Append("bat")
            .Append("cmd")
            .Concat(QueryCommandRunner.GetCompletionLanguageAliases())
            .Distinct()
            .OrderBy(l => l));

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
            "typealias", "union", "unsubscribe", "variable",
        }.OrderBy(k => k, StringComparer.Ordinal));

    // Commands that get their own per-command completion branch (bash/zsh). Order matters: the
    // `else` generic branch is the catch-all, and `search` must remain the last `elif` so the
    // tests `CompletionRenderer_BashAndZshScopeMaxLineWidthToSearchBranch` can isolate it.
    // bash / zsh の専用ブランチを持つコマンド。順序は意図的で、`search` が最終 elif、`else` が
    // generic catch-all となるよう揃える。テストもこの並びを前提にしている。
    private static readonly string[] EnumeratedCompletionCommands =
    [
        "find", "excerpt", "references", "inspect", "hotspots", "status", "validate-config", "db", "report", "suggestions", "search",
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
        var cmds = string.Join(" ", Commands);
        var topLevelFlags = string.Join(" ", BuildTopLevelFlagList());
        var langs = GetCompletionLangs();
        var kinds = GetCompletionKinds();
        var version = ConsoleUi.LoadVersion();
        var sb = new StringBuilder();
        sb.Append($"# cdidx bash completions generated for version {version}\n");
        sb.Append("# Regenerate this script after upgrading cdidx.\n");
        sb.Append("_cdidx() {\n");
        sb.Append("    local cur prev commands\n");
        sb.Append("    local cmd\n");
        sb.Append("    cur=\"${COMP_WORDS[COMP_CWORD]}\"\n");
        sb.Append("    prev=\"${COMP_WORDS[COMP_CWORD-1]}\"\n");
        sb.Append("    cmd=\"${COMP_WORDS[1]}\"\n");
        sb.Append($"    commands=\"{cmds}\"\n");
        sb.Append("\n");
        sb.Append("    if [ $COMP_CWORD -eq 1 ]; then\n");
        sb.Append($"        COMPREPLY=($(compgen -W \"$commands --help --version --license {topLevelFlags}\" -- \"$cur\"))\n");
        sb.Append("        return\n");
        sb.Append("    fi\n");
        sb.Append("\n");
        sb.Append("    case \"$prev\" in\n");
        sb.Append("        --db|--path|--exclude-path|--open-issues|--output|-o|--metrics) COMPREPLY=($(compgen -f -- \"$cur\")) ;;\n");
        sb.Append("        --color) COMPREPLY=($(compgen -W \"auto always never\" -- \"$cur\")) ;;\n");
        sb.Append("        --palette) COMPREPLY=($(compgen -W \"basic 256 truecolor\" -- \"$cur\")) ;;\n");
        sb.Append("        --log-format) COMPREPLY=($(compgen -W \"text json\" -- \"$cur\")) ;;\n");
        sb.Append($"        --lang) COMPREPLY=($(compgen -W \"{langs}\" -- \"$cur\")) ;;\n");
        sb.Append($"        --kind) COMPREPLY=($(compgen -W \"{kinds}\" -- \"$cur\")) ;;\n");
        sb.Append("        *)\n");
        for (var i = 0; i < EnumeratedCompletionCommands.Length; i++)
        {
            var command = EnumeratedCompletionCommands[i];
            var keyword = i == 0 ? "if" : "elif";
            sb.Append($"            {keyword} [ \"$cmd\" = \"{command}\" ]; then\n");
            sb.Append($"                COMPREPLY=($(compgen -W \"{BuildBashFlagList(command)}\" -- \"$cur\"))\n");
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

    private static string BuildBashFlagList(string command)
    {
        // Per-command branch: schema flags + universal --help. `find` additionally surfaces
        // `--` as the end-of-options marker so users can pass literal queries starting with `-`.
        // schema のフラグに `--help` を加え、`find` のみ `--` end-of-options マーカーも露出させる。
        var tokens = new List<string>();
        foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
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
            or "--prefix" or "--exact-substring" or "--integrity-check" or "--check" or "--stale-after"
            or "--start" or "--end" or "--focus-line" or "--focus-column" or "--focus-length"
            or "--before" or "--after" or "--group-by-name";

    private static string GetZshCompletions()
    {
        var cmds = string.Join(" ", Commands.Select(c => $"'{c}:{c} command'"));
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
        sb.Append("            local subcmd\n");
        sb.Append("            subcmd=$words[2]\n");
        for (var i = 0; i < EnumeratedCompletionCommands.Length; i++)
        {
            var command = EnumeratedCompletionCommands[i];
            var keyword = i == 0 ? "if" : "elif";
            sb.Append($"            {keyword} [[ $subcmd == {command} ]]; then\n");
            AppendZshArguments(sb, BuildZshArgsForCommand(command, langs, kinds));
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

    private static List<string> BuildZshArgsForCommand(string command, string langs, string kinds)
    {
        var args = new List<string>();
        foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
            args.AddRange(FormatZshArguments(flag, langs, kinds));
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

    private static IEnumerable<string> FormatZshArguments(CliFlag flag, string langs, string kinds)
    {
        yield return FormatZshArgument(flag.Name, flag, langs, kinds);
        if (flag.ShortName is not null)
            yield return FormatZshArgument(flag.ShortName, flag, langs, kinds);
    }

    private static string FormatZshArgument(string name, CliFlag flag, string langs, string kinds)
    {
        var desc = flag.Description.Replace("'", "''");
        if (!flag.IsValueBearing)
            return $"'{name}[{desc}]'";

        var valueSpec = flag.ValuePlaceholder switch
        {
            "<path>" => "file:_files",
            "<glob>" => "pattern",
            "<n>" => "number",
            "<line>" => "number",
            "<id>" => "id",
            "<datetime>" => "datetime",
            "<lang>" => $"language:({langs})",
            "<kind>" => $"kind:({kinds})",
            "<auto|always|never>" => "mode:(auto always never)",
            "<basic|256|truecolor>" => "palette:(basic 256 truecolor)",
            "<text|json>" => "format:(text json)",
            "<query>" => "query",
            "<name>" => "name",
            "<host:port>" => "address",
            "<stdio|http>" => "transport:(stdio http)",
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
        };
        foreach (var cmd in Commands)
            lines.Add($"complete -c cdidx -n '__fish_use_subcommand' -a '{cmd}' -d '{cmd} command'");
        lines.Add("complete -c cdidx -n '__fish_use_subcommand' -l help -d 'Show help'");
        lines.Add("complete -c cdidx -n '__fish_use_subcommand' -l version -d 'Show version'");
        lines.Add("complete -c cdidx -n '__fish_use_subcommand' -l license -d 'Show license summary'");
        foreach (var flag in CliFlagSchema.GetTopLevelCompletionFlags())
        {
            var name = flag.Name.TrimStart('-');
            var shortName = flag.ShortName is null ? "" : $" -s {flag.ShortName.TrimStart('-')}";
            var requiresArg = flag.IsValueBearing ? " -r" : "";
            var argSpec = flag.ValuePlaceholder switch
            {
                "<auto|always|never>" => " -a 'auto always never'",
                "<basic|256|truecolor>" => " -a 'basic 256 truecolor'",
                "<text|json>" => " -a 'text json'",
                _ => "",
            };
            var description = flag.Description.Replace("'", "\\'");
            lines.Add($"complete -c cdidx -n '__fish_use_subcommand' -l {name}{shortName}{requiresArg}{argSpec} -d '{description}'");
        }

        // Emit one `complete` line per schema flag, joining the applicable command list into the
        // fish `__fish_seen_subcommand_from` predicate. Hotspots' `--group-by-name` description is
        // shortened to match the legacy "Collapse same-name rows across files" tooltip that the
        // existing test pins (the schema description is fuller and still appears in zsh).
        // schema 1 行 = fish の 1 行 (`complete -c cdidx -n '__fish_seen_subcommand_from <cmds>' -l <name> ...`)
        // という対応で生成する。`--group-by-name` のみ既存テストが期待する短い tooltip を維持。
        foreach (var flag in CliFlagSchema.All)
        {
            if (flag.Commands.Count == 0)
                continue;
            var commands = string.Join(' ', flag.Commands.OrderBy(c => Array.IndexOf(Commands, c)));
            var name = flag.Name.TrimStart('-');
            // Token order is `-l name (-r)? (-a 'values')? -d 'description'` - matches the
            // pre-refactor hand-written script so the ConsoleUiTests fish-extractor regex
            // (`'  -l <flag>`) keeps working for value-bearing flags too.
            // トークン順は旧スクリプトと同じ `-l name (-r)? (-a) -d` を維持する。
            // ConsoleUiTests の fish 抽出正規表現が -l の直前に値マーカーを期待していないため。
            var requiresArg = flag.IsValueBearing ? " -r" : "";
            var shortName = flag.ShortName is null ? "" : $" -s {flag.ShortName.TrimStart('-')}";
            var description = name switch
            {
                "group-by-name" => "Collapse same-name rows across files",
                _ => flag.Description,
            };
            var argSpec = flag.ValuePlaceholder switch
            {
                "<lang>" => $" -a '{langs}'",
                "<kind>" => $" -a '{kinds}'",
                "<stdio|http>" => " -a 'stdio http'",
                _ => "",
            };
            description = description.Replace("'", "\\'");
            lines.Add($"complete -c cdidx -n '__fish_seen_subcommand_from {commands}' -l {name}{shortName}{requiresArg}{argSpec} -d '{description}'");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string GetPowerShellCompletions()
    {
        var cmds = FormatPowerShellArray(Commands);
        var langs = FormatPowerShellArray(GetCompletionLangs().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var kinds = FormatPowerShellArray(GetCompletionKinds().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var topLevelFlags = FormatPowerShellArray(BuildTopLevelFlagList());
        var sb = new StringBuilder();
        sb.AppendLine($"# cdidx PowerShell completions generated for version {ConsoleUi.LoadVersion()}");
        sb.AppendLine("# Regenerate this script after upgrading cdidx.");
        sb.AppendLine("Register-ArgumentCompleter -Native -CommandName cdidx -ScriptBlock {");
        sb.AppendLine("    param($wordToComplete, $commandAst, $cursorPosition)");
        sb.AppendLine($"    $commands = @({cmds})");
        sb.AppendLine($"    $langs = @({langs})");
        sb.AppendLine($"    $kinds = @({kinds})");
        sb.AppendLine("    $colorModes = @('auto', 'always', 'never')");
        sb.AppendLine("    $palettes = @('basic', '256', 'truecolor')");
        sb.AppendLine("    $logFormats = @('text', 'json')");
        sb.AppendLine($"    $topLevelFlags = @({topLevelFlags})");
        sb.AppendLine("    $elements = @($commandAst.CommandElements)");
        sb.AppendLine("    $tokens = @($elements | ForEach-Object { $_.Extent.Text })");
        sb.AppendLine("    $lastElement = if ($elements.Count -ge 1) { $elements[$elements.Count - 1] } else { $null }");
        sb.AppendLine("    $afterLastToken = $lastElement -and $cursorPosition -gt $lastElement.Extent.EndOffset");
        sb.AppendLine("    $subcmd = $tokens | Where-Object { $_ -ne 'cdidx' -and -not $_.StartsWith('-') } | Select-Object -First 1");
        sb.AppendLine("    $prev = if ([string]::IsNullOrEmpty($wordToComplete) -and $tokens.Count -ge 1) { $tokens[$tokens.Count - 1] } elseif ($tokens.Count -ge 2) { $tokens[$tokens.Count - 2] } else { '' }");
        sb.AppendLine("    function New-CdidxCompletion($value, $kind = 'ParameterValue') {");
        sb.AppendLine("        [System.Management.Automation.CompletionResult]::new($value, $value, $kind, $value)");
        sb.AppendLine("    }");
        sb.AppendLine("    switch ($prev) {");
        sb.AppendLine("        { $_ -in @('--db', '--path', '--exclude-path', '--open-issues', '--output', '-o', '--metrics') } {");
        sb.AppendLine("            Get-ChildItem -Name \"$wordToComplete*\" -ErrorAction SilentlyContinue | ForEach-Object { New-CdidxCompletion $_ 'ProviderItem' }");
        sb.AppendLine("            return");
        sb.AppendLine("        }");
        sb.AppendLine("        '--color' { $colorModes | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ }; return }");
        sb.AppendLine("        '--palette' { $palettes | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ }; return }");
        sb.AppendLine("        '--log-format' { $logFormats | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ }; return }");
        sb.AppendLine("        '--lang' { $langs | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ }; return }");
        sb.AppendLine("        '--kind' { $kinds | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ }; return }");
        sb.AppendLine("    }");
        sb.AppendLine("    if (-not $subcmd -or ($tokens.Count -le 2 -and -not ([string]::IsNullOrEmpty($wordToComplete)) -and -not $afterLastToken)) {");
        sb.AppendLine("        $commands + @('--help', '--version', '--license') + $topLevelFlags | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ 'ParameterName' }");
        sb.AppendLine("        return");
        sb.AppendLine("    }");
        sb.AppendLine("    switch ($subcmd) {");
        foreach (var command in EnumeratedCompletionCommands)
            sb.AppendLine($"        '{EscapePowerShellSingleQuoted(command)}' {{ $flags = @({FormatPowerShellArray(BuildPowerShellFlagList(command))}) }}");
        sb.AppendLine($"        default {{ $flags = @({FormatPowerShellArray(BuildPowerShellGenericFlagList())}) }}");
        sb.AppendLine("    }");
        sb.AppendLine("    $flags | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ 'ParameterName' }");
        sb.Append("}");
        return sb.ToString();
    }

    private static List<string> BuildPowerShellFlagList(string command)
    {
        var tokens = new List<string>();
        foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
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

    private static string FormatPowerShellArray(IEnumerable<string> values) =>
        string.Join(", ", values.Select(value => $"'{EscapePowerShellSingleQuoted(value)}'"));

    private static string EscapePowerShellSingleQuoted(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
