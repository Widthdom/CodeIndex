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
        "--db", "--data-dir", "--rebuild", "--verbose", "--json", "--quiet", "--dry-run", "--force",
        "--yes", "--watch", "--debounce", "--duration-format", "--max-file-bytes", "--max-symbols-per-file",
        "--notify",
        "--parallelism", "--memory-trace", "--follow-symlinks",
        "--commits", "--changed-between", "--files", "--solution", "--project",
        "--include-symbol-kind", "--exclude-symbol-kind", "--optimize", "--help",
        "--read-only", "--immutable",
    ];

    internal const string IndexParallelismEnvironmentVariable = "CDIDX_INDEX_PARALLELISM";
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
        bool dryRun = false;
        bool force = false;
        bool readOnly = false;
        bool yes = false;
        bool watch = false;
        bool optimizeOnly = false;
        bool memoryTrace = false;
        int? watchDebounceMs = null;
        var durationFormat = DurationOutputFormat.Auto;
        var notifyMode = ReadCompletionNotificationModeFromEnvironment();
        long? maxFileSizeBytes = ReadMaxFileSizeBytesFromEnvironment();
        var maxSymbolsPerFile = DefaultMaxSymbolsPerFile;
        var parallelism = ReadIndexParallelismFromEnvironment();
        var symlinkPolicy = FileIndexer.SymlinkPolicy.None;
        string? easterEgg = null;
        int spinnerFlagCount = 0;
        bool randomSpinner = false;
        var commits = new List<string>();
        var changedBetweenRefs = new List<string>();
        var changedBetweenSpecified = false;
        var updateFiles = new List<string>();
        var projectFilters = new List<string>();
        string? solutionPath = null;
        string? projectFilterError = null;
        string? parseError = null;
        var includeSymbolKinds = new List<string>();
        var excludeSymbolKinds = new List<string>();
        string? symbolKindFilterError = null;
        var includeSymbolKindsSpecifiedOnCli = false;
        var excludeSymbolKindsSpecifiedOnCli = false;

        AddSymbolKindFilterValues(
            IncludeSymbolKindsEnvironmentVariable,
            Environment.GetEnvironmentVariable(IncludeSymbolKindsEnvironmentVariable),
            includeSymbolKinds,
            ref symbolKindFilterError);
        AddSymbolKindFilterValues(
            ExcludeSymbolKindsEnvironmentVariable,
            Environment.GetEnvironmentVariable(ExcludeSymbolKindsEnvironmentVariable),
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
                case "--dry-run":
                    dryRun = true;
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
                case "--memory-trace":
                    memoryTrace = true;
                    break;
                case "--debounce" when i + 1 < args.Length:
                    if (int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedDebounce) && parsedDebounce >= 0)
                    {
                        watchDebounceMs = parsedDebounce;
                        i++;
                    }
                    else
                    {
                        Console.Error.WriteLine($"Warning: invalid --debounce value '{args[i + 1]}' (ignored; must be a non-negative integer in milliseconds) / 不正な --debounce 値 '{args[i + 1]}'（無視。ミリ秒の0以上の整数を指定）");
                        i++;
                    }
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
                    maxFileSizeBytes = ParseMaxFileBytes(args[++i], maxFileSizeBytes);
                    break;
                case var option when option.StartsWith("--max-file-bytes=", StringComparison.Ordinal):
                    maxFileSizeBytes = ParseMaxFileBytes(option["--max-file-bytes=".Length..], maxFileSizeBytes);
                    break;
                case "--max-symbols-per-file" when i + 1 < args.Length:
                    maxSymbolsPerFile = ParseMaxSymbolsPerFile(args[++i], maxSymbolsPerFile, "--max-symbols-per-file");
                    break;
                case var option when option.StartsWith("--max-symbols-per-file=", StringComparison.Ordinal):
                    maxSymbolsPerFile = ParseMaxSymbolsPerFile(option["--max-symbols-per-file=".Length..], maxSymbolsPerFile, "--max-symbols-per-file");
                    break;
                case "--parallelism" when i + 1 < args.Length:
                    parallelism = ParseIndexParallelism(args[++i], parallelism, "--parallelism");
                    break;
                case var option when option.StartsWith("--parallelism=", StringComparison.Ordinal):
                    parallelism = ParseIndexParallelism(option["--parallelism=".Length..], parallelism, "--parallelism");
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
                        commits.Add(commit);
                    }
                    if (commits.Count == 0)
                        Console.Error.WriteLine("Warning: --commits specified but no commit refs provided / --commits が指定されましたがコミットrefがありません");
                    break;
                case "--changed-between":
                    changedBetweenSpecified = true;
                    while (i + 1 < args.Length && !args[i + 1].StartsWith('-') && changedBetweenRefs.Count < 2)
                        changedBetweenRefs.Add(args[++i]);
                    if (changedBetweenRefs.Count != 2)
                        Console.Error.WriteLine("Warning: --changed-between requires exactly two refs / --changed-between は2つのrefが必要です");
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
                    while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        updateFiles.Add(args[++i]);
                    if (updateFiles.Count == 0)
                        Console.Error.WriteLine("Warning: --files specified but no file paths provided / --files が指定されましたがファイルパスがありません");
                    break;
                case "--help" or "-h":
                    return new IndexCommandOptions { ShowHelp = true };
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
                projectFilterError = ex.Message;
            }
        }

        if (spinnerFlagCount > 1)
        {
            Console.Error.WriteLine("\U0001f375 Simultaneous intake of beer and coffee is not recommended. How about some matcha instead?");
            Console.Error.WriteLine("   \u30d3\u30fc\u30eb\u3068\u30b3\u30fc\u30d2\u30fc\u306e\u540c\u6642\u6442\u53d6\u306f\u304a\u3059\u3059\u3081\u3057\u307e\u305b\u3093\u3002\u62b9\u8336\u306f\u3044\u304b\u304c\uff1f");
            easterEgg = "--matcha";
        }

        if (randomSpinner && easterEgg == null)
        {
            var themes = new[] { "--sushi", "--coffee", "--ramen", "--wine", "--beer", "--matcha", "--whisky" };
            easterEgg = themes[Random.Shared.Next(themes.Length)];
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
            Commits = commits,
            ChangedBetweenSpecified = changedBetweenSpecified,
            ChangedBetweenRefs = changedBetweenRefs,
            UpdateFiles = updateFiles,
            ProjectFilters = projectFilters,
            SolutionPath = solutionPath,
            ProjectFilterError = projectFilterError,
            ParseError = parseError,
            EasterEgg = easterEgg,
            DryRun = dryRun,
            Force = force,
            ReadOnly = readOnly,
            Yes = yes,
            Watch = watch,
            OptimizeOnly = optimizeOnly,
            MemoryTrace = memoryTrace,
            WatchDebounceMs = watchDebounceMs,
            DurationFormat = durationFormat,
            NotifyMode = notifyMode,
            MaxFileSizeBytes = maxFileSizeBytes,
            MaxSymbolsPerFile = maxSymbolsPerFile,
            Parallelism = parallelism,
            SymlinkPolicy = symlinkPolicy,
            SymbolKindFilter = SymbolKindFilter.Create(includeSymbolKinds, excludeSymbolKinds, symbolKindFilterError),
        };
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
                parseError ??= $"invalid --follow-symlinks value '{value}': expected none, internal, or all";
                return fallback;
        }
    }

    private static string BuildUnknownIndexOptionError(string token)
    {
        var name = TrimInlineValue(token);
        var suggestion = ConsoleUi.FindClosestMatch(name, AcceptedIndexFlags);
        return suggestion == null
            ? $"unknown option '{token}'"
            : $"unknown option '{token}'\nDid you mean: {suggestion}?";
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

    internal static int DefaultIndexParallelism()
        => Math.Clamp(Environment.ProcessorCount, 1, MaxIndexParallelism);

    private static int ReadIndexParallelismFromEnvironment()
    {
        var fallback = DefaultIndexParallelism();
        var value = Environment.GetEnvironmentVariable(IndexParallelismEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return ParseIndexParallelism(value, fallback, IndexParallelismEnvironmentVariable);
    }

    private static int ParseIndexParallelism(string value, int fallback, string source)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            if (parsed <= MaxIndexParallelism)
                return parsed;

            Console.Error.WriteLine($"Warning: {source} value '{value}' exceeds the maximum {MaxIndexParallelism}; using {MaxIndexParallelism} / {source} 値 '{value}' は最大 {MaxIndexParallelism} を超えています。{MaxIndexParallelism} を使用します");
            return MaxIndexParallelism;
        }

        Console.Error.WriteLine($"Warning: invalid {source} value '{value}' (ignored; use a positive integer) / 不正な {source} 値 '{value}'（無視。正の整数を指定）");
        return fallback;
    }

    private static long? ReadMaxFileSizeBytesFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (FileIndexer.TryParseMaxFileSizeBytes(value, out var parsed))
            return parsed;

        Console.Error.WriteLine($"Warning: invalid {FileIndexer.MaxFileSizeEnvironmentVariable} value '{value}' (ignored; use positive bytes or K/M/G suffixes) / 不正な {FileIndexer.MaxFileSizeEnvironmentVariable} 値 '{value}'（無視。正の byte 数または K/M/G 接尾辞を指定）");
        return null;
    }

    private static long? ParseMaxFileBytes(string value, long? fallback)
    {
        if (FileIndexer.TryParseMaxFileSizeBytes(value, out var parsed))
            return parsed;

        Console.Error.WriteLine($"Warning: invalid --max-file-bytes value '{value}' (ignored; use positive bytes or K/M/G suffixes) / 不正な --max-file-bytes 値 '{value}'（無視。正の byte 数または K/M/G 接尾辞を指定）");
        return fallback;
    }

    private static int ParseMaxSymbolsPerFile(string value, int fallback, string source)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            return parsed;

        Console.Error.WriteLine($"Warning: invalid {source} value '{value}' (ignored; use a positive integer) / 不正な {source} 値 '{value}'（無視。正の整数を指定）");
        return fallback;
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
        Console.Error.WriteLine($"Warning: invalid --duration-format value '{value}' (ignored; use auto, seconds, or hms) / 不正な --duration-format 値 '{value}'（無視。auto, seconds, hms のいずれかを指定）");
        return fallback;
    }

    private static CompletionNotificationMode ReadCompletionNotificationModeFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("CDIDX_NOTIFY");
        if (string.IsNullOrWhiteSpace(value))
            return CompletionNotificationMode.Auto;

        string? ignored = null;
        return ParseCompletionNotificationMode(value, CompletionNotificationMode.Auto, ref ignored);
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
        parseError ??= $"invalid --notify value '{value}': expected auto, bell, osc9, desktop, or none";
        return fallback;
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
