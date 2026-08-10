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
        IReadOnlyList<long>? excludedExistingFileIds = null,
        Func<string, bool>? isExistingSymbolPathExcluded = null,
        bool loadExistingSymbolsOnlyForPendingQualifiedMemberAccess = false,
        bool patternConfigsAlreadyLoaded = false,
        CancellationToken cancellationToken = default,
        CSharpPrepassSymbolArtifactCache? symbolArtifactCache = null)
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
            var canExcludeExistingPath = includeExistingSymbols && !IsOutsideProjectRoot(relativePath);

            var language = target.Language;
            if (language == null)
            {
                var detection = indexer.TryDetectLanguageForIndexing(absolutePath);
                if (detection.Status != FileIndexer.FileProbeStatus.Supported)
                {
                    if (canExcludeExistingPath)
                        pendingPaths!.Add(target.IndexPath);
                    continue;
                }

                language = detection.Language;
            }

            if (language != "csharp")
            {
                if (canExcludeExistingPath)
                    pendingPaths!.Add(target.IndexPath);
                continue;
            }

            // An unchanged reusable C# row remains authoritative for the workspace
            // lookup. Only paths whose current extraction will replace or suppress
            // that row belong to pendingPaths.
            // 再利用可能な未変更C#行はworkspace lookupに保持し、今回の抽出で置換・
            // suppressionされるpathだけをpendingPathsへ入れる。
            if (includeExistingSymbols && canReuseExistingSymbolsWithoutRead?.Invoke(target) == true)
                continue;

            if (canExcludeExistingPath)
                pendingPaths!.Add(target.IndexPath);

            var generatedExtractionSuppressed = isGeneratedCodeExtractionSuppressed?.Invoke(target)
                ?? target.GeneratedExtractionSuppressed
                ?? indexer.IsGeneratedCodeExtractionSuppressed(target.IndexPath);
            if (!generatedExtractionSuppressed)
                candidates.Add(target);
        }

        var extractedByCandidate = new List<SymbolRecord>?[candidates.Count];
        var artifactChecksums = symbolArtifactCache == null
            ? null
            : new string?[candidates.Count];
        var artifactHadRegexTimeouts = symbolArtifactCache == null
            ? null
            : new bool[candidates.Count];
        var sourceEvidenceComplete = 1;
        var hasPendingQualifiedMemberAccessCandidate = 0;
        string? firstIncompleteSourcePath = null;
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
                    string? content;
                    string? checksum = null;
                    if (symbolArtifactCache == null)
                    {
                        content = indexer.LoadCSharpStaticInterfaceCandidateContentForPrepass(
                            target.FilePath,
                            target.RelativePath,
                            includeQualifiedMemberAccessCandidate:
                                loadExistingSymbolsOnlyForPendingQualifiedMemberAccess,
                            cancellationToken);
                    }
                    else
                    {
                        var loaded = indexer
                            .LoadCSharpStaticInterfaceCandidateContentWithChecksumForPrepass(
                                target.FilePath,
                                target.RelativePath,
                                includeQualifiedMemberAccessCandidate:
                                    loadExistingSymbolsOnlyForPendingQualifiedMemberAccess,
                                cancellationToken);
                        content = loaded?.Content;
                        checksum = loaded?.Checksum;
                    }
                    if (content is not null)
                    {
                        if (content.AsSpan().IndexOf('.') >= 0)
                        {
                            Interlocked.Exchange(
                                ref hasPendingQualifiedMemberAccessCandidate,
                                1);
                        }

                        if (MayContainCSharpWorkspaceReferenceTargets(content))
                        {
                            var extractionFilePath = symbolArtifactCache == null
                                ? target.IndexPath
                                : target.FilePath;
                            var extractionProjectRoot = symbolArtifactCache == null
                                ? null
                                : indexer.ProjectRootForExtraction;
                            using var regexTimeouts = symbolArtifactCache == null
                                ? null
                                : BoundedRegex.CaptureTimeouts(
                                    "csharp",
                                    "symbol_extraction");
                            extractedByCandidate[candidateIndex] =
                                patternConfigsAlreadyLoaded
                                    ? SymbolExtractor.ExtractWithPatternConfigsLoaded(
                                        0,
                                        "csharp",
                                        content,
                                        extractionFilePath,
                                        extractionProjectRoot,
                                        cancellationToken: cancellationToken)
                                    : SymbolExtractor.Extract(
                                        0,
                                        "csharp",
                                        content,
                                        extractionFilePath,
                                        extractionProjectRoot,
                                        cancellationToken: cancellationToken);
                            if (regexTimeouts != null)
                            {
                                artifactChecksums![candidateIndex] = checksum;
                                artifactHadRegexTimeouts![candidateIndex] =
                                    regexTimeouts.HasTimeouts;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is FileIndexer.BinaryFileSkippedException
                                           or FileIndexer.FileTooLargeSkippedException)
                {
                    // The authoritative indexing pass persists these files with no symbols,
                    // so their empty source evidence is complete rather than an I/O gap.
                    // main pass が symbol なしで確定保存する intentional skip は、read failure
                    // ではなく complete な negative source evidence として扱う。
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Keep one actionable path for the caller's bounded partial-run diagnostic.
                    // A workspace-wide permission failure must not retain and sort every path.
                    Interlocked.Exchange(ref sourceEvidenceComplete, 0);
                    Interlocked.CompareExchange(
                        ref firstIncompleteSourcePath,
                        target.DisplayRelativePath,
                        null);
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
        for (var candidateIndex = 0; candidateIndex < extractedByCandidate.Length; candidateIndex++)
        {
            var extracted = extractedByCandidate[candidateIndex];
            if (extracted != null)
            {
                var checksum = artifactChecksums?[candidateIndex];
                if (symbolArtifactCache != null && checksum != null)
                {
                    symbolArtifactCache.TryAdmit(
                        candidates[candidateIndex].IndexPath,
                        checksum,
                        extracted,
                        artifactHadRegexTimeouts![candidateIndex],
                        cancellationToken);
                }
                pendingSymbols.AddRange(extracted);
            }
        }

        var hasSourceStaticInterfaceContracts = HasCSharpStaticInterfaceContractSymbol(pendingSymbols);
        var hadPendingContracts = false;
        var hadPendingMemberReadTargets = false;
        var shouldLoadExistingSymbols = includeExistingSymbols
            && (!loadExistingSymbolsOnlyForPendingQualifiedMemberAccess
                || hasPendingQualifiedMemberAccessCandidate != 0);
        var symbols = shouldLoadExistingSymbols
            ? writer.LoadCSharpStaticInterfaceContractSymbols(
                pendingPaths!,
                excludedExistingFileIds,
                isExistingSymbolPathExcluded,
                out hadPendingContracts,
                out hadPendingMemberReadTargets,
                cancellationToken)
            : [];
        symbols.AddRange(pendingSymbols);
        var hasStaticInterfaceContracts = HasCSharpStaticInterfaceContractSymbol(symbols) || hadPendingContracts;
        var requiresMemberReadReferenceRefresh =
            hadPendingMemberReadTargets
            || pendingSymbols.Any(
                ReferenceExtractor.IsCSharpQualifiedMemberReadTargetSymbol);
        IReadOnlyList<string> incompletePaths = firstIncompleteSourcePath == null
            ? []
            : [firstIncompleteSourcePath];
        return new CSharpStaticInterfaceWorkspaceSymbols(
            symbols,
            hasStaticInterfaceContracts,
            ReferenceExtractor.BuildCSharpStaticInterfaceMemberLookups(symbols),
            hasSourceStaticInterfaceContracts,
            sourceEvidenceComplete != 0,
            incompletePaths,
            ReferenceExtractor.BuildCSharpQualifiedPatternLookups(symbols),
            requiresMemberReadReferenceRefresh);
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
            excludedExistingFileIds: null,
            loadExistingSymbolsOnlyForPendingQualifiedMemberAccess: false,
            patternConfigsAlreadyLoaded: false,
            cancellationToken: cancellationToken,
            symbolArtifactCache: null);
    }

    internal static bool HasCSharpStaticInterfaceContractSymbol(IEnumerable<SymbolRecord> symbols)
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

    internal static bool MayContainCSharpWorkspaceReferenceTargets(string content)
    {
        var contentSpan = content.AsSpan();
        return ContainsCSharpWord(contentSpan, "enum")
            || ContainsCSharpWord(contentSpan, "const")
            || ContainsCSharpWord(contentSpan, "static");
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
        private bool _hasEnum;
        private bool _hasConst;
        private bool _hasDot;
        private bool _mayContainUtf16;

        internal bool MayContainContractCandidate => _hasInterface && _hasStatic && (_hasAbstract || _hasVirtual);
        internal bool MayContainWorkspaceCandidate => _hasStatic || _hasEnum || _hasConst;
        internal bool MayContainWorkspaceOrQualifiedMemberAccessCandidate =>
            MayContainWorkspaceCandidate || _hasDot;

        internal bool AppendAndCheck(ReadOnlySpan<byte> bytes)
        {
            Append(bytes);
            return MayContainContractCandidate;
        }

        internal bool AppendAndCheckWorkspaceCandidate(ReadOnlySpan<byte> bytes)
        {
            Append(bytes);
            return MayContainWorkspaceCandidate;
        }

        internal bool AppendAndCheckWorkspaceOrQualifiedMemberAccessCandidate(
            ReadOnlySpan<byte> bytes)
        {
            Append(bytes);
            return MayContainWorkspaceOrQualifiedMemberAccessCandidate;
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
            if (!_hasDot && bytes.IndexOf((byte)'.') >= 0)
                _hasDot = true;

            if (!_hasInterface)
                _hasInterface = ContainsAsciiTokenInCommonEncodings(bytes, CSharpInterfaceKeywordBytes, _mayContainUtf16);
            if (!_hasStatic)
                _hasStatic = ContainsAsciiTokenInCommonEncodings(bytes, CSharpStaticKeywordBytes, _mayContainUtf16);
            if (!_hasEnum)
                _hasEnum = ContainsAsciiTokenInCommonEncodings(bytes, "enum"u8, _mayContainUtf16);
            if (!_hasConst)
                _hasConst = ContainsAsciiTokenInCommonEncodings(bytes, "const"u8, _mayContainUtf16);
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
           && IsCSharpStaticInterfaceContractSignature(symbol.Signature);

    internal static bool IsCSharpStaticInterfaceContractSignature(string? signature)
        => !string.IsNullOrWhiteSpace(signature)
           && ContainsCSharpWord(signature!, "static")
           && (ContainsCSharpWord(signature!, "abstract")
               || ContainsCSharpWord(signature!, "virtual"));

    internal static bool TryCaptureFileStatSnapshots(
        IReadOnlyList<FileTarget> targets,
        out Dictionary<string, FileStatSnapshot> snapshots,
        out string? failedPath,
        CancellationToken cancellationToken = default,
        Action<FileTarget>? validateTarget = null)
    {
        snapshots = new Dictionary<string, FileStatSnapshot>(targets.Count, StringComparer.Ordinal);
        failedPath = null;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                validateTarget?.Invoke(target);
                var resolvedPath = ResolveFileStatPath(
                    target.FilePath,
                    target.ResolveSymlinkTargets);
                var info = new FileInfo(LongPath.EnsureWindowsPrefix(resolvedPath));
                info.Refresh();
                if (!info.Exists)
                {
                    failedPath = target.DisplayRelativePath;
                    return false;
                }

                snapshots[target.IndexPath] = new FileStatSnapshot(
                    info.Length,
                    info.LastWriteTimeUtc,
                    resolvedPath,
                    target.ResolveSymlinkTargets);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException)
            {
                failedPath = target.DisplayRelativePath;
                return false;
            }
        }

        return true;
    }

    internal static bool FileStatSnapshotsMatch(
        IReadOnlyDictionary<string, FileStatSnapshot> before,
        IReadOnlyDictionary<string, FileStatSnapshot> after,
        out string? changedPath)
    {
        foreach (var (path, snapshot) in before)
        {
            if (!after.TryGetValue(path, out var current)
                || current.Size != snapshot.Size
                || current.ModifiedUtc != snapshot.ModifiedUtc
                || !FileIndexer.FileReadPathsEqual(current.ResolvedPath, snapshot.ResolvedPath))
            {
                changedPath = path;
                return false;
            }
        }

        if (before.Count != after.Count)
        {
            changedPath = after.Keys.FirstOrDefault(path => !before.ContainsKey(path));
            return false;
        }

        changedPath = null;
        return true;
    }

    internal static bool TryValidateFileStatSnapshots(
        IReadOnlyList<FileTarget> targets,
        IReadOnlyDictionary<string, FileStatSnapshot> snapshots,
        out string? changedPath,
        CancellationToken cancellationToken = default,
        Action<FileTarget>? validateTarget = null)
    {
        if (targets.Count != snapshots.Count)
        {
            changedPath = "<csharp_workspace>";
            return false;
        }

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                validateTarget?.Invoke(target);
                if (!snapshots.TryGetValue(target.IndexPath, out var snapshot))
                {
                    changedPath = target.DisplayRelativePath;
                    return false;
                }

                var resolvedPath = ResolveFileStatPath(
                    target.FilePath,
                    snapshot.ResolveSymlinkTargets);
                var info = new FileInfo(LongPath.EnsureWindowsPrefix(resolvedPath));
                info.Refresh();
                if (!info.Exists
                    || !FileIndexer.FileReadPathsEqual(resolvedPath, snapshot.ResolvedPath)
                    || info.Length != snapshot.Size
                    || info.LastWriteTimeUtc != snapshot.ModifiedUtc)
                {
                    changedPath = target.DisplayRelativePath;
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException)
            {
                changedPath = target.DisplayRelativePath;
                return false;
            }
        }

        changedPath = null;
        return true;
    }

    internal static bool TryValidateLoadedFileStatSnapshot(
        string filePath,
        string indexPath,
        string displayPath,
        long loadedSize,
        DateTime loadedModifiedUtc,
        IReadOnlyDictionary<string, FileStatSnapshot> snapshots,
        out string? changedPath,
        CancellationToken cancellationToken = default,
        Action<string>? validatePath = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshots.TryGetValue(indexPath, out var snapshot)
            || loadedSize != snapshot.Size
            || loadedModifiedUtc != snapshot.ModifiedUtc)
        {
            changedPath = displayPath;
            return false;
        }

        try
        {
            validatePath?.Invoke(filePath);
            var resolvedPath = ResolveFileStatPath(
                filePath,
                snapshot.ResolveSymlinkTargets);
            var info = new FileInfo(LongPath.EnsureWindowsPrefix(resolvedPath));
            info.Refresh();
            if (!info.Exists
                || !FileIndexer.FileReadPathsEqual(resolvedPath, snapshot.ResolvedPath)
                || info.Length != snapshot.Size
                || info.LastWriteTimeUtc != snapshot.ModifiedUtc)
            {
                changedPath = displayPath;
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            changedPath = displayPath;
            return false;
        }

        changedPath = null;
        return true;
    }

    private static string ResolveFileStatPath(
        string filePath,
        bool resolveSymlinkTargets)
        => resolveSymlinkTargets
            ? FileIndexer.ResolveFileReadPath(filePath)
            : Path.GetFullPath(filePath);

    internal static bool TryCaptureDirectoryStatSnapshots(
        IEnumerable<string> directories,
        out Dictionary<string, DirectoryStatSnapshot> snapshots,
        out string? failedPath,
        CancellationToken cancellationToken = default,
        Action<string>? validateDirectory = null)
    {
        snapshots = new Dictionary<string, DirectoryStatSnapshot>(StringComparer.Ordinal);
        failedPath = null;
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshots.ContainsKey(directory))
                continue;
            try
            {
                validateDirectory?.Invoke(directory);
                var info = new DirectoryInfo(LongPath.EnsureWindowsPrefix(directory));
                info.Refresh();
                if (!info.Exists)
                {
                    failedPath = directory;
                    return false;
                }

                snapshots[directory] = new DirectoryStatSnapshot(info.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException)
            {
                failedPath = directory;
                return false;
            }
        }

        return true;
    }

    internal static bool DirectoryStatSnapshotsMatch(
        IReadOnlyDictionary<string, DirectoryStatSnapshot> before,
        IReadOnlyDictionary<string, DirectoryStatSnapshot> after,
        out string? changedPath)
    {
        foreach (var (path, snapshot) in before)
        {
            if (!after.TryGetValue(path, out var current) || current != snapshot)
            {
                changedPath = path;
                return false;
            }
        }

        if (before.Count != after.Count)
        {
            changedPath = after.Keys.FirstOrDefault(path => !before.ContainsKey(path));
            return false;
        }

        changedPath = null;
        return true;
    }

    internal static bool TryValidateDirectoryStatSnapshots(
        IReadOnlyList<string> directories,
        IReadOnlyDictionary<string, DirectoryStatSnapshot> snapshots,
        out string? changedPath,
        CancellationToken cancellationToken = default,
        Action<string>? validateDirectory = null)
    {
        if (directories.Count != snapshots.Count)
        {
            changedPath = "<csharp_workspace>";
            return false;
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                validateDirectory?.Invoke(directory);
                var info = new DirectoryInfo(LongPath.EnsureWindowsPrefix(directory));
                info.Refresh();
                if (!info.Exists
                    || !snapshots.TryGetValue(directory, out var snapshot)
                    || info.LastWriteTimeUtc != snapshot.ModifiedUtc)
                {
                    changedPath = directory;
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException)
            {
                changedPath = directory;
                return false;
            }
        }

        changedPath = null;
        return true;
    }

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
        bool? GeneratedExtractionSuppressed = null,
        bool ResolveSymlinkTargets = false)
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

    internal readonly record struct FileStatSnapshot(
        long Size,
        DateTime ModifiedUtc,
        string ResolvedPath,
        bool ResolveSymlinkTargets = false);

    internal readonly record struct DirectoryStatSnapshot(DateTime ModifiedUtc);

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
        ReferenceExtractor.CSharpStaticInterfaceMemberLookups? StaticInterfaceMemberLookups = null,
        bool HasSourceStaticInterfaceContracts = false,
        bool SourceContractEvidenceComplete = true,
        IReadOnlyList<string>? IncompleteSourcePaths = null,
        ReferenceExtractor.CSharpQualifiedPatternLookups? QualifiedPatternLookups = null,
        bool RequiresMemberReadReferenceRefresh = false);
