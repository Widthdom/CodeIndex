using System.Globalization;
using System.Xml;

namespace CodeIndex.TestTelemetry;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            if (args[0] != "summarize")
                throw new TelemetryException($"Unknown command '{args[0]}'.");

            var options = ParseSummarizeOptions(args[1..]);
            var summary = TrxTelemetry.Load(options.ResultsDirectory, options.Top);
            Console.Out.Write(TrxTelemetryRenderer.Render(summary));
            return 0;
        }
        catch (TelemetryException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool IsHelp(string arg) => arg is "-h" or "--help" or "help";

    private static void PrintUsage()
    {
        Console.Out.WriteLine("Usage:");
        Console.Out.WriteLine("  dotnet run --project tools/CodeIndex.TestTelemetry -- summarize --results-directory ./TestResults [--top 10]");
    }

    private static SummarizeOptions ParseSummarizeOptions(string[] args)
    {
        var resultsDirectory = "TestResults";
        var top = 10;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--results-directory")
            {
                if (i + 1 >= args.Length)
                    throw new TelemetryException("Missing value for --results-directory.");

                resultsDirectory = args[++i];
                continue;
            }

            if (arg == "--top")
            {
                if (i + 1 >= args.Length)
                    throw new TelemetryException("Missing value for --top.");

                if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out top) ||
                    top <= 0 ||
                    top > TrxTelemetry.MaxTop)
                {
                    throw new TelemetryException($"--top must be between 1 and {TrxTelemetry.MaxTop}.");
                }

                continue;
            }

            throw new TelemetryException($"Unknown option '{arg}'.");
        }

        return new SummarizeOptions(resultsDirectory, top);
    }

    private sealed record SummarizeOptions(string ResultsDirectory, int Top);
}

public static class TrxTelemetry
{
    public const int MaxTop = 100;
    public const int MaxTrxFiles = 256;
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
        var slowest = new List<TrxTestResult>(Math.Min(top, 16));
        var failures = new List<TrxTestResult>(Math.Min(top, 16));
        var total = 0;
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var path in trxFiles)
        {
            if (!CanReadTrxFile(resultsDirectory, path, warnings))
                continue;

            try
            {
                foreach (var result in ReadResults(path))
                {
                    total++;

                    if (IsOutcome(result, "Passed"))
                    {
                        passed++;
                    }
                    else if (IsFailureOutcome(result))
                    {
                        failed++;
                        AddTopResult(failures, result, top);
                    }
                    else if (IsOutcome(result, "NotExecuted") || IsOutcome(result, "Skipped"))
                    {
                        skipped++;
                    }

                    AddTopResult(slowest, result, top);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                warnings.Add($"Could not parse {FormatTrxPath(resultsDirectory, path)}: {GetWarningReason(ex)}");
            }
        }

        var other = total - passed - failed - skipped;

        return new TrxTelemetrySummary(
            ResultsDirectory: resultsDirectory,
            TrxFileCount: trxFiles.Count,
            Total: total,
            Passed: passed,
            Failed: failed,
            Skipped: skipped,
            Other: other,
            Slowest: slowest,
            Failures: failures,
            Warnings: warnings);
    }

    private static List<string> EnumerateTrxFiles(string resultsDirectory, List<string> warnings)
    {
        var trxFiles = new List<string>(MaxTrxFiles);

        try
        {
            foreach (var path in Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories))
            {
                if (trxFiles.Count >= MaxTrxFiles)
                {
                    warnings.Add($"TRX file cap reached: using first {MaxTrxFiles} files.");
                    break;
                }

                trxFiles.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not enumerate TRX files: {GetWarningReason(ex)}");
        }

        trxFiles.Sort(StringComparer.Ordinal);
        return trxFiles;
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

public sealed class TelemetryException(string message) : Exception(message);
