using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodeIndex.Cli;

public static partial class ConsoleUi
{
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

    public static void PrintIndexCompleteSummary(
        string projectRoot,
        string resolvedDbPath,
        bool incremental,
        int filesScanned,
        IReadOnlyDictionary<string, int> languageCounts)
    {
        Console.WriteLine(incremental ? "Next steps (incremental):" : "Next steps:");
        Console.WriteLine("  - Search code: cdidx search \"authenticate\" --path src/");
        Console.WriteLine("  - Find a definition: cdidx definition SymbolName");
        Console.WriteLine($"  - Start MCP: cdidx mcp --db {QuoteForDisplay(resolvedDbPath)}");
        Console.WriteLine($"  - Database: {resolvedDbPath}");
        Console.WriteLine("  - Exclude paths with .gitignore or .cdidxignore, then rerun cdidx index .");
        Console.WriteLine($"  - Scanned {Counted(filesScanned, "file", format: "N0")} under {projectRoot}");
        if (languageCounts.Count > 0)
        {
            var summary = string.Join(
                ", ",
                languageCounts
                    .OrderByDescending(static pair => pair.Value)
                    .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Take(6)
                    .Select(static pair => $"{pair.Key} {pair.Value.ToString("N0", CultureInfo.InvariantCulture)}"));
            Console.WriteLine($"  - Languages: {summary}");
        }
        Console.WriteLine();
    }

    public static void EmitCompletionNotification(CompletionNotificationMode mode, string message)
    {
        var resolved = mode == CompletionNotificationMode.Auto
            ? ShouldUseInteractiveConsole() ? CompletionNotificationMode.Bell : CompletionNotificationMode.None
            : mode;
        if (resolved == CompletionNotificationMode.None)
            return;

        var safeMessage = message.Replace('\r', ' ').Replace('\n', ' ');
        if (resolved == CompletionNotificationMode.Osc9)
            Console.Error.Write($"\u001b]9;{safeMessage}\a");
        else
            Console.Error.Write('\a');
        Console.Error.Flush();
    }

    private static string QuoteForDisplay(string value)
        => value.IndexOfAny([' ', '\t', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    // --- Easter eggs / イースターエッグ ---

    /// <summary>
    /// Print easter egg message (standalone mode). Renders the catalog entry for
    /// <paramref name="flag"/> in the language chosen by <see cref="UiLanguageResolver"/>
    /// (<c>CDIDX_LANG</c> env > <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>
    /// > English fallback). Unknown flags print two blank lines for legacy compatibility.
    /// Pass <paramref name="languageOverride"/> to bypass env/culture resolution (used by
    /// tests so they do not mutate the live process environment).
    /// イースターエッグメッセージを表示（単体実行時）。<see cref="UiLanguageResolver"/>
    /// が選んだ言語（<c>CDIDX_LANG</c> 環境変数 &gt; カルチャ &gt; 英語）でカタログ
    /// エントリを描画する。未知フラグは従来互換で空行を2つ出力。
    /// <paramref name="languageOverride"/> を指定すると環境変数/カルチャ判定をスキップする
    /// （テストがプロセス環境を書き換えずに済むようにするためのフック）。
    /// </summary>
    public static void PrintEasterEggMessage(string flag, UiLanguage? languageOverride = null)
    {
        var pair = flag switch
        {
            "--sushi" => UiMessages.EasterEggSushi,
            "--coffee" => UiMessages.EasterEggCoffee,
            "--ramen" => UiMessages.EasterEggRamen,
            "--wine" => UiMessages.EasterEggWine,
            "--beer" => UiMessages.EasterEggBeer,
            "--matcha" => UiMessages.EasterEggMatcha,
            "--whisky" => UiMessages.EasterEggWhisky,
            _ => null,
        };
        if (pair is null)
        {
            Console.WriteLine();
            Console.WriteLine();
            return;
        }

        var lang = languageOverride ?? UiLanguageResolver.Resolve();
        foreach (var line in UiMessages.Render(pair, lang))
            Console.WriteLine(line);
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
        if (!File.Exists(LongPath.EnsureWindowsPrefix(path)))
        {
            // Fallback: look relative to current directory / カレントディレクトリからの相対パスでフォールバック
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.json");
        }
        var ioPath = LongPath.EnsureWindowsPrefix(path);
        if (File.Exists(ioPath))
            return LoadVersionFromFile(ioPath);

        return FallbackVersion;
    }

    internal static string LoadVersionFromFile(string ioPath)
    {
        try
        {
            var json = DataDirectorySecurity.ReadTextWithinLimit(ioPath, MaxVersionJsonBytes);
            if (json is null)
                return FallbackVersion;

            using var doc = BoundedJson.ParseDocument(json, MaxVersionJsonBytes, MaxVersionJsonDepth);
            if (doc.RootElement.TryGetProperty("version", out var ver))
                return ver.GetString() ?? FallbackVersion;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or InvalidOperationException)
        {
            return FallbackVersion;
        }

        return FallbackVersion;
    }

    /// <summary>
    /// Format byte counts for human-facing CLI output using binary units.
    /// 人間向けCLI出力用にバイト数を2進単位で整形する。
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} bytes");
        if (bytes < 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} bytes");

        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < ByteUnits.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:N1} {ByteUnits[unitIndex]}");
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
}
