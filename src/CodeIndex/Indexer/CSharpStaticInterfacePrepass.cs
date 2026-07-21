using System.Buffers;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class CSharpStaticInterfacePrepass
{
    private static readonly bool IsWindowsPlatform = OperatingSystem.IsWindows();
    private static ReadOnlySpan<byte> CSharpInterfaceKeywordBytes => "interface"u8;
    private static ReadOnlySpan<byte> CSharpStaticKeywordBytes => "static"u8;
    private static ReadOnlySpan<byte> CSharpAbstractKeywordBytes => "abstract"u8;
    private static ReadOnlySpan<byte> CSharpVirtualKeywordBytes => "virtual"u8;

    internal static CSharpStaticInterfaceWorkspaceSymbols BuildWorkspaceSymbols(
        DbWriter writer,
        FileIndexer indexer,
        IEnumerable<FileTarget> fileTargets,
        bool includeExistingSymbols = true,
        Func<FileTarget, bool>? canReuseExistingSymbolsWithoutRead = null,
        Func<FileTarget, bool>? isGeneratedCodeExtractionSuppressed = null,
        Action<string?>? reportCurrentFile = null,
        Action<int, string?>? reportCandidateFile = null,
        int parallelism = 1,
        CancellationToken cancellationToken = default)
    {
        var targetCount = fileTargets.TryGetNonEnumeratedCount(out var count) ? count : 0;
        var candidates = new List<FileTarget>(targetCount);
        var pendingPaths = includeExistingSymbols
            ? new HashSet<string>(targetCount, StringComparer.Ordinal)
            : null;
        foreach (var target in fileTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolutePath = target.FilePath;
            var relativePath = target.DisplayRelativePath;
            if (includeExistingSymbols && !IsOutsideProjectRoot(relativePath))
                pendingPaths!.Add(target.IndexPath);

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

            if (includeExistingSymbols && canReuseExistingSymbolsWithoutRead?.Invoke(target) == true)
                continue;

            var generatedExtractionSuppressed = isGeneratedCodeExtractionSuppressed?.Invoke(target)
                ?? target.GeneratedExtractionSuppressed
                ?? indexer.IsGeneratedCodeExtractionSuppressed(target.IndexPath);
            if (!generatedExtractionSuppressed)
                candidates.Add(target);
        }

        var extractedByCandidate = new List<SymbolRecord>?[candidates.Count];
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, parallelism),
        };
        if (candidates.Count > 0)
            Parallel.For(0, candidates.Count, parallelOptions, candidateIndex =>
            {
                var target = candidates[candidateIndex];
                if (parallelOptions.MaxDegreeOfParallelism == 1)
                    reportCurrentFile?.Invoke(target.DisplayRelativePath);
                reportCandidateFile?.Invoke(candidateIndex, target.DisplayRelativePath);
                try
                {
                    var content = indexer.LoadCSharpStaticInterfaceCandidateContentForPrepass(
                        target.FilePath,
                        target.RelativePath,
                        cancellationToken);
                    if (content is not null && MayContainCSharpStaticInterfaceContract(content))
                        extractedByCandidate[candidateIndex] = SymbolExtractor.Extract(
                            0,
                            "csharp",
                            content,
                            target.IndexPath,
                            cancellationToken: cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // The real indexing pass reports file failures; this pre-pass only supplies
                    // workspace symbols for cross-file static interface member matching.
                }
                finally
                {
                    if (parallelOptions.MaxDegreeOfParallelism == 1)
                        reportCurrentFile?.Invoke(null);
                    reportCandidateFile?.Invoke(candidateIndex, null);
                }
            });

        var pendingSymbolCount = 0;
        foreach (var extracted in extractedByCandidate)
            pendingSymbolCount += extracted?.Count ?? 0;

        var pendingSymbols = new List<SymbolRecord>(pendingSymbolCount);
        foreach (var extracted in extractedByCandidate)
        {
            if (extracted != null)
                pendingSymbols.AddRange(extracted);
        }

        var symbols = includeExistingSymbols
            ? writer.LoadCSharpStaticInterfaceContractSymbols(pendingPaths!)
            : [];
        symbols.AddRange(pendingSymbols);
        var hadPendingContracts = includeExistingSymbols
            && writer.HasCSharpStaticInterfaceContractSymbolsInPaths(pendingPaths!);
        var hasStaticInterfaceContracts = HasCSharpStaticInterfaceContractSymbol(symbols) || hadPendingContracts;
        return new CSharpStaticInterfaceWorkspaceSymbols(
            symbols,
            hasStaticInterfaceContracts,
            ReferenceExtractor.BuildCSharpStaticInterfaceMemberLookups(symbols));
    }

    internal static CSharpStaticInterfaceWorkspaceSymbols BuildWorkspaceSymbols(
        DbWriter writer,
        FileIndexer indexer,
        string projectRoot,
        IEnumerable<string> filePaths,
        bool includeExistingSymbols = true,
        Action<string?>? reportCurrentFile = null,
        CancellationToken cancellationToken = default)
    {
        return BuildWorkspaceSymbols(
            writer,
            indexer,
            EnumerateFileTargets(projectRoot, filePaths),
            includeExistingSymbols,
            canReuseExistingSymbolsWithoutRead: null,
            isGeneratedCodeExtractionSuppressed: null,
            reportCurrentFile: reportCurrentFile,
            reportCandidateFile: null,
            parallelism: 1,
            cancellationToken: cancellationToken);
    }

    private static bool HasCSharpStaticInterfaceContractSymbol(IReadOnlyList<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (IsCSharpStaticInterfaceContractSymbol(symbol))
                return true;
        }

        return false;
    }

    private static IEnumerable<FileTarget> EnumerateFileTargets(string projectRoot, IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
            yield return FileTarget.CreateFromPath(projectRoot, path);
    }

    internal static bool MayContainCSharpStaticInterfaceContract(string content)
    {
        var contentSpan = content.AsSpan();
        if (contentSpan.IndexOf('{') < 0
            || !ContainsCSharpWord(contentSpan, "interface")
            || !ContainsCSharpWord(contentSpan, "static")
            || (!ContainsCSharpWord(contentSpan, "abstract")
                && !ContainsCSharpWord(contentSpan, "virtual")))
        {
            return false;
        }

        return CSharpCodeMayContainStaticInterfaceContract(contentSpan);
    }

    internal static bool RawBytesMayContainCSharpStaticInterfaceContract(byte[] bytes)
    {
        var span = bytes.AsSpan();
        var mayContainUtf16 = span.IndexOf((byte)0) >= 0;
        return ContainsAsciiTokenInCommonEncodings(span, CSharpInterfaceKeywordBytes, mayContainUtf16)
               && ContainsAsciiTokenInCommonEncodings(span, CSharpStaticKeywordBytes, mayContainUtf16)
               && (ContainsAsciiTokenInCommonEncodings(span, CSharpAbstractKeywordBytes, mayContainUtf16)
                   || ContainsAsciiTokenInCommonEncodings(span, CSharpVirtualKeywordBytes, mayContainUtf16));
    }

    internal static RawByteContractProbe CreateRawByteContractProbe() => new();

    internal static bool RawByteChunksMayContainCSharpStaticInterfaceContract(IEnumerable<byte[]> chunks)
    {
        var probe = CreateRawByteContractProbe();
        foreach (var chunk in chunks)
        {
            if (probe.AppendAndCheck(chunk))
                return true;
        }

        return probe.MayContainContractCandidate;
    }

    internal sealed class RawByteContractProbe
    {
        private const int MaxTokenSearchBytes = 18; // "interface" encoded as UTF-16.
        private const int TailBytes = MaxTokenSearchBytes - 1;

        private readonly byte[] _tail = new byte[TailBytes];
        private int _tailLength;
        private bool _hasInterface;
        private bool _hasStatic;
        private bool _hasAbstract;
        private bool _hasVirtual;
        private bool _mayContainUtf16;

        internal bool MayContainContractCandidate => _hasInterface && _hasStatic && (_hasAbstract || _hasVirtual);

        internal bool AppendAndCheck(ReadOnlySpan<byte> bytes)
        {
            Append(bytes);
            return MayContainContractCandidate;
        }

        internal void Append(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0 || MayContainContractCandidate)
                return;

            if (_tailLength == 0)
            {
                Scan(bytes);
                CaptureTail(bytes);
                return;
            }

            var boundaryNewLength = Math.Min(TailBytes, bytes.Length);
            Span<byte> boundary = stackalloc byte[TailBytes * 2];
            _tail.AsSpan(0, _tailLength).CopyTo(boundary);
            bytes[..boundaryNewLength].CopyTo(boundary[_tailLength..]);
            Scan(boundary[..(_tailLength + boundaryNewLength)]);
            if (!MayContainContractCandidate)
                Scan(bytes);
            CaptureTail(bytes);
        }

        private void Scan(ReadOnlySpan<byte> bytes)
        {
            if (!_mayContainUtf16 && bytes.IndexOf((byte)0) >= 0)
                _mayContainUtf16 = true;

            if (!_hasInterface)
                _hasInterface = ContainsAsciiTokenInCommonEncodings(bytes, CSharpInterfaceKeywordBytes, _mayContainUtf16);
            if (!_hasStatic)
                _hasStatic = ContainsAsciiTokenInCommonEncodings(bytes, CSharpStaticKeywordBytes, _mayContainUtf16);
            if (!_hasAbstract && !_hasVirtual)
            {
                _hasAbstract = ContainsAsciiTokenInCommonEncodings(bytes, CSharpAbstractKeywordBytes, _mayContainUtf16);
                if (!_hasAbstract)
                    _hasVirtual = ContainsAsciiTokenInCommonEncodings(bytes, CSharpVirtualKeywordBytes, _mayContainUtf16);
            }
        }

        private void CaptureTail(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= TailBytes)
            {
                _tailLength = TailBytes;
                bytes[^_tailLength..].CopyTo(_tail);
                return;
            }

            var retainedLength = Math.Min(TailBytes - bytes.Length, _tailLength);
            if (retainedLength > 0)
                _tail.AsSpan(_tailLength - retainedLength, retainedLength).CopyTo(_tail);

            bytes.CopyTo(_tail.AsSpan(retainedLength));
            _tailLength = retainedLength + bytes.Length;
        }
    }

    private static bool ContainsAsciiTokenInCommonEncodings(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> token,
        bool mayContainUtf16)
    {
        if (bytes.IndexOf(token) >= 0)
            return true;
        if (!mayContainUtf16)
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

    private static bool CSharpCodeMayContainStaticInterfaceContract(ReadOnlySpan<char> content)
    {
        const int StackFrameCount = 32;
        Span<CSharpInterfaceScanFrame> frames = stackalloc CSharpInterfaceScanFrame[StackFrameCount];
        CSharpInterfaceScanFrame[]? rentedFrames = null;
        var frameCount = 0;
        var braceDepth = 0;
        var pendingInterfaceBody = false;
        var mode = CSharpLexMode.Code;
        var rawQuoteCount = 0;

        try
        {
            var index = 0;
            while (index < content.Length)
            {
                var ch = content[index];
                var next = index + 1 < content.Length ? content[index + 1] : '\0';
                switch (mode)
                {
                    case CSharpLexMode.LineComment:
                        if (ch is '\r' or '\n')
                            mode = CSharpLexMode.Code;
                        index++;
                        continue;
                    case CSharpLexMode.BlockComment:
                        if (ch == '*' && next == '/')
                        {
                            mode = CSharpLexMode.Code;
                            index += 2;
                        }
                        else
                        {
                            index++;
                        }
                        continue;
                    case CSharpLexMode.String:
                        if (ch == '\\' && next != '\0')
                            index += 2;
                        else
                        {
                            if (ch == '"')
                                mode = CSharpLexMode.Code;
                            index++;
                        }
                        continue;
                    case CSharpLexMode.Character:
                        if (ch == '\\' && next != '\0')
                            index += 2;
                        else
                        {
                            if (ch == '\'')
                                mode = CSharpLexMode.Code;
                            index++;
                        }
                        continue;
                    case CSharpLexMode.VerbatimString:
                        if (ch == '"' && next == '"')
                            index += 2;
                        else
                        {
                            if (ch == '"')
                                mode = CSharpLexMode.Code;
                            index++;
                        }
                        continue;
                    case CSharpLexMode.RawString:
                        if (ch != '"')
                        {
                            index++;
                            continue;
                        }

                        var closingQuoteCount = CountConsecutiveQuotes(content, index);
                        if (closingQuoteCount < rawQuoteCount)
                        {
                            index += closingQuoteCount;
                            continue;
                        }

                        index += rawQuoteCount;
                        mode = CSharpLexMode.Code;
                        continue;
                }

                if (ch == '/' && next == '/')
                {
                    mode = CSharpLexMode.LineComment;
                    index += 2;
                    continue;
                }
                if (ch == '/' && next == '*')
                {
                    mode = CSharpLexMode.BlockComment;
                    index += 2;
                    continue;
                }
                if ((ch == '@' && next == '"')
                    || (index + 2 < content.Length
                        && ((ch == '$' && next == '@') || (ch == '@' && next == '$'))
                        && content[index + 2] == '"'))
                {
                    mode = CSharpLexMode.VerbatimString;
                    index += next == '"' ? 2 : 3;
                    continue;
                }
                if (ch == '"')
                {
                    var quoteCount = CountConsecutiveQuotes(content, index);
                    if (quoteCount >= 3)
                    {
                        rawQuoteCount = quoteCount;
                        mode = CSharpLexMode.RawString;
                        index += quoteCount;
                    }
                    else
                    {
                        mode = CSharpLexMode.String;
                        index++;
                    }
                    continue;
                }
                if (ch == '\'')
                {
                    mode = CSharpLexMode.Character;
                    index++;
                    continue;
                }
                if (IsCSharpIdentifierPart(ch))
                {
                    var wordStart = index++;
                    while (index < content.Length && IsCSharpIdentifierPart(content[index]))
                        index++;

                    var word = content[wordStart..index];
                    if (word.SequenceEqual("interface"))
                        pendingInterfaceBody = true;
                    if (frameCount > 0 && braceDepth == frames[frameCount - 1].BodyDepth)
                    {
                        ref var frame = ref frames[frameCount - 1];
                        if (word.SequenceEqual("static"))
                            frame.HasStatic = true;
                        else if (word.SequenceEqual("abstract"))
                            frame.HasAbstract = true;
                        else if (word.SequenceEqual("virtual"))
                            frame.HasVirtual = true;
                    }
                    continue;
                }

                if (ch == '{')
                {
                    if (frameCount > 0
                        && braceDepth == frames[frameCount - 1].BodyDepth
                        && frames[frameCount - 1].HasStaticContract)
                    {
                        return true;
                    }

                    braceDepth++;
                    if (pendingInterfaceBody)
                    {
                        if (frameCount == frames.Length)
                        {
                            var expandedFrames = ArrayPool<CSharpInterfaceScanFrame>.Shared.Rent(frames.Length * 2);
                            frames.CopyTo(expandedFrames);
                            if (rentedFrames is not null)
                                ArrayPool<CSharpInterfaceScanFrame>.Shared.Return(rentedFrames);
                            rentedFrames = expandedFrames;
                            frames = rentedFrames;
                        }
                        frames[frameCount++] = new CSharpInterfaceScanFrame(braceDepth);
                        pendingInterfaceBody = false;
                    }
                    index++;
                    continue;
                }

                if (ch == '}')
                {
                    if (braceDepth > 0)
                        braceDepth--;
                    while (frameCount > 0 && frames[frameCount - 1].BodyDepth > braceDepth)
                        frameCount--;
                    if (frameCount > 0 && frames[frameCount - 1].BodyDepth == braceDepth)
                        frames[frameCount - 1].ResetMemberHeader();
                    index++;
                    continue;
                }

                if (ch == ';'
                    && frameCount > 0
                    && braceDepth == frames[frameCount - 1].BodyDepth)
                {
                    ref var frame = ref frames[frameCount - 1];
                    if (frame.HasStaticContract)
                        return true;
                    frame.ResetMemberHeader();
                }
                index++;
            }

            return false;
        }
        finally
        {
            if (rentedFrames is not null)
                ArrayPool<CSharpInterfaceScanFrame>.Shared.Return(rentedFrames);
        }
    }

    private static int CountConsecutiveQuotes(ReadOnlySpan<char> content, int index)
    {
        var count = 0;
        while (index + count < content.Length && content[index + count] == '"')
            count++;
        return count;
    }

    private enum CSharpLexMode : byte
    {
        Code,
        LineComment,
        BlockComment,
        String,
        Character,
        VerbatimString,
        RawString,
    }

    private struct CSharpInterfaceScanFrame(int bodyDepth)
    {
        internal int BodyDepth { get; } = bodyDepth;
        internal bool HasStatic { get; set; }
        internal bool HasAbstract { get; set; }
        internal bool HasVirtual { get; set; }
        internal readonly bool HasStaticContract => HasStatic && (HasAbstract || HasVirtual);

        internal void ResetMemberHeader()
        {
            HasStatic = false;
            HasAbstract = false;
            HasVirtual = false;
        }
    }

    private static bool IsCSharpStaticInterfaceContractSymbol(SymbolRecord symbol)
        => symbol.Kind is "function" or "operator" or "property"
           && symbol.ContainerKind == "interface"
           && !string.IsNullOrWhiteSpace(symbol.Signature)
           && ContainsCSharpWord(symbol.Signature!, "static")
           && (ContainsCSharpWord(symbol.Signature!, "abstract")
               || ContainsCSharpWord(symbol.Signature!, "virtual"));

    private static bool ContainsCSharpWord(string text, string word)
        => ContainsCSharpWord(text.AsSpan(), word);

    private static bool ContainsCSharpWord(ReadOnlySpan<char> text, string word)
    {
        var index = 0;
        while (index < text.Length)
        {
            var found = text[index..].IndexOf(word.AsSpan(), StringComparison.Ordinal);
            if (found < 0)
                return false;

            index += found;
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

    internal readonly record struct FileTarget(
        string FilePath,
        string RelativePath,
        string DisplayRelativePath,
        string IndexPath,
        string? Language,
        bool? GeneratedExtractionSuppressed = null)
    {
        public static FileTarget CreateFromPath(string projectRoot, string path)
        {
            var filePath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(projectRoot, FileIndexer.NormalizeRelativePathForCurrentPlatform(path));
            return Create(projectRoot, filePath);
        }

        public static FileTarget Create(string projectRoot, string filePath, string? language = null)
        {
            var relativePath = FileIndexer.GetRelativePathFromProjectRoot(projectRoot, filePath);
            return new FileTarget(
                filePath,
                relativePath,
                FileIndexer.NormalizePathSeparators(relativePath),
                FileIndexer.NormalizeIndexPath(relativePath),
                language);
        }
    }

    private static bool IsOutsideProjectRoot(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return true;

        var normalized = IsWindowsPlatform
            ? relativePath.Replace('\\', '/')
            : relativePath;
        return normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal);
    }
}

internal sealed record CSharpStaticInterfaceWorkspaceSymbols(
        IReadOnlyList<SymbolRecord> Symbols,
        bool HasStaticInterfaceContracts,
        ReferenceExtractor.CSharpStaticInterfaceMemberLookups? StaticInterfaceMemberLookups = null);
