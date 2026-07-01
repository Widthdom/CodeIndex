using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    [ThreadStatic]
    private static DbReader? s_batchReader;

    [ThreadStatic]
    private static string? s_batchDbPath;

    [ThreadStatic]
    private static bool s_batchDbPathExplicit;

    [ThreadStatic]
    private static string? s_activeQueryProjectRoot;

    internal const string ProjectFilterRootFallbackReasonCurrentDirectory = "project_root_unresolved_using_current_directory";

    internal readonly record struct ProjectFilterRootResolution(string Root, string? FallbackReason);

    private static DateTime GetUtcNow() => TimeProvider.GetUtcNow().UtcDateTime;
}
