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
    private readonly SqliteConnection _conn;
    private readonly HashSet<string> _fileColumns;

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

    public RepoMapBuilder(SqliteConnection connection, HashSet<string> fileColumns, bool hasReferencesTable)
    {
        _conn = connection;
        _fileColumns = fileColumns;
        _hasReferencesTable = hasReferencesTable;
    }

    /// <summary>
    /// Build a repo-level overview to help AI clients orient before deep queries.
    /// 深掘り前の把握に使うリポジトリ俯瞰情報を構築する。
    /// </summary>
    public RepoMapResult Build(int limit, string? lang, IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns, bool excludeTests, double minEntrypointConfidence,
        Func<(DateTime? IndexedAt, DateTime? LatestModified)> getFreshness)
    {
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
        var javaModuleDescriptors = LoadJavaModuleDescriptors();
        var aggregate = BuildAggregate(
            EnumerateFileStats(lang, pathPatterns, excludePathPatterns, excludeTests),
            Math.Max(limit, 0),
            javaModuleDescriptors);
        var freshness = getFreshness();
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
            Languages = BuildLanguageResults(aggregate.Languages, limit),
            Modules = BuildModuleResults(aggregate.Modules, limit),
            TopFiles = aggregate.TopFiles,
            LargestFiles = BuildLargestFileResults(aggregate.LargestFiles),
            SymbolRichFiles = BuildSymbolRichFileResults(aggregate.SymbolRichFiles),
            ReferenceRichFiles = BuildReferenceRichFileResults(aggregate.ReferenceRichFiles),
            Entrypoints = GetEntrypoints(aggregate.EntrypointFallbacks, limit, lang, pathPatterns, excludePathPatterns, excludeTests, minEntrypointConfidence),
            GraphTableAvailable = _hasReferencesTable,
        };
        txn.Commit();
        return result;
    }

    private IEnumerable<RepoFileStat> EnumerateFileStats(string? lang, IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
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
        sql += " ORDER BY f.path";

        cmd.CommandText = sql;
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        DbReader.AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

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

    private static RepoMapAggregate BuildAggregate(
        IEnumerable<RepoFileStat> fileStats,
        int limit,
        IReadOnlyDictionary<string, string> moduleByDescriptorPath)
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

            var languageKey = file.Lang ?? "unknown";
            if (!languages.TryGetValue(languageKey, out var language))
            {
                language = new RepoLanguageResult { Lang = languageKey };
                languages.Add(languageKey, language);
            }

            AddFileStats(language, file);

            var moduleKey = GetModuleKey(file);
            if (!modules.TryGetValue(moduleKey, out var module))
            {
                module = new RepoModuleResult { Module = moduleKey };
                modules.Add(moduleKey, module);
            }

            AddFileStats(module, file);

            var scoredSummary = CreateScoredFileSummary(file);
            AddBounded(aggregate.TopFiles, scoredSummary, limit, CompareTopFiles);
            AddBounded(aggregate.LargestFiles, scoredSummary, limit, CompareLargestFiles);
            AddBounded(aggregate.SymbolRichFiles, scoredSummary, limit, CompareSymbolRichFiles);
            AddBounded(aggregate.ReferenceRichFiles, scoredSummary, limit, CompareReferenceRichFiles);

            var fallback = ScoreEntrypointFileFallback(file.Path, file.Lang, file.SymbolCount, file.ReferenceCount);
            if (fallback.Score > 0)
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

    private static List<RepoLanguageResult> BuildLanguageResults(IReadOnlyDictionary<string, RepoLanguageResult> languages, int limit)
    {
        return languages.Values
            .OrderByDescending(group => group.Files)
            .ThenBy(group => group.Lang)
            .Take(limit)
            .ToList();
    }

    private static List<RepoModuleResult> BuildModuleResults(IReadOnlyDictionary<string, RepoModuleResult> modules, int limit)
    {
        return modules.Values
            .OrderByDescending(group => group.References)
            .ThenByDescending(group => group.Symbols)
            .ThenByDescending(group => group.Lines)
            .ThenBy(group => group.Module)
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

    private List<RepoEntrypointResult> GetEntrypoints(IReadOnlyList<RepoEntrypointResult> fallbackEntrypoints, int limit,
        string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        double minConfidence)
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
        sql += " ORDER BY f.path, s.line";

        cmd.CommandText = sql;
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        DbReader.AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

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

        // Fall back to known entry files when symbol extraction misses entrypoints
        // シンボル抽出がエントリポイントを捉えられない場合、既知のエントリファイルにフォールバック
        var filesWithEntrypoints = results
            .Select(result => result.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var fallback in fallbackEntrypoints)
        {
            if (filesWithEntrypoints.Contains(fallback.Path))
                continue;
            results.Add(fallback);
        }

        ApplyEntrypointAmbiguityPenalty(results);
        return results
            .Where(result => result.Confidence >= minConfidence)
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Confidence)
            .ThenBy(result => result.HintRank)
            .ThenBy(result => result.Path)
            .ThenBy(result => result.Line)
            .Take(limit)
            .ToList();
    }

    private string GetFileColumnSql(string columnName)
    {
        return _fileColumns.Contains(columnName) ? $"f.{columnName}" : "NULL";
    }

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

    private static string GetModuleKey(RepoFileStat file)
    {
        if (!string.IsNullOrWhiteSpace(file.ModuleName))
        {
            return file.ModuleName;
        }

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

        score += GetPathLocationBoost(path);
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

        return new EntrypointScore(score, matchType, NormalizeConfidence(confidence + GetPathLocationConfidenceBoost(path)), hintRank);
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

        score += GetPathLocationBoost(path);
        return new EntrypointScore(score, "path", NormalizeConfidence(0.4 + GetPathLocationConfidenceBoost(path)), pathRank);
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

    private static int GetPathLocationBoost(string path)
    {
        var slashCount = path.Count(ch => ch == '/');
        if (slashCount == 0)
            return 2;
        if (path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("app/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("cmd/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
            return 1;

        return 0;
    }

    private static double GetPathLocationConfidenceBoost(string path)
    {
        var slashCount = path.Count(ch => ch == '/');
        if (slashCount == 0)
            return 0.1;
        if (path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("app/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("cmd/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
            return 0.05;

        return 0;
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

    private static double NormalizeConfidence(double confidence) => Math.Round(Math.Min(confidence, 1.0), 3);

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
    }
}
