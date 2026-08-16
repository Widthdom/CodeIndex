using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    [ThreadStatic]
    private static BatchDatabaseContext? s_batchDatabaseContext;

    [ThreadStatic]
    private static string? s_activeQueryProjectRoot;

    internal const string ProjectFilterRootFallbackReasonCurrentDirectory = "project_root_unresolved_using_current_directory";

    internal readonly record struct ProjectFilterRootResolution(string Root, string? FallbackReason);

    private sealed class BatchDatabaseContext(
        DbReader reader,
        string dbPath,
        bool dbPathExplicit)
    {
        public DbReader Reader { get; } = reader;
        public string DbPath { get; } = dbPath;
        public bool DbPathExplicit { get; } = dbPathExplicit;
        public bool ReaderInheritedByCurrentChild { get; set; }
    }

    private static DateTime GetUtcNow() => TimeProvider.GetUtcNow().UtcDateTime;
}
