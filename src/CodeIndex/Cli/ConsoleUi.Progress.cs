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
    public static CancellationTokenSource? StartSpinner(
        string message,
        string[] frames,
        bool writeToStandardError = false)
    {
        EnsureConsoleWritersSynchronized();
        var output = writeToStandardError ? Console.Error : Console.Out;

        // Braille frames are single-char; themed frames are longer strings containing the display text
        // ブレイルフレームは1文字、テーマフレームは表示テキストを含む長い文字列
        bool isThemed = frames.Length > 0 && frames[0].Length > 2;

        if (!ShouldUseInteractiveConsole() || !ShouldUseProgressAnimation())
        {
            output.WriteLine(message);
            return null;
        }

        var cts = new SpinnerCancellationTokenSource(output);
        var ct = cts.Token;
        var spinnerTask = BackgroundTaskObserver.Run(async token =>
        {
            int i = 0;
            while (!token.IsCancellationRequested)
            {
                var frame = frames[i % frames.Length];
                var line = isThemed ? $"\r{frame}" : $"\r{frame} {message}";
                lock (TerminalLock)
                {
                    output.Write(line);
                    output.Flush();
                }
                i++;
                try { await Task.Delay(SpinnerFrameDelayMs, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }, "cdidx", "console spinner", ct);
        cts.SetSpinnerTask(spinnerTask);
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
        if (cts is SpinnerCancellationTokenSource spinnerCts)
        {
            try
            {
                spinnerCts.SpinnerTask.GetAwaiter().GetResult();
            }
            catch
            {
                // Spinner shutdown is best-effort; BackgroundTaskObserver reports faults.
                // spinner shutdown は best-effort。fault は BackgroundTaskObserver が報告する。
            }
        }
        if (ShouldUseInteractiveConsole())
        {
            var output = cts is SpinnerCancellationTokenSource ownedSpinner
                ? ownedSpinner.Output
                : Console.Out;
            lock (TerminalLock)
            {
                output.Write($"\r{new string(' ', GetWindowWidth() - ConsoleLineMargin)}\r");
                output.Flush();
            }
        }
        cts.Dispose();
    }

    private sealed class SpinnerCancellationTokenSource(TextWriter output) : CancellationTokenSource
    {
        private Task _spinnerTask = Task.CompletedTask;

        public Task SpinnerTask => _spinnerTask;
        public TextWriter Output { get; } = output;

        public void SetSpinnerTask(Task spinnerTask)
            => _spinnerTask = spinnerTask;
    }

    internal static void SetProgressAnimationEnabled(bool? enabled)
        => _progressAnimationEnabledOverride = enabled;

    internal static bool? GetProgressAnimationOverrideForDiagnostics()
        => _progressAnimationEnabledOverride;

    internal static bool ShouldUseProgressAnimation()
    {
        if (_progressAnimationEnabledOverride.HasValue)
            return _progressAnimationEnabledOverride.Value;

        if (IsTruthyEnvironmentVariable(DisableProgressEnvironmentVariable))
            return false;

        var reducedMotion = CdidxEnvironment.GetEnvironmentVariable(PrefersReducedMotionEnvironmentVariable);
        return string.IsNullOrWhiteSpace(reducedMotion) || !IsTruthyEnvironmentValue(reducedMotion);
    }

    /// <summary>
    /// Get spinner frames based on easter egg flag.
    /// イースターエッグフラグに基づくスピナーフレームを取得。
    /// </summary>
    public static string[] GetSpinnerFrames(string? easterEgg)
    {
        if (!ShouldUseUnicodeGlyphs())
            return AsciiSpinnerFrames;

        return easterEgg switch
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
    }

    // --- Progress bar / プログレスバー ---

    // Active spinner frames for progress bar (themed or default braille)
    // プログレスバー用アクティブスピナーフレーム（テーマ付きまたはデフォルトブレイル）
    private static string[] _progressSpinnerFrames = DefaultBrailleSpinnerFrames;
    // Track last progress line length for clearing / クリア用に最後のプログレス行の長さを記録
    private static int _lastProgressLineLength;
    private static bool _asciiOutputForced;
    private static bool? _progressAnimationEnabledOverride;
    private static bool _widthDetectionFailed;
    private static bool _widthDetectionTraceWritten;
    private static bool _traceWidthDetectionFailures;

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

        var line = FormatProgressLine(
            current,
            total,
            redirected ? 80 : GetWindowWidth(),
            ShouldUseUnicodeGlyphs(),
            ShouldUseProgressAnimation());

        if (!redirected)
        {
            lock (TerminalLock)
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
        }
        else
        {
            // Fallback for redirected output / リダイレクト時はフォールバック
            output.WriteLine(line.TrimStart());
        }
    }

    internal static string FormatProgressLine(
        int current,
        int total,
        int windowWidth,
        bool useUnicodeGlyphs,
        bool useProgressAnimation = true)
    {
        const int barWidth = 32;
        var pct = (double)current / total;
        var percentAndCounts = string.Create(
            CultureInfo.InvariantCulture,
            $"{pct * 100,5:F1}%  [{current:N0}/{total:N0}]");

        if (useUnicodeGlyphs && windowWidth < 40)
            return percentAndCounts;

        int filled = (int)Math.Round(pct * barWidth);
        if (filled > barWidth) filled = barWidth;
        if (filled < 0) filled = 0;

        var spinner = useProgressAnimation ? ResolveProgressSpinner(current, total, useUnicodeGlyphs) : " ";
        var bar = useUnicodeGlyphs
            ? new string('\u2588', filled) + new string('\u2591', barWidth - filled)
            : $"[{new string('#', filled)}{new string('-', barWidth - filled)}]";
        return $"{spinner} {bar} {percentAndCounts}";
    }

    private static string ResolveProgressSpinner(int current, int total, bool useUnicodeGlyphs)
    {
        if (current == total)
            return " ";

        return useUnicodeGlyphs
            ? _progressSpinnerFrames[(current / 50) % _progressSpinnerFrames.Length]
            : "-";
    }

    /// <summary>
    /// Clear the current progress bar line so other output can be printed cleanly.
    /// 他の出力を正しく表示するために現在のプログレスバー行をクリア。
    /// </summary>
    public static void ClearProgressLine()
    {
        lock (TerminalLock)
        {
            ClearProgressLineCore();
        }
    }

    private static void ClearProgressLineCore()
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
        lock (TerminalLock)
        {
            ClearProgressLineCore();
            CommandErrorWriter.WriteStderr($"  [WARN] {message}");
            Console.Error.Flush();
            Console.Out.Flush();
        }
    }

    // --- Banner / バナー ---

    /// <summary>
    /// Print ASCII-art banner.
    /// ASCIIアートバナーを表示。
    /// </summary>
}
