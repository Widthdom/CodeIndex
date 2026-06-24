using System.Text;

namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader(long maxFileSizeBytes)
{
    private const int GitLfsPointerMaxBytes = 1024;
    private static ReadOnlySpan<byte> GitLfsPointerPrefix => "version https://git-lfs.github.com/spec/v1"u8;

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
            ? ComputeChecksum(bytes)
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
        if (!content.Contains('\uFEFF') && !content.Contains('\u200B'))
            return content;

        var firstStripIndex = -1;
        var atLineStart = true;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (IsLineLeadingInvisible(c) && atLineStart)
            {
                firstStripIndex = i;
                break;
            }
            atLineStart = c == '\n';
        }
        if (firstStripIndex < 0)
            return content;

        var sb = new StringBuilder(content.Length - 1);
        if (firstStripIndex > 0)
            sb.Append(content, 0, firstStripIndex);
        atLineStart = true;
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
        if (!content.Contains('\r') && !content.Contains('\uFEFF') && !content.Contains('\u200B'))
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

    internal static bool IsGitLfsPointer(byte[] rawBytes)
    {
        if (rawBytes.Length == 0 || rawBytes.Length >= GitLfsPointerMaxBytes)
            return false;
        if (!rawBytes.AsSpan().StartsWith(GitLfsPointerPrefix))
            return false;

        var pointerText = Encoding.UTF8.GetString(rawBytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = pointerText.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];
        if (lines.Length < 3)
            return false;
        if (!string.Equals(lines[0], "version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
            return false;

        var lineIndex = 1;
        while (lineIndex < lines.Length && lines[lineIndex].StartsWith("ext-", StringComparison.Ordinal))
            lineIndex++;

        if (lineIndex + 1 >= lines.Length)
            return false;
        if (!IsGitLfsSha256OidLine(lines[lineIndex]))
            return false;
        lineIndex++;
        if (!IsGitLfsSizeLine(lines[lineIndex]))
            return false;

        return lineIndex == lines.Length - 1;
    }

    private static bool IsGitLfsSha256OidLine(string line)
    {
        const string prefix = "oid sha256:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var hash = line.AsSpan(prefix.Length);
        if (hash.Length != 64)
            return false;
        foreach (var c in hash)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }

    private static bool IsGitLfsSizeLine(string line)
    {
        const string prefix = "size ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var size = line.AsSpan(prefix.Length);
        if (size.Length == 0)
            return false;
        foreach (var c in size)
        {
            if (c < '0' || c > '9')
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
