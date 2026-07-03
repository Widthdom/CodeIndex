using System.Text;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    // Extensionless shebang detection reads at most the first physical line within this
    // byte cap. NUL bytes or a line that reaches the cap without LF/CR are treated as
    // unsupported so binary executables and minified data are not parsed as scripts.
    private const int ShebangProbeByteLimit = 256;
    private static readonly UTF8Encoding StrictShebangUtf8Encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictShebangUtf16LittleEndianEncoding = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictShebangUtf16BigEndianEncoding = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Try to infer a language from an extensionless script shebang.
    /// This is a cheap fallback used only after extension and exact-filename checks fail.
    /// It reads at most <see cref="ShebangProbeByteLimit"/> bytes from the first line;
    /// NUL bytes and over-cap first lines are treated as non-scripts.
    /// 拡張子・完全一致ファイル名で判定できない場合だけ、拡張子なしスクリプトの shebang から言語を推定する。
    /// </summary>
    private static LanguageDetectionResult TryDetectLanguageFromShebang(
        string filePath,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot,
        FileProbeStatus? knownIndexability)
    {
        var indexability = knownIndexability ?? GetFileIndexability(filePath, symlinkPolicy, projectRoot);
        if (indexability == FileProbeStatus.Missing)
            return new LanguageDetectionResult(FileProbeStatus.Missing, null);

        if (indexability != FileProbeStatus.Supported)
            return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

        try
        {
            using var stream = BoundedFile.OpenReadForPrefixProbe(filePath);
            if (!stream.CanRead)
                return new LanguageDetectionResult(FileProbeStatus.ProbeFailed, null);

            Span<byte> buffer = stackalloc byte[ShebangProbeByteLimit];
            var bytesRead = stream.Read(buffer);
            if (bytesRead <= 0)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var bytes = buffer[..bytesRead];
            var shebangEncoding = DetectShebangEncoding(bytes);
            if (shebangEncoding == ShebangEncoding.Unsupported)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            if ((shebangEncoding == ShebangEncoding.Utf8 || shebangEncoding == ShebangEncoding.Utf8Bom)
                && bytes.Contains((byte)0))
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var preambleLength = GetShebangPreambleLength(shebangEncoding);
            if (!HasRawShebangPrefix(bytes, shebangEncoding, preambleLength))
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var lineEnd = FindShebangLineEnd(bytes, shebangEncoding, preambleLength);
            if (lineEnd < 0)
            {
                if (bytesRead == ShebangProbeByteLimit)
                    return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);
                lineEnd = bytesRead;
            }

            var firstLineBytes = bytes[preambleLength..lineEnd];
            var firstLine = DecodeShebangLine(firstLineBytes, shebangEncoding);

            if (firstLine.StartsWith('\uFEFF'))
                firstLine = firstLine[1..];

            if (!firstLine.StartsWith("#!", StringComparison.Ordinal))
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var commandLine = firstLine[2..].Trim();
            if (string.IsNullOrWhiteSpace(commandLine))
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var tokens = TokenizeShebangCommandLine(commandLine);
            if (tokens.Count == 0)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var interpreter = ResolveShebangInterpreter(tokens);
            if (interpreter == null)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var language = MapShebangInterpreterToLanguage(interpreter);
            return language != null
                ? new LanguageDetectionResult(FileProbeStatus.Supported, language)
                : new LanguageDetectionResult(FileProbeStatus.Unsupported, null);
        }
        catch (FileNotFoundException)
        {
            return new LanguageDetectionResult(FileProbeStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new LanguageDetectionResult(FileProbeStatus.Missing, null);
        }
        catch (IOException)
        {
            return new LanguageDetectionResult(FileProbeStatus.ProbeFailed, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new LanguageDetectionResult(FileProbeStatus.ProbeFailed, null);
        }
        catch (DecoderFallbackException)
        {
            return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);
        }
    }

    private enum ShebangEncoding
    {
        Utf8,
        Utf8Bom,
        Utf16LittleEndian,
        Utf16BigEndian,
        Unsupported,
    }

    private static ShebangEncoding DetectShebangEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                return ShebangEncoding.Unsupported;
            if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                return ShebangEncoding.Unsupported;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return ShebangEncoding.Utf8Bom;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return ShebangEncoding.Utf16LittleEndian;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return ShebangEncoding.Utf16BigEndian;

        return ShebangEncoding.Utf8;
    }

    private static int GetShebangPreambleLength(ShebangEncoding encoding) => encoding switch
    {
        ShebangEncoding.Utf8Bom => 3,
        ShebangEncoding.Utf16LittleEndian or ShebangEncoding.Utf16BigEndian => 2,
        _ => 0,
    };

    private static bool HasRawShebangPrefix(ReadOnlySpan<byte> bytes, ShebangEncoding encoding, int start)
    {
        var remaining = bytes[start..];
        return encoding switch
        {
            ShebangEncoding.Utf16LittleEndian => StartsWithUtf16LeShebang(remaining)
                || (remaining.Length >= 2
                    && remaining[0] == 0xFF
                    && remaining[1] == 0xFE
                    && StartsWithUtf16LeShebang(remaining[2..])),
            ShebangEncoding.Utf16BigEndian => StartsWithUtf16BeShebang(remaining)
                || (remaining.Length >= 2
                    && remaining[0] == 0xFE
                    && remaining[1] == 0xFF
                    && StartsWithUtf16BeShebang(remaining[2..])),
            _ => StartsWithUtf8Shebang(remaining)
                || (remaining.Length >= 3
                    && remaining[0] == 0xEF
                    && remaining[1] == 0xBB
                    && remaining[2] == 0xBF
                    && StartsWithUtf8Shebang(remaining[3..])),
        };
    }

    private static bool StartsWithUtf8Shebang(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 2 && bytes[0] == (byte)'#' && bytes[1] == (byte)'!';

    private static bool StartsWithUtf16LeShebang(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 4
            && bytes[0] == (byte)'#'
            && bytes[1] == 0
            && bytes[2] == (byte)'!'
            && bytes[3] == 0;

    private static bool StartsWithUtf16BeShebang(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 4
            && bytes[0] == 0
            && bytes[1] == (byte)'#'
            && bytes[2] == 0
            && bytes[3] == (byte)'!';

    private static int FindShebangLineEnd(ReadOnlySpan<byte> bytes, ShebangEncoding encoding, int start)
    {
        if (encoding is ShebangEncoding.Utf8 or ShebangEncoding.Utf8Bom)
            return bytes[start..].IndexOfAny((byte)'\r', (byte)'\n') is var lineEnd && lineEnd >= 0
                ? start + lineEnd
                : -1;

        for (var i = start; i + 1 < bytes.Length; i += 2)
        {
            var ch = encoding == ShebangEncoding.Utf16LittleEndian
                ? (bytes[i] | (bytes[i + 1] << 8))
                : ((bytes[i] << 8) | bytes[i + 1]);
            if (ch is '\r' or '\n')
                return i;
        }

        return -1;
    }

    private static string DecodeShebangLine(ReadOnlySpan<byte> bytes, ShebangEncoding encoding) => encoding switch
    {
        ShebangEncoding.Utf16LittleEndian => StrictShebangUtf16LittleEndianEncoding.GetString(bytes),
        ShebangEncoding.Utf16BigEndian => StrictShebangUtf16BigEndianEncoding.GetString(bytes),
        _ => StrictShebangUtf8Encoding.GetString(bytes),
    };

    private static IReadOnlyList<string> TokenizeShebangCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        var token = new StringBuilder(commandLine.Length);
        char? quote = null;
        var escaped = false;

        foreach (var ch in commandLine)
        {
            if (escaped)
            {
                token.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote is { } activeQuote)
            {
                if (ch == activeQuote)
                    quote = null;
                else
                    token.Append(ch);
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch is ' ' or '\t')
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }
                continue;
            }

            token.Append(ch);
        }

        if (escaped)
            token.Append('\\');
        if (token.Length > 0)
            tokens.Add(token.ToString());

        return tokens;
    }

    private static string? ResolveShebangInterpreter(IReadOnlyList<string> tokens)
    {
        var interpreter = NormalizeShebangInterpreterToken(tokens[0]);
        if (interpreter == null)
            return null;
        if (interpreter is not "env")
            return interpreter;

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == "--")
                continue;
            if (token.StartsWith("-", StringComparison.Ordinal))
                continue;

            // `env FOO=bar python` style assignments before the real interpreter.
            // `env FOO=bar python` のような代入はスキップして本体の interpreter を探す。
            if (token.Contains('='))
                continue;

            return NormalizeShebangInterpreterToken(token);
        }

        return null;
    }

    private static string? NormalizeShebangInterpreterToken(string token)
    {
        var candidate = token;
        if (token.IndexOfAny([' ', '\t']) >= 0)
        {
            var nestedTokens = TokenizeShebangCommandLine(token);
            if (nestedTokens.Count == 0)
                return null;
            candidate = nestedTokens[0];
        }

        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        return Path.GetFileName(candidate).ToLowerInvariant();
    }

    private static string? MapShebangInterpreterToLanguage(string interpreter) => interpreter switch
    {
        "bash" or "sh" or "zsh" or "fish" or "dash" or "ksh" or "ash" => "shell",
        "node" or "nodejs" => "javascript",
        "ruby" => "ruby",
        "php" => "php",
        "lua" => "lua",
        "pwsh" or "powershell" => "powershell",
        _ when interpreter.StartsWith("python", StringComparison.Ordinal) => "python",
        _ => null,
    };
}
