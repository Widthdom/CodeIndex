using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

/// <summary>
/// Shared opt-in projection, byte-budget, and offset-cursor contract for high-volume
/// query responses. Existing streaming and compact output remains unchanged unless a
/// bounded-response control is requested. Issue #4585.
/// 高ボリュームなクエリ応答で共有する、opt-in のフィールド投影・総バイト上限・
/// offset cursor 契約。bounded-response control が指定されない限り、既存の streaming / compact
/// 出力は変更しない。Issue #4585。
/// </summary>
internal static partial class JsonEnvelopeWrapper
{
    private const int DefaultPageLimit = 20;
    private const int ResponseBudgetRetryHeadroomBytes = 1024;
    private const int MaxPageWindow = MaxRawJsonItems;
    private const string LegacyResponseCursorPrefix = "response:v1:";
    private const string ResponseCursorPrefix = "response:v2:";
    private static readonly AsyncLocal<BoundedExecutionContext?> BoundedExecution = new();
    internal static Action? ResponseSnapshotValidatedForTesting { get; set; }
    internal static Action? PartialFamilyPageReadForTesting { get; set; }

    private static readonly HashSet<string> BoundedResponseCommands =
        ProjectionFieldRegistry.SupportedCommands.ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> AutoWrapByteBudgetCommands = new(StringComparer.Ordinal)
    {
        "find", "status", "references", "callers", "callees", "languages", "outline", "unused",
    };

    private static readonly HashSet<string> AutoWrapCompactCommands = new(StringComparer.Ordinal)
    {
        "search", "definition", "find", "status", "hotspots", "references", "callers", "callees",
        "symbols", "files", "impact", "map",
    };

    private static readonly HashSet<string> LegacyLocationCompactCommands = new(StringComparer.Ordinal)
    {
        "search", "definition", "find", "references", "callers", "callees", "symbols", "files",
    };

    private static readonly HashSet<string> PageableResponseCommands = new(StringComparer.Ordinal)
    {
        "search", "definition", "find", "hotspots", "references", "callers", "callees",
        "symbols", "files", "languages", "impact", "map", "outline", "unused",
    };

    private static readonly HashSet<string> CountableResponseCommands = new(StringComparer.Ordinal)
    {
        "search", "definition", "find", "hotspots", "references", "callers", "callees",
        "symbols", "files", "languages", "impact", "unused",
    };

    private static readonly string[] UnusedCompactResponseFields =
    [
        "path",
        "line",
        "lang",
        "kind",
        "name",
        "visibility",
        "container_name",
        "unused_bucket",
        "unused_confidence",
        "unused_contract_domain",
    ];

    private static readonly string[] PartialFamilyContinuationProjectionFields =
    [
        "partial_family_id",
        "family_members_truncated",
        "family_member_total_count",
        "family_member_total_count_authoritative",
        "family_member_returned_count",
        "family_member_omitted_count",
        "family_member_remaining_count",
        "family_members_recovery_cursor",
        "family_members_next_cursor",
    ];

    private static readonly string[] BodyContentCompanionProjectionFields =
    [
        "body_start_line",
        "body_end_line",
        "body_content_start_line",
        "body_content_end_line",
        "body_content_next_start_line",
        "body_content_truncated",
        "body_requested_start_line",
        "body_requested_end_line",
        "body_effective_start_line",
        "body_effective_end_line",
        "body_content_truncation_reasons",
        "body_content_recovery",
        "content_omitted",
        "content_omitted_reason",
    ];

    internal static bool ShouldAutoWrapBoundedResponse(string command, string[] args)
    {
        if (!BoundedResponseCommands.Contains(command)
            && !IsStandaloneWholeRowByteBudgetRequest(command, args))
            return false;
        if (command == "search" && IsSearchAggregateResponseRequest(args))
            return false;
        if (command == "find" && IsStandaloneFindCountContinuationRequest(args))
            return false;
        if (HasArgument(command, args, "--fields") || HasArgument(command, args, "--cursor"))
            return true;
        if (command == "languages"
            && HasJsonOutputSelection(command, args)
            && (HasArgument(command, args, "--limit") || HasArgument(command, args, "--top")))
        {
            return true;
        }
        if (AutoWrapByteBudgetCommands.Contains(command) && HasArgument(command, args, "--max-json-bytes"))
            return true;
        return AutoWrapCompactCommands.Contains(command) && HasCompactOutputSelection(command, args);
    }

    private static bool IsBoundedResponseRequest(string command, string[] args)
    {
        if (!BoundedResponseCommands.Contains(command)
            && !IsStandaloneWholeRowByteBudgetRequest(command, args))
            return false;
        if (command == "search" && IsSearchAggregateResponseRequest(args))
            return false;

        return HasArgument(command, args, "--fields")
               || HasArgument(command, args, "--cursor")
               || (command == "search" && HasEnvelopeFlag(command, args) && HasJsonArrayOutputSelection(command, args))
               || (command != "search" && HasEnvelopeFlag(command, args) && HasArgument(command, args, "--max-json-bytes"))
               || ShouldAutoWrapBoundedResponse(command, args);
    }

    private static bool IsStandaloneWholeRowByteBudgetRequest(string command, string[] args)
        => command is "outline" or "unused"
           && HasArgument(command, args, "--max-json-bytes");

    private static bool HasUnsupportedStandaloneBoundedControl(string command, string[] args)
        => command is "outline" or "unused"
           && (HasArgument(command, args, "--fields")
               || HasArgument(command, args, "--format")
               || ClassifyArgumentTokens(command, args)
                   .Any(token => token.IsOption && token.Value.StartsWith("--json=", StringComparison.Ordinal)));

    private static bool IsStandaloneFindCountContinuationRequest(string[] args)
        => IsFindCountResponseRequest(args)
           && HasArgument("find", args, "--cursor")
           && !HasArgument("find", args, "--fields")
           && !HasArgument("find", args, "--max-json-bytes")
           && !HasCompactOutputSelection("find", args)
           && !HasEnvelopeFlag("find", args);

