using CodeIndex.Database;
using CodeIndex.Indexer;
using System.Reflection;
using System.Text;

namespace CodeIndex.Cli;

/// <summary>
/// User-visible color output policy. Drives ANSI escape emission.
/// 色出力ポリシー。ANSI エスケープ発行を制御する。
/// </summary>
public enum ColorMode
{
    /// <summary>Honor env vars (NO_COLOR / CLICOLOR_FORCE / CLICOLOR) and fall back to TTY auto-detect.</summary>
    Auto = 0,
    /// <summary>Always emit ANSI escapes, even when stdout is redirected.</summary>
    Always = 1,
    /// <summary>Never emit ANSI escapes, even on a TTY.</summary>
    Never = 2,
}

/// <summary>
/// ANSI color palette to use when color output is enabled. <see cref="Basic"/>
/// stays within the 8 standard SGR colors (30–37) and avoids the bright-black
/// dim escape (<c>\x1b[90m</c>), which is unreadable on many SSH/CI terminals.
/// <see cref="Color256"/> uses 256-color codes (`\x1b[38;5;Nm`) for higher
/// contrast on capable terminals. <see cref="Truecolor"/> uses 24-bit RGB
/// (`\x1b[38;2;R;G;Bm`) for terminals that advertise truecolor via
/// <c>COLORTERM=truecolor|24bit</c>.
/// 色出力で使用する ANSI パレット。Basic は標準8色のみで `\x1b[90m`（dim）を
/// 避け、SSH / CI 端末でも可読性を確保する。Color256 / Truecolor はそれぞれ
/// 256色 / 24ビットRGB を用い、対応端末で高コントラストを実現する。
/// </summary>
public enum ColorPalette
{
    /// <summary>Standard 8-color ANSI palette (30–37); avoids dim (`\x1b[90m`).</summary>
    Basic = 0,
    /// <summary>256-color ANSI palette (`\x1b[38;5;Nm`).</summary>
    Color256 = 1,
    /// <summary>24-bit RGB / truecolor palette (`\x1b[38;2;R;G;Bm`).</summary>
    Truecolor = 2,
}

/// <summary>
/// Console UI helpers: spinner, progress bar, banner, and easter egg messages.
/// コンソールUIヘルパー: スピナー、プログレスバー、バナー、イースターエッグメッセージ。
/// </summary>
public static class ConsoleUi
{
    private static readonly (string Command, string Usage)[] CommandUsageLines =
    [
        ("index", "cdidx index <projectPath> [--db <path>] [--rebuild] [--verbose] [--dry-run] [--force] [--json]"),
        ("backfill-fold", "cdidx backfill-fold [--db <path>] [--json]"),
        ("index-commits", "cdidx index <projectPath> --commits <id> [id ...] [--db <path>] [--verbose] [--dry-run] [--json]"),
        ("index-files", "cdidx index <projectPath> --files <path> [path ...] [--db <path>] [--verbose] [--dry-run] [--json]"),
        ("search", "cdidx search <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--snippet-lines <n>] [--max-line-width <n>] [--fts] [--exact|--exact-substring] [--prefix] [--count] [--since <datetime>] [--no-dedup]"),
        ("definition", "cdidx definition <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--exact|--exact-name] [--count] [--since <datetime>]"),
        ("references", "cdidx references <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--max-line-width <n>] [--exact|--exact-name] [--count]"),
        ("callers", "cdidx callers <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--count]"),
        ("callees", "cdidx callees <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--count]"),
        ("symbols", "cdidx symbols [query|--query <query>|-- <query>] [--name <name>] [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--count] [--since <datetime>]"),
        ("files", "cdidx files [query|--query <query>|-- <query>] [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--since <datetime>]"),
        ("find", "cdidx find <query> --path <glob> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--exclude-path <glob>] [--exclude-tests] [--before <n>] [--after <n>] [--max-line-width <n>] [--exact] [--count]"),
        ("excerpt", "cdidx excerpt <path> --start <line> [--end <line>] [--before <n>] [--after <n>] [--max-line-width <n>] [--focus-line <line>] [--focus-column <n>] [--focus-length <n>] [--db <path>] [--json]"),
        ("map", "cdidx map [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests]"),
        ("inspect", "cdidx inspect <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--max-line-width <n>] [--exact|--exact-name]"),
        ("outline", "cdidx outline <path> [--db <path>] [--json]"),
        ("status", "cdidx status [--db <path>] [--json] [--check]"),
        ("db", "cdidx db --integrity-check [--db <path>] [--json]"),
        ("report", "cdidx report --output <path> [--db <path>] [--json] [--log-lines <n>] [--no-log] [--include-args]"),
        ("validate", "cdidx validate [--db <path>] [--json] [--kind <kind>] [--path <glob>]"),
        ("impact", "cdidx impact <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--depth <n>] [--count] [--with-paths]"),
        ("deps", "cdidx deps [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--reverse]"),
        ("unused", "cdidx unused [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count]"),
        ("hotspots", "cdidx hotspots [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--group-by-name]"),
        ("languages", "cdidx languages [--json]"),
        ("mcp", "cdidx mcp [--db <path>]"),
        ("license", "cdidx license"),
    ];

    private const int SpinnerFrameDelayMs = 100;
    private const int SpinnerStopDelayMs = 20;
    private const int ConsoleLineMargin = 1;

    private static readonly string[] DefaultBrailleSpinnerFrames =
    [
        "⠋", "⠙", "⠹", "⠸", "⠼",
        "⠴", "⠦", "⠧", "⠇", "⠏",
    ];

    // --- Spinner / スピナー ---

    /// <summary>
    /// Start spinner on a background thread, returns CancellationTokenSource to stop it.
    /// バックグラウンドスレッドでスピナーを開始。停止用のCancellationTokenSourceを返す。
    /// </summary>
    public static CancellationTokenSource? StartSpinner(string message, string[] frames)
    {
        // Braille frames are single-char; themed frames are longer strings containing the display text
        // ブレイルフレームは1文字、テーマフレームは表示テキストを含む長い文字列
        bool isThemed = frames.Length > 0 && frames[0].Length > 2;

        if (!ShouldUseInteractiveConsole())
        {
            Console.WriteLine(message);
            return null;
        }

        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        Task.Run(() =>
        {
            int i = 0;
            while (!ct.IsCancellationRequested)
            {
                var frame = frames[i % frames.Length];
                var line = isThemed ? $"\r{frame}" : $"\r{frame} {message}";
                Console.Write(line);
                Console.Out.Flush();
                i++;
                try { Task.Delay(SpinnerFrameDelayMs, ct).Wait(ct); } catch (OperationCanceledException) { break; }
            }
        }, ct);
        return cts;
    }

    /// <summary>
    /// Stop spinner and clear the line.
    /// スピナーを停止して行をクリア。
    /// </summary>
    public static void StopSpinner(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        cts.Cancel();
        // Small delay to let the spinner task exit / スピナータスク終了のための短い待機
        Thread.Sleep(SpinnerStopDelayMs);
        if (ShouldUseInteractiveConsole())
        {
            Console.Write($"\r{new string(' ', GetWindowWidth() - ConsoleLineMargin)}\r");
            Console.Out.Flush();
        }
        cts.Dispose();
    }

