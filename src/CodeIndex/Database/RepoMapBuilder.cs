using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

/// <summary>
/// Builds repo-level overview (map) from indexed data.
/// Extracted from DbReader to keep each class focused.
/// インデックス済みデータからリポジトリ俯瞰情報（map）を構築する。
/// クラスの責務を明確にするためDbReaderから分離。
/// </summary>
internal sealed class RepoMapBuilder
{
    internal static AsyncLocal<Action?> HeadMetadataCapturedForTesting { get; } = new();

    private readonly SqliteConnection _conn;
    private readonly IReadOnlySet<string> _fileColumns;
    private readonly Func<StringComparer> _getIndexedPathComparer;

    private static readonly Dictionary<string, string[]> EntrypointNameHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = ["Main", "Program", "App", "Startup", "CreateHostBuilder"],
        ["python"] = ["main", "app", "cli"],
        ["javascript"] = ["main", "bootstrap", "start", "createApp", "App"],
        ["typescript"] = ["main", "bootstrap", "start", "createApp", "App"],
        ["go"] = ["main"],
        ["rust"] = ["main"],
        ["java"] = ["main", "Application", "App"],
        ["kotlin"] = ["main", "Application", "App"],
        ["ruby"] = ["main", "call", "App"],
        ["php"] = ["main", "handle", "App"],
        ["swift"] = ["main", "App"],
        ["dart"] = ["main", "runApp"],
        ["scala"] = ["main", "App"],
        ["fsharp"] = ["main", "Program", "App"],
        ["vb"] = ["Main", "Program", "App"],
        ["c"] = ["main"],
        ["cpp"] = ["main"],
        ["haskell"] = ["main"],
        ["r"] = ["main"],
        ["lua"] = ["main"],
        ["elixir"] = ["start", "init", "call"],
    };
    private static readonly Dictionary<string, string[]> EntrypointPathHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = ["Program.cs", "Startup.cs", "App.xaml.cs", "MainWindow.xaml.cs", "MainPage.xaml.cs", "AppShell.xaml.cs", "Shell.xaml.cs", "ContentPage.xaml.cs", "ContentView.xaml.cs", "Window.xaml.cs", "UserControl.xaml.cs", "App.cs", "App.razor"],
        ["python"] = ["main.py", "__main__.py", "app.py", "cli.py"],
        ["javascript"] = ["index.js", "main.js", "app.js", "server.js"],
        ["typescript"] = ["index.ts", "main.ts", "app.ts", "server.ts"],
        ["go"] = ["main.go"],
        ["rust"] = ["main.rs", "lib.rs"],
        ["java"] = ["Main.java", "App.java", "Application.java"],
        ["kotlin"] = ["Main.kt", "App.kt", "Application.kt"],
        ["ruby"] = ["app.rb", "main.rb", "cli.rb"],
        ["php"] = ["index.php", "app.php"],
        ["swift"] = ["main.swift", "App.swift"],
        ["dart"] = ["main.dart", "app.dart"],
        ["scala"] = ["Main.scala", "App.scala", "Application.scala"],
        ["fsharp"] = ["Program.fs", "App.fs"],
        ["vb"] = ["Program.vb", "Main.vb", "Module.vb", "Module1.vb", "Form1.vb", "App.xaml.vb", "App.vb"],
        ["c"] = ["main.c"],
        ["cpp"] = ["main.cpp", "main.cc", "main.cxx"],
        ["haskell"] = ["Main.hs", "Main.lhs"],
        ["r"] = ["main.R", "app.R"],
        ["lua"] = ["main.lua", "init.lua"],
        ["elixir"] = ["application.ex", "router.ex", "endpoint.ex"],
    };

    private readonly bool _hasReferencesTable;

    public RepoMapBuilder(SqliteConnection connection, IReadOnlySet<string> fileColumns, bool hasReferencesTable, Func<StringComparer> getIndexedPathComparer)
    {
        _conn = connection;
        _fileColumns = fileColumns;
        _hasReferencesTable = hasReferencesTable;
        _getIndexedPathComparer = getIndexedPathComparer;
    }

    /// <summary>
    /// Build a repo-level overview to help AI clients orient before deep queries.
    /// 深掘り前の把握に使うリポジトリ俯瞰情報を構築する。
    /// </summary>
    public RepoMapResult Build(int limit, string? lang, IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns, bool excludeTests, double minEntrypointConfidence,
        Func<(DateTime? IndexedAt, DateTime? LatestModified)> getFreshness,
        int? moduleDepth = null, int? oversizedLineThreshold = null, long? oversizedByteThreshold = null,
        int offset = 0, string? requestedCollection = null, bool summaryProjection = false,
        IReadOnlyList<string>? requiredPathPatterns = null)
    {
        DbReader.EnsurePathFilterParameterBudget(pathPatterns, excludePathPatterns, requiredPathPatterns);
        offset = Math.Max(0, offset);
        var retainedLimit = checked(Math.Max(limit, 0) + offset);
        var includeLanguages = IncludesMapCollection(requestedCollection, summaryProjection, "languages");
        var includeModules = IncludesMapCollection(requestedCollection, summaryProjection, "modules");
        var useRankedFilePage = requestedCollection is "top_files" or "largest_files" or "symbol_rich_files" or "reference_rich_files";
        var includeTopFiles = !useRankedFilePage && IncludesMapCollection(requestedCollection, summaryProjection, "top_files");
        var includeLargestFiles = !useRankedFilePage && IncludesMapCollection(requestedCollection, summaryProjection, "largest_files");
        var includeSymbolRichFiles = !useRankedFilePage && IncludesMapCollection(requestedCollection, summaryProjection, "symbol_rich_files");
        var includeReferenceRichFiles = !useRankedFilePage && IncludesMapCollection(requestedCollection, summaryProjection, "reference_rich_files");
        var includeEntrypoints = IncludesMapCollection(requestedCollection, summaryProjection, "entrypoints");
        // Query file stats first, then workspace freshness — preserves original
        // ordering so concurrent indexing cannot make workspace timestamps older
        // than scoped timestamps.
        // ファイル統計を先に取得し、その後にワークスペース鮮度を取得 — 元の順序を
        // 維持し、並行インデックス時にワークスペースのタイムスタンプがスコープ付き
        // タイムスタンプより古くならないようにする。
        //
        // Issue #180: wrap the multi-statement map build in one DEFERRED transaction so
        // the scoped file stats, the workspace freshness, and the entrypoint lookups all
        // come from the same WAL snapshot. Otherwise a concurrent writer committing
        // between statements can make `workspace_latest_modified` older than
        // `latest_modified`, or make entrypoint rows disagree with the file-stats rows
        // they came from.
        // Issue #180: map 内の多段 SELECT を 1 つの DEFERRED transaction で囲み、scoped
        // file stats / workspace freshness / entrypoint 取得が同じ WAL snapshot から返る
        // ようにする。
        using var txn = _conn.BeginTransaction(deferred: true);
        var indexedPathComparer = _getIndexedPathComparer();
        var javaModuleDescriptors = includeModules ? LoadJavaModuleDescriptors() : new Dictionary<string, string>(StringComparer.Ordinal);
        var aggregate = BuildAggregate(
            EnumerateFileStats(lang, pathPatterns, excludePathPatterns, excludeTests, requiredPathPatterns),
            retainedLimit,
            javaModuleDescriptors,
            moduleDepth,
            oversizedLineThreshold,
            oversizedByteThreshold,
            includeLanguages,
            includeModules,
            includeTopFiles,
            includeLargestFiles,
            includeSymbolRichFiles,
            includeReferenceRichFiles,
            includeEntrypoints);
        var freshness = getFreshness();
        var indexedHeadSnapshot = LoadIndexedHeadSnapshot();
        HeadMetadataCapturedForTesting.Value?.Invoke();
        var entrypointPage = includeEntrypoints
            ? GetEntrypoints(aggregate.EntrypointFallbacks, limit, offset, lang, pathPatterns, excludePathPatterns, excludeTests, minEntrypointConfidence, indexedPathComparer, requiredPathPatterns)
            : (Results: new List<RepoEntrypointResult>(), TotalCount: 0);
        var rankedFilePage = useRankedFilePage
            ? GetRankedFilePage(requestedCollection!, limit, offset, lang, pathPatterns, excludePathPatterns, excludeTests, requiredPathPatterns)
            : null;
        var result = new RepoMapResult
        {
            FileCount = aggregate.FileCount,
            TotalLines = aggregate.TotalLines,
            TotalSymbols = aggregate.TotalSymbols,
            TotalReferences = aggregate.TotalReferences,
            IndexedAt = aggregate.IndexedAt,
            LatestModified = aggregate.LatestModified,
            WorkspaceIndexedAt = freshness.IndexedAt,
            WorkspaceLatestModified = freshness.LatestModified,
            ProjectRoot = indexedHeadSnapshot.ProjectRoot,
            LanguageCount = includeLanguages ? aggregate.Languages.Count : null,
            ModuleCount = includeModules ? aggregate.Modules.Count : null,
            EntrypointCount = includeEntrypoints ? entrypointPage.TotalCount : null,
            Languages = BuildLanguageResults(aggregate.Languages, limit, offset),
            Modules = BuildModuleResults(aggregate.Modules, limit, offset),
            TopFiles = requestedCollection == "top_files" ? rankedFilePage! : aggregate.TopFiles.Skip(offset).Take(limit).ToList(),
            LargestFiles = requestedCollection == "largest_files" ? BuildLargestFileResults(rankedFilePage!) : BuildLargestFileResults(aggregate.LargestFiles.Skip(offset).Take(limit).ToList()),
            SymbolRichFiles = requestedCollection == "symbol_rich_files" ? BuildSymbolRichFileResults(rankedFilePage!) : BuildSymbolRichFileResults(aggregate.SymbolRichFiles.Skip(offset).Take(limit).ToList()),
            ReferenceRichFiles = requestedCollection == "reference_rich_files" ? BuildReferenceRichFileResults(rankedFilePage!) : BuildReferenceRichFileResults(aggregate.ReferenceRichFiles.Skip(offset).Take(limit).ToList()),
            Entrypoints = entrypointPage.Results,
            GraphTableAvailable = _hasReferencesTable,
            IndexedHeadSnapshot = indexedHeadSnapshot,
            IssueDraftCandidateCount = aggregate.IssueDraftCandidateCount,
            IssueDraftCandidates = BuildLargestFileResults(aggregate.IssueDraftCandidates.Skip(offset).Take(limit).ToList()),
        };
        txn.Commit();
        return result;
    }

    private static bool IncludesMapCollection(string? requestedCollection, bool summaryProjection, string collection)
        => !summaryProjection
           && (requestedCollection is null || string.Equals(requestedCollection, collection, StringComparison.Ordinal));

    private IEnumerable<RepoFileStat> EnumerateFileStats(string? lang, IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? requiredPathPatterns)
    {
        using var cmd = _conn.CreateCommand();
        var refCountExpr = _hasReferencesTable
            ? "(SELECT COUNT(*) FROM symbol_references r WHERE r.file_id = f.id)"
            : "0";
        var sql = $@"
            SELECT f.path, f.lang, f.size, f.lines,
                   (SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id) AS symbol_count,
                   {refCountExpr} AS reference_count,
                   {GetFileColumnSql("checksum")} AS checksum,
                   {GetFileColumnSql("modified")} AS modified,
                   {GetFileColumnSql("indexed_at")} AS indexed_at
            FROM files f
            WHERE 1=1";

        if (lang != null)
            sql += " AND f.lang = @lang";
        DbReader.AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        DbReader.AppendAdditionalPathIncludeFilters(ref sql, requiredPathPatterns, "requiredPathPattern");
        sql += " ORDER BY f.path";

        cmd.CommandText = sql;
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        DbReader.AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        DbReader.AddPathIncludeFilterParameters(cmd, requiredPathPatterns, "requiredPathPattern");

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            yield return new RepoFileStat
            {
                Path = reader.GetString(0),
                Lang = DbReader.GetNullableString(reader, 1),
                Size = reader.GetInt64(2),
                Lines = reader.GetInt32(3),
                SymbolCount = reader.GetInt32(4),
                ReferenceCount = reader.GetInt32(5),
                Checksum = DbReader.GetNullableString(reader, 6),
                Modified = DbReader.GetNullableDateTime(reader, 7),
                IndexedAt = DbReader.GetNullableDateTime(reader, 8),
            };
        }
    }

    private List<RepoFileSummaryResult> GetRankedFilePage(
        string collection,
        int limit,
        int offset,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        IReadOnlyList<string>? requiredPathPatterns)
    {
        using var cmd = _conn.CreateCommand();
        var refCountExpr = _hasReferencesTable
            ? "(SELECT COUNT(*) FROM symbol_references r WHERE r.file_id = f.id)"
            : "0";
        var sql = $@"
            WITH ranked_files AS (
                SELECT f.path,
                       f.lang,
                       f.size,
                       f.lines,
                       (SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id) AS symbol_count,
                       {refCountExpr} AS reference_count
                FROM files f
                WHERE 1=1";
        if (lang != null)
            sql += " AND f.lang = @lang";
        DbReader.AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        DbReader.AppendAdditionalPathIncludeFilters(ref sql, requiredPathPatterns, "requiredPathPattern");
        sql += ") SELECT path, lang, size, lines, symbol_count, reference_count FROM ranked_files ORDER BY ";
        sql += collection switch
        {
            "top_files" => "(lines + (symbol_count * 5) + (reference_count * 2)) DESC, reference_count DESC, symbol_count DESC, lines DESC, path COLLATE BINARY ASC",
            "largest_files" => "lines DESC, size DESC, path COLLATE BINARY ASC",
            "symbol_rich_files" => "symbol_count DESC, reference_count DESC, lines DESC, path COLLATE BINARY ASC",
            "reference_rich_files" => "reference_count DESC, symbol_count DESC, lines DESC, path COLLATE BINARY ASC",
            _ => throw new ArgumentOutOfRangeException(nameof(collection), collection, "Unsupported ranked map collection."),
        };
        sql += " LIMIT @limit OFFSET @offset";

        cmd.CommandText = sql;
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        DbReader.AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        DbReader.AddPathIncludeFilterParameters(cmd, requiredPathPatterns, "requiredPathPattern");
        SqliteCommandPolicy.Add(cmd, "@limit", Math.Max(0, limit));
        SqliteCommandPolicy.Add(cmd, "@offset", Math.Max(0, offset));

        var results = new List<RepoFileSummaryResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var lines = reader.GetInt32(3);
            var symbolCount = reader.GetInt32(4);
            var referenceCount = reader.GetInt32(5);
            results.Add(new RepoFileSummaryResult
            {
                Path = reader.GetString(0),
                Lang = DbReader.GetNullableString(reader, 1),
                Size = reader.GetInt64(2),
                Lines = lines,
                SymbolCount = symbolCount,
                ReferenceCount = referenceCount,
                Score = collection == "top_files"
                    ? lines + (symbolCount * 5L) + (referenceCount * 2L)
                    : null,
            });
        }
        return results;
    }

    private static RepoMapAggregate BuildAggregate(
        IEnumerable<RepoFileStat> fileStats,
        int limit,
        IReadOnlyDictionary<string, string> moduleByDescriptorPath,
        int? moduleDepth,
        int? oversizedLineThreshold,
        long? oversizedByteThreshold,
        bool includeLanguages,
        bool includeModules,
        bool includeTopFiles,
        bool includeLargestFiles,
        bool includeSymbolRichFiles,
        bool includeReferenceRichFiles,
        bool includeEntrypoints)
    {
        var languages = new Dictionary<string, RepoLanguageResult>(StringComparer.Ordinal);
        var modules = new Dictionary<string, RepoModuleResult>(StringComparer.Ordinal);
        var aggregate = new RepoMapAggregate
        {
            Languages = languages,
            Modules = modules,
            TopFiles = [],
            LargestFiles = [],
            SymbolRichFiles = [],
            ReferenceRichFiles = [],
            EntrypointFallbacks = [],
            IssueDraftCandidates = [],
        };

        foreach (var file in fileStats)
        {
            aggregate.FileCount++;
            if (moduleByDescriptorPath.Count > 0 && string.Equals(file.Lang, "java", StringComparison.OrdinalIgnoreCase))
            {
                var owningModuleName = ResolveOwningJavaModuleName(file.Path, moduleByDescriptorPath);
                if (!string.IsNullOrWhiteSpace(owningModuleName))
                    file.ModuleName = owningModuleName;
            }

            aggregate.TotalLines += file.Lines;
            aggregate.TotalSymbols += file.SymbolCount;
            aggregate.TotalReferences += file.ReferenceCount;
            aggregate.IndexedAt = MaxDateTime(aggregate.IndexedAt, file.IndexedAt);
            aggregate.LatestModified = MaxDateTime(aggregate.LatestModified, file.Modified);

            if (includeLanguages)
            {
                var languageKey = file.Lang ?? "unknown";
                if (!languages.TryGetValue(languageKey, out var language))
                {
                    language = new RepoLanguageResult { Lang = languageKey };
                    languages.Add(languageKey, language);
                }
                AddFileStats(language, file);
            }

            if (includeModules)
            {
                var moduleKey = GetModuleKey(file, moduleDepth);
                if (!modules.TryGetValue(moduleKey, out var module))
                {
                    module = new RepoModuleResult { Module = moduleKey };
                    modules.Add(moduleKey, module);
                }
                AddFileStats(module, file);
            }

            var scoredSummary = CreateScoredFileSummary(file);
            if (includeTopFiles)
                AddBounded(aggregate.TopFiles, scoredSummary, limit, CompareTopFiles);
            if (includeLargestFiles)
                AddBounded(aggregate.LargestFiles, scoredSummary, limit, CompareLargestFiles);
            if (includeSymbolRichFiles)
                AddBounded(aggregate.SymbolRichFiles, scoredSummary, limit, CompareSymbolRichFiles);
            if (includeReferenceRichFiles)
                AddBounded(aggregate.ReferenceRichFiles, scoredSummary, limit, CompareReferenceRichFiles);

            if ((oversizedLineThreshold.HasValue && file.Lines >= oversizedLineThreshold.Value)
                || (oversizedByteThreshold.HasValue && file.Size >= oversizedByteThreshold.Value))
            {
                aggregate.IssueDraftCandidateCount++;
                AddBounded(
                    aggregate.IssueDraftCandidates,
                    CreateUnscoredFileSummary(file),
                    limit,
                    CompareLargestFiles);
            }

            var fallback = includeEntrypoints
                ? ScoreEntrypointFileFallback(file.Path, file.Lang, file.SymbolCount, file.ReferenceCount)
                : default;
            if (includeEntrypoints && fallback.Score > 0)
            {
                aggregate.EntrypointFallbacks.Add(new RepoEntrypointResult
                {
                    Path = file.Path,
                    Lang = file.Lang,
                    Kind = "file",
                    Name = Path.GetFileName(file.Path),
                    Line = 1,
                    Score = fallback.Score,
                    MatchType = fallback.MatchType,
                    Confidence = fallback.Confidence,
                    HintRank = fallback.HintRank,
                });
            }
        }

        return aggregate;
    }

    private static List<RepoLanguageResult> BuildLanguageResults(IReadOnlyDictionary<string, RepoLanguageResult> languages, int limit, int offset)
    {
        return languages.Values
            .OrderByDescending(group => group.Files)
            .ThenBy(group => group.Lang)
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    private static List<RepoModuleResult> BuildModuleResults(IReadOnlyDictionary<string, RepoModuleResult> modules, int limit, int offset)
    {
        return modules.Values
            .OrderByDescending(group => group.References)
            .ThenByDescending(group => group.Symbols)
            .ThenByDescending(group => group.Lines)
            .ThenBy(group => group.Module)
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    private static List<RepoFileSummaryResult> BuildLargestFileResults(IReadOnlyList<RepoFileSummaryResult> fileSummaries)
        => fileSummaries.Select(CopyUnscoredFileSummary).ToList();

    private static List<RepoFileSummaryResult> BuildSymbolRichFileResults(IReadOnlyList<RepoFileSummaryResult> fileSummaries)
        => fileSummaries.Select(CopyUnscoredFileSummary).ToList();

    private static List<RepoFileSummaryResult> BuildReferenceRichFileResults(IReadOnlyList<RepoFileSummaryResult> fileSummaries)
        => fileSummaries.Select(CopyUnscoredFileSummary).ToList();

    private static void AddBounded<T>(List<T> items, T candidate, int limit, Comparison<T> comparison)
    {
        if (limit <= 0)
            return;

        var index = items.BinarySearch(candidate, Comparer<T>.Create(comparison));
        if (index < 0)
            index = ~index;
        if (index >= limit)
            return;

        items.Insert(index, candidate);
        if (items.Count > limit)
            items.RemoveAt(items.Count - 1);
    }

    private static int CompareTopFiles(RepoFileSummaryResult left, RepoFileSummaryResult right)
    {
        var score = (right.Score ?? 0).CompareTo(left.Score ?? 0);
        if (score != 0)
            return score;
        var references = right.ReferenceCount.CompareTo(left.ReferenceCount);
        if (references != 0)
            return references;
        var symbols = right.SymbolCount.CompareTo(left.SymbolCount);
        if (symbols != 0)
            return symbols;
        var lines = right.Lines.CompareTo(left.Lines);
        if (lines != 0)
            return lines;
        return string.Compare(left.Path, right.Path, StringComparison.Ordinal);
    }

    private static int CompareLargestFiles(RepoFileSummaryResult left, RepoFileSummaryResult right)
    {
        var lines = right.Lines.CompareTo(left.Lines);
        if (lines != 0)
            return lines;
        var size = right.Size.CompareTo(left.Size);
        if (size != 0)
            return size;
        return string.Compare(left.Path, right.Path, StringComparison.Ordinal);
    }

    private static int CompareSymbolRichFiles(RepoFileSummaryResult left, RepoFileSummaryResult right)
    {
        var symbols = right.SymbolCount.CompareTo(left.SymbolCount);
        if (symbols != 0)
            return symbols;
        var references = right.ReferenceCount.CompareTo(left.ReferenceCount);
        if (references != 0)
            return references;
        var lines = right.Lines.CompareTo(left.Lines);
        if (lines != 0)
            return lines;
        return string.Compare(left.Path, right.Path, StringComparison.Ordinal);
    }

    private static int CompareReferenceRichFiles(RepoFileSummaryResult left, RepoFileSummaryResult right)
    {
        var references = right.ReferenceCount.CompareTo(left.ReferenceCount);
        if (references != 0)
            return references;
        var symbols = right.SymbolCount.CompareTo(left.SymbolCount);
        if (symbols != 0)
            return symbols;
        var lines = right.Lines.CompareTo(left.Lines);
        if (lines != 0)
            return lines;
        return string.Compare(left.Path, right.Path, StringComparison.Ordinal);
    }

    private static void AddFileStats(RepoLanguageResult target, RepoFileStat file)
    {
        target.Files++;
        target.Lines += file.Lines;
        target.Symbols += file.SymbolCount;
        target.References += file.ReferenceCount;
    }

    private static void AddFileStats(RepoModuleResult target, RepoFileStat file)
    {
        target.Files++;
        target.Lines += file.Lines;
        target.Symbols += file.SymbolCount;
        target.References += file.ReferenceCount;
    }

    private static DateTime? MaxDateTime(DateTime? current, DateTime? candidate)
    {
        if (candidate == null)
            return current;

        if (current == null || candidate > current)
            return candidate;

        return current;
    }

    private Dictionary<string, string> LoadJavaModuleDescriptors()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.path, s.name
            FROM files f
            JOIN symbols s ON s.file_id = f.id
            WHERE f.lang = 'java'
              AND (f.path = 'module-info.java' OR f.path LIKE '%/module-info.java')
              AND s.kind = 'namespace'
            ORDER BY f.path, s.line
            """;

        var moduleByDescriptorPath = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var descriptorPath = reader.GetString(0);
            if (moduleByDescriptorPath.ContainsKey(descriptorPath))
                continue;

            var moduleName = reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(moduleName))
                moduleByDescriptorPath[descriptorPath] = moduleName;
        }

        return moduleByDescriptorPath;
    }

    private (List<RepoEntrypointResult> Results, int TotalCount) GetEntrypoints(IReadOnlyList<RepoEntrypointResult> fallbackEntrypoints, int limit, int offset,
        string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        double minConfidence, StringComparer indexedPathComparer, IReadOnlyList<string>? requiredPathPatterns)
    {
        using var cmd = _conn.CreateCommand();
        var sql = @"
            SELECT f.path, f.lang, s.kind, s.name, s.line
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE s.kind IN ('function', 'class')";

        if (lang != null)
            sql += " AND f.lang = @lang";
        DbReader.AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        DbReader.AppendAdditionalPathIncludeFilters(ref sql, requiredPathPatterns, "requiredPathPattern");
        sql += " ORDER BY f.path, s.line";

        cmd.CommandText = sql;
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        DbReader.AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        DbReader.AddPathIncludeFilterParameters(cmd, requiredPathPatterns, "requiredPathPattern");

        var results = new List<RepoEntrypointResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var path = reader.GetString(0);
            var candidateLang = DbReader.GetNullableString(reader, 1);
            var kind = reader.GetString(2);
            var name = reader.GetString(3);
            var line = reader.GetInt32(4);
            var match = ScoreEntrypoint(path, candidateLang, kind, name);
            if (match.Score <= 0)
                continue;

            results.Add(new RepoEntrypointResult
            {
                Path = path,
                Lang = candidateLang,
                Kind = kind,
                Name = name,
                Line = line,
                Score = match.Score,
                MatchType = match.MatchType,
                Confidence = match.Confidence,
                HintRank = match.HintRank,
            });
        }

        // Fall back to known entry files when symbol extraction has not found a named entrypoint.
        // Path-only helper symbols in Program.cs should not suppress the file fallback.
        // 名前一致の entrypoint が見つからない場合、既知の entry file にフォールバックする。
        // Program.cs 内の path-only な補助シンボルでは file fallback を抑止しない。
        var filesWithNamedEntrypoints = results
            .Where(result => result.MatchType.Contains("name", StringComparison.OrdinalIgnoreCase))
            .Select(result => result.Path)
            .ToHashSet(indexedPathComparer);

        foreach (var fallback in fallbackEntrypoints)
        {
            if (filesWithNamedEntrypoints.Contains(fallback.Path))
                continue;
            results.Add(fallback);
        }

        ApplyEntrypointAmbiguityPenalty(results);
        var ranked = results
            .Where(result => result.Confidence >= minConfidence)
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Confidence)
            .ThenBy(result => result.HintRank)
            .ThenBy(result => result.Path)
            .ThenBy(result => result.Line)
            .ToList();
        return (ranked
            .Skip(offset)
            .Take(limit)
            .ToList(), ranked.Count);
    }

    private string GetFileColumnSql(string columnName)
    {
        return _fileColumns.Contains(columnName) ? $"f.{columnName}" : "NULL";
    }

    private RepoMapIndexedHeadSnapshot LoadIndexedHeadSnapshot()
    {
        string? projectRoot = null;
        string? legacyHead = null;
        string? workspaceVerifiedHead = null;
        string? latestHead = null;
        string? latestBranch = null;
        string? latestTimestamp = null;
        string? legacyBranch = null;
        var latestBranchStampPresent = false;
        var legacyBranchStampPresent = false;

        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT key, value
                FROM codeindex_meta
                WHERE key IN (@projectRoot, @legacyHead, @workspaceVerifiedHead, @latestHead, @latestBranch, @latestTimestamp, @legacyBranch)
                """;
            SqliteCommandPolicy.Add(cmd, "@projectRoot", DbContext.IndexedProjectRootMetaKey);
            SqliteCommandPolicy.Add(cmd, "@legacyHead", DbContext.IndexedHeadCommitMetaKey);
            SqliteCommandPolicy.Add(cmd, "@workspaceVerifiedHead", DbContext.WorkspaceVerifiedHeadShaMetaKey);
            SqliteCommandPolicy.Add(cmd, "@latestHead", DbContext.IndexedHeadShaMetaKey);
            SqliteCommandPolicy.Add(cmd, "@latestBranch", DbContext.IndexedHeadBranchMetaKey);
            SqliteCommandPolicy.Add(cmd, "@latestTimestamp", DbContext.IndexedHeadTimestampMetaKey);
            SqliteCommandPolicy.Add(cmd, "@legacyBranch", DbContext.IndexedHeadCommitBranchMetaKey);

            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var key = reader.GetString(0);
                var value = reader.IsDBNull(1) ? null : reader.GetString(1);
                switch (key)
                {
                    case DbContext.IndexedProjectRootMetaKey:
                        projectRoot = value;
                        break;
                    case DbContext.IndexedHeadCommitMetaKey:
                        legacyHead = value;
                        break;
                    case DbContext.WorkspaceVerifiedHeadShaMetaKey:
                        workspaceVerifiedHead = value;
                        break;
                    case DbContext.IndexedHeadShaMetaKey:
                        latestHead = value;
                        break;
                    case DbContext.IndexedHeadBranchMetaKey:
                        latestBranch = value;
                        latestBranchStampPresent = true;
                        break;
                    case DbContext.IndexedHeadTimestampMetaKey:
                        latestTimestamp = value;
                        break;
                    case DbContext.IndexedHeadCommitBranchMetaKey:
                        legacyBranch = value;
                        legacyBranchStampPresent = true;
                        break;
                }
            }
        }
        catch (SqliteException)
        {
            // Legacy databases without metadata remain queryable.
            // metadata table を持たない legacy DB も引き続き query 可能にする。
        }

        return new RepoMapIndexedHeadSnapshot(
            projectRoot,
            legacyHead,
            workspaceVerifiedHead,
            latestHead,
            latestBranch,
            ParseIndexedHeadTimestamp(latestTimestamp),
            latestBranchStampPresent,
            legacyBranch,
            legacyBranchStampPresent);
    }

    private static DateTimeOffset? ParseIndexedHeadTimestamp(string? raw)
        => DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value.ToUniversalTime()
            : null;

    private static RepoFileSummaryResult CreateScoredFileSummary(RepoFileStat file)
    {
        var summary = CreateUnscoredFileSummary(file);
        summary.Score = (file.Lines * 1L) + (file.SymbolCount * 5L) + (file.ReferenceCount * 2L);
        return summary;
    }

    private static RepoFileSummaryResult CreateUnscoredFileSummary(RepoFileStat file)
    {
        return new RepoFileSummaryResult
        {
            Path = file.Path,
            Lang = file.Lang,
            Lines = file.Lines,
            Size = file.Size,
            SymbolCount = file.SymbolCount,
            ReferenceCount = file.ReferenceCount,
        };
    }

    private static RepoFileSummaryResult CopyUnscoredFileSummary(RepoFileSummaryResult file)
    {
        return new RepoFileSummaryResult
        {
            Path = file.Path,
            Lang = file.Lang,
            Lines = file.Lines,
            Size = file.Size,
            SymbolCount = file.SymbolCount,
            ReferenceCount = file.ReferenceCount,
        };
    }

    private static string GetModuleKey(RepoFileStat file, int? moduleDepth)
    {
        var moduleKey = GetNaturalModuleKey(file);
        if (!moduleDepth.HasValue)
            return moduleKey;
        if (moduleDepth.Value <= 0)
            return ".";

        var segments = moduleKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= moduleDepth.Value
            ? moduleKey
            : string.Join('/', segments.Take(moduleDepth.Value));
    }

    private static string GetNaturalModuleKey(RepoFileStat file)
    {
        if (!string.IsNullOrWhiteSpace(file.ModuleName))
            return file.ModuleName;

        var segments = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return ".";
        if (segments.Length == 1)
            return segments[0];

        return segments[0] switch
        {
            "src" or "app" or "lib" or "tests" or "test" or "docs" or "packages" when segments.Length >= 3 => $"{segments[0]}/{segments[1]}",
            "src" or "app" or "lib" or "tests" or "test" or "docs" or "packages" => segments[0],
            _ => segments[0],
        };
    }

    private static string? ResolveOwningJavaModuleName(string path, IReadOnlyDictionary<string, string> moduleByDescriptorPath)
    {
        var currentDirectory = GetParentDirectoryPath(path) ?? string.Empty;
        while (true)
        {
            var descriptorPath = string.IsNullOrEmpty(currentDirectory)
                ? "module-info.java"
                : $"{currentDirectory}/module-info.java";
            if (moduleByDescriptorPath.TryGetValue(descriptorPath, out var moduleName))
                return moduleName;

            if (string.IsNullOrEmpty(currentDirectory))
                return null;

            currentDirectory = GetParentDirectoryPath(currentDirectory) ?? string.Empty;
        }
    }

    private static string? GetParentDirectoryPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0)
            return string.Empty;

        return path[..lastSlash];
    }

    private static EntrypointScore ScoreEntrypoint(string path, string? lang, string kind, string name)
    {
        if (lang == null)
            return EntrypointScore.None;

        var score = 0;
        var nameRank = GetHintRank(EntrypointNameHints, lang, name);
        if (nameRank > 0)
            score += 4;

        var fileName = Path.GetFileName(path);
        var pathRank = GetHintRank(EntrypointPathHints, lang, fileName);
        if (pathRank > 0)
            score += 3;

        if (score == 0)
            return EntrypointScore.None;

        if (kind == "function")
            score += 1;

        if (kind == "class" && string.Equals(Path.GetFileNameWithoutExtension(fileName), name, StringComparison.OrdinalIgnoreCase))
            score += 1;

        var hasStrongPathHint = pathRank > 0 && nameRank > 0;
        score += GetPathLocationBoost(path, hasStrongPathHint);
        var matchType = pathRank > 0 && nameRank > 0
            ? "path+name"
            : pathRank > 0
                ? "path"
                : "name";
        var hintRank = pathRank > 0 && nameRank > 0
            ? Math.Min(pathRank, nameRank)
            : Math.Max(pathRank, nameRank);
        var confidence = pathRank > 0 && nameRank > 0
            ? 0.85
            : pathRank > 0
                ? 0.65
                : 0.5;

        return new EntrypointScore(score, matchType, NormalizeConfidence(confidence + GetPathLocationConfidenceBoost(path, hasStrongPathHint)), hintRank);
    }

    private static EntrypointScore ScoreEntrypointFileFallback(string path, string? lang, int symbolCount, int referenceCount)
    {
        if (lang == null)
            return EntrypointScore.None;

        var fileName = Path.GetFileName(path);
        var pathRank = GetHintRank(EntrypointPathHints, lang, fileName);
        if (pathRank <= 0)
        {
            return EntrypointScore.None;
        }

        var score = 2;
        if (symbolCount > 0)
            score += 1;
        if (referenceCount > 0)
            score += 1;

        score += GetFileFallbackPathLocationBoost(path);
        return new EntrypointScore(score, "path", NormalizeConfidence(0.4 + GetPathLocationConfidenceBoost(path, hasPathHint: true)), pathRank);
    }

    private static int GetHintRank(IReadOnlyDictionary<string, string[]> hintsByLang, string lang, string candidate)
    {
        if (!hintsByLang.TryGetValue(lang, out var hints))
            return 0;

        for (var i = 0; i < hints.Length; i++)
        {
            if (string.Equals(hints[i], candidate, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }

        return 0;
    }

    private static int GetPathLocationBoost(string path, bool hasPathHint)
    {
        if (IsTestOrFixturePath(path))
            return -4;
        if (IsToolingPath(path))
            return hasPathHint ? -3 : -2;
        if (IsSupportEntrypointPath(path))
            return hasPathHint ? -2 : -1;

        var slashCount = path.Count(ch => ch == '/');
        if (slashCount == 0)
            return hasPathHint ? 3 : 1;
        if (IsProductionEntrypointPath(path))
            return hasPathHint ? 5 : 1;

        return 0;
    }

    private static int GetFileFallbackPathLocationBoost(string path)
    {
        var boost = GetPathLocationBoost(path, hasPathHint: true);
        if (IsTestOrFixturePath(path))
            return Math.Max(boost, -1);
        if (IsSupportEntrypointPath(path))
            return Math.Max(boost, -1);

        return IsToolingPath(path) ? Math.Max(boost, -1) : boost;
    }

    private static double GetPathLocationConfidenceBoost(string path, bool hasPathHint)
    {
        if (IsTestOrFixturePath(path))
            return -0.2;
        if (IsToolingPath(path))
            return -0.15;
        if (IsSupportEntrypointPath(path))
            return -0.1;

        var slashCount = path.Count(ch => ch == '/');
        if (slashCount == 0)
            return hasPathHint ? 0.2 : 0.05;
        if (IsProductionEntrypointPath(path))
            return hasPathHint ? 0.35 : 0.05;

        return 0;
    }

    private static bool IsProductionEntrypointPath(string path)
    {
        return path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("app/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("cmd/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("bin/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsToolingPath(string path)
    {
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsToolingPathSegment(segment))
                return true;
        }

        return false;
    }

    private static bool IsToolingPathSegment(string segment)
    {
        return segment.Equals("tool", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("tools", StringComparison.OrdinalIgnoreCase) ||
               segment.StartsWith("tool.", StringComparison.OrdinalIgnoreCase) ||
               segment.StartsWith("tools.", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith(".tool", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith(".tools", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith("_tool", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith("_tools", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith("-tool", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith("-tools", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains(".tool.", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains(".tools.", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains("_tool.", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains("_tools.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportEntrypointPath(string path)
    {
        return path.StartsWith(".", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/.", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("install.sh", StringComparison.OrdinalIgnoreCase) ||
               HasPathSegment(path, "docs") ||
               HasPathSegment(path, "scripts") ||
               HasPathSegment(path, "install_modules") ||
               HasPathSegment(path, "workflow") ||
               HasPathSegment(path, "workflows");
    }

    private static bool IsTestOrFixturePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsTestOrFixturePathSegment(segment))
                return true;
        }

        return false;
    }

    private static bool IsTestOrFixturePathSegment(string segment)
    {
        return IsTestPathSegment(segment) ||
               IsFixturePathSegment(segment) ||
               segment.Equals("conftest.py", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestPathSegment(string segment)
    {
        return segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("spec", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("specs", StringComparison.OrdinalIgnoreCase) ||
               segment.StartsWith("test.", StringComparison.OrdinalIgnoreCase) ||
               segment.StartsWith("tests.", StringComparison.OrdinalIgnoreCase) ||
               segment.StartsWith("spec.", StringComparison.OrdinalIgnoreCase) ||
               segment.StartsWith("specs.", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith(".tests", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith(".spec", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith(".specs", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith("_test", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith("_tests", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains(".tests.", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains(".spec.", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains(".specs.", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains("_test.", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains("_tests.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFixturePathSegment(string segment)
    {
        return segment.Equals("fixture", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("fixtures", StringComparison.OrdinalIgnoreCase) ||
               segment.StartsWith("fixture.", StringComparison.OrdinalIgnoreCase) ||
               segment.StartsWith("fixtures.", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith(".fixture", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith(".fixtures", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith("_fixture", StringComparison.OrdinalIgnoreCase) ||
               segment.EndsWith("_fixtures", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains(".fixture.", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains(".fixtures.", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains("_fixture.", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains("_fixtures.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPathSegment(string path, string segment)
    {
        foreach (var candidate in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(candidate, segment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void ApplyEntrypointAmbiguityPenalty(List<RepoEntrypointResult> results)
    {
        foreach (var group in results.GroupBy(result => $"{result.Lang ?? ""}\0{result.MatchType}\0{result.Name}", StringComparer.OrdinalIgnoreCase))
        {
            var count = group.Count();
            if (count <= 1)
                continue;

            var penalty = Math.Min(0.3, 0.1 * (count - 1));
            foreach (var result in group)
                result.Confidence = NormalizeConfidence(Math.Max(0.2, result.Confidence - penalty));
        }
    }

    private static double NormalizeConfidence(double confidence) => Math.Round(Math.Clamp(confidence, 0.0, 1.0), 3);

    private readonly record struct EntrypointScore(int Score, string MatchType, double Confidence, int HintRank)
    {
        public static EntrypointScore None { get; } = new(0, "", 0, 0);
    }

    private sealed class RepoMapAggregate
    {
        public int FileCount { get; set; }
        public long TotalLines { get; set; }
        public long TotalSymbols { get; set; }
        public long TotalReferences { get; set; }
        public DateTime? IndexedAt { get; set; }
        public DateTime? LatestModified { get; set; }
        public required Dictionary<string, RepoLanguageResult> Languages { get; init; }
        public required Dictionary<string, RepoModuleResult> Modules { get; init; }
        public required List<RepoFileSummaryResult> TopFiles { get; init; }
        public required List<RepoFileSummaryResult> LargestFiles { get; init; }
        public required List<RepoFileSummaryResult> SymbolRichFiles { get; init; }
        public required List<RepoFileSummaryResult> ReferenceRichFiles { get; init; }
        public required List<RepoEntrypointResult> EntrypointFallbacks { get; init; }
        public int IssueDraftCandidateCount { get; set; }
        public required List<RepoFileSummaryResult> IssueDraftCandidates { get; init; }
    }
}
