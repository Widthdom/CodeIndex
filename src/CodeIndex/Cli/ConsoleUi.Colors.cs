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
    internal static ColorMode GetColorModeForDiagnostics()
        => _colorMode;

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

        var envPalette = CdidxEnvironment.GetEnvironmentVariable("CDIDX_COLOR_PALETTE");
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
        var colorTerm = CdidxEnvironment.GetEnvironmentVariable("COLORTERM");
        if (!string.IsNullOrEmpty(colorTerm))
        {
            var ct = colorTerm.Trim().ToLowerInvariant();
            if (ct == "truecolor" || ct == "24bit")
                return ColorPalette.Truecolor;
        }

        var term = CdidxEnvironment.GetEnvironmentVariable("TERM");
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
        if (JsonOutputDepth.Value <= 0 && ShouldUseColor())
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
        => ShouldUseInteractiveConsole(
            Console.IsOutputRedirected,
            Console.Out.Encoding,
            Console.Out is StringWriter,
            HasTerminalEnvironmentHint(),
            IsTerminalEnvironmentDisabled(),
            OperatingSystem.IsWindows());

    internal static bool ShouldUseInteractiveConsole(
        bool isOutputRedirected,
        Encoding outputEncoding,
        bool isTextWriterCapture,
        bool hasTerminalEnvironmentHint,
        bool isTerminalEnvironmentDisabled,
        bool isWindows)
    {
        if (isOutputRedirected)
            return false;

        if (isTerminalEnvironmentDisabled)
            return false;

        // StringWriter-based test capture leaves the process console attached, so
        // Console.IsOutputRedirected stays false even though interactive terminal
        // behavior would be unsafe. Detect it directly instead of inferring from
        // encoding, because real terminals may expose UTF-8 or UTF-16 independently
        // of ConPTY/ANSI support.
        if (isTextWriterCapture)
            return false;

        return isWindows || hasTerminalEnvironmentHint;
    }

    internal static bool ShouldUseAnsiOutput()
        => ShouldUseAnsiOutput(
            Console.IsOutputRedirected,
            Console.Out.Encoding,
            Console.Out is StringWriter,
            HasTerminalEnvironmentHint(),
            IsTerminalEnvironmentDisabled(),
            OperatingSystem.IsWindows(),
            GetWindowsVirtualTerminalProcessingEnabled());

    internal static bool ShouldUseAnsiOutput(
        bool isOutputRedirected,
        Encoding outputEncoding,
        bool isTextWriterCapture,
        bool hasTerminalEnvironmentHint,
        bool isTerminalEnvironmentDisabled,
        bool isWindows,
        bool windowsVirtualTerminalProcessingEnabled)
    {
        if (!ShouldUseInteractiveConsole(isOutputRedirected, outputEncoding, isTextWriterCapture, hasTerminalEnvironmentHint, isTerminalEnvironmentDisabled, isWindows))
            return false;

        if (!isWindows)
            return true;

        return windowsVirtualTerminalProcessingEnabled || hasTerminalEnvironmentHint;
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
        return ShouldUseAnsiOutput();
    }

    private static bool HasTerminalEnvironmentHint()
    {
        if (!string.IsNullOrEmpty(CdidxEnvironment.GetEnvironmentVariable("WT_SESSION")))
            return true;
        if (!string.IsNullOrEmpty(CdidxEnvironment.GetEnvironmentVariable("WT_PROFILE_ID")))
            return true;
        if (!string.IsNullOrEmpty(CdidxEnvironment.GetEnvironmentVariable("TERM_PROGRAM")))
            return true;

        var term = CdidxEnvironment.GetEnvironmentVariable("TERM");
        return !string.IsNullOrWhiteSpace(term)
            && !term.Equals("dumb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalEnvironmentDisabled()
        => IsDumbTerminal() || IsCiEnvironment();

    private static bool IsCiEnvironment()
    {
        var ci = CdidxEnvironment.GetEnvironmentVariable("CI");
        return !string.IsNullOrEmpty(ci)
            && !ci.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !ci.Equals("false", StringComparison.OrdinalIgnoreCase)
            && !ci.Equals("no", StringComparison.OrdinalIgnoreCase)
            && !ci.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private static bool GetWindowsVirtualTerminalProcessingEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (_windowsVirtualTerminalProcessingEnabled is { } cached)
            return cached;

        var detected = (_windowsVirtualTerminalProcessingDetectorForTests ?? DetectWindowsVirtualTerminalProcessing)();
        _windowsVirtualTerminalProcessingEnabled = detected;
        return detected;
    }

    private static bool DetectWindowsVirtualTerminalProcessing()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return false;

        return GetConsoleMode(handle, out var mode)
            && (mode & EnableVirtualTerminalProcessing) != 0;
    }

    internal static void SetWindowsVirtualTerminalProcessingDetectorForTests(Func<bool>? detector)
    {
        _windowsVirtualTerminalProcessingDetectorForTests = detector;
        _windowsVirtualTerminalProcessingEnabled = null;
    }

    internal static void ResetTerminalCapabilityCacheForTests()
    {
        _windowsVirtualTerminalProcessingEnabled = null;
        _windowsVirtualTerminalProcessingDetectorForTests = null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    private static bool IsForceColorRequested()
    {
        var force = CdidxEnvironment.GetEnvironmentVariable("CLICOLOR_FORCE");
        return !string.IsNullOrEmpty(force) && force != "0";
    }

    private static bool IsNoColorRequested()
    {
        var noColor = CdidxEnvironment.GetEnvironmentVariable("NO_COLOR");
        if (!string.IsNullOrEmpty(noColor))
            return true;

        var cliColor = CdidxEnvironment.GetEnvironmentVariable("CLICOLOR");
        return cliColor == "0";
    }

}
