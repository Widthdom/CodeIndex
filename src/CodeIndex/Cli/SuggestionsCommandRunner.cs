using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

internal static class SuggestionsCommandRunner
{
    private const string Usage = "Usage: cdidx suggestions <list|show|export> [id] [--db <path>] [--json] [--status <all|draft|submitted_pending_triage|open_in_upstream|resolved_in_upstream|wont_fix|duplicate|superseded|submitted|unsubmitted>] [--language <lang>] [--category <category>] [--since <datetime>] [--agent <name>] [--limit <n>] [--offset <n>] [--format <json|markdown|issue-drafts>] [--open-issues <path|github|github:owner/name>] [--repo <owner/name>] [--duplicate-confidence <low|medium|high>|--duplicate-threshold <score>]";
    internal const int MaxOpenIssuesJsonBytes = IssueDuplicatePreflight.MaxOpenIssuesJsonBytes;
    internal const int MaxOpenIssuesJsonDepth = IssueDuplicatePreflight.MaxOpenIssuesJsonDepth;
    internal const int MaxSuggestionExportTextFieldLength = 4096;
    internal const int MaxSuggestionIssueDraftBodyLength = 24 * 1024;
    private const string SuggestionOutputTruncationMarker = "\n[truncated]";

    public static int Run(string[] args, JsonSerializerOptions jsonOptions)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? CommandExitCodes.UsageError : CommandExitCodes.Success;
        }

        var verb = args[0];
        var options = Parse(args[1..]);
        if (options.Error != null)
        {
            CommandErrorWriter.WriteStderr(options.Error);
            CommandErrorWriter.WriteStderr(Usage);
            return CommandExitCodes.UsageError;
        }
        if ((options.DuplicateConfidenceSpecified || options.DuplicateThresholdSpecified)
            && (verb != "export" || options.ExportFormat != "issue-drafts"))
            return WriteUsageError("--duplicate-confidence and --duplicate-threshold can only be used with `suggestions export --format issue-drafts`.");
        if (options.OpenIssuesPath != null && (verb != "export" || options.ExportFormat != "issue-drafts"))
            return WriteUsageError("--open-issues can only be used with `suggestions export --format issue-drafts`.");
        if (options.OpenIssuesRepository != null && (verb != "export" || options.ExportFormat != "issue-drafts"))
            return WriteUsageError("--repo can only be used with `suggestions export --format issue-drafts --open-issues github`.");
        if (verb == "show" && options.HasPagination)
            return WriteUsageError("--limit and --offset can only be used with `suggestions list` or `suggestions export`.");

        var store = CreateStore(options.DbPath);
        var records = ApplyFilters(store.LoadAll(), options)
            .OrderByDescending(s => s.CreatedAt)
            .ThenBy(s => s.Hash, StringComparer.Ordinal)
            .ToList();
        var outputRecords = verb is "list" or "export"
            ? ApplyOutputPage(records, options)
            : records;

        return verb switch
        {
            "list" => RunList(outputRecords, options, jsonOptions),
            "show" => RunShow(records, options, jsonOptions),
            "export" => RunExport(outputRecords, options, jsonOptions),
            _ => WriteUsageError($"Unknown suggestions subcommand: {verb}")
        };
    }

    private static int RunList(List<SuggestionRecord> records, Options options, JsonSerializerOptions jsonOptions)
    {
        if (options.Json)
        {
            foreach (var item in records.Select(ToListItem))
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    item,
                    CliJsonSerializerContextFactory.Create(jsonOptions).SuggestionListItemJsonResult));
            }
            return CommandExitCodes.Success;
        }

        if (records.Count == 0)
        {
            CommandErrorWriter.WriteStderr("No suggestions found.");
            return CommandExitCodes.NotFound;
        }

        foreach (var record in records)
        {
            var id = ShortId(record.Hash);
            var status = GetStatus(record);
            var language = string.IsNullOrWhiteSpace(record.Language) ? "-" : record.Language;
            Console.WriteLine($"{id}  {record.CreatedAt:yyyy-MM-ddTHH:mm:ssZ}  {status,-11}  {record.Category,-20}  {language,-10}  {FormatTitle(record.Description, 80)}");
        }

        return CommandExitCodes.Success;
    }

    private static int RunShow(List<SuggestionRecord> records, Options options, JsonSerializerOptions jsonOptions)
    {
        if (string.IsNullOrWhiteSpace(options.Id))
            return WriteUsageError("suggestions show requires an id.");

        var record = ResolveById(records, options.Id);
        if (record == null)
        {
            Console.Error.WriteLine($"Suggestion not found: {options.Id}");
            return CommandExitCodes.NotFound;
        }

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                ToDetail(record),
                CliJsonSerializerContextFactory.Create(jsonOptions).SuggestionDetailJsonResult));
            return CommandExitCodes.Success;
        }

        Console.WriteLine($"id: {record.Hash}");
        Console.WriteLine($"created_at: {record.CreatedAt:O}");
        Console.WriteLine($"status: {GetStatus(record)}");
        Console.WriteLine($"category: {record.Category}");
        Console.WriteLine($"language: {record.Language ?? "-"}");
        var agent = GetAgent(record);
        if (!string.IsNullOrWhiteSpace(agent))
            Console.WriteLine($"agent: {agent}");
        if (!string.IsNullOrWhiteSpace(record.McpClientName))
            Console.WriteLine($"mcp_client: {record.McpClientName}{(string.IsNullOrWhiteSpace(record.McpClientVersion) ? string.Empty : " " + record.McpClientVersion)}");
        if (!string.IsNullOrWhiteSpace(record.ClientVersion) && record.ClientVersion != "unknown")
            Console.WriteLine($"cdidx_version: {record.ClientVersion}");
        if (!string.IsNullOrWhiteSpace(record.SessionId) && record.SessionId != "unknown")
            Console.WriteLine($"session_id: {record.SessionId}");
        Console.WriteLine($"submitted_to_github: {IsSubmitted(record).ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(record.UpstreamUrl))
            Console.WriteLine($"upstream_url: {record.UpstreamUrl}");
        if (record.UpstreamIssueNumber != null)
            Console.WriteLine($"upstream_issue_number: {record.UpstreamIssueNumber}");
        var evidencePaths = NormalizeEvidencePaths(record);
        if (evidencePaths.Count > 0)
        {
            Console.WriteLine("evidence_paths:");
            foreach (var path in evidencePaths)
                Console.WriteLine($"- {path}");
        }
        Console.WriteLine();
        Console.WriteLine(record.Description);
        if (!string.IsNullOrWhiteSpace(record.Context))
        {
            Console.WriteLine();
            Console.WriteLine("context:");
            Console.WriteLine(record.Context);
        }

        return CommandExitCodes.Success;
    }

    private static int RunExport(List<SuggestionRecord> records, Options options, JsonSerializerOptions jsonOptions)
    {
        if (options.ExportFormat == "markdown")
        {
            Console.WriteLine(FormatMarkdown(records));
            return CommandExitCodes.Success;
        }
        if (options.ExportFormat == "issue-drafts")
            return RunIssueDraftExport(records, options, jsonOptions);

        var payload = new SuggestionExportJsonResult(
            JsonOutputContract.ApiVersion,
            records.Count,
            records.Select(ToExportDetail).ToList());
        Console.WriteLine(JsonSerializer.Serialize(
            payload,
            CliJsonSerializerContextFactory.Create(jsonOptions).SuggestionExportJsonResult));
        return CommandExitCodes.Success;
    }

    private static int RunIssueDraftExport(List<SuggestionRecord> records, Options options, JsonSerializerOptions jsonOptions)
    {
        if (!IssueDuplicatePreflight.TryLoad(options.OpenIssuesPath, options.OpenIssuesRepository, out var preflight, out var error))
            return WriteUsageError(error!);

        var drafts = records.Select(record => ToIssueDraft(record, preflight, options)).ToList();
        var payload = new SuggestionIssueDraftExportJsonResult(
            JsonOutputContract.ApiVersion,
            drafts.Count,
            new SuggestionIssueDraftPreflightSummaryJsonResult(
                preflight.Checked,
                preflight.Source,
                preflight.OpenIssueCount,
                options.DuplicateConfidence,
                options.DuplicateThreshold),
            drafts);
        Console.WriteLine(JsonSerializer.Serialize(
            payload,
            CliJsonSerializerContextFactory.Create(jsonOptions).SuggestionIssueDraftExportJsonResult));
        return CommandExitCodes.Success;
    }

    private static SuggestionStore CreateStore(string? dbPath)
    {
        var normalizedDbPath = string.IsNullOrWhiteSpace(dbPath)
            ? DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null).DbPath
            : DbPathResolver.NormalizeDbPath(dbPath);
        var fullDbPath = Path.GetFullPath(normalizedDbPath);
        var cdidxDir = Path.GetDirectoryName(fullDbPath) ?? Path.Combine(Environment.CurrentDirectory, ".cdidx");
        var dbName = Path.GetFileNameWithoutExtension(fullDbPath);
        return new SuggestionStore(cdidxDir, dbName);
    }

    private static IEnumerable<SuggestionRecord> ApplyFilters(IEnumerable<SuggestionRecord> records, Options options)
    {
        foreach (var record in records)
        {
            if (options.Status != "all" && !MatchesStatus(record, options.Status))
                continue;
            if (options.Language != null && !string.Equals(record.Language, options.Language, StringComparison.OrdinalIgnoreCase))
                continue;
            if (options.Category != null && !string.Equals(record.Category, options.Category, StringComparison.OrdinalIgnoreCase))
                continue;
            if (options.Agent != null && !MatchesAgent(record, options.Agent))
                continue;
            if (options.Since != null && new DateTimeOffset(DateTime.SpecifyKind(record.CreatedAt, DateTimeKind.Utc)) < options.Since.Value)
                continue;
            yield return record;
        }
    }

    private static List<SuggestionRecord> ApplyOutputPage(List<SuggestionRecord> records, Options options)
    {
        if (options.Offset == 0 && options.Limit == null)
            return records;

        var page = records.AsEnumerable();
        if (options.Offset > 0)
            page = page.Skip(options.Offset);
        if (options.Limit.HasValue)
            page = page.Take(options.Limit.Value);
        return page.ToList();
    }

    private static SuggestionRecord? ResolveById(List<SuggestionRecord> records, string id)
    {
        var matches = records
            .Where(r => r.Hash.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : records.FirstOrDefault(r => string.Equals(r.Hash, id, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetStatus(SuggestionRecord record) => ToSnakeCase(record.Status);

    private static bool IsSubmitted(SuggestionRecord record) =>
        record.Status != SuggestionStatus.Draft
        || record.SubmittedToGitHub == true
        || !string.IsNullOrWhiteSpace(record.UpstreamUrl)
        || !string.IsNullOrWhiteSpace(record.GitHubIssueUrl);

    private static bool MatchesStatus(SuggestionRecord record, string status)
    {
        if (status == "submitted")
            return IsSubmitted(record);
        if (status == "unsubmitted")
            return !IsSubmitted(record);
        return string.Equals(GetStatus(record), status, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToSnakeCase(SuggestionStatus status) => status switch
    {
        SuggestionStatus.Draft => "draft",
        SuggestionStatus.SubmittedPendingTriage => "submitted_pending_triage",
        SuggestionStatus.OpenInUpstream => "open_in_upstream",
        SuggestionStatus.ResolvedInUpstream => "resolved_in_upstream",
        SuggestionStatus.WontFix => "wont_fix",
        SuggestionStatus.Duplicate => "duplicate",
        SuggestionStatus.Superseded => "superseded",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static bool IsValidStatusFilter(string status) =>
        status is "all" or "submitted" or "unsubmitted" or "draft" or "submitted_pending_triage" or "open_in_upstream" or "resolved_in_upstream" or "wont_fix" or "duplicate" or "superseded";

    private static string? GetAgent(SuggestionRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Agent))
            return record.Agent;
        if (!string.IsNullOrWhiteSpace(record.CreatedByAgent) && record.CreatedByAgent != "unknown")
            return record.CreatedByAgent;
        return record.McpClientName;
    }

    private static bool MatchesAgent(SuggestionRecord record, string agent)
    {
        return string.Equals(record.Agent, agent, StringComparison.OrdinalIgnoreCase)
            || string.Equals(record.CreatedByAgent, agent, StringComparison.OrdinalIgnoreCase)
            || string.Equals(record.McpClientName, agent, StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortId(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static string FormatTitle(string description, int maxLength)
    {
        var firstLine = description.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return firstLine.Length <= maxLength ? firstLine : firstLine[..(maxLength - 1)] + "...";
    }

    private static SuggestionListItemJsonResult ToListItem(SuggestionRecord record) => new(
        JsonOutputContract.ApiVersion,
        record.Hash,
        ShortId(record.Hash),
        record.CreatedAt,
        GetStatus(record),
        record.Category,
        record.Language,
        GetAgent(record),
        record.CreatedByAgent,
        record.ClientVersion,
        record.McpClientName,
        record.McpClientVersion,
        FormatTitle(record.Description, 120),
        IsSubmitted(record),
        record.UpstreamUrl,
        record.UpstreamIssueNumber,
        record.LastSubmitAttempt,
        record.SubmitAttemptCount,
        record.LastSubmitError);

    private static SuggestionDetailJsonResult ToDetail(SuggestionRecord record) => ToDetail(record, capTextFields: false);

    private static SuggestionDetailJsonResult ToExportDetail(SuggestionRecord record) => ToDetail(record, capTextFields: true);

    private static SuggestionDetailJsonResult ToDetail(SuggestionRecord record, bool capTextFields) => new(
        JsonOutputContract.ApiVersion,
        record.Hash,
        record.CreatedAt,
        GetStatus(record),
        record.Category,
        record.Language,
        GetAgent(record),
        record.CreatedByAgent,
        record.SessionId,
        record.ClientVersion,
        record.McpClientName,
        record.McpClientVersion,
        BoundSuggestionOutputValue(record.ToolInvocationContext, capTextFields),
        RedactSuggestionOutputValue(record.SampledTitle),
        RedactSuggestionOutputArray(record.SampledTags),
        NormalizeEvidencePaths(record),
        BoundSuggestionOutputValue(record.Description, capTextFields) ?? string.Empty,
        BoundSuggestionOutputValue(record.Context, capTextFields),
        IsSubmitted(record),
        record.UpstreamUrl,
        record.UpstreamIssueNumber,
        record.LastSyncedAt,
        record.ResolvedAt,
        record.Supersedes,
        record.SupersededBy,
        record.LastSubmitAttempt,
        record.SubmitAttemptCount,
        record.LastSubmitError);

    private static SuggestionIssueDraftJsonResult ToIssueDraft(SuggestionRecord record, IssueDuplicatePreflight preflight, Options options)
    {
        var title = BuildIssueDraftTitle(record);
        var labels = GitHubIssueReporter.BuildIssueLabels(record).ToList();
        var evidencePaths = NormalizeEvidencePaths(record);
        var duplicateMatches = preflight.FindMatches(title, labels, options.DuplicateThreshold);
        var triage = BuildSuggestionIssueDraftTriage(record, evidencePaths, preflight.Checked, duplicateMatches.Count);
        return new SuggestionIssueDraftJsonResult(
            record.Hash,
            ShortId(record.Hash),
            title,
            labels,
            evidencePaths,
            triage,
            BuildIssueDraftBody(record, evidencePaths, triage),
            new SuggestionIssueDraftSourceJsonResult(
                record.Category,
                record.Language,
                GetStatus(record),
                GetAgent(record),
                record.CreatedAt),
            new SuggestionIssueDraftDuplicatePreflightJsonResult(
                preflight.Checked,
                duplicateMatches.Count,
                duplicateMatches));
    }

    private static IssueDraftTriageMetadataJsonResult BuildSuggestionIssueDraftTriage(
        SuggestionRecord record,
        IReadOnlyList<string> evidencePaths,
        bool duplicatePreflightChecked,
        int duplicateMatchCount)
    {
        var severity = record.Category switch
        {
            "crash_report" or "unexpected_error" => "high",
            "other" => "low",
            _ => "medium",
        };
        var confidence = evidencePaths.Count > 0
            ? "medium"
            : !string.IsNullOrWhiteSpace(record.SampledTitle) || !string.IsNullOrWhiteSpace(record.Context)
                ? "medium"
                : "low";
        return new IssueDraftTriageMetadataJsonResult(
            severity,
            confidence,
            evidencePaths.Count,
            BuildSuggestionIssueDraftDuplicateGuidance(duplicatePreflightChecked, duplicateMatchCount));
    }

    private static string BuildSuggestionIssueDraftDuplicateGuidance(bool duplicatePreflightChecked, int duplicateMatchCount)
    {
        if (!duplicatePreflightChecked)
            return "Duplicate preflight was not checked; search open issues before filing.";
        if (duplicateMatchCount > 0)
            return "Review duplicate_preflight.matches before filing; merge evidence into an existing issue when the same root cause is already tracked.";
        return "No duplicate candidates were found by preflight; still verify open issues before filing.";
    }

    private static string BuildIssueDraftTitle(SuggestionRecord record)
    {
        var titleSource = !string.IsNullOrWhiteSpace(record.SampledTitle)
            ? record.SampledTitle
            : record.Description;
        return GitHubIssueReporter.BuildIssueTitle(record.Category, RedactSuggestionOutputValue(titleSource) ?? string.Empty);
    }

    private static string? RedactSuggestionOutputValue(string? value)
        => value == null ? null : SuggestionStore.RedactSensitiveText(value, out _);

    private static List<string> RedactSuggestionOutputArray(string[]? values)
        => NormalizeNullableArray(values)
            .Select(RedactSuggestionOutputValue)
            .Where(value => value != null)
            .Cast<string>()
            .ToList();

    private static string BuildIssueDraftBody(
        SuggestionRecord record,
        IReadOnlyList<string> evidencePaths,
        IssueDraftTriageMetadataJsonResult triage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        sb.AppendLine(BoundSuggestionOutputValue(GitHubIssueReporter.ScrubInlineCode(record.Description), capTextFields: true));
        sb.AppendLine();
        sb.AppendLine("## Category");
        sb.AppendLine(record.Category);
        sb.AppendLine();
        sb.AppendLine("## Language");
        sb.AppendLine(record.Language ?? "N/A");
        sb.AppendLine();
        sb.AppendLine("## Evidence paths");
        if (evidencePaths.Count == 0)
        {
            sb.AppendLine("N/A");
        }
        else
        {
            foreach (var path in evidencePaths)
                sb.AppendLine($"- {path}");
        }
        sb.AppendLine();
        AppendSuggestionIssueDraftTriageMetadata(sb, triage);
        sb.AppendLine();
        sb.AppendLine("## Context");
        sb.AppendLine(record.Context != null
            ? BoundSuggestionOutputValue(GitHubIssueReporter.ScrubInlineCode(record.Context), capTextFields: true)
            : "N/A");
        if (!string.IsNullOrWhiteSpace(record.ToolInvocationContext))
        {
            sb.AppendLine();
            sb.AppendLine("## Tool invocation context");
            sb.AppendLine(BoundSuggestionOutputValue(GitHubIssueReporter.ScrubInlineCode(record.ToolInvocationContext), capTextFields: true));
        }
        sb.AppendLine();
        sb.AppendLine("## Suggestion metadata");
        sb.AppendLine($"- suggestion_id: `{record.Hash}`");
        sb.AppendLine($"- status: `{GetStatus(record)}`");
        sb.AppendLine($"- created_at: `{record.CreatedAt:O}`");
        var agent = GetAgent(record);
        if (!string.IsNullOrWhiteSpace(agent))
            sb.AppendLine($"- agent: `{agent}`");
        if (!string.IsNullOrWhiteSpace(record.ClientVersion) && record.ClientVersion != "unknown")
            sb.AppendLine($"- cdidx_version: `{record.ClientVersion}`");
        return BoundSuggestionOutputValue(sb.ToString().TrimEnd(), MaxSuggestionIssueDraftBodyLength);
    }

    private static void AppendSuggestionIssueDraftTriageMetadata(StringBuilder sb, IssueDraftTriageMetadataJsonResult triage)
    {
        sb.AppendLine("## Triage metadata");
        sb.AppendLine($"- severity: `{triage.Severity}`");
        sb.AppendLine($"- confidence: `{triage.Confidence}`");
        sb.AppendLine($"- evidence_count: `{triage.EvidenceCount}`");
        sb.AppendLine($"- duplicate_guidance: {triage.DuplicateGuidance}");
    }

    private static List<string> NormalizeEvidencePaths(SuggestionRecord record)
        => SuggestionEvidencePaths.Normalize(record.EvidencePaths);

    private static List<string> NormalizeNullableArray(string[]? values)
    {
        if (values == null || values.Length == 0)
            return [];

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string FormatMarkdown(List<SuggestionRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# cdidx Suggestions");
        sb.AppendLine();
        sb.AppendLine($"Exported suggestions: {records.Count}");
        foreach (var record in records)
        {
            sb.AppendLine();
            sb.AppendLine($"## {ShortId(record.Hash)} - {FormatTitle(record.Description, 100)}");
            sb.AppendLine();
            sb.AppendLine($"- id: `{record.Hash}`");
            sb.AppendLine($"- created_at: `{record.CreatedAt:O}`");
            sb.AppendLine($"- status: `{GetStatus(record)}`");
            sb.AppendLine($"- category: `{record.Category}`");
            sb.AppendLine($"- language: `{record.Language ?? "-"}`");
            var agent = GetAgent(record);
            if (!string.IsNullOrWhiteSpace(agent))
                sb.AppendLine($"- agent: `{agent}`");
            if (!string.IsNullOrWhiteSpace(record.ClientVersion) && record.ClientVersion != "unknown")
                sb.AppendLine($"- cdidx_version: `{record.ClientVersion}`");
            if (!string.IsNullOrWhiteSpace(record.McpClientName))
                sb.AppendLine($"- mcp_client: `{record.McpClientName}{(string.IsNullOrWhiteSpace(record.McpClientVersion) ? string.Empty : " " + record.McpClientVersion)}`");
            if (!string.IsNullOrWhiteSpace(record.SessionId) && record.SessionId != "unknown")
                sb.AppendLine($"- session_id: `{record.SessionId}`");
            var evidencePaths = NormalizeEvidencePaths(record);
            if (evidencePaths.Count > 0)
            {
                sb.AppendLine("- evidence_paths:");
                foreach (var path in evidencePaths)
                    sb.AppendLine($"  - `{path}`");
            }
            if (!string.IsNullOrWhiteSpace(record.UpstreamUrl))
                sb.AppendLine($"- upstream_url: {record.UpstreamUrl}");
            if (record.UpstreamIssueNumber != null)
                sb.AppendLine($"- upstream_issue_number: `{record.UpstreamIssueNumber}`");
            if (record.LastSubmitAttempt != null)
                sb.AppendLine($"- last_submit_attempt: `{record.LastSubmitAttempt:O}`");
            if (record.SubmitAttemptCount > 0)
                sb.AppendLine($"- submit_attempt_count: `{record.SubmitAttemptCount}`");
            if (!string.IsNullOrWhiteSpace(record.LastSubmitError))
                sb.AppendLine($"- last_submit_error: `{record.LastSubmitError}`");
            sb.AppendLine();
            sb.AppendLine(BoundSuggestionOutputValue(record.Description, capTextFields: true));
            if (!string.IsNullOrWhiteSpace(record.Context))
            {
                sb.AppendLine();
                sb.AppendLine("Context:");
                sb.AppendLine();
                sb.AppendLine(BoundSuggestionOutputValue(record.Context, capTextFields: true));
            }
            if (!string.IsNullOrWhiteSpace(record.ToolInvocationContext))
            {
                sb.AppendLine();
                sb.AppendLine("Tool invocation context:");
                sb.AppendLine();
                sb.AppendLine(BoundSuggestionOutputValue(record.ToolInvocationContext, capTextFields: true));
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string? BoundSuggestionOutputValue(string? value, bool capTextFields) =>
        !capTextFields || value == null
            ? value
            : BoundSuggestionOutputValue(value, MaxSuggestionExportTextFieldLength);

    private static string BoundSuggestionOutputValue(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        if (maxLength <= SuggestionOutputTruncationMarker.Length)
            return value[..maxLength];

        var retainedLength = maxLength - SuggestionOutputTruncationMarker.Length;
        return value[..retainedLength].TrimEnd() + SuggestionOutputTruncationMarker;
    }

    private static int WriteUsageError(string message)
    {
        CommandErrorWriter.WriteStderr($"Error: {message}");
        CommandErrorWriter.WriteStderr(Usage);
        return CommandExitCodes.UsageError;
    }

    private static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--json":
                    options.Json = true;
                    break;
                case "--db":
                    if (!TryReadValue(args, ref i, "--db", out var dbPath, out var dbError))
                    {
                        options.Error = dbError;
                        return options;
                    }
                    options.DbPath = dbPath;
                    break;
                case "--status":
                    if (!TryReadValue(args, ref i, "--status", out var status, out var statusError))
                    {
                        options.Error = statusError;
                        return options;
                    }
                    options.Status = status;
                    if (!IsValidStatusFilter(options.Status))
                        options.Error = "Error: --status must be one of all, draft, submitted_pending_triage, open_in_upstream, resolved_in_upstream, wont_fix, duplicate, superseded, submitted, unsubmitted.";
                    break;
                case "--language":
                case "--lang":
                    if (!TryReadValue(args, ref i, arg, out var language, out var languageError))
                    {
                        options.Error = languageError;
                        return options;
                    }
                    options.Language = language;
                    break;
                case "--category":
                    if (!TryReadValue(args, ref i, "--category", out var category, out var categoryError))
                    {
                        options.Error = categoryError;
                        return options;
                    }
                    options.Category = category;
                    break;
                case "--agent":
                    if (!TryReadValue(args, ref i, "--agent", out var agent, out var agentError))
                    {
                        options.Error = agentError;
                        return options;
                    }
                    options.Agent = agent;
                    break;
                case "--limit":
                    if (!TryReadValue(args, ref i, "--limit", out var limit, out var limitError))
                    {
                        options.Error = limitError;
                        return options;
                    }
                    if (!TryParseNonNegativeInt("--limit", limit, out var parsedLimit, out var parsedLimitError))
                    {
                        options.Error = parsedLimitError;
                        return options;
                    }
                    options.Limit = parsedLimit;
                    break;
                case "--offset":
                    if (!TryReadValue(args, ref i, "--offset", out var offset, out var offsetError))
                    {
                        options.Error = offsetError;
                        return options;
                    }
                    if (!TryParseNonNegativeInt("--offset", offset, out var parsedOffset, out var parsedOffsetError))
                    {
                        options.Error = parsedOffsetError;
                        return options;
                    }
                    options.Offset = parsedOffset;
                    options.OffsetSpecified = true;
                    break;
                case "--since":
                    if (!TryReadValue(args, ref i, "--since", out var since, out var sinceError))
                    {
                        options.Error = sinceError;
                        return options;
                    }
                    if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedSince))
                        options.Error = $"Error: could not parse --since value '{since}' as a date/time.";
                    else
                        options.Since = parsedSince;
                    break;
                case "--format":
                    if (!TryReadValue(args, ref i, "--format", out var format, out var formatError))
                    {
                        options.Error = formatError;
                        return options;
                    }
                    options.ExportFormat = format;
                    if (!IsValidExportFormat(options.ExportFormat))
                        options.Error = "Error: --format must be one of json, markdown, issue-drafts.";
                    break;
                case "--open-issues":
                    if (!TryReadValue(args, ref i, "--open-issues", out var openIssuesPath, out var openIssuesError))
                    {
                        options.Error = openIssuesError;
                        return options;
                    }
                    options.OpenIssuesPath = openIssuesPath;
                    break;
                case "--repo":
                    if (!TryReadValue(args, ref i, "--repo", out var repository, out var repositoryError))
                    {
                        options.Error = repositoryError;
                        return options;
                    }
                    options.OpenIssuesRepository = repository;
                    break;
                case "--duplicate-confidence":
                    if (!TryReadValue(args, ref i, "--duplicate-confidence", out var duplicateConfidence, out var duplicateConfidenceError))
                    {
                        options.Error = duplicateConfidenceError;
                        return options;
                    }
                    if (IssueDuplicatePreflight.TryNormalizeDuplicateConfidence(duplicateConfidence, out var normalizedDuplicateConfidence))
                    {
                        options.DuplicateConfidence = normalizedDuplicateConfidence;
                        options.DuplicateThreshold = IssueDuplicatePreflight.ThresholdForDuplicateConfidence(normalizedDuplicateConfidence);
                        options.DuplicateConfidenceSpecified = true;
                    }
                    else
                    {
                        options.Error = $"Error: --duplicate-confidence must be one of low, medium, high; got '{duplicateConfidence}'.";
                    }
                    break;
                case "--duplicate-threshold":
                    if (!TryReadValue(args, ref i, "--duplicate-threshold", out var duplicateThreshold, out var duplicateThresholdError))
                    {
                        options.Error = duplicateThresholdError;
                        return options;
                    }
                    if (TryParseScoreThreshold("--duplicate-threshold", duplicateThreshold, out var parsedDuplicateThreshold, out var parsedDuplicateThresholdError))
                    {
                        options.DuplicateThreshold = parsedDuplicateThreshold;
                        options.DuplicateConfidence = IssueDuplicatePreflight.CustomDuplicateConfidence;
                        options.DuplicateThresholdSpecified = true;
                    }
                    else
                    {
                        options.Error = parsedDuplicateThresholdError;
                    }
                    break;
                default:
                    if (arg.StartsWith("--db=", StringComparison.Ordinal))
                        options.DbPath = arg["--db=".Length..];
                    else if (arg.StartsWith("--status=", StringComparison.Ordinal))
                        options.Status = arg["--status=".Length..];
                    else if (arg.StartsWith("--language=", StringComparison.Ordinal))
                        options.Language = arg["--language=".Length..];
                    else if (arg.StartsWith("--lang=", StringComparison.Ordinal))
                        options.Language = arg["--lang=".Length..];
                    else if (arg.StartsWith("--category=", StringComparison.Ordinal))
                        options.Category = arg["--category=".Length..];
                    else if (arg.StartsWith("--agent=", StringComparison.Ordinal))
                        options.Agent = arg["--agent=".Length..];
                    else if (arg.StartsWith("--limit=", StringComparison.Ordinal))
                    {
                        var inlineLimit = arg["--limit=".Length..];
                        if (!TryParseNonNegativeInt("--limit", inlineLimit, out var parsedInlineLimit, out var parsedInlineLimitError))
                            options.Error = parsedInlineLimitError;
                        else
                            options.Limit = parsedInlineLimit;
                    }
                    else if (arg.StartsWith("--offset=", StringComparison.Ordinal))
                    {
                        var inlineOffset = arg["--offset=".Length..];
                        if (!TryParseNonNegativeInt("--offset", inlineOffset, out var parsedInlineOffset, out var parsedInlineOffsetError))
                        {
                            options.Error = parsedInlineOffsetError;
                        }
                        else
                        {
                            options.Offset = parsedInlineOffset;
                            options.OffsetSpecified = true;
                        }
                    }
                    else if (arg.StartsWith("--format=", StringComparison.Ordinal))
                        options.ExportFormat = arg["--format=".Length..];
                    else if (arg.StartsWith("--open-issues=", StringComparison.Ordinal))
                        options.OpenIssuesPath = arg["--open-issues=".Length..];
                    else if (arg.StartsWith("--repo=", StringComparison.Ordinal))
                        options.OpenIssuesRepository = arg["--repo=".Length..];
                    else if (arg.StartsWith("--duplicate-confidence=", StringComparison.Ordinal))
                    {
                        var inlineConfidence = arg["--duplicate-confidence=".Length..];
                        if (IssueDuplicatePreflight.TryNormalizeDuplicateConfidence(inlineConfidence, out var normalizedInlineConfidence))
                        {
                            options.DuplicateConfidence = normalizedInlineConfidence;
                            options.DuplicateThreshold = IssueDuplicatePreflight.ThresholdForDuplicateConfidence(normalizedInlineConfidence);
                            options.DuplicateConfidenceSpecified = true;
                        }
                        else
                        {
                            options.Error = $"Error: --duplicate-confidence must be one of low, medium, high; got '{inlineConfidence}'.";
                        }
                    }
                    else if (arg.StartsWith("--duplicate-threshold=", StringComparison.Ordinal))
                    {
                        var inlineThreshold = arg["--duplicate-threshold=".Length..];
                        if (TryParseScoreThreshold("--duplicate-threshold", inlineThreshold, out var parsedInlineThreshold, out var parsedInlineThresholdError))
                        {
                            options.DuplicateThreshold = parsedInlineThreshold;
                            options.DuplicateConfidence = IssueDuplicatePreflight.CustomDuplicateConfidence;
                            options.DuplicateThresholdSpecified = true;
                        }
                        else
                        {
                            options.Error = parsedInlineThresholdError;
                        }
                    }
                    else if (arg.StartsWith("--since=", StringComparison.Ordinal))
                    {
                        var inlineSince = arg["--since=".Length..];
                        if (!DateTimeOffset.TryParse(inlineSince, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedInlineSince))
                            options.Error = $"Error: could not parse --since value '{inlineSince}' as a date/time.";
                        else
                            options.Since = parsedInlineSince;
                    }
                    else if (arg.StartsWith("-", StringComparison.Ordinal))
                        options.Error = $"Error: {arg} is not supported for suggestions.";
                    else if (options.Id == null)
                        options.Id = arg;
                    else
                        options.Error = $"Error: unexpected argument '{arg}'.";
                    break;
            }

            if (options.Error != null)
                return options;
        }

        options.Status = options.Status.ToLowerInvariant();
        options.ExportFormat = options.ExportFormat.ToLowerInvariant();
        if (!IsValidStatusFilter(options.Status))
            options.Error = "Error: --status must be one of all, draft, submitted_pending_triage, open_in_upstream, resolved_in_upstream, wont_fix, duplicate, superseded, submitted, unsubmitted.";
        if (!IsValidExportFormat(options.ExportFormat))
            options.Error = "Error: --format must be one of json, markdown, issue-drafts.";
        if (options.DuplicateConfidenceSpecified && options.DuplicateThresholdSpecified)
            options.Error = "Error: --duplicate-confidence and --duplicate-threshold cannot be combined; use the preset or the explicit score threshold.";
        if ((options.DuplicateConfidenceSpecified || options.DuplicateThresholdSpecified) && options.ExportFormat != "issue-drafts")
            options.Error = "Error: --duplicate-confidence and --duplicate-threshold can only be used with --format issue-drafts.";
        return options;
    }

    private static bool IsValidExportFormat(string format) => format is "json" or "markdown" or "issue-drafts";

    private static bool TryParseNonNegativeInt(string option, string rawValue, out int value, out string? error)
    {
        if (int.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0)
        {
            error = null;
            return true;
        }

        value = 0;
        error = $"Error: {option} must be a non-negative integer.";
        return false;
    }

    private static bool TryParseScoreThreshold(string option, string rawValue, out double value, out string? error)
    {
        if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !double.IsNaN(value) &&
            !double.IsInfinity(value) &&
            value >= 0 &&
            value <= 1)
        {
            error = null;
            return true;
        }

        value = IssueDuplicatePreflight.DefaultDuplicateThreshold;
        error = $"Error: {option} must be a number between 0 and 1.";
        return false;
    }

    private static bool TryReadValue(string[] args, ref int i, string option, out string value, out string? error)
    {
        value = string.Empty;
        error = null;
        if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
        {
            error = $"Error: {option} requires a value.";
            return false;
        }

        value = args[++i];
        return true;
    }

    private sealed class Options
    {
        public string? Id { get; set; }
        public string? DbPath { get; set; }
        public bool Json { get; set; }
        public string Status { get; set; } = "all";
        public string ExportFormat { get; set; } = "json";
        public string? Language { get; set; }
        public string? Category { get; set; }
        public string? Agent { get; set; }
        public int? Limit { get; set; }
        public int Offset { get; set; }
        public bool OffsetSpecified { get; set; }
        public string? OpenIssuesPath { get; set; }
        public string? OpenIssuesRepository { get; set; }
        public string DuplicateConfidence { get; set; } = IssueDuplicatePreflight.DefaultDuplicateConfidence;
        public double DuplicateThreshold { get; set; } = IssueDuplicatePreflight.DefaultDuplicateThreshold;
        public bool DuplicateConfidenceSpecified { get; set; }
        public bool DuplicateThresholdSpecified { get; set; }
        public DateTimeOffset? Since { get; set; }
        public string? Error { get; set; }
        public bool HasPagination => Limit.HasValue || OffsetSpecified;
    }
}

internal sealed record SuggestionListItemJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("short_id")] string ShortId,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("agent")] string? Agent,
    [property: JsonPropertyName("created_by_agent")] string CreatedByAgent,
    [property: JsonPropertyName("client_version")] string ClientVersion,
    [property: JsonPropertyName("mcp_client_name")] string? McpClientName,
    [property: JsonPropertyName("mcp_client_version")] string? McpClientVersion,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("submitted_to_github")] bool SubmittedToGitHub,
    [property: JsonPropertyName("upstream_url")] string? UpstreamUrl,
    [property: JsonPropertyName("upstream_issue_number")] int? UpstreamIssueNumber,
    [property: JsonPropertyName("last_submit_attempt")] DateTime? LastSubmitAttempt,
    [property: JsonPropertyName("submit_attempt_count")] int SubmitAttemptCount,
    [property: JsonPropertyName("last_submit_error")] string? LastSubmitError);

internal sealed record SuggestionDetailJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("agent")] string? Agent,
    [property: JsonPropertyName("created_by_agent")] string CreatedByAgent,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("client_version")] string ClientVersion,
    [property: JsonPropertyName("mcp_client_name")] string? McpClientName,
    [property: JsonPropertyName("mcp_client_version")] string? McpClientVersion,
    [property: JsonPropertyName("tool_invocation_context")] string? ToolInvocationContext,
    [property: JsonPropertyName("sampled_title")] string? SampledTitle,
    [property: JsonPropertyName("sampled_tags")] List<string> SampledTags,
    [property: JsonPropertyName("evidence_paths")] List<string> EvidencePaths,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("context")] string? Context,
    [property: JsonPropertyName("submitted_to_github")] bool SubmittedToGitHub,
    [property: JsonPropertyName("upstream_url")] string? UpstreamUrl,
    [property: JsonPropertyName("upstream_issue_number")] int? UpstreamIssueNumber,
    [property: JsonPropertyName("last_synced_at")] DateTime? LastSyncedAt,
    [property: JsonPropertyName("resolved_at")] DateTime? ResolvedAt,
    [property: JsonPropertyName("supersedes")] string? Supersedes,
    [property: JsonPropertyName("superseded_by")] string? SupersededBy,
    [property: JsonPropertyName("last_submit_attempt")] DateTime? LastSubmitAttempt,
    [property: JsonPropertyName("submit_attempt_count")] int SubmitAttemptCount,
    [property: JsonPropertyName("last_submit_error")] string? LastSubmitError);

