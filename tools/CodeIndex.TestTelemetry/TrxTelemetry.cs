using System.Globalization;
using System.Xml;

namespace CodeIndex.TestTelemetry;

public static class TrxTelemetry
{
    public const int MaxTop = 100;
    public const int MaxTrxFiles = 256;
    public const int MaxTraversalDirectories = 256;
    public const int MaxTraversalEntries = 4096;
    public const long MaxTrxFileBytes = 16 * 1024 * 1024;

    public static TrxTelemetrySummary Load(string resultsDirectory, int top)
    {
        if (top <= 0 || top > MaxTop)
            throw new TelemetryException($"Top count must be between 1 and {MaxTop}.");

        if (!Directory.Exists(resultsDirectory))
        {
            return new TrxTelemetrySummary(
                ResultsDirectory: resultsDirectory,
                TrxFileCount: 0,
                Total: 0,
                Passed: 0,
                Failed: 0,
                Skipped: 0,
                Other: 0,
                Slowest: [],
                Failures: [],
                Warnings: [$"Results directory not found: {resultsDirectory}"]);
        }

        var warnings = new List<string>();
        var trxFiles = EnumerateTrxFiles(resultsDirectory, warnings);
        var results = new TrxResultAccumulator(top);

        foreach (var path in trxFiles)
        {
            if (!CanReadTrxFile(resultsDirectory, path, warnings))
                continue;

            try
            {
                var fileResults = new TrxResultAccumulator(top);
                foreach (var result in ReadResults(path))
                {
                    fileResults.Add(result);
                }

                results.Merge(fileResults);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                warnings.Add($"Could not parse {FormatTrxPath(resultsDirectory, path)}: {GetWarningReason(ex)}");
            }
        }

        return new TrxTelemetrySummary(
            ResultsDirectory: resultsDirectory,
            TrxFileCount: trxFiles.Count,
            Total: results.Total,
            Passed: results.Passed,
            Failed: results.Failed,
            Skipped: results.Skipped,
            Other: results.Other,
            Slowest: results.Slowest,
            Failures: results.Failures,
            Warnings: warnings);
    }

    private static List<string> EnumerateTrxFiles(string resultsDirectory, List<string> warnings)
    {
        var trxFiles = new List<string>(MaxTrxFiles);
        var pendingDirectories = new Queue<string>();
        pendingDirectories.Enqueue(resultsDirectory);
        var visitedDirectories = 0;
        var visitedEntries = 0;

        while (pendingDirectories.Count > 0)
        {
            if (visitedDirectories >= MaxTraversalDirectories)
            {
                warnings.Add($"TRX directory traversal cap reached: visited first {MaxTraversalDirectories} directories.");
                break;
            }

            var directory = pendingDirectories.Dequeue();
            visitedDirectories++;

            foreach (var entry in EnumerateDirectoryEntries(directory, warnings))
            {
                visitedEntries++;
                if (visitedEntries > MaxTraversalEntries)
                {
                    warnings.Add($"TRX entry traversal cap reached: visited first {MaxTraversalEntries} entries.");
                    trxFiles.Sort(StringComparer.Ordinal);
                    return trxFiles;
                }

                if (!TryGetAttributes(entry, warnings, out var attributes))
                    continue;

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Enqueue(entry);
                    continue;
                }

                if (!IsRegularFile(entry, attributes, warnings))
                    continue;

                if (!string.Equals(Path.GetExtension(entry), ".trx", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (trxFiles.Count >= MaxTrxFiles)
                {
                    warnings.Add($"TRX file cap reached: using first {MaxTrxFiles} files.");
                    trxFiles.Sort(StringComparer.Ordinal);
                    return trxFiles;
                }

                trxFiles.Add(entry);
            }
        }

        trxFiles.Sort(StringComparer.Ordinal);
        return trxFiles;
    }

    private static IEnumerable<string> EnumerateDirectoryEntries(string directory, List<string> warnings)
    {
        IEnumerator<string>? enumerator;
        try
        {
            enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not enumerate TRX directory: {GetWarningReason(ex)}");
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string entry;
                try
                {
                    if (!enumerator.MoveNext())
                        yield break;

                    entry = enumerator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Could not enumerate TRX directory: {GetWarningReason(ex)}");
                    yield break;
                }

                yield return entry;
            }
        }
    }

