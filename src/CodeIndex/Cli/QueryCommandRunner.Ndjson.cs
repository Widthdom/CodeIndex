using System.Text;
using System.Text.Json;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private const int NdjsonResponseBudgetRetryHeadroomBytes = 1024;

    private sealed record NdjsonOutputRecord(string Line, bool CountsAsResult = true);

    private sealed record NdjsonStreamWriteResult(
        int ReturnedCount,
        bool Interrupted,
        int? FirstOmittedRecordBytes,
        string? TerminalLine,
        int ExitCode);

    private static bool TryWriteCappedJsonDiagnosticsUsageError(
        string commandName,
        QueryCommandOptions options)
    {
        if (!options.MaxJsonBytes.HasValue || (!options.Profile && !options.Verbose))
            return false;

        WriteUsageError(
            "--max-json-bytes cannot be combined with --profile or --verbose because those diagnostics add separate stdout records outside the bounded result payload.",
            options,
            commandName,
            "Remove --profile/--verbose to keep a hard stdout byte cap, or remove --max-json-bytes when diagnostic records are required.");
        return true;
    }

    private static NdjsonStreamWriteResult WriteNdjsonStream(
        IReadOnlyList<NdjsonOutputRecord> records,
        int totalCount,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        DbReader? reader,
        string commandName,
        bool limitTruncated,
        string limitRecoveryGuidance,
        bool totalCountAuthoritative = true,
        string? truncationReason = null,
        string? selectionReason = null,
        int? selectionOmittedCount = null,
        int? sourceTotal = null,
        bool? sourceTotalAuthoritative = null,
        int? selectedTotal = null,
        int? selectorOmittedCount = null,
        int? limitOmittedCount = null,
        List<SearchRowSelectorJsonResult>? selectors = null)
    {
        if (options.ResultsOnly)
            return WriteResultOnlyNdjson(records, options, jsonOptions, commandName);

        var emittedRecords = records.Count;
        string? terminalLine = null;
        var continuationCursorFactory = reader is not null && IsCursorCapableNdjson(commandName, options)
            ? new Lazy<Func<int, string>>(() => JsonEnvelopeWrapper.BuildNdjsonResponseCursorFactory(
                commandName,
                options.InvocationArgs,
                reader))
            : null;

        string BuildTerminal(
            int returnedCount,
            bool interrupted,
            bool truncated,
            int? firstOmittedResultBytes,
            int omittedCount,
            int omittedRecordCount,
            string? recoveryGuidance,
            bool includeSelectionAccounting)
        {
            var hasMore = truncated || interrupted;
            if (hasMore && totalCountAuthoritative)
            {
                var nextOffset = checked(
                    JsonEnvelopeWrapper.GetBoundedResponseOffset(commandName) + returnedCount);
                hasMore = nextOffset < totalCount;
            }
            var (nextCursor, unavailableReason) = BuildNdjsonContinuation(
                returnedCount,
                hasMore,
                continuationCursorFactory);
            return BuildJsonStreamDoneLine(
                returnedCount,
                totalCount,
                jsonOptions,
                interrupted,
                truncated,
                reader,
                maxJsonBytes: options.MaxJsonBytes,
                firstOmittedResultBytes: firstOmittedResultBytes,
                omittedCount: omittedCount,
                omittedRecordCount: omittedRecordCount,
                appliedLimit: options.Limit,
                recoveryGuidance: recoveryGuidance,
                totalCountAuthoritative: totalCountAuthoritative,
                truncationReason: truncationReason,
                selectionReason: selectionReason,
                selectionOmittedCount: selectionOmittedCount,
                sourceTotal: includeSelectionAccounting ? sourceTotal : null,
                sourceTotalAuthoritative: includeSelectionAccounting ? sourceTotalAuthoritative : null,
                selectedTotal: includeSelectionAccounting ? selectedTotal : null,
                selectorOmittedCount: includeSelectionAccounting ? selectorOmittedCount : null,
                limitOmittedCount: includeSelectionAccounting ? limitOmittedCount : null,
                selectors: includeSelectionAccounting ? selectors : null,
                nextCursor: nextCursor,
                nextCursorUnavailableReason: unavailableReason,
                hasMore: hasMore);
        }

        if (options.MaxJsonBytes.HasValue)
        {
            for (var candidate = records.Count; candidate >= 0; candidate--)
            {
                var candidateReturnedCount = CountResults(records, candidate);
                var candidateInterrupted = candidate < records.Count;
                var candidateFirstOmittedBytes = candidateInterrupted ? JsonLineBytes(records[candidate].Line) : (int?)null;
                var candidateRecoveryGuidance = candidateInterrupted
                    ? "Increase --max-json-bytes or reduce --limit. Pass --allow-partial only when exit code 0 is acceptable for incomplete output."
                    : limitTruncated ? limitRecoveryGuidance : null;
                var candidateTerminal = BuildTerminal(
                    candidateReturnedCount,
                    candidateInterrupted,
                    limitTruncated || candidateInterrupted,
                    candidateFirstOmittedBytes,
                    Math.Max(0, totalCount - candidateReturnedCount),
                    Math.Max(0, records.Count - candidate),
                    candidateRecoveryGuidance,
                    includeSelectionAccounting: true);
                var candidatePrefixBytes = PrefixBytes(records, candidate);
                if (candidatePrefixBytes + JsonLineBytes(candidateTerminal) > options.MaxJsonBytes.Value
                    && sourceTotal.HasValue)
                {
                    candidateTerminal = BuildTerminal(
                        candidateReturnedCount,
                        candidateInterrupted,
                        limitTruncated || candidateInterrupted,
                        candidateFirstOmittedBytes,
                        Math.Max(0, totalCount - candidateReturnedCount),
                        Math.Max(0, records.Count - candidate),
                        candidateRecoveryGuidance,
                        includeSelectionAccounting: false);
                }
                if (candidatePrefixBytes + JsonLineBytes(candidateTerminal) > options.MaxJsonBytes.Value)
                    continue;

                emittedRecords = candidate;
                terminalLine = candidateTerminal;
                break;
            }

            if (terminalLine == null)
            {
                var requiredTerminal = BuildTerminal(
                    0,
                    records.Count > 0,
                    limitTruncated || records.Count > 0,
                    records.Count > 0 ? JsonLineBytes(records[0].Line) : null,
                    totalCount,
                    records.Count,
                    "Increase --max-json-bytes so the bounded NDJSON terminal record fits before streaming begins.",
                    includeSelectionAccounting: true);
                if (JsonLineBytes(requiredTerminal) > options.MaxJsonBytes.Value
                    && sourceTotal.HasValue)
                {
                    requiredTerminal = BuildTerminal(
                        0,
                        records.Count > 0,
                        limitTruncated || records.Count > 0,
                        records.Count > 0 ? JsonLineBytes(records[0].Line) : null,
                        totalCount,
                        records.Count,
                        "Increase --max-json-bytes so the bounded NDJSON terminal record fits before streaming begins.",
                        includeSelectionAccounting: false);
                }
                var requiredTerminalBytes = JsonLineBytes(requiredTerminal);
                var budgetExitCode = WriteNdjsonResponseBudgetError(
                    options,
                    jsonOptions,
                    commandName,
                    $"{commandName} NDJSON terminal record is {requiredTerminalBytes} bytes and exceeds --max-json-bytes {options.MaxJsonBytes.Value}.",
                    "Increase --max-json-bytes; the hard cap includes both result records and the terminal record.",
                    requiredTerminalBytes,
                    minimumUncertain: true);
                return new(0, false, null, null, budgetExitCode);
            }
        }
        else
        {
            var returnedCount = CountResults(records, emittedRecords);
            terminalLine = BuildTerminal(
                returnedCount,
                interrupted: false,
                limitTruncated,
                firstOmittedResultBytes: null,
                Math.Max(0, totalCount - returnedCount),
                omittedRecordCount: 0,
                limitTruncated ? limitRecoveryGuidance : null,
                includeSelectionAccounting: true);
        }

        for (var i = 0; i < emittedRecords; i++)
            Console.WriteLine(records[i].Line);

        var finalReturnedCount = CountResults(records, emittedRecords);
        var interrupted = emittedRecords < records.Count;
        var exitCode = interrupted && !options.AllowPartial
            ? CommandExitCodes.PartialResult
            : CommandExitCodes.Success;
        return new(
            finalReturnedCount,
            interrupted,
            interrupted ? JsonLineBytes(records[emittedRecords].Line) : null,
            terminalLine,
            exitCode);
    }

    private static (string? Cursor, string? UnavailableReason) BuildNdjsonContinuation(
        int returnedCount,
        bool hasMore,
        Lazy<Func<int, string>>? cursorFactory)
    {
        if (!hasMore)
            return (null, null);
        if (returnedCount <= 0)
            return (null, "no_result_row_emitted");
        if (cursorFactory is null)
            return (null, "stream_not_cursor_capable");

        return (cursorFactory.Value(returnedCount), null);
    }

    private static bool IsCursorCapableNdjson(string commandName, QueryCommandOptions options)
        => commandName is "symbols" or "files"
           || commandName == "search"
           && options.RecipeName is null
           && options.NamedSearchQueries.Count == 0
           && !options.ListRecipes;

    private static NdjsonStreamWriteResult WriteResultOnlyNdjson(
        IReadOnlyList<NdjsonOutputRecord> records,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string commandName)
    {
        if (options.MaxJsonBytes.HasValue && records.Count > 0)
        {
            var firstRecordBytes = JsonLineBytes(records[0].Line);
            if (firstRecordBytes > options.MaxJsonBytes.Value)
            {
                var exitCode = WriteNdjsonResponseBudgetError(
                    options,
                    jsonOptions,
                    commandName,
                    $"{commandName} first complete NDJSON result record is {firstRecordBytes} bytes and exceeds --max-json-bytes {options.MaxJsonBytes.Value}.",
                    "Reduce projected fields or increase --max-json-bytes before streaming begins.",
                    firstRecordBytes,
                    minimumUncertain: false);
                return new(0, false, firstRecordBytes, null, exitCode);
            }
        }

        var emittedRecords = 0;
        var bytesWritten = 0;
        foreach (var record in records)
        {
            var recordBytes = JsonLineBytes(record.Line);
            if (options.MaxJsonBytes.HasValue && bytesWritten + recordBytes > options.MaxJsonBytes.Value)
                break;
            Console.WriteLine(record.Line);
            emittedRecords++;
            bytesWritten += recordBytes;
        }

        var interrupted = emittedRecords < records.Count;
        return new(
            CountResults(records, emittedRecords),
            interrupted,
            interrupted ? JsonLineBytes(records[emittedRecords].Line) : null,
            TerminalLine: null,
            interrupted && !options.AllowPartial ? CommandExitCodes.PartialResult : CommandExitCodes.Success);
    }

    private static int WriteNdjsonResponseBudgetError(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string commandName,
        string message,
        string hint,
        long minimumRequiredBytes,
        bool minimumUncertain)
    {
        var uncertainRecommendedBytes = minimumRequiredBytes + NdjsonResponseBudgetRetryHeadroomBytes;
        var retryByIncreasingBudget = minimumRequiredBytes <= MaxSearchJsonByteLimit
                                      && (!minimumUncertain
                                          || uncertainRecommendedBytes <= MaxSearchJsonByteLimit);
        var effectiveHint = retryByIncreasingBudget
            ? hint
            : $"{hint} The response minimum exceeds the usable maximum effective --max-json-bytes budget; reduce the response size before retrying.";
        return CommandErrorWriter.WriteResponseBudgetError(
            json: true,
            jsonOptions,
            commandName,
            message,
            effectiveHint,
            requestedBytes: options.RequestedMaxJsonBytes ?? options.MaxJsonBytes,
            effectiveBytes: options.MaxJsonBytes,
            minimumRequiredBytes,
            minimumRequiredBytesUncertaintyReason: minimumUncertain
                ? CommandErrorWriter.MinimumResponseBytesUncertainRuntimeEnvelope
                : null,
            recommendedBytes: retryByIncreasingBudget
                ? minimumUncertain ? uncertainRecommendedBytes : minimumRequiredBytes
                : null,
            usage: GetUsageLineOrThrow(commandName),
            retryByIncreasingBudget: retryByIncreasingBudget,
            maximumEffectiveBytes: MaxSearchJsonByteLimit);
    }

    private static int CountResults(IReadOnlyList<NdjsonOutputRecord> records, int count)
    {
        var resultCount = 0;
        for (var i = 0; i < count && i < records.Count; i++)
        {
            if (records[i].CountsAsResult)
                resultCount++;
        }
        return resultCount;
    }

    private static int PrefixBytes(IReadOnlyList<NdjsonOutputRecord> records, int count)
    {
        var bytes = 0;
        for (var i = 0; i < count && i < records.Count; i++)
            bytes += JsonLineBytes(records[i].Line);
        return bytes;
    }

    private static int JsonLineBytes(string line)
        => Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
}