internal sealed record SuggestionExportJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("suggestions")] List<SuggestionDetailJsonResult> Suggestions);

internal sealed record SuggestionIssueDraftExportJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("duplicate_preflight")] SuggestionIssueDraftPreflightSummaryJsonResult DuplicatePreflight,
    [property: JsonPropertyName("drafts")] List<SuggestionIssueDraftJsonResult> Drafts);

internal sealed record SuggestionIssueDraftPreflightSummaryJsonResult(
    [property: JsonPropertyName("checked")] bool Checked,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("open_issue_count")] int OpenIssueCount,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("minimum_score")] double MinimumScore);

internal sealed record IssueDraftTriageMetadataJsonResult(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("evidence_count")] int EvidenceCount,
    [property: JsonPropertyName("duplicate_guidance")] string DuplicateGuidance);

internal sealed record SuggestionIssueDraftJsonResult(
    [property: JsonPropertyName("suggestion_id")] string SuggestionId,
    [property: JsonPropertyName("short_id")] string ShortId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("labels")] List<string> Labels,
    [property: JsonPropertyName("evidence_paths")] List<string> EvidencePaths,
    [property: JsonPropertyName("triage")] IssueDraftTriageMetadataJsonResult Triage,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("source")] SuggestionIssueDraftSourceJsonResult Source,
    [property: JsonPropertyName("duplicate_preflight")] SuggestionIssueDraftDuplicatePreflightJsonResult DuplicatePreflight);

internal sealed record SuggestionIssueDraftSourceJsonResult(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("agent")] string? Agent,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt);

internal sealed record SuggestionIssueDraftDuplicatePreflightJsonResult(
    [property: JsonPropertyName("checked")] bool Checked,
    [property: JsonPropertyName("match_count")] int MatchCount,
    [property: JsonPropertyName("matches")] List<SuggestionIssueDraftDuplicateMatchJsonResult> Matches);

internal sealed record SuggestionIssueDraftDuplicateMatchJsonResult(
    [property: JsonPropertyName("number")] int? Number,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("labels")] List<string> Labels,
    [property: JsonPropertyName("overlapping_labels")] List<string> OverlappingLabels,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("score")] double Score);