    /// <summary>
    /// Get spinner frames based on easter egg flag.
    /// イースターエッグフラグに基づくスピナーフレームを取得。
    /// </summary>
    public static string[] GetSpinnerFrames(string? easterEgg) => easterEgg switch
    {
        "--sushi" =>
        [
            "\U0001f363 Slicing       ", "\U0001f363 Slicing.      ", "\U0001f363 Slicing..     ", "\U0001f363 Slicing...    ",
            "\U0001f363 Shaping       ", "\U0001f363 Shaping.      ", "\U0001f363 Shaping..     ", "\U0001f363 Shaping...    ",
            "\U0001f363 Pressing      ", "\U0001f363 Pressing.     ", "\U0001f363 Pressing..    ", "\U0001f363 Pressing...   ",
            "\U0001f363 Itadakimasu!  ",
        ],
        "--coffee" =>
        [
            "\u2615 Grinding      ", "\u2615 Grinding.     ", "\u2615 Grinding..    ", "\u2615 Grinding...   ",
            "\u2615 Heating       ", "\u2615 Heating.      ", "\u2615 Heating..     ", "\u2615 Heating...    ",
            "\u2615 Brewing       ", "\u2615 Brewing.      ", "\u2615 Brewing..     ", "\u2615 Brewing...    ",
        ],
        "--ramen" =>
        [
            "\U0001f35c Boiling       ", "\U0001f35c Boiling.      ", "\U0001f35c Boiling..     ", "\U0001f35c Boiling...    ",
            "\U0001f35c Steaming      ", "\U0001f35c Steaming.     ", "\U0001f35c Steaming..    ", "\U0001f35c Steaming...   ",
            "\U0001f35c Slurping      ", "\U0001f35c Slurping.     ", "\U0001f35c Slurping..    ", "\U0001f35c Slurping...   ",
            "\U0001f35c Itadakimasu!  ",
        ],
        "--wine" =>
        [
            "\U0001f377 Crushing      ", "\U0001f377 Crushing.     ", "\U0001f377 Crushing..    ", "\U0001f377 Crushing...   ",
            "\U0001f377 Aging         ", "\U0001f377 Aging.        ", "\U0001f377 Aging..       ", "\U0001f377 Aging...      ",
            "\U0001f377 Pouring       ", "\U0001f377 Pouring.      ", "\U0001f377 Pouring..     ", "\U0001f377 Pouring...    ",
            "\U0001f377 Sant\u00e9!        ",
        ],
        "--beer" =>
        [
            "\U0001f37a Tapping       ", "\U0001f37a Tapping.      ", "\U0001f37a Tapping..     ", "\U0001f37a Tapping...    ",
            "\U0001f37a Pouring       ", "\U0001f37a Pouring.      ", "\U0001f37a Pouring..     ", "\U0001f37a Pouring...    ",
            "\U0001f37a Foaming       ", "\U0001f37a Foaming.      ", "\U0001f37a Foaming..     ", "\U0001f37a Foaming...    ",
            "\U0001f37a Cheers!       ",
        ],
        "--matcha" =>
        [
            "\U0001f375 Sifting       ", "\U0001f375 Sifting.      ", "\U0001f375 Sifting..     ", "\U0001f375 Sifting...    ",
            "\U0001f375 Pouring       ", "\U0001f375 Pouring.      ", "\U0001f375 Pouring..     ", "\U0001f375 Pouring...    ",
            "\U0001f375 Whisking      ", "\U0001f375 Whisking.     ", "\U0001f375 Whisking..    ", "\U0001f375 Whisking...   ",
            "\U0001f375 Douzo!        ",
        ],
        "--whisky" =>
        [
            "\U0001f943 Mashing       ", "\U0001f943 Mashing.      ", "\U0001f943 Mashing..     ", "\U0001f943 Mashing...    ",
            "\U0001f943 Distilling    ", "\U0001f943 Distilling.   ", "\U0001f943 Distilling..  ", "\U0001f943 Distilling... ",
            "\U0001f943 Aging         ", "\U0001f943 Aging.        ", "\U0001f943 Aging..       ", "\U0001f943 Aging...      ",
            "\U0001f943 Slainte!      ",
        ],
        // Default: Braille spinner / デフォルト: ブレイルスピナー
        _ => DefaultBrailleSpinnerFrames,
    };

    // --- Progress bar / プログレスバー ---

    // Active spinner frames for progress bar (themed or default braille)
    // プログレスバー用アクティブスピナーフレーム（テーマ付きまたはデフォルトブレイル）
    private static string[] _progressSpinnerFrames = DefaultBrailleSpinnerFrames;
    // Track last progress line length for clearing / クリア用に最後のプログレス行の長さを記録
    private static int _lastProgressLineLength;

    /// <summary>
    /// Set progress bar spinner theme (reuses GetSpinnerFrames).
    /// プログレスバーのスピナーテーマを設定（GetSpinnerFramesを再利用）。
    /// </summary>
    public static void SetProgressTheme(string? easterEgg)
    {
        _progressSpinnerFrames = GetSpinnerFrames(easterEgg);
    }

    /// <summary>
    /// Print inline progress bar with spinner.
    /// スピナー付きインライン進捗バーを表示。
    /// </summary>
    public static void PrintProgress(int current, int total)
    {
        if (total <= 0)
            return;

        var output = Console.Out;
        var redirected = !ShouldUseInteractiveConsole();

        // Update every 50 files or at completion / 50ファイルごと、または完了時に更新
        if (current % 50 != 0 && current != total)
            return;

        const int barWidth = 32;
        var pct = (double)current / total;
        int filled = (int)Math.Round(pct * barWidth);
        if (filled > barWidth) filled = barWidth;

        // Show spinner while in progress, checkmark on completion / 処理中はスピナー、完了時はチェックマーク
        var spinner = current == total ? " " : _progressSpinnerFrames[(current / 50) % _progressSpinnerFrames.Length];
        var bar = new string('\u2588', filled) + new string('\u2591', barWidth - filled);
        var line = $"{spinner} {bar} {pct * 100,5:F1}%  [{current:N0}/{total:N0}]";

        if (!redirected)
        {
            output.Write($"\r{line}");
            output.Flush();
            _lastProgressLineLength = line.Length;
            if (current == total)
            {
                output.WriteLine();
                _lastProgressLineLength = 0;
            }
        }
        else
        {
            // Fallback for redirected output / リダイレクト時はフォールバック
            output.WriteLine(line.TrimStart());
        }
    }

    /// <summary>
    /// Clear the current progress bar line so other output can be printed cleanly.
    /// 他の出力を正しく表示するために現在のプログレスバー行をクリア。
    /// </summary>
    public static void ClearProgressLine()
    {
        if (ShouldUseInteractiveConsole() && _lastProgressLineLength > 0)
        {
            Console.Write($"\r{new string(' ', _lastProgressLineLength)}\r");
            Console.Out.Flush();
            _lastProgressLineLength = 0;
        }
    }

    /// <summary>
    /// Print a warning message, clearing the progress bar line first if needed.
    /// 必要に応じてプログレスバー行をクリアしてから警告メッセージを表示。
    /// </summary>
    public static void PrintWarning(string message)
    {
        ClearProgressLine();
        Console.Error.WriteLine($"  [WARN] {message}");
    }

    // --- Banner / バナー ---

