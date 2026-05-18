namespace CodeIndex.Cli;

/// <summary>
/// Argument-parsing helpers shared by the top-level CLI and subcommand runners.
/// トップレベルCLIとサブコマンドで共有する引数解析ヘルパー。
/// </summary>
public static class ArgHelper
{
    // Flags that consume exactly one following token as their value. A `-h` /
    // `--help` appearing immediately after one of these is the flag's value,
    // not a help request, and must be passed through to the subcommand parser.
    // 直後の1トークンを値として消費するフラグ。--help / -h が直後に来ても
    // それは値なので、トップレベルの help 検出でショートカットしてはならない。
    private static readonly HashSet<string> SingleValueFlags = new(StringComparer.Ordinal)
    {
        "--db",
        "--limit", "--top",
        "--lang", "--kind",
        "--path", "--exclude-path",
        "--name",
        "--since",
        "--start", "--end",
        "--before", "--after",
        "--snippet-lines",
        "--max-hops",
        "--depth",
        "--completions",
    };

    /// <summary>
    /// Returns true if the given argument slice contains a bare `--help` / `-h`
    /// token that is not being consumed as the value of a single-value flag.
    /// Multi-value flags (`--commits`, `--files`) naturally stop at any token
    /// starting with '-', so a `-h` / `--help` there is still treated as help.
    /// </summary>
    public static bool WantsHelp(ReadOnlySpan<string> args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token is "--help" or "-h")
                return true;
            if (SingleValueFlags.Contains(token) && i + 1 < args.Length)
            {
                // Skip the value position so a value that happens to equal
                // "--help" / "-h" isn't misread as a help request.
                i++;
            }
        }
        return false;
    }
}
