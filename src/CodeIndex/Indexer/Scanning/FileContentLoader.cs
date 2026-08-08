using System.Text;

namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader(
    long maxFileSizeBytes,
    Func<string, FileStream>? openReadForIndexContent = null,
    Func<string, string>? resolveFileReadPath = null,
    bool bindReadToFileSystemIdentity = false,
    Action<string>? validateResolvedFileReadPath = null)
{
    private readonly Func<string, FileStream> _openReadForIndexContent =
        openReadForIndexContent ?? BoundedFile.OpenReadForIndexContent;
    private readonly Func<string, string> _resolveFileReadPath =
        resolveFileReadPath ?? Path.GetFullPath;
    private readonly bool _bindReadToFileSystemIdentity =
        bindReadToFileSystemIdentity;
    private readonly Action<string>? _validateResolvedFileReadPath =
        validateResolvedFileReadPath;
    private const int GitLfsPointerMaxBytes = 1024;
    private static ReadOnlySpan<byte> GitLfsPointerPrefix => "version https://git-lfs.github.com/spec/v1"u8;
    private static ReadOnlySpan<byte> GitLfsExtensionPrefix => "ext-"u8;
    private static ReadOnlySpan<byte> GitLfsSha256OidPrefix => "oid sha256:"u8;
    private static ReadOnlySpan<byte> GitLfsSizePrefix => "size "u8;

    internal readonly record struct NormalizedIndexableContent(
        string Content,
        NormalizedContentFacts Facts)
    {
        internal int LineCount => Facts.LineCount;
        internal bool HasOversizeLine => Facts.HasOversizeLine;
        internal int ConflictMarkerLine => Facts.ConflictMarkerLine;
    }

    internal LoadedFileContent Load(
        string absolutePath,
        string normalizedRelativePath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var (bytes, sizeBytes, modifiedUtc) = ReadRawBytesWithSizeLimit(
            absolutePath,
            normalizedRelativePath,
            cancellationToken);
        if (IsGitLfsPointer(bytes))
        {
            var lfsInspection = FileContentInspection.GitLfsPointer();
            return new LoadedFileContent(
                string.Empty,
                bytes,
                sizeBytes,
                modifiedUtc,
                NormalizedContentFacts.Empty,
                ComputeChecksum(bytes),
                null,
                lfsInspection);
        }

        var (content, warning, inspection, hadInvalidUtf8Replacement) = DecodeIndexableContent(bytes, relativePath);
        var normalized = NormalizeForIndexing(
            content,
            discardReplacementLinesWhenNonUtf8Likely: hadInvalidUtf8Replacement);
        var checksum = CanReuseRawBytesForNormalizedChecksum(content, warning, inspection, normalized)
            ? ComputeRawChecksum(bytes)
            : ComputeChecksumFromNormalizedContent(normalized.Content);

        return new LoadedFileContent(
            normalized.Content,
            bytes,
            sizeBytes,
            modifiedUtc,
            normalized.Facts,
            checksum,
            warning,
            inspection);
    }

    internal static bool CanReuseRawBytesForNormalizedChecksum(
        string decodedContent,
        string? decodeWarning,
        FileContentInspection inspection,
        NormalizedIndexableContent normalized)
    {
        return decodeWarning is null
            && !inspection.IsUtf16
            && ReferenceEquals(normalized.Content, decodedContent);
    }

    internal string LoadNormalizedContentForPrepass(
        string absolutePath,
        string normalizedRelativePath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var (bytes, _, _) = ReadRawBytesWithSizeLimit(
            absolutePath,
            normalizedRelativePath,
            cancellationToken);
        if (IsGitLfsPointer(bytes))
            return string.Empty;

        var (content, _, _, _) = DecodeIndexableContent(bytes, relativePath, inspectRawByteContent: false);
        return NormalizeContentForPrepass(content);
    }

    internal (string? Content, bool RequiresRetry) LoadCSharpStaticInterfaceCandidateContentForPrepass(
        string absolutePath,
        string normalizedRelativePath,
        string relativePath,
        bool retryOnMutation,
        bool includeQualifiedMemberAccessCandidate,
        CancellationToken cancellationToken)
    {
        var readPath = _resolveFileReadPath(absolutePath);
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? bytes = null;
        bool lengthChanged;
        bool pathIdentityChanged;
        DateTime modifiedBeforeRead;
        DateTime modifiedAfterRead;
        using (var stream = OpenValidatedReadStream(absolutePath, readPath))
        {
            modifiedBeforeRead = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            var initialLength = stream.Length;
            if (initialLength > maxFileSizeBytes)
                throw new FileIndexer.FileTooLargeSkippedException(
                    normalizedRelativePath,
                    initialLength,
                    maxFileSizeBytes,
                    BuildFileTooLargeMessage(initialLength, grewDuringRead: false));

            var probe = CSharpStaticInterfacePrepass.CreateRawByteContractProbe();
            var rawCandidate = RawByteChunksMayMatch(
                stream,
                initialLength,
                normalizedRelativePath,
                includeQualifiedMemberAccessCandidate
                    ? probe
                        .AppendAndCheckWorkspaceOrQualifiedMemberAccessCandidate
                    : probe.AppendAndCheckWorkspaceCandidate,
                cancellationToken);
            if (rawCandidate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Seek(0, SeekOrigin.Begin);
                (bytes, _) = ReadStreamBytesWithKnownInitialLength(
                    stream,
                    initialLength,
                    normalizedRelativePath,
                    cancellationToken);
            }

            lengthChanged = stream.Length != initialLength;
            modifiedAfterRead = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            pathIdentityChanged = ReadPathIdentityChanged(absolutePath, stream);
        }

        if (retryOnMutation
            && (modifiedAfterRead != modifiedBeforeRead || lengthChanged || pathIdentityChanged))
            return (null, RequiresRetry: true);
        if (bytes is null || IsGitLfsPointer(bytes))
            return (null, RequiresRetry: false);

        var (content, _, _, _) = DecodeIndexableContent(bytes, relativePath, inspectRawByteContent: false);
        return (NormalizeContentForPrepass(content), RequiresRetry: false);
    }

    internal static string NormalizeLineEndings(string content)
    {
        var firstCarriageReturn = content.IndexOf('\r');
        if (firstCarriageReturn < 0)
            return content;

        var builder = new StringBuilder(content.Length);
        builder.Append(content, 0, firstCarriageReturn);

        for (var index = firstCarriageReturn; index < content.Length; index++)
        {
            if (content[index] != '\r')
            {
                builder.Append(content[index]);
                continue;
            }

            builder.Append('\n');
            if (index + 1 < content.Length && content[index + 1] == '\n')
                index++;
        }

        return builder.ToString();
    }

    internal static string StripLineLeadingInvisibles(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var firstStripIndex = FindFirstLineLeadingInvisible(content);
        if (firstStripIndex < 0)
            return content;

        var sb = new StringBuilder(content.Length - 1);
        if (firstStripIndex > 0)
            sb.Append(content, 0, firstStripIndex);
        var atLineStart = true;
        for (var i = firstStripIndex + 1; i < content.Length; i++)
        {
            var c = content[i];
            if (IsLineLeadingInvisible(c) && atLineStart)
                continue;
            sb.Append(c);
            atLineStart = c == '\n';
        }
        return sb.ToString();
    }

    private static int FindFirstLineLeadingInvisible(string content)
    {
        var searchOffset = 0;
        while (searchOffset < content.Length)
        {
            var relativeIndex = content.AsSpan(searchOffset).IndexOfAny('\uFEFF', '\u200B');
            if (relativeIndex < 0)
                return -1;

            var index = searchOffset + relativeIndex;
            if (index == 0 || content[index - 1] == '\n')
                return index;

            searchOffset = index + 1;
        }

        return -1;
    }

    private static bool IsLineLeadingInvisible(char c) => c is '\uFEFF' or '\u200B';

    internal static NormalizedIndexableContent NormalizeForIndexing(
        string content,
        bool discardReplacementLinesWhenNonUtf8Likely = false)
    {
        if (content.Length == 0)
            return new NormalizedIndexableContent(content, NormalizedContentFacts.Empty);

        StringBuilder? builder = null;
        var outputLength = 0;
        var lineCount = 0;
        var currentLineLength = 0;
        var firstOversizeLine = 0;
        var conflictMarkerLine = 0;
        var conflictScanByteCount = 0;
        var conflictScanComplete = false;
        var replacementCharacterCount = 0;
        List<int>? replacementCharacterLines = null;
        var retainReplacementCharacterLines = true;
        var firstOversizeFtsTokenLine = 0;
        var ftsTokenLength = 0;
        var trackFtsTokens = content.Length
            > CodeIndex.Database.DbReader.FtsUnicode61MaxTokenLength;
        var pendingChunkStartOffset = 0;
        var pendingFullChunkEndOffset = 0;
        List<int>? additionalChunkStartOffsets = null;
        List<int>? fullChunkEndOffsets = null;
        var trackChunkSlices = true;
        var previousOutputWasLineBreak = false;
        var atLineStart = true;

        StringBuilder EnsureBuilder(int sourceIndex)
        {
            builder ??= new StringBuilder(content.Length).Append(content, 0, sourceIndex);
            return builder;
        }

        void BeginOutputUnit()
        {
            if (outputLength == 0)
            {
                lineCount = 1;
            }
            else if (previousOutputWasLineBreak)
            {
                lineCount++;

                if (!trackChunkSlices)
                    return;

                var chunkStep = ChunkSplitter.ChunkSize - ChunkSplitter.Overlap;
                if (lineCount > ChunkSplitter.ChunkSize
                    && (lineCount - ChunkSplitter.ChunkSize - 1) % chunkStep == 0)
                {
                    (additionalChunkStartOffsets ??= []).Add(pendingChunkStartOffset);
                    (fullChunkEndOffsets ??= []).Add(pendingFullChunkEndOffset);
                }

                if (lineCount > 1
                    && (lineCount - 1) % chunkStep == 0)
                {
                    pendingChunkStartOffset = outputLength;
                }
            }
        }

        void TrackConflictBytes(int utf8ByteLength, char firstChar = '\0', int sourceIndex = -1)
        {
            if (conflictMarkerLine > 0 || conflictScanComplete)
                return;

            conflictScanByteCount += utf8ByteLength;
            if (conflictScanByteCount > FileIndexer.ConflictMarkerScanLimitBytes)
            {
                conflictScanComplete = true;
                return;
            }

            if (atLineStart
                && sourceIndex >= 0
                && firstChar is '<' or '>'
                && FileIndexer.IsConflictMarkerLineStart(content.AsSpan(sourceIndex)))
            {
                conflictMarkerLine = lineCount;
            }
        }

        void TrackReplacementCharacter(char c)
        {
            if (c != '\uFFFD')
                return;

            replacementCharacterCount++;
            if (discardReplacementLinesWhenNonUtf8Likely
                && FileIndexer.MeetsNonUtf8LikelyReplacementThreshold(
                    replacementCharacterCount,
                    content.Length))
            {
                replacementCharacterLines = null;
                retainReplacementCharacterLines = false;
                return;
            }

            if (!retainReplacementCharacterLines)
                return;

            if (replacementCharacterLines is null || replacementCharacterLines[^1] != lineCount)
                (replacementCharacterLines ??= []).Add(lineCount);
        }

        void TrackFtsRune(Rune rune)
        {
            if (firstOversizeFtsTokenLine > 0)
                return;

            var isTokenRune = rune.Value <= '\u007F'
                ? FileIndexer.IsLikelyUnicode61AsciiTokenChar((char)rune.Value)
                : FileIndexer.IsLikelyUnicode61TokenRune(rune);
            if (isTokenRune)
            {
                ftsTokenLength++;
                if (ftsTokenLength > CodeIndex.Database.DbReader.FtsUnicode61MaxTokenLength)
                    firstOversizeFtsTokenLine = lineCount;
            }
            else
            {
                ftsTokenLength = 0;
            }
        }

        void TrackInvalidFtsRune()
        {
            if (firstOversizeFtsTokenLine == 0)
                ftsTokenLength = 0;
        }

        void FinishOutputChars(int charCount)
        {
            currentLineLength += charCount;
            if (firstOversizeLine == 0
                && currentLineLength > ChunkSplitter.MaxLineLength)
            {
                firstOversizeLine = lineCount;
                trackChunkSlices = false;
                additionalChunkStartOffsets = null;
                fullChunkEndOffsets = null;
            }

            outputLength += charCount;
            previousOutputWasLineBreak = false;
            atLineStart = false;
        }

        void FinishOutputLineBreak()
        {
            if (trackChunkSlices
                && lineCount >= ChunkSplitter.ChunkSize
                && (lineCount - ChunkSplitter.ChunkSize) % (ChunkSplitter.ChunkSize - ChunkSplitter.Overlap) == 0)
            {
                pendingFullChunkEndOffset = outputLength;
            }

            outputLength++;
            currentLineLength = 0;
            ftsTokenLength = 0;
            previousOutputWasLineBreak = true;
            atLineStart = true;
        }

        NormalizedChunkSlice[]? BuildChunkSlices()
        {
            if (outputLength == 0
                || firstOversizeLine > 0
                || lineCount <= ChunkSplitter.ChunkSize)
            {
                return null;
            }

            var chunkCount = 1 + (lineCount - ChunkSplitter.ChunkSize + (ChunkSplitter.ChunkSize - ChunkSplitter.Overlap) - 1)
                / (ChunkSplitter.ChunkSize - ChunkSplitter.Overlap);
            var slices = new NormalizedChunkSlice[chunkCount];
            var effectiveContentLength = previousOutputWasLineBreak
                ? outputLength - 1
                : outputLength;
            for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                var startOffset = chunkIndex == 0
                    ? 0
                    : additionalChunkStartOffsets![chunkIndex - 1];
                var startLineIndex = chunkIndex * (ChunkSplitter.ChunkSize - ChunkSplitter.Overlap);
                var endLineIndex = Math.Min(startLineIndex + ChunkSplitter.ChunkSize, lineCount);
                var endOffset = endLineIndex < lineCount
                    ? fullChunkEndOffsets![chunkIndex]
                    : effectiveContentLength;
                slices[chunkIndex] = new NormalizedChunkSlice(
                    startOffset,
                    endOffset - startOffset);
            }

            return slices;
        }

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (IsLineLeadingInvisible(c) && atLineStart)
            {
                EnsureBuilder(i);
                continue;
            }

            if (c == '\r')
            {
                EnsureBuilder(i).Append('\n');
                BeginOutputUnit();
                TrackConflictBytes(1);
                FinishOutputLineBreak();
                if (i + 1 < content.Length && content[i + 1] == '\n')
                    i++;
                continue;
            }

            if (char.IsHighSurrogate(c)
                && i + 1 < content.Length
                && char.IsLowSurrogate(content[i + 1]))
            {
                var lowSurrogate = content[++i];
                builder?.Append(c).Append(lowSurrogate);
                BeginOutputUnit();
                var rune = new Rune(c, lowSurrogate);
                TrackConflictBytes(rune.Utf8SequenceLength);
                if (trackFtsTokens)
                    TrackFtsRune(rune);
                FinishOutputChars(2);
                continue;
            }

            builder?.Append(c);
            BeginOutputUnit();
            if (char.IsSurrogate(c))
            {
                TrackConflictBytes(3);
                if (trackFtsTokens)
                    TrackInvalidFtsRune();
                FinishOutputChars(1);
                continue;
            }

            var utf8ByteLength = c <= '\u007F'
                ? 1
                : new Rune(c).Utf8SequenceLength;
            TrackConflictBytes(utf8ByteLength, c, i);
            TrackReplacementCharacter(c);
            if (c == '\n')
            {
                FinishOutputLineBreak();
                continue;
            }

            if (trackFtsTokens)
                TrackFtsRune(new Rune(c));
            FinishOutputChars(1);
        }

        var normalized = builder?.ToString() ?? content;
        var replacementLines = discardReplacementLinesWhenNonUtf8Likely
            && FileIndexer.MeetsNonUtf8LikelyReplacementThreshold(
                replacementCharacterCount,
                normalized.Length)
                ? null
                : replacementCharacterLines?.ToArray();
        return new NormalizedIndexableContent(
            normalized,
            new NormalizedContentFacts(
                lineCount,
                firstOversizeLine,
                conflictMarkerLine,
                replacementCharacterCount,
                replacementLines,
                firstOversizeFtsTokenLine,
                BuildChunkSlices()));
    }

    internal static string NormalizeContentForPrepass(string content)
    {
        if (content.Length == 0)
            return content;
        var firstNormalizationIndex = FindFirstPrepassNormalizationIndex(content);
        if (firstNormalizationIndex < 0)
            return content;

        StringBuilder? builder = null;
        var atLineStart = firstNormalizationIndex == 0 || content[firstNormalizationIndex - 1] == '\n';

        StringBuilder EnsureBuilder(int sourceIndex)
        {
            builder ??= new StringBuilder(content.Length).Append(content, 0, sourceIndex);
            return builder;
        }

        for (var i = firstNormalizationIndex; i < content.Length; i++)
        {
            var c = content[i];
            if (IsLineLeadingInvisible(c) && atLineStart)
            {
                EnsureBuilder(i);
                continue;
            }

            if (c == '\r')
            {
                EnsureBuilder(i).Append('\n');
                if (i + 1 < content.Length && content[i + 1] == '\n')
                    i++;
                atLineStart = true;
                continue;
            }

            builder?.Append(c);
            atLineStart = c == '\n';
        }

        return builder?.ToString() ?? content;
    }

    private static int FindFirstPrepassNormalizationIndex(string content)
    {
        var searchOffset = 0;
        while (searchOffset < content.Length)
        {
            var relativeIndex = content.AsSpan(searchOffset).IndexOfAny('\r', '\uFEFF', '\u200B');
            if (relativeIndex < 0)
                return -1;

            var index = searchOffset + relativeIndex;
            if (content[index] == '\r' || index == 0 || content[index - 1] == '\n')
                return index;

            searchOffset = index + 1;
        }

        return -1;
    }

    internal static bool IsGitLfsPointer(byte[] rawBytes)
    {
        if (rawBytes.Length == 0 || rawBytes.Length >= GitLfsPointerMaxBytes)
            return false;

        ReadOnlySpan<byte> remaining = rawBytes;
        if (!remaining.StartsWith(GitLfsPointerPrefix))
            return false;

        if (!TryReadGitLfsLine(ref remaining, out var line)
            || !line.SequenceEqual(GitLfsPointerPrefix))
            return false;

        if (!TryReadGitLfsLine(ref remaining, out line))
            return false;
        while (line.StartsWith(GitLfsExtensionPrefix))
        {
            if (!TryReadGitLfsLine(ref remaining, out line))
                return false;
        }

        if (!IsGitLfsSha256OidLine(line))
            return false;
        if (!TryReadGitLfsLine(ref remaining, out line)
            || !IsGitLfsSizeLine(line))
        {
            return false;
        }

        return remaining.IsEmpty;
    }

    private static bool TryReadGitLfsLine(ref ReadOnlySpan<byte> remaining, out ReadOnlySpan<byte> line)
    {
        if (remaining.IsEmpty)
        {
            line = default;
            return false;
        }

        var newlineIndex = remaining.IndexOfAny((byte)'\r', (byte)'\n');
        if (newlineIndex < 0)
        {
            line = remaining;
            remaining = ReadOnlySpan<byte>.Empty;
            return true;
        }

        line = remaining[..newlineIndex];
        var nextIndex = newlineIndex + 1;
        if (remaining[newlineIndex] == (byte)'\r'
            && nextIndex < remaining.Length
            && remaining[nextIndex] == (byte)'\n')
        {
            nextIndex++;
        }

        remaining = remaining[nextIndex..];
        return true;
    }

    private static bool IsGitLfsSha256OidLine(ReadOnlySpan<byte> line)
    {
        if (!line.StartsWith(GitLfsSha256OidPrefix))
            return false;

        var hash = line[GitLfsSha256OidPrefix.Length..];
        if (hash.Length != 64)
            return false;
        foreach (var value in hash)
        {
            if (!((value >= (byte)'0' && value <= (byte)'9')
                  || (value >= (byte)'a' && value <= (byte)'f')))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsGitLfsSizeLine(ReadOnlySpan<byte> line)
    {
        if (!line.StartsWith(GitLfsSizePrefix))
            return false;

        var size = line[GitLfsSizePrefix.Length..];
        if (size.Length == 0)
            return false;
        foreach (var value in size)
        {
            if (value < (byte)'0' || value > (byte)'9')
                return false;
        }
        return true;
    }

}

internal readonly record struct LoadedFileContent(
    string Content,
    byte[] RawBytes,
    long SizeBytes,
    DateTime ModifiedUtc,
    NormalizedContentFacts Facts,
    string Checksum,
    string? Warning,
    FileContentInspection Inspection)
{
    internal int LineCount => Facts.LineCount;
    internal bool HasOversizeLine => Facts.HasOversizeLine;
    internal int ConflictMarkerLine => Facts.ConflictMarkerLine;
}
