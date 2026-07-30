using System.Globalization;
using System.Text.Json;

namespace CodeIndex.Cli;

internal static class DiffResultWriter
{
    internal static int? WriteResult(DiffJsonResult result, DiffCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (options.SummaryOnly)
            return WriteSummaryJson(result, options, jsonOptions);
        else if (options.Json)
            return WriteJson(result, options, jsonOptions);
        else
            WriteText(result, options);
        return null;
    }

    internal static int WriteCommandError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string message,
        int exitCode,
        string? hint = null,
        string? errorCode = null,
        int? maxJsonBytes = null)
    {
        if (json)
        {
            var serialized = JsonSerializer.Serialize(
                new CommandErrorJsonResult("error", message, hint, errorCode),
                CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult);
            if (maxJsonBytes.HasValue && !FitsJsonLine(serialized, maxJsonBytes.Value))
            {
                serialized = JsonSerializer.Serialize(
                    new CommandErrorJsonResult(
                        "error",
                        "diff error details were omitted to honor --max-json-bytes",
                        "Increase --max-json-bytes and retry to inspect the full error.",
                        errorCode),
                    CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult);
            }

            if (!maxJsonBytes.HasValue || FitsJsonLine(serialized, maxJsonBytes.Value))
                Console.WriteLine(serialized);
        }
        else
        {
            CommandErrorWriter.WriteStderr($"Error [{errorCode ?? CommandErrorCodes.UsageError}]: {message}");
            if (!string.IsNullOrWhiteSpace(hint))
                CommandErrorWriter.WriteStderr($"Hint: {hint}");
        }
        return exitCode;
    }

    internal static string FormatDelta(long delta)
        => delta >= 0 ? $"+{delta}" : delta.ToString(CultureInfo.InvariantCulture);

    private static int? WriteJson(
        DiffJsonResult result,
        DiffCommandOptions options,
        JsonSerializerOptions jsonOptions)
    {
        if (!options.Detailed)
        {
            var serialized = JsonSerializer.Serialize(
                result,
                CliJsonSerializerContextFactory.Create(jsonOptions).DiffJsonResult);
            if (options.MaxJsonBytes.HasValue && !FitsJsonLine(serialized, options.MaxJsonBytes.Value))
            {
                return WriteCommandError(
                    json: true,
                    jsonOptions,
                    "--max-json-bytes is too small for the requested diff samples",
                    CommandExitCodes.UsageError,
                    "Increase --max-json-bytes, lower --limit, or use --summary-only.",
                    CommandErrorCodes.UsageError,
                    options.MaxJsonBytes);
            }

            Console.WriteLine(serialized);
            return null;
        }

        var budget = options.MaxJsonBytes ?? DiffCommandRunner.DefaultDiffJsonBytes;
        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        var sourceRecords = result.Records ?? [];
        var maximumCandidate = BuildBoundedCandidate(
            result,
            options,
            sourceRecords,
            sourceRecords.Count,
            budget,
            context);
        var maximumJson = JsonSerializer.Serialize(maximumCandidate, context.DiffJsonResult);
        if (FitsJsonLine(maximumJson, budget))
        {
            Console.WriteLine(maximumJson);
            return null;
        }

        var low = 0;
        var high = Math.Min(
            GetSerializedRecordCeiling(sourceRecords, budget, context),
            sourceRecords.Count - 1);
        DiffJsonResult? bestResult = null;
        string? bestJson = null;
        while (low <= high)
        {
            var candidateCount = low + ((high - low) / 2);
            var candidate = BuildBoundedCandidate(result, options, sourceRecords, candidateCount, budget, context);
            var candidateJson = JsonSerializer.Serialize(candidate, context.DiffJsonResult);
            if (FitsJsonLine(candidateJson, budget))
            {
                bestResult = candidate;
                bestJson = candidateJson;
                low = candidateCount + 1;
            }
            else
            {
                high = candidateCount - 1;
            }
        }

        if (bestResult is null || bestJson is null)
        {
            return WriteCommandError(
                json: true,
                jsonOptions,
                $"--max-json-bytes is too small for detailed diff metadata; use at least {DiffCommandRunner.MinDiffJsonBytes}",
                CommandExitCodes.UsageError,
                "Increase --max-json-bytes and rerun the same database comparison.",
                CommandErrorCodes.UsageError,
                budget);
        }

        Console.WriteLine(bestJson);
        return null;
    }

    private static int? WriteSummaryJson(
        DiffJsonResult result,
        DiffCommandOptions options,
        JsonSerializerOptions jsonOptions)
    {
        var serialized = JsonSerializer.Serialize(
            new DiffSummaryOnlyJsonResult(result.Status, result.Identical, result.LeftDb, result.RightDb, result.Summary),
            CliJsonSerializerContextFactory.Create(jsonOptions).DiffSummaryOnlyJsonResult);
        if (options.MaxJsonBytes.HasValue && !FitsJsonLine(serialized, options.MaxJsonBytes.Value))
        {
            return WriteCommandError(
                json: true,
                jsonOptions,
                "--max-json-bytes is too small for the diff summary",
                CommandExitCodes.UsageError,
                "Increase --max-json-bytes and rerun the same database comparison.",
                CommandErrorCodes.UsageError,
                options.MaxJsonBytes);
        }

        Console.WriteLine(serialized);
        return null;
    }

    private static void WriteText(DiffJsonResult result, DiffCommandOptions options)
    {
        Console.WriteLine("Index database diff");
        Console.WriteLine($"  left   : {result.LeftDb}");
        Console.WriteLine($"  right  : {result.RightDb}");
        Console.WriteLine($"  status : {result.Status}");
        Console.WriteLine($"  mode   : {result.Summary.ComparisonMode}");
        Console.WriteLine($"  schema : {result.Summary.LeftSchemaVersion} -> {result.Summary.RightSchemaVersion}");
        Console.WriteLine($"  files  : {result.Summary.LeftFileCount} -> {result.Summary.RightFileCount} ({FormatDelta(result.Summary.FileCountDelta)})");
        Console.WriteLine($"  symbols: {result.Summary.LeftSymbolCount} -> {result.Summary.RightSymbolCount} ({FormatDelta(result.Summary.SymbolCountDelta)})");
        Console.WriteLine($"  refs   : {result.Summary.LeftReferenceCount} -> {result.Summary.RightReferenceCount} ({FormatDelta(result.Summary.ReferenceCountDelta)})");
        WriteDifferenceCategories(result.Summary.Categories);
        if (result.Offset > 0)
            Console.WriteLine($"  page   : offset {result.Offset}, limit {result.Limit}");

        WriteList("files only in left", result.FilesOnlyInLeft);
        WriteList("files only in right", result.FilesOnlyInRight);
        if (options.Detailed)
            WriteRecords(result.Records ?? []);
        if (result.HasMore && result.NextOffset is int nextOffset)
            Console.WriteLine($"  more   : rerun with --offset {nextOffset}");
        else if (result.HasMore && result.Limit == 0)
            Console.WriteLine("  more   : rerun with a positive --limit");
    }

    private static void WriteList(string label, List<string> values)
    {
        if (values.Count == 0)
            return;
        Console.WriteLine($"  {label}:");
        foreach (var value in values)
            Console.WriteLine($"    - {value}");
    }

    private static void WriteDifferenceCategories(List<DiffCategorySummaryJsonResult> categories)
    {
        foreach (var category in categories.Where(category => category.Different))
        {
            var disposition = category.Included ? "included" : "excluded";
            Console.WriteLine(
                $"  {category.Category} ({disposition}): {string.Join(", ", category.Reasons)}");
        }
    }

    private static void WriteRecords(List<DiffRecordJsonResult> records)
    {
        if (records.Count == 0)
            return;

        Console.WriteLine("  detailed records:");
        foreach (var record in records)
        {
            Console.WriteLine($"    - {record.Area} {record.Side} identity_sha256={record.IdentitySha256}");
            foreach (var field in record.Fields)
            {
                var value = field.Redacted
                    ? $"[redacted byte_length={field.ByteLength} sha256={field.Sha256}]"
                    : field.Value ?? "null";
                Console.WriteLine($"      {field.Name}: {value}");
            }
        }
    }

    private static DiffJsonResult BuildBoundedCandidate(
        DiffJsonResult source,
        DiffCommandOptions options,
        List<DiffRecordJsonResult> sourceRecords,
        int returnedCount,
        int budget,
        CliJsonSerializerContext context)
    {
        var records = sourceRecords.Take(returnedCount).ToList();
        var totalCount = source.TotalCount ?? sourceRecords.Count;
        var omittedCount = totalCount - returnedCount;
        var hasMore = totalCount > (long)source.Offset + returnedCount;
        var canAdvance = hasMore && returnedCount > 0;
        var nextOffset = canAdvance
            ? checked(source.Offset + returnedCount)
            : default(int?);
        var selectionFingerprint = source.SelectionFingerprint
            ?? throw new InvalidOperationException("detailed diff selection fingerprint is missing");
        var currentCursor = source.CurrentCursor
            ?? DiffCursorCodec.Encode(source.Offset, selectionFingerprint);
        var nextCursor = nextOffset.HasValue
            ? DiffCursorCodec.Encode(nextOffset.Value, selectionFingerprint)
            : null;
        var byteTruncated = returnedCount < sourceRecords.Count;
        var diagnostics = (source.Diagnostics ?? [])
            .Where(item => item.Code is not "diff_records_truncated" and not "diff_json_bytes_truncated")
            .ToList();
        if (omittedCount > 0)
        {
            diagnostics.Add(new DiffDiagnosticJsonResult(
                byteTruncated ? "diff_json_bytes_truncated" : "diff_records_truncated",
                byteTruncated && returnedCount == 0
                    ? "No whole detailed diff record fits within --max-json-bytes; increase the byte budget or rerun without --include-content."
                    : byteTruncated
                    ? "Detailed diff output stopped at a whole-record boundary to honor --max-json-bytes; use replay.next_page_arguments to continue."
                    : hasMore && returnedCount > 0
                        ? "Detailed diff records were omitted; use replay.next_page_arguments to continue from the next whole record."
                        : hasMore && options.Limit == 0
                            ? "Detailed diff records were omitted because --limit is 0; rerun with a positive --limit."
                        : "Detailed diff records before the requested offset were omitted from this page."));
        }

        int? firstOmittedRecordBytes = null;
        if (byteTruncated)
        {
            firstOmittedRecordBytes = JsonSerializer.SerializeToUtf8Bytes(
                sourceRecords[returnedCount],
                context.DiffRecordJsonResult).Length;
        }

        return source with
        {
            Records = records,
            ReturnedCount = returnedCount,
            OmittedCount = omittedCount,
            HasMore = hasMore,
            NextOffset = nextOffset,
            NextCursor = nextCursor,
            Replay = DiffCommandRunner.BuildReplayMetadata(
                options,
                selectionFingerprint,
                currentCursor,
                nextCursor,
                budget),
            Truncated = omittedCount > 0,
            TruncationReason = omittedCount > 0
                ? byteTruncated ? "max_json_bytes" : source.TruncationReason ?? "limit_or_offset"
                : null,
            Diagnostics = diagnostics.Count > 0 ? diagnostics : null,
            MaxJsonBytes = budget,
            FirstOmittedRecordBytes = firstOmittedRecordBytes,
        };
    }

    private static int GetSerializedRecordCeiling(
        List<DiffRecordJsonResult> records,
        int budget,
        CliJsonSerializerContext context)
    {
        long measuredBytes = 0;
        for (var i = 0; i < records.Count; i++)
        {
            var recordBytes = JsonSerializer.SerializeToUtf8Bytes(
                records[i],
                context.DiffRecordJsonResult).LongLength;
            var separatorBytes = i == 0 ? 0 : 1;
            if (recordBytes + separatorBytes > budget - measuredBytes)
                return i + 1;
            measuredBytes += recordBytes + separatorBytes;
        }

        return records.Count;
    }

    private static bool FitsJsonLine(string json, int maxJsonBytes)
        => System.Text.Encoding.UTF8.GetByteCount(json)
            + System.Text.Encoding.UTF8.GetByteCount(Console.Out.NewLine)
            <= maxJsonBytes;
}