    /// <summary>
    /// Print ASCII-art banner.
    /// ASCIIアートバナーを表示。
    /// </summary>
    public static void PrintBanner()
    {
        const string banner = """

             ██████╗ ██████╗ ██████╗ ███████╗██╗███╗   ██╗██████╗ ███████╗██╗  ██╗
            ██╔════╝██╔═══██╗██╔══██╗██╔════╝██║████╗  ██║██╔══██╗██╔════╝╚██╗██╔╝
            ██║     ██║   ██║██║  ██║█████╗  ██║██╔██╗ ██║██║  ██║█████╗   ╚███╔╝
            ██║     ██║   ██║██║  ██║██╔══╝  ██║██║╚██╗██║██║  ██║██╔══╝   ██╔██╗
            ╚██████╗╚██████╔╝██████╔╝███████╗██║██║ ╚████║██████╔╝███████╗██╔╝ ██╗
             ╚═════╝ ╚═════╝ ╚═════╝ ╚══════╝╚═╝╚═╝  ╚═══╝╚═════╝ ╚══════╝╚═╝  ╚═╝
            """;
        Console.WriteLine(banner);
    }

    // --- Easter eggs / イースターエッグ ---

    /// <summary>
    /// Print easter egg message (standalone mode).
    /// イースターエッグメッセージを表示（単体実行時）。
    /// </summary>
    public static void PrintEasterEggMessage(string flag)
    {
        var (en, ja) = flag switch
        {
            "--sushi"  => ("\U0001f363 Indexing is like making sushi \u2014 patience yields perfection.",
                           "   \u30a4\u30f3\u30c7\u30c3\u30af\u30b9\u306f\u5bff\u53f8\u4f5c\u308a\u306e\u3088\u3046\u306b \u2014 \u5fcd\u8010\u304c\u5b8c\u74a7\u3092\u751f\u3080\u3002"),
            "--coffee" => ("\u2615 Leave the indexing to me and go grab a coffee!",
                           "   \u30a4\u30f3\u30c7\u30c3\u30af\u30b9\u306f\u4efb\u305b\u3066\u3001\u30b3\u30fc\u30d2\u30fc\u3067\u3082\u98f2\u3093\u3067\u304d\u3066\uff01"),
            "--ramen"  => ("\U0001f35c Indexing in progress... perfect time for a bowl of ramen!",
                           "   \u30a4\u30f3\u30c7\u30c3\u30af\u30b9\u4e2d\u2026\u30e9\u30fc\u30e1\u30f3\u4e00\u676f\u3044\u304b\u304c\uff1f"),
            "--wine"   => ("\U0001f377 Crushing... Aging... Pouring... Sant\u00e9!",
                           "   \u30a4\u30f3\u30c7\u30c3\u30af\u30b9\u306f\u30ef\u30a4\u30f3\u306e\u3088\u3046\u306b\u2014\u719f\u6210\u3092\u5f85\u3064\u4fa1\u5024\u304c\u3042\u308b\u3002"),
            "--beer"   => ("\U0001f37a Tapping... Pouring... Foaming... Cheers!",
                           "   \u30a4\u30f3\u30c7\u30c3\u30af\u30b9\u5b8c\u4e86\u307e\u3067\u3001\u4e7e\u676f\uff01"),
            "--matcha" => ("\U0001f375 Sifting... Pouring... Whisking... \u3069\u3046\u305e\uff01",
                           "   \u4e00\u670d\u306e\u62b9\u8336\u3067\u3082\u3044\u304b\u304c\u3067\u3059\u304b\uff1f"),
            "--whisky" => ("\U0001f943 Mashing... Distilling... Aging... Slainte!",
                           "   \u30a4\u30f3\u30c7\u30c3\u30af\u30b9\u306f\u30a6\u30a4\u30b9\u30ad\u30fc\u306e\u3088\u3046\u306b\u2014\u719f\u6210\u304c\u5927\u4e8b\u3002"),
            _ => ("", ""),
        };
        Console.WriteLine(en);
        Console.WriteLine(ja);
    }

    // --- Version loading / バージョン読み込み ---

