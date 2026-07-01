using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    // Issue kinds emitted by FileIndexer.ValidateFileContent for `validate --kind` filtering.
    // Keep in sync with `Kind = "..."` assignments in FileIndexer.cs so typos like
    // `--kind replacement_chra` produce a did-you-mean hint instead of silently filtering
    // to zero results (#1582).
    // FileIndexer.ValidateFileContent が出力する file_issues 行の Kind 一覧。
    // `--kind replacement_chra` のようなタイプミスを did-you-mean で救うため、
    // FileIndexer.cs 内の `Kind = "..."` 代入と同期させる (#1582)。
    private static readonly string[] AllValidValidateKinds =
        ["bom", "cr_only_line_endings", "file_too_large", "fts_token_too_long", "line_too_long", "mixed_line_endings", "mixed_line_endings_three_way", "non_utf8_likely", "null_byte", "replacement_char", "utf16_bom"];
    private static readonly string[] AllValidValidateSeverities =
        ["error", FileIssue.SeverityInfo, FileIssue.SeverityWarning];

    public static int RunValidate(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("validate", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("validate", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("validate")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "validate", jsonOptions))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("validate", options))
            return CommandExitCodes.UsageError;
        if (options.Severity != null && !AllValidValidateSeverities.Contains(options.Severity, StringComparer.Ordinal))
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                $"unsupported validate severity '{options.Severity}'.",
                CommandExitCodes.UsageError,
                "use one of: info, warning, error.",
                "cdidx validate [--severity <info|warning|error>]");
        }

        return WithDb(options, jsonOptions, reader =>
        {
            var issueLimit = HasOption(cmdArgs, "--limit") || HasOption(cmdArgs, "--top")
                ? options.Limit
                : (int?)null;
            var issues = reader.GetIssues(
                options.Kind,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                issueLimit,
                options.Severity);
            AnnotateValidateIssues(issues);
            var issuesAvailable = reader._hasIssuesTable;
            if (options.CountOnly || options.OutputFormat == OutputFormatCount)
            {
                WriteFormattedCount(issues.Count, jsonOptions);
                return CommandExitCodes.Success;
            }
            if (issues.Count == 0)
            {
                if (options.Json)
                {
                    if (options.OutputFormat == OutputFormatCompact)
                    {
                        WriteValidateCompactJson(issues, issuesAvailable, jsonOptions);
                        return CommandExitCodes.Success;
                    }
                    if (options.OutputFormat == OutputFormatJson && options.JsonOutputFormat == JsonOutputFormatArray)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(
                            new List<FileIssue>(),
                            CliJsonSerializerContextFactory.Create(jsonOptions).ListFileIssue));
                        return CommandExitCodes.Success;
                    }
                    if (TryWriteEmptyFormattedResult(options, jsonOptions))
                        return CommandExitCodes.Success;
                    Console.WriteLine(BuildValidateJsonPayload(issues, issuesAvailable, jsonOptions).ToJsonString(jsonOptions));
                }
                else if (!issuesAvailable)
                    CommandErrorWriter.WriteStderr("WARN: file_issues table missing in this index (legacy or read-only DB) — validate output is degraded, not a real clean signal.");
                else
                {
                    CommandErrorWriter.WriteStderr("No encoding issues found.");
                    WriteValidateKindHint(options.Kind);
                }
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                if (options.OutputFormat == OutputFormatCompact)
                {
                    WriteValidateCompactJson(issues, issuesAvailable, jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (TryWriteFormattedLocations(
                    options,
                    issues.Select(i => new FormattedLocation(i.Path, i.Line, null, $"{i.Kind}: {i.Message}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(issues.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(issues.Select(i => (i.Path, i.Line, 1, $"{i.Kind}: {i.Message}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(issues.Select(i => (i.Path, i.Line, 1, i.Message, i.Kind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatJson && options.JsonOutputFormat == JsonOutputFormatArray)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        issues,
                        CliJsonSerializerContextFactory.Create(jsonOptions).ListFileIssue));
                    return CommandExitCodes.Success;
                }
                Console.WriteLine(BuildValidateJsonPayload(issues, issuesAvailable, jsonOptions).ToJsonString(jsonOptions));
            }
            else
            {
                foreach (var issue in issues)
                {
                    var location = issue.Line > 0 ? $":{issue.Line}" : "";
                    var metadata = FormatValidateIssueMetadata(issue);
                    Console.WriteLine($"  {issue.Kind,-20} {issue.Path}{location}  {metadata}{issue.Message}");
                }
                var kindCounts = issues.GroupBy(i => i.Kind).Select(g => $"{g.Key}: {g.Count()}");
                CommandErrorWriter.WriteStderr($"\n({issues.Count} issues: {string.Join(", ", kindCounts)})");
                WriteValidateHumanSummary(issues);
            }
            return CommandExitCodes.Success;
        });
    }

    internal static void AnnotateValidateIssues(IEnumerable<FileIssue> issues)
    {
        foreach (var issue in issues)
        {
            issue.Category = CategorizeValidateIssue(issue);
            issue.Actionable = IsValidateIssueActionable(issue);
        }
    }

    internal static JsonObject BuildValidateIssueSummary(IReadOnlyList<FileIssue> issues)
    {
        var actionable = issues.Count(IsValidateIssueActionable);
        return new JsonObject
        {
            ["total"] = issues.Count,
            ["actionable"] = actionable,
            ["informational"] = issues.Count - actionable,
            ["actionability"] = issues.Count == 0
                ? "clean"
                : actionable == 0
                    ? "informational_only"
                    : actionable == issues.Count
                        ? "actionable"
                        : "mixed",
            ["by_kind"] = BuildValidateCountObject(issues, static issue => issue.Kind),
            ["by_severity"] = BuildValidateCountObject(issues, static issue => issue.Severity),
            ["by_origin"] = BuildValidateCountObject(issues, static issue => issue.Origin),
            ["by_category"] = BuildValidateCountObject(issues, static issue => issue.Category),
        };
    }

    private static JsonObject BuildValidateJsonPayload(IReadOnlyList<FileIssue> issues, bool issuesAvailable, JsonSerializerOptions jsonOptions)
    {
        return new JsonObject
        {
            ["count"] = issues.Count,
            ["summary"] = BuildValidateIssueSummary(issues),
            ["issues"] = JsonSerializer.SerializeToNode(issues, CliJsonSerializerContextFactory.Create(jsonOptions).ListFileIssue),
            ["issues_table_available"] = issuesAvailable,
            ["degraded"] = !issuesAvailable,
        };
    }

    private static void WriteValidateCompactJson(IReadOnlyList<FileIssue> issues, bool issuesAvailable, JsonSerializerOptions jsonOptions)
    {
        Console.WriteLine(new JsonObject
        {
            ["format"] = OutputFormatCompact,
            ["count"] = issues.Count,
            ["summary"] = BuildValidateIssueSummary(issues),
            ["issues"] = BuildCompactValidateIssues(issues),
            ["issues_table_available"] = issuesAvailable,
            ["degraded"] = !issuesAvailable,
        }.ToJsonString(jsonOptions));
    }

    private static JsonArray BuildCompactValidateIssues(IEnumerable<FileIssue> issues)
    {
        var compact = new JsonArray();
        foreach (var issue in issues)
        {
            compact.Add(new JsonObject
            {
                ["path"] = issue.Path,
                ["line"] = issue.Line,
                ["kind"] = issue.Kind,
                ["severity"] = issue.Severity,
                ["origin"] = issue.Origin,
                ["category"] = issue.Category,
                ["actionable"] = issue.Actionable,
                ["message"] = issue.Message,
            });
        }
        return compact;
    }

    private static JsonObject BuildValidateCountObject(IEnumerable<FileIssue> issues, Func<FileIssue, string?> selector)
    {
        var counts = new JsonObject();
        foreach (var group in issues
                     .GroupBy(issue => NormalizeValidateSummaryKey(selector(issue)), StringComparer.Ordinal)
                     .Select(group => new { Key = group.Key, Count = group.Count() })
                     .OrderByDescending(group => group.Count)
                     .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            counts[group.Key] = group.Count;
        }
        return counts;
    }

    private static string NormalizeValidateSummaryKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unspecified" : value;

    private static string CategorizeValidateIssue(FileIssue issue)
    {
        return issue.Kind switch
        {
            "replacement_char" when string.Equals(issue.Origin, FileIssue.OriginSourceLiteral, StringComparison.Ordinal)
                && IsValidateTestOrFixturePath(issue.Path) => FileIssue.CategoryExpectedFixtureLiteral,
            "replacement_char" when string.Equals(issue.Origin, FileIssue.OriginSourceLiteral, StringComparison.Ordinal) => FileIssue.CategoryIntentionalSourceLiteral,
            "replacement_char" or "non_utf8_likely" or "utf16_heuristic" => FileIssue.CategoryDecodingRisk,
            "bom" or "utf16_bom" => FileIssue.CategoryByteOrderMark,
            "mixed_line_endings" or "mixed_line_endings_three_way" or "cr_only_line_endings" => FileIssue.CategoryLineEndings,
            "null_byte" => FileIssue.CategoryRawBytes,
            "file_too_large" or "fts_token_too_long" or "line_too_long" or "symbol_count_exceeded" or "reference_count_exceeded" => FileIssue.CategoryContentLimit,
            "lfs_pointer_skipped" or "conflict_markers" or "dockerfile_json_form_invalid" or "dockerfile_json_form_truncated" or "dockerfile_json_form_issue_limit_reached" or "xml_structure_invalid" => FileIssue.CategoryContentStructure,
            _ => FileIssue.CategoryOther,
        };
    }

    private static bool IsValidateIssueActionable(FileIssue issue)
    {
        var category = issue.Category ?? CategorizeValidateIssue(issue);
        if (category is FileIssue.CategoryExpectedFixtureLiteral or FileIssue.CategoryIntentionalSourceLiteral)
            return false;
        return !string.Equals(issue.Severity, FileIssue.SeverityInfo, StringComparison.Ordinal);
    }

    private static void WriteValidateHumanSummary(IReadOnlyList<FileIssue> issues)
    {
        var actionable = issues.Count(IsValidateIssueActionable);
        var parts = new List<string>
        {
            $"actionable: {actionable}",
            $"informational: {issues.Count - actionable}",
            $"severity: {FormatValidateSummaryCounts(issues, static issue => issue.Severity)}",
            $"category: {FormatValidateSummaryCounts(issues, static issue => issue.Category)}",
        };
        var origins = FormatValidateSummaryCounts(issues, static issue => issue.Origin, includeUnspecified: false);
        if (origins.Length > 0)
            parts.Add($"origin: {origins}");

        CommandErrorWriter.WriteStderr($"Summary: {string.Join("; ", parts)}");
    }

    private static string FormatValidateSummaryCounts(
        IEnumerable<FileIssue> issues,
        Func<FileIssue, string?> selector,
        bool includeUnspecified = true)
    {
        return string.Join(
            ", ",
            issues
                .Select(issue => selector(issue))
                .Where(value => includeUnspecified || !string.IsNullOrWhiteSpace(value))
                .Select(NormalizeValidateSummaryKey)
                .GroupBy(value => value, StringComparer.Ordinal)
                .Select(group => new { Key = group.Key, Count = group.Count() })
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}: {group.Count}"));
    }

    private static string FormatValidateIssueMetadata(FileIssue issue)
    {
        var tags = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(issue.Severity))
            tags.Add(issue.Severity);
        if (!string.IsNullOrWhiteSpace(issue.Origin))
            tags.Add(issue.Origin);
        if (IsValidateTestOrFixturePath(issue.Path))
            tags.Add("test_fixture");

        return tags.Count == 0
            ? string.Empty
            : $"[{string.Join(", ", tags)}] ";
    }

    private static bool IsValidateTestOrFixturePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lower = normalized.ToLowerInvariant();
        return lower.StartsWith("test/", StringComparison.Ordinal)
            || lower.StartsWith("tests/", StringComparison.Ordinal)
            || lower.StartsWith("fixture/", StringComparison.Ordinal)
            || lower.StartsWith("fixtures/", StringComparison.Ordinal)
            || lower.Contains("/test/", StringComparison.Ordinal)
            || lower.Contains("/tests/", StringComparison.Ordinal)
            || lower.Contains("/fixture/", StringComparison.Ordinal)
            || lower.Contains("/fixtures/", StringComparison.Ordinal)
            || lower.StartsWith("test.", StringComparison.Ordinal)
            || lower.StartsWith("tests.", StringComparison.Ordinal)
            || lower.StartsWith("test_", StringComparison.Ordinal)
            || lower.StartsWith("tests_", StringComparison.Ordinal)
            || lower.Contains("/test.", StringComparison.Ordinal)
            || lower.Contains("/tests.", StringComparison.Ordinal)
            || lower.Contains("/test_", StringComparison.Ordinal)
            || lower.Contains("/tests_", StringComparison.Ordinal)
            || lower.Contains("_test.", StringComparison.Ordinal)
            || lower.Contains("_tests.", StringComparison.Ordinal)
            || lower == "conftest.py"
            || lower.EndsWith("/conftest.py", StringComparison.Ordinal)
            || lower.Contains(".spec.", StringComparison.Ordinal)
            || lower.Contains(".test.", StringComparison.Ordinal);
    }
}