    private static bool IsFindCountResponseRequest(string[] args)
    {
        var nextTokenIsLiteralQuery = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (nextTokenIsLiteralQuery)
            {
                nextTokenIsLiteralQuery = false;
                continue;
            }
            if (string.Equals(args[i], "--", StringComparison.Ordinal))
            {
                nextTokenIsLiteralQuery = true;
                continue;
            }
            if (string.Equals(args[i], "--query", StringComparison.Ordinal)
                && i + 1 < args.Length)
            {
                i++;
                continue;
            }
            if (string.Equals(args[i], "--count", StringComparison.Ordinal)
                || string.Equals(args[i], "--format=count", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(args[i], "--format", StringComparison.Ordinal)
                && i + 1 < args.Length
                && string.Equals(args[i + 1], "count", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSearchAggregateResponseRequest(string[] args)
        => HasArgument("search", args, "--recipe")
           || HasArgument("search", args, "--list-recipes")
           || HasArgument("search", args, "--named-query")
           || HasArgument("search", args, "--count")
           || HasArgument("search", args, "--group-by")
           || HasArgument("search", args, "--unique")
           || HasArgument("search", args, "--count-by")
           || HasArgument("search", args, "--summary-only");

    private static bool HasJsonOutputSelection(string command, string[] args)
        => HasArgument(command, args, "--json");

    private static bool HasJsonArrayOutputSelection(string command, string[] args)
        => ClassifyArgumentTokens(command, args)
            .Any(token => token.IsOption
                          && string.Equals(token.Value, "--json=array", StringComparison.OrdinalIgnoreCase));

    private static bool HasCompactOutputSelection(string command, string[] args)
    {
        var tokens = ClassifyArgumentTokens(command, args).ToArray();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].IsOption)
                continue;
            var arg = tokens[i].Value;
            if (string.Equals(arg, "--compact", StringComparison.Ordinal)
                || string.Equals(arg, "--format=compact", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(arg, "--format", StringComparison.Ordinal)
                && i + 1 < args.Length
                && string.Equals(args[i + 1], "compact", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsStaticStatusExplainRequest(string command, string[] args)
        => string.Equals(command, "status", StringComparison.Ordinal)
           && HasArgument(command, args, "--explain");

    private static int RunBoundedResponse(
        string command,
        string[] args,
        string appVersion,
        JsonSerializerOptions jsonOptions,
        Func<string[], int> runInner)
    {
        if (TryReadRequestedMaxJsonBytes(command, args, out var requestedBytes)
            && requestedBytes <= 0)
        {
            return CommandErrorWriter.WriteResponseBudgetError(
                json: true,
                jsonOptions,
                command,
                "--max-json-bytes requires a positive integer.",
                "Use a positive --max-json-bytes value; retry with at least 1 byte to begin response sizing.",
                requestedBytes,
                effectiveBytes: null,
                minimumRequiredBytes: null,
                minimumRequiredBytesUnavailableReason:
                    CommandErrorWriter.MinimumResponseBytesUnavailableBeforeMaterialization,
                usage: GetBoundedResponseUsage(command));
        }

        if (!TryParseBoundedResponseControls(command, args, out var controls, out var controlError))
        {
            return WriteBoundedResponseUsageError(controlError!, "Use the command help to pass positive --limit/--max-json-bytes values and a next_cursor returned by the same query.");
        }
        if (HasUnsupportedStandaloneBoundedControl(command, args)
            || command == "unused" && HasArgument(command, args, "--summary-only"))
        {
            return RunStandaloneValidationWithinBudget(
                command,
                args,
                controls.MaxJsonBytes!.Value,
                jsonOptions,
                runInner);
        }
        if (ProjectionFieldRegistry.IsDiscoveryRequest(controls.Fields))
        {
            var discoveryJson = ProjectionFieldRegistry.CreateDiscoveryDocument(command).ToJsonString(jsonOptions);
            return WriteProjectionRegistryResponse(
                command,
                discoveryJson,
                CommandExitCodes.Success,
                controls.MaxJsonBytes,
                jsonOptions);
        }
        if (!ProjectionFieldRegistry.TryValidate(command, controls.Fields, out var fieldError))
        {
            var errorJson = JsonSerializer.Serialize(
                new CommandErrorJsonResult(
                    "error",
                    fieldError!.Message,
                    fieldError.Hint,
                    CommandErrorCodes.UsageError,
                    Category: "usage"),
                CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult);
            return WriteProjectionRegistryResponse(
                command,
                errorJson,
                CommandExitCodes.UsageError,
                controls.MaxJsonBytes,
                jsonOptions);
        }
        var bodyProjected = HasExplicitBodyProjection(controls.Fields);
        var bodyOutputHidden = !bodyProjected
                               && controls.Compact
                               && controls.Fields is not { Count: > 0 };
        var bodyIntentValidationArgs = PrepareBoundedGraphBodyIntentValidationArgs(command, args);
        if (!QueryCommandRunner.TryValidateBoundedGraphSnippetLinesOption(command, bodyIntentValidationArgs, bodyOutputHidden))
            return CommandExitCodes.UsageError;
        if (HasArgument(command, args, "--count")
            || command == "find" && IsFindCountResponseRequest(args))
            return WriteBoundedResponseUsageError("Bounded response controls cannot be combined with --count.", "Run --count --json separately for a count-only response, or remove --count to page projected rows.");
        if (command == "map" && ValidateMapProjectionControls(args, controls.Fields) is { } mapProjectionError)
            return WriteBoundedResponseUsageError(mapProjectionError, "Remove the conflicting map filter, or select a collection enabled by --sections.");

        var queryNormalized = ExtractQueryArg(command, args);
        var (resolvedDbPath, dbPathExplicit) = ResolveQueryDbPath(command, args);
        var queryFingerprint = BuildResponseFingerprint(command, args);
        var suppressRuntimeMetadata = IsStaticStatusExplainRequest(command, args);
        var snapshot = suppressRuntimeMetadata
            ? BuildFallbackResponseSnapshot(appVersion)
            : SafeReadResponseSnapshot(resolvedDbPath, dbPathExplicit, appVersion);
        if (controls.CursorQueryFingerprint is not null
            && !string.Equals(controls.CursorQueryFingerprint, queryFingerprint, StringComparison.Ordinal))
        {
            return WriteBoundedResponseUsageError(
                "cursor_mismatch: --cursor does not match this command, query, or filter set.",
                "Use next_cursor from the preceding page without changing query, filter, or sort arguments.");
        }
        if (controls.CursorGenerationFingerprint is not null
            && !string.Equals(controls.CursorGenerationFingerprint, snapshot.GenerationFingerprint, StringComparison.Ordinal))
        {
            return WriteBoundedResponseUsageError(
                "cursor_stale: --cursor is stale because the index generation changed.",
                "Restart pagination without --cursor and use the next_cursor returned by the refreshed index.");
        }
        if (controls.Offset > MaxPageWindow - controls.PageLimit)
        {
            return WriteBoundedResponseUsageError(
                $"The requested cursor window exceeds the {MaxPageWindow} row safety cap.",
                "Narrow the query or filters before continuing pagination.");
        }

        var innerArgs = PrepareBoundedInnerArgs(command, args, controls);
        using var captured = new BoundedStringWriter(MaxCapturedOutputChars);
        using var capturedError = new BoundedStringWriter(MaxCapturedOutputChars);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int exitCode;
        JsonEnvelopeCaptureLimitExceededException? captureLimitExceeded = null;
        BoundedExecutionContext? executionContext = null;
        try
        {
            using var outputScope = ScopedConsoleOutput.Redirect(captured);
            using var errorScope = ScopedConsoleError.Redirect(capturedError);
            executionContext = new BoundedExecutionContext(
                command,
                controls.Offset,
                controls.PageLimit,
                controls.Fields,
                controls.Compact,
                controls.ResumePath,
                controls.ResumeLine,
                controls.ResumeFileOrdinal,
                controls.ResumeMatchOrdinal,
                controls.ResumeByteOffset,
                controls.PartialFamilyId,
                controls.FamilyMemberOffset);
            using var executionScope = EnterBoundedExecution(executionContext);
            exitCode = runInner(innerArgs);
        }
        catch (JsonEnvelopeCaptureLimitExceededException ex)
        {
            captureLimitExceeded = ex;
            exitCode = CommandExitCodes.InvalidArgument;
        }
        finally
        {
            stopwatch.Stop();
        }

        if (captureLimitExceeded is not null)
        {
            var message = $"Bounded response captured output exceeded {captureLimitExceeded.MaxChars} characters.";
            return WriteBoundedCaptureError(
                command,
                queryNormalized,
                resolvedDbPath,
                dbPathExplicit,
                appVersion,
                stopwatch.Elapsed.TotalMilliseconds,
                jsonOptions,
                exitCode,
                message,
                "Reduce --limit, choose fewer --fields, or use a narrower query.",
                controls.MaxJsonBytes,
                suppressRuntimeMetadata);
        }

        JsonArray rawResults;
        JsonObject? streamTerminal;
        JsonArray streamControlRecords;
        try
        {
            rawResults = ParseRawJsonItems(command, captured.ToString(), out streamTerminal, out streamControlRecords);
        }
        catch (JsonEnvelopeRawJsonItemLimitExceededException ex)
        {
            return WriteBoundedParseError(command, queryNormalized, resolvedDbPath, dbPathExplicit, appVersion, stopwatch.Elapsed.TotalMilliseconds, jsonOptions, $"Bounded response raw JSON item line exceeded {ex.MaxChars} characters.", "Reduce --limit or exclude large detail fields.", "max_chars", ex.MaxChars, controls.MaxJsonBytes, suppressRuntimeMetadata);
        }
        catch (JsonEnvelopeRawJsonBudgetExceededException ex)
        {
            return WriteBoundedParseError(command, queryNormalized, resolvedDbPath, dbPathExplicit, appVersion, stopwatch.Elapsed.TotalMilliseconds, jsonOptions, $"Bounded response raw JSON {ex.BudgetName} exceeded {ex.MaxValue}.", "Reduce --limit or narrow the query.", ex.JsonPropertyName, ex.MaxValue, controls.MaxJsonBytes, suppressRuntimeMetadata);
        }

        var capturedErrorText = capturedError.ToString();
        var commandError = TakeCommandError(rawResults, exitCode);
        if (commandError is null
            && exitCode != CommandExitCodes.Success
            && !string.IsNullOrWhiteSpace(capturedErrorText))
        {
            commandError = BuildCapturedCommandError(command, capturedErrorText, exitCode);
        }
        PromoteEmptyLegacyCompactPayload(command, controls, rawResults, streamControlRecords);
        var extraction = ExtractResponseItems(command, rawResults, controls);
        var availableItems = extraction.Items;
        var effectiveFields = controls.EffectiveFields(
            command,
            extraction.PrimaryCollection,
            suppressRuntimeMetadata,
            groupedSymbolsRequest: command == "symbols" && HasArgument(command, args, "--group-partials"));
        var pageItems = availableItems
            .Take(controls.PageLimit)
            .Select(item => ProjectResponseItem(
                item,
                effectiveFields,
                command,
                extraction.PrimaryCollection))
            .ToList();
        var statusExplainOmittedOptionalFields = BuildStatusExplainOmittedOptionalFields(
            suppressRuntimeMetadata,
            controls.Fields,
            availableItems,
            pageItems);

        var count = exitCode == CommandExitCodes.UsageError
            ? new ResponseCount(controls.Offset + pageItems.Count, false)
            : executionContext?.ReportedTotalCount is { } reportedTotalCount
            ? new ResponseCount(
                reportedTotalCount,
                executionContext.ReportedTotalCountAuthoritative)
            : ResolveTotalCount(command, args, runInner, extraction, availableItems.Count, controls.Offset, streamTerminal);
        if (count.Context is not null)
            extraction = extraction with { Context = MergeResponseContexts(extraction.Context, count.Context) };
        var totalCount = Math.Max(count.TotalCount, controls.Offset + pageItems.Count);
        var totalAuthoritative = count.Authoritative;
        var completedSnapshot = suppressRuntimeMetadata
            ? snapshot
            : SafeReadResponseSnapshot(resolvedDbPath, dbPathExplicit, appVersion);
        if (!string.Equals(snapshot.GenerationFingerprint, completedSnapshot.GenerationFingerprint, StringComparison.Ordinal))
        {
            return WriteBoundedResponseUsageError(
                "The index generation changed while this page was being read.",
                "Restart pagination without --cursor after the active index refresh completes.");
        }
        ResponseSnapshotValidatedForTesting?.Invoke();
        var envelope = BuildBoundedEnvelopeWithinBudget(
            command,
            queryNormalized,
            resolvedDbPath,
            dbPathExplicit,
            appVersion,
            stopwatch.Elapsed.TotalMilliseconds,
            pageItems,
            controls,
            queryFingerprint,
            snapshot,
            totalCount,
            totalAuthoritative,
            exitCode,
            extraction,
            commandError,
            streamTerminal,
            streamControlRecords,
            suppressRuntimeMetadata,
            statusExplainOmittedOptionalFields,
            jsonOptions,
            out var emittedJson,
            out var emittedCount,
            out var minimumRequiredBytes);

        if (envelope is null)
        {
            var selectionHint = command switch
            {
                "outline" => "choose fewer --outline-fields",
                "unused" => "add --compact",
                _ => "choose fewer --fields",
            };
            var minimumDescription = commandError is not null
                ? "the complete bounded error envelope"
                : pageItems.Count > 0
                    ? "bounded response metadata and one projected row"
                    : "the complete empty bounded response envelope";
            return CommandErrorWriter.WriteResponseBudgetError(
                json: true,
                jsonOptions,
                command,
                $"--max-json-bytes {controls.MaxJsonBytes} is too small for {minimumDescription}.",
                $"Increase --max-json-bytes or {selectionHint}.",
                requestedBytes: controls.MaxJsonBytes,
                effectiveBytes: controls.MaxJsonBytes,
                minimumRequiredBytes: minimumRequiredBytes,
                minimumRequiredBytesUncertaintyReason:
                    CommandErrorWriter.MinimumResponseBytesUncertainRuntimeEnvelope,
                recommendedBytes: minimumRequiredBytes + ResponseBudgetRetryHeadroomBytes,
                usage: GetBoundedResponseUsage(command));
        }

        if (exitCode == CommandExitCodes.Success && capturedErrorText.Length > 0)
            Console.Error.Write(capturedErrorText);
        Console.WriteLine(emittedJson);
        return exitCode;
    }

    private static int RunStandaloneValidationWithinBudget(
        string command,
        string[] args,
        int maxJsonBytes,
        JsonSerializerOptions jsonOptions,
        Func<string[], int> runInner)
    {
        using var captured = new BoundedStringWriter(MaxCapturedOutputChars);
        int exitCode;
        using (ScopedConsoleOutput.Redirect(captured))
            exitCode = runInner(args);

        var output = captured.ToString();
        if (output.Length == 0)
            return exitCode;
        if (Encoding.UTF8.GetByteCount(output) <= maxJsonBytes)
        {
            Console.Write(output);
            return exitCode;
        }

        string? message = null;
        string? hint = null;
        try
        {
            var error = JsonNode.Parse(output) as JsonObject;
            message = ReadString(error, "message");
            hint = ReadString(error, "hint");
        }
        catch (JsonException)
        {
            // Fall back to a bounded generic diagnostic if validation emitted malformed JSON.
        }

        return CommandErrorWriter.WriteResponseBudgetError(
            json: true,
            jsonOptions,
            command,
            message ?? $"{command} output-selector validation failed or its summary document exceeds the byte budget.",
            $"{hint ?? $"Use only options shown in `{command} --help`."} Increase --max-json-bytes to receive the structured response.",
                requestedBytes: maxJsonBytes,
                effectiveBytes: maxJsonBytes,
                minimumRequiredBytes: Encoding.UTF8.GetByteCount(output),
                minimumRequiredBytesUncertaintyReason:
                    CommandErrorWriter.MinimumResponseBytesUncertainCapturedValidation,
                recommendedBytes: (long)Encoding.UTF8.GetByteCount(output) + ResponseBudgetRetryHeadroomBytes,
                usage: GetBoundedResponseUsage(command));
    }

    private static JsonObject? BuildBoundedEnvelopeWithinBudget(
        string command,
        string? queryNormalized,
        string dbPath,
        bool dbPathExplicit,
        string appVersion,
        double elapsedMs,
        IReadOnlyList<JsonNode?> pageItems,
        BoundedResponseControls controls,
        string queryFingerprint,
        ResponseSnapshot snapshot,
        int totalCount,
        bool totalAuthoritative,
        int exitCode,
        ResponseExtraction extraction,
        JsonObject? commandError,
        JsonObject? streamTerminal,
        JsonArray streamControlRecords,
        bool suppressRuntimeMetadata,
        IReadOnlyList<string>? statusExplainOmittedOptionalFields,
        JsonSerializerOptions jsonOptions,
        out string emittedJson,
        out int emittedCount,
        out long minimumRequiredBytes)
    {
        emittedJson = string.Empty;
        emittedCount = 0;
        JsonObject BuildCandidate(
            int count,
            IReadOnlyList<JsonNode?>? candidateItems = null,
            int byteLimitOmittedPathCount = 0)
        {
            var sourceItems = candidateItems ?? pageItems;
            var results = new JsonArray();
            for (var i = 0; i < count; i++)
                results.Add(sourceItems[i]?.DeepClone());
            var adjustedStreamTerminal = AdjustSearchSelectionAccountingForBoundedRows(
                command,
                streamTerminal,
                count);
            var envelope = BuildEnvelope(
                command,
                queryNormalized,
                dbPath,
                dbPathExplicit,
                appVersion,
                elapsedMs,
                results,
                exitCode,
                error: commandError is null ? null : (JsonObject)commandError.DeepClone(),
                streamTerminal: adjustedStreamTerminal,
                streamControlRecords: streamControlRecords,
                responseSnapshot: snapshot,
                suppressRuntimeMetadata: suppressRuntimeMetadata);
            var metadata = (JsonObject)envelope["metadata"]!;
            if (!suppressRuntimeMetadata)
                metadata["result_stable_at"] = snapshot.ResultStableAt;
            if (commandError is not null)
            {
                metadata["returned_count"] = 0;
                metadata["total_count"] = 0;
                metadata["total_count_authoritative"] = true;
                metadata["omitted_count"] = 0;
                if (controls.Fields is { Count: > 0 })
                {
                    var errorFields = new JsonArray();
                    foreach (var field in controls.Fields)
                        errorFields.Add(field);
                    metadata["fields"] = errorFields;
                }
                if (controls.MaxJsonBytes.HasValue)
                    metadata["max_json_bytes"] = controls.MaxJsonBytes.Value;
                return envelope;
            }
            var nextOffset = controls.Offset + count;
            var paginationWindowExhausted = nextOffset < totalCount && nextOffset >= MaxPageWindow;
            var scanCursor = ReadString(streamTerminal, "next_cursor");
            var emittedAllCapturedRows = count == pageItems.Count;
            var selectedScanCursor = emittedAllCapturedRows ? scanCursor : null;
            var findScanTerminalAuthoritative = command == "find"
                                                && streamTerminal?["scan_complete"] is JsonValue;
            var capturedRowsRemain = !emittedAllCapturedRows && count > 0;
            var hasMore = selectedScanCursor is not null
                          || capturedRowsRemain
                          || !findScanTerminalAuthoritative
                          && count > 0
                          && nextOffset < totalCount
                          && !paginationWindowExhausted;
            metadata["result_count"] = count;
            metadata["returned_count"] = count;
            metadata["total_count"] = totalCount;
            metadata["total_count_authoritative"] = totalAuthoritative;
            metadata["omitted_count"] = Math.Max(0, totalCount - count);
            metadata["remaining_count"] = Math.Max(0, totalCount - nextOffset);
            metadata["cursor_offset"] = controls.Offset;
            metadata["page_limit"] = controls.PageLimit;
            metadata["has_more"] = hasMore;
            metadata["next_cursor"] = selectedScanCursor
                ?? (hasMore && count > 0
                    ? FormatResponseCursor(
                        nextOffset,
                        queryFingerprint,
                        snapshot.GenerationFingerprint,
                        controls.ResumePath,
                        controls.ResumeLine,
                        controls.ResumeFileOrdinal,
                        controls.ResumeMatchOrdinal,
                        controls.ResumeByteOffset)
                    : null);
            metadata["truncated"] = scanCursor is not null || totalCount > count || byteLimitOmittedPathCount > 0;
            metadata["pagination_window_limit"] = MaxPageWindow;
            metadata["pagination_window_exhausted"] = paginationWindowExhausted;
            if (controls.Compact)
                metadata["format"] = "compact";
            if (controls.Fields is { Count: > 0 })
            {
                var fields = new JsonArray();
                foreach (var field in controls.Fields)
                    fields.Add(field);
                metadata["fields"] = fields;
            }
            if (controls.MaxJsonBytes.HasValue)
                metadata["max_json_bytes"] = controls.MaxJsonBytes.Value;
            if (statusExplainOmittedOptionalFields is not null)
            {
                metadata["explanation_schema"] = "compact";
                metadata["explanation_required_fields"] = new JsonArray(
                    ProjectionFieldRegistry.GetStatusExplainCompactFields()
                        .Select(field => (JsonNode?)field)
                        .ToArray());
                metadata["explanation_omitted_optional_field_count"] =
                    statusExplainOmittedOptionalFields.Count;
                metadata["explanation_omitted_optional_fields"] = new JsonArray(
                    statusExplainOmittedOptionalFields
                        .Select(field => (JsonNode?)field)
                        .ToArray());
            }
            if (pageItems.Count > count)
            {
                metadata["byte_limit_reached"] = true;
                metadata["byte_limit_omitted_count"] = pageItems.Count - count;
            }
            if (byteLimitOmittedPathCount > 0)
            {
                metadata["byte_limit_reached"] = true;
                metadata["byte_limit_omitted_path_count"] = byteLimitOmittedPathCount;
            }
            if (extraction.PrimaryCollection is not null)
                metadata["primary_collection"] = extraction.PrimaryCollection;
            if (extraction.Context is { Count: > 0 })
            {
                metadata["response_context"] = command == "unused"
                    ? BuildBoundedUnusedResponseContext(extraction.Context, results)
                    : extraction.Context.DeepClone();
            }
            if (command == "unused"
                && extraction.SourcePayload?["by_bucket"] is JsonObject)
            {
                envelope["by_bucket"] = BuildBoundedUnusedBuckets(results);
            }
            if (controls.Compact
                && LegacyLocationCompactCommands.Contains(command)
                && extraction.SourcePayload is not null)
            {
                return BuildBackwardCompatibleCompactEnvelope(
                    command,
                    extraction.SourcePayload,
                    envelope,
                    results,
                    controls,
                    count,
                    totalCount,
                    totalAuthoritative,
                    nextOffset,
                    hasMore,
                    paginationWindowExhausted,
                    queryFingerprint,
                    snapshot,
                    extraction.PrimaryCollection);
            }
            if (controls.Compact
                && command == "map"
                && controls.Fields is null
                && extraction.SourcePayload is not null)
            {
                return BuildBackwardCompatibleMapCompactEnvelope(extraction.SourcePayload, envelope);
            }
            return envelope;
        }

        var requestedCount = pageItems.Count;
        var minimumCandidate = BuildCandidate(requestedCount > 0 ? 1 : 0);
        var minimumJson = SerializeBoundedEnvelope(minimumCandidate, jsonOptions);
        minimumRequiredBytes = GetJsonResponseByteCount(minimumJson);
        var candidate = BuildCandidate(requestedCount);
        var candidateJson = SerializeBoundedEnvelope(candidate, jsonOptions);
        if (!controls.MaxJsonBytes.HasValue || JsonFitsResponseBudget(candidateJson, controls.MaxJsonBytes.Value))
        {
            emittedJson = candidateJson;
            emittedCount = requestedCount;
            return candidate;
        }

        if (command == "status"
            && requestedCount == 1
            && TryGetStatusWorkspaceCheckMaxSampleCount(pageItems[0], out var maxSampleCount))
        {
            JsonObject? bestStatusCandidate = null;
            string? bestStatusJson = null;
            var lowStatusLimit = 0;
            var highStatusLimit = Math.Max(0, maxSampleCount - 1);
            while (lowStatusLimit <= highStatusLimit)
            {
                var sampleLimit = lowStatusLimit + ((highStatusLimit - lowStatusLimit) / 2);
                TryTrimStatusWorkspaceCheckSamples(
                    pageItems[0],
                    sampleLimit,
                    out var trimmedStatus,
                    out var omittedPathCount);
                var current = BuildCandidate(1, [trimmedStatus], omittedPathCount);
                var currentJson = SerializeBoundedEnvelope(current, jsonOptions);
                minimumRequiredBytes = Math.Min(minimumRequiredBytes, GetJsonResponseByteCount(currentJson));
                if (JsonFitsResponseBudget(currentJson, controls.MaxJsonBytes.Value))
                {
                    bestStatusCandidate = current;
                    bestStatusJson = currentJson;
                    emittedCount = 1;
                    lowStatusLimit = sampleLimit + 1;
                }
                else
                {
                    highStatusLimit = sampleLimit - 1;
                }
            }

            if (bestStatusCandidate is not null)
            {
                emittedJson = bestStatusJson!;
                return bestStatusCandidate;
            }
        }

        JsonObject? best = null;
        string? bestJson = null;
        var low = 0;
        var high = requestedCount;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var current = BuildCandidate(mid);
            var currentJson = SerializeBoundedEnvelope(current, jsonOptions);
            if (JsonFitsResponseBudget(currentJson, controls.MaxJsonBytes.Value))
            {
                best = current;
                bestJson = currentJson;
                emittedCount = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (best is null || (requestedCount > 0 && emittedCount == 0))
            return null;
        emittedJson = bestJson!;
        return best;
    }

    private static IReadOnlyList<string>? BuildStatusExplainOmittedOptionalFields(
        bool statusExplainRequest,
        IReadOnlyList<string>? requestedFields,
        JsonArray sourceItems,
        IReadOnlyList<JsonNode?> projectedItems)
    {
        if (!statusExplainRequest
            || requestedFields is not null
            || sourceItems.Count != 1
            || projectedItems.Count != 1
            || sourceItems[0] is not JsonObject source
            || projectedItems[0] is not JsonObject projected)
        {
            return null;
        }

        return source
            .Select(property => property.Key)
            .Where(field => !projected.ContainsKey(field))
            .ToArray();
    }

    private static string SerializeBoundedEnvelope(JsonNode node, JsonSerializerOptions jsonOptions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = jsonOptions.Encoder,
            Indented = jsonOptions.WriteIndented,
        }))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static bool JsonFitsResponseBudget(string json, int maxJsonBytes)
        => GetJsonResponseByteCount(json) <= maxJsonBytes;

    private static long GetJsonResponseByteCount(string json)
        => (long)Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Environment.NewLine);

    private static int WriteProjectionRegistryResponse(
        string command,
        string json,
        int exitCode,
        int? maxJsonBytes,
        JsonSerializerOptions jsonOptions)
    {
        if (maxJsonBytes.HasValue && !JsonFitsResponseBudget(json, maxJsonBytes.Value))
        {
            return CommandErrorWriter.WriteResponseBudgetError(
                json: true,
                jsonOptions,
                command,
                $"--max-json-bytes {maxJsonBytes.Value} is too small for the projection-field response.",
                "Increase --max-json-bytes and rerun the same --fields request.",
                requestedBytes: maxJsonBytes.Value,
                effectiveBytes: maxJsonBytes.Value,
                minimumRequiredBytes: GetJsonResponseByteCount(json),
                usage: GetBoundedResponseUsage(command));
        }

        Console.WriteLine(json);
        return exitCode;
    }

    private static JsonObject? TakeCommandError(JsonArray rawResults, int exitCode)
    {
        if (exitCode == CommandExitCodes.Success
            || rawResults.Count != 1
            || rawResults[0] is not JsonObject candidate
            || candidate["status"] is not JsonValue status
            || !status.TryGetValue<string>(out var statusText)
            || !string.Equals(statusText, "error", StringComparison.Ordinal)
            || candidate["error_code"] is null)
        {
            return null;
        }

        var error = (JsonObject)candidate.DeepClone();
        error.Remove("status");
        error.Remove("api_version");
        rawResults.Clear();
        return error;
    }

    private static JsonObject BuildCapturedCommandError(
        string command,
        string stderr,
        int exitCode)
    {
        var lines = stderr
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var firstLine = lines.FirstOrDefault() ?? "Command validation failed.";
        var (errorCode, category) = CommandErrorWriter.ResolveMachineContract(exitCode);
        string message;
        if (firstLine.StartsWith("Error [", StringComparison.Ordinal)
            && firstLine.IndexOf("]: ", StringComparison.Ordinal) is var separator
            && separator > "Error [".Length)
        {
            errorCode = firstLine["Error [".Length..separator];
            message = firstLine[(separator + 3)..];
        }
        else if (firstLine.StartsWith("Error: ", StringComparison.Ordinal))
        {
            message = firstLine["Error: ".Length..];
        }
        else
        {
            message = firstLine;
        }

        var hint = lines
            .FirstOrDefault(line => line.StartsWith("Hint: ", StringComparison.Ordinal));
        return new JsonObject
        {
            ["message"] = message,
            ["hint"] = hint is null ? null : hint["Hint: ".Length..],
            ["error_code"] = errorCode,
            ["category"] = category,
            ["command"] = command,
            ["usage"] = GetBoundedResponseUsage(command),
        };
    }

    private static void PromoteEmptyLegacyCompactPayload(
        string command,
        BoundedResponseControls controls,
        JsonArray rawResults,
        JsonArray streamControlRecords)
    {
        if (!controls.Compact || !LegacyLocationCompactCommands.Contains(command) || rawResults.Count > 0)
            return;
        for (var index = streamControlRecords.Count - 1; index >= 0; index--)
        {
            if (streamControlRecords[index] is not JsonObject candidate
                || candidate["results"] is not JsonArray
                || candidate["format"]?.GetValue<string>() != "compact")
                continue;
            rawResults.Add(candidate.DeepClone());
            streamControlRecords.RemoveAt(index);
            return;
        }
    }

    private static JsonObject BuildBackwardCompatibleCompactEnvelope(
        string command,
        JsonObject sourcePayload,
        JsonObject envelope,
        JsonArray results,
        BoundedResponseControls controls,
        int returnedCount,
        int totalCount,
        bool totalAuthoritative,
        int nextOffset,
        bool hasMore,
        bool paginationWindowExhausted,
        string queryFingerprint,
        ResponseSnapshot snapshot,
        string? primaryCollection)
    {
        var compatible = AdjustSearchSelectionAccountingForBoundedRows(
            command,
            sourcePayload,
            returnedCount) ?? (JsonObject)sourcePayload.DeepClone();
        var collectionName = primaryCollection ?? "results";
        compatible[collectionName] = results.DeepClone();
        compatible["metadata"] = envelope["metadata"]!.DeepClone();
        if (collectionName == "results")
            compatible["count"] = returnedCount;
        else
            compatible["emitted_count"] = returnedCount;
        compatible["returned_count"] = returnedCount;
        compatible["total_count"] = totalCount;
        compatible["total_count_authoritative"] = totalAuthoritative;
        compatible["omitted_count"] = Math.Max(0, totalCount - returnedCount);
        compatible["remaining_count"] = Math.Max(0, totalCount - nextOffset);
        compatible["cursor_offset"] = controls.Offset;
        compatible["page_limit"] = controls.PageLimit;
        compatible["has_more"] = hasMore;
        compatible["next_cursor"] = hasMore && returnedCount > 0
            ? FormatResponseCursor(nextOffset, queryFingerprint, snapshot.GenerationFingerprint)
            : null;
        compatible["result_stable_at"] = snapshot.ResultStableAt;
        compatible["truncated"] = totalCount > returnedCount;
        compatible["pagination_window_limit"] = MaxPageWindow;
        compatible["pagination_window_exhausted"] = paginationWindowExhausted;
        var truncation = compatible["truncation"] as JsonObject;
        if (truncation is null)
        {
            truncation = new JsonObject();
            compatible["truncation"] = truncation;
        }
        truncation["limit"] = controls.PageLimit;
        truncation["limit_reached"] = totalCount > returnedCount;
        if (compatible["query_context"] is JsonObject queryContext)
            queryContext["limit"] = controls.PageLimit;
        return compatible;
    }

    private static JsonObject? AdjustSearchSelectionAccountingForBoundedRows(
        string command,
        JsonObject? payload,
        int returnedCount)
    {
        if (payload is null
            || !string.Equals(command, "search", StringComparison.Ordinal)
            || payload["selectors"] is not JsonArray
            || !payload.ContainsKey("returned"))
        {
            return payload;
        }

        var adjusted = (JsonObject)payload.DeepClone();
        adjusted["returned"] = JsonNode.Parse(
            Math.Max(0, returnedCount).ToString(CultureInfo.InvariantCulture));
        return adjusted;
    }

    private static JsonObject BuildBackwardCompatibleMapCompactEnvelope(
        JsonObject sourcePayload,
        JsonObject envelope)
    {
        var compatible = (JsonObject)sourcePayload.DeepClone();
        compatible["metadata"] = envelope["metadata"]!.DeepClone();
        return compatible;
    }

    private static ResponseExtraction ExtractResponseItems(string command, JsonArray rawResults, BoundedResponseControls controls)
    {
        if (controls.Compact
            && LegacyLocationCompactCommands.Contains(command)
            && rawResults.FirstOrDefault() is JsonObject compactPayload
            && compactPayload["results"] is JsonArray compactResults)
        {
            return new ResponseExtraction(
                new JsonArray(compactResults.Select(item => item?.DeepClone()).ToArray()),
                "results",
                ExtractGraphIdentityResponseContext(command, compactPayload),
                compactPayload);
        }
        if (command == "hotspots" && rawResults.FirstOrDefault() is JsonObject hotspotsPayload)
            return ExtractNestedCollection(hotspotsPayload, "hotspots");
        if (command == "symbols"
            && rawResults.FirstOrDefault() is JsonObject symbolsPayload
            && (symbolsPayload["symbols"] is JsonArray || ReadOptionalBool(symbolsPayload, "summary_only")))
            return ExtractNestedCollection(symbolsPayload, "symbols");
        if (command == "files"
            && rawResults.FirstOrDefault() is JsonObject filesPayload
            && (filesPayload["files"] is JsonArray || ReadOptionalBool(filesPayload, "summary_only")))
            return ExtractNestedCollection(filesPayload, "files");
        if (command == "languages" && rawResults.FirstOrDefault() is JsonObject languagesPayload)
            return ExtractNestedCollection(languagesPayload, "languages");
        if (command == "outline" && rawResults.FirstOrDefault() is JsonObject outlinePayload)
            return ExtractOutlineSymbols(outlinePayload);
        if (command == "unused" && rawResults.FirstOrDefault() is JsonObject unusedPayload)
            return ExtractUnusedSymbols(unusedPayload);
        if (command is "references" or "callers" or "callees"
            && rawResults.FirstOrDefault() is JsonObject graphPayload)
        {
            var primaryCollection = command switch
            {
                "references" => "references",
                "callers" => "callers",
                _ => "callees",
            };
            if (graphPayload[primaryCollection] is JsonArray)
            {
                var extraction = ExtractNestedCollection(graphPayload, primaryCollection);
                var identityContext = ExtractGraphIdentityResponseContext(command, graphPayload);
                return identityContext is null
                    ? extraction
                    : extraction with { Context = MergeResponseContexts(extraction.Context, identityContext) };
            }
        }
        if (command == "impact" && rawResults.FirstOrDefault() is JsonObject impactPayload)
        {
            var requestedCollection = SelectRequestedCollection(controls.Fields, "callers", "file_impacts", "definitions");
            var primary = requestedCollection
                          ?? FirstPresentArray(impactPayload, "callers", "file_impacts", "definitions")
                          ?? "callers";
            return ExtractNestedCollection(impactPayload, primary);
        }
        if (command == "map" && rawResults.FirstOrDefault() is JsonObject mapPayload)
        {
            var requestedCollection = SelectRequestedCollection(
                controls.Fields,
                "languages",
                "modules",
                "top_files",
                "largest_files",
                "symbol_rich_files",
                "reference_rich_files",
                "entrypoints");
            if (requestedCollection is not null)
                return ExtractNestedCollection(mapPayload, requestedCollection);
            if (controls.Compact && controls.Fields is null)
            {
                return new ResponseExtraction(
                    new JsonArray(mapPayload.DeepClone()),
                    null,
                    null,
                    mapPayload);
            }
        }
        if (command is "symbols" or "files")
            return ExtractDiscoveryRows(command, rawResults);
        if (rawResults.Count == 1 && rawResults[0] is JsonArray arrayPayload)
        {
            return new ResponseExtraction(
                new JsonArray(arrayPayload.Select(item => item?.DeepClone()).ToArray()),
                "results",
                null,
                null);
        }

        var rows = new JsonArray();
        JsonObject? graphIdentityContext = null;
        foreach (var result in rawResults)
        {
            if (result is JsonObject obj && IsJsonStreamTerminal(obj))
                continue;
            rows.Add(result?.DeepClone());
            if (graphIdentityContext == null && result is JsonObject graphRow)
                graphIdentityContext = ExtractGraphIdentityResponseContext(command, graphRow);
        }
        return new ResponseExtraction(rows, null, graphIdentityContext, null);
    }

    private static JsonObject? ExtractGraphIdentityResponseContext(
        string command,
        JsonObject source)
    {
        if (command is not ("references" or "callers" or "callees" or "impact"))
            return null;

        var context = new JsonObject();
        foreach (var field in new[]
                 {
                     "identity_scoped",
                     "identity_scope_reason",
                     "selected_symbol",
                     "candidate_count",
                     "candidates",
                     "candidates_truncated",
                     "identity_warning",
                     "reference_graph_incomplete_reasons",
                 })
        {
            if (source.TryGetPropertyValue(field, out var value))
                context[field] = value?.DeepClone();
        }
        return context.Count > 0 ? context : null;
    }

    private static ResponseExtraction ExtractOutlineSymbols(JsonObject payload)
    {
        var extraction = ExtractNestedCollection(payload, "symbols");
        foreach (var pagingField in new[]
                 {
                     "returned_symbol_count",
                     "cursor_offset",
                     "next_cursor",
                     "has_more",
                     "result_stable_at",
                 })
        {
            extraction.Context?.Remove(pagingField);
        }
        return extraction;
    }

    private static ResponseExtraction ExtractUnusedSymbols(JsonObject payload)
    {
        var items = payload["symbols"] as JsonArray ?? [];
        var context = new JsonObject();
        foreach (var property in payload)
        {
            if (property.Key is "symbols" or "by_bucket" or "next_cursor" or "result_stable_at"
                || property.Value is JsonArray)
            {
                continue;
            }
            context[property.Key] = property.Value?.DeepClone();
        }
        return new ResponseExtraction(
            new JsonArray(items.Select(item => item?.DeepClone()).ToArray()),
            "symbols",
            context,
            payload);
    }

    private static JsonObject BuildBoundedUnusedBuckets(JsonArray results)
    {
        var byBucket = new JsonObject();
        foreach (var bucket in QueryCommandRunner.OrderedUnusedBuckets)
            byBucket[bucket] = new JsonArray();
        foreach (var result in results.OfType<JsonObject>())
        {
            var bucket = ReadString(result, "unused_bucket");
            if (bucket is null || byBucket[bucket] is not JsonArray rows)
                continue;
            rows.Add(result.DeepClone());
        }
        return byBucket;
    }

    private static JsonObject BuildBoundedUnusedResponseContext(JsonObject source, JsonArray results)
    {
        var context = (JsonObject)source.DeepClone();
        context["count"] = results.Count;
        context["returned_bucket_counts"] = CountResponseRowsByProperty(
            results,
            "unused_bucket",
            QueryCommandRunner.OrderedUnusedBuckets);
        context["returned_contract_domain_counts"] = CountResponseRowsByProperty(
            results,
            "unused_contract_domain");
        if (context["summary"] is JsonObject summary)
        {
            summary["by_bucket"] = CountResponseRowsByProperty(
                results,
                "unused_bucket",
                QueryCommandRunner.OrderedUnusedBuckets);
            summary["by_confidence"] = CountResponseRowsByProperty(
                results,
                "unused_confidence");
            summary["by_contract_domain"] = CountResponseRowsByProperty(
                results,
                "unused_contract_domain");
        }
        return context;
    }

    private static JsonObject CountResponseRowsByProperty(
        JsonArray results,
        string propertyName,
        IReadOnlyList<string>? preferredOrder = null)
    {
        var counts = results
            .OfType<JsonObject>()
            .Select(row => ReadString(row, propertyName))
            .Where(value => !string.IsNullOrEmpty(value))
            .GroupBy(value => value!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var output = new JsonObject();
        if (preferredOrder is not null)
        {
            foreach (var value in preferredOrder)
            {
                if (counts.Remove(value, out var count))
                    output[value] = count;
            }
        }
        foreach (var entry in counts.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            output[entry.Key] = entry.Value;
        return output;
    }

    private static ResponseExtraction ExtractDiscoveryRows(string command, JsonArray rawResults)
    {
        var rows = new JsonArray();
        JsonObject? context = null;
        foreach (var result in rawResults)
        {
            if (result is JsonObject obj && IsJsonStreamTerminal(obj))
                continue;
            rows.Add(result?.DeepClone());
            if (command != "symbols" || result is not JsonObject row)
                continue;

            if (row.TryGetPropertyValue("exact_index_available", out var exactIndexAvailable))
            {
                context ??= new JsonObject();
                context["exact_index_available"] = exactIndexAvailable?.DeepClone();
            }
            if (row.TryGetPropertyValue("degraded_reason", out var degradedReason))
            {
                context ??= new JsonObject();
                context["degraded_reason"] = degradedReason?.DeepClone();
            }
        }
        return new ResponseExtraction(rows, command, context, null);
    }

    private static JsonObject MergeResponseContexts(JsonObject? existing, JsonObject additional)
    {
        var merged = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
        foreach (var property in additional)
            merged[property.Key] = property.Value?.DeepClone();
        return merged;
    }

    private static ResponseExtraction ExtractNestedCollection(JsonObject payload, string collectionName)
    {
        var items = payload[collectionName] as JsonArray ?? [];
        var clonedItems = new JsonArray(items.Select(item => item?.DeepClone()).ToArray());
        var context = new JsonObject();
        foreach (var property in payload)
        {
            if (property.Value is JsonArray)
                continue;
            context[property.Key] = property.Value?.DeepClone();
        }
        return new ResponseExtraction(clonedItems, collectionName, context, payload);
    }

    private static string? SelectRequestedCollection(IReadOnlyList<string>? fields, params string[] collections)
    {
        if (fields is null)
            return null;
        foreach (var collection in collections)
        {
            if (fields.Any(field => string.Equals(field, collection, StringComparison.Ordinal)
                                    || field.StartsWith(collection + ".", StringComparison.Ordinal)))
                return collection;
        }
        return null;
    }

    private static string? FirstPresentArray(JsonObject payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (payload[name] is JsonArray { Count: > 0 })
                return name;
        }
        return names.FirstOrDefault(name => payload[name] is JsonArray);
    }

    private static JsonNode? ProjectResponseItem(
        JsonNode? item,
        IReadOnlyList<string>? fields,
        string command,
        string? primaryCollection)
    {
        if (item is not JsonObject obj || fields is null || fields.Count == 0 || fields.Contains("all", StringComparer.Ordinal))
            return item?.DeepClone();
        var projected = new JsonObject();
        foreach (var field in fields)
        {
            if (obj.TryGetPropertyValue(field, out var value))
                projected[field] = value?.DeepClone();
            else if (ProjectionFieldRegistry.TryResolveAlias(command, primaryCollection, field, out var sourceField)
                     && obj.TryGetPropertyValue(sourceField, out var aliasValue))
                projected[string.Equals(field, "body", StringComparison.Ordinal) ? sourceField : field] =
                    aliasValue?.DeepClone();
            else if (string.Equals(field, "path", StringComparison.Ordinal)
                     && obj.TryGetPropertyValue("file", out var file))
                projected[field] = file?.DeepClone();
            else if (string.Equals(field, "file", StringComparison.Ordinal)
                     && obj.TryGetPropertyValue("path", out var path))
                projected[field] = path?.DeepClone();
            else if (TryProjectNestedResponseField(obj, projected, field))
                AddStatusWorkspaceCheckProjectionSignals(obj, projected, command, field);
        }
        return projected;
    }

    private static bool TryProjectNestedResponseField(
        JsonObject source,
        JsonObject projected,
        string field)
    {
        var segments = field.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        JsonObject currentSource = source;
        JsonObject currentProjected = projected;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (currentSource[segments[i]] is not JsonObject nextSource)
                return false;
            currentSource = nextSource;
            if (currentProjected[segments[i]] is not JsonObject nextProjected)
            {
                nextProjected = new JsonObject();
                currentProjected[segments[i]] = nextProjected;
            }
            currentProjected = nextProjected;
        }

        var leaf = segments[^1];
        if (!currentSource.TryGetPropertyValue(leaf, out var value))
            return false;
        currentProjected[leaf] = value?.DeepClone();
        return true;
    }

    private static void AddStatusWorkspaceCheckProjectionSignals(
        JsonObject source,
        JsonObject projected,
        string command,
        string field)
    {
        if (!string.Equals(command, "status", StringComparison.Ordinal)
            || source["workspace_check"] is not JsonObject sourceCheck
            || projected["workspace_check"] is not JsonObject projectedCheck)
        {
            return;
        }

        var descriptor = WorkspaceCheckPathSamples.Descriptors.FirstOrDefault(candidate =>
            string.Equals(
                field,
                $"workspace_check.{candidate.ListPropertyName}",
                StringComparison.Ordinal));
        if (string.IsNullOrEmpty(descriptor.ListPropertyName))
            return;

        foreach (var signal in new[]
                 {
                     descriptor.CountPropertyName,
                     descriptor.TruncatedPropertyName,
                     descriptor.PathLimitPropertyName,
                     descriptor.OmittedCountPropertyName,
                 })
        {
            if (sourceCheck.TryGetPropertyValue(signal, out var value))
                projectedCheck[signal] = value?.DeepClone();
        }
    }

    private static bool TryGetStatusWorkspaceCheckMaxSampleCount(JsonNode? source, out int maxSampleCount)
    {
        maxSampleCount = 0;
        if (source is not JsonObject status
            || status["workspace_check"] is not JsonObject check)
        {
            return false;
        }

        foreach (var descriptor in WorkspaceCheckPathSamples.Descriptors)
        {
            if (check[descriptor.ListPropertyName] is JsonArray samples)
                maxSampleCount = Math.Max(maxSampleCount, samples.Count);
        }
        return maxSampleCount > 0;
    }

    private static bool TryTrimStatusWorkspaceCheckSamples(
        JsonNode? source,
        int sampleLimit,
        out JsonNode? trimmed,
        out int omittedPathCount)
    {
        trimmed = source?.DeepClone();
        omittedPathCount = 0;
        if (trimmed is not JsonObject status
            || status["workspace_check"] is not JsonObject check)
        {
            return false;
        }

        foreach (var descriptor in WorkspaceCheckPathSamples.Descriptors)
        {
            if (check[descriptor.ListPropertyName] is not JsonArray samples)
                continue;

            var originalSampleCount = samples.Count;
            while (samples.Count > sampleLimit)
                samples.RemoveAt(samples.Count - 1);
            omittedPathCount += originalSampleCount - samples.Count;

            var authoritativeCount = TryReadInt(check, descriptor.CountPropertyName, out var count)
                ? count
                : originalSampleCount;
            var totalOmittedCount = Math.Max(0, authoritativeCount - samples.Count);
            check[descriptor.TruncatedPropertyName] = totalOmittedCount > 0;
            check[descriptor.PathLimitPropertyName] = WorkspaceCheckPathSamples.PathLimit;
            check[descriptor.OmittedCountPropertyName] = totalOmittedCount;
        }
        return omittedPathCount > 0;
    }

    private static ResponseCount ResolveTotalCount(
        string command,
        string[] args,
        Func<string[], int> runInner,
        ResponseExtraction extraction,
        int availableCount,
        int offset,
        JsonObject? streamTerminal)
    {
        if (command == "status")
            return new ResponseCount(availableCount, true);
        if (command == "map")
        {
            var totalProperty = extraction.PrimaryCollection switch
            {
                "languages" => "language_count",
                "modules" => "module_count",
                "entrypoints" => "entrypoint_count",
                "top_files" or "largest_files" or "symbol_rich_files" or "reference_rich_files" => "file_count",
                _ => null,
            };
            if (totalProperty is not null && TryReadInt(extraction.SourcePayload, totalProperty, out var sectionTotal))
                return new ResponseCount(sectionTotal, true);
            return new ResponseCount(availableCount, true);
        }
        if (command == "impact" && extraction.PrimaryCollection is "callers" or "file_impacts")
        {
            var impactMode = extraction.SourcePayload?["impact_mode"]?.GetValue<string>();
            var collectionIsActive = extraction.PrimaryCollection == "callers"
                ? string.Equals(impactMode, "callers", StringComparison.Ordinal)
                : string.Equals(impactMode, "file_dependency_hints", StringComparison.Ordinal);
            if (!collectionIsActive)
            {
                var missingIdentityRoot = string.Equals(
                    extraction.SourcePayload?["identity_root_unavailable_reason"]?.GetValue<string>(),
                    "no_identity_backed_root",
                    StringComparison.Ordinal);
                var incompleteCallerGraph = extraction.PrimaryCollection == "callers"
                    && extraction.SourcePayload is not null
                    && TryReadBool(extraction.SourcePayload, "reference_graph_complete", out var graphComplete)
                    && !graphComplete;
                var unavailableCallerRoot = extraction.PrimaryCollection == "callers"
                    && extraction.SourcePayload is not null
                    && TryReadBool(extraction.SourcePayload, "identity_root_available", out var identityRootAvailable)
                    && !identityRootAvailable;
                var authoritative = (!missingIdentityRoot && !incompleteCallerGraph && !unavailableCallerRoot)
                    || extraction.SourcePayload == null
                    || !TryReadBool(
                        extraction.SourcePayload,
                        "authoritative_count",
                        out var explicitAuthority)
                    || explicitAuthority;
                return new ResponseCount(0, authoritative);
            }
        }
        if (command == "impact" && extraction.PrimaryCollection == "definitions")
        {
            if (TryReadInt(extraction.SourcePayload, "logical_definition_count", out var logicalDefinitionCount))
                return new ResponseCount(logicalDefinitionCount, true);
            if (TryReadInt(extraction.SourcePayload, "definition_count", out var definitionCount))
                return new ResponseCount(definitionCount, true);
        }
        ResponseCount? terminalCount = null;
        if (streamTerminal is not null
            && TryReadInt(streamTerminal, "total_count", out var terminalTotal)
            && TryReadBool(streamTerminal, "total_count_authoritative", out var terminalAuthoritative))
        {
            terminalCount = new ResponseCount(terminalTotal, terminalAuthoritative);
            if (terminalAuthoritative)
                return terminalCount.Value;
        }
        if (!CountableResponseCommands.Contains(command))
            return terminalCount ?? new ResponseCount(offset + availableCount, false);

        var countArgs = PrepareCountArgs(command, args);
        using var captured = new BoundedStringWriter(MaxRawJsonItemChars);
        int countExitCode;
        try
        {
            using var outputScope = ScopedConsoleOutput.Redirect(captured);
            countExitCode = runInner(countArgs);
        }
        catch
        {
            return terminalCount ?? new ResponseCount(offset + availableCount, false);
        }
        try
        {
            var countItems = ParseRawJsonItems(command, captured.ToString(), out _, out _);
            var countPayload = countItems.OfType<JsonObject>().FirstOrDefault(obj => obj.ContainsKey("count"));
            if (countPayload is null || !TryReadInt(countPayload, "count", out var total))
                return terminalCount ?? new ResponseCount(offset + availableCount, false);
            var authoritative = TryReadBool(countPayload, "authoritative_count", out var explicitAuthority)
                ? explicitAuthority
                : countExitCode == CommandExitCodes.Success
                  && !ReadOptionalBool(countPayload, "degraded")
                  && ReadOptionalBool(countPayload, "graph_table_available", defaultValue: true)
                  && ReadOptionalBool(countPayload, "hotspot_family_ready", defaultValue: true);
            return new ResponseCount(total, authoritative, ExtractCountResponseContext(command, countPayload));
        }
        catch
        {
            return terminalCount ?? new ResponseCount(offset + availableCount, false);
        }
    }

    private static bool TryReadInt(JsonObject? obj, string propertyName, out int value)
    {
        value = 0;
        return obj is not null
               && obj[propertyName] is JsonValue jsonValue
               && jsonValue.TryGetValue(out value);
    }

    private static bool TryReadBool(JsonObject obj, string propertyName, out bool value)
    {
        value = false;
        return obj[propertyName] is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static bool ReadOptionalBool(JsonObject obj, string propertyName, bool defaultValue = false)
        => TryReadBool(obj, propertyName, out var value) ? value : defaultValue;

    private static JsonObject? ExtractCountResponseContext(string command, JsonObject countPayload)
    {
        if (command != "symbols"
            || !countPayload.TryGetPropertyValue("exact_index_available", out var exactIndexAvailable))
        {
            return null;
        }

        var context = new JsonObject
        {
            ["exact_index_available"] = exactIndexAvailable?.DeepClone(),
        };
        if (countPayload.TryGetPropertyValue("degraded_reason", out var degradedReason))
            context["degraded_reason"] = degradedReason?.DeepClone();
        return context;
    }

    private static string? ReadString(JsonObject? obj, string propertyName)
        => obj?[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string[] PrepareBoundedInnerArgs(string command, string[] args, BoundedResponseControls controls)
    {
        var stripped = StripResponseOptions(command, args, stripLimit: PageableResponseCommands.Contains(command));
        var bodyProjected = HasExplicitBodyProjection(controls.Fields);
        var bodyOptionRequested = HasArgument(command, args, "--body");
        if (command is not ("outline" or "references" or "callers" or "callees")
            && !bodyProjected
            && (controls.Compact || controls.Fields is { Count: > 0 }))
        {
            RemoveFlagOptions(command, stripped, arg => string.Equals(arg, "--body", StringComparison.Ordinal));
        }
        var additions = new List<string>();
        if (PageableResponseCommands.Contains(command))
        {
            additions.Add("--limit");
            additions.Add(controls.PageLimit.ToString(CultureInfo.InvariantCulture));
        }
        if (controls.Compact
            && (command == "map"
                || LegacyLocationCompactCommands.Contains(command)
                && !bodyProjected
                && (command is not ("references" or "callers" or "callees")
                    || !bodyOptionRequested)))
        {
            additions.Add("--format");
            additions.Add("compact");
        }
        additions.Add("--json");
        InsertBeforeEndOfOptions(command, stripped, additions);
        return [.. stripped];
    }

    private static bool HasExplicitBodyProjection(IReadOnlyList<string>? fields)
        => fields?.Any(field =>
        {
            var separator = field.LastIndexOf('.');
            var projectedField = separator >= 0 ? field[(separator + 1)..] : field;
            return string.Equals(field, "all", StringComparison.Ordinal)
                   || string.Equals(projectedField, "body", StringComparison.Ordinal)
                   || string.Equals(projectedField, "body_content", StringComparison.Ordinal)
                   || projectedField.StartsWith("body_", StringComparison.Ordinal)
                   || projectedField.StartsWith("callsite_", StringComparison.Ordinal);
        }) == true;

    private static string? ValidateMapProjectionControls(string[] args, IReadOnlyList<string>? fields)
    {
        var collection = SelectRequestedCollection(
            fields,
            "languages",
            "modules",
            "top_files",
            "largest_files",
            "symbol_rich_files",
            "reference_rich_files",
            "entrypoints");
        if (collection is null)
            return null;
        if (HasArgument("map", args, "--summary-only"))
            return $"Map collection projection '{collection}' cannot be combined with --summary-only.";

        var requestedSections = ReadMapSections(args);
        if (requestedSections is null)
            return null;
        var requiredSection = collection switch
        {
            "languages" => "languages",
            "modules" => "tree",
            "largest_files" => "metrics",
            _ => "hotspots",
        };
        return requestedSections.Contains(requiredSection, StringComparer.Ordinal)
            ? null
            : $"Map collection projection '{collection}' requires --sections {requiredSection}.";
    }

    private static List<string>? ReadMapSections(string[] args)
    {
        List<string>? sections = null;
        for (var i = 0; i < args.Length; i++)
        {
            string? value = null;
            if (args[i].StartsWith("--sections=", StringComparison.Ordinal))
                value = args[i]["--sections=".Length..];
            else if (string.Equals(args[i], "--sections", StringComparison.Ordinal) && i + 1 < args.Length)
                value = args[++i];
            if (value is null)
                continue;
            sections ??= [];
            foreach (var section in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                sections.Add(section switch
                {
                    "modules" or "module" => "tree",
                    "entrypoints" or "entrypoint" => "hotspots",
                    "largest-files" or "largest" => "metrics",
                    _ => section,
                });
            }
        }
        return sections;
    }

    private static string[] PrepareCountArgs(string command, string[] args)
    {
        var stripped = StripResponseOptions(command, args, stripLimit: true);
        RemoveFlagOptions(
            command,
            stripped,
            arg => string.Equals(arg, "--body", StringComparison.Ordinal)
                   || string.Equals(arg, "--summary-only", StringComparison.Ordinal)
                   || string.Equals(arg, "--strict-not-found", StringComparison.Ordinal));
        RemoveParsedGraphSnippetLinesOption(stripped);
        var additions = new List<string>();
        if (command == "impact")
        {
            additions.Add("--limit");
            additions.Add(MaxPageWindow.ToString(CultureInfo.InvariantCulture));
        }
        additions.Add("--count");
        additions.Add("--json");
        InsertBeforeEndOfOptions(command, stripped, additions);
        return [.. stripped];
    }

    private static void RemoveFlagOptions(
        string command,
        List<string> args,
        Func<string, bool> shouldRemove)
    {
        var retained = ClassifyArgumentTokens(command, [.. args])
            .Where(token => !token.IsOption || !shouldRemove(token.Value))
            .Select(token => token.Value)
            .ToArray();
        args.Clear();
        args.AddRange(retained);
    }

    private static void RemoveParsedGraphSnippetLinesOption(List<string> args)
    {
        for (var i = 0; i < args.Count;)
        {
            var arg = args[i];
            if (string.Equals(arg, "--", StringComparison.Ordinal))
            {
                i += i + 1 < args.Count ? 2 : 1;
                continue;
            }
            if (arg is "--query" or "--db" or "--path")
            {
                i += i + 1 < args.Count ? 2 : 1;
                continue;
            }
            if (arg.StartsWith("--snippet-lines=", StringComparison.Ordinal))
            {
                args.RemoveAt(i);
                continue;
            }
            if (string.Equals(arg, "--snippet-lines", StringComparison.Ordinal))
            {
                args.RemoveAt(i);
                if (i < args.Count)
                    args.RemoveAt(i);
                continue;
            }
            i++;
        }
    }

    private static List<string> StripResponseOptions(string command, string[] args, bool stripLimit)
    {
        var stripped = new List<string>(args.Length);
        var tokens = ClassifyArgumentTokens(command, args).ToArray();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var arg = token.Value;
            if (token.IsOption
                && (string.Equals(arg, EnvelopeFlag, StringComparison.Ordinal)
                    || string.Equals(arg, "--compact", StringComparison.Ordinal)
                    || string.Equals(arg, "--pretty", StringComparison.Ordinal)
                    || string.Equals(arg, "--json", StringComparison.Ordinal)
                    || arg.StartsWith("--json=", StringComparison.Ordinal)
                    || arg.StartsWith("--fields=", StringComparison.Ordinal)
                    || arg.StartsWith("--cursor=", StringComparison.Ordinal)
                    || arg.StartsWith("--max-json-bytes=", StringComparison.Ordinal)
                    || arg.StartsWith("--format=", StringComparison.Ordinal)
                    || (stripLimit && (arg.StartsWith("--limit=", StringComparison.Ordinal) || arg.StartsWith("--top=", StringComparison.Ordinal)))))
                continue;
            if (token.IsOption && IsResponseValueOption(arg, stripLimit) && i + 1 < tokens.Length)
            {
                i++;
                continue;
            }
            if (token.IsOption && string.Equals(arg, "--count", StringComparison.Ordinal))
                continue;
            stripped.Add(arg);
        }
        return stripped;
    }

    private static string[] PrepareBoundedGraphBodyIntentValidationArgs(string command, string[] args)
    {
        var prepared = new List<string>(args.Length);
        var tokens = ClassifyArgumentTokens(command, args).ToArray();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.IsOption
                && (token.Value.StartsWith("--fields=", StringComparison.Ordinal)
                    || token.Value.StartsWith("--cursor=", StringComparison.Ordinal)))
            {
                continue;
            }
            if (token.IsOption
                && token.Value is "--fields" or "--cursor")
            {
                if (i + 1 < tokens.Length)
                    i++;
                continue;
            }
            prepared.Add(token.Value);
        }
        return [.. prepared];
    }

    private static void InsertBeforeEndOfOptions(
        string command,
        List<string> args,
        IReadOnlyCollection<string> additions)
    {
        var insertionIndex = args.Count;
        var index = 0;
        foreach (var token in ClassifyArgumentTokens(command, [.. args]))
        {
            if (token.IsOption && string.Equals(token.Value, "--", StringComparison.Ordinal))
            {
                insertionIndex = index;
                break;
            }
            index++;
        }
        args.InsertRange(insertionIndex, additions);
    }

    private static bool IsResponseValueOption(string arg, bool includeLimit)
        => arg is "--fields" or "--cursor" or "--max-json-bytes" or "--format"
           || includeLimit && arg is "--limit" or "--top";

    private static bool TryParseBoundedResponseControls(
        string command,
        string[] args,
        out BoundedResponseControls controls,
        out string? error)
    {
        List<string>? fields = null;
        string? cursor = null;
        int? maxJsonBytes = null;
        var pageLimit = DefaultPageLimit;
        var compact = HasCompactOutputSelection(command, args);
        error = null;
        var tokens = ClassifyArgumentTokens(command, args).ToArray();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].IsOption)
                continue;
            var arg = tokens[i].Value;
            if (!TryReadInlineOrSeparated(args, ref i, arg, "--fields", out var fieldsValue, out var matched, out error))
            {
                controls = default!;
                return false;
            }
            if (matched)
            {
                fields = fieldsValue!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (fields.Count == 0 || fields.Count > 64 || fields.Any(field => field.Length > 128))
                {
                    controls = default!;
                    error = "--fields must contain 1 to 64 comma-separated field names, each at most 128 characters.";
                    return false;
                }
                continue;
            }
            if (!TryReadInlineOrSeparated(args, ref i, arg, "--cursor", out var cursorValue, out matched, out error))
            {
                controls = default!;
                return false;
            }
            if (matched)
            {
                cursor = cursorValue;
                continue;
            }
            if (!TryReadPositiveIntControl(args, ref i, arg, "--max-json-bytes", out var parsedBytes, out matched, out error))
            {
                controls = default!;
                return false;
            }
            if (matched)
            {
                maxJsonBytes = parsedBytes;
                continue;
            }
            if (!TryReadPositiveIntControl(args, ref i, arg, "--limit", out var parsedLimit, out matched, out error)
                || !matched && !TryReadPositiveIntControl(args, ref i, arg, "--top", out parsedLimit, out matched, out error))
            {
                controls = default!;
                return false;
            }
            if (matched)
                pageLimit = parsedLimit;
        }

        var offset = 0;
        string? cursorQueryFingerprint = null;
        string? cursorGenerationFingerprint = null;
        string? resumePath = null;
        int? resumeLine = null;
        int? resumeFileOrdinal = null;
        int? resumeMatchOrdinal = null;
        int? resumeByteOffset = null;
        string? partialFamilyId = null;
        int? familyMemberOffset = null;
        if (cursor is not null
            && !TryParseResponseCursor(
                cursor,
                out offset,
                out cursorQueryFingerprint,
                out cursorGenerationFingerprint,
                out resumePath,
                out resumeLine,
                out resumeFileOrdinal,
                out resumeMatchOrdinal,
                out resumeByteOffset,
                out partialFamilyId,
                out familyMemberOffset))
        {
            controls = default!;
            error = "cursor_malformed: --cursor must be an opaque response:v2 cursor returned as next_cursor.";
            return false;
        }
        controls = new BoundedResponseControls(
            fields,
            compact,
            maxJsonBytes,
            pageLimit,
            offset,
            cursorQueryFingerprint,
            cursorGenerationFingerprint,
            resumePath,
            resumeLine,
            resumeFileOrdinal,
            resumeMatchOrdinal,
            resumeByteOffset,
            partialFamilyId,
            familyMemberOffset);
        return true;
    }

