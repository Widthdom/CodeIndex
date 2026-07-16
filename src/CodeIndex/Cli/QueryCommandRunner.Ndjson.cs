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

    private static NdjsonStreamWriteResult WriteNdjsonStream(
        IReadOnlyList<NdjsonOutputRecord> records,
        int totalCount,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        DbReader? reader,
        string commandName,
        bool limitTruncated,
        string limitRecoveryGuidance)
    {
        if (options.ResultsOnly)
            return WriteResultOnlyNdjson(records, options);

        var emittedRecords = records.Count;
        string? terminalLine = null;
        if (options.MaxJsonBytes.HasValue)
        {
            for (var candidate = records.Count; candidate >= 0; candidate--)
            {
                var candidateReturnedCount = CountResults(records, candidate);
                var candidateInterrupted = candidate < records.Count;
                var candidateFirstOmittedBytes = candidateInterrupted ? JsonLineBytes(records[candidate].Line) : (int?)null;
                var candidateTerminal = BuildJsonStreamDoneLine(
                    candidateReturnedCount,
                    totalCount,
                    jsonOptions,
                    interrupted: candidateInterrupted,
                    truncated: limitTruncated || candidateInterrupted,
                    reader,
                    maxJsonBytes: options.MaxJsonBytes,
                    firstOmittedResultBytes: candidateFirstOmittedBytes,
                    omittedCount: Math.Max(0, totalCount - candidateReturnedCount),
                    omittedRecordCount: Math.Max(0, records.Count - candidate),
                    appliedLimit: options.Limit,
                    recoveryGuidance: candidateInterrupted
                        ? "Increase --max-json-bytes or reduce --limit. Pass --allow-partial only when exit code 0 is acceptable for incomplete output."
                        : limitTruncated ? limitRecoveryGuidance : null);
                if (PrefixBytes(records, candidate) + JsonLineBytes(candidateTerminal) > options.MaxJsonBytes.Value)
                    continue;

                emittedRecords = candidate;
                terminalLine = candidateTerminal;
                break;
            }

            if (terminalLine == null)
            {
                var requiredTerminal = BuildJsonStreamDoneLine(
                    count: 0,
                    totalCount,
                    jsonOptions,
                    interrupted: records.Count > 0,
                    truncated: limitTruncated || records.Count > 0,
                    reader,
                    maxJsonBytes: options.MaxJsonBytes,
                    firstOmittedResultBytes: records.Count > 0 ? JsonLineBytes(records[0].Line) : null,
                    omittedCount: totalCount,
                    omittedRecordCount: records.Count,
                    appliedLimit: options.Limit,
                    recoveryGuidance: "Increase --max-json-bytes so the bounded NDJSON terminal record fits before streaming begins.");
                WriteUsageError(
                    $"{commandName} NDJSON terminal record is {JsonLineBytes(requiredTerminal)} bytes and exceeds --max-json-bytes {options.MaxJsonBytes.Value}.",
                    GetUsageLineOrThrow(commandName),
                    "Increase --max-json-bytes; the hard cap includes both result records and the terminal record.");
                return new(0, false, null, null, CommandExitCodes.UsageError);
            }
        }
        else
        {
            var returnedCount = CountResults(records, emittedRecords);
            terminalLine = BuildJsonStreamDoneLine(
                returnedCount,
                totalCount,
                jsonOptions,
                interrupted: false,
                truncated: limitTruncated,
                reader,
                omittedCount: Math.Max(0, totalCount - returnedCount),
                omittedRecordCount: 0,
                appliedLimit: options.Limit,
                recoveryGuidance: limitTruncated ? limitRecoveryGuidance : null);
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
