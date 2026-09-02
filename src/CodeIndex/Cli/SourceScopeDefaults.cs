namespace CodeIndex.Cli;

internal static class SourceScopeDefaults
{
    private static readonly string[] IncludePathsValue = ["src/**"];
    private static readonly string[] ExcludePathsValue =
    [
        "src/CodeIndex/Cli/SearchAuditRecipes.cs",
        "tests/**",
        "docs/**",
        "CHANGELOG.md",
        "changelog.d/**",
        "README.md",
        "USER_GUIDE.md",
        "DEVELOPER_GUIDE.md",
        "TESTING_GUIDE.md",
        "AGENT_GUIDE.md",
        ".agent_harness/**",
        ".claude/**",
        ".codex/**",
        ".github/**"
    ];

    internal static IReadOnlyList<string> IncludePaths => IncludePathsValue;
    internal static IReadOnlyList<string> ExcludePaths => ExcludePathsValue;
}
