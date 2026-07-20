using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CodeIndex.TestTelemetry;

public static class TrxRetryFilter
{
    public const int MaxFailedResults = 20;
    public const int MaxFilterLength = 4096;

    public static TrxRetryFilterDecision Load(string trxFile)
    {
        if (string.IsNullOrWhiteSpace(trxFile) || !File.Exists(trxFile))
            return Fallback(TrxRetryFilterReasons.TrxMissing);

        try
        {
            var attributes = File.GetAttributes(trxFile);
            if (!IsRegularFile(trxFile, attributes))
                return Fallback(TrxRetryFilterReasons.TrxNotRegular);

            if (new FileInfo(trxFile).Length > TrxTelemetry.MaxTrxFileBytes)
                return Fallback(TrxRetryFilterReasons.TrxTooLarge);

            using var stream = File.OpenRead(trxFile);
            using var reader = XmlReader.Create(stream, TrxTelemetry.CreateXmlReaderSettings());
            var document = XDocument.Load(reader, LoadOptions.None);
            return CreateDecision(document);
        }
        catch (XmlException)
        {
            return Fallback(TrxRetryFilterReasons.InvalidXml);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fallback(TrxRetryFilterReasons.TrxUnreadable);
        }
    }

    private static TrxRetryFilterDecision CreateDecision(XDocument document)
    {
        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "TestRun", StringComparison.Ordinal))
            return Fallback(TrxRetryFilterReasons.TrxInconsistent);

        var summary = GetSingleChild(root, "ResultSummary");
        var counters = summary is null ? null : GetSingleChild(summary, "Counters");
        if (summary is null || counters is null)
            return Fallback(TrxRetryFilterReasons.TrxInconsistent);

        if (!string.Equals(GetTrimmedAttribute(summary, "outcome"), "Failed", StringComparison.OrdinalIgnoreCase))
            return Fallback(TrxRetryFilterReasons.RunIncomplete);

        if (!TryReadCounters(counters, out var counterValues))
            return Fallback(TrxRetryFilterReasons.TrxInconsistent);

        if (counterValues.Error > 0 ||
            counterValues.Timeout > 0 ||
            counterValues.Aborted > 0 ||
            counterValues.Inconclusive > 0 ||
            counterValues.PassedButRunAborted > 0 ||
            counterValues.NotRunnable > 0 ||
            counterValues.Disconnected > 0 ||
            counterValues.Warning > 0 ||
            counterValues.InProgress > 0 ||
            counterValues.Pending > 0)
        {
            return Fallback(TrxRetryFilterReasons.RunIncomplete);
        }

        var resultsElement = GetSingleChild(root, "Results");
        if (resultsElement is null)
            return Fallback(TrxRetryFilterReasons.TrxInconsistent);

        var results = resultsElement.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "UnitTestResult", StringComparison.Ordinal))
            .ToArray();
        if (results.Length != counterValues.Total)
            return Fallback(TrxRetryFilterReasons.TrxInconsistent);

        var failedResults = new List<XElement>();
        var failedTestNames = new HashSet<string>(StringComparer.Ordinal);
        var passedResults = 0;
        var notExecutedResults = 0;
        foreach (var result in results)
        {
            var outcome = GetTrimmedAttribute(result, "outcome");
            if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                failedResults.Add(result);
                var testName = GetTrimmedAttribute(result, "testName");
                if (testName is null)
                    return Fallback(TrxRetryFilterReasons.TrxInconsistent);

                failedTestNames.Add(testName);
                continue;
            }

            if (string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase))
            {
                passedResults++;
                continue;
            }

            if (string.Equals(outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outcome, "Skipped", StringComparison.OrdinalIgnoreCase))
            {
                notExecutedResults++;
                continue;
            }

            return Fallback(TrxRetryFilterReasons.RunIncomplete);
        }

        if (failedResults.Count == 0 ||
            failedResults.Count != counterValues.Failed ||
            passedResults != counterValues.Passed ||
            counterValues.Executed != passedResults + failedResults.Count ||
            counterValues.Total != passedResults + failedResults.Count + notExecutedResults ||
            (counterValues.NotExecuted != 0 && counterValues.NotExecuted != notExecutedResults))
            return Fallback(TrxRetryFilterReasons.TrxInconsistent);

        if (HasUnsafeRunInfo(summary, failedTestNames))
            return Fallback(TrxRetryFilterReasons.RunIncomplete);

        if (failedResults.Count > MaxFailedResults)
        {
            return Fallback(
                TrxRetryFilterReasons.FailureLimitExceeded,
                failedResultCount: failedResults.Count);
        }

        var definitionsElement = GetSingleChild(root, "TestDefinitions");
        if (definitionsElement is null)
        {
            return Fallback(
                TrxRetryFilterReasons.FailureIdentityUnavailable,
                failedResultCount: failedResults.Count);
        }

        var definitions = ReadDefinitions(definitionsElement);
        var fullyQualifiedNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var failedResult in failedResults)
        {
            var testId = GetTrimmedAttribute(failedResult, "testId");
            if (testId is null || !definitions.TryGetValue(testId, out var fullyQualifiedName) || fullyQualifiedName is null)
            {
                return Fallback(
                    TrxRetryFilterReasons.FailureIdentityUnavailable,
                    failedResultCount: failedResults.Count);
            }

            if (!IsSafeFilterValue(fullyQualifiedName))
            {
                return Fallback(
                    TrxRetryFilterReasons.FailureNameUnsafe,
                    failedResultCount: failedResults.Count);
            }

            fullyQualifiedNames.Add(fullyQualifiedName);
        }

        var filter = string.Join(
            '|',
            fullyQualifiedNames.Select(fullyQualifiedName => $"FullyQualifiedName={fullyQualifiedName}"));
        if (filter.Length > MaxFilterLength)
        {
            return Fallback(
                TrxRetryFilterReasons.FilterLengthExceeded,
                failedResultCount: failedResults.Count,
                testMethodCount: fullyQualifiedNames.Count);
        }

        return new TrxRetryFilterDecision(
            UseFocusedRetry: true,
            Filter: filter,
            FailedResultCount: failedResults.Count,
            TestMethodCount: fullyQualifiedNames.Count,
            Reason: TrxRetryFilterReasons.Focused);
    }

    private static Dictionary<string, string?> ReadDefinitions(XElement definitionsElement)
    {
        var definitions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitionsElement.Elements()
                     .Where(element => string.Equals(element.Name.LocalName, "UnitTest", StringComparison.Ordinal)))
        {
            var testId = GetTrimmedAttribute(definition, "id");
            if (testId is null)
                continue;

            var methods = definition.Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "TestMethod", StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            string? fullyQualifiedName = null;
            if (methods.Length == 1)
            {
                var className = GetTrimmedAttribute(methods[0], "className");
                var methodName = GetTrimmedAttribute(methods[0], "name");
                if (className is not null && methodName is not null)
                {
                    fullyQualifiedName = methodName.StartsWith($"{className}.", StringComparison.Ordinal)
                        ? methodName
                        : $"{className}.{methodName}";
                }
            }

            if (!definitions.TryAdd(testId, fullyQualifiedName))
                definitions[testId] = null;
        }

        return definitions;
    }

    private static bool TryReadCounters(XElement counters, out TrxCounters values)
    {
        values = default;
        if (!TryGetNonNegativeInt(counters, "total", out var total) ||
            !TryGetNonNegativeInt(counters, "executed", out var executed) ||
            !TryGetNonNegativeInt(counters, "passed", out var passed) ||
            !TryGetNonNegativeInt(counters, "failed", out var failed) ||
            !TryGetNonNegativeInt(counters, "error", out var error) ||
            !TryGetNonNegativeInt(counters, "timeout", out var timeout) ||
            !TryGetNonNegativeInt(counters, "aborted", out var aborted) ||
            !TryGetNonNegativeInt(counters, "inconclusive", out var inconclusive) ||
            !TryGetNonNegativeInt(counters, "passedButRunAborted", out var passedButRunAborted) ||
            !TryGetNonNegativeInt(counters, "notRunnable", out var notRunnable) ||
            !TryGetNonNegativeInt(counters, "notExecuted", out var notExecuted) ||
            !TryGetNonNegativeInt(counters, "disconnected", out var disconnected) ||
            !TryGetNonNegativeInt(counters, "warning", out var warning) ||
            !TryGetNonNegativeInt(counters, "inProgress", out var inProgress) ||
            !TryGetNonNegativeInt(counters, "pending", out var pending))
        {
            return false;
        }

        values = new TrxCounters(
            total,
            executed,
            passed,
            failed,
            error,
            timeout,
            aborted,
            inconclusive,
            passedButRunAborted,
            notRunnable,
            notExecuted,
            disconnected,
            warning,
            inProgress,
            pending);
        return true;
    }

    private static bool TryGetNonNegativeInt(XElement element, string name, out int value) =>
        int.TryParse(
            GetTrimmedAttribute(element, name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value) && value >= 0;

    private static bool HasUnsafeRunInfo(XElement summary, IReadOnlySet<string> failedTestNames) =>
        summary.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "RunInfo", StringComparison.Ordinal))
            .Any(runInfo => !IsSafeRunInfo(runInfo, failedTestNames));

    private static bool IsSafeRunInfo(XElement runInfo, IReadOnlySet<string> failedTestNames)
    {
        var outcome = GetTrimmedAttribute(runInfo, "outcome");
        if (string.Equals(outcome, "Warning", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(outcome, "Error", StringComparison.OrdinalIgnoreCase))
            return false;

        var children = runInfo.Elements().Take(2).ToArray();
        if (children.Length != 1 ||
            !string.Equals(children[0].Name.LocalName, "Text", StringComparison.Ordinal) ||
            children[0].HasElements)
        {
            return false;
        }

        var text = children[0].Value.Trim();
        return TryReadXunitFailureName(text, out var failedTestName) &&
               failedTestNames.Contains(failedTestName);
    }

    private static bool TryReadXunitFailureName(string text, out string failedTestName)
    {
        const string prefix = "[xUnit.net ";
        const string separator = "]     ";
        const string suffix = " [FAIL]";
        failedTestName = string.Empty;

        if (!text.StartsWith(prefix, StringComparison.Ordinal) ||
            !text.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = text.IndexOf(separator, prefix.Length, StringComparison.Ordinal);
        if (separatorIndex < 0 ||
            !TimeSpan.TryParseExact(
                text.AsSpan(prefix.Length, separatorIndex - prefix.Length),
                @"hh\:mm\:ss\.ff",
                CultureInfo.InvariantCulture,
                TimeSpanStyles.None,
                out _))
        {
            return false;
        }

        var nameStart = separatorIndex + separator.Length;
        var nameLength = text.Length - nameStart - suffix.Length;
        if (nameLength <= 0)
            return false;

        failedTestName = text.Substring(nameStart, nameLength);
        return failedTestName.Length == failedTestName.Trim().Length;
    }

    private static XElement? GetSingleChild(XElement parent, string localName)
    {
        var matches = parent.Elements()
            .Where(element => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string? GetTrimmedAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsSafeFilterValue(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('.' or '_' or '+' or '`'))
                return false;
        }

        return value.Length > 0;
    }

    private static bool IsRegularFile(string path, FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            return false;

        if (OperatingSystem.IsWindows())
            return true;

        return UnixFileStatus.TryGetFileMode(path, out var mode) &&
               (mode & UnixFileStatus.FileTypeMask) == UnixFileStatus.RegularFile;
    }

    private static TrxRetryFilterDecision Fallback(
        string reason,
        int failedResultCount = 0,
        int testMethodCount = 0) =>
        new(
            UseFocusedRetry: false,
            Filter: null,
            FailedResultCount: failedResultCount,
            TestMethodCount: testMethodCount,
            Reason: reason);

    private readonly record struct TrxCounters(
        int Total,
        int Executed,
        int Passed,
        int Failed,
        int Error,
        int Timeout,
        int Aborted,
        int Inconclusive,
        int PassedButRunAborted,
        int NotRunnable,
        int NotExecuted,
        int Disconnected,
        int Warning,
        int InProgress,
        int Pending);
}

public static class TrxRetryFilterRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Render(TrxRetryFilterDecision decision) =>
        JsonSerializer.Serialize(decision, JsonOptions);
}

public static class TrxRetryFilterReasons
{
    public const string Focused = "focused";
    public const string TrxMissing = "trx_missing";
    public const string TrxNotRegular = "trx_not_regular";
    public const string TrxTooLarge = "trx_too_large";
    public const string TrxUnreadable = "trx_unreadable";
    public const string InvalidXml = "invalid_xml";
    public const string RunIncomplete = "run_incomplete";
    public const string TrxInconsistent = "trx_inconsistent";
    public const string FailureIdentityUnavailable = "failure_identity_unavailable";
    public const string FailureNameUnsafe = "failure_name_unsafe";
    public const string FailureLimitExceeded = "failure_limit_exceeded";
    public const string FilterLengthExceeded = "filter_length_exceeded";
}

public sealed record TrxRetryFilterDecision(
    bool UseFocusedRetry,
    string? Filter,
    int FailedResultCount,
    int TestMethodCount,
    string Reason);
