using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static readonly byte[] CSharpInterfaceKeywordBytes = "interface"u8.ToArray();
    private static readonly byte[] CSharpStaticKeywordBytes = "static"u8.ToArray();
    private static readonly byte[] CSharpAbstractKeywordBytes = "abstract"u8.ToArray();
    private static readonly byte[] CSharpVirtualKeywordBytes = "virtual"u8.ToArray();

    private static CSharpStaticInterfaceWorkspaceSymbols BuildCSharpStaticInterfaceWorkspaceSymbols(
        DbWriter writer,
        FileIndexer indexer,
        IEnumerable<FullScanFileTarget> fileTargets,
        Action<string?>? reportCurrentFile = null,
        CancellationToken cancellationToken = default)
    {
        var pendingSymbols = new List<SymbolRecord>();
        var pendingPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in fileTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolutePath = target.FilePath;
            var relativePath = target.DisplayRelativePath;
            if (!IsOutsideProjectRoot(relativePath))
                pendingPaths.Add(target.IndexPath);

            var language = target.Language;
            if (language == null)
            {
                var detection = indexer.TryDetectLanguageForIndexing(absolutePath);
                if (detection.Status != FileIndexer.FileProbeStatus.Supported)
                    continue;

                language = detection.Language;
            }

            if (language != "csharp")
                continue;

            try
            {
                reportCurrentFile?.Invoke(relativePath);
                if (indexer.BuildGeneratedCodeExtractionSkippedIssue(target.IndexPath) != null)
                    continue;

                var content = indexer.LoadNormalizedContentForPrepass(
                    absolutePath,
                    target.RelativePath,
                    RawBytesMayContainCSharpStaticInterfaceContract,
                    cancellationToken);
                if (content == null)
                    continue;
                if (!MayContainCSharpStaticInterfaceContract(content))
                    continue;

                pendingSymbols.AddRange(SymbolExtractor.Extract(0, "csharp", content, target.IndexPath, cancellationToken: cancellationToken));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The real indexing pass reports file failures; this pre-pass only supplies
                // workspace symbols for cross-file static interface member matching.
            }
            finally
            {
                reportCurrentFile?.Invoke(null);
            }
        }

        var symbols = writer.LoadCSharpStaticInterfaceContractSymbols(pendingPaths);
        symbols.AddRange(pendingSymbols);
        var hadPendingContracts = writer.HasCSharpStaticInterfaceContractSymbolsInPaths(pendingPaths);
        return new CSharpStaticInterfaceWorkspaceSymbols(
            symbols,
            symbols.Any(IsCSharpStaticInterfaceContractSymbol) || hadPendingContracts);
    }

    private static CSharpStaticInterfaceWorkspaceSymbols BuildCSharpStaticInterfaceWorkspaceSymbols(
        DbWriter writer,
        FileIndexer indexer,
        string projectRoot,
        IEnumerable<string> filePaths,
        Action<string?>? reportCurrentFile = null,
        CancellationToken cancellationToken = default)
    {
        var fileTargets = filePaths.Select(path => FullScanFileTarget.CreateFromPath(projectRoot, path));
        return BuildCSharpStaticInterfaceWorkspaceSymbols(
            writer,
            indexer,
            fileTargets,
            reportCurrentFile,
            cancellationToken);
    }

    internal static bool MayContainCSharpStaticInterfaceContract(string content)
    {
        var masked = MaskCSharpCommentsAndStrings(content);
        var index = 0;
        while ((index = IndexOfCSharpWord(masked, "interface", index)) >= 0)
        {
            var bodyStart = masked.IndexOf('{', index + "interface".Length);
            if (bodyStart < 0)
                return false;

            if (CSharpInterfaceBodyMayContainStaticContract(masked, bodyStart))
                return true;

            index = bodyStart + 1;
        }

        return false;
    }

    internal static bool RawBytesMayContainCSharpStaticInterfaceContract(byte[] bytes)
    {
        var span = bytes.AsSpan();
        return ContainsAsciiTokenInCommonEncodings(span, CSharpInterfaceKeywordBytes)
               && ContainsAsciiTokenInCommonEncodings(span, CSharpStaticKeywordBytes)
               && (ContainsAsciiTokenInCommonEncodings(span, CSharpAbstractKeywordBytes)
                   || ContainsAsciiTokenInCommonEncodings(span, CSharpVirtualKeywordBytes));
    }

    private static bool ContainsAsciiTokenInCommonEncodings(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> token)
    {
        if (bytes.IndexOf(token) >= 0)
            return true;
        if (bytes.IndexOf((byte)0) < 0)
            return false;

        return ContainsUtf16AsciiToken(bytes, token, littleEndian: true)
               || ContainsUtf16AsciiToken(bytes, token, littleEndian: false);
    }

    private static bool ContainsUtf16AsciiToken(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> asciiToken, bool littleEndian)
    {
        var byteLength = asciiToken.Length * 2;
        if (bytes.Length < byteLength)
            return false;

        return ContainsUtf16AsciiTokenAtParity(bytes, asciiToken, littleEndian, startOffset: 0)
               || ContainsUtf16AsciiTokenAtParity(bytes, asciiToken, littleEndian, startOffset: 1);
    }

    private static bool ContainsUtf16AsciiTokenAtParity(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> asciiToken,
        bool littleEndian,
        int startOffset)
    {
        var byteLength = asciiToken.Length * 2;
        for (var start = startOffset; start <= bytes.Length - byteLength; start += 2)
        {
            var matched = true;
            for (var tokenIndex = 0; tokenIndex < asciiToken.Length; tokenIndex++)
            {
                var byteIndex = start + tokenIndex * 2;
                var first = bytes[byteIndex];
                var second = bytes[byteIndex + 1];
                if (littleEndian)
                {
                    if (first == asciiToken[tokenIndex] && second == 0)
                        continue;
                }
                else if (first == 0 && second == asciiToken[tokenIndex])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
                return true;
        }

        return false;
    }

    private static bool CSharpInterfaceBodyMayContainStaticContract(string masked, int bodyStart)
    {
        var depth = 1;
        var memberStart = bodyStart + 1;
        for (var index = bodyStart + 1; index < masked.Length; index++)
        {
            var ch = masked[index];
            if (ch == '{')
            {
                if (depth == 1 && CSharpMemberHeaderHasStaticContract(masked, memberStart, index))
                    return true;

                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return false;

                if (depth == 1)
                    memberStart = index + 1;
            }
            else if (ch == ';' && depth == 1)
            {
                if (CSharpMemberHeaderHasStaticContract(masked, memberStart, index))
                    return true;

                memberStart = index + 1;
            }
        }

        return false;
    }

    private static bool CSharpMemberHeaderHasStaticContract(string masked, int start, int endExclusive)
    {
        if (start < 0 || endExclusive <= start || endExclusive > masked.Length)
            return false;

        var header = masked[start..endExclusive];
        return ContainsCSharpWord(header, "static")
               && (ContainsCSharpWord(header, "abstract")
                   || ContainsCSharpWord(header, "virtual"));
    }

    private static int IndexOfCSharpWord(string text, string word, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < text.Length)
        {
            index = text.IndexOf(word, index, StringComparison.Ordinal);
            if (index < 0)
                return -1;

            var before = index == 0 ? '\0' : text[index - 1];
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length ? '\0' : text[afterIndex];
            if (!IsCSharpIdentifierPart(before) && !IsCSharpIdentifierPart(after))
                return index;

            index += word.Length;
        }

        return -1;
    }

    private static string MaskCSharpCommentsAndStrings(string content)
    {
        var chars = content.ToCharArray();
        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var inChar = false;
        var inVerbatimString = false;
        var inRawString = false;
        var rawQuoteCount = 0;

        for (var index = 0; index < chars.Length; index++)
        {
            var ch = chars[index];
            var next = index + 1 < chars.Length ? chars[index + 1] : '\0';

            if (inLineComment)
            {
                if (ch is '\r' or '\n')
                {
                    inLineComment = false;
                }
                else
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    chars[index] = ' ';
                    chars[index + 1] = ' ';
                    index++;
                    inBlockComment = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inRawString)
            {
                if (ch == '"' && HasConsecutiveQuotes(chars, index, rawQuoteCount))
                {
                    for (var quote = 0; quote < rawQuoteCount && index + quote < chars.Length; quote++)
                        chars[index + quote] = ' ';
                    index += rawQuoteCount - 1;
                    inRawString = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inVerbatimString)
            {
                if (ch == '"' && next == '"')
                {
                    chars[index] = ' ';
                    chars[index + 1] = ' ';
                    index++;
                }
                else if (ch == '"')
                {
                    chars[index] = ' ';
                    inVerbatimString = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inString)
            {
                if (ch == '\\' && next != '\0')
                {
                    chars[index] = ' ';
                    chars[index + 1] = ' ';
                    index++;
                }
                else if (ch == '"')
                {
                    chars[index] = ' ';
                    inString = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inChar)
            {
                if (ch == '\\' && next != '\0')
                {
                    chars[index] = ' ';
                    chars[index + 1] = ' ';
                    index++;
                }
                else if (ch == '\'')
                {
                    chars[index] = ' ';
                    inChar = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (ch == '/' && next == '/')
            {
                chars[index] = ' ';
                chars[index + 1] = ' ';
                index++;
                inLineComment = true;
            }
            else if (ch == '/' && next == '*')
            {
                chars[index] = ' ';
                chars[index + 1] = ' ';
                index++;
                inBlockComment = true;
            }
            else if (ch == '@' && next == '"')
            {
                chars[index] = ' ';
                chars[index + 1] = ' ';
                index++;
                inVerbatimString = true;
            }
            else if (ch == '"' && HasConsecutiveQuotes(chars, index, 3))
            {
                rawQuoteCount = CountConsecutiveQuotes(chars, index);
                for (var quote = 0; quote < rawQuoteCount && index + quote < chars.Length; quote++)
                    chars[index + quote] = ' ';
                index += rawQuoteCount - 1;
                inRawString = true;
            }
            else if (ch == '"')
            {
                chars[index] = ' ';
                inString = true;
            }
            else if (ch == '\'')
            {
                chars[index] = ' ';
                inChar = true;
            }
        }

        return new string(chars);
    }

    private static bool HasConsecutiveQuotes(char[] chars, int index, int count)
    {
        if (index + count > chars.Length)
            return false;

        for (var offset = 0; offset < count; offset++)
        {
            if (chars[index + offset] != '"')
                return false;
        }

        return true;
    }

    private static int CountConsecutiveQuotes(char[] chars, int index)
    {
        var count = 0;
        while (index + count < chars.Length && chars[index + count] == '"')
            count++;
        return count;
    }

    private static bool IsCSharpStaticInterfaceContractSymbol(SymbolRecord symbol)
        => symbol.Kind is "function" or "operator" or "property"
           && symbol.ContainerKind == "interface"
           && !string.IsNullOrWhiteSpace(symbol.Signature)
           && ContainsCSharpWord(symbol.Signature!, "static")
           && (ContainsCSharpWord(symbol.Signature!, "abstract")
               || ContainsCSharpWord(symbol.Signature!, "virtual"));

    private static bool ContainsCSharpWord(string text, string word)
    {
        var index = 0;
        while (index < text.Length)
        {
            index = text.IndexOf(word, index, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var before = index == 0 ? '\0' : text[index - 1];
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length ? '\0' : text[afterIndex];
            if (!IsCSharpIdentifierPart(before) && !IsCSharpIdentifierPart(after))
                return true;

            index += word.Length;
        }

        return false;
    }

    private static bool IsCSharpIdentifierPart(char ch)
        => char.IsLetterOrDigit(ch) || ch == '_';

    private sealed record CSharpStaticInterfaceWorkspaceSymbols(
        IReadOnlyList<SymbolRecord> Symbols,
        bool HasStaticInterfaceContracts);
}