    private static bool IsRegularFile(string path, FileAttributes attributes, List<string> warnings)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            return false;

        if (OperatingSystem.IsWindows())
            return true;

        if (!UnixFileStatus.TryGetFileMode(path, out var mode))
        {
            warnings.Add("Could not inspect TRX traversal entry: file_type_unavailable");
            return false;
        }

        return (mode & UnixFileStatus.FileTypeMask) == UnixFileStatus.RegularFile;
    }

    private static bool TryGetAttributes(string path, List<string> warnings, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not inspect TRX traversal entry: {GetWarningReason(ex)}");
            attributes = default;
            return false;
        }
    }

    private static bool CanReadTrxFile(string resultsDirectory, string path, List<string> warnings)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Length > MaxTrxFileBytes)
            {
                warnings.Add($"TRX file exceeds {MaxTrxFileBytes} byte cap: {FormatTrxPath(resultsDirectory, path)}");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not inspect {FormatTrxPath(resultsDirectory, path)}: {GetWarningReason(ex)}");
            return false;
        }
    }

    private static string FormatTrxPath(string resultsDirectory, string path)
    {
        try
        {
            var relativePath = Path.GetRelativePath(resultsDirectory, path);
            if (!IsParentTraversal(relativePath) && !Path.IsPathFullyQualified(relativePath))
                return NormalizePath(relativePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
        }

        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? "<trx-file>" : fileName;
    }

    private static bool IsParentTraversal(string path) =>
        path == ".." ||
        path.StartsWith("../", StringComparison.Ordinal) ||
        path.StartsWith(@"..\", StringComparison.Ordinal);

    private static string NormalizePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string GetWarningReason(Exception ex) => ex switch
    {
        XmlException => "invalid_xml",
        UnauthorizedAccessException => "access_denied",
        IOException => "io_error",
        _ => "unknown_error"
    };

    private static IEnumerable<TrxTestResult> ReadResults(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, CreateXmlReaderSettings());

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "UnitTestResult")
                continue;

            var testName = reader.GetAttribute("testName");
            var outcome = reader.GetAttribute("outcome");

            if (string.IsNullOrWhiteSpace(testName) || string.IsNullOrWhiteSpace(outcome))
                continue;

            yield return new TrxTestResult(
                TestName: testName.Trim(),
                Outcome: outcome.Trim(),
                Duration: ParseDuration(reader.GetAttribute("duration")));
        }
    }

    private static XmlReaderSettings CreateXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = MaxTrxFileBytes,
        XmlResolver = null
    };

    private static void AddTopResult(List<TrxTestResult> results, TrxTestResult result, int limit)
    {
        results.Add(result);
        results.Sort(CompareByDurationDescendingThenName);

        if (results.Count > limit)
            results.RemoveAt(limit);
    }

    private static int CompareByDurationDescendingThenName(TrxTestResult left, TrxTestResult right)
    {
        var duration = right.Duration.CompareTo(left.Duration);
        return duration != 0
            ? duration
            : string.Compare(left.TestName, right.TestName, StringComparison.Ordinal);
    }

    private sealed class TrxResultAccumulator
    {
        private readonly int _top;

        public TrxResultAccumulator(int top)
        {
            _top = top;
            Slowest = new List<TrxTestResult>(Math.Min(top, 16));
            Failures = new List<TrxTestResult>(Math.Min(top, 16));
        }

        public int Total { get; private set; }

        public int Passed { get; private set; }

        public int Failed { get; private set; }

        public int Skipped { get; private set; }

        public int Other => Total - Passed - Failed - Skipped;

        public List<TrxTestResult> Slowest { get; }

        public List<TrxTestResult> Failures { get; }

        public void Add(TrxTestResult result)
        {
            Total++;

            if (IsOutcome(result, "Passed"))
            {
                Passed++;
            }
            else if (IsFailureOutcome(result))
            {
                Failed++;
                AddTopResult(Failures, result, _top);
            }
            else if (IsOutcome(result, "NotExecuted") || IsOutcome(result, "Skipped"))
            {
                Skipped++;
            }

            AddTopResult(Slowest, result, _top);
        }

        public void Merge(TrxResultAccumulator other)
        {
            Total += other.Total;
            Passed += other.Passed;
            Failed += other.Failed;
            Skipped += other.Skipped;

            foreach (var result in other.Slowest)
            {
                AddTopResult(Slowest, result, _top);
            }

            foreach (var result in other.Failures)
            {
                AddTopResult(Failures, result, _top);
            }
        }
    }

    private static class UnixFileStatus
    {
        internal const int FileTypeMask = 0xF000;
        internal const int RegularFile = 0x8000;

        internal static bool TryGetFileMode(string filePath, out int mode)
        {
            mode = 0;
            try
            {
                if (NativeMethods.Stat(filePath, out var status) != 0)
                    return false;

                mode = status.Mode;
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FileStatus
        {
            internal FileStatusFlags Flags;
            internal int Mode;
            internal uint Uid;
            internal uint Gid;
            internal long Size;
            internal long ATime;
            internal long ATimeNsec;
            internal long MTime;
            internal long MTimeNsec;
            internal long CTime;
            internal long CTimeNsec;
            internal long BirthTime;
            internal long BirthTimeNsec;
            internal long Dev;
            internal long RDev;
            internal long Ino;
            internal uint UserFlags;
        }

        [System.Flags]
        private enum FileStatusFlags : uint
        {
            None = 0,
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
            internal static extern int Stat(string path, out FileStatus output);
        }
    }

    private static TimeSpan ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TimeSpan.Zero;

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : TimeSpan.Zero;
    }

    private static bool IsOutcome(TrxTestResult result, string outcome) =>
        string.Equals(result.Outcome, outcome, StringComparison.OrdinalIgnoreCase);

    private static bool IsFailureOutcome(TrxTestResult result) =>
        IsOutcome(result, "Failed") ||
        IsOutcome(result, "Error") ||
        IsOutcome(result, "Timeout") ||
        IsOutcome(result, "Aborted") ||
        IsOutcome(result, "NotRunnable") ||
        IsOutcome(result, "Disconnected");
}

