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
    internal static bool ShouldUseUnicodeGlyphs()
    {
        if (IsAsciiOutputRequested())
            return false;

        if (IsDumbTerminal())
            return false;

        var locale = FirstNonEmptyEnvironmentVariable("LC_ALL", "LC_CTYPE", "LANG");
        if (locale != null && !IsUnicodeLocale(locale))
            return false;

        return Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage
            || Console.OutputEncoding.CodePage == Encoding.Unicode.CodePage;
    }

    private static bool IsAsciiOutputRequested()
    {
        if (_asciiOutputForced)
            return true;

        var ascii = CdidxEnvironment.GetEnvironmentVariable("CDIDX_ASCII");
        if (!string.IsNullOrEmpty(ascii) && ascii != "0")
            return true;

        var noUnicode = CdidxEnvironment.GetEnvironmentVariable("NO_UNICODE");
        if (!string.IsNullOrEmpty(noUnicode) && noUnicode != "0")
            return true;

        var atBridgeType = CdidxEnvironment.GetEnvironmentVariable("AT_BRIDGE_TYPE");
        if (!string.IsNullOrEmpty(atBridgeType))
            return true;

        var accessibilityEnabled = CdidxEnvironment.GetEnvironmentVariable("ACCESSIBILITY_ENABLED");
        if (!string.IsNullOrEmpty(accessibilityEnabled) && accessibilityEnabled != "0")
            return true;

        return IsPosixLocale(CdidxEnvironment.GetEnvironmentVariable("LC_ALL"))
            || IsPosixLocale(CdidxEnvironment.GetEnvironmentVariable("LC_CTYPE"))
            || IsPosixLocale(CdidxEnvironment.GetEnvironmentVariable("LANG"));
    }

    private static bool IsTruthyEnvironmentVariable(string name)
        => IsTruthyEnvironmentValue(CdidxEnvironment.GetEnvironmentVariable(name));

    private static bool IsTruthyEnvironmentValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Trim() is not ("0" or "false" or "False" or "FALSE" or "no" or "No" or "NO");
    }

    private static bool IsDumbTerminal()
        => string.Equals(CdidxEnvironment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);

    private static bool IsPosixLocale(string? locale)
        => locale != null
            && (locale.Equals("C", StringComparison.OrdinalIgnoreCase)
                || locale.Equals("POSIX", StringComparison.OrdinalIgnoreCase));

    private static bool IsUnicodeLocale(string locale)
        => locale.Contains(".UTF-8", StringComparison.OrdinalIgnoreCase)
            || locale.Contains(".UTF8", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmptyEnvironmentVariable(params string[] names)
    {
        foreach (var name in names)
        {
            var value = CdidxEnvironment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }

    internal static void SetAsciiOutput(bool enabled) => _asciiOutputForced = enabled;

    internal static bool IsAsciiOutputForced() => _asciiOutputForced;

    internal static bool WidthDetectionFailed => _widthDetectionFailed;

    internal static void SetWidthDetectionTracing(bool enabled) => _traceWidthDetectionFailures = enabled;

    /// <summary>
    /// Get console window width safely (some environments throw IOException).
    /// コンソール幅を安全に取得する（一部環境ではIOExceptionが発生する）。
    /// </summary>
    internal static int GetWindowWidth()
    {
        if (TryGetColumnsEnvironmentWidth(out var columnsWidth))
            return columnsWidth;

        try
        {
            var w = Console.WindowWidth;
            if (w > 0)
                return w;
        }
        catch (IOException ex)
        {
            return GetFallbackWindowWidth(ex);
        }
        catch (NotSupportedException ex)
        {
            return GetFallbackWindowWidth(ex);
        }

        return GetFallbackWindowWidth(null);
    }

    private static int GetFallbackWindowWidth(Exception? exception)
    {
        _widthDetectionFailed = true;
        if (_traceWidthDetectionFailures && !_widthDetectionTraceWritten)
        {
            var suffix = exception == null ? string.Empty : $" ({CommandErrorWriter.FormatSanitizedExceptionDetail(exception)})";
            CommandErrorWriter.WriteStderr($"cdidx: console width detection failed; using COLUMNS or 80 columns{suffix}");
            _widthDetectionTraceWritten = true;
        }

        return TryGetColumnsEnvironmentWidth(out var columnsWidth) ? columnsWidth : 80;
    }

    private static bool TryGetColumnsEnvironmentWidth(out int width)
    {
        var columns = CdidxEnvironment.GetEnvironmentVariable("COLUMNS");
        if (int.TryParse(columns, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) && width > 0)
            return true;

        width = 0;
        return false;
    }

    private sealed class JsonOutputScope : IDisposable
    {
        public void Dispose()
        {
            if (JsonOutputDepth.Value > 0)
                JsonOutputDepth.Value--;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}
