using System.Text;
using System.Text.Json;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
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
            return WriteResultOnlyNdjson(records, options);

        var emittedRecords = records.Count;
        string? terminalLine = null;

        string BuildTerminal(
            int returnedCount,
            bool interrupted,
            bool truncated,
            int? firstOmittedResultBytes,
            int omittedCount,
            int omittedRecordCount,
            string? recoveryGuidance,
            bool includeSelectionAccounting)
            => BuildJsonStreamDoneLine(
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
                selectors: includeSelectionAccounting ? selectors : null);

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
                WriteUsageError(
                    $"{commandName} NDJSON terminal record is {JsonLineBytes(requiredTerminal)} bytes and exceeds --max-json-bytes {options.MaxJsonBytes.Value}.",
                    options,
                    commandName,
                    "Increase --max-json-bytes; the hard cap includes both result records and the terminal record.");
                return new(0, false, null, null, CommandExitCodes.UsageError);
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

    private static NdjsonStreamWriteResult WriteResultOnlyNdjson(
        IReadOnlyList<NdjsonOutputRecord> records,
        QueryCommandOptions options)
    {
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
