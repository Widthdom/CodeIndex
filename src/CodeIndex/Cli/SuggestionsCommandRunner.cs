using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

internal static class SuggestionsCommandRunner
{
    private const string Usage = "Usage: cdidx suggestions <list|show|export> [id] [--db <path>] [--json] [--status <all|draft|submitted_pending_triage|open_in_upstream|resolved_in_upstream|wont_fix|duplicate|superseded|submitted|unsubmitted>] [--language <lang>] [--category <category>] [--since <datetime>] [--agent <name>] [--format <json|markdown|issue-drafts>] [--open-issues <path>]";
    internal const int MaxOpenIssuesJsonBytes = 8 * 1024 * 1024;
    internal const int MaxOpenIssuesJsonDepth = 32;

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
            Console.Error.WriteLine(options.Error);
            Console.Error.WriteLine(Usage);
            return CommandExitCodes.UsageError;
        }
        if (options.OpenIssuesPath != null && (verb != "export" || options.ExportFormat != "issue-drafts"))
            return WriteUsageError("--open-issues can only be used with `suggestions export --format issue-drafts`.");

        var store = CreateStore(options.DbPath);
        var records = ApplyFilters(store.LoadAll(), options)
            .OrderByDescending(s => s.CreatedAt)
            .ThenBy(s => s.Hash, StringComparer.Ordinal)
            .ToList();

        return verb switch
        {
            "list" => RunList(records, options, jsonOptions),
            "show" => RunShow(records, options, jsonOptions),
            "export" => RunExport(records, options, jsonOptions),
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
            Console.Error.WriteLine("No suggestions found.");
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
            records.Select(ToDetail).ToList());
        Console.WriteLine(JsonSerializer.Serialize(
            payload,
            CliJsonSerializerContextFactory.Create(jsonOptions).SuggestionExportJsonResult));
        return CommandExitCodes.Success;
    }

    private static int RunIssueDraftExport(List<SuggestionRecord> records, Options options, JsonSerializerOptions jsonOptions)
    {
        if (!IssueDuplicatePreflight.TryLoad(options.OpenIssuesPath, out var preflight, out var error))
            return WriteUsageError(error!);

        var drafts = records.Select(record => ToIssueDraft(record, preflight)).ToList();
        var payload = new SuggestionIssueDraftExportJsonResult(
            JsonOutputContract.ApiVersion,
            drafts.Count,
            new SuggestionIssueDraftPreflightSummaryJsonResult(
                preflight.Checked,
                preflight.Source,
                preflight.OpenIssueCount),
            drafts);
        Console.WriteLine(JsonSerializer.Serialize(
            payload,
            CliJsonSerializerContextFactory.Create(jsonOptions).SuggestionIssueDraftExportJsonResult));
        return CommandExitCodes.Success;
    }

    private static SuggestionStore CreateStore(string? dbPath)
    {
        var normalizedDbPath = string.IsNullOrWhiteSpace(dbPath)
            ? Path.Combine(Environment.CurrentDirectory, ".cdidx", "codeindex.db")
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

    private static SuggestionDetailJsonResult ToDetail(SuggestionRecord record) => new(
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
        record.ToolInvocationContext,
        record.SampledTitle,
        NormalizeNullableArray(record.SampledTags),
        NormalizeEvidencePaths(record),
        record.Description,
        record.Context,
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

    private static SuggestionIssueDraftJsonResult ToIssueDraft(SuggestionRecord record, IssueDuplicatePreflight preflight)
    {
        var title = BuildIssueDraftTitle(record);
        var labels = GitHubIssueReporter.BuildIssueLabels(record).ToList();
        var evidencePaths = NormalizeEvidencePaths(record);
        var duplicateMatches = preflight.FindMatches(title, labels);
        return new SuggestionIssueDraftJsonResult(
            record.Hash,
            ShortId(record.Hash),
            title,
            labels,
            evidencePaths,
            BuildIssueDraftBody(record, evidencePaths),
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

    private static string BuildIssueDraftTitle(SuggestionRecord record)
    {
        var titleSource = !string.IsNullOrWhiteSpace(record.SampledTitle)
            ? record.SampledTitle
            : record.Description;
        return GitHubIssueReporter.BuildIssueTitle(record.Category, titleSource);
    }

    private static string BuildIssueDraftBody(SuggestionRecord record, IReadOnlyList<string> evidencePaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        sb.AppendLine(GitHubIssueReporter.ScrubInlineCode(record.Description));
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
        sb.AppendLine("## Context");
        sb.AppendLine(record.Context != null ? GitHubIssueReporter.ScrubInlineCode(record.Context) : "N/A");
        if (!string.IsNullOrWhiteSpace(record.ToolInvocationContext))
        {
            sb.AppendLine();
            sb.AppendLine("## Tool invocation context");
            sb.AppendLine(GitHubIssueReporter.ScrubInlineCode(record.ToolInvocationContext));
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
        return sb.ToString().TrimEnd();
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
            sb.AppendLine(record.Description);
            if (!string.IsNullOrWhiteSpace(record.Context))
            {
                sb.AppendLine();
                sb.AppendLine("Context:");
                sb.AppendLine();
                sb.AppendLine(record.Context);
            }
            if (!string.IsNullOrWhiteSpace(record.ToolInvocationContext))
            {
                sb.AppendLine();
                sb.AppendLine("Tool invocation context:");
                sb.AppendLine();
                sb.AppendLine(record.ToolInvocationContext);
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static int WriteUsageError(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        Console.Error.WriteLine(Usage);
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
                    else if (arg.StartsWith("--format=", StringComparison.Ordinal))
                        options.ExportFormat = arg["--format=".Length..];
                    else if (arg.StartsWith("--open-issues=", StringComparison.Ordinal))
                        options.OpenIssuesPath = arg["--open-issues=".Length..];
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
        return options;
    }

    private static bool IsValidExportFormat(string format) => format is "json" or "markdown" or "issue-drafts";

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

    private sealed class IssueDuplicatePreflight
    {
        private static readonly HashSet<string> StopTitleTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "ai",
            "suggestion",
            "suggestions",
            "cdidx",
            "the",
            "and",
            "or",
            "for",
            "with",
            "from",
            "into",
            "that",
            "this",
        };

        private readonly List<OpenIssue> _issues;

        private IssueDuplicatePreflight(bool isChecked, string? source, List<OpenIssue> issues)
        {
            Checked = isChecked;
            Source = source;
            _issues = issues;
        }

        public bool Checked { get; }
        public string? Source { get; }
        public int OpenIssueCount => _issues.Count;

        public static bool TryLoad(string? path, out IssueDuplicatePreflight preflight, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                preflight = new IssueDuplicatePreflight(false, null, []);
                return true;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                var json = DataDirectorySecurity.ReadTextWithinLimit(fullPath, MaxOpenIssuesJsonBytes);
                if (json == null)
                {
                    preflight = new IssueDuplicatePreflight(false, null, []);
                    error = $"--open-issues file '{path}' exceeds maximum supported size of {MaxOpenIssuesJsonBytes} bytes.";
                    return false;
                }

                var root = JsonNode.Parse(
                    json,
                    documentOptions: new JsonDocumentOptions { MaxDepth = MaxOpenIssuesJsonDepth });
                preflight = new IssueDuplicatePreflight(true, fullPath, ParseOpenIssues(root));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                preflight = new IssueDuplicatePreflight(false, null, []);
                error = $"could not read --open-issues file '{path}': {ex.Message}";
                return false;
            }
        }

        public List<SuggestionIssueDraftDuplicateMatchJsonResult> FindMatches(string draftTitle, IReadOnlyList<string> draftLabels)
        {
            if (!Checked || _issues.Count == 0)
                return [];

            var draftLabelSet = draftLabels.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var normalizedDraftTitle = NormalizeTitleText(draftTitle);
            var draftTokens = TokenizeTitle(draftTitle);
            var matches = new List<SuggestionIssueDraftDuplicateMatchJsonResult>();
            foreach (var issue in _issues)
            {
                var issueLabels = issue.Labels
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var overlappingLabels = issueLabels
                    .Where(draftLabelSet.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var normalizedIssueTitle = NormalizeTitleText(issue.Title);
                var score = 0.0;
                string? reason = null;
                if (normalizedIssueTitle.Length > 0 && normalizedIssueTitle == normalizedDraftTitle)
                {
                    reason = "title_exact";
                    score = 1.0;
                }
                else if (overlappingLabels.Count > 0)
                {
                    score = ScoreTitleSimilarity(draftTokens, TokenizeTitle(issue.Title));
                    if (score >= 0.45)
                    {
                        reason = "title_label_similarity";
                    }
                    else if (normalizedIssueTitle.Length > 16
                        && normalizedDraftTitle.Length > 16
                        && (normalizedIssueTitle.Contains(normalizedDraftTitle, StringComparison.Ordinal)
                            || normalizedDraftTitle.Contains(normalizedIssueTitle, StringComparison.Ordinal)))
                    {
                        reason = "title_label_contains";
                        score = Math.Max(score, 0.45);
                    }
                }

                if (reason == null)
                    continue;

                matches.Add(new SuggestionIssueDraftDuplicateMatchJsonResult(
                    issue.Number,
                    issue.Title,
                    issue.Url,
                    issueLabels,
                    overlappingLabels,
                    reason,
                    Math.Round(score, 3)));
            }

            return matches
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Number ?? int.MaxValue)
                .Take(5)
                .ToList();
        }

        private static List<OpenIssue> ParseOpenIssues(JsonNode? root)
        {
            var array = root as JsonArray
                ?? root?["issues"] as JsonArray
                ?? root?["items"] as JsonArray;
            if (array == null)
                return [];

            var issues = new List<OpenIssue>();
            foreach (var item in array)
            {
                var title = TryReadString(item?["title"]);
                if (string.IsNullOrWhiteSpace(title))
                    continue;
                issues.Add(new OpenIssue(
                    TryReadInt(item?["number"]),
                    title,
                    TryReadString(item?["url"]) ?? TryReadString(item?["html_url"]),
                    ReadLabels(item?["labels"])));
            }

            return issues;
        }

        private static List<string> ReadLabels(JsonNode? labelsNode)
        {
            if (labelsNode is not JsonArray labels)
                return [];

            var result = new List<string>();
            foreach (var labelNode in labels)
            {
                var label = TryReadString(labelNode) ?? TryReadString(labelNode?["name"]);
                if (!string.IsNullOrWhiteSpace(label))
                    result.Add(label.Trim());
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string? TryReadString(JsonNode? node)
        {
            if (node == null)
                return null;
            try
            {
                return node.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static int? TryReadInt(JsonNode? node)
        {
            if (node == null)
                return null;
            try
            {
                return node.GetValue<int>();
            }
            catch (InvalidOperationException)
            {
                var value = TryReadString(node);
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            }
        }

        private static string NormalizeTitleText(string title)
        {
            var builder = new StringBuilder(title.Length);
            var previousWasSpace = true;
            foreach (var c in title)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                    previousWasSpace = false;
                }
                else if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }
            }

            return builder.ToString().Trim();
        }

        private static HashSet<string> TokenizeTitle(string title)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = new StringBuilder();
            foreach (var c in title)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(char.ToLowerInvariant(c));
                    continue;
                }

                AddToken(tokens, current);
            }

            AddToken(tokens, current);
            return tokens;
        }

        private static void AddToken(HashSet<string> tokens, StringBuilder current)
        {
            if (current.Length == 0)
                return;
            var token = current.ToString();
            current.Clear();
            if (token.Length < 3 || StopTitleTokens.Contains(token))
                return;
            tokens.Add(token);
        }

        private static double ScoreTitleSimilarity(HashSet<string> left, HashSet<string> right)
        {
            if (left.Count == 0 || right.Count == 0)
                return 0.0;

            var intersection = left.Count(right.Contains);
            var union = left.Count + right.Count - intersection;
            return union == 0 ? 0.0 : intersection / (double)union;
        }

        private sealed record OpenIssue(int? Number, string Title, string? Url, List<string> Labels);
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
        public string? OpenIssuesPath { get; set; }
        public DateTimeOffset? Since { get; set; }
        public string? Error { get; set; }
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
    [property: JsonPropertyName("open_issue_count")] int OpenIssueCount);

internal sealed record SuggestionIssueDraftJsonResult(
    [property: JsonPropertyName("suggestion_id")] string SuggestionId,
    [property: JsonPropertyName("short_id")] string ShortId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("labels")] List<string> Labels,
    [property: JsonPropertyName("evidence_paths")] List<string> EvidencePaths,
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
