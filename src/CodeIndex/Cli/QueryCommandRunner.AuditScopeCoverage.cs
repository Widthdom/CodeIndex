using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal const int AuditScopeCoveragePathLimit = 10;

    internal static SearchRecipeCoverageJsonResult CreateSearchRecipeCoverage(
        IEnumerable<SearchAuditRecipeQuery> selectedQueries)
        => new(selectedQueries.Select(query => query.Name));

    internal static void EnsureSearchRecipeCoverage(
        DbReader reader,
        SearchRecipeScopeJsonResult scope,
        QueryCommandOptions options)
        => EnsureSearchRecipeCoverage(
            reader,
            scope,
            options.Lang,
            options.Since,
            options.IncludeGenerated,
            cache: null);

    private sealed class SearchRecipeFileCoverageCache
    {
        private readonly List<Entry> _entries = [];

        internal bool TryGet(
            SearchRecipeScopeJsonResult scope,
            string? lang,
            DateTime? since,
            bool includeGenerated,
            out SearchRecipeFileCoverageSnapshot snapshot)
        {
            foreach (var entry in _entries)
            {
                if (entry.ExcludeTests == scope.ExcludeTests
                    && entry.IncludeGenerated == includeGenerated
                    && entry.Since == since
                    && string.Equals(entry.Lang, lang, StringComparison.OrdinalIgnoreCase)
                    && entry.PathPatterns.SequenceEqual(scope.PathPatterns, StringComparer.Ordinal)
                    && entry.ExcludePaths.SequenceEqual(scope.ExcludePaths, StringComparer.Ordinal))
                {
                    snapshot = entry.Snapshot;
                    return true;
                }
            }

            snapshot = null!;
            return false;
        }

        internal void Add(
            SearchRecipeScopeJsonResult scope,
            string? lang,
            DateTime? since,
            bool includeGenerated,
            SearchRecipeFileCoverageSnapshot snapshot)
            => _entries.Add(new(
                lang,
                since,
                [.. scope.PathPatterns],
                [.. scope.ExcludePaths],
                scope.ExcludeTests,
                includeGenerated,
                snapshot));

        private sealed record Entry(
            string? Lang,
            DateTime? Since,
            string[] PathPatterns,
            string[] ExcludePaths,
            bool ExcludeTests,
            bool IncludeGenerated,
            SearchRecipeFileCoverageSnapshot Snapshot);
    }

    private sealed record SearchRecipeFileCoverageSnapshot(
        SearchRecipeCoverageSetJsonResult Included,
        SearchRecipeCoverageSetJsonResult Excluded,
        SearchRecipeCoverageSetJsonResult Unindexed);

    internal static void EnsureSearchRecipeCoverage(
        DbReader reader,
        SearchRecipeScopeJsonResult scope,
        string? lang,
        DateTime? since,
        bool includeGenerated)
        => EnsureSearchRecipeCoverage(reader, scope, lang, since, includeGenerated, cache: null);

    private static void EnsureSearchRecipeCoverage(
        DbReader reader,
        SearchRecipeScopeJsonResult scope,
        string? lang,
        DateTime? since,
        bool includeGenerated,
        SearchRecipeFileCoverageCache? cache)
    {
        AddExternalRecipeSourceExclusion(reader, scope);
        var coverage = scope.Coverage;
        if (coverage is null || coverage.FileCoverageInitialized)
            return;
        coverage.GeneratedCodePolicy = includeGenerated
            ? "include"
            : reader.GeneratedFileFilterAvailable
                ? "exclude"
                : "unavailable";

        if (cache?.TryGet(scope, lang, since, includeGenerated, out var cached) == true)
        {
            ApplyFileCoverage(coverage, cached);
            return;
        }

        var indexCompletion = reader.GetPersistedIndexCompletion();
        var indexed = reader.GetAuditScopeIndexedFileCoverage(
            lang,
            scope.PathPatterns,
            scope.ExcludePaths,
            scope.ExcludeTests,
            since,
            AuditScopeCoveragePathLimit);
        var knownUnindexed = reader.GetAuditScopeKnownUnindexedFileCoverage(
            lang,
            scope.PathPatterns,
            scope.ExcludePaths,
            scope.ExcludeTests,
            since,
            AuditScopeCoveragePathLimit);
        var snapshot = new SearchRecipeFileCoverageSnapshot(
            indexCompletion.IndexComplete
                ? ExactCoverageSet(
                    "indexed_files",
                    indexed.IncludedCount,
                    indexed.IncludedPaths,
                    AuditScopeCoveragePathLimit)
                : IncompleteIndexCoverageSet(indexed.IncludedPaths),
            indexCompletion.IndexComplete
                ? ExactCoverageSet(
                    "indexed_files",
                    indexed.ExcludedCount,
                    indexed.ExcludedPaths,
                    AuditScopeCoveragePathLimit)
                : IncompleteIndexCoverageSet(indexed.ExcludedPaths),
            BuildUnindexedCoverage(
                reader,
                scope,
                lang,
                since,
                knownUnindexed,
                indexCompletion.IndexComplete));
        ApplyFileCoverage(coverage, snapshot);
        cache?.Add(scope, lang, since, includeGenerated, snapshot);
    }

    private static void AddExternalRecipeSourceExclusion(
        DbReader reader,
        SearchRecipeScopeJsonResult scope)
    {
        if (string.IsNullOrWhiteSpace(scope.RecipeSourcePath)
            || string.Equals(scope.Name, SearchAuditRecipes.AllAuditScope, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var projectRoot = reader.GetIndexedProjectRoot();
        if (string.IsNullOrWhiteSpace(projectRoot))
            return;

        try
        {
            var relativePath = Path.GetRelativePath(
                Path.GetFullPath(projectRoot),
                Path.GetFullPath(scope.RecipeSourcePath));
            if (Path.IsPathRooted(relativePath)
                || string.Equals(relativePath, "..", StringComparison.Ordinal)
                || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return;
            }

            var normalizedPath = relativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            if (!string.IsNullOrWhiteSpace(normalizedPath)
                && !string.Equals(normalizedPath, ".", StringComparison.Ordinal))
            {
                AddDistinct(scope.ExcludePaths, [normalizedPath]);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // The recipe loader already bounded and diagnosed invalid source paths. If the indexed
            // root cannot be related safely, retain the declared scope instead of broadening it.
        }
    }

    private static void ApplyFileCoverage(
        SearchRecipeCoverageJsonResult coverage,
        SearchRecipeFileCoverageSnapshot snapshot)
    {
        coverage.Included = snapshot.Included;
        coverage.Excluded = snapshot.Excluded;
        coverage.Unindexed = snapshot.Unindexed;
        coverage.FileCoverageInitialized = true;
    }

    internal static void MarkSearchRecipeQueryExecuted(
        SearchRecipeScopeJsonResult scope,
        string queryName)
        => scope.Coverage?.MarkExecuted(queryName);

    private static SearchRecipeCoverageSetJsonResult BuildUnindexedCoverage(
        DbReader reader,
        SearchRecipeScopeJsonResult scope,
        string? lang,
        DateTime? since,
        AuditScopeKnownUnindexedFileCoverageSnapshot knownUnindexed,
        bool indexComplete)
    {
        var unknownExtensions = BuildUnknownExtensionCoverage(reader, scope, lang, since);
        var paths = knownUnindexed.Paths
            .Concat(unknownExtensions.Paths)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(AuditScopeCoveragePathLimit)
            .ToList();

        if (indexComplete
            && knownUnindexed.Available
            && unknownExtensions.CountAuthoritative
            && unknownExtensions.Count.HasValue)
        {
            return ExactCoverageSet(
                "workspace_files",
                checked(knownUnindexed.Count + unknownExtensions.Count.Value),
                paths,
                AuditScopeCoveragePathLimit);
        }

        var lowerBound = checked(
            (knownUnindexed.Available ? knownUnindexed.Count : 0L)
            + unknownExtensions.CountLowerBound);
        long? upperBound = null;
        if (indexComplete && knownUnindexed.Available && unknownExtensions.CountUpperBound.HasValue)
            upperBound = checked(knownUnindexed.Count + unknownExtensions.CountUpperBound.Value);

        var uncertaintyReason = !indexComplete
            ? "index_generation_incomplete"
            : !knownUnindexed.Available
                ? "known_unindexed_file_diagnostics_unavailable"
                : unknownExtensions.UncertaintyReason ?? "unknown_extension_inventory_uncertain";
        return new SearchRecipeCoverageSetJsonResult(
            "workspace_files",
            null,
            false,
            lowerBound,
            upperBound,
            paths,
            AuditScopeCoveragePathLimit,
            true,
            null,
            false,
            uncertaintyReason);
    }

    private static SearchRecipeCoverageSetJsonResult BuildUnknownExtensionCoverage(
        DbReader reader,
        SearchRecipeScopeJsonResult scope,
        string? lang,
        DateTime? since)
    {
        if (!string.IsNullOrWhiteSpace(lang))
        {
            return SearchRecipeCoverageSetJsonResult.Unavailable(
                "workspace_files",
                "unknown_extension_inventory_has_no_language_classification_for_lang_filter");
        }

        if (since.HasValue)
        {
            return SearchRecipeCoverageSetJsonResult.Unavailable(
                "workspace_files",
                "unknown_extension_inventory_has_no_modified_timestamp_for_since_filter");
        }

        var inventory = reader.GetAuditScopeUnknownFileInventory();
        if (!inventory.Available || !inventory.TotalCount.HasValue)
        {
            return SearchRecipeCoverageSetJsonResult.Unavailable(
                "workspace_files",
                "unknown_extension_inventory_unavailable");
        }

        var matchingPaths = inventory.Paths
            .Where(path => reader.AuditScopePathMatches(
                path,
                scope.PathPatterns,
                scope.ExcludePaths,
                scope.ExcludeTests))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var displayedPaths = matchingPaths.Take(AuditScopeCoveragePathLimit).ToList();
        if (!inventory.PathsTruncated)
        {
            return ExactCoverageSet(
                "workspace_files",
                matchingPaths.Count,
                displayedPaths,
                AuditScopeCoveragePathLimit);
        }

        var unfilteredScope = scope.PathPatterns.Count == 0
            && scope.ExcludePaths.Count == 0
            && !scope.ExcludeTests;
        if (unfilteredScope)
        {
            return ExactCoverageSet(
                "workspace_files",
                inventory.TotalCount.Value,
                displayedPaths,
                AuditScopeCoveragePathLimit);
        }

        var rawOmittedCount = Math.Max(0, inventory.TotalCount.Value - inventory.Paths.Count);
        var lowerBound = matchingPaths.Count;
        return new SearchRecipeCoverageSetJsonResult(
            "workspace_files",
            null,
            false,
            lowerBound,
            checked(lowerBound + rawOmittedCount),
            displayedPaths,
            AuditScopeCoveragePathLimit,
            true,
            null,
            false,
            "unknown_extension_path_inventory_truncated_before_scope_filtering");
    }

    private static SearchRecipeCoverageSetJsonResult IncompleteIndexCoverageSet(List<string> paths)
        => new(
            "indexed_files",
            null,
            false,
            paths.Count,
            null,
            paths,
            AuditScopeCoveragePathLimit,
            true,
            null,
            false,
            "index_generation_incomplete");

    private static SearchRecipeCoverageSetJsonResult ExactCoverageSet(
        string unit,
        long count,
        List<string> paths,
        int pathLimit)
    {
        var omitted = Math.Max(0, count - paths.Count);
        return new(
            unit,
            count,
            true,
            count,
            count,
            paths,
            pathLimit,
            omitted > 0,
            omitted,
            true,
            null);
    }

    internal static void WriteSearchRecipeCoverageText(SearchRecipeCoverageJsonResult? coverage)
    {
        if (coverage is null)
            return;

        Console.WriteLine("Coverage:");
        Console.WriteLine($"  included: {FormatCoverageCount(coverage.Included)} {coverage.Included.Unit}");
        Console.WriteLine($"  excluded: {FormatCoverageCount(coverage.Excluded)} {coverage.Excluded.Unit}");
        Console.WriteLine($"  unindexed: {FormatCoverageCount(coverage.Unindexed)} {coverage.Unindexed.Unit}");
        Console.WriteLine($"  unexecuted: {coverage.Unexecuted.Count} {coverage.Unexecuted.Unit}");
        Console.WriteLine($"  human review: {coverage.HumanReview.State}");
    }

    private static string FormatCoverageCount(SearchRecipeCoverageSetJsonResult coverage)
    {
        if (coverage.CountAuthoritative && coverage.Count.HasValue)
            return coverage.Count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (coverage.CountUpperBound.HasValue)
            return $"{coverage.CountLowerBound}..{coverage.CountUpperBound.Value}";
        return $">={coverage.CountLowerBound} ({coverage.UncertaintyReason ?? "uncertain"})";
    }
}
