using System.Text;

namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader(long maxFileSizeBytes)
{
    private const int GitLfsPointerMaxBytes = 1024;
    private static ReadOnlySpan<byte> GitLfsPointerPrefix => "version https://git-lfs.github.com/spec/v1"u8;
    private static ReadOnlySpan<byte> GitLfsExtensionPrefix => "ext-"u8;
    private static ReadOnlySpan<byte> GitLfsSha256OidPrefix => "oid sha256:"u8;
    private static ReadOnlySpan<byte> GitLfsSizePrefix => "size "u8;

    internal readonly record struct NormalizedIndexableContent(
        string Content,
        int LineCount,
        bool HasOversizeLine);

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
                0,
                false,
                ComputeChecksum(bytes),
                null,
                lfsInspection);
        }

        var (content, warning, inspection) = DecodeIndexableContent(bytes, relativePath);
        var normalized = NormalizeForIndexing(content);
        var checksum = CanReuseRawBytesForNormalizedChecksum(content, warning, inspection, normalized)
            ? ComputeRawChecksum(bytes)
            : ComputeChecksumFromNormalizedContent(normalized.Content);

        return new LoadedFileContent(
            normalized.Content,
            bytes,
            sizeBytes,
            modifiedUtc,
            normalized.LineCount,
            normalized.HasOversizeLine,
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

        var (content, _, _) = DecodeIndexableContent(bytes, relativePath);
        return NormalizeContentForPrepass(content);
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

    internal static NormalizedIndexableContent NormalizeForIndexing(string content)
    {
        if (content.Length == 0)
            return new NormalizedIndexableContent(content, 0, false);

        StringBuilder? builder = null;
        var outputLength = 0;
        var lineCount = 0;
        var currentLineLength = 0;
        var hasOversizeLine = false;
        var previousOutputWasLineBreak = false;
        var atLineStart = true;

        StringBuilder EnsureBuilder(int sourceIndex)
        {
            builder ??= new StringBuilder(content.Length).Append(content, 0, sourceIndex);
            return builder;
        }

        void CountOutputChar(char c)
        {
            if (outputLength == 0)
                lineCount = 1;
            else if (previousOutputWasLineBreak)
                lineCount++;

            outputLength++;
            if (c == '\n')
            {
                currentLineLength = 0;
            }
            else if (!hasOversizeLine)
            {
                currentLineLength++;
                hasOversizeLine = currentLineLength > ChunkSplitter.MaxLineLength;
            }
            previousOutputWasLineBreak = c == '\n';
            atLineStart = c == '\n';
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
                CountOutputChar('\n');
                if (i + 1 < content.Length && content[i + 1] == '\n')
                    i++;
                continue;
            }

            builder?.Append(c);
            CountOutputChar(c);
        }

        return new NormalizedIndexableContent(builder?.ToString() ?? content, lineCount, hasOversizeLine);
    }

    internal static string NormalizeContentForPrepass(string content)
    {
        if (content.Length == 0)
            return content;
        if (!RequiresPrepassNormalization(content))
            return content;

        StringBuilder? builder = null;
        var atLineStart = true;

        StringBuilder EnsureBuilder(int sourceIndex)
        {
            builder ??= new StringBuilder(content.Length).Append(content, 0, sourceIndex);
            return builder;
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

    private static bool RequiresPrepassNormalization(string content)
        => content.AsSpan().IndexOfAny('\r', '\uFEFF', '\u200B') >= 0;

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
    int LineCount,
    bool HasOversizeLine,
    string Checksum,
    string? Warning,
    FileContentInspection Inspection);