    /// <summary>
    /// Load version from version.json.
    /// version.jsonからバージョンを読み込み。
    /// </summary>
    public static string LoadVersion()
    {
        var exeDir = AppContext.BaseDirectory;
        var path = Path.Combine(exeDir, "version.json");
        if (!File.Exists(path))
        {
            // Fallback: look relative to current directory / カレントディレクトリからの相対パスでフォールバック
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.json");
        }
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var ver))
                return ver.GetString() ?? "0.0.0";
        }
        return "0.0.0";
    }

    /// <summary>
    /// Build metadata stamped into the assembly at compile time, used by
    /// `--version` so dev builds and tagged releases are distinguishable in
    /// bug reports (#1550). Any field can be "unknown" when the build host
    /// lacks git (e.g. a tarball-only checkout).
    /// `--version` がバグ報告で dev ビルドとタグ済みリリースを区別できる
    /// よう、ビルド時にアセンブリへ刻んだメタデータ (#1550)。git の無い
    /// ビルドホストでは各フィールドが "unknown" になりうる。
    /// </summary>
    public sealed record BuildMetadata(string Version, string Commit, string BuildDate, string Dirty);

    /// <summary>
    /// Load the full build metadata: semver from version.json plus commit/build
    /// date/dirty flag stamped into the assembly via <c>AssemblyMetadataAttribute</c>.
    /// version.json の semver と、AssemblyMetadataAttribute で刻まれた
    /// commit / build date / dirty フラグを合わせて読み込む。
    /// </summary>
    public static BuildMetadata LoadBuildMetadata()
    {
        var assembly = typeof(ConsoleUi).Assembly;
        return new BuildMetadata(
            Version: LoadVersion(),
            Commit: ReadAssemblyMetadata(assembly, "CdidxCommit"),
            BuildDate: ReadAssemblyMetadata(assembly, "CdidxBuildDate"),
            Dirty: ReadAssemblyMetadata(assembly, "CdidxBuildDirty"));
    }

    private static string ReadAssemblyMetadata(Assembly assembly, string key)
    {
        foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attr.Key, key, StringComparison.Ordinal))
                return string.IsNullOrWhiteSpace(attr.Value) ? "unknown" : attr.Value!;
        }
        return "unknown";
    }

    // --- Usage / 使い方 ---

    /// <summary>
    /// Print usage information.
    /// 使い方を表示する。
    /// </summary>
    public static void PrintUsage(bool showBanner = true)
    {
        if (showBanner)
        {
            PrintBanner();
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  cdidx <projectPath>");
        foreach (var (_, usage) in CommandUsageLines)
            Console.WriteLine($"  {usage}");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  index <projectPath>        Build or update the index for a project");
        Console.WriteLine("  backfill-fold              Upgrade folded-name columns in an existing index DB");
        Console.WriteLine("  search <query>             Full-text search across indexed chunks");
        Console.WriteLine("  definition <query>         Resolve symbol definitions with extracted ranges");
        Console.WriteLine("  references <query>         Find indexed references for a symbol (--kind uses reference kind)");
        Console.WriteLine("  callers <query>            Find callers of a symbol (--kind uses reference kind)");
        Console.WriteLine("  callees <query>            Find callees used by a caller (--kind uses reference kind)");
        Console.WriteLine("  symbols [query]            Search symbols (functions, classes, imports)");
        Console.WriteLine("  files [query]              List indexed files");
        Console.WriteLine("  find <query>               Find literal substring matches inside known indexed files");
        Console.WriteLine("  excerpt <path>             Reconstruct a line-range excerpt from indexed chunks");
        Console.WriteLine("  map                        Show a repo-level overview for AI orientation");
        Console.WriteLine("  inspect <query>            Bundle definition, graph, and nearby symbol context");
        Console.WriteLine("  outline <path>             Show the symbol outline of a single file");
        Console.WriteLine("  status                     Show database statistics; add --check to verify DB/worktree match");
        Console.WriteLine("  db --integrity-check       Run SQLite `PRAGMA integrity_check` and report findings");
        Console.WriteLine("  report --output <path>     Build a redacted crash-repro tarball (.tgz) for bug reports");
        Console.WriteLine("  validate                   Report encoding issues (U+FFFD, BOM, null bytes, mixed line endings, UTF-16 BOM, likely non-UTF8)");
        Console.WriteLine("  impact <query>             Show transitive callers; type queries may return heuristic file-level dependency hints");
        Console.WriteLine("  deps                       Show file-level dependency edges from the reference graph");
        Console.WriteLine("  unused                     Find symbols defined but never referenced (dead code)");
        Console.WriteLine("  hotspots                   Find high-impact symbols; duplicate-name families may fall back conservatively");
        Console.WriteLine("  languages                  List supported languages and their capabilities");
        Console.WriteLine("  mcp                        Start MCP server (for AI tools: Claude, Cursor, etc.)");
        Console.WriteLine("  license                    Show licensing, trademark, and commercial-use summary");
        Console.WriteLine();
        Console.WriteLine("Index and update options:");
        Console.WriteLine("  --db <path>                Database file path (default for index: <projectPath>/.cdidx/codeindex.db)");
        Console.WriteLine("  --rebuild                  Delete existing DB and rebuild from scratch");
        Console.WriteLine("  --verbose                  Show per-file status ([OK  ]/[SKIP]/[DEL ]/[ERR ])");
        Console.WriteLine("  --dry-run                  Scan files without writing to the database");
        Console.WriteLine("  --force                    Bypass the per-database index lock; only use when no other cdidx index is active");
        Console.WriteLine("  --json                     Output results as JSON (for AI/machine use)");
        Console.WriteLine("  --commits <id> [id ...]    Update only files changed in the specified git commits (preferred after commits)");
        Console.WriteLine("  --files <path> [path ...]  Update only the specified files; old rename/delete paths are not purged unless also listed");
        Console.WriteLine("  --color <when>             Color output: `auto` (default), `always`, or `never`; flag wins over `CLICOLOR_FORCE` / `NO_COLOR` / `CLICOLOR` env vars, which win over TTY auto-detect");
        Console.WriteLine("  --palette <name>           ANSI palette: `basic` (8-color, default fallback), `256`, or `truecolor`; flag wins over `CDIDX_COLOR_PALETTE` env var, which wins over `COLORTERM` / `TERM` auto-detect");
        Console.WriteLine("  --metrics <path>           Append one JSONL record per CLI command / MCP tool call to <path> (also honors CDIDX_METRICS=<path>)");
        Console.WriteLine("  --help, -h                 Show this help message");
        Console.WriteLine("  --version, -V              Show version information");
        Console.WriteLine("  --license                  Show licensing, trademark, and commercial-use summary");
        Console.WriteLine("  --completions <shell>      Generate shell completions (bash, zsh, fish)");
        Console.WriteLine();
        Console.WriteLine("Update workflows:");
        Console.WriteLine("  Use --commits with a project path after normal commits; git diff sees rename/delete paths too.");
        Console.WriteLine("  Use --files only for known in-place edits or new files; old rename/delete paths stay indexed unless also listed.");
        Console.WriteLine();
        Console.WriteLine("Query options:");
        Console.WriteLine("  --db <path>                Database file path (default: .cdidx/codeindex.db in current directory)");
        Console.WriteLine("  --json                     Output as JSON (streaming hits use JSON lines; counts/summaries use one object)");
        Console.WriteLine("  --limit <n>, --top <n>     Max results to return (default: 20)");
        Console.WriteLine("  --lang <lang>              Filter by language (aliases: bat, cmd, cshtml, razor, ts, tsx, cts, mts)");
        Console.WriteLine("  --path <glob>              Restrict matches to glob-style path patterns (* and ?)");
        Console.WriteLine("  --query <query>            Pass a query literal, useful when the query starts with '-'");
        Console.WriteLine("  --exclude-path <glob>      Exclude glob-style path patterns (* and ?) (repeatable)");
        Console.WriteLine("  --exclude-tests            Exclude likely test files");
        Console.WriteLine("  --snippet-lines <n>        Search snippet length (1-20, default: 8)");
        Console.WriteLine($"  --max-line-width <n>       search/references/find/excerpt/inspect only: clamp very long single-line snippet/context/excerpt payloads (`0` disables clamping; default: {LineWidthFormatter.DefaultMaxLineWidth})");
        Console.WriteLine("  --focus-line <line>        excerpt: line whose focused column should stay visible (requires --focus-column)");
        Console.WriteLine("  --focus-column <n>         excerpt: column to keep centered when clamping (must be within the focused line)");
        Console.WriteLine("  --focus-length <n>         excerpt: width of the focused span (default: 1, requires --focus-column)");
        Console.WriteLine("  --fts                      Use raw FTS5 query syntax for search (trailing * is a prefix shorthand in literal-safe mode)");
        Console.WriteLine("  --exact                    Backward-compatible shorthand. Prefer --exact-substring for search, keep --exact for find, and prefer --exact-name for symbols/definition/references/callers/callees/inspect. Pass at most one of --exact, --exact-substring, --exact-name; combining two or more is rejected.");
        Console.WriteLine("  --exact-substring          Search only: case-sensitive exact substring (no FTS5)");
        Console.WriteLine("  --exact-name               symbols/definition/references/callers/callees/inspect: NFKC + Unicode CaseFold exact name match (legacy/stale-fold DBs fall back to ASCII NOCASE; use `cdidx backfill-fold` or check `status --json` fold_ready)");
        Console.WriteLine("  --kind <kind>              definition/symbols/hotspots/unused: symbol kind; references: reference kind (call/instantiate/subscribe/attribute/annotation); callers/callees: call-graph kinds only (call/instantiate/subscribe — metadata kinds rejected, use references instead); validate: issue kind");
        Console.WriteLine("  --count                    Count only; search/definition/references/callers/callees/symbols/files/find/unused ignore --limit, impact/hotspots still use visible page counts");
        Console.WriteLine("  --since <datetime>         Filter to files modified since this timestamp (ISO 8601)");
        Console.WriteLine("  --depth <n>                Max BFS depth for impact analysis, inclusive (default: 5; --depth 2 returns callers at depth 1 and 2; --depth 0 resolves the symbol without traversing callers)");
        Console.WriteLine("  --reverse                  Reverse direction for deps (show dependents)");
        Console.WriteLine("  --group-by-name            hotspots: collapse rows sharing (name, kind) across files into one line");
        Console.WriteLine("  --with-paths               impact: also emit `paths` per caller — the shortest call chains [root, ..., caller] (diamond graphs surface every converging route, capped per row)");
        Console.WriteLine("  Note: if a query itself starts with '-', pass it with --query <query> or -- <query>; for option values that start with '--', use --opt=<value>.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  cdidx ./myproject                             Index a project");
        Console.WriteLine("  cdidx backfill-fold                           Upgrade folded-name columns in an existing DB");
        Console.WriteLine("  cdidx index ./myproject --commits abc123      Update DB from one commit");
        Console.WriteLine("  cdidx index ./myproject --commits abc123 def456");
        Console.WriteLine("                                              Update DB from multiple commits");
        Console.WriteLine("  cdidx index ./myproject --files src/app.cs    Update specific files");
        Console.WriteLine("  cdidx search \"authenticate\"                    Full-text search");
        Console.WriteLine("  cdidx search \"auth*\"                          Prefix shorthand in literal-safe mode");
        Console.WriteLine("  cdidx search --query --path --path README.md   Search for a literal option token");
        Console.WriteLine("  cdidx search \"Run();\" --exact-substring        Case-sensitive exact substring search");
        Console.WriteLine("  cdidx definition ResolveGitCommonDir --body   Show a symbol definition and body");
        Console.WriteLine("  cdidx references ResolveGitCommonDir          Find indexed references");
        Console.WriteLine("  cdidx references DbContext --kind instantiate Filter constructor sites by reference kind");
        Console.WriteLine("  cdidx references e --path dist/app.js --max-line-width 120");
        Console.WriteLine("                                              Clamp a minified single-line context window");
        Console.WriteLine("  cdidx excerpt src/app.js --start 120 --focus-column 88 --max-line-width 120");
        Console.WriteLine("                                              Keep the requested token visible inside a long line");
        Console.WriteLine("  cdidx callers ResolveGitCommonDir             Find callers");
        Console.WriteLine("  cdidx callees AddToGitExclude                 Find callees used by a caller");
        Console.WriteLine("  cdidx symbols Run --exact-name                Exact symbol-name match");
        Console.WriteLine("  cdidx symbols UserService --kind class        Find class definitions");
        Console.WriteLine("  cdidx find guard --path src/Auth.cs --after 2 Find literal matches inside a known file");
        Console.WriteLine("  cdidx find --path README.md -- --path         Search a literal that starts with '-'");
        Console.WriteLine("  cdidx excerpt src/app.cs --start 10 --end 20  Reconstruct a file excerpt");
        Console.WriteLine("  cdidx map --path src/ --exclude-tests          Show a repo map for source code");
        Console.WriteLine("  cdidx inspect Run --body --exclude-tests       Inspect one symbol with bundled context");
        Console.WriteLine("  cdidx outline src/app.cs --json                Symbol outline of a single file");
        Console.WriteLine("  cdidx deps --path src/ --exclude-tests          Show file-level dependency edges");
        Console.WriteLine("  cdidx deps --reverse --path src/app.cs          Show what depends on a file");
        Console.WriteLine("  cdidx unused --lang csharp --exclude-tests      Find potentially unused symbols");
        Console.WriteLine("  cdidx hotspots --lang csharp --exclude-tests    Find high-impact symbols with conservative duplicate fallback");
        Console.WriteLine("  cdidx hotspots --group-by-name --exclude-tests  Collapse same-name hotspots across files");
        Console.WriteLine("  cdidx impact Run --depth 0 --exclude-tests      Resolve a symbol without traversing callers");
        Console.WriteLine("  cdidx impact FolderDiffService --json           Type query may return heuristic file-level dependency hints");
        Console.WriteLine("  cdidx files --lang python                      List Python files");
        Console.WriteLine("  cdidx files --since 2024-01-01                 Files modified since a date");
        Console.WriteLine("  cdidx status --json                            DB stats as JSON");
        Console.WriteLine("  cdidx languages                                Show supported languages");
        Console.WriteLine("  cdidx license                                  Show licensing and commercial-use terms");
    }

    public static void PrintLicenseSummary()
    {
        Console.WriteLine("cdidx / CodeIndex license");
        Console.WriteLine();
        Console.WriteLine("License: Functional Source License, Version 1.1, ALv2 Future License (FSL-1.1-ALv2)");
        Console.WriteLine("Copyright: Copyright 2026 Widthdom.");
        Console.WriteLine("Summary: use, modification, and distribution are allowed for non-competing purposes, including internal, commercial, AI, IDE, MCP, CI, and scripting integrations.");
        Console.WriteLine("Competing commercial products or services require a separate written agreement with Widthdom.");
        Console.WriteLine("Names and trademarks: CodeIndex and cdidx are not licensed for derivative product, package, service, or endorsement branding.");
        Console.WriteLine();
        Console.WriteLine("See LICENSE, LICENSES/FSL-1.1-ALv2.txt, LICENSES/Apache-2.0.txt, COMMERCIAL_LICENSE.md, INTEGRATION_POLICY.md, and TRADEMARKS.md for the controlling terms.");
    }

    public static string? GetUsageLine(string command)
    {
        foreach (var (name, usage) in CommandUsageLines)
        {
            if (string.Equals(name, command, StringComparison.Ordinal))
                return usage;
        }

        return null;
    }

    // --- Did-you-mean / もしかして ---

    /// <summary>
    /// Find the closest matching command name using Damerau-Levenshtein distance.
    /// Short commands use a stricter threshold to avoid unrelated suggestions.
    /// Damerau-Levenshtein距離で最も近いコマンド名を返す。短いコマンドは無関係な推薦を避けるため閾値を厳しくする。
    /// </summary>
    public static string? FindClosestCommand(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.ToLowerInvariant();
        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var cmd in Commands)
        {
            var dist = DamerauLevenshteinDistance(input, cmd);
            if (dist > GetSuggestionDistanceThreshold(input.Length, cmd.Length))
                continue;

            if (dist < bestDist)
            {
                bestDist = dist;
                best = cmd;
            }
        }
        return best;
    }

    private static int GetSuggestionDistanceThreshold(int inputLength, int commandLength)
    {
        var shorter = Math.Min(inputLength, commandLength);
        return shorter switch
        {
            <= 4 => 1,
            <= 10 => 2,
            _ => 3,
        };
    }

    private static int DamerauLevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[n, m];
    }

    // --- Shell Completions / シェル補完 ---

    private static readonly string[] Commands =
    [
        "index", "backfill-fold", "search", "definition", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status",
        "validate", "deps", "impact", "unused", "hotspots", "languages", "mcp", "db", "report", "license",
    ];

    /// <summary>
    /// Print shell completion script for the specified shell.
    /// 指定シェル向けの補完スクリプトを出力する。
    /// </summary>
    /// <summary>
    /// Print shell completion script. Returns false for unknown shells.
    /// シェル補完スクリプトを出力。不明なシェルの場合はfalseを返す。
    /// </summary>
    public static bool PrintCompletions(string shell)
    {
        try
        {
            Console.WriteLine(GetCompletionScript(shell));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            Console.Error.WriteLine($"Unknown shell: {shell}. Supported: bash, zsh, fish");
            return false;
        }
    }

    internal static string GetCompletionScript(string shell) =>
        shell.ToLowerInvariant() switch
        {
            "bash" => GetBashCompletions(),
            "zsh" => GetZshCompletions(),
            "fish" => GetFishCompletions(),
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unknown shell"),
        };

    /// <summary>
    /// Get sorted unique language names from FileIndexer for completion values.
    /// 補完値用にFileIndexerからソート済みのユニークな言語名を取得する。
    /// </summary>
    private static string GetCompletionLangs() =>
        string.Join(" ", FileIndexer.GetLanguageExtensions().Values
            .Append("bat")
            .Append("cmd")
            .Concat(QueryCommandRunner.GetCompletionLanguageAliases())
            .Distinct()
            .OrderBy(l => l));

    private static string GetBashCompletions()
    {
        var cmds = string.Join(" ", Commands);
        var langs = GetCompletionLangs();
        return $@"_cdidx() {{
    local cur prev commands
    local cmd
    cur=""${{COMP_WORDS[COMP_CWORD]}}""
    prev=""${{COMP_WORDS[COMP_CWORD-1]}}""
    cmd=""${{COMP_WORDS[1]}}""
    commands=""{cmds}""

    if [ $COMP_CWORD -eq 1 ]; then
        COMPREPLY=($(compgen -W ""$commands --help --version --license"" -- ""$cur""))
        return
    fi

    case ""$prev"" in
        --db|--path|--exclude-path) COMPREPLY=($(compgen -f -- ""$cur"")) ;;
        --lang) COMPREPLY=($(compgen -W ""{langs}"" -- ""$cur"")) ;;
        --kind) COMPREPLY=($(compgen -W ""function class struct interface enum property event delegate namespace import"" -- ""$cur"")) ;;
        *)
            if [ ""$cmd"" = ""find"" ]; then
                COMPREPLY=($(compgen -W ""--db --json --no-json --limit --top --lang --path --exclude-path --exclude-tests --before --after --max-line-width --exact --count --query --help --"" -- ""$cur""))
            elif [ ""$cmd"" = ""excerpt"" ]; then
                COMPREPLY=($(compgen -W ""--db --json --before --after --max-line-width --focus-line --focus-column --focus-length --start --end --help"" -- ""$cur""))
            elif [ ""$cmd"" = ""references"" ]; then
                COMPREPLY=($(compgen -W ""--db --json --limit --lang --kind --path --exclude-path --exclude-tests --count --max-line-width --exact --exact-name --help"" -- ""$cur""))
            elif [ ""$cmd"" = ""inspect"" ]; then
                COMPREPLY=($(compgen -W ""--db --json --limit --lang --path --exclude-path --exclude-tests --body --max-line-width --exact --exact-name --help"" -- ""$cur""))
            elif [ ""$cmd"" = ""hotspots"" ]; then
                COMPREPLY=($(compgen -W ""--db --json --limit --lang --kind --path --exclude-path --exclude-tests --count --group-by-name --help"" -- ""$cur""))
            elif [ ""$cmd"" = ""status"" ]; then
                COMPREPLY=($(compgen -W ""--db --json --check --help"" -- ""$cur""))
            elif [ ""$cmd"" = ""db"" ]; then
                COMPREPLY=($(compgen -W ""--db --json --integrity-check --help"" -- ""$cur""))
            elif [ ""$cmd"" = ""search"" ]; then
                COMPREPLY=($(compgen -W ""--db --json --limit --top --lang --path --exclude-path --exclude-tests --count --fts --snippet-lines --max-line-width --since --no-dedup --exact --exact-substring --help"" -- ""$cur""))
            else
                COMPREPLY=($(compgen -W ""--db --json --limit --lang --kind --path --exclude-path --exclude-tests --body --count --since --depth --reverse --exact --exact-name --help"" -- ""$cur""))
            fi
            ;;
    esac
}}
complete -F _cdidx cdidx";
    }

    private static string GetZshCompletions()
    {
        var cmds = string.Join(" ", Commands.Select(c => $"'{c}:{c} command'"));
        return $@"#compdef cdidx
_cdidx() {{
    local -a commands
    commands=(
        {cmds}
    )

    _arguments -C \
        '1:command:->cmds' \
        '*::arg:->args'

    case $state in
        cmds) _describe 'command' commands ;;
        args)
            local subcmd
            subcmd=$words[2]
            if [[ $subcmd == find ]]; then
                _arguments \
                    '--db[Database path]:file:_files' \
                    '--json[JSON output]' \
                    '--no-json[Disable JSON output]' \
                    '--limit[Max results]:number' \
                    '--top[Max results]:number' \
                    '--lang[Filter by language]:language:({GetCompletionLangs()})' \
                      '--path[Path filter]:glob' \
                      '--exclude-path[Exclude path]:glob' \
                    '--exclude-tests[Exclude tests]' \
                    '--before[Context lines before]:number' \
                    '--after[Context lines after]:number' \
                    '--max-line-width[Clamp long single-line snippets (0 disables clamping)]:number' \
                    '--exact[Exact match]' \
                    '--count[Count only]' \
                    '--query[Literal query]' \
                    '*:query'
            elif [[ $subcmd == excerpt ]]; then
                _arguments \
                    '--db[Database path]:file:_files' \
                    '--json[JSON output]' \
                    '--start[Start line]:number' \
                    '--end[End line]:number' \
                    '--before[Context lines before]:number' \
                    '--after[Context lines after]:number' \
                    '--max-line-width[Clamp long single-line excerpts]:number' \
                    '--focus-line[excerpt: focused line number]:number' \
                    '--focus-column[excerpt: focused column]:number' \
                    '--focus-length[excerpt: focused span width]:number' \
                    '*:path'
            elif [[ $subcmd == references ]]; then
                _arguments \
                    '--db[Database path]:file:_files' \
                    '--json[JSON output]' \
                    '--limit[Max results]:number' \
                    '--lang[Filter by language]:language:({GetCompletionLangs()})' \
                    '--kind[Filter by kind]:kind:(function class struct interface enum property event delegate namespace import)' \
                    '--path[Path filter]:pattern' \
                    '--exclude-path[Exclude path]:pattern' \
                    '--exclude-tests[Exclude tests]' \
                    '--count[Count only]' \
                    '--max-line-width[Clamp long single-line contexts (0 disables clamping)]:number' \
                    '--exact[Backward-compatible exact shorthand]' \
                    '--exact-name[Exact symbol-name equality]' \
                    '*:query'
            elif [[ $subcmd == inspect ]]; then
                _arguments \
                    '--db[Database path]:file:_files' \
                    '--json[JSON output]' \
                    '--limit[Max results]:number' \
                    '--lang[Filter by language]:language:({GetCompletionLangs()})' \
                    '--path[Path filter]:pattern' \
                    '--exclude-path[Exclude path]:pattern' \
                    '--exclude-tests[Exclude tests]' \
                    '--body[Include body]' \
                    '--max-line-width[Clamp long single-line contexts (0 disables clamping)]:number' \
                    '--exact[Backward-compatible exact shorthand]' \
                    '--exact-name[Exact symbol-name equality]' \
                    '*:query'
            elif [[ $subcmd == hotspots ]]; then
                _arguments \
                    '--db[Database path]:file:_files' \
                    '--json[JSON output]' \
                    '--limit[Max results]:number' \
                    '--lang[Filter by language]:language:({GetCompletionLangs()})' \
                    '--kind[Filter by kind]:kind:(function class struct interface enum property event delegate namespace import)' \
                    '--path[Path filter]:pattern' \
                    '--exclude-path[Exclude path]:pattern' \
                    '--exclude-tests[Exclude tests]' \
                    '--count[Count only]' \
                    '--group-by-name[Hotspots: collapse same-name rows across files]' \
                    '*:query'
            elif [[ $subcmd == status ]]; then
                _arguments \
                    '--db[Database path]:file:_files' \
                    '--json[JSON output]' \
                    '--check[Verify index matches current workspace]'
            elif [[ $subcmd == db ]]; then
                _arguments \
                    '--db[Database path]:file:_files' \
                    '--json[JSON output]' \
                    '--integrity-check[Run PRAGMA integrity_check on the database]'
            elif [[ $subcmd == search ]]; then
                _arguments \
                    '--db[Database path]:file:_files' \
                    '--json[JSON output]' \
                    '--limit[Max results]:number' \
                    '--top[Max results]:number' \
                    '--lang[Filter by language]:language:({GetCompletionLangs()})' \
                    '--path[Path filter]:pattern' \
                    '--exclude-path[Exclude path]:pattern' \
                    '--exclude-tests[Exclude tests]' \
                    '--count[Count only]' \
                    '--fts[Raw FTS5 syntax]' \
                    '--snippet-lines[Snippet length]:number' \
                    '--max-line-width[Clamp long single-line snippets]:number' \
                    '--since[Filter by modified-since timestamp]:datetime' \
                    '--no-dedup[Show duplicate chunks]' \
                    '--exact[Backward-compatible exact shorthand]' \
                    '--exact-substring[Search-only exact substring match]' \
                    '*:query'
            else
                _arguments \
                    '--db[Database path]:file:_files' \
                    '--json[JSON output]' \
                    '--limit[Max results]:number' \
                    '--lang[Filter by language]:language:({GetCompletionLangs()})' \
                    '--kind[Filter by kind]:kind:(function class struct interface enum property event delegate namespace import)' \
                    '--path[Path filter]:pattern' \
                    '--exclude-path[Exclude path]:pattern' \
                    '--exclude-tests[Exclude tests]' \
                    '--body[Include body]' \
                    '--count[Count only]' \
                    '--exact[Backward-compatible exact shorthand]' \
                    '--exact-name[Exact symbol-name equality]' \
                    '*:query'
            fi
            ;;
    esac
}}
_cdidx";
    }

    private static string GetFishCompletions()
    {
        var lines = new List<string>
        {
            "# cdidx fish completions",
        };
        foreach (var cmd in Commands)
            lines.Add($"complete -c cdidx -n '__fish_use_subcommand' -a '{cmd}' -d '{cmd} command'");
        lines.Add("complete -c cdidx -n '__fish_use_subcommand' -l help -d 'Show help'");
        lines.Add("complete -c cdidx -n '__fish_use_subcommand' -l version -d 'Show version'");
        lines.Add("complete -c cdidx -n '__fish_use_subcommand' -l license -d 'Show license summary'");

        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search definition references callers callees symbols files find excerpt map inspect outline status' -l db -r -d 'Database path'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search definition references callers callees symbols files find excerpt map inspect outline status' -l json -d 'JSON output'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from status' -l check -d 'Verify index matches current workspace'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from db' -l db -r -d 'Database path'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from db' -l json -d 'JSON output'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from db' -l integrity-check -d 'Run PRAGMA integrity_check on the database'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search definition references callers callees symbols files find' -l limit -r -d 'Max results'");
        lines.Add($"complete -c cdidx -n '__fish_seen_subcommand_from search definition references callers callees symbols files find' -l lang -r -a '{GetCompletionLangs()}' -d 'Filter by language'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search definition references callers callees symbols files find' -l count -d 'Count only'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search definition references callers callees symbols files find' -l path -r -d 'Path filter'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search definition references callers callees symbols files find' -l exclude-path -r -d 'Exclude path'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search definition references callers callees symbols files find' -l exclude-tests -d 'Exclude tests'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from find' -l query -r -d 'Literal query'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from find excerpt' -l before -r -d 'Context lines before'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from find excerpt' -l after -r -d 'Context lines after'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from excerpt' -l start -r -d 'Start line'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from excerpt' -l end -r -d 'End line'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search references excerpt find inspect' -l max-line-width -r -d 'Clamp long single-line payloads (0 disables clamping)'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from excerpt' -l focus-line -r -d 'Focused line to keep visible when clamping'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from excerpt' -l focus-column -r -d 'Focused column to keep visible when clamping'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from excerpt' -l focus-length -r -d 'Focused span width when clamping'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search find' -l exact -d 'Exact match'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from definition inspect' -l body -d 'Include body'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search' -l fts -d 'Raw FTS5 syntax'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search' -l snippet-lines -r -d 'Snippet length'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from hotspots' -l group-by-name -d 'Collapse same-name rows across files'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search definition references callers callees symbols inspect' -l exact -d 'Backward-compatible exact shorthand'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from search' -l exact-substring -d 'Search-only exact substring match'");
        lines.Add("complete -c cdidx -n '__fish_seen_subcommand_from definition references callers callees symbols inspect' -l exact-name -d 'Exact symbol-name equality'");
        return string.Join(Environment.NewLine, lines);
    }

    // --- Helpers / ヘルパー ---

    private static ColorMode _colorMode = ColorMode.Auto;
    private static ColorPalette? _explicitPalette;

    /// <summary>
    /// Set the active color-output mode. <see cref="ColorMode.Always"/> and
    /// <see cref="ColorMode.Never"/> short-circuit env / TTY checks in
    /// <see cref="ShouldUseColor"/>; <see cref="ColorMode.Auto"/> defers to
    /// the existing CLICOLOR_FORCE / NO_COLOR / CLICOLOR / TTY chain.
    /// 色出力モードを設定する。Always / Never は環境変数と TTY 判定を上書きする。
    /// </summary>
    public static void SetColorMode(ColorMode mode) => _colorMode = mode;

    internal static ColorMode GetColorMode() => _colorMode;

    /// <summary>
    /// Override the active ANSI palette. <c>null</c> restores auto-detection
    /// via <c>COLORTERM</c> / <c>TERM</c> / <c>CDIDX_COLOR_PALETTE</c>.
    /// </summary>
    public static void SetColorPalette(ColorPalette? palette) => _explicitPalette = palette;

    internal static ColorPalette? GetExplicitColorPalette() => _explicitPalette;

    /// <summary>
    /// Parse a user-supplied `--palette` value. Accepts `basic`, `256`,
    /// `color256`, `truecolor`, and `24bit` (case-insensitive). Returns false
    /// on any other value.
    /// `--palette` 値を解析する。`basic` / `256` / `truecolor` などを許可する。
    /// </summary>
    public static bool TryParseColorPalette(string? value, out ColorPalette palette)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "basic":
            case "8":
            case "16":
            case "ansi":
                palette = ColorPalette.Basic;
                return true;
            case "256":
            case "color256":
            case "8bit":
                palette = ColorPalette.Color256;
                return true;
            case "truecolor":
            case "24bit":
            case "rgb":
                palette = ColorPalette.Truecolor;
                return true;
            default:
                palette = ColorPalette.Basic;
                return false;
        }
    }

    /// <summary>
    /// Resolve the palette to use. Honors the explicit override set via
    /// <see cref="SetColorPalette"/> first, then falls back to the
    /// <c>CDIDX_COLOR_PALETTE</c> environment variable, then to capability
    /// detection from <c>COLORTERM</c> / <c>TERM</c>.
    /// </summary>
    public static ColorPalette ResolveColorPalette()
    {
        if (_explicitPalette is { } explicitPalette)
            return explicitPalette;

        var envPalette = Environment.GetEnvironmentVariable("CDIDX_COLOR_PALETTE");
        if (!string.IsNullOrWhiteSpace(envPalette) && TryParseColorPalette(envPalette, out var parsed))
            return parsed;

        return DetectColorPalette();
    }

    /// <summary>
    /// Detect the terminal palette from the <c>COLORTERM</c> and <c>TERM</c>
    /// environment variables. <c>COLORTERM=truecolor</c> / <c>COLORTERM=24bit</c>
    /// → <see cref="ColorPalette.Truecolor"/>. <c>TERM</c> containing
    /// <c>256color</c> (e.g. <c>xterm-256color</c>, <c>screen-256color</c>) →
    /// <see cref="ColorPalette.Color256"/>. Otherwise <see cref="ColorPalette.Basic"/>.
    /// </summary>
    internal static ColorPalette DetectColorPalette()
    {
        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        if (!string.IsNullOrEmpty(colorTerm))
        {
            var ct = colorTerm.Trim().ToLowerInvariant();
            if (ct == "truecolor" || ct == "24bit")
                return ColorPalette.Truecolor;
        }

        var term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrEmpty(term))
        {
            var t = term.ToLowerInvariant();
            if (t.Contains("256color", StringComparison.Ordinal))
                return ColorPalette.Color256;
            if (t.Contains("truecolor", StringComparison.Ordinal) || t.Contains("direct", StringComparison.Ordinal))
                return ColorPalette.Truecolor;
        }

        return ColorPalette.Basic;
    }

    /// <summary>
    /// Parse a user-supplied `--color` value. Accepts `auto`, `always`, and
    /// `never` (case-insensitive). Returns false on any other value.
    /// `--color` 値を解析する。`auto` / `always` / `never` のみ許可。
    /// </summary>
    public static bool TryParseColorMode(string? value, out ColorMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "auto":
                mode = ColorMode.Auto;
                return true;
            case "always":
                mode = ColorMode.Always;
                return true;
            case "never":
                mode = ColorMode.Never;
                return true;
            default:
                mode = ColorMode.Auto;
                return false;
        }
    }

    /// <summary>
    /// Colorize a symbol kind name with ANSI escape codes for terminal output.
    /// Honors the active <see cref="ColorMode"/>; in <see cref="ColorMode.Auto"/>
    /// falls back to <see cref="ShouldUseColor"/>'s env + TTY policy.
    /// シンボル種別名を ANSI エスケープコードで色付けする。<see cref="ColorMode"/> を尊重し、
    /// auto では環境変数と TTY 自動判定にフォールバックする。
    /// </summary>
    public static string ColorizeKind(string kind, int padWidth = 0)
    {
        var padded = padWidth > 0 ? kind.PadRight(padWidth) : kind;
        if (ShouldUseColor())
        {
            var color = GetKindColorCode(kind, ResolveColorPalette());
            if (color.Length > 0)
                return $"{color}{padded}\x1b[0m";
        }
        return padded;
    }

    // Per-palette SGR introducer for a given symbol kind. Basic stays within
    // the 8 standard ANSI colors (30–37) and intentionally avoids
    // `\x1b[90m` (bright-black / dim), which is unreadable on many minimal
    // SSH / CI terminals; namespace / import fall back to plain white (37).
    // 各パレットでのシンボル種別ごとの SGR コード。Basic は標準8色のみで
    // dim (`\x1b[90m`) を避け、SSH/CI 端末でも可読性を確保する。
    internal static string GetKindColorCode(string kind, ColorPalette palette) => palette switch
    {
        ColorPalette.Truecolor => kind switch
        {
            "class" => "\x1b[38;2;102;217;239m",     // bright cyan
            "struct" => "\x1b[38;2;102;217;239m",
            "interface" => "\x1b[38;2;102;160;255m",  // bright blue
            "enum" => "\x1b[38;2;215;110;215m",       // bright magenta
            "function" => "\x1b[38;2;255;215;75m",    // gold yellow
            "property" => "\x1b[38;2;160;230;100m",   // bright green
            "event" => "\x1b[38;2;255;100;100m",      // bright red
            "delegate" => "\x1b[38;2;215;110;215m",
            "namespace" => "\x1b[38;2;180;180;180m",  // light gray (readable on dark + light bg)
            "import" => "\x1b[38;2;180;180;180m",
            _ => "",
        },
        ColorPalette.Color256 => kind switch
        {
            "class" => "\x1b[38;5;81m",     // cyan
            "struct" => "\x1b[38;5;81m",
            "interface" => "\x1b[38;5;75m",  // blue
            "enum" => "\x1b[38;5;213m",      // magenta
            "function" => "\x1b[38;5;221m",  // gold
            "property" => "\x1b[38;5;120m",  // green
            "event" => "\x1b[38;5;203m",     // salmon red
            "delegate" => "\x1b[38;5;213m",
            "namespace" => "\x1b[38;5;245m", // medium gray (not as dim as 90m)
            "import" => "\x1b[38;5;245m",
            _ => "",
        },
        _ => kind switch
        {
            "class" => "\x1b[36m",      // cyan / シアン
            "struct" => "\x1b[36m",     // cyan / シアン
            "interface" => "\x1b[34m",  // blue / 青
            "enum" => "\x1b[35m",       // magenta / マゼンタ
            "function" => "\x1b[33m",   // yellow / 黄
            "property" => "\x1b[32m",   // green / 緑
            "event" => "\x1b[31m",      // red / 赤
            "delegate" => "\x1b[35m",   // magenta / マゼンタ
            "namespace" => "\x1b[37m",  // white (instead of dim 90m) / 白（dim 回避）
            "import" => "\x1b[37m",     // white (instead of dim 90m) / 白（dim 回避）
            _ => "",
        },
    };

    internal static bool ShouldUseInteractiveConsole()
    {
        if (Console.IsOutputRedirected)
            return false;

        // StringWriter-based test capture leaves the process console attached, so
        // Console.IsOutputRedirected stays false even though interactive terminal
        // behavior would be unsafe. Treat UTF-16 Console.Out as redirected capture.
        return !Console.Out.Encoding.Equals(Encoding.Unicode);
    }

    /// <summary>
    /// Decide whether ANSI color escapes should be emitted. Precedence (highest first):
    ///   1. Explicit <see cref="ColorMode"/> from `--color` flag (Always/Never short-circuit).
    ///   2. CLICOLOR_FORCE (any non-empty value other than "0") — force color on.
    ///   3. NO_COLOR (any non-empty value) — color off.
    ///   4. CLICOLOR=0 — color off.
    ///   5. Otherwise fall back to <see cref="ShouldUseInteractiveConsole"/>.
    /// ANSI 色エスケープを出力するかを判定する。`--color` フラグ > 環境変数 > TTY 判定。
    /// </summary>
    public static bool ShouldUseColor()
    {
        if (_colorMode == ColorMode.Always)
            return true;
        if (_colorMode == ColorMode.Never)
            return false;
        if (IsForceColorRequested())
            return true;
        if (IsNoColorRequested())
            return false;
        return ShouldUseInteractiveConsole();
    }

    private static bool IsForceColorRequested()
    {
        var force = Environment.GetEnvironmentVariable("CLICOLOR_FORCE");
        return !string.IsNullOrEmpty(force) && force != "0";
    }

    private static bool IsNoColorRequested()
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR");
        if (!string.IsNullOrEmpty(noColor))
            return true;

        var cliColor = Environment.GetEnvironmentVariable("CLICOLOR");
        return cliColor == "0";
    }

    /// <summary>
    /// Get console window width safely (some environments throw IOException).
    /// コンソール幅を安全に取得する（一部環境ではIOExceptionが発生する）。
    /// </summary>
    private static int GetWindowWidth()
    {
        try
        {
            var w = Console.WindowWidth;
            return w > 0 ? w : 80;
        }
        catch
        {
            return 80;
        }
    }
}
