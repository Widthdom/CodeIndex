using System.Text;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    // Script-header detection reads at most the first physical line within this
    // byte cap. NUL bytes or a line that reaches the cap without LF/CR are treated as
    // unsupported so binary executables and minified data are not parsed as scripts.
    internal const int ShebangProbeByteLimit = 256;
    private const string ZshCompdefDirective = "#compdef";
    private const string PythonShebangInterpreterPrefix = "python";
    private static readonly UTF8Encoding StrictShebangUtf8Encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictShebangUtf16LittleEndianEncoding = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictShebangUtf16BigEndianEncoding = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly IReadOnlyDictionary<string, string> ExactShebangInterpreterLanguages =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bash"] = "shell",
            ["sh"] = "shell",
            ["zsh"] = "shell",
            ["fish"] = "shell",
            ["dash"] = "shell",
            ["ksh"] = "shell",
            ["ash"] = "shell",
            ["node"] = "javascript",
            ["nodejs"] = "javascript",
            ["ruby"] = "ruby",
            ["perl"] = "perl",
            ["tclsh"] = "tcl",
            ["wish"] = "tcl",
            ["matlab"] = "matlab",
            ["octave"] = "matlab",
            ["octave-cli"] = "matlab",
            ["prolog"] = "prolog",
            ["swipl"] = "prolog",
            ["sicstus"] = "prolog",
            ["gprolog"] = "prolog",
            ["php"] = "php",
            ["lua"] = "lua",
            ["pwsh"] = "powershell",
            ["powershell"] = "powershell",
        };

    internal sealed record ShebangInterpreterRule(
        string MatchKind,
        string Pattern,
        string Language);

    /// <summary>
    /// Try to infer a language from a bounded first-line script signature.
    /// This is a cheap fallback for extensionless/unknown files and an authoritative signal
    /// from shebangs for explicitly ambiguous extensions after language-map overrides have
    /// been applied. Zsh #compdef metadata is accepted only for extensionless/unknown files.
    /// It reads at most <see cref="ShebangProbeByteLimit"/> bytes from the first line;
    /// NUL bytes and over-cap first lines are treated as non-scripts.
    /// 拡張子なし/未知ファイルでは shebang または zsh #compdef metadata を fallback として使い、
    /// 明示的に曖昧な拡張子では override 適用後の shebang だけを authoritative signal として使う。
    /// </summary>
    private static LanguageDetectionResult TryDetectLanguageFromScriptHeader(
        string filePath,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot,
        FileProbeStatus? knownIndexability,
        Func<string, FileStream>? openReadForIndexContent,
        bool allowZshCompdef)
    {
        var indexability = knownIndexability ?? GetFileIndexability(filePath, symlinkPolicy, projectRoot);
        if (indexability == FileProbeStatus.Missing)
            return new LanguageDetectionResult(FileProbeStatus.Missing, null);

        if (indexability != FileProbeStatus.Supported)
            return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

        try
        {
            using var stream = openReadForIndexContent?.Invoke(filePath)
                ?? BoundedFile.OpenReadForPrefixProbe(filePath);
            if (!stream.CanRead)
                return new LanguageDetectionResult(FileProbeStatus.ProbeFailed, null);

            Span<byte> buffer = stackalloc byte[ShebangProbeByteLimit];
            var bytesRead = stream.Read(buffer);
            if (bytesRead <= 0)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var bytes = buffer[..bytesRead];
            var scriptHeaderEncoding = DetectScriptHeaderEncoding(bytes);
            if (scriptHeaderEncoding == ScriptHeaderEncoding.Unsupported)
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            if ((scriptHeaderEncoding == ScriptHeaderEncoding.Utf8 || scriptHeaderEncoding == ScriptHeaderEncoding.Utf8Bom)
                && bytes.Contains((byte)0))
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var preambleLength = GetScriptHeaderPreambleLength(scriptHeaderEncoding);
            if (!HasRawScriptHeaderPrefix(bytes, scriptHeaderEncoding, preambleLength, allowZshCompdef))
                return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);

            var lineEnd = FindScriptHeaderLineEnd(bytes, scriptHeaderEncoding, preambleLength);
            if (lineEnd < 0)
            {
                if (bytesRead == ShebangProbeByteLimit)
                    return new LanguageDetectionResult(FileProbeStatus.Unsupported, null);
                lineEnd = bytesRead;
            }

            var firstLineBytes = bytes[preambleLength..lineEnd];
            var firstLine = DecodeScriptHeaderLine(firstLineBytes, scriptHeaderEncoding);

            if (firstLine.StartsWith('\uFEFF'))
                firstLine = firstLine[1..];

            if (allowZshCompdef && IsZshCompdefDirective(firstLine))
            {
                return new LanguageDetectionResult(
                    FileProbeStatus.Supported,
                    "shell",
                    DetectionSource: ZshCompdefDetectionSource);
            }

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
                ? new LanguageDetectionResult(FileProbeStatus.Supported, language, DetectionSource: ShebangDetectionSource)
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

    private static bool IsZshCompdefDirective(string firstLine)
        => firstLine.StartsWith(ZshCompdefDirective, StringComparison.Ordinal)
            && (firstLine.Length == ZshCompdefDirective.Length
                || char.IsWhiteSpace(firstLine[ZshCompdefDirective.Length]));

    private enum ScriptHeaderEncoding
    {
        Utf8,
        Utf8Bom,
        Utf16LittleEndian,
        Utf16BigEndian,
        Unsupported,
    }

    private static ScriptHeaderEncoding DetectScriptHeaderEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                return ScriptHeaderEncoding.Unsupported;
            if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                return ScriptHeaderEncoding.Unsupported;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return ScriptHeaderEncoding.Utf8Bom;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return ScriptHeaderEncoding.Utf16LittleEndian;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return ScriptHeaderEncoding.Utf16BigEndian;

        return ScriptHeaderEncoding.Utf8;
    }

    private static int GetScriptHeaderPreambleLength(ScriptHeaderEncoding encoding) => encoding switch
    {
        ScriptHeaderEncoding.Utf8Bom => 3,
        ScriptHeaderEncoding.Utf16LittleEndian or ScriptHeaderEncoding.Utf16BigEndian => 2,
        _ => 0,
    };

    private static bool HasRawScriptHeaderPrefix(
        ReadOnlySpan<byte> bytes,
        ScriptHeaderEncoding encoding,
        int start,
        bool allowZshCompdef)
    {
        var remaining = bytes[start..];
        if (StartsWithSupportedScriptHeader(remaining, encoding, allowZshCompdef))
            return true;

        var duplicatePreambleLength = encoding switch
        {
            ScriptHeaderEncoding.Utf16LittleEndian
                when remaining.Length >= 2 && remaining[0] == 0xFF && remaining[1] == 0xFE => 2,
            ScriptHeaderEncoding.Utf16BigEndian
                when remaining.Length >= 2 && remaining[0] == 0xFE && remaining[1] == 0xFF => 2,
            ScriptHeaderEncoding.Utf8 or ScriptHeaderEncoding.Utf8Bom
                when remaining.Length >= 3
                    && remaining[0] == 0xEF
                    && remaining[1] == 0xBB
                    && remaining[2] == 0xBF => 3,
            _ => 0,
        };

        return duplicatePreambleLength > 0
            && StartsWithSupportedScriptHeader(
                remaining[duplicatePreambleLength..],
                encoding,
                allowZshCompdef);
    }

    private static bool StartsWithSupportedScriptHeader(
        ReadOnlySpan<byte> bytes,
        ScriptHeaderEncoding encoding,
        bool allowZshCompdef)
        => StartsWithEncodedAsciiPrefix(bytes, encoding, "#!")
            || (allowZshCompdef && StartsWithEncodedAsciiPrefix(bytes, encoding, ZshCompdefDirective));

    private static bool StartsWithEncodedAsciiPrefix(
        ReadOnlySpan<byte> bytes,
        ScriptHeaderEncoding encoding,
        string prefix)
    {
        var bytesPerCharacter = encoding is ScriptHeaderEncoding.Utf16LittleEndian or ScriptHeaderEncoding.Utf16BigEndian
            ? 2
            : 1;
        if (bytes.Length < prefix.Length * bytesPerCharacter)
            return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            var expected = (byte)prefix[i];
            if (encoding == ScriptHeaderEncoding.Utf16LittleEndian)
            {
                if (bytes[i * 2] != expected || bytes[(i * 2) + 1] != 0)
                    return false;
            }
            else if (encoding == ScriptHeaderEncoding.Utf16BigEndian)
            {
                if (bytes[i * 2] != 0 || bytes[(i * 2) + 1] != expected)
                    return false;
            }
            else if (bytes[i] != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static int FindScriptHeaderLineEnd(ReadOnlySpan<byte> bytes, ScriptHeaderEncoding encoding, int start)
    {
        if (encoding is ScriptHeaderEncoding.Utf8 or ScriptHeaderEncoding.Utf8Bom)
            return bytes[start..].IndexOfAny((byte)'\r', (byte)'\n') is var lineEnd && lineEnd >= 0
                ? start + lineEnd
                : -1;

        for (var i = start; i + 1 < bytes.Length; i += 2)
        {
            var ch = encoding == ScriptHeaderEncoding.Utf16LittleEndian
                ? (bytes[i] | (bytes[i + 1] << 8))
                : ((bytes[i] << 8) | bytes[i + 1]);
            if (ch is '\r' or '\n')
                return i;
        }

        return -1;
    }

    private static string DecodeScriptHeaderLine(ReadOnlySpan<byte> bytes, ScriptHeaderEncoding encoding) => encoding switch
    {
        ScriptHeaderEncoding.Utf16LittleEndian => StrictShebangUtf16LittleEndianEncoding.GetString(bytes),
        ScriptHeaderEncoding.Utf16BigEndian => StrictShebangUtf16BigEndianEncoding.GetString(bytes),
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

    internal static IReadOnlyList<string> GetShebangInterpretersForLanguage(string language)
        => ExactShebangInterpreterLanguages
            .Where(pair => string.Equals(pair.Value, language, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    internal static IReadOnlyList<ShebangInterpreterRule> GetShebangInterpreterRules()
    {
        var rules = ExactShebangInterpreterLanguages
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ShebangInterpreterRule("exact", pair.Key, pair.Value))
            .ToList();
        rules.Add(new ShebangInterpreterRule(
            "prefix",
            PythonShebangInterpreterPrefix,
            "python"));
        return rules;
    }

    private static string? MapShebangInterpreterToLanguage(string interpreter)
    {
        if (ExactShebangInterpreterLanguages.TryGetValue(interpreter, out var language))
            return language;
        return interpreter.StartsWith(PythonShebangInterpreterPrefix, StringComparison.Ordinal)
            ? "python"
            : null;
    }
}
