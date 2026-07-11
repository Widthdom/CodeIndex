using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    /// <summary>
    /// Maximum length for suggestion description text.
    /// 提案説明テキストの最大長。
    /// </summary>
    private const int MaxDescriptionLength = 2000;

    /// <summary>
    /// Maximum length for suggestion context text.
    /// 提案コンテキストテキストの最大長。
    /// </summary>
    private const int MaxContextLength = 1000;

    private const int MaxSamplingPromptBytes = 4096;
    private const int MaxSamplingShortFieldChars = 80;
    private const int MaxSamplingDescriptionChars = 800;
    private const int MaxSamplingContextChars = 400;
    private const int MaxSamplingToolInvocationSummaryChars = 160;
    private const int MaxSamplingResponseTextChars = 8192;
    private const int MaxSamplingResponseJsonBytes = MaxSamplingResponseTextChars * 4;
    private const int MaxSamplingResponseJsonDepth = 16;

    /// <summary>
    /// Handle the suggest_improvement tool call.
    /// Records a structured suggestion to .cdidx/suggestions-*.json.
    /// Validates that no source code is included in the description or context.
    /// suggest_improvementツール呼び出しを処理する。
    /// 構造化された提案を .cdidx/suggestions-*.json に記録する。
    /// description と context にソースコードが含まれていないことを検証する。
    /// </summary>
    private async Task<JsonNode> ExecuteSuggestImprovementAsync(JsonNode? id, JsonNode? args)
    {
        // 1. Validate required parameters / 必須パラメータのバリデーション
        if (!TryReadRequiredStringParameter(args, "category", out var category, out var requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        if (!SuggestionRecord.ValidCategories.Contains(category))
        {
            var similar = ConsoleUi.FindClosestMatches(category, SuggestionRecord.ValidCategories);
            var message = $"Invalid category: '{category}'. Must be one of: {string.Join(", ", SuggestionRecord.ValidCategories)}";
            if (similar.Count > 0)
                message += $". Did you mean: {string.Join(", ", similar)}?";
            return CreateToolErrorResponse(id, message, similar);
        }

        if (!TryReadRequiredStringParameter(args, "description", out var description, out requiredError))
            return CreateToolErrorResponse(id, requiredError!);

        if (description.Length > MaxDescriptionLength)
            return CreateToolErrorResponse(id, $"Description too long ({description.Length} chars, max {MaxDescriptionLength})");

        // 2. Validate optional parameters / 任意パラメータのバリデーション
        var language = args?["language"]?.GetValue<string>();
        var context = args?["context"]?.GetValue<string>();
        var toolInvocationContext = args?["toolInvocationContext"]?.GetValue<string>();
        var evidencePaths = ReadEvidencePaths(args?["evidencePaths"] ?? args?["evidence_paths"], out var evidencePathsError);
        if (evidencePathsError != null)
            return CreateToolErrorResponse(id, evidencePathsError);

        if (context != null && context.Length > MaxContextLength)
            return CreateToolErrorResponse(id, $"Context too long ({context.Length} chars, max {MaxContextLength})");
        if (toolInvocationContext != null && toolInvocationContext.Length > MaxContextLength)
            return CreateToolErrorResponse(id, $"Tool invocation context too long ({toolInvocationContext.Length} chars, max {MaxContextLength})");

        // 3. Source code leak detection — reject if code is detected
        //    ソースコード漏洩検出 — コードが検出されたら拒否
        var descriptionDetection = SourceCodeDetector.Detect(description);
        if (descriptionDetection.ContainsSourceCode)
            return CreateSourceCodeDetectedErrorResponse(
                id,
                "description",
                descriptionDetection,
                "Description appears to contain source code. Please describe the gap in natural language without including code.");

        if (context != null)
        {
            var contextDetection = SourceCodeDetector.Detect(context);
            if (contextDetection.ContainsSourceCode)
                return CreateSourceCodeDetectedErrorResponse(
                    id,
                    "context",
                    contextDetection,
                    "Context appears to contain source code. Please describe what you were trying to do without including code.");
        }

        if (toolInvocationContext != null)
        {
            var invocationDetection = SourceCodeDetector.Detect(toolInvocationContext);
            if (invocationDetection.ContainsSourceCode)
                return CreateSourceCodeDetectedErrorResponse(
                    id,
                    "toolInvocationContext",
                    invocationDetection,
                    "Tool invocation context appears to contain source code. Please describe the invocation without including code.");
        }

        var samplingDecision = ResolveSuggestionSamplingDecision();
        var samplingAttempt = await TrySampleSuggestionMetadataAsync(
            category,
            language,
            RedactSuggestionSamplingInput(description),
            context == null ? null : RedactSuggestionSamplingInput(context),
            toolInvocationContext == null ? null : RedactSuggestionSamplingInput(toolInvocationContext),
            samplingDecision).ConfigureAwait(false);
        var sampling = RedactSuggestionSamplingResult(samplingAttempt.Result);

        // 4. Compute dedup hash / 重複排除ハッシュを計算
        var hash = SuggestionStore.ComputeHash(category, language, description);

        // 5. Resolve .cdidx directory and create if needed
        //    .cdidx ディレクトリを解決し、必要に応じて作成
        var cdidxDir = Path.GetDirectoryName(_dbPath);
        if (string.IsNullOrEmpty(cdidxDir))
            cdidxDir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (string.IsNullOrEmpty(cdidxDir))
            cdidxDir = Path.Combine(Path.GetFullPath("."), ".cdidx");
        DataDirectorySecurity.CreatePrivateDirectory(cdidxDir);
        cdidxDir = Path.GetFullPath(cdidxDir);
        if (!TryProbeCdidxDirectoryWritable(cdidxDir, out var probeError))
            return CreateToolErrorResponse(id, probeError!);

        // 6. Store locally, reserve a submission attempt under the file lock,
        //    then call GitHub outside the lock so slow remote I/O does not block
        //    other suggestion-store writers.
        //    ローカル保存と送信試行の予約だけをファイルロック内で行い、
        //    GitHub 呼び出しはロック外で実行する。遅い remote I/O が他の
        //    suggestion-store writer をブロックしないようにする。
        // Derive DB identity for scoped suggestion storage.
        // スコープ付き提案蓄積のため DB identity を導出。
        var dbName = Path.GetFileNameWithoutExtension(_dbPath);
        var store = new SuggestionStore(cdidxDir, dbName, _timeProvider);
        var record = new SuggestionRecord
        {
            Category = category,
            Language = language,
            Description = description,
            Context = context,
            Hash = hash,
            CreatedByAgent = ResolveSuggestionAgent(),
            SessionId = _sessionId,
            ClientVersion = _version,
            McpClientName = _clientName,
            McpClientVersion = _clientVersion,
            ToolInvocationContext = toolInvocationContext,
            SampledTitle = sampling?.Title,
            SampledTags = sampling?.Tags,
            EvidencePaths = evidencePaths,
        };

        // Build GitHub submission callback (null if no token configured).
        // GitHub 送信コールバックを構築（トークン未設定なら null）。
        Func<SuggestionRecord, CancellationToken, Task<SuggestionStore.SubmitAttemptResult>>? githubCallback = null;
        var githubTokenConfigured = GitHubIssueReporter.ResolveToken() != null;
        var cancellationToken = _currentRequestToken.Value;
        if (githubTokenConfigured)
        {
            var version = _version;
            githubCallback = (r, token) => GitHubIssueReporter.TryCreateIssueDetailedAsync(r, version, token);
        }

        var result = await store.TryAddAndSubmitAsync(record, githubCallback, cancellationToken).ConfigureAwait(false);
        var storedHash = result.StoredHash ?? hash;

        if (!result.IsNew)
        {
            var dupPayload = new JsonObject
            {
                ["status"] = "duplicate",
                ["hash"] = storedHash,
                ["message"] = result.AlreadySubmitted
                    ? "This suggestion has already been recorded and submitted."
                    : result.UpstreamUrl != null
                        ? "This suggestion was already recorded. GitHub submission retried successfully."
                        : "This suggestion has already been recorded.",
                ["submitted_to_github"] = result.AlreadySubmitted || result.UpstreamUrl != null,
                ["github_submission_reason"] = ResolveGitHubSubmissionReason(result, githubTokenConfigured),
                ["lifecycle_status"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString()),
                ["cdidx_dir"] = cdidxDir,
            };
            if (result.SubmissionError != null)
                dupPayload["github_submission_error"] = result.SubmissionError;
            if (result.DuplicateOfHash != null)
                dupPayload["duplicate_of"] = result.DuplicateOfHash;
            if (result.DuplicateScore != null)
                dupPayload["duplicate_score"] = result.DuplicateScore.Value;
            if (result.UpstreamUrl != null)
            {
                dupPayload["upstream_url"] = result.UpstreamUrl;
                dupPayload["github_issue_url"] = result.UpstreamUrl;
            }
            AddSuggestionSamplingDiagnostics(dupPayload, samplingDecision, sampling, samplingAttempt.Diagnostic);
            return CreateToolResult(id, "Duplicate suggestion (already recorded).", dupPayload);
        }

        // 7. Return success / 成功レスポンスを返す
        var payload = new JsonObject
        {
            ["status"] = "recorded",
            ["hash"] = storedHash,
            ["category"] = category,
            ["language"] = language,
            ["stored_locally"] = true,
            ["submitted_to_github"] = result.UpstreamUrl != null,
            ["github_submission_reason"] = ResolveGitHubSubmissionReason(result, githubTokenConfigured),
            ["lifecycle_status"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(result.Status.ToString()),
            ["cdidx_dir"] = cdidxDir,
        };
        AddSuggestionSamplingDiagnostics(payload, samplingDecision, sampling, samplingAttempt.Diagnostic);
        if (result.SubmissionError != null)
            payload["github_submission_error"] = result.SubmissionError;
        if (result.UpstreamUrl != null)
        {
            payload["upstream_url"] = result.UpstreamUrl;
            payload["github_issue_url"] = result.UpstreamUrl;
        }
        if (sampling?.Title != null)
            payload["sampled_title"] = sampling.Title;
        if (sampling?.Tags is { Length: > 0 })
            payload["sampled_tags"] = new JsonArray(sampling.Tags.Select(tag => JsonValue.Create(tag)).ToArray<JsonNode?>());
        if (evidencePaths is { Length: > 0 })
            payload["evidence_paths"] = new JsonArray(evidencePaths.Select(path => JsonValue.Create(path)).ToArray<JsonNode?>());
        return CreateToolResult(id, "Suggestion recorded. Thank you for the feedback.", payload);
    }

    private JsonObject CreateSourceCodeDetectedErrorResponse(
        JsonNode? id,
        string field,
        SourceCodeDetectionResult detection,
        string message)
    {
        var extraData = new JsonObject
        {
            ["source_code_rejection"] = new JsonObject
            {
                ["field"] = field,
                ["reason_code"] = detection.ReasonCode ?? "unknown",
                ["reason_code_counts"] = CreateSourceCodeReasonCounts(detection),
            },
        };
        return CreateToolErrorResponse(
            id,
            message,
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Describe the gap in natural language without including code.",
            retrySafe: false,
            extraData: extraData);
    }

    private static JsonObject CreateSourceCodeReasonCounts(SourceCodeDetectionResult detection)
    {
        var counts = new JsonObject();
        if (detection.ReasonCounts is null)
            return counts;

        foreach (var reason in detection.ReasonCounts)
        {
            if (reason.Value > 0)
                counts[reason.Key] = reason.Value;
        }

        return counts;
    }

    private static string[]? ReadEvidencePaths(JsonNode? node, out string? error)
    {
        error = null;
        if (node == null)
            return null;
        if (node is not JsonArray array)
        {
            error = "evidencePaths must be an array of path strings.";
            return null;
        }
        if (array.Count > SuggestionEvidencePaths.MaxCount)
        {
            error = $"evidencePaths has too many entries ({array.Count}, max {SuggestionEvidencePaths.MaxCount}).";
            return null;
        }

        var paths = new List<string>();
        foreach (var item in array)
        {
            string? path;
            try
            {
                path = item?.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                error = "evidencePaths must contain only path strings.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (!SuggestionEvidencePaths.TryNormalize(path, out var normalizedPath, out var pathError))
            {
                error = pathError;
                return null;
            }
            if (normalizedPath.Length > 0 && !paths.Contains(normalizedPath, StringComparer.Ordinal))
                paths.Add(normalizedPath);
        }

        return paths.Count == 0 ? null : paths.ToArray();
    }

    private static string ResolveGitHubSubmissionReason(SuggestionStore.AddAndSubmitResult result, bool githubTokenConfigured)
    {
        if (result.AlreadySubmitted || result.UpstreamUrl != null)
            return "submitted";
        if (!githubTokenConfigured)
            return "token_not_configured";
        if (result.SubmissionError != null)
            return StartsWithHttpStatusCode(result.SubmissionError) ? "api_error" : "network_error";
        return "repo_not_configured";
    }

    private static bool StartsWithHttpStatusCode(string value)
    {
        return value.Length >= 4
            && char.IsDigit(value[0])
            && char.IsDigit(value[1])
            && char.IsDigit(value[2])
            && value[3] == ':';
    }

    private sealed record SuggestionSamplingResult(string? Title, string[]? Tags);

    private sealed record SuggestionSamplingAttempt(SuggestionSamplingResult? Result, string? Diagnostic);

    private readonly record struct SuggestionSamplingDecision(
        bool ShouldRequestClient,
        string Status,
        string? Diagnostic);

    private static string RedactSuggestionSamplingInput(string value)
        => SuggestionStore.RedactSensitiveText(value, out _);

    private static SuggestionSamplingResult? RedactSuggestionSamplingResult(SuggestionSamplingResult? sampling)
    {
        if (sampling == null)
            return null;

        var title = SanitizeSampledTitle(RedactNullableSamplingValue(sampling.Title));
        var tags = sampling.Tags?
            .Select(RedactNullableSamplingValue)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(SanitizeSampledTag)
            .Where(t => t != null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();

        return title == null && (tags == null || tags.Length == 0)
            ? null
            : new SuggestionSamplingResult(title, tags is { Length: > 0 } ? tags : null);
    }

    private static string? RedactNullableSamplingValue(string? value)
        => value == null ? null : SuggestionStore.RedactSensitiveText(value, out _);

    private async Task<SuggestionSamplingAttempt> TrySampleSuggestionMetadataAsync(
        string category,
        string? language,
        string description,
        string? context,
        string? toolInvocationContext,
        SuggestionSamplingDecision samplingDecision)
    {
        if (!samplingDecision.ShouldRequestClient)
            return new SuggestionSamplingAttempt(null, null);

        var prompt = BuildSuggestionSamplingPrompt(category, language, description, context, toolInvocationContext);

        var result = await SendClientRequestAsync("sampling/createMessage", new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = prompt,
                    }
                }
            },
            ["maxTokens"] = 200,
        }, _currentRequestToken.Value).ConfigureAwait(false);

        var text = ExtractSamplingText(result);
        if (string.IsNullOrWhiteSpace(text))
            return new SuggestionSamplingAttempt(null, null);
        if (text.Length > MaxSamplingResponseTextChars)
            return new SuggestionSamplingAttempt(
                null,
                BuildSamplingRejectionDiagnostic(
                    $"Sampling response rejected: text length {text.Length.ToString(CultureInfo.InvariantCulture)} exceeds {MaxSamplingResponseTextChars.ToString(CultureInfo.InvariantCulture)} characters."));
        try
        {
            var parsed = BoundedJson.ParseNode(text, MaxSamplingResponseJsonBytes, MaxSamplingResponseJsonDepth);
            if (parsed is not JsonObject obj)
                return new SuggestionSamplingAttempt(null, BuildSamplingSchemaRejectionDiagnostic());

            var titleNode = obj["title"];
            var titleText = TryReadStringValue(titleNode);
            if (titleNode is not null && titleText is null)
                return new SuggestionSamplingAttempt(null, BuildSamplingSchemaRejectionDiagnostic());
            var title = SanitizeSampledTitle(RedactNullableSamplingValue(titleText));

            var tagsNode = obj["tags"];
            string[]? tags = null;
            if (tagsNode is JsonArray tagArray)
            {
                var tagList = new List<string>();
                foreach (var tagNode in tagArray)
                {
                    var tagText = TryReadStringValue(tagNode);
                    if (tagText is null)
                        return new SuggestionSamplingAttempt(null, BuildSamplingSchemaRejectionDiagnostic());
                    if (string.IsNullOrWhiteSpace(tagText))
                        continue;
                    var tag = SanitizeSampledTag(RedactNullableSamplingValue(tagText));
                    if (tag is not null && !tagList.Contains(tag, StringComparer.Ordinal))
                        tagList.Add(tag);
                    if (tagList.Count >= 6)
                        break;
                }
                tags = tagList.Count > 0 ? tagList.ToArray() : null;
            }
            else if (tagsNode is not null)
            {
                return new SuggestionSamplingAttempt(null, BuildSamplingSchemaRejectionDiagnostic());
            }

            if (title == null && (tags == null || tags.Length == 0))
                return new SuggestionSamplingAttempt(null, BuildSamplingSchemaRejectionDiagnostic());
            return new SuggestionSamplingAttempt(new SuggestionSamplingResult(title, tags is { Length: > 0 } ? tags : null), null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            var detail = ex is JsonException jsonException
                ? JsonFrameParser.FormatExceptionDetail(jsonException)
                : CommandErrorWriter.FormatSanitizedExceptionMessage(ex);
            return new SuggestionSamplingAttempt(
                null,
                BuildSamplingRejectionDiagnostic(
                    $"Sampling response JSON rejected: {detail}."));
        }
    }

    private static string BuildSamplingSchemaRejectionDiagnostic()
        => BuildSamplingRejectionDiagnostic(
            "Sampling response schema rejected: expected compact JSON with optional title string and tags array containing strings.");

    private static string BuildSamplingRejectionDiagnostic(string diagnostic)
        => DiagnosticRedactor.BoundDiagnosticText(diagnostic, 240);

    private static string BuildSuggestionSamplingPrompt(
        string category,
        string? language,
        string description,
        string? context,
        string? toolInvocationContext)
    {
        var prompt = new StringBuilder();
        var remainingBytes = MaxSamplingPromptBytes;
        AppendSamplingPromptLine(prompt, "Extract structured metadata for a cdidx improvement suggestion.", ref remainingBytes);
        AppendSamplingPromptLine(prompt, "Return only compact JSON with keys: title (one line, <=80 chars) and tags (array of 1-6 lowercase identifiers).", ref remainingBytes);
        AppendSamplingPromptLine(prompt, "Do not include source code.", ref remainingBytes);
        AppendSamplingPromptField(prompt, "category", category, MaxSamplingShortFieldChars, ref remainingBytes);
        if (!string.IsNullOrWhiteSpace(language))
            AppendSamplingPromptField(prompt, "language", language, MaxSamplingShortFieldChars, ref remainingBytes);
        AppendSamplingPromptField(prompt, "description", description, MaxSamplingDescriptionChars, ref remainingBytes);
        if (!string.IsNullOrWhiteSpace(context))
            AppendSamplingPromptField(prompt, "context", context, MaxSamplingContextChars, ref remainingBytes);
        if (!string.IsNullOrWhiteSpace(toolInvocationContext))
        {
            var summary = SummarizeToolInvocationContextForSampling(toolInvocationContext);
            AppendSamplingPromptField(prompt, "tool_invocation_context", summary, MaxSamplingToolInvocationSummaryChars, ref remainingBytes);
        }

        return prompt.ToString();
    }

    private static void AppendSamplingPromptField(StringBuilder prompt, string name, string value, int maxChars, ref int remainingBytes)
    {
        var sanitized = SanitizeSamplingPromptField(value, maxChars);
        if (sanitized.Length == 0)
            return;
        AppendSamplingPromptLine(prompt, $"{name}: {sanitized}", ref remainingBytes);
    }

    private static void AppendSamplingPromptLine(StringBuilder prompt, string line, ref int remainingBytes)
    {
        if (remainingBytes <= 0)
            return;

        var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
        if (lineBytes > remainingBytes)
        {
            const string suffix = " ... [truncated]";
            var suffixBytes = Encoding.UTF8.GetByteCount(suffix);
            var prefixBudget = remainingBytes - suffixBytes - 1;
            if (prefixBudget <= 0)
                return;
            line = TruncateUtf8(line, prefixBudget).TrimEnd() + suffix;
            lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (lineBytes > remainingBytes)
                return;
        }

        prompt.Append(line);
        prompt.Append('\n');
        remainingBytes -= lineBytes;
    }

    private static string SanitizeSamplingPromptField(string value, int maxChars)
    {
        var collapsed = CollapseSamplingPromptWhitespace(value);
        if (collapsed.Length <= maxChars)
            return collapsed;
        var end = Math.Min(maxChars, collapsed.Length);
        if (end > 0 && char.IsHighSurrogate(collapsed[end - 1]))
            end--;
        return collapsed[..end].TrimEnd() + " ... [truncated]";
    }

    private static string CollapseSamplingPromptWhitespace(string value)
    {
        var trimmed = value.Trim();
        var collapsed = new StringBuilder(trimmed.Length);
        var previousWhitespace = false;
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace)
                    collapsed.Append(' ');
                previousWhitespace = true;
                continue;
            }

            collapsed.Append(ch);
            previousWhitespace = false;
        }

        return collapsed.ToString().Trim();
    }

    private static string SummarizeToolInvocationContextForSampling(string value)
    {
        var trimmed = value.Trim();
        var lineCount = CountLogicalLines(trimmed);
        var byteCount = Encoding.UTF8.GetByteCount(trimmed);
        return $"provided; {trimmed.Length} chars; {byteCount} UTF-8 bytes; {lineCount} line(s); raw content withheld";
    }

    private static int CountLogicalLines(string value)
    {
        if (value.Length == 0)
            return 0;
        var lines = 1;
        foreach (var ch in value)
        {
            if (ch == '\n')
                lines++;
        }
        return lines;
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (maxBytes <= 0)
            return string.Empty;
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            var bytes = Encoding.UTF8.GetByteCount(value.AsSpan(0, mid));
            if (bytes <= maxBytes)
                low = mid;
            else
                high = mid - 1;
        }

        if (low > 0 && char.IsHighSurrogate(value[low - 1]))
            low--;
        return value[..low];
    }

    private bool HasClientCapability(string name)
        => name switch
        {
            "roots" => _clientSupportsRoots,
            "sampling" => _clientSupportsSampling,
            _ => _clientCapabilities is JsonObject obj
                && obj.TryGetPropertyValue(name, out var node)
                && node is not null,
        };

    private SuggestionSamplingDecision ResolveSuggestionSamplingDecision()
    {
        var sampling = McpEnvironment.ReadOptInSwitch(SamplingEnabledEnvironmentVariable);
        if (sampling.State == McpEnvironmentSwitchState.Unset)
        {
            return new SuggestionSamplingDecision(
                false,
                "disabled",
                $"{SamplingEnabledEnvironmentVariable} is unset; suggestion metadata sampling requires explicit opt-in with true, 1, yes, or on.");
        }

        if (sampling.IsEnabled)
        {
            return HasClientCapability("sampling")
                ? new SuggestionSamplingDecision(true, "enabled", null)
                : new SuggestionSamplingDecision(
                    false,
                    "client_capability_missing",
                    "Client did not advertise MCP sampling capability; suggestion metadata sampling skipped.");
        }

        if (sampling.IsDisabled)
        {
            return new SuggestionSamplingDecision(
                false,
                "disabled",
                $"{SamplingEnabledEnvironmentVariable} is set to an opt-out value; suggestion metadata sampling disabled.");
        }

        return new SuggestionSamplingDecision(
            false,
            "disabled",
            $"{SamplingEnabledEnvironmentVariable} contains an unrecognized value; suggestion metadata sampling disabled. Use true, 1, yes, or on to enable.");
    }

    private static void AddSuggestionSamplingDiagnostics(
        JsonObject payload,
        SuggestionSamplingDecision samplingDecision,
        SuggestionSamplingResult? sampling,
        string? samplingRejectionDiagnostic)
    {
        payload["sampling_status"] = sampling != null
            ? "sampled"
            : samplingRejectionDiagnostic != null
                ? "sampling_rejected"
                : samplingDecision.Status;
        if (samplingRejectionDiagnostic != null)
            payload["sampling_diagnostic"] = samplingRejectionDiagnostic;
        else if (samplingDecision.Diagnostic != null)
            payload["sampling_diagnostic"] = samplingDecision.Diagnostic;
    }

    private static string? ExtractSamplingText(JsonNode? result)
    {
        if (result is null)
            return null;
        if (TryReadStringValue(result["content"]?["text"]) is { Length: > 0 } contentText)
            return contentText;
        if (result["content"] is JsonArray contentArray)
        {
            foreach (var item in contentArray)
            {
                if (TryReadStringValue(item?["text"]) is { Length: > 0 } itemText)
                    return itemText;
            }
        }
        return TryReadStringValue(result["text"]);
    }

    private static string? SanitizeSampledTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        title = title.Trim();
        return title.Length <= 80 ? title : title[..80];
    }

    private static string? SanitizeSampledTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var normalized = new string(tag.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray()).Trim('_');
        return normalized.Length == 0 ? null : normalized.Length <= 40 ? normalized : normalized[..40];
    }

    private static bool TryProbeCdidxDirectoryWritable(string cdidxDir, out string? error)
    {
        var probePath = Path.Combine(cdidxDir, $".write_probe.{Guid.NewGuid():N}.tmp");
        var createdProbe = false;
        try
        {
            FileWriteProbe.WriteEmptyFile(probePath);
            createdProbe = true;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Cannot write to .cdidx directory {cdidxDir}; check directory ownership, permissions, and read-only mounts.";
            return false;
        }
        finally
        {
            if (createdProbe)
                TryDeleteCdidxDirectoryWritableProbe(probePath);
        }
    }

    private static void TryDeleteCdidxDirectoryWritableProbe(string probePath)
    {
        try
        {
            if (!File.Exists(probePath))
                return;

            if (DeleteCdidxDirectoryWritableProbeForTesting != null)
                DeleteCdidxDirectoryWritableProbeForTesting(probePath);
            else
                File.Delete(probePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CommandErrorWriter.WriteStderr($"Warning: failed to delete .cdidx writable probe {ConsoleUi.FormatBoundedValue(probePath)} ({CommandErrorWriter.FormatSanitizedException(ex)}).");
        }
    }

    internal static Action<string>? DeleteCdidxDirectoryWritableProbeForTesting { get; set; }

    private string ResolveSuggestionAgent()
    {
        return string.IsNullOrWhiteSpace(_caller) ? "unknown" : _caller;
    }

}
