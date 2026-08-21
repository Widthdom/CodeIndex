using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    // Index-mode flag names recognized by `ParseArgs`. Kept in sync with the switch above
    // so unknown option errors can suggest the closest accepted flag (#1582). Easter-egg
    // and random-spinner flags are excluded since they are intentionally undiscoverable.
    // `ParseArgs` の switch と同期した index 系の受理フラグ一覧。`unknown option` error で
    // 最も近い受理フラグを did-you-mean 提案するのに用いる (#1582)。
    // easter egg や random-spinner は意図的に未公開なので除外する。
    private static readonly string[] AcceptedIndexFlags =
    [
        "--db", "--data-dir", "--rebuild", "--verbose", "--json", "--quiet", "--dry-run", "--dry-run-path-limit", "--force",
        "--yes", "--watch", "--debounce", "--watch-pending-path-limit", "--duration-format", "--max-file-bytes", "--max-symbols-per-file",
        "--max-references-per-file", "--allow-partial", "--notify",
        "--parallelism", "--memory-trace", "--follow-symlinks", "--symbols-only",
        "--commits", "--changed-between", "--files", "--solution", "--project",
        "--include-symbol-kind", "--exclude-symbol-kind", "--optimize", "--help",
        "--read-only", "--immutable",
    ];

    internal const string CompletionNotificationEnvironmentVariable = "CDIDX_NOTIFY";
    internal const string IndexParallelismEnvironmentVariable = "CDIDX_INDEX_PARALLELISM";
    internal const string WatchPendingPathLimitEnvironmentVariable = "CDIDX_INDEX_WATCH_PENDING_PATH_LIMIT";
    internal const int DefaultIndexParallelismCap = 8;
    internal const int MaxIndexParallelism = 16;
    internal const int MaxSymbolKindFilterCsvLength = 2048;
    internal const int MaxSymbolKindFilterCsvEntries = 128;

    public static IndexCommandOptions ParseArgs(string[] args)
    {
        string? projectPath = null;
        string? dbPath = null;
        string? dataDir = null;
        bool rebuild = false;
        bool verbose = false;
        bool json = false;
        bool quiet = false;
        bool allowPartial = false;
        bool dryRun = false;
        var dryRunPathLimit = DefaultDryRunPathLimit;
        bool force = false;
        bool readOnly = false;
        bool yes = false;
        bool watch = false;
        bool optimizeOnly = false;
        bool showPaths = false;
        bool symbolsOnly = false;
        bool memoryTrace = false;
        int? watchDebounceMs = null;
        var optionWarnings = new List<CliJsonMessage>();
        var watchPendingPathLimit = ReadWatchPendingPathLimitFromEnvironment(optionWarnings);
        var durationFormat = DurationOutputFormat.Auto;
        var notifyMode = ReadCompletionNotificationModeFromEnvironment();
        long? maxFileSizeBytes = ReadMaxFileSizeBytesFromEnvironment(optionWarnings);
        var maxSymbolsPerFile = DefaultMaxSymbolsPerFile;
        var maxReferencesPerFile = DefaultMaxReferencesPerFile;
        var parallelism = ReadIndexParallelismFromEnvironment(optionWarnings);
        var watchPendingPathLimitSpecifiedOnCli = false;
        var maxFileSizeBytesSpecifiedOnCli = false;
        var parallelismSpecifiedOnCli = false;
        var symlinkPolicy = FileIndexer.SymlinkPolicy.None;
        string? easterEgg = null;
        int spinnerFlagCount = 0;
        bool randomSpinner = false;
        var commits = new List<string>();
        var changedBetweenRefs = new List<string>();
        var changedBetweenSpecified = false;
        var updateFiles = new List<string>();
        var explicitFiles = new List<string>();
        var explicitFilesSpecified = false;
        var projectFilters = new List<string>();
        string? solutionPath = null;
        string? projectFilterError = null;
        string? parseError = null;
        var includeSymbolKinds = new List<string>();
        var excludeSymbolKinds = new List<string>();
        string? symbolKindFilterError = null;
        var includeSymbolKindsSpecifiedOnCli = false;
        var excludeSymbolKindsSpecifiedOnCli = false;
        string? generatedCodePatternError = null;
        var generatedCodePatterns = ReadGeneratedCodePatternsFromEnvironment(ref generatedCodePatternError);

        AddSymbolKindFilterValues(
            IncludeSymbolKindsEnvironmentVariable,
            CdidxEnvironment.GetEnvironmentVariable(IncludeSymbolKindsEnvironmentVariable),
            includeSymbolKinds,
            ref symbolKindFilterError);
        AddSymbolKindFilterValues(
            ExcludeSymbolKindsEnvironmentVariable,
            CdidxEnvironment.GetEnvironmentVariable(ExcludeSymbolKindsEnvironmentVariable),
            excludeSymbolKinds,
            ref symbolKindFilterError);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
                case var option when option.StartsWith("--data-dir=", StringComparison.Ordinal):
                    dataDir = option["--data-dir=".Length..];
                    break;
                case "--rebuild":
                    rebuild = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--allow-partial":
                    allowPartial = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--dry-run-path-limit" when i + 1 < args.Length:
                    dryRunPathLimit = ParseDryRunPathLimit(args[++i], dryRunPathLimit, "--dry-run-path-limit", ref parseError);
                    break;
                case var option when option.StartsWith("--dry-run-path-limit=", StringComparison.Ordinal):
                    dryRunPathLimit = ParseDryRunPathLimit(option["--dry-run-path-limit=".Length..], dryRunPathLimit, "--dry-run-path-limit", ref parseError);
                    break;
                case "--force":
                    force = true;
                    break;
                case "--read-only":
                case "--immutable":
                    readOnly = true;
                    parseError ??= $"{args[i]} is only supported by query commands; index mutates the database and cannot run read-only";
                    break;
                case "--yes":
                    yes = true;
                    break;
                case "--watch":
                    watch = true;
                    break;
                case "--optimize":
                    optimizeOnly = true;
                    break;
                case "--show-paths":
                    showPaths = true;
                    break;
                case "--symbols-only":
                    symbolsOnly = true;
                    break;
                case "--memory-trace":
                    memoryTrace = true;
                    break;
                case "--debounce" when i + 1 < args.Length:
                    watchDebounceMs = ParseWatchDebounce(args[++i], watchDebounceMs, ref parseError);
                    break;
                case "--watch-pending-path-limit" when i + 1 < args.Length:
                    watchPendingPathLimitSpecifiedOnCli = true;
                    watchPendingPathLimit = ParseWatchPendingPathLimit(args[++i], watchPendingPathLimit, "--watch-pending-path-limit", ref parseError);
                    break;
                case var option when option.StartsWith("--watch-pending-path-limit=", StringComparison.Ordinal):
                    watchPendingPathLimitSpecifiedOnCli = true;
                    watchPendingPathLimit = ParseWatchPendingPathLimit(option["--watch-pending-path-limit=".Length..], watchPendingPathLimit, "--watch-pending-path-limit", ref parseError);
                    break;
                case "--duration-format" when i + 1 < args.Length:
                    durationFormat = ParseDurationFormat(args[++i], durationFormat);
                    break;
                case var option when option.StartsWith("--duration-format=", StringComparison.Ordinal):
                    durationFormat = ParseDurationFormat(option["--duration-format=".Length..], durationFormat);
                    break;
                case "--notify" when i + 1 < args.Length:
                    notifyMode = ParseCompletionNotificationMode(args[++i], notifyMode, ref parseError);
                    break;
                case var option when option.StartsWith("--notify=", StringComparison.Ordinal):
                    notifyMode = ParseCompletionNotificationMode(option["--notify=".Length..], notifyMode, ref parseError);
                    break;
                case "--max-file-bytes" when i + 1 < args.Length:
                    maxFileSizeBytesSpecifiedOnCli = true;
                    maxFileSizeBytes = ParseMaxFileBytes(args[++i], maxFileSizeBytes, ref parseError);
                    break;
                case var option when option.StartsWith("--max-file-bytes=", StringComparison.Ordinal):
                    maxFileSizeBytesSpecifiedOnCli = true;
                    maxFileSizeBytes = ParseMaxFileBytes(option["--max-file-bytes=".Length..], maxFileSizeBytes, ref parseError);
                    break;
                case "--max-symbols-per-file" when i + 1 < args.Length:
                    maxSymbolsPerFile = ParseMaxSymbolsPerFile(args[++i], maxSymbolsPerFile, "--max-symbols-per-file", ref parseError);
                    break;
                case var option when option.StartsWith("--max-symbols-per-file=", StringComparison.Ordinal):
                    maxSymbolsPerFile = ParseMaxSymbolsPerFile(option["--max-symbols-per-file=".Length..], maxSymbolsPerFile, "--max-symbols-per-file", ref parseError);
                    break;
                case "--max-references-per-file" when i + 1 < args.Length:
                    maxReferencesPerFile = ParseMaxReferencesPerFile(args[++i], maxReferencesPerFile, "--max-references-per-file", ref parseError);
                    break;
                case var option when option.StartsWith("--max-references-per-file=", StringComparison.Ordinal):
                    maxReferencesPerFile = ParseMaxReferencesPerFile(option["--max-references-per-file=".Length..], maxReferencesPerFile, "--max-references-per-file", ref parseError);
                    break;
                case "--parallelism" when i + 1 < args.Length:
                    parallelismSpecifiedOnCli = true;
                    parallelism = ParseIndexParallelism(args[++i], parallelism, "--parallelism", ref parseError);
                    break;
                case var option when option.StartsWith("--parallelism=", StringComparison.Ordinal):
                    parallelismSpecifiedOnCli = true;
                    parallelism = ParseIndexParallelism(option["--parallelism=".Length..], parallelism, "--parallelism", ref parseError);
                    break;
                case "--follow-symlinks" when i + 1 < args.Length:
                    symlinkPolicy = ParseSymlinkPolicy(args[++i], symlinkPolicy, ref parseError);
                    break;
                case var option when option.StartsWith("--follow-symlinks=", StringComparison.Ordinal):
                    symlinkPolicy = ParseSymlinkPolicy(option["--follow-symlinks=".Length..], symlinkPolicy, ref parseError);
                    break;
                case "--commits":
                    while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    {
                        var commit = args[++i];
                        AddCommitRef(commit, commits, ref parseError);
                    }
                    if (commits.Count == 0)
                        CommandErrorWriter.WriteStderr("Warning: --commits specified but no commit refs provided / --commits が指定されましたがコミットrefがありません");
                    break;
                case "--changed-between":
                    changedBetweenSpecified = true;
                    while (i + 1 < args.Length && !args[i + 1].StartsWith('-') && changedBetweenRefs.Count < 2)
                        changedBetweenRefs.Add(args[++i]);
                    if (changedBetweenRefs.Count != 2)
                        CommandErrorWriter.WriteStderr("Warning: --changed-between requires exactly two refs / --changed-between は2つのrefが必要です");
                    break;
                case "--solution" when i + 1 < args.Length:
                    solutionPath = args[++i];
                    break;
                case var option when option.StartsWith("--solution=", StringComparison.Ordinal):
                    solutionPath = option["--solution=".Length..];
                    break;
                case "--project" when i + 1 < args.Length:
                    projectFilters.Add(args[++i]);
                    break;
                case var option when option.StartsWith("--project=", StringComparison.Ordinal):
                    projectFilters.Add(option["--project=".Length..]);
                    break;
                case "--include-symbol-kind" when i + 1 < args.Length:
                    if (!includeSymbolKindsSpecifiedOnCli)
                    {
                        includeSymbolKinds.Clear();
                        includeSymbolKindsSpecifiedOnCli = true;
                    }
                    AddSymbolKindFilterValues("--include-symbol-kind", args[++i], includeSymbolKinds, ref symbolKindFilterError);
                    break;
                case var option when option.StartsWith("--include-symbol-kind=", StringComparison.Ordinal):
                    if (!includeSymbolKindsSpecifiedOnCli)
                    {
                        includeSymbolKinds.Clear();
                        includeSymbolKindsSpecifiedOnCli = true;
                    }
                    AddSymbolKindFilterValues("--include-symbol-kind", option["--include-symbol-kind=".Length..], includeSymbolKinds, ref symbolKindFilterError);
                    break;
                case "--exclude-symbol-kind" when i + 1 < args.Length:
                    if (!excludeSymbolKindsSpecifiedOnCli)
                    {
                        excludeSymbolKinds.Clear();
                        excludeSymbolKindsSpecifiedOnCli = true;
                    }
                    AddSymbolKindFilterValues("--exclude-symbol-kind", args[++i], excludeSymbolKinds, ref symbolKindFilterError);
                    break;
                case var option when option.StartsWith("--exclude-symbol-kind=", StringComparison.Ordinal):
                    if (!excludeSymbolKindsSpecifiedOnCli)
                    {
                        excludeSymbolKinds.Clear();
                        excludeSymbolKindsSpecifiedOnCli = true;
                    }
                    AddSymbolKindFilterValues("--exclude-symbol-kind", option["--exclude-symbol-kind=".Length..], excludeSymbolKinds, ref symbolKindFilterError);
                    break;
                case "--files":
                    {
                        explicitFilesSpecified = true;
                        var explicitFileCountBefore = explicitFiles.Count;
                        while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        {
                            var file = args[++i];
                            explicitFiles.Add(file);
                            updateFiles.Add(file);
                        }
                        if (explicitFiles.Count == explicitFileCountBefore)
                            parseError ??= "--files requires at least one file path";
                        break;
                    }
                case "--help" or "-h":
                    return new IndexCommandOptions
                    {
                        ShowHelp = true,
                        ExplicitFilesSpecified = explicitFilesSpecified,
                        ExplicitFiles = explicitFiles,
                    };
                case "--sushi" or "--coffee" or "--ramen" or "--wine" or "--beer" or "--matcha" or "--whisky":
                    easterEgg = args[i];
                    spinnerFlagCount++;
                    break;
                case "--random-spinner":
                    randomSpinner = true;
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        parseError ??= BuildUnknownIndexOptionError(args[i]);
                    }
                    else
                        projectPath = args[i];
                    break;
            }
        }

        if (projectFilters.Count > 0 && projectPath != null)
        {
            try
            {
                updateFiles.AddRange(SolutionProjectResolver.ResolveProjectFiles(projectPath, projectFilters, solutionPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                projectFilterError = CommandErrorWriter.FormatSanitizedExceptionMessage(ex);
            }
        }

        if (spinnerFlagCount > 1)
        {
            CommandErrorWriter.WriteStderr("\U0001f375 Simultaneous intake of beer and coffee is not recommended. How about some matcha instead?");
            CommandErrorWriter.WriteStderr("   \u30d3\u30fc\u30eb\u3068\u30b3\u30fc\u30d2\u30fc\u306e\u540c\u6642\u6442\u53d6\u306f\u304a\u3059\u3059\u3081\u3057\u307e\u305b\u3093\u3002\u62b9\u8336\u306f\u3044\u304b\u304c\uff1f");
            easterEgg = "--matcha";
        }

        if (randomSpinner && easterEgg == null)
        {
            var themes = new[] { "--sushi", "--coffee", "--ramen", "--wine", "--beer", "--matcha", "--whisky" };
            easterEgg = themes[Random.Shared.Next(themes.Length)];
        }
        if (showPaths && !optimizeOnly)
            parseError ??= "--show-paths is only valid with `cdidx index <projectPath> --optimize`.";

        RemoveOverriddenEnvironmentWarning(optionWarnings, WatchPendingPathLimitEnvironmentVariable, watchPendingPathLimitSpecifiedOnCli);
        RemoveOverriddenEnvironmentWarning(optionWarnings, FileIndexer.MaxFileSizeEnvironmentVariable, maxFileSizeBytesSpecifiedOnCli);
        RemoveOverriddenEnvironmentWarning(optionWarnings, IndexParallelismEnvironmentVariable, parallelismSpecifiedOnCli);
        if (optimizeOnly)
        {
            // Optimize does not consume indexing worker, file-size, or watch queue settings.
            // Do not emit fallback warnings for environment values that cannot affect this mode.
            // optimize は indexing worker / file size / watch queue 設定を使用しないため、
            // この mode に影響しない環境変数の fallback warning は出力しない。
            optionWarnings.Clear();
        }

        var finalParseError = parseError ?? generatedCodePatternError;
        if (finalParseError != null)
        {
            optionWarnings.Clear();
        }
        else
        {
            foreach (var warning in optionWarnings)
                CommandErrorWriter.WriteStderr($"Warning: {warning.Message}");
        }

        return new IndexCommandOptions
        {
            // Absolutize critical paths at the option-parsing boundary so a cwd shift after
            // this point (embedded host, signal handler, future plugin) cannot silently break
            // relative-path math in FileIndexer / GitHelper / DbPathResolver. Issue #1577.
            // オプション解析の境界で絶対化し、以降の cwd 変化で相対パス計算が崩れないようにする。
            ProjectPath = AbsolutizePathOption(projectPath),
            DbPath = AbsolutizeDbPathOption(dbPath),
            DataDir = AbsolutizePathOption(dataDir),
            Rebuild = rebuild,
            Verbose = verbose,
            Json = json,
            Quiet = quiet,
            AllowPartial = allowPartial,
            Commits = commits,
            ChangedBetweenSpecified = changedBetweenSpecified,
            ChangedBetweenRefs = changedBetweenRefs,
            UpdateFiles = updateFiles,
            ExplicitFilesSpecified = explicitFilesSpecified,
            ExplicitFiles = explicitFiles,
            ProjectFilters = projectFilters,
            SolutionPath = solutionPath,
            ProjectFilterError = projectFilterError,
            ParseError = finalParseError,
            OptionWarnings = optionWarnings,
            EasterEgg = easterEgg,
            DryRun = dryRun,
            DryRunPathLimit = dryRunPathLimit,
            Force = force,
            ReadOnly = readOnly,
            Yes = yes,
            Watch = watch,
            OptimizeOnly = optimizeOnly,
            ShowPaths = showPaths,
            SymbolsOnly = symbolsOnly,
            MemoryTrace = memoryTrace,
            WatchDebounceMs = watchDebounceMs,
            WatchPendingPathLimit = watchPendingPathLimit,
            DurationFormat = durationFormat,
            NotifyMode = notifyMode,
            MaxFileSizeBytes = maxFileSizeBytes,
            MaxSymbolsPerFile = maxSymbolsPerFile,
            MaxReferencesPerFile = maxReferencesPerFile,
            Parallelism = parallelism,
            SymlinkPolicy = symlinkPolicy,
            SymbolKindFilter = SymbolKindFilter.Create(includeSymbolKinds, excludeSymbolKinds, symbolKindFilterError),
            GeneratedCodePatterns = generatedCodePatterns,
        };
    }

    internal static IReadOnlyList<string> ReadGeneratedCodePatternsFromEnvironment()
    {
        string? parseError = null;
        return ReadGeneratedCodePatternsFromEnvironment(ref parseError);
    }

    private static IReadOnlyList<string> ReadGeneratedCodePatternsFromEnvironment(ref string? parseError)
    {
        var value = CdidxEnvironment.GetEnvironmentVariable(GeneratedCodePatternsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return [];
        if (!ValidateCsvBounds(GeneratedCodePatternsEnvironmentVariable, value, MaxGeneratedCodePatternCsvLength, MaxGeneratedCodePatternCount, ref parseError))
            return [];

        var patterns = new List<string>();
        foreach (var raw in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                parseError ??= $"{GeneratedCodePatternsEnvironmentVariable} contains an empty generated-code pattern";
                continue;
            }

            patterns.Add(raw);
        }

        return patterns;
    }

    private static FileIndexer.SymlinkPolicy ParseSymlinkPolicy(string value, FileIndexer.SymlinkPolicy fallback, ref string? parseError)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "none":
                return FileIndexer.SymlinkPolicy.None;
            case "internal":
                return FileIndexer.SymlinkPolicy.Internal;
            case "all":
                return FileIndexer.SymlinkPolicy.All;
            default:
                parseError ??= $"invalid --follow-symlinks value '{ConsoleUi.FormatBoundedValue(value)}': expected none, internal, or all";
                return fallback;
        }
    }

    private static string BuildUnknownIndexOptionError(string token)
    {
        var name = TrimInlineValue(token);
        var suggestion = ConsoleUi.FindClosestMatch(name, AcceptedIndexFlags);
        var displayToken = ConsoleUi.FormatBoundedValue(token);
        return suggestion == null
            ? $"unknown option '{displayToken}'"
            : $"unknown option '{displayToken}'\nDid you mean: {suggestion}?";
    }

    private static string TrimInlineValue(string token)
    {
        var eq = token.IndexOf('=');
        return eq < 0 ? token : token[..eq];
    }

    private static void AddSymbolKindFilterValues(string source, string? value, List<string> target, ref string? parseError)
    {
        if (value == null)
            return;
        if (!ValidateCsvBounds(source, value, MaxSymbolKindFilterCsvLength, MaxSymbolKindFilterCsvEntries, ref parseError))
            return;

        foreach (var raw in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                parseError ??= $"{source} contains an empty symbol kind";
                continue;
            }

            target.Add(raw);
        }
    }

    private static bool ValidateCsvBounds(
        string source,
        string value,
        int maxLength,
        int maxEntries,
        ref string? parseError)
    {
        if (value.Length > maxLength)
        {
            parseError ??= $"{source} value is too long ({value.Length} characters; max {maxLength})";
            return false;
        }

        var entries = CountCsvEntries(value);
        if (entries > maxEntries)
        {
            parseError ??= $"{source} accepts at most {maxEntries} comma-separated entries";
            return false;
        }

        return true;
    }

    private static int CountCsvEntries(string value)
    {
        if (value.Length == 0)
            return 0;

        var count = 1;
        foreach (var ch in value)
        {
            if (ch == ',')
                count++;
        }

        return count;
    }

    private static void AddCommitRef(string commit, List<string> commits, ref string? parseError)
    {
        if (commits.Count >= MaxCommitRefCount)
        {
            parseError ??= $"--commits accepts at most {MaxCommitRefCount} commit refs";
            return;
        }

        if (commit.Length > MaxCommitRefLength)
        {
            parseError ??= $"--commits commit ref is too long ({commit.Length} characters; max {MaxCommitRefLength})";
            return;
        }

        commits.Add(commit);
    }

    internal static int DefaultIndexParallelism()
        => CalculateDefaultIndexParallelism(Environment.ProcessorCount);

    internal static int CalculateDefaultIndexParallelism(int processorCount)
        => Math.Clamp(processorCount, 1, DefaultIndexParallelismCap);

    private static int ReadIndexParallelismFromEnvironment(List<CliJsonMessage> warnings)
    {
        var fallback = DefaultIndexParallelism();
        var value = CdidxEnvironment.GetProcessEnvironmentVariable(IndexParallelismEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 1)
        {
            if (parsed <= MaxIndexParallelism)
                return parsed;

            AddEnvironmentOptionWarning(
                warnings,
                IndexParallelismEnvironmentVariable,
                value,
                $"must be between 1 and {MaxIndexParallelism} inclusive; using maximum clamp {MaxIndexParallelism}",
                $"1 以上 {MaxIndexParallelism} 以下である必要があります。上限補正値 {MaxIndexParallelism} を使用します");
            return MaxIndexParallelism;
        }

        AddEnvironmentOptionWarning(
            warnings,
            IndexParallelismEnvironmentVariable,
            value,
            $"must be between 1 and {MaxIndexParallelism} inclusive; using automatic CPU default {fallback}",
            $"1 以上 {MaxIndexParallelism} 以下である必要があります。CPU 数から算出した既定値 {fallback} を使用します");
        return fallback;
    }

    private static int ParseIndexParallelism(string value, int fallback, string source, ref string? parseError)
        => ParseIndexNumericOption(value, fallback, source, 1, MaxIndexParallelism, ref parseError);

    private static int ReadWatchPendingPathLimitFromEnvironment(List<CliJsonMessage> warnings)
    {
        var fallback = IndexWatchRunner.DefaultWatchPendingPathLimit;
        var value = CdidxEnvironment.GetEnvironmentVariable(WatchPendingPathLimitEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            if (parsed <= IndexWatchRunner.MaxWatchPendingPathLimit)
                return parsed;

            AddEnvironmentOptionWarning(
                warnings,
                WatchPendingPathLimitEnvironmentVariable,
                value,
                $"must be between 1 and {IndexWatchRunner.MaxWatchPendingPathLimit} inclusive; using maximum clamp {IndexWatchRunner.MaxWatchPendingPathLimit}",
                $"1 以上 {IndexWatchRunner.MaxWatchPendingPathLimit} 以下である必要があります。上限補正値 {IndexWatchRunner.MaxWatchPendingPathLimit} を使用します");
            return IndexWatchRunner.MaxWatchPendingPathLimit;
        }

        AddEnvironmentOptionWarning(
            warnings,
            WatchPendingPathLimitEnvironmentVariable,
            value,
            $"must be between 1 and {IndexWatchRunner.MaxWatchPendingPathLimit} inclusive; using built-in default {fallback}",
            $"1 以上 {IndexWatchRunner.MaxWatchPendingPathLimit} 以下である必要があります。組み込み既定値 {fallback} を使用します");
        return fallback;
    }

    private static int ParseWatchPendingPathLimit(string value, int fallback, string source, ref string? parseError)
    {
        return ParseIndexNumericOption(value, fallback, source, 1, IndexWatchRunner.MaxWatchPendingPathLimit, ref parseError);
    }

    private static int ParseDryRunPathLimit(string value, int fallback, string source, ref string? parseError)
    {
        return ParseIndexNumericOption(value, fallback, source, 1, MaxDryRunPathLimit, ref parseError);
    }

    private static long? ReadMaxFileSizeBytesFromEnvironment(List<CliJsonMessage> warnings)
    {
        var value = CdidxEnvironment.GetProcessEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (FileIndexer.TryParseMaxFileSizeBytes(value, out var parsed))
            return parsed;

        AddEnvironmentOptionWarning(
            warnings,
            FileIndexer.MaxFileSizeEnvironmentVariable,
            value,
            $"must be between 1 and {int.MaxValue} bytes inclusive, with an optional B/K/M/G suffix; using built-in default {FileIndexer.DefaultMaxFileSizeBytes} bytes",
            $"B/K/M/G 接尾辞を任意で付けた 1 以上 {int.MaxValue} byte 以下である必要があります。組み込み既定値 {FileIndexer.DefaultMaxFileSizeBytes} byte を使用します");
        return null;
    }

    private static long? ParseMaxFileBytes(string value, long? fallback, ref string? parseError)
    {
        if (FileIndexer.TryParseMaxFileSizeBytes(value, out var parsed))
            return parsed;

        parseError ??= $"--max-file-bytes value '{ConsoleUi.FormatBoundedValue(value)}' must be between 1 and {int.MaxValue} inclusive (bytes, with an optional B/K/M/G suffix)";
        return fallback;
    }

    private static int ParseMaxSymbolsPerFile(string value, int fallback, string source, ref string? parseError)
    {
        return ParseIndexNumericOption(value, fallback, source, 1, MaxSymbolsPerFileLimit, ref parseError);
    }

    private static int ParseMaxReferencesPerFile(string value, int fallback, string source, ref string? parseError)
    {
        return ParseIndexNumericOption(value, fallback, source, 1, MaxReferencesPerFileLimit, ref parseError);
    }

    private static int ParseIndexNumericOption(
        string value,
        int fallback,
        string source,
        int minimum,
        int maximum,
        ref string? parseError)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed >= minimum
            && parsed <= maximum)
        {
            return parsed;
        }

        parseError ??= $"{source} value '{ConsoleUi.FormatBoundedValue(value)}' must be between {minimum} and {maximum} inclusive";
        return fallback;
    }

    private static int? ParseWatchDebounce(string value, int? fallback, ref string? parseError)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0
            && parsed <= IndexWatchRunner.MaxDebounceMs)
        {
            return parsed;
        }

        parseError ??= $"--debounce value '{ConsoleUi.FormatBoundedValue(value)}' must be between 0 and {IndexWatchRunner.MaxDebounceMs} inclusive";
        return fallback;
    }

    private static void AddEnvironmentOptionWarning(
        List<CliJsonMessage> warnings,
        string source,
        string value,
        string englishDetail,
        string japaneseDetail)
    {
        var displayValue = ConsoleUi.FormatBoundedValue(value);
        warnings.Add(new CliJsonMessage(
            $"<environment:{source}>",
            $"{source} value '{displayValue}' {englishDetail} / {source} 値 '{displayValue}' は {japaneseDetail}"));
    }

    private static void RemoveOverriddenEnvironmentWarning(
        List<CliJsonMessage> warnings,
        string source,
        bool overridden)
    {
        if (overridden)
            warnings.RemoveAll(warning => string.Equals(warning.File, $"<environment:{source}>", StringComparison.Ordinal));
    }

    private static DurationOutputFormat ParseDurationFormat(string value, DurationOutputFormat fallback)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => DurationOutputFormat.Auto,
            "seconds" => DurationOutputFormat.Seconds,
            "hms" => DurationOutputFormat.Hms,
            _ => WarnInvalidDurationFormat(value, fallback),
        };
    }

    private static DurationOutputFormat WarnInvalidDurationFormat(string value, DurationOutputFormat fallback)
    {
        var displayValue = ConsoleUi.FormatBoundedValue(value);
        CommandErrorWriter.WriteStderr($"Warning: invalid --duration-format value '{displayValue}' (ignored; use auto, seconds, or hms) / 不正な --duration-format 値 '{displayValue}'（無視。auto, seconds, hms のいずれかを指定）");
        return fallback;
    }

    private static CompletionNotificationMode ReadCompletionNotificationModeFromEnvironment()
    {
        var value = CdidxEnvironment.GetProcessEnvironmentVariable(CompletionNotificationEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return CompletionNotificationMode.Auto;

        string? parseError = null;
        var mode = ParseCompletionNotificationMode(value, CompletionNotificationMode.Auto, ref parseError);
        if (parseError != null)
            WarnInvalidCompletionNotificationEnvironmentValue(value);

        return mode;
    }

    private static CompletionNotificationMode ParseCompletionNotificationMode(string value, CompletionNotificationMode fallback, ref string? parseError)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => CompletionNotificationMode.Auto,
            "none" => CompletionNotificationMode.None,
            "bell" => CompletionNotificationMode.Bell,
            "osc9" => CompletionNotificationMode.Osc9,
            "desktop" => CompletionNotificationMode.Osc9,
            _ => WarnInvalidCompletionNotificationMode(value, fallback, ref parseError),
        };
    }

    private static CompletionNotificationMode WarnInvalidCompletionNotificationMode(string value, CompletionNotificationMode fallback, ref string? parseError)
    {
        parseError ??= $"invalid --notify value '{ConsoleUi.FormatBoundedValue(value)}': expected auto, bell, osc9, desktop, or none";
        return fallback;
    }

    private static void WarnInvalidCompletionNotificationEnvironmentValue(string value)
    {
        var displayValue = ConsoleUi.FormatBoundedValue(value);
        CommandErrorWriter.WriteStderr($"Warning: invalid {CompletionNotificationEnvironmentVariable} value '{displayValue}' (ignored; use auto, bell, osc9, desktop, or none) / 不正な {CompletionNotificationEnvironmentVariable} 値 '{displayValue}'（無視。auto, bell, osc9, desktop, none のいずれかを指定）");
    }

    private static string? AbsolutizePathOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value;
        }
    }

    private static string? AbsolutizeDbPathOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return value;
        return AbsolutizePathOption(value);
    }

    internal static string? TryCaptureCurrentDirectory()
    {
        try
        {
            return Environment.CurrentDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool IsUpdateMode(IndexCommandOptions options)
    {
        return options.Commits.Count > 0
            || options.ChangedBetweenSpecified
            || options.UpdateFiles.Count > 0
            || options.ProjectFilters.Count > 0;
    }

    /// <summary>
    /// Build a warning message when the process cwd at the final write step differs from
    /// the cwd captured at the option-parsing boundary. Returns null when the two cwds
    /// are equal or either snapshot is missing. Issue #1577.
    /// </summary>
    internal static string? BuildCwdDriftNotice(string? initialCwd, string? currentCwd)
    {
        if (string.IsNullOrEmpty(initialCwd) || string.IsNullOrEmpty(currentCwd))
            return null;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(initialCwd, currentCwd, comparison))
            return null;
        return $"Process working directory changed during index (was {initialCwd}, now {currentCwd}). "
            + "Index/query paths were absolutized at the option-parsing boundary so this run "
            + "is unaffected, but later code paths that depend on Environment.CurrentDirectory "
            + "may misbehave. Restore the original working directory or re-resolve relative paths.";
    }
}
