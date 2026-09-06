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

internal static class ProductionAndToolingScopeDefaults
{
    // Tooling inclusion is intentionally location-agnostic: production tooling may live in hidden
    // automation directories, installer modules, build folders, or repository-specific paths.
    // Exclude documentation by file shape/conventional documentation directories and rely on the
    // shared test classifier plus conventional fixture directories instead of maintaining a
    // universal list of tooling directory names.
    private static readonly string[] IncludePathsValue = [];
    private static readonly string[] ExcludePathsValue =
    [
        "src/CodeIndex/Cli/SearchAuditRecipes.cs",
        "doc/**",
        "docs/**",
        "documentation/**",
        "**/doc/**",
        "**/docs/**",
        "**/documentation/**",
        "fixture/**",
        "fixtures/**",
        "**/fixture/**",
        "**/fixtures/**",
        "*.md",
        "**/*.md",
        "*.mdx",
        "**/*.mdx",
        "*.rst",
        "**/*.rst",
        "*.adoc",
        "**/*.adoc"
    ];

    internal static IReadOnlyList<string> IncludePaths => IncludePathsValue;
    internal static IReadOnlyList<string> ExcludePaths => ExcludePathsValue;
}
