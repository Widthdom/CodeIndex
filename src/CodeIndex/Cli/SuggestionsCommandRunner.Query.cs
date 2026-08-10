using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

internal static partial class SuggestionsCommandRunner
{
    private const int SuggestionSummaryStatusLimit = 16;
    private const int SuggestionSummaryCategoryLimit = 32;
    private const int SuggestionSummaryLanguageLimit = 20;

    private static bool MatchesQuery(SuggestionRecord record, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0)
            return true;

        if (ContainsSuggestionQuery(record.Id, normalizedQuery)
            || ContainsSuggestionQuery(record.SampledTitle, normalizedQuery)
            || ContainsSuggestionQuery(record.Description, normalizedQuery)
            || ContainsSuggestionQuery(record.Context, normalizedQuery)
            || ContainsSuggestionQuery(record.Category, normalizedQuery)
            || ContainsSuggestionQuery(record.Language, normalizedQuery))
        {
            return true;
        }

        if (record.EvidencePaths == null)
            return false;

        foreach (var evidencePath in record.EvidencePaths)
        {
            if (ContainsSuggestionQuery(evidencePath, normalizedQuery))
                return true;
        }

        return false;
    }

    private static bool ContainsSuggestionQuery(string? value, string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var redacted = SuggestionStore.RedactSensitiveText(value, out _);
        return NormalizeSuggestionQueryText(redacted).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSuggestionQueryText(string value)
        => value.Normalize(NormalizationForm.FormKC).Trim();

    private static int RunStructuredQueryOutput(
        IReadOnlyList<SuggestionRecord> filteredRecords,
        IReadOnlyList<SuggestionRecord> pageRecords,
        Options options,
        JsonSerializerOptions jsonOptions,
        bool exportDetails)
    {
        var resultNodes = new JsonArray();
        if (!options.Count && !options.SummaryOnly)
        {
            foreach (var record in pageRecords)
            {
                resultNodes.Add(options.Compact
                    ? ToCompactQueryItem(record)
                    : SerializeStructuredQueryItem(record, jsonOptions, exportDetails));
            }
        }

        var totalCount = filteredRecords.Count;
        var offset = Math.Min(options.Offset, totalCount);
        var aggregateMode = options.Count || options.SummaryOnly;
        var pageCount = aggregateMode ? 0 : pageRecords.Count;
        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["mode"] = options.Count ? "count" : options.SummaryOnly ? "summary" : options.Compact ? "compact" : "full",
            ["query"] = RedactSuggestionOutputValue(options.Query),
            ["total_count"] = totalCount,
            ["total_count_authoritative"] = true,
            ["returned_count"] = resultNodes.Count,
            ["offset"] = offset,
            ["omitted_count"] = Math.Max(0, totalCount - resultNodes.Count),
            ["pagination_omitted_count"] = aggregateMode ? 0 : Math.Max(0, totalCount - pageCount),
            ["byte_limit_omitted_count"] = 0,
            ["projection_omitted_count"] = options.Count || options.SummaryOnly ? totalCount : 0,
            ["truncated"] = false,
            ["has_more"] = false,
            ["next_offset"] = null,
            ["results"] = resultNodes,
        };

        if (options.Count)
            payload["count"] = totalCount;
        if (options.SummaryOnly)
            payload["summary"] = BuildSuggestionSummary(filteredRecords);
        if (options.MaxJsonBytes != null)
            payload["output_byte_limit"] = options.MaxJsonBytes.Value;

        return WriteBoundedStructuredQueryPayload(payload, resultNodes, totalCount, offset, pageCount, options, jsonOptions);
    }

    private static JsonNode SerializeStructuredQueryItem(
        SuggestionRecord record,
        JsonSerializerOptions jsonOptions,
        bool exportDetails)
    {
        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        return exportDetails
            ? JsonSerializer.SerializeToNode(ToExportDetail(record), context.SuggestionDetailJsonResult)!
            : JsonSerializer.SerializeToNode(ToListItem(record), context.SuggestionListItemJsonResult)!;
    }

    private static JsonObject ToCompactQueryItem(SuggestionRecord record)
    {
        var redactedTitle = RedactSuggestionOutputValue(record.SampledTitle ?? record.Description) ?? string.Empty;
        var evidencePaths = new JsonArray();
        foreach (var evidencePath in NormalizeEvidencePaths(record).Take(SuggestionEvidencePaths.MaxCount))
        {
            var redactedPath = RedactSuggestionOutputValue(evidencePath);
            if (redactedPath != null)
                evidencePaths.Add(redactedPath);
        }

        return new JsonObject
        {
            ["id"] = record.Id,
            ["title"] = FormatTitle(redactedTitle, 120),
            ["status"] = GetStatus(record),
            ["evidence_paths"] = evidencePaths,
        };
    }

    private static JsonObject BuildSuggestionSummary(IReadOnlyList<SuggestionRecord> records)
    {
        return new JsonObject
        {
            ["by_status"] = BuildSuggestionCountSummary(records.Select(GetStatus), SuggestionSummaryStatusLimit),
            ["by_category"] = BuildSuggestionCountSummary(
                records.Select(record => RedactSuggestionOutputValue(record.Category) ?? "unknown"),
                SuggestionSummaryCategoryLimit),
            ["by_language"] = BuildSuggestionCountSummary(
                records.Select(record => string.IsNullOrWhiteSpace(record.Language)
                    ? "unknown"
                    : RedactSuggestionOutputValue(record.Language) ?? "unknown"),
                SuggestionSummaryLanguageLimit),
        };
    }

    private static JsonObject BuildSuggestionCountSummary(IEnumerable<string> values, int limit)
    {
        var groups = values
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.Key, Count: group.Count()))
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Name, StringComparer.Ordinal)
            .ToList();
        var counts = new JsonObject();
        foreach (var group in groups.Take(limit))
            counts[group.Name] = group.Count;

        return new JsonObject
        {
            ["distinct_count"] = groups.Count,
            ["returned_distinct_count"] = counts.Count,
            ["omitted_distinct_count"] = Math.Max(0, groups.Count - counts.Count),
            ["truncated"] = groups.Count > counts.Count,
            ["counts"] = counts,
        };
    }

    private static int WriteBoundedStructuredQueryPayload(
        JsonObject payload,
        JsonArray results,
        int totalCount,
        int offset,
        int pageCount,
        Options options,
        JsonSerializerOptions jsonOptions)
    {
        UpdateStructuredQueryPayloadMetadata(payload, results.Count, totalCount, offset, pageCount, options);
        var json = payload.ToJsonString(jsonOptions);
        if (options.MaxJsonBytes == null || GetTerminatedUtf8ByteCount(json) <= options.MaxJsonBytes.Value)
        {
            CommandOutputWriter.WriteRawJson(json);
            return CommandExitCodes.Success;
        }

        var resultRows = results.Select(static node => node!).ToArray();
        ResizeStructuredQueryResults(results, resultRows, 0);
        UpdateStructuredQueryPayloadMetadata(payload, 0, totalCount, offset, pageCount, options);
        var emptyJson = payload.ToJsonString(jsonOptions);
        var emptyByteCount = GetTerminatedUtf8ByteCount(emptyJson);
        if (emptyByteCount > options.MaxJsonBytes.Value)
        {
            return WriteUsageError(
                $"--max-json-bytes {options.MaxJsonBytes.Value} is too small for the suggestion metadata envelope; at least {emptyByteCount} bytes are required.",
                json: false,
                jsonOptions,
                "Increase --max-json-bytes; no partial JSON was emitted.");
        }

        var fittingCount = 0;
        var failingCount = resultRows.Length;
        while (fittingCount + 1 < failingCount)
        {
            var candidateCount = fittingCount + ((failingCount - fittingCount) / 2);
            ResizeStructuredQueryResults(results, resultRows, candidateCount);
            UpdateStructuredQueryPayloadMetadata(payload, candidateCount, totalCount, offset, pageCount, options);
            var candidateJson = payload.ToJsonString(jsonOptions);
            if (GetTerminatedUtf8ByteCount(candidateJson) <= options.MaxJsonBytes.Value)
                fittingCount = candidateCount;
            else
                failingCount = candidateCount;
        }

        ResizeStructuredQueryResults(results, resultRows, fittingCount);
        UpdateStructuredQueryPayloadMetadata(payload, fittingCount, totalCount, offset, pageCount, options);
        json = payload.ToJsonString(jsonOptions);
        CommandOutputWriter.WriteRawJson(json);
        return CommandExitCodes.Success;
    }

    private static void UpdateStructuredQueryPayloadMetadata(
        JsonObject payload,
        int returnedCount,
        int totalCount,
        int offset,
        int pageCount,
        Options options)
    {
        var byteLimitOmittedCount = Math.Max(0, pageCount - returnedCount);
        var nextOffset = offset + returnedCount;
        var hasMore = !options.Count && !options.SummaryOnly && nextOffset < totalCount;
        payload["returned_count"] = returnedCount;
        payload["omitted_count"] = Math.Max(0, totalCount - returnedCount);
        payload["byte_limit_omitted_count"] = byteLimitOmittedCount;
        payload["truncated"] = byteLimitOmittedCount > 0;
        payload["has_more"] = hasMore;
        payload["next_offset"] = hasMore && returnedCount > 0 ? nextOffset : null;
        if (byteLimitOmittedCount > 0)
        {
            payload["recovery_guidance"] = returnedCount > 0
                ? $"Increase --max-json-bytes or resume with --offset {nextOffset}."
                : "Increase --max-json-bytes; the first remaining row does not fit the current byte limit.";
        }
        else
            payload.Remove("recovery_guidance");
    }

    private static void ResizeStructuredQueryResults(
        JsonArray results,
        IReadOnlyList<JsonNode> resultRows,
        int count)
    {
        while (results.Count > count)
            results.RemoveAt(results.Count - 1);
        while (results.Count < count)
            results.Add(resultRows[results.Count]);
    }

    private static int GetTerminatedUtf8ByteCount(string json)
        => Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Environment.NewLine);
}
