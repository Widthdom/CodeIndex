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

    internal readonly record struct CSharpPrepassCandidateContent(
        string Content,
        string Checksum);

    internal LoadedFileContent Load(
        string absolutePath,
        string normalizedRelativePath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var rawFile = ReadRawBytesWithSizeLimit(
            absolutePath,
            normalizedRelativePath,
            cancellationToken);
        if (IsGitLfsPointer(rawFile.Bytes))
            return BuildGitLfsPointerContent(rawFile);

        var decoded = DecodeIndexableContent(rawFile.Bytes, relativePath);
        var normalized = NormalizeForIndexing(
            decoded.Content,
            discardReplacementLinesWhenNonUtf8Likely: decoded.HadInvalidUtf8Replacement);
        return BuildLoadedFileContent(rawFile, decoded, normalized);
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
        var rawFile = ReadRawBytesWithSizeLimit(
            absolutePath,
            normalizedRelativePath,
            cancellationToken);
        if (IsGitLfsPointer(rawFile.Bytes))
            return string.Empty;

        var decoded = DecodeIndexableContent(
            rawFile.Bytes,
            relativePath,
            inspectRawByteContent: false);
        return NormalizeContentForPrepass(decoded.Content);
    }

    internal (CSharpPrepassCandidateContent? Content, bool RequiresRetry)
        LoadCSharpStaticInterfaceCandidateContentForPrepass(
        string absolutePath,
        string normalizedRelativePath,
        string relativePath,
        bool retryOnMutation,
        bool includeQualifiedMemberAccessCandidate,
        bool includeChecksum,
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
            ThrowIfInitialLengthExceedsMaxFileSize(
                normalizedRelativePath,
                initialLength);

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
        {
            return (null, RequiresRetry: true);
        }

        if (bytes is null || IsGitLfsPointer(bytes))
            return (null, RequiresRetry: false);

        var decoded = DecodeIndexableContent(
            bytes,
            relativePath,
            inspectRawByteContent: false);
        var normalized = NormalizeContentForPrepass(decoded.Content);
        var checksum = includeChecksum
            ? decoded.Warning is null
                && !decoded.Inspection.IsUtf16
                && ReferenceEquals(normalized, decoded.Content)
                    ? ComputeRawChecksum(bytes)
                    : ComputeChecksumFromNormalizedContent(normalized)
            : string.Empty;
        return (
            new CSharpPrepassCandidateContent(normalized, checksum),
            RequiresRetry: false);
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