    private static bool TryReadInlineOrSeparated(
        string[] args,
        ref int index,
        string arg,
        string option,
        out string? value,
        out bool matched,
        out string? error)
    {
        value = null;
        matched = false;
        error = null;
        if (arg.StartsWith(option + "=", StringComparison.Ordinal))
        {
            matched = true;
            value = arg[(option.Length + 1)..];
        }
        else if (string.Equals(arg, option, StringComparison.Ordinal))
        {
            matched = true;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                error = $"{option} requires a value.";
                return false;
            }
            value = args[++index];
        }
        return true;
    }

    private static bool TryReadPositiveIntControl(
        string[] args,
        ref int index,
        string arg,
        string option,
        out int value,
        out bool matched,
        out string? error)
    {
        value = 0;
        if (!TryReadInlineOrSeparated(args, ref index, arg, option, out var raw, out matched, out error))
            return false;
        if (!matched)
            return true;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            error = $"{option} requires a positive integer.";
            return false;
        }
        return true;
    }

    private static bool TryReadRequestedMaxJsonBytes(string command, string[] args, out long requestedBytes)
    {
        requestedBytes = 0;
        var tokens = ClassifyArgumentTokens(command, args).ToArray();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].IsOption)
                continue;
            const string option = "--max-json-bytes";
            var arg = tokens[i].Value;
            string? raw = null;
            if (arg.StartsWith(option + "=", StringComparison.Ordinal))
                raw = arg[(option.Length + 1)..];
            else if (string.Equals(arg, option, StringComparison.Ordinal) && i + 1 < args.Length)
                raw = args[i + 1];
            if (raw is null)
                continue;
            if (!long.TryParse(
                    raw,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out requestedBytes))
            {
                return false;
            }
            if (requestedBytes <= 0)
                return true;
            if (requestedBytes > int.MaxValue)
                return false;
        }

        return false;
    }

    private static string BuildResponseFingerprint(string command, string[] args)
    {
        var scanMode = command == "find"
            ? IsFindCountResponseRequest(args) ? "count" : "rows"
            : null;
        var normalized = StripResponseOptions(command, args, stripLimit: true);
        RemoveFlagOptions(
            command,
            normalized,
            arg => arg is "--body" or "--allow-partial" or "--results-only" or "--verbose" or "--profile");
        RemoveOptionWithValue(command, normalized, "--line-scan-limit");
        var input = command + "\0" + string.Join('\0', normalized);
        if (scanMode is not null)
            input += "\0scan-mode=" + scanMode;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static void RemoveOptionWithValue(string command, List<string> args, string option)
    {
        var tokens = ClassifyArgumentTokens(command, [.. args]).ToArray();
        var retained = new List<string>(args.Count);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (!token.IsOption)
            {
                retained.Add(token.Value);
                continue;
            }
            if (token.Value.StartsWith(option + "=", StringComparison.Ordinal))
                continue;
            if (string.Equals(token.Value, option, StringComparison.Ordinal))
            {
                if (i + 1 < tokens.Length)
                    i++;
                continue;
            }
            retained.Add(token.Value);
        }
        args.Clear();
        args.AddRange(retained);
    }

    internal static string FormatResponseCursor(
        int offset,
        string queryFingerprint,
        string generationFingerprint,
        string? resumePath = null,
        int? resumeLine = null,
        int? resumeFileOrdinal = null,
        int? resumeMatchOrdinal = null,
        int? resumeByteOffset = null,
        string? partialFamilyId = null,
        int? familyMemberOffset = null)
    {
        var payload = new JsonObject
        {
            ["offset"] = offset,
            ["query"] = queryFingerprint,
            ["generation"] = generationFingerprint,
        };
        if (resumePath is not null)
            payload["resume_path"] = resumePath;
        if (resumeLine.HasValue)
            payload["resume_line"] = resumeLine.Value;
        if (resumeFileOrdinal.HasValue)
            payload["resume_file_ordinal"] = resumeFileOrdinal.Value;
        if (resumeMatchOrdinal.HasValue)
            payload["resume_match_ordinal"] = resumeMatchOrdinal.Value;
        if (resumeByteOffset.HasValue)
            payload["resume_byte_offset"] = resumeByteOffset.Value;
        if (partialFamilyId is not null
            && familyMemberOffset.HasValue)
        {
            payload["partial_family_id"] = partialFamilyId;
            payload["family_member_offset"] = familyMemberOffset.Value;
            payload["family_member_integrity"] = BuildPartialFamilyCursorIntegrity(
                queryFingerprint,
                generationFingerprint,
                partialFamilyId,
                familyMemberOffset.Value);
        }
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return ResponseCursorPrefix + encoded;
    }

    internal static bool TryParseResponseCursor(
        string cursor,
        out int offset,
        out string? queryFingerprint,
        out string? generationFingerprint,
        out string? resumePath,
        out int? resumeLine,
        out int? resumeFileOrdinal,
        out int? resumeMatchOrdinal,
        out int? resumeByteOffset)
        => TryParseResponseCursor(
            cursor,
            out offset,
            out queryFingerprint,
            out generationFingerprint,
            out resumePath,
            out resumeLine,
            out resumeFileOrdinal,
            out resumeMatchOrdinal,
            out resumeByteOffset,
            out _,
            out _);

    internal static bool TryParseResponseCursor(
        string cursor,
        out int offset,
        out string? queryFingerprint,
        out string? generationFingerprint,
        out string? resumePath,
        out int? resumeLine,
        out int? resumeFileOrdinal,
        out int? resumeMatchOrdinal,
        out int? resumeByteOffset,
        out string? partialFamilyId,
        out int? familyMemberOffset)
    {
        offset = 0;
        queryFingerprint = null;
        generationFingerprint = null;
        resumePath = null;
        resumeLine = null;
        resumeFileOrdinal = null;
        resumeMatchOrdinal = null;
        resumeByteOffset = null;
        partialFamilyId = null;
        familyMemberOffset = null;
        if (cursor.StartsWith(LegacyResponseCursorPrefix, StringComparison.Ordinal))
        {
            var remainder = cursor[LegacyResponseCursorPrefix.Length..];
            var separator = remainder.IndexOf(':');
            if (separator <= 0 || separator == remainder.Length - 1)
                return false;
            if (!int.TryParse(remainder[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
                return false;
            queryFingerprint = remainder[(separator + 1)..];
            return IsCursorFingerprint(queryFingerprint);
        }
        if (!cursor.StartsWith(ResponseCursorPrefix, StringComparison.Ordinal)
            || cursor.Length > 16_384)
        {
            return false;
        }

        var encoded = cursor[ResponseCursorPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var paddingLength = (4 - encoded.Length % 4) % 4;
        if (paddingLength > 0)
            encoded += new string('=', paddingLength);
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))) as JsonObject;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
        if (payload is null
            || !TryReadInt(payload, "offset", out offset)
            || offset < 0)
        {
            return false;
        }
        queryFingerprint = ReadString(payload, "query");
        generationFingerprint = ReadString(payload, "generation");
        resumePath = ReadString(payload, "resume_path");
        if (payload.ContainsKey("resume_line"))
        {
            if (payload["resume_line"] is not JsonValue resumeValue
                || !resumeValue.TryGetValue<int>(out var parsedResumeLine))
            {
                return false;
            }
            resumeLine = parsedResumeLine;
        }
        if (payload.ContainsKey("resume_file_ordinal"))
        {
            if (payload["resume_file_ordinal"] is not JsonValue fileOrdinalValue
                || !fileOrdinalValue.TryGetValue<int>(out var parsedFileOrdinal))
            {
                return false;
            }
            resumeFileOrdinal = parsedFileOrdinal;
        }
        if (payload.ContainsKey("resume_match_ordinal"))
        {
            if (payload["resume_match_ordinal"] is not JsonValue matchOrdinalValue
                || !matchOrdinalValue.TryGetValue<int>(out var parsedMatchOrdinal))
            {
                return false;
            }
            resumeMatchOrdinal = parsedMatchOrdinal;
        }
        if (payload.ContainsKey("resume_byte_offset"))
        {
            if (payload["resume_byte_offset"] is not JsonValue byteOffsetValue
                || !byteOffsetValue.TryGetValue<int>(out var parsedByteOffset))
            {
                return false;
            }
            resumeByteOffset = parsedByteOffset;
        }
        partialFamilyId = ReadString(payload, "partial_family_id");
        var partialFamilyIntegrity = ReadString(payload, "family_member_integrity");
        if (payload.ContainsKey("family_member_offset"))
        {
            if (payload["family_member_offset"] is not JsonValue familyOffsetValue
                || !familyOffsetValue.TryGetValue<int>(out var parsedFamilyOffset))
            {
                return false;
            }
            familyMemberOffset = parsedFamilyOffset;
        }
        var extendedResumeFieldsPresent = resumeFileOrdinal.HasValue
                                          || resumeMatchOrdinal.HasValue
                                          || resumeByteOffset.HasValue;
        var partialFamilyFieldsPresent = partialFamilyId is not null
                                         || familyMemberOffset.HasValue
                                         || partialFamilyIntegrity is not null;
        var partialFamilyFieldsValid = !partialFamilyFieldsPresent
            || offset == 0
            && IsPartialFamilyId(partialFamilyId)
            && familyMemberOffset is >= 0
            && string.Equals(
                partialFamilyIntegrity,
                BuildPartialFamilyCursorIntegrity(
                    queryFingerprint!,
                    generationFingerprint!,
                    partialFamilyId!,
                    familyMemberOffset.Value),
                StringComparison.Ordinal);
        return IsCursorFingerprint(queryFingerprint)
               && IsCursorFingerprint(generationFingerprint)
               && (resumePath is null || resumePath.Length <= 4096)
               && (!resumeLine.HasValue || resumeLine.Value > 0)
               && (resumePath is null) == !resumeLine.HasValue
               && (!extendedResumeFieldsPresent
                   || resumePath is not null
                   && resumeFileOrdinal is >= 0
                   && resumeByteOffset is >= 0
                   && resumeMatchOrdinal is null or >= 0)
               && (!resumeMatchOrdinal.HasValue || resumeByteOffset.HasValue)
               && !payload.ContainsKey("partial_family_key")
               && partialFamilyFieldsValid;
    }

    private static string BuildPartialFamilyCursorIntegrity(
        string queryFingerprint,
        string generationFingerprint,
        string partialFamilyId,
        int familyMemberOffset)
        => BuildResponseValueFingerprint(string.Join(
            '\0',
            "partial-family-members:v2",
            queryFingerprint,
            generationFingerprint,
            partialFamilyId,
            familyMemberOffset.ToString(CultureInfo.InvariantCulture)));

    private static bool IsPartialFamilyId(string? value)
        => value is { Length: 32 }
           && value.StartsWith("partial:", StringComparison.Ordinal)
           && value["partial:".Length..].All(static character =>
               character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCursorFingerprint(string? fingerprint)
        => fingerprint is { Length: 16 } && fingerprint.All(Uri.IsHexDigit);

    private static int WriteBoundedResponseUsageError(string message, string hint)
    {
        CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.UsageError}]: {message}");
        CommandErrorWriter.WriteStderr($"Hint: {hint}");
        return CommandExitCodes.UsageError;
    }

    private static string GetBoundedResponseUsage(string command)
        => ConsoleUi.GetUsageLine(command) ?? $"cdidx {command} --help";

    private static int WriteBoundedCaptureError(
        string command,
        string? queryNormalized,
        string dbPath,
        bool dbPathExplicit,
        string appVersion,
        double elapsedMs,
        JsonSerializerOptions jsonOptions,
        int exitCode,
        string message,
        string hint,
        int? maxJsonBytes,
        bool suppressRuntimeMetadata)
        => WriteBoundedErrorEnvelope(command, queryNormalized, dbPath, dbPathExplicit, appVersion, elapsedMs, jsonOptions, exitCode, message, hint, null, null, maxJsonBytes, suppressRuntimeMetadata);

    private static int WriteBoundedParseError(
        string command,
        string? queryNormalized,
        string dbPath,
        bool dbPathExplicit,
        string appVersion,
        double elapsedMs,
        JsonSerializerOptions jsonOptions,
        string message,
        string hint,
        string budgetProperty,
        int budgetValue,
        int? maxJsonBytes,
        bool suppressRuntimeMetadata)
        => WriteBoundedErrorEnvelope(command, queryNormalized, dbPath, dbPathExplicit, appVersion, elapsedMs, jsonOptions, CommandExitCodes.InvalidArgument, message, hint, budgetProperty, budgetValue, maxJsonBytes, suppressRuntimeMetadata);

    private static int WriteBoundedErrorEnvelope(
        string command,
        string? queryNormalized,
        string dbPath,
        bool dbPathExplicit,
        string appVersion,
        double elapsedMs,
        JsonSerializerOptions jsonOptions,
        int exitCode,
        string message,
        string hint,
        string? budgetProperty,
        int? budgetValue,
        int? maxJsonBytes,
        bool suppressRuntimeMetadata)
    {
        var error = new JsonObject
        {
            ["message"] = message,
            ["hint"] = hint,
            ["error_code"] = CommandErrorCodes.UsageError,
        };
        if (budgetProperty is not null)
            error[budgetProperty] = budgetValue;
        var envelope = BuildEnvelope(
            command,
            queryNormalized,
            dbPath,
            dbPathExplicit,
            appVersion,
            elapsedMs,
            [],
            exitCode,
            error,
            suppressRuntimeMetadata: suppressRuntimeMetadata);
        var json = envelope.ToJsonString(jsonOptions);
        if (maxJsonBytes.HasValue && !JsonFitsResponseBudget(json, maxJsonBytes.Value))
        {
            return CommandErrorWriter.WriteResponseBudgetError(
                json: true,
                jsonOptions,
                command,
                $"--max-json-bytes {maxJsonBytes.Value} is too small for the complete error envelope.",
                "Increase --max-json-bytes and retry the same command.",
                requestedBytes: maxJsonBytes.Value,
                effectiveBytes: maxJsonBytes.Value,
                minimumRequiredBytes: GetJsonResponseByteCount(json),
                minimumRequiredBytesUncertaintyReason:
                    CommandErrorWriter.MinimumResponseBytesUncertainRuntimeEnvelope,
                recommendedBytes: GetJsonResponseByteCount(json) + ResponseBudgetRetryHeadroomBytes,
                usage: GetBoundedResponseUsage(command),
                exitCode: exitCode);
        }

        Console.WriteLine(json);
        return exitCode;
    }

    private sealed record BoundedResponseControls(
        IReadOnlyList<string>? Fields,
        bool Compact,
        int? MaxJsonBytes,
        int PageLimit,
        int Offset,
        string? CursorQueryFingerprint,
        string? CursorGenerationFingerprint,
        string? ResumePath,
        int? ResumeLine,
        int? ResumeFileOrdinal,
        int? ResumeMatchOrdinal,
        int? ResumeByteOffset,
        string? PartialFamilyId,
        int? FamilyMemberOffset)
    {
        public IReadOnlyList<string>? EffectiveFields(
            string command,
            string? primaryCollection,
            bool statusExplainRequest,
            bool groupedSymbolsRequest)
        {
            var preserveFullDiscoveryRows = command is "search" or "languages"
                                            || command == "symbols"
                                            && PartialFamilyId is not null
                                            && !Compact;
            var selected = Fields
                           ?? (statusExplainRequest
                               ? ProjectionFieldRegistry.GetStatusExplainCompactFields()
                               : null)
                           ?? (command == "unused" && Compact
                               ? UnusedCompactResponseFields
                               : null)
                           ?? ((!preserveFullDiscoveryRows || Compact)
                               && ProjectionFieldRegistry.GetCompactFields(command) is { } defaults
                               ? defaults
                               : null);
            if (selected is null)
                return selected;
            if (primaryCollection is null)
                return PreserveBodyContentCompanionFields(selected);
            var dotted = selected
                .Where(field => field.StartsWith(primaryCollection + ".", StringComparison.Ordinal))
                .Select(field => field[(primaryCollection.Length + 1)..])
                .ToList();
            if (dotted.Count > 0)
                return PreserveBodyContentCompanionFields(
                    PreservePartialFamilyContinuationFields(
                        dotted,
                        command,
                        groupedSymbolsRequest));
            if (selected.Contains(primaryCollection, StringComparer.Ordinal))
                return null;
            return PreserveBodyContentCompanionFields(
                PreservePartialFamilyContinuationFields(
                    selected.Where(field => !field.Contains('.')).ToList(),
                    command,
                    groupedSymbolsRequest));
        }

        private static IReadOnlyList<string> PreserveBodyContentCompanionFields(
            IReadOnlyList<string> selected)
            => selected.Any(field => field is "body" or "body_content")
                ? selected
                    .Concat(BodyContentCompanionProjectionFields)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : selected;

        private static IReadOnlyList<string> PreservePartialFamilyContinuationFields(
            IReadOnlyList<string> selected,
            string command,
            bool groupedSymbolsRequest)
            => command == "symbols" && groupedSymbolsRequest
                ? selected
                    .Concat(PartialFamilyContinuationProjectionFields)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : selected;
    }

    private sealed record ResponseExtraction(
        JsonArray Items,
        string? PrimaryCollection,
        JsonObject? Context,
        JsonObject? SourcePayload);

    private readonly record struct ResponseCount(
        int TotalCount,
        bool Authoritative,
        JsonObject? Context = null);

    private readonly record struct ResponseSnapshot(
        string GenerationFingerprint,
        string? ResultStableAt,
        string? IndexedHead);

    private static ResponseSnapshot SafeReadResponseSnapshot(
        string dbPath,
        bool dbPathExplicit,
        string appVersion)
    {
        try
        {
            if (!dbPathExplicit
                && !dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(dbPath))
            {
                return BuildFallbackResponseSnapshot(appVersion);
            }

            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            if (!db.TryValidateIsCodeIndexDb(out _))
                return BuildFallbackResponseSnapshot(appVersion);
            return BuildResponseSnapshot(new DbReader(db));
        }
        catch
        {
            return BuildFallbackResponseSnapshot(appVersion);
        }
    }

    private static ResponseSnapshot BuildResponseSnapshot(DbReader reader)
    {
        var generation = reader.GetPaginationGeneration();
        return new(
            BuildResponseValueFingerprint(generation.Identity),
            generation.StableAt,
            reader.GetIndexedHeadForResponse());
    }

    private static ResponseSnapshot BuildFallbackResponseSnapshot(string appVersion)
        => new(BuildResponseValueFingerprint("catalog\0" + appVersion), null, null);

    private static string BuildResponseValueFingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    internal static (string Cursor, string? ResultStableAt) BuildFindResumeCursor(
        string[] args,
        DbReader reader,
        string resumePath,
        int resumeLine,
        int resumeFileOrdinal,
        int? resumeMatchOrdinal,
        int resumeByteOffset)
    {
        var snapshot = BuildResponseSnapshot(reader);
        return (
            FormatResponseCursor(
                offset: 0,
                BuildResponseFingerprint("find", args),
                snapshot.GenerationFingerprint,
                resumePath,
                resumeLine,
                resumeFileOrdinal,
                resumeMatchOrdinal,
                resumeByteOffset),
            snapshot.ResultStableAt);
    }

    internal static string BuildPartialFamilyMembersCursor(
        string[] args,
        PartialFamilyCursorSnapshot snapshot,
        string partialFamilyId,
        int familyMemberOffset)
    {
        return FormatResponseCursor(
            offset: 0,
            BuildResponseFingerprint("symbols", args),
            snapshot.GenerationFingerprint,
            partialFamilyId: partialFamilyId,
            familyMemberOffset: familyMemberOffset);
    }

    internal static PartialFamilyCursorSnapshot CapturePartialFamilyCursorSnapshot(
        DbReader reader)
        => new(BuildResponseSnapshot(reader).GenerationFingerprint);

    internal static int? ValidatePartialFamilyCursorSnapshot(
        string dbPath,
        bool dbPathExplicit,
        PartialFamilyCursorSnapshot snapshot)
    {
        PartialFamilyPageReadForTesting?.Invoke();
        var completedSnapshot = SafeReadResponseSnapshot(
            dbPath,
            dbPathExplicit,
            appVersion: "partial-family");
        if (string.Equals(
                snapshot.GenerationFingerprint,
                completedSnapshot.GenerationFingerprint,
                StringComparison.Ordinal))
        {
            return null;
        }

        return WriteBoundedResponseUsageError(
            "The index generation changed while this partial-family page was being read.",
            "Restart pagination without --cursor after the active index refresh completes.");
    }

    internal static int GetBoundedResponseOffset(string command)
    {
        var execution = BoundedExecution.Value;
        return execution is not null
               && string.Equals(execution.Command, CanonicalizeCommandName(command), StringComparison.Ordinal)
            ? execution.Offset
            : 0;
    }

    internal static PartialFamilyContinuation? GetPartialFamilyContinuation(string command)
    {
        var execution = BoundedExecution.Value;
        if (execution is null
            || !string.Equals(execution.Command, CanonicalizeCommandName(command), StringComparison.Ordinal)
            || execution.PartialFamilyId is null
            || !execution.FamilyMemberOffset.HasValue)
        {
            return null;
        }

        return new PartialFamilyContinuation(
            execution.PartialFamilyId,
            execution.FamilyMemberOffset.Value);
    }

    internal readonly record struct PartialFamilyContinuation(
        string PartialFamilyId,
        int FamilyMemberOffset);

    internal readonly record struct PartialFamilyCursorSnapshot(
        string GenerationFingerprint);

    internal static bool ShouldMaterializeBody(string command)
    {
        var execution = BoundedExecution.Value;
        if (execution is null
            || !string.Equals(execution.Command, CanonicalizeCommandName(command), StringComparison.Ordinal))
        {
            return true;
        }

        return execution.Fields is { Count: > 0 }
            ? HasExplicitBodyProjection(execution.Fields)
            : !execution.Compact;
    }

    internal static (string? Path, int? Line, int? FileOrdinal, int? MatchOrdinal, int? ByteOffset) GetBoundedFindResume()
    {
        var execution = BoundedExecution.Value;
        return execution is not null && string.Equals(execution.Command, "find", StringComparison.Ordinal)
            ? (
                execution.ResumePath,
                execution.ResumeLine,
                execution.ResumeFileOrdinal,
                execution.ResumeMatchOrdinal,
                execution.ResumeByteOffset)
            : (null, null, null, null, null);
    }

    internal static (string? Path, int? Line, int? FileOrdinal, int? MatchOrdinal, int? ByteOffset)
        GetStandaloneFindResume(string[] args, DbReader reader)
    {
        string? cursor = null;
        var cursorSeen = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--", StringComparison.Ordinal))
                break;
            if (string.Equals(args[i], "--query", StringComparison.Ordinal)
                && i + 1 < args.Length)
            {
                i++;
                continue;
            }
            if (args[i].StartsWith("--cursor=", StringComparison.Ordinal))
            {
                if (cursorSeen)
                    throw new FindContinuationException("cursor_malformed", "find count accepts exactly one --cursor value.");
                cursorSeen = true;
                cursor = args[i]["--cursor=".Length..];
                continue;
            }
            if (!string.Equals(args[i], "--cursor", StringComparison.Ordinal))
                continue;
            if (cursorSeen
                || i + 1 >= args.Length
                || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new FindContinuationException("cursor_malformed", "find count requires exactly one non-empty --cursor value.");
            }
            cursorSeen = true;
            cursor = args[++i];
        }
        if (cursor is null)
            return (null, null, null, null, null);
        if (!TryParseResponseCursor(
                cursor,
                out var offset,
                out var queryFingerprint,
                out var generationFingerprint,
                out var resumePath,
                out var resumeLine,
                out var resumeFileOrdinal,
                out var resumeMatchOrdinal,
                out var resumeByteOffset)
            || offset != 0
            || resumePath is null
            || !resumeLine.HasValue)
        {
            throw new FindContinuationException(
                "cursor_malformed",
                "find count cursor must be an opaque resumable response:v2 cursor returned as next_cursor.");
        }

        var expectedQueryFingerprint = BuildResponseFingerprint("find", args);
        if (!string.Equals(queryFingerprint, expectedQueryFingerprint, StringComparison.Ordinal))
        {
            throw new FindContinuationException(
                "cursor_mismatch",
                "find count cursor does not match this query, scan mode, or option set.");
        }
        var snapshot = BuildResponseSnapshot(reader);
        if (!string.Equals(generationFingerprint, snapshot.GenerationFingerprint, StringComparison.Ordinal))
        {
            throw new FindContinuationException(
                "cursor_stale",
                "find count cursor is stale because the index generation changed.");
        }
        return (resumePath, resumeLine, resumeFileOrdinal, resumeMatchOrdinal, resumeByteOffset);
    }

    internal static int? GetBoundedResponseLimit(string command)
    {
        var execution = BoundedExecution.Value;
        return execution is not null
               && string.Equals(execution.Command, CanonicalizeCommandName(command), StringComparison.Ordinal)
            ? execution.Limit
            : null;
    }

    internal static void ReportBoundedResponseTotal(
        string command,
        int totalCount,
        bool authoritative)
    {
        var execution = BoundedExecution.Value;
        if (execution is null
            || !string.Equals(execution.Command, CanonicalizeCommandName(command), StringComparison.Ordinal))
        {
            return;
        }

        execution.ReportedTotalCount = Math.Max(0, totalCount);
        execution.ReportedTotalCountAuthoritative = authoritative;
    }

    internal static string? GetBoundedMapCollection()
    {
        var execution = BoundedExecution.Value;
        if (execution is null || !string.Equals(execution.Command, "map", StringComparison.Ordinal))
            return null;
        return SelectRequestedCollection(
            execution.Fields,
            "languages",
            "modules",
            "top_files",
            "largest_files",
            "symbol_rich_files",
            "reference_rich_files",
            "entrypoints");
    }

    internal static bool IsBoundedMapScalarProjection()
    {
        var execution = BoundedExecution.Value;
        return execution is not null
               && string.Equals(execution.Command, "map", StringComparison.Ordinal)
               && GetBoundedMapCollection() is null
               && execution.Fields is { Count: > 0 } fields
               && !fields.Any(field =>
                   string.Equals(field, "language_count", StringComparison.Ordinal)
                   || string.Equals(field, "module_count", StringComparison.Ordinal)
                   || string.Equals(field, "entrypoint_count", StringComparison.Ordinal));
    }

    internal static string? GetBoundedImpactCollection()
    {
        var execution = BoundedExecution.Value;
        return execution is not null && string.Equals(execution.Command, "impact", StringComparison.Ordinal)
            ? SelectRequestedCollection(execution.Fields, "callers", "file_impacts", "definitions")
            : null;
    }

    private static IDisposable EnterBoundedExecution(BoundedExecutionContext execution)
    {
        var previous = BoundedExecution.Value;
        BoundedExecution.Value = execution;
        return new BoundedExecutionScope(previous);
    }

    private sealed record BoundedExecutionContext(
        string Command,
        int Offset,
        int Limit,
        IReadOnlyList<string>? Fields,
        bool Compact,
        string? ResumePath,
        int? ResumeLine,
        int? ResumeFileOrdinal,
        int? ResumeMatchOrdinal,
        int? ResumeByteOffset,
        string? PartialFamilyId,
        int? FamilyMemberOffset)
    {
        public int? ReportedTotalCount { get; set; }
        public bool ReportedTotalCountAuthoritative { get; set; }
    }

    private sealed class BoundedExecutionScope(BoundedExecutionContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            BoundedExecution.Value = previous;
            _disposed = true;
        }
    }
}
