using System.Text;

namespace CodeIndex.Cli;

internal static class ReportLogTailBuilder
{
    private const string TruncatedLogLineSuffix = "...[line truncated]";

    internal static string BuildRecentLogTail(
        int maxLines,
        bool includeArgs,
        out int linesIncluded,
        out ReportRedactionSummary redactions,
        out bool logTailTruncated,
        out bool logLineCharsTruncated)
    {
        linesIncluded = 0;
        logTailTruncated = false;
        logLineCharsTruncated = false;
        var redactionCounter = new ReportRedactionCounter();
        var logDir = GlobalToolLog.ResolveLogDirectoryForReport();
        if (string.IsNullOrWhiteSpace(logDir) || !Directory.Exists(logDir))
        {
            redactions = redactionCounter.ToSummary();
            return $"no cdidx lifecycle log directory found (looked at: {ReportCommandRunner.RedactedPlaceholder}).\n";
        }

        var logFiles = SelectRecentLogFiles(
            new DirectoryInfo(logDir).EnumerateFiles("stderr-*.log", SearchOption.TopDirectoryOnly),
            out var olderLogFilesOmitted);
        logTailTruncated = olderLogFilesOmitted;
        if (logFiles.Count == 0)
        {
            redactions = redactionCounter.ToSummary();
            return $"no cdidx lifecycle log files found in: {ReportCommandRunner.RedactedPlaceholder}\n";
        }

        var collected = new LinkedList<string>();
        foreach (var file in logFiles)
        {
            if (collected.Count >= maxLines)
            {
                logTailTruncated = true;
                break;
            }
            ReportLogTailReadResult result;
            try
            {
                result = ReadLogFileTailLinesResult(file.FullName, maxLines - collected.Count);
            }
            catch (IOException)
            {
                continue;
            }
            if (result.LinesTruncated || result.BytesTruncated)
                logTailTruncated = true;
            if (result.LineCharsTruncated)
                logLineCharsTruncated = true;
            var lines = result.Lines;
            for (var i = lines.Count - 1; i >= 0 && collected.Count < maxLines; i--)
                collected.AddFirst(lines[i]);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# cdidx lifecycle log (last {collected.Count} lines, newest last)");
        sb.AppendLine($"# source directory: {ReportCommandRunner.RedactedPlaceholder}");
        sb.AppendLine();
        foreach (var line in collected)
        {
            var redacted = ReportCommandRunner.RedactLogLine(line, includeArgs);
            redactionCounter.Observe(line, redacted);
            sb.AppendLine(redacted);
        }
        linesIncluded = collected.Count;
        redactions = redactionCounter.ToSummary();
        return sb.ToString();
    }

    internal static IReadOnlyList<string> ReadLogFileTailLines(string path, int maxLines)
        => ReadLogFileTailLinesResult(path, maxLines).Lines;

    internal static ReportLogTailReadResult ReadLogFileTailLinesResult(string path, int maxLines)
    {
        if (maxLines <= 0)
            return new ReportLogTailReadResult([], LinesTruncated: false, BytesTruncated: false, LineCharsTruncated: false);

        using var stream = BoundedFile.OpenReadForTailWindow(path, ReportCommandRunner.MaxLogFileTailBytes, out var bytesTruncated);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: !bytesTruncated,
            bufferSize: 8192,
            leaveOpen: false);
        if (bytesTruncated)
        {
            if (ReadBoundedLogLine(reader, out _) == null)
                return new ReportLogTailReadResult([], LinesTruncated: false, BytesTruncated: true, LineCharsTruncated: false);
        }

        var lines = new Queue<string>(maxLines + 1);
        var lineCharsTruncated = false;
        string? line;
        while ((line = ReadBoundedLogLine(reader, out var lineTruncated)) != null)
        {
            lineCharsTruncated |= lineTruncated;
            if (lines.Count == maxLines + 1)
                lines.Dequeue();
            lines.Enqueue(line);
        }

        var linesTruncated = lines.Count > maxLines;
        if (linesTruncated)
            lines.Dequeue();
        return new ReportLogTailReadResult(lines.ToArray(), linesTruncated, bytesTruncated, lineCharsTruncated);
    }

    private static IReadOnlyList<FileInfo> SelectRecentLogFiles(IEnumerable<FileInfo> files, out bool olderLogFilesOmitted)
    {
        var recent = new List<FileInfo>(ReportCommandRunner.MaxRecentLogFiles);
        olderLogFilesOmitted = false;
        foreach (var file in files)
        {
            var insertAt = recent.FindIndex(
                existing => string.Compare(file.Name, existing.Name, StringComparison.Ordinal) > 0);
            if (insertAt < 0)
            {
                if (recent.Count < ReportCommandRunner.MaxRecentLogFiles)
                    recent.Add(file);
                else
                    olderLogFilesOmitted = true;
                continue;
            }

            recent.Insert(insertAt, file);
            if (recent.Count > ReportCommandRunner.MaxRecentLogFiles)
            {
                olderLogFilesOmitted = true;
                recent.RemoveAt(recent.Count - 1);
            }
        }

        return recent;
    }

    private static string? ReadBoundedLogLine(StreamReader reader, out bool lineTruncated)
    {
        lineTruncated = false;
        var displayLimit = ReportCommandRunner.MaxLogTailLineChars - TruncatedLogLineSuffix.Length;
        var sb = new StringBuilder(Math.Min(ReportCommandRunner.MaxLogTailLineChars, 1024));
        while (true)
        {
            var next = reader.Read();
            if (next < 0)
                return sb.Length == 0 && !lineTruncated ? null : sb.ToString();

            var c = (char)next;
            if (c == '\n')
                return sb.ToString();
            if (c == '\r')
            {
                if (reader.Peek() == '\n')
                    reader.Read();
                return sb.ToString();
            }

            if (lineTruncated)
                continue;

            if (sb.Length < displayLimit)
            {
                sb.Append(c);
                continue;
            }

            sb.Append(TruncatedLogLineSuffix);
            lineTruncated = true;
        }
    }
}