public static class TrxTelemetryRenderer
{
    public static string Render(TrxTelemetrySummary summary)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.WriteLine("TRX telemetry summary");
        writer.WriteLine($"Results directory: {summary.ResultsDirectory}");
        writer.WriteLine($"TRX files: {summary.TrxFileCount}");
        writer.WriteLine($"Tests: {summary.Total}; passed: {summary.Passed}; failed: {summary.Failed}; skipped: {summary.Skipped}; other: {summary.Other}");

        foreach (var warning in summary.Warnings)
        {
            writer.WriteLine($"Warning: {warning}");
        }

        if (summary.Failures.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Failed tests:");
            foreach (var result in summary.Failures)
            {
                writer.WriteLine($"- {result.TestName} ({result.Outcome}, {FormatDuration(result.Duration)})");
            }
        }

        if (summary.Slowest.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Slowest tests:");
            foreach (var result in summary.Slowest)
            {
                writer.WriteLine($"- {result.TestName} ({result.Outcome}, {FormatDuration(result.Duration)})");
            }
        }

        return writer.ToString();
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds >= 1
            ? $"{duration.TotalSeconds:F3}s"
            : $"{duration.TotalMilliseconds:F0}ms";
}

public sealed record TrxTelemetrySummary(
    string ResultsDirectory,
    int TrxFileCount,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    int Other,
    IReadOnlyList<TrxTestResult> Slowest,
    IReadOnlyList<TrxTestResult> Failures,
    IReadOnlyList<string> Warnings);

public sealed record TrxTestResult(string TestName, string Outcome, TimeSpan Duration);
